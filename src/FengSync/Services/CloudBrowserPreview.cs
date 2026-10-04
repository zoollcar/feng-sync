using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FengSync.Core;

namespace FengSync.Services;

/// <summary>
/// What the browser can show for one row without making the user download it first. The rule is a refusal by
/// default: this program renders a file only when the toolkit in front of the user can finish the job, and says
/// which part it cannot otherwise.
/// <para>
/// <see cref="CloudBrowserTiles"/> answers the same question for a 64-pixel decoration the app pulls on its own;
/// this answers it for a picture or a document the user explicitly asked to look at, so the ceilings differ and
/// neither borrows the other's bytes. A preview keeps its bytes in memory and writes nothing: the tile cache is
/// throwaway decoration, and handing its files to a panel would present a decoration as the object itself.
/// </para>
/// </summary>
public static class CloudBrowserPreview
{
    /// <summary>
    /// The largest picture a preview pulls. One explicit click on one row pays for this, which is why it may be
    /// larger than a tile's ceiling: 32 MB is above any JPEG a camera produces today, and still a small fraction
    /// of the raw TIFF and multi-gigabyte media a drive holds — those keep the download route.
    /// </summary>
    public const long PictureMaxBytes = 32L * 1024 * 1024;

    /// <summary>
    /// The largest text file a preview pulls. The panel shows around sixty lines at a time, and 256 KiB of
    /// ordinary lines is dozens of pages of scrolling — enough that "the rest of the log" is never a reason to
    /// download, small enough that asking for it is still a read rather than a transfer.
    /// </summary>
    public const long TextMaxBytes = 256L * 1024;

    /// <summary>
    /// The width a picture is decoded at. The panel is 1000 device-independent pixels wide, so pixels past this
    /// cannot be seen; the number is there to bound the decode, because a 32-megapixel photo decoded at full size
    /// is over a hundred megabytes of pixels held for a preview.
    /// </summary>
    public const int PreviewEdgePixels = 1024;

    /// <summary>
    /// Extensions whose bytes are expected to be read as text. Code and configuration count: a log or a CSV in a
    /// drive is the file a user most often wants to check without downloading.
    /// </summary>
    public static readonly IReadOnlySet<string> TextExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".text", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".jsonl", ".xml", ".yml", ".yaml",
            ".toml", ".ini", ".cfg", ".conf", ".properties", ".env", ".html", ".htm", ".css", ".scss", ".js", ".mjs",
            ".cjs", ".ts", ".jsx", ".tsx", ".cs", ".vb", ".fs", ".py", ".rb", ".go", ".rs", ".java", ".kt", ".c",
            ".h", ".cpp", ".hpp", ".sql", ".sh", ".bash", ".ps1", ".psm1", ".bat", ".cmd", ".vbs", ".lua", ".pl",
            ".r", ".dart", ".gradle", ".mk", ".cmake", ".srt", ".vtt", ".ics", ".asc", ".pem", ".key"
        };

    /// <summary>
    /// Formats a cloud drive is full of and WPF cannot render at all: documents, archives, audio, video, disc
    /// images and executables. The set exists only so the refusal can name the format — a PDF is out of scope
    /// because the application's target framework has no PDF renderer, and that is a different sentence from
    /// "nobody has heard of this extension".
    /// </summary>
    public static readonly IReadOnlySet<string> NoRendererExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".epub", ".mobi",
            ".zip", ".rar", ".7z", ".gz", ".xz", ".bz2", ".tar", ".tgz", ".iso", ".img", ".vhd", ".vhdx",
            ".exe", ".msi", ".dll", ".apk", ".ipa", ".deb", ".rpm",
            ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".aiff",
            ".mp4", ".m4v", ".mov", ".avi", ".mkv", ".wmv", ".flv", ".webm", ".mpg", ".mpeg", ".3gp"
        };

    /// <summary>Whether a row earns the 预览 command: a picture or a text file with bytes to read.</summary>
    public static bool OffersPreview(CloudFileEntry entry) => IsPaintable(DecideFor(entry));

    /// <summary>True for the two outcomes that end with something painted rather than explained.</summary>
    public static bool IsPaintable(CloudPreviewOutcome outcome) =>
        outcome is CloudPreviewOutcome.Picture or CloudPreviewOutcome.Text;

    /// <summary>
    /// How many bytes a read for this kind may ask for. Zero for anything that will not be painted, so a caller
    /// cannot accidentally pay for a read it has already been told to refuse.
    /// </summary>
    public static long ReadCapFor(CloudPreviewOutcome outcome) => outcome switch
    {
        CloudPreviewOutcome.Picture => PictureMaxBytes,
        CloudPreviewOutcome.Text => TextMaxBytes,
        _ => 0L
    };

    /// <summary>
    /// The ceiling a file name falls under, whichever of the two it is. Needed apart from
    /// <see cref="ReadCapFor(CloudPreviewOutcome)"/> because a refusal says which ceiling was crossed, and by the
    /// time a row has been decided <c>TooLarge</c> the kind it was promised to be is no longer in the outcome.
    /// </summary>
    public static long CapForName(string name)
    {
        var extension = Path.GetExtension(name);
        return CloudBrowserTiles.DecodableExtensions.Contains(extension) ? PictureMaxBytes
            : TextExtensions.Contains(extension) ? TextMaxBytes
            : 0L;
    }

    /// <summary>
    /// The pre-read answer, from the row's own name and listing size. Every state here is a different problem, so
    /// they are not collapsed: an undecodable photo format stays undecodable when it is re-uploaded, a 40 MB text
    /// file would preview if the ceiling moved, and a folder is not a refusal at all.
    /// </summary>
    public static CloudPreviewOutcome DecideFor(CloudFileEntry entry)
    {
        if (entry.IsDirectory) return CloudPreviewOutcome.Folder;
        var extension = Path.GetExtension(entry.Name);
        var kind =
            CloudBrowserTiles.DecodableExtensions.Contains(extension) ? CloudPreviewOutcome.Picture
            : CloudBrowserTiles.UndecodableImageExtensions.Contains(extension) ? CloudPreviewOutcome.PictureWithoutDecoder
            : TextExtensions.Contains(extension) ? CloudPreviewOutcome.Text
            : NoRendererExtensions.Contains(extension) ? CloudPreviewOutcome.NoRenderer
            : CloudPreviewOutcome.Unrecognized;
        if (!IsPaintable(kind)) return kind;
        // A listing that carries no size is not a promise of an empty file, but it is also not worth a read: the
        // preview says so and leaves the download route intact.
        if (entry.Size <= 0) return CloudPreviewOutcome.Empty;
        return entry.Size > ReadCapFor(kind) ? CloudPreviewOutcome.TooLarge : kind;
    }

    /// <summary>
    /// What the cloud's answer means for the row the name promised. The read's own status wins over the listing:
    /// an object that outgrew the size the listing recorded is reported with the total the server gave, not with
    /// the stale number on screen. Whether the bytes are then a picture or readable text is a second question,
    /// answered by <see cref="DecodePicture"/> and <see cref="ExtractText"/> on the bytes this returns the kind
    /// for — a state that costs a decode is never decided inside a status mapping.
    /// </summary>
    public static CloudPreviewOutcome OutcomeOf(CloudPreviewOutcome kind, RcloneObjectRead read)
    {
        if (read.Status != RcloneReadStatus.Read)
            return read.Status switch
            {
                RcloneReadStatus.TooLarge => CloudPreviewOutcome.TooLarge,
                RcloneReadStatus.Missing => CloudPreviewOutcome.Missing,
                _ => CloudPreviewOutcome.Failed
            };
        return read.Bytes.Length == 0 ? CloudPreviewOutcome.Empty : kind;
    }

    /// <summary>
    /// The one sentence that explains a row the panel will not paint. <paramref name="total"/> is the size the
    /// cloud reported for an object that turned out to be too big, or -1 when the listing's own size is all there
    /// is; <paramref name="detail"/> is rclone's refusal, shown rather than paraphrased.
    /// </summary>
    public static string ReasonFor(CloudPreviewOutcome outcome, CloudFileEntry entry, long total = -1, string? detail = null)
    {
        var extension = Path.GetExtension(entry.Name);
        return outcome switch
        {
            CloudPreviewOutcome.Folder =>
                $"“{entry.Name}”是文件夹。预览只看一个文件，文件夹要列出来才知道里面有什么。",
            CloudPreviewOutcome.PictureWithoutDecoder =>
                $"“{entry.Name}”是 {extension} 图片，本程序没有它的解码器。请下载后用图片查看器打开。",
            CloudPreviewOutcome.NoRenderer =>
                $"“{extension.TrimStart('.')}”不是本程序能渲染的格式（{entry.Name}）。内置渲染只有图片和文本，请下载后用对应的程序打开。",
            CloudPreviewOutcome.Unrecognized =>
                $"本程序不知道“{entry.Name}”{(extension.Length > 0 ? $"（{extension}）" : "（没有后缀）")}是什么格式，所以不预览。请下载后用对应的程序打开。",
            CloudPreviewOutcome.Empty => $"“{entry.Name}”没有字节可读。",
            CloudPreviewOutcome.TooLarge =>
                $"“{entry.Name}”有 {CloudItemDetails.HumanSize(total > 0 ? total : entry.Size)}，"
                + $"超过预览的上限 {CloudItemDetails.HumanSize(CapForName(entry.Name))}。请下载后打开。",
            CloudPreviewOutcome.Missing =>
                $"云端已没有“{entry.Name}”，列表里的这一行是上次浏览时的快照。",
            CloudPreviewOutcome.Failed =>
                $"读不到“{entry.Name}”：{(string.IsNullOrWhiteSpace(detail) ? "云端拒绝了这次读取" : detail!.TrimEnd('。', '.'))}。",
            CloudPreviewOutcome.Binary =>
                $"“{entry.Name}”里有 0 字节，它不是文本文件，预览画出来的只会是乱码。请下载后用对应的程序打开。",
            CloudPreviewOutcome.NotUtf8 =>
                $"“{entry.Name}”不是 UTF-8 或 UTF-16 文本。本程序不猜它是哪种代码页——猜错的中文看着像文件损坏。请下载后用编辑器打开。",
            CloudPreviewOutcome.UndecodableBytes =>
                $"“{entry.Name}”读到的字节不是本程序能解码的图片，多半是文件损坏或后缀写错了。",
            _ => $"“{entry.Name}”没有可预览的内容。"
        };
    }

    /// <summary>
    /// The text a preview paints, and what it was read as. A file whose bytes contain a NUL is binary whatever its
    /// name says; bytes that fail strict UTF-8 are refused rather than decoded with replacement characters, which
    /// would show a Chinese or Russian log as question marks and look like the file itself was damaged.
    /// </summary>
    public static (CloudPreviewOutcome Outcome, string Text, string Label) ExtractText(byte[] bytes)
    {
        if (StartsWith(bytes, [0xEF, 0xBB, 0xBF]))
            return (CloudPreviewOutcome.Text, Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8");
        if (StartsWith(bytes, [0xFF, 0xFE]))
            return (CloudPreviewOutcome.Text, Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 LE");
        if (StartsWith(bytes, [0xFE, 0xFF]))
            return (CloudPreviewOutcome.Text, Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE");
        if (bytes.Contains((byte)0)) return (CloudPreviewOutcome.Binary, "", "");
        try
        {
            return (CloudPreviewOutcome.Text, StrictUtf8.GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            return (CloudPreviewOutcome.NotUtf8, "", "");
        }
    }

    /// <summary>
    /// The bytes as a bitmap at preview size, or null when no decoder claims them. Which shapes the bundled WPF
    /// throws for that is documented once, on <see cref="CloudBrowserTiles.RefusesDecode"/>, because the tile path
    /// has to give the same answer for the same bytes.
    /// </summary>
    public static ImageSource? DecodePicture(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.DecodePixelWidth = PreviewEdgePixels;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (CloudBrowserTiles.RefusesDecode(ex))
        {
            return null;
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static bool StartsWith(byte[] bytes, byte[] prefix)
    {
        if (bytes.Length < prefix.Length) return false;
        for (var index = 0; index < prefix.Length; index++)
            if (bytes[index] != prefix[index]) return false;
        return true;
    }
}

/// <summary>
/// The panel a preview shows: one painted thing, or one sentence that says why nothing is painted. Which of the
/// states it is stays named rather than folded into a single "no" because the user's next step differs — a folder
/// needs no action, a too-large file needs a download, and a broken file needs a second look at the source.
/// </summary>
public sealed record CloudPreviewPanel(CloudPreviewOutcome Outcome, ImageSource? Image, string? Content,
    string? TextEncoding, string? Message)
{
    /// <summary>A panel that explains instead of painting. The sentence comes from <see cref="CloudBrowserPreview.ReasonFor"/>.</summary>
    public static CloudPreviewPanel Refused(CloudPreviewOutcome outcome, string message) =>
        new(outcome, null, null, null, message);

    /// <summary>A picture the toolkit decoded.</summary>
    public static CloudPreviewPanel Picture(ImageSource image) =>
        new(CloudPreviewOutcome.Picture, image, null, null, null);

    /// <summary>Text and the encoding it was read as, so the caption can say which one it was.</summary>
    public static CloudPreviewPanel Text(string content, string label) =>
        new(CloudPreviewOutcome.Text, null, content, label, null);
}

/// <summary>What the browser can and cannot do with one row when the user asks to see it in place.</summary>
public enum CloudPreviewOutcome
{
    /// <summary>A picture within the ceiling. Paint it.</summary>
    Picture,
    /// <summary>A text file within the ceiling whose bytes decode. Paint it.</summary>
    Text,
    /// <summary>A folder — not a refusal, there is just nothing one panel can show.</summary>
    Folder,
    /// <summary>Named like a picture, but <c>PresentationCore</c> has no decoder for the format.</summary>
    PictureWithoutDecoder,
    /// <summary>A format this target framework has no renderer for at all (documents, archives, audio, video).</summary>
    NoRenderer,
    /// <summary>An extension the application does not know, so it cannot promise anything about its bytes.</summary>
    Unrecognized,
    /// <summary>No bytes to show, whether the listing said so or the read did.</summary>
    Empty,
    /// <summary>Bigger than this kind's ceiling. See <see cref="CloudBrowserPreview.PictureMaxBytes"/>.</summary>
    TooLarge,
    /// <summary>The object is gone; the row is a stale listing.</summary>
    Missing,
    /// <summary>The cloud refused the read, and its own sentence is the answer.</summary>
    Failed,
    /// <summary>A text-named file whose bytes contain NULs — binary whatever the name says.</summary>
    Binary,
    /// <summary>A text file in a code page the application will not guess.</summary>
    NotUtf8,
    /// <summary>Picture bytes no decoder claimed, so the file is damaged or misnamed.</summary>
    UndecodableBytes,
}
