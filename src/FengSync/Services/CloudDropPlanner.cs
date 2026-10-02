using System.IO;
using FengSync.Core;

namespace FengSync.Services;

/// <summary>
/// Turns an Explorer drop payload into the concrete upload set. A dropped folder keeps its own name
/// as a new remote subdirectory and preserves the relative structure beneath it, so dropping
/// <c>C:\docs</c> onto remote <c>team</c> uploads <c>C:\docs\a\b.txt</c> to <c>team/docs/a/b.txt</c>.
/// Kept pure and injectable so the mapping is unit-testable without a window or a live rclone.
/// </summary>
public static class CloudDropPlanner
{
    public sealed record UploadItem(string LocalPath, string RemotePath);

    public static IReadOnlyList<UploadItem> Expand(IEnumerable<string> droppedPaths, string remoteDirectory)
    {
        var items = new List<UploadItem>();
        foreach (var path in droppedPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (Directory.Exists(path))
            {
                var folder = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    items.Add(new UploadItem(file, Join(Join(remoteDirectory, folder), Relative(path, file))));
            }
            else if (File.Exists(path))
            {
                items.Add(new UploadItem(path, Join(remoteDirectory, Path.GetFileName(path))));
            }
        }
        return items;
    }

    /// <summary>Remote-relative path of a file inside a dropped folder, always using forward slashes.</summary>
    public static string Relative(string folder, string file) =>
        Path.GetRelativePath(folder, file).Replace('\\', '/');

    public static string Join(string directory, string name)
    {
        var trimmedDirectory = (directory ?? "").Trim().Trim('/');
        var trimmedName = (name ?? "").Trim().Trim('/');
        if (trimmedName.Length == 0) return trimmedDirectory;
        return trimmedDirectory.Length == 0 ? trimmedName : trimmedDirectory + "/" + trimmedName;
    }
}

/// <summary>
/// Local landing area for drag-out downloads. Explorer copies the file named by the drop payload, so a
/// remote file must already exist on disk before the drag can start; this cache is what makes a second
/// drag of the same file instant and keeps the staging/resume machinery of <see cref="CloudTransferQueue"/> intact.
/// </summary>
public static class CloudDragCache
{
    public static string Root => Path.Combine(AppDataPaths.Root, "cloud-cache");

    public static string LocalPathFor(string remoteName, string remotePath) =>
        Path.Combine(Root, remoteName, remotePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// A cached copy is only reused when its length matches the cloud listing, so an edited remote file
    /// is re-downloaded instead of dragging a stale version out.
    /// </summary>
    public static bool IsUsable(string localPath, long expectedSize) =>
        expectedSize > 0 && File.Exists(localPath) && new FileInfo(localPath).Length == expectedSize;
}
