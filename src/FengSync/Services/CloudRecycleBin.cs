using FengSync.Localization;

namespace FengSync.Services;

/// <summary>
/// How one 回收站 command ended. Each value is a different fact about the cloud, and a command that could not
/// run must never be reported as one that did.
/// </summary>
public enum CloudRecycleOutcome
{
    /// <summary>The rows landed in the endpoint's trash folder.</summary>
    Moved,

    /// <summary>Trashed rows went back to the directories they came from.</summary>
    Restored,

    /// <summary>The user said no, so nothing was ever asked of the cloud.</summary>
    Declined,

    /// <summary>The rows already live in the trash; recycling them again would only shuffle them deeper.</summary>
    InsideTrash,

    /// <summary>The selection is the trash folder itself.</summary>
    IsTrashFolder,

    /// <summary>A restore was asked for something that was never recycled.</summary>
    NotInTrash,

    /// <summary>Nothing moved at all, and <see cref="CloudRecycleResult.Detail"/> says why.</summary>
    Failed,
}

/// <summary>One object and the path it is allowed to land on, plus whether that path needed a new name.</summary>
public sealed record CloudRecyclePlacement(string Source, string Destination, bool Renamed);

/// <summary>
/// The answer to one 移到回收站 or 还原 request, for the whole batch. <see cref="Placed"/> is what actually
/// moved — with the destination and the rename flag, because where a restore landed is the fact the user asked
/// for — and <see cref="Failed"/> counts the rows the cloud refused.
/// </summary>
public sealed record CloudRecycleResult(
    CloudRecycleOutcome Outcome,
    IReadOnlyList<CloudRecyclePlacement> Placed,
    int Failed = 0,
    string? Detail = null);

/// <summary>
/// The recycle bin, which is a real folder inside the endpoint rather than the backend's own trash. That choice
/// is forced by rclone v1.75.0, measured against the bundled binary: <c>operations/trash</c> is not a method at
/// all (<c>couldn't find method</c>, 404), the only trash switches are per-backend flags
/// (<c>--drive-use-trash</c>, default on) and none of them can be <em>listed</em> or <em>restored</em> through
/// the RC API — so a delete that leans on the backend has no 还原 to offer, and a folder the application moves
/// objects into can be listed, restored and emptied on every provider it supports.
///
/// Four measured hazards decide the rest of these rules.
/// <list type="number">
/// <item><c>operations/movefile</c> and <c>sync/move</c> both <strong>overwrite a destination that already
/// exists</strong> — a file silently replaces a file, and a directory move merges and lets the incoming bytes
/// win — so a landing name has to be chosen free before anything moves.</item>
/// <item>Both legs <strong>create missing parent directories</strong>, which is what lets an object be restored
/// to a folder the user deleted in the meantime and saves the trash folder itself from needing a mkdir.</item>
/// <item>A directory move onto a destination that already exists <strong>leaves the source behind as an empty
/// husk</strong> (<c>--delete-empty-src-dirs</c> never removes the source root), so the caller has to remove it;
/// the same call onto a fresh path removes it itself.</item>
/// <item>A landing name that belongs to an existing <em>directory</em> is refused outright (500 "is a directory
/// not a file"), which is why the free names are chosen from a listing rather than from the files only.</item>
/// </list>
/// </summary>
public static class CloudRecycleBin
{
    /// <summary>The folder's name. A leading dot keeps it out of the way in a web UI without hiding it anywhere.</summary>
    public const string FolderName = ".fengsync-trash";

    /// <summary>
    /// The endpoint's trash, addressed from the rclone remote root like every other browser path. It sits at the
    /// <em>endpoint's</em> root rather than the remote's, because that is the boundary the endpoint was
    /// configured with: an enforced-prefix S3 endpoint would otherwise put the trash outside the area rclone
    /// lets it write to.
    /// </summary>
    public static string RootOf(string endpointRoot) => CloudFileManagerService.Join(endpointRoot, FolderName);

    /// <summary>True when <paramref name="path"/> is the trash folder itself.</summary>
    public static bool IsRoot(string endpointRoot, string path) =>
        string.Equals(Trim(path), Trim(RootOf(endpointRoot)), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is inside the trash folder, but not the folder itself.</summary>
    public static bool IsInside(string endpointRoot, string path) =>
        !IsRoot(endpointRoot, path) && Trim(path).StartsWith(Trim(RootOf(endpointRoot)) + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="path"/> is the trash folder or a directory inside it — the views where 还原 and
    /// 清空回收站 belong. The folder itself counts: standing in it is exactly when a user wants to empty it.
    /// </summary>
    public static bool IsTrashView(string endpointRoot, string path) => IsRoot(endpointRoot, path) || IsInside(endpointRoot, path);

    /// <summary>
    /// The directory inside the trash that mirrors the directory <paramref name="path"/> lives in. Keeping the
    /// mirror is what makes 还原 a pure name transform rather than a lookup that has to remember where things
    /// came from: the object carries its own origin in its path, so nothing is lost if the app data is cleared.
    /// </summary>
    public static string LandingDirectory(string endpointRoot, string path) =>
        Trim(CloudFileManagerService.Join(RootOf(endpointRoot), RelativeToRoot(endpointRoot, CloudFileManagerService.Parent(path))));

    /// <summary>
    /// Where a trashed row goes back to: the mirror's own rule, drop the trash folder's prefix and put the
    /// endpoint root in front of it. <see langword="null"/> for a row that is not in the trash, because restoring
    /// something that was never recycled would move it somewhere the user did not ask for.
    /// </summary>
    public static string? OriginalPath(string endpointRoot, string path)
    {
        if (!IsInside(endpointRoot, path)) return null;
        var inside = Trim(path)[(Trim(RootOf(endpointRoot)).Length + 1)..];
        return inside.Length == 0 ? null : CloudFileManagerService.Join(Trim(endpointRoot), inside);
    }

    /// <summary>The directory a trashed row returns to — the endpoint root for a row that came from its top level.</summary>
    public static string OriginalDirectory(string endpointRoot, string path) =>
        OriginalPath(endpointRoot, path) is { } original ? CloudFileManagerService.Parent(original) : "";

    /// <summary>
    /// One placement per row, each on a name that is free in the directory it lands in.
    /// <paramref name="takenNamesIn"/> is that directory's own listing, and every name chosen here is claimed for
    /// the rest of the batch, so no two rows can be pointed at the same path. A row whose name is taken lands as
    /// <c>name (1)</c> rather than on top of the copy that got there first — replacing it would silently destroy
    /// the object the whole gesture exists to keep.
    /// </summary>
    public static IReadOnlyList<CloudRecyclePlacement> PlacementsFor(
        string endpointRoot,
        IReadOnlyList<CloudFileEntry> entries,
        Func<string, IReadOnlyCollection<string>> takenNamesIn,
        bool restore)
    {
        var placements = new List<CloudRecyclePlacement>(entries.Count);
        var claimed = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var directory = Trim(restore
                ? OriginalDirectory(endpointRoot, entry.Path)
                : LandingDirectory(endpointRoot, entry.Path));
            if (!claimed.TryGetValue(directory, out var names))
            {
                names = [.. takenNamesIn(directory)];
                claimed[directory] = names;
            }
            // KeepBothName exists for the upload case, where the plain name is taken by definition; here the name
            // is free unless the listing or an earlier row of this batch says otherwise, and an object that could
            // have gone back under its own name must not come home as "名称 (1)".
            var name = names.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)
                ? CloudFileNames.KeepBothName(entry.Name, names)
                : entry.Name;
            names.Add(name);
            placements.Add(new(entry.Path, CloudFileManagerService.Join(directory, name),
                !string.Equals(name, entry.Name, StringComparison.Ordinal)));
        }
        return placements;
    }

    /// <summary>
    /// The rows 移到回收站 may actually move, and the one reason it may not. A selection mixed with trashed rows
    /// is the user pointing at the folder and its neighbours at once, so the neighbours still move: refusing the
    /// whole gesture would be less useful than saying what was left out.
    /// </summary>
    public static (IReadOnlyList<CloudFileEntry> Movable, CloudRecycleOutcome? Refusal) SelectForMove(
        string endpointRoot, IReadOnlyList<CloudFileEntry> entries)
    {
        if (entries.Any(entry => IsRoot(endpointRoot, entry.Path))) return (entries, CloudRecycleOutcome.IsTrashFolder);
        if (entries.All(entry => IsInside(endpointRoot, entry.Path))) return (entries, CloudRecycleOutcome.InsideTrash);
        return (entries.Where(entry => !IsInside(endpointRoot, entry.Path)).ToList(), null);
    }

    /// <summary>The rows 还原 may move back: only the ones that came from somewhere.</summary>
    public static IReadOnlyList<CloudFileEntry> SelectForRestore(string endpointRoot, IReadOnlyList<CloudFileEntry> entries) =>
        entries.Where(entry => OriginalPath(endpointRoot, entry.Path) is not null).ToList();

    /// <summary>
    /// The question before anything moves. It has to say that space is <em>not</em> freed — a user who wanted the
    /// bytes back would otherwise discover that at the quota page — that a folder goes whole, because the mirror
    /// keeps its path rather than flattening it, and where to look afterwards, because a folder with a leading
    /// dot is easy to miss.
    /// </summary>
    public static string ConfirmFor(IReadOnlyList<CloudFileEntry> entries)
    {
        var folders = entries.Count(entry => entry.IsDirectory);
        var lines = new List<string>
        {
            Tr.Format("Recycle.ConfirmHeader", entries.Count),
            "",
            Tr.Format("Recycle.ConfirmMovesTo", FolderName),
        };
        if (folders > 0) lines.Add(Tr.Format("Recycle.ConfirmFolders", folders));
        lines.Add("");
        lines.Add(Tr.Format("Recycle.ConfirmHowToRestore", FolderName));
        lines.Add(Tr.F("Recycle.ConfirmRename"));
        lines.Add("");
        lines.Add(Tr.Format("Recycle.ConfirmFreeSpace", FolderName));
        lines.Add("");
        lines.Add(Tr.F("Recycle.ConfirmProceed"));
        return string.Join(Environment.NewLine, lines);
    }

    public static string Explain(CloudRecycleResult result) => result.Outcome switch
    {
        CloudRecycleOutcome.Moved =>
            Tr.Format("Recycle.Moved", result.Placed.Count, FolderName)
            + RenamedNote(result) + FailureNote(result),
        CloudRecycleOutcome.Restored =>
            Tr.Format("Recycle.Restored", result.Placed.Count) + RestoredNote(result) + FailureNote(result),
        CloudRecycleOutcome.Declined => Tr.F("Recycle.Declined"),
        CloudRecycleOutcome.InsideTrash => Tr.F("Recycle.InsideTrash"),
        CloudRecycleOutcome.IsTrashFolder => Tr.F("Recycle.IsTrashFolder"),
        CloudRecycleOutcome.NotInTrash => Tr.F("Recycle.NotInTrash"),
        CloudRecycleOutcome.Failed => Tr.F("Recycle.Failed") + (Why(result).Length > 0 ? " " + Why(result) : ""),
        _ => Tr.F("Recycle.Failed"),
    };

    /// <summary>
    /// The rows a move put where they belong, named when they had to take a different name. A batch in which
    /// some rows moved and some did not is said as counts first and reasons after, because the count is what
    /// tells the user whether to refresh: a silent half-move leaves them unsure what is left.
    /// </summary>
    private static string RenamedNote(CloudRecycleResult result)
    {
        var names = result.Placed.Where(placement => placement.Renamed)
            .Select(placement => CloudFileManagerService.NameOf(placement.Destination)).ToList();
        return names.Count == 0 ? "" : Tr.Format("Recycle.RenamedOnMove", names.Count, Join(names));
    }

    /// <summary>
    /// The restored batch's destination, named one by one when the original name was still taken: the user's
    /// whole question was where the object went, so a count alone would not answer it.
    /// </summary>
    private static string RestoredNote(CloudRecycleResult result)
    {
        if (result.Placed.Count == 1) return Tr.Format("Recycle.RestoredTo", result.Placed[0].Destination);
        var renamed = result.Placed.Where(placement => placement.Renamed).ToList();
        return renamed.Count == 0
            ? ""
            : Tr.Format("Recycle.RenamedOnRestore", renamed.Count,
                Join(renamed.Select(placement => CloudFileManagerService.NameOf(placement.Destination)).ToList()));
    }

    private static string FailureNote(CloudRecycleResult result) =>
        result.Failed > 0 ? Tr.Format("Recycle.SomeFailed", result.Failed, Why(result)) : "";

    /// <summary>The reason the cloud refused, when there is one; an empty string when the outcome needs no diagnosis.</summary>
    private static string Why(CloudRecycleResult result) => result.Detail ?? "";

    private static string Join(IReadOnlyList<string> names) =>
        string.Join(Tr.F("Recycle.NameSeparator"), names.Take(3)) + (names.Count > 3 ? Tr.F("Recycle.AndMore") : "");

    private static string Trim(string path) => (path ?? "").Trim('/');

    private static string RelativeToRoot(string endpointRoot, string path)
    {
        var root = Trim(endpointRoot);
        var value = Trim(path);
        if (root.Length == 0) return value;
        // The root addressed as itself is "nothing below the root", which is what puts a top-level object at the
        // trash folder's own level instead of under a copy of the root's name.
        if (value.Equals(root, StringComparison.OrdinalIgnoreCase)) return "";
        return value.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ? value[(root.Length + 1)..] : value;
    }
}
