using FengSync.Core;
using FengSync.Core.Configuration;

namespace FengSync.Services;

/// <summary>
/// Validation for the names the file browser creates. rclone treats a separator inside a name as a path,
/// so an unvalidated name would silently create a file somewhere else than the user pointed at.
/// </summary>
public static class CloudFileNames
{
    /// <summary>The longest single path segment rclone's backends accept in common.</summary>
    public const int MaxLength = 255;

    /// <summary>The reason <paramref name="name"/> cannot be used, or <see langword="null"/> when it can.</summary>
    public static string? Validate(string? name)
    {
        var value = name ?? "";
        if (value.Length == 0) return "请输入名称。";
        if (value != value.Trim()) return "名称首尾不能有空格。";
        if (value is "." or "..") return "名称不能是“.”或“..”。";
        if (value.Contains('/') || value.Contains('\\')) return "名称不能包含“/”或“\\”。";
        if (value.Any(char.IsControl)) return "名称不能包含控制字符。";
        if (value.Length > MaxLength) return $"名称不能超过 {MaxLength} 个字符。";
        return null;
    }

    /// <summary>True when <paramref name="name"/> is usable, for callers that only need the decision.</summary>
    public static bool IsValid(string? name) => Validate(name) is null;

    /// <summary>
    /// Where a rename lands: the same directory as <paramref name="path"/>, under the new name. Split out
    /// because the destination must stay in the original directory — a rename is not a move.
    /// </summary>
    public static string RenameTarget(string path, string newName) =>
        CloudFileManagerService.Join(CloudFileManagerService.Parent(path), newName);
}

/// <summary>What the user is about to delete, and how firmly they have to mean it.</summary>
/// <param name="Message">Text shown in the confirmation, listing what will go.</param>
/// <param name="IsBulk">
/// True when the batch is large enough that a single "yes" is too easy. The caller adds a second,
/// deliberate step for these.
/// </param>
/// <param name="IsIrreversible">
/// True when the backend has no trash. The caller must not present these as recoverable.
/// </param>
public sealed record CloudDeleteConfirmation(string Message, bool IsBulk, bool IsIrreversible);

/// <summary>
/// The safety rules for destructive cloud file operations. Deleting is the one action in the cloud manager
/// that a user cannot undo from inside this application, so the decision of how hard to confirm is kept
/// here — away from the window — where it can be stated once and tested.
/// </summary>
public static class CloudDeleteSafety
{
    /// <summary>Above this many items the confirmation adds a second, deliberate acknowledgement.</summary>
    public const int BulkThreshold = 10;

    /// <summary>How many names the confirmation lists before summarising the rest.</summary>
    private const int PreviewLimit = 8;

    /// <summary>
    /// Whether a backend's delete can be undone. The answer is the provider descriptor's, so the rule lives
    /// next to everything else known about that backend; only Google Drive is currently verified to keep a
    /// trash rclone uses by default (<c>--drive-use-trash</c> is on unless the remote turned it off). SFTP
    /// and S3 delete for real, and an S3 endpoint reached through a prefix-enforcing alias is still S3.
    /// </summary>
    public static bool SupportsTrash(EndpointType kind) => CloudProviderCatalog.SupportsTrash(kind);

    public static CloudDeleteConfirmation Describe(IReadOnlyList<CloudFileEntry> entries, bool supportsTrash)
    {
        var folders = entries.Count(entry => entry.IsDirectory);
        var files = entries.Count - folders;
        var lines = new List<string> { $"确定删除以下 {entries.Count} 项？", "" };
        lines.AddRange(entries.Take(PreviewLimit).Select(entry => "  " + entry.Path));
        if (entries.Count > PreviewLimit) lines.Add($"  （以及另外 {entries.Count - PreviewLimit} 项）");
        lines.Add("");

        // A folder delete purges the folder and everything under it, so the count above is a count of
        // things the user pointed at — not of everything that will disappear. Saying so is the difference
        // between an informed confirmation and a misleading one.
        if (folders > 0)
        {
            var composition = files > 0 ? $"文件夹 {folders} 个、文件 {files} 个" : $"文件夹 {folders} 个";
            lines.Add($"其中{composition}；文件夹会连同其中的全部内容一起删除。");
        }
        lines.Add(supportsTrash
            ? "该端点会先移入云端回收站，可在服务商的网页端还原。"
            : "该端点没有回收站，删除后无法恢复。");

        return new(string.Join(Environment.NewLine, lines), entries.Count > BulkThreshold, !supportsTrash);
    }

    /// <summary>
    /// The upload-conflict answer a batch runs with, so the question is asked once instead of per file.
    /// <see cref="CloudUploadConflictPolicy.Ask"/> stays <see langword="null"/> because the caller has to
    /// prompt for it.
    /// </summary>
    public static bool? OverwriteFor(CloudUploadConflictPolicy policy) => policy switch
    {
        CloudUploadConflictPolicy.Overwrite => true,
        CloudUploadConflictPolicy.Skip => false,
        _ => null
    };
}
