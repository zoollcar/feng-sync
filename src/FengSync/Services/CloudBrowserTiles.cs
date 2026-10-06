using System.IO;
using System.Security.Cryptography;
using System.Text;
using FengSync.Core;
using FengSync.Localization;

namespace FengSync.Services;

/// <summary>
/// Which browser rows may be painted as a picture, and where the bytes for that picture are kept. Two separate
/// questions, deliberately answered apart: whether a row <em>is</em> a picture the toolkit can decode, and what
/// a tile should do when it is not.
/// <para>
/// The decodable set is WPF's own built-in image formats, not the set of image extensions that exist. WebP and
/// AVIF are common in cloud drives and neither has a decoder in <c>PresentationCore</c>, so a tile that asked
/// for one would not show a broken picture but no picture at all — the honest answer is to leave it as a file
/// row and say why, which is what <see cref="RefusalFor"/> is for.
/// </para>
/// </summary>
public static class CloudBrowserTiles
{
    /// <summary>
    /// The extensions whose bytes WPF can decode with no plugin: BMP, GIF, ICO, JPEG, PNG and TIFF. Anything
    /// else is a file row, however "obvious" an image it looks by name.
    /// </summary>
    public static readonly IReadOnlySet<string> DecodableExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".bmp", ".gif", ".ico", ".jpg", ".jpeg", ".png", ".tif", ".tiff" };

    /// <summary>
    /// Picture formats that are named like a picture and are not in <see cref="DecodableExtensions"/>. The only
    /// job of this set is to make the refusal honest: a row ending <c>.webp</c> in a cloud drive is a photo, and
    /// saying "the toolkit cannot decode it" is a real answer, while saying nothing at all would read as though
    /// the app had not noticed the file.
    /// </summary>
    public static readonly IReadOnlySet<string> UndecodableImageExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".webp", ".avif", ".heic", ".heif", ".svg", ".raw", ".cr2", ".nef", ".arw", ".dng" };

    /// <summary>
    /// The largest object a tile will pull. A tile is decoration: it has to be paid for out of bandwidth the
    /// user did not ask to spend, on a row they may scroll past before it lands. Eight megabytes is already
    /// wider than any camera output worth showing at 64 device-independent pixels, and a file above it keeps
    /// its file icon instead of growing a half-downloaded one.
    /// </summary>
    public const long ThumbnailMaxBytes = 8L * 1024 * 1024;

    /// <summary>Total bytes the thumbnail cache may hold before the oldest entries are dropped.</summary>
    public const long CacheMaxBytes = 64L * 1024 * 1024;

    /// <summary>Tile edge length in device-independent pixels; the decoded bitmap is told to stop at this width.</summary>
    public const int TileEdgePixels = 64;

    /// <summary>
    /// True when this row is a picture small enough to be worth pulling. A directory is never a picture, a
    /// zero-length one is a listing that has not been read yet rather than an empty image, and an unknown size
    /// (<see cref="CloudFileEntry.Size"/> reported by a listing that does not carry one) is not treated as free.
    /// This is <see cref="DecideFor"/> read one way; it cannot disagree with it.
    /// </summary>
    public static bool OffersThumbnail(CloudFileEntry entry) => DecideFor(entry) == CloudTileDecision.Show;

    /// <summary>Whether the toolkit can decode this name's format at all, ignoring size.</summary>
    public static bool CanDecode(string name) => DecodableExtensions.Contains(Path.GetExtension(name));

    /// <summary>
    /// True for everything the bundled WPF throws when it is handed bytes it cannot turn into a picture. Both
    /// decode paths ask this, because a shape one of them missed does not fail quietly — it escapes through the
    /// tile round as a crash on the UI thread. Observed against the shipped toolkit: bytes no imaging component
    /// claims at all are a <see cref="NotSupportedException"/> ("No imaging component suitable"), an
    /// unrecognisable container a <see cref="FileFormatException"/> (0x88982F07, which derives from
    /// <see cref="SystemException"/> and so is <em>not</em> caught by an IOException filter), and a stream the
    /// decoder cannot complete an <see cref="InvalidOperationException"/>.
    /// </summary>
    public static bool RefusesDecode(Exception exception) =>
        exception is NotSupportedException or InvalidOperationException or FileFormatException
            or ArgumentException or IOException or UnauthorizedAccessException;

    /// <summary>
    /// Why a row is not going to show a picture, in one sentence a status line can carry. Null means either
    /// <see cref="CloudTileDecision.Show"/> or <see cref="CloudTileDecision.NotAPicture"/>, so this is never read
    /// as the whole answer on its own — <see cref="DecideFor"/> is, and it is the only place the offer and the
    /// refusal can disagree.
    /// </summary>
    public static string? RefusalFor(CloudFileEntry entry) => DecideFor(entry) switch
    {
        CloudTileDecision.Empty => Tr.Format("Thumb.Empty", entry.Name),
        CloudTileDecision.Undecodable => Tr.Format("Thumb.Undecodable", entry.Name, Path.GetExtension(entry.Name)),
        CloudTileDecision.TooLarge => Tr.Format("Thumb.TooLarge", entry.Name, CloudItemDetails.HumanSize(entry.Size), CloudItemDetails.HumanSize(ThumbnailMaxBytes)),
        _ => null,
    };

    /// <summary>
    /// The one honest answer for a row: a picture, or which of four separate reasons it is not. Kept as distinct
    /// states rather than a single "no" because they are not the same problem — an undecodable format will still
    /// be undecodable after it is re-uploaded, and a 20 MB photo would thumbnail fine if the cap were raised.
    /// </summary>
    public static CloudTileDecision DecideFor(CloudFileEntry entry)
    {
        if (entry.IsDirectory) return CloudTileDecision.NotAPicture;
        var extension = Path.GetExtension(entry.Name);
        if (UndecodableImageExtensions.Contains(extension)) return CloudTileDecision.Undecodable;
        if (!DecodableExtensions.Contains(extension)) return CloudTileDecision.NotAPicture;
        if (entry.Size <= 0) return CloudTileDecision.Empty;
        if (entry.Size > ThumbnailMaxBytes) return CloudTileDecision.TooLarge;
        return CloudTileDecision.Show;
    }

    /// <summary>
    /// The cache identity of one row: which endpoint, which path, how many bytes, and when it changed. Size and
    /// modification time are in it because a picture that was overwritten keeps its name — a cache keyed by name
    /// alone would go on painting the old bytes forever, and an old byte-for-byte identical file costs nothing to
    /// re-read.
    /// </summary>
    public static string KeyFor(string remote, CloudFileEntry entry)
    {
        var identity = $"{remote.TrimEnd(':')}\n{entry.Path}\n{entry.Size}\n{entry.ModifiedUtc?.UtcTicks.ToString() ?? "-"}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    /// <summary>
    /// Where the pulled bytes for one row live. The extension is kept so a cache directory can be inspected by
    /// hand and so the decoder still gets its format hint from the file itself. The root is a parameter because
    /// the location is the application's business while the name is the row's: a test aims the same rules at a
    /// temporary directory instead of the real app data.
    /// </summary>
    public static string CachedFileFor(string root, string remote, CloudFileEntry entry) =>
        Path.Combine(root, KeyFor(remote, entry) + Path.GetExtension(entry.Name).ToLowerInvariant());

    /// <summary>The cached file for one row in the application's own thumbnail directory.</summary>
    public static string CachedFileFor(string remote, CloudFileEntry entry) => CachedFileFor(AppDataPaths.CloudThumbnails, remote, entry);
}

/// <summary>Whether a browser row gets a picture tile, or which single reason keeps it a file row.</summary>
public enum CloudTileDecision
{
    /// <summary>A picture the toolkit can decode, within the tile size cap. Pull the bytes.</summary>
    Show,
    /// <summary>Not a picture at all — a folder, a document, an archive. A file row is the correct drawing.</summary>
    NotAPicture,
    /// <summary>Named like a picture, but <c>PresentationCore</c> has no decoder for this format.</summary>
    Undecodable,
    /// <summary>A picture with no bytes to decode.</summary>
    Empty,
    /// <summary>A picture bigger than a tile is allowed to cost. See <see cref="CloudBrowserTiles.ThumbnailMaxBytes"/>.</summary>
    TooLarge,
}
