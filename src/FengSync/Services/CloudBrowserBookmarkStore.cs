using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FengSync.Core;

namespace FengSync.Services;

/// <summary>One remembered place as it appears on disk.</summary>
public sealed record CloudBookmarkRecord(string Remote, string Path);

/// <summary>
/// The whole bookmark file. <see cref="Version"/> is written first and checked on the way in, so a file from a
/// later release is treated as unreadable rather than half-understood.
/// </summary>
public sealed record CloudBookmarkFile(int Version, IReadOnlyList<CloudBookmarkRecord> Favorites, IReadOnlyList<CloudBookmarkRecord> Recent)
{
    /// <summary>The only version this build writes.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>
/// What the bookmark file yielded, and how it got there. The recovery fields exist because the file this window
/// overwrites on the next visit is the only copy of what the user chose to keep, so a read that could not be
/// understood has to say so instead of presenting an empty list as though nobody had filled it.
/// </summary>
public sealed record CloudBookmarkLoad(
    IReadOnlyList<CloudBookmark> Favorites,
    IReadOnlyList<CloudBookmark> Recent,
    bool RecoveredFromCorruption = false,
    string? BackupPath = null)
{
    public static CloudBookmarkLoad Empty { get; } = new([], []);
}

/// <summary>
/// Persists the browser's 收藏 and 最近访问 lists. The file holds place names only — an rclone remote name and a
/// path inside it — and never a token, a password or a key, so it follows the same rule as an endpoint export.
/// The destination is chosen when the store is built, because <see cref="AppDataPaths.Root"/> is read once into
/// <see cref="_path"/>; a test that redirects <c>FENGSYNC_DATA_DIR</c> therefore has to do it before the window —
/// and so before this store — exists, which is why every window harness sets it first.
/// </summary>
public sealed class CloudBrowserBookmarkStore(string? path = null)
{
    /// <summary>
    /// One write at a time, process-wide, because this file has no second copy to recover from. Every await taken
    /// inside that region resumes on the thread pool instead of on whoever asked: a continuation posted back to a
    /// dispatcher that has stopped pumping would leave the gate held for the rest of the process, and every later
    /// window would then wait on a file nobody is writing any more.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _path = path ?? Path.Combine(AppDataPaths.Root, "cloud-bookmarks.json");

    /// <summary>Where the store persists, so a change that spans several files can back them up together.</summary>
    public string FilePath => _path;

    public async Task<CloudBookmarkLoad> LoadAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await ReadUnsafeAsync(backUpUnreadable: true, ct).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Writes both lists. They go together in one write on purpose: a partial save would leave the 收藏 of an
    /// endpoint whose 最近访问 had already been trimmed, and the two are read back as one screen.
    /// </summary>
    public async Task SaveAsync(IReadOnlyList<CloudBookmark> favorites, IReadOnlyList<CloudBookmark> recent, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try { await WriteUnsafeAsync(favorites, recent, ct).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Moves remembered places onto a renamed endpoint, in one read-modify-write. A bookmark is a pointer, so a
    /// rename that left it naming a remote rclone no longer has would empty the 收藏 list for reasons the user
    /// cannot see. Nothing is written when neither list changes.
    /// </summary>
    public async Task RenameRemoteAsync(string oldRemote, string newRemote, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var loaded = await ReadUnsafeAsync(backUpUnreadable: false, ct).ConfigureAwait(false);
            var favorites = CloudBrowserBookmarks.Renamed(loaded.Favorites, oldRemote, newRemote);
            var recent = CloudBrowserBookmarks.Renamed(loaded.Recent, oldRemote, newRemote);
            if (Same(favorites, loaded.Favorites) && Same(recent, loaded.Recent)) return;
            await WriteUnsafeAsync(favorites, recent, ct).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Reads the file. An unreadable file — unparseable, or a version this build does not know — is moved aside to
    /// a timestamped backup and read as empty, the same way the settings store treats a file it cannot parse: the
    /// alternative is to overwrite the only copy of the user's choices with a list nobody chose.
    /// </summary>
    private async Task<CloudBookmarkLoad> ReadUnsafeAsync(bool backUpUnreadable, CancellationToken ct)
    {
        if (!File.Exists(_path)) return CloudBookmarkLoad.Empty;
        CloudBookmarkFile? file;
        try
        {
            await using var stream = File.OpenRead(_path);
            file = await JsonSerializer.DeserializeAsync(stream, CloudBookmarkJsonContext.Default.CloudBookmarkFile, ct)
                .ConfigureAwait(false);
            if (file is null || file.Version != CloudBookmarkFile.CurrentVersion) throw new JsonException("无法识别的收藏文件。");
        }
        catch (JsonException)
        {
            if (backUpUnreadable)
            {
                var backup = _path + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.bak";
                File.Move(_path, backup, true);
                return new CloudBookmarkLoad([], [], true, backup);
            }

            return CloudBookmarkLoad.Empty;
        }

        // Entries the rules refuse are dropped rather than shown: the file is input somebody can edit, and a place
        // that cannot be addressed must not appear as a button that does nothing.
        return new CloudBookmarkLoad(Places(file.Favorites), Places(file.Recent));
    }

    private async Task WriteUnsafeAsync(IReadOnlyList<CloudBookmark> favorites, IReadOnlyList<CloudBookmark> recent, CancellationToken ct)
    {
        var file = new CloudBookmarkFile(CloudBookmarkFile.CurrentVersion, [.. favorites.Select(Record)], [.. recent.Select(Record)]);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using (stream.ConfigureAwait(false))
                await JsonSerializer.SerializeAsync(stream, file, CloudBookmarkJsonContext.Default.CloudBookmarkFile, ct)
                    .ConfigureAwait(false);
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static IReadOnlyList<CloudBookmark> Places(IReadOnlyList<CloudBookmarkRecord> records)
    {
        var places = new List<CloudBookmark>();
        foreach (var record in records)
        {
            if (CloudBrowserBookmarks.Normalize(record.Remote, record.Path) is not { } place) continue;
            if (!places.Any(existing => CloudBrowserBookmarks.SamePlace(existing, place))) places.Add(place);
        }

        return places;
    }

    private static CloudBookmarkRecord Record(CloudBookmark place) => new(place.Remote, place.Path);

    private static bool Same(IReadOnlyList<CloudBookmark> left, IReadOnlyList<CloudBookmark> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => CloudBrowserBookmarks.SamePlace(pair.First, pair.Second));
}

[JsonSerializable(typeof(CloudBookmarkFile))]
internal partial class CloudBookmarkJsonContext : JsonSerializerContext;
