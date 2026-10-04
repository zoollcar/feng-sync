using System.IO;

namespace FengSync.Services;

/// <summary>
/// The one decision a folder drag-out cannot make twice: which local paths have to exist before Explorer may
/// be handed the folder. A file drag needs one file, so it can afford to guess; a folder drag hands over a tree
/// the user will read as complete, so a partial mirror must never reach the drop target — Explorer copies what
/// is on disk and reports success either way. Every rule here is pure apart from <see cref="IsMirrorReady"/>,
/// which only asks the disk what the plan already named, and is the reason the window can start a drag at all.
/// </summary>
public static class CloudFolderDrag
{
    /// <summary>
    /// How many files one gesture may ask the queue to download. This is the browser's existing bound on one
    /// recursive read (<see cref="CloudFileManagerService.SearchResultLimit"/>), reused rather than invented: a
    /// folder drag is that same read, plus one journal row and one transfer per file it names.
    /// </summary>
    public const int MaxFilesPerDrag = CloudFileManagerService.SearchResultLimit;

    /// <summary>One file of the folder, in both coordinate systems a drag needs: the endpoint's for the queue,
    /// the local cache's for Explorer.</summary>
    public sealed record Item(string RemotePath, string LocalPath, long Size);

    /// <summary>
    /// The local mirror of one folder: its root, the directories inside it (empty ones included — a recursive
    /// listing does report them, which is what lets the structure survive the drag), its files, and the single
    /// reason the drag may not start. A plan that is refused names nothing the window should create.
    /// </summary>
    public sealed record Plan(
        string FolderLocalPath,
        IReadOnlyList<string> DirectoryLocalPaths,
        IReadOnlyList<Item> Files,
        long TotalBytes,
        string? Refusal)
    {
        public bool IsRefused => Refusal is not null;
        public int FileCount => Files.Count;
    }

    /// <summary>
    /// Builds the mirror for <paramref name="folder"/> from a recursive listing of it. The listing is the shape
    /// measured in <c>RcloneRecursiveListFactTests</c>: every path already relative to the remote root, the
    /// browsed folder itself absent from it, directories marked, and a directory's size never standing for its
    /// contents. Names the local filesystem cannot hold are refused rather than rewritten — a renamed file in a
    /// folder the user is about to trust is worse than no drag.
    /// </summary>
    public static Plan Build(CloudEndpointAccount account, CloudFileEntry folder, IReadOnlyList<CloudFileEntry> listing)
    {
        var folderRelative = CloudFileManagerService.RelativeToEndpoint(account, folder.Path);
        var folderLocal = CloudDragCache.LocalPathFor(account.Name, folderRelative);
        var prefix = folder.Path.Trim('/') + "/";

        var directories = new List<string>();
        var files = new List<Item>();
        foreach (var entry in listing)
        {
            var path = entry.Path.Trim('/');
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"递归列表给出的“{path}”不在被拖出的文件夹里。");
            var tail = path[prefix.Length..];

            // One account folder per endpoint name, so a remote name is only ever a path segment beneath it.
            var relative = CloudFileManagerService.Join(folderRelative, tail);
            var local = CloudDragCache.LocalPathFor(account.Name, relative);
            var refusal = Unrepresentable(relative, folderLocal, local);
            if (refusal is not null) return Refused(folderLocal, refusal);

            if (entry.IsDirectory) directories.Add(local);
            else files.Add(new Item(relative, local, entry.Size));
        }

        // S3 keys allow "x" and "x/y" in one bucket, and rclone reports the second as a directory of that name.
        // Windows cannot hold both, so the folder is refused rather than drawn half one way and half the other.
        var fileNames = new HashSet<string>(files.Select(x => x.LocalPath), StringComparer.OrdinalIgnoreCase);
        var directoryNames = new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase);
        var clashes = files.Where(x => directoryNames.Contains(x.LocalPath)).Select(x => x.LocalPath)
            .Concat(directories.Where(x => fileNames.Contains(x))).ToArray();
        if (clashes.Length > 0)
            return Refused(folderLocal, $"云端有同名的文件与文件夹（“{Path.GetFileName(clashes[0])}”），本地放不进同一个名字。");

        if (files.Count > MaxFilesPerDrag)
            return Refused(folderLocal, $"这个文件夹有 {files.Count} 个文件，一次最多拖出 {MaxFilesPerDrag} 个，请进入子文件夹分批拖出。");

        return new Plan(folderLocal, directories, files, files.Sum(x => x.Size), null);
    }

    /// <summary>
    /// Whether the bytes this row promised are on disk. A cloud file of 0 bytes is genuinely empty — measured, an
    /// empty object reads back as empty rather than as a refusal — so an existing empty file counts as ready,
    /// which is where this deliberately does <em>not</em> reuse <see cref="CloudDragCache.IsUsable"/>: that gate
    /// treats an unknown size as nothing to trust, and a folder holding one empty file would otherwise never be
    /// draggable at all.
    /// </summary>
    public static bool IsReady(Item file) =>
        System.IO.File.Exists(file.LocalPath) && new FileInfo(file.LocalPath).Length == file.Size;

    /// <summary>
    /// Whether the mirror is genuinely there: every directory exists, and every file exists with the length the
    /// cloud reported for it. There is no cheaper answer, because a directory's reported size is always 0, so
    /// nothing about the tree can be checked with one number.
    /// </summary>
    public static bool IsMirrorReady(Plan plan) =>
        !plan.IsRefused
        && Directory.Exists(plan.FolderLocalPath)
        && plan.DirectoryLocalPaths.All(Directory.Exists)
        && plan.Files.All(IsReady);

    /// <summary>Creates the directories the mirror needs, so a download has somewhere to land and an empty
    /// folder survives the drag even though no file will ever be written into it.</summary>
    public static void CreateDirectories(Plan plan)
    {
        Directory.CreateDirectory(plan.FolderLocalPath);
        foreach (var directory in plan.DirectoryLocalPaths) Directory.CreateDirectory(directory);
    }

    /// <summary>The name the status line uses for what is being dragged out.</summary>
    public static string Describe(Plan plan) =>
        plan.FileCount == 0
            ? $"{Path.GetFileName(plan.FolderLocalPath)}（空文件夹）"
            : $"{Path.GetFileName(plan.FolderLocalPath)} · {plan.FileCount} 个文件 · {CloudItemDetails.HumanSize(plan.TotalBytes)}";

    private static Plan Refused(string folderLocal, string reason) =>
        new(folderLocal, [], [], 0, reason);

    /// <summary>
    /// Why one reported name cannot become a local path, or null when it can. Only segments that would move the
    /// path out of the account's cache folder are refused: a cloud key may hold characters Windows reserves, and
    /// a drag that silently dropped it would hand over a folder that looks like the one on screen but is not.
    /// </summary>
    private static string? Unrepresentable(string relative, string folderLocal, string local)
    {
        var segments = relative.Split('/');
        foreach (var segment in segments)
        {
            if (segment == "." || segment == "..")
                return $"云端的路径里有一段“{segment}”，本地不能这样放。";
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return $"“{segment}”这个云端名字在本地不是合法的文件名。";
        }
        var root = Path.GetFullPath(folderLocal).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(local);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? null : "云端给出的名字会落到这个文件夹外面，没有拖出它。";
    }
}
