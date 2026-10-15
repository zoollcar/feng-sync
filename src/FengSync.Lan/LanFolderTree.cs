using FengSync.Core;

namespace FengSync.Lan;

/// <summary>
/// The one walk that turns a local folder into the set of things a peer should receive.
/// </summary>
/// <remarks>
/// Both directions that hand over a tree need it — the zip a phone downloads and the per-file push to another
/// computer — and they must agree about what a folder contains, or the same folder would mean two different
/// things depending on which way the bytes went. It lives beside the protocol rather than inside
/// <see cref="LanZipStream"/> so the rule has one owner.
/// </remarks>
public static class LanFolderTree
{
    /// <summary>One entry of a folder tree.</summary>
    /// <param name="RelativePath">Its path from the walked root, with forward slashes — the form the wire rules take.</param>
    /// <param name="Name">Its own last segment.</param>
    public sealed record Entry(string RelativePath, string Name, bool IsDirectory);

    /// <summary>
    /// Every shareable entry under a root, a directory before anything inside it, in a stable order.
    /// </summary>
    /// <remarks>
    /// The exclusions are the ones a local scan already applies, so a peer is never handed this application's own
    /// partial files, and skipping a reparse point is what stops a junction from walking somebody else's tree
    /// twice. A parent is yielded before its children because that is the only order a receiver can act on:
    /// a directory has to exist on the far side before a file inside it is admitted.
    /// </remarks>
    public static IEnumerable<Entry> Entries(string root)
    {
        var full = Path.GetFullPath(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(full).OrderBy(entry => entry, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (name.Length == 0 || SyncInternalPaths.IsExcludedFromScan(name)) continue;

            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.System) || attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            yield return new Entry(name, name, isDirectory);
            if (!isDirectory) continue;
            foreach (var child in Entries(path).Select(entry => entry with { RelativePath = name + "/" + entry.RelativePath }))
                yield return child;
        }
    }
}
