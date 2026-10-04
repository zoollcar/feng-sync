using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FengSync.Core;

namespace FengSync.Services;

/// <summary>
/// Pulls, caches and decodes the pictures the grid view paints, and nothing else. It is deliberately not a
/// general content cache: the bytes here are a 64-pixel decoration the user never asked to download, so they are
/// capped by <see cref="CloudBrowserTiles.ThumbnailMaxBytes"/> on the way in, dropped when the cache grows past
/// <see cref="CloudBrowserTiles.CacheMaxBytes"/>, and never handed to anything that would present them as the
/// file itself.
/// <para>
/// The disk cache is keyed by <see cref="CloudBrowserTiles.KeyFor"/>, which carries size and modification time,
/// so an overwritten picture is re-pulled rather than repainted from stale bytes. The in-memory decode cache
/// additionally remembers the rows that produced nothing — a missing object, a refused read, bytes that are not
/// a picture — because a repaint of that row would otherwise pay for the same failed read every time the listing
/// refreshes.
/// </para>
/// </summary>
public sealed class CloudThumbnailStore
{
    /// <summary>
    /// How many tile reads may be in flight at once. Two, which is the number the application gives a user's own
    /// queued transfers by default: a tile is decoration, and a queue of decorations must not out-rank the
    /// uploads and downloads the user is watching — every one of which travels through this same daemon.
    /// </summary>
    public const int ReadParallelism = 2;

    /// <summary>
    /// How many decoded tiles one window may hold. 512 at <see cref="CloudBrowserTiles.TileEdgePixels"/> pixels
    /// is about 8 MB of 32-bit pixels (64 × 64 × 4 bytes each), and that arithmetic is the whole reason for the
    /// bound; past it the oldest tiles are dropped and re-decoded from disk if their rows come back on screen.
    /// </summary>
    public const int DecodedLimit = 512;

    private readonly CloudFileManagerService _files;
    private readonly string? _cacheRoot;
    private readonly SemaphoreSlim _gate = new(ReadParallelism, ReadParallelism);
    private readonly object _lock = new();
    private readonly Dictionary<string, ImageSource?> _decoded = new(StringComparer.Ordinal);
    private readonly Queue<string> _decodedOrder = new();
    private readonly Dictionary<string, Task<bool>> _loading = new(StringComparer.Ordinal);

    public CloudThumbnailStore(CloudFileManagerService? files = null, string? cacheRoot = null)
    {
        _files = files ?? new CloudFileManagerService();
        _cacheRoot = cacheRoot;
    }

    /// <summary>
    /// The directory the pulled tiles are kept in. Resolved per use rather than cached, because
    /// <see cref="AppDataPaths.CloudThumbnails"/> honours FENGSYNC_DATA_DIR at the moment it is read, which is
    /// what lets a test aim the cache at a temporary directory without constructing a different store.
    /// </summary>
    public string CacheRoot => _cacheRoot ?? AppDataPaths.CloudThumbnails;

    /// <summary>
    /// What a tile should show right now, without starting or waiting for anything. Null covers three real
    /// states — not pulled yet, pulled and refused, not a picture — and the tile template draws the file icon for
    /// all three, which is the honest answer in each.
    /// </summary>
    public ImageSource? Peek(string remote, CloudFileEntry entry)
    {
        if (!CloudBrowserTiles.OffersThumbnail(entry)) return null;
        lock (_lock)
            return _decoded.TryGetValue(CloudBrowserTiles.KeyFor(remote, entry), out var image) ? image : null;
    }

    /// <summary>
    /// Makes sure one row's tile is reachable from <see cref="Peek"/>, pulling it from the cloud only when the
    /// cache has nothing. Returns false for a row that will never show a picture; the reason is in
    /// <see cref="CloudBrowserTiles.RefusalFor"/>, not in this call, because a repaint does not need it.
    /// </summary>
    public async Task<bool> LoadAsync(string remote, CloudFileEntry entry, CancellationToken ct = default)
    {
        if (!CloudBrowserTiles.OffersThumbnail(entry)) return false;
        var key = CloudBrowserTiles.KeyFor(remote, entry);
        Task<bool>? joined = null;
        lock (_lock)
        {
            if (_decoded.TryGetValue(key, out var ready)) return ready is not null;
            _loading.TryGetValue(key, out joined);
        }
        // A second caller for the same tile joins the read already running rather than starting a second one. It
        // waits without its own token: the first caller owns that cancellation, and this caller leaving the
        // directory must not cancel a read somebody else is still waiting on.
        if (joined is not null) return await joined.ConfigureAwait(false);

        var load = PullAsync(remote, entry, key, ct);
        lock (_lock) _loading[key] = load;
        return await load.ConfigureAwait(false);
    }

    private async Task<bool> PullAsync(string remote, CloudFileEntry entry, string key, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = CloudBrowserTiles.CachedFileFor(CacheRoot, remote, entry);
            var pulled = false;
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                var read = await _files.ReadAsync(remote, entry.Path, CloudBrowserTiles.ThumbnailMaxBytes, ct)
                    .ConfigureAwait(false);
                if (read.Status != RcloneReadStatus.Read || read.Bytes.Length == 0)
                {
                    // TooLarge, Missing and Failed all end the same way for a tile: no picture. They are not
                    // merged into one reason for the caller, because the caller's sentence comes from the listing
                    // rules, and this is only the read that came after them.
                    Remember(key, null);
                    return false;
                }
                await WriteAsync(read.Bytes, path, ct).ConfigureAwait(false);
                pulled = true;
            }

            var image = Decode(path);
            // Bytes that will not decode are a broken cache entry, not a permanent verdict on the object, so the
            // file goes and the next listing re-pulls it. An entry that was already on disk is kept: re-pulling
            // the same bytes would not make them decode.
            if (image is null && pulled) TryDelete(path);
            Remember(key, image);
            if (pulled) Prune();
            return image is not null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            || ex is HttpRequestException)
        {
            // A read that failed for a reason the client's own answer did not report still ends as "no picture for
            // this row", and the row is not re-read on every repaint.
            Remember(key, null);
            return false;
        }
        finally
        {
            lock (_lock) _loading.Remove(key);
            _gate.Release();
        }
    }

    private async Task WriteAsync(byte[] bytes, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheRoot);
        // Written beside its final name and moved in, so a cache file is either complete or absent: a tile that
        // half-decoded from a torn write would be remembered as a bad picture the object never was.
        var temporary = path + ".writing";
        await File.WriteAllBytesAsync(temporary, bytes, ct).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private ImageSource? Decode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.DecodePixelWidth = CloudBrowserTiles.TileEdgePixels;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (CloudBrowserTiles.RefusesDecode(ex))
        {
            // No tile for this row, whatever shape the toolkit's refusal took (see
            // <see cref="CloudBrowserTiles.RefusesDecode"/>). The read itself succeeded, so this is about the
            // bytes rather than about the cloud.
            return null;
        }
    }

    private void Remember(string key, ImageSource? image)
    {
        lock (_lock)
        {
            if (_decoded.ContainsKey(key)) return;
            _decoded[key] = image;
            _decodedOrder.Enqueue(key);
            while (_decodedOrder.Count > DecodedLimit)
            {
                var oldest = _decodedOrder.Dequeue();
                if (oldest != key) _decoded.Remove(oldest);
            }
        }
    }

    /// <summary>
    /// Keeps the cache under its ceiling by dropping the least recently written tiles. Oldest-first is the right
    /// order here because the cache holds pictures the user has already scrolled past, while the newest ones are
    /// the rows on screen.
    /// </summary>
    private void Prune()
    {
        try
        {
            if (!Directory.Exists(CacheRoot)) return;
            var files = Directory.EnumerateFiles(CacheRoot).Select(name => new FileInfo(name)).ToList();
            var total = files.Sum(file => file.Length);
            if (total <= CloudBrowserTiles.CacheMaxBytes) return;
            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= CloudBrowserTiles.CacheMaxBytes) break;
                total -= file.Length;
                TryDelete(file.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that has not been trimmed yet is still correct; the next pull tries again.
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Turns a browser row into the picture it currently has, for the tile template. The row is the only binding
/// input a template gets, so the lookup function is handed to the converter instead: the window owns both the
/// thumbnail store and which endpoint is being browsed, and a template cannot reach either on its own.
/// <para>
/// This is a one-way read of a cache, never a loader. A converter that started a network read would do it on
/// every repaint of every visible row, on the UI thread's layout pass, and there would be no way to cancel it
/// when the user leaves the directory.
/// </para>
/// </summary>
public sealed class CloudTileSourceConverter : IValueConverter
{
    /// <summary>What the tile shows for this row right now, or null for the file icon.</summary>
    public Func<CloudFileEntry, ImageSource?>? Peek { get; set; }

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is CloudFileEntry entry && Peek is { } peek ? peek(entry) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("缩略图只用于显示，不参与写回。");
}
