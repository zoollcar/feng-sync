using System.IO;
using FengSync.Core;
using FengSync.Core.Configuration;
using FengSync.Localization;

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
        if (value.Length == 0) return Tr.F("Safety.NameRequired");
        if (value != value.Trim()) return Tr.F("Safety.NameWhitespace");
        if (value is "." or "..") return Tr.F("Safety.NameDot");
        if (value.Contains('/') || value.Contains('\\')) return Tr.F("Safety.NameSeparator");
        if (value.Any(char.IsControl)) return Tr.F("Safety.NameControl");
        if (value.Length > MaxLength) return Tr.Format("Safety.NameTooLong", MaxLength);
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

    /// <summary>
    /// A name beside <paramref name="name"/> rather than on top of it: <c>report.pdf</c> becomes
    /// <c>report (1).pdf</c>, then <c>(2)</c>, until the name is free. Compared case-insensitively because
    /// most backends are, and cut back to <see cref="MaxLength"/> because a rename that produced a name the
    /// backend rejects would fail the upload the user chose 保留两者 to avoid losing.
    /// </summary>
    public static string KeepBothName(string name, IEnumerable<string> taken)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var attempt = 1; attempt <= MaximumRenameAttempts; attempt++)
        {
            var suffix = $" ({attempt})";
            var room = Math.Max(0, MaxLength - extension.Length - suffix.Length);
            var candidate = (stem.Length > room ? stem[..room] : stem) + suffix + extension;
            if (!taken.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return candidate;
        }
        // A directory that already holds "name (1)" through "name (9999)" is not a case the rename can
        // solve by counting further; the last candidate is returned so the caller's own overwrite rules
        // decide, rather than looping on a name that will never free up.
        return $"{stem} ({MaximumRenameAttempts}){extension}";
    }

    private const int MaximumRenameAttempts = 9999;
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

    /// <summary>
    /// The delete question. <paramref name="insideRecycleBin"/> is what 清空回收站 passes: the same counting, the
    /// same bulk threshold and the same second confirmation, with one extra line saying that the last safety net
    /// the application offers is the thing being removed. There is deliberately no second delete rule.
    /// </summary>
    public static CloudDeleteConfirmation Describe(IReadOnlyList<CloudFileEntry> entries, bool supportsTrash,
        bool insideRecycleBin = false)
    {
        var folders = entries.Count(entry => entry.IsDirectory);
        var files = entries.Count - folders;
        var lines = new List<string> { Tr.Format("Safety.ConfirmDelete", entries.Count), "" };
        lines.AddRange(entries.Take(PreviewLimit).Select(entry => "  " + entry.Path));
        if (entries.Count > PreviewLimit) lines.Add(Tr.Format("Safety.AndMore", entries.Count - PreviewLimit));
        lines.Add("");

        // A folder delete purges the folder and everything under it, so the count above is a count of
        // things the user pointed at — not of everything that will disappear. Saying so is the difference
        // between an informed confirmation and a misleading one.
        if (folders > 0)
        {
            var composition = files > 0
                ? Tr.Format("Safety.CompositionFilesFolders", folders, files)
                : Tr.Format("Safety.CompositionFolders", folders);
            lines.Add(Tr.Format("Safety.CompositionLine", composition));
        }
        if (insideRecycleBin)
            lines.Add(Tr.Format("Safety.InRecycleBin", CloudRecycleBin.FolderName));
        lines.Add(supportsTrash
            ? Tr.F("Safety.TrashSupported")
            : Tr.F("Safety.NoTrash"));

        return new(string.Join(Environment.NewLine, lines), entries.Count > BulkThreshold, !supportsTrash);
    }
}
