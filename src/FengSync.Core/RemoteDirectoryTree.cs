namespace FengSync.Core;

public sealed record RemoteDirectoryNode(string Name, string Path, IReadOnlyList<RemoteDirectoryNode> Children);
public static class RemoteDirectoryTree
{
    /// <summary>RC backends differ: some return paths relative to the requested folder, others retain that folder prefix.</summary>
    public static string RelativeToListingRoot(string listedPath, string listingRoot)
    {
        var path = listedPath.Replace('\\', '/').Trim('/'); var root = listingRoot.Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(root)) return path;
        var prefix = root + "/";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : path;
    }
    public static RemoteDirectoryNode Build(IEnumerable<string> directories)
    {
        var root = new Mutable("", "");
        foreach (var raw in directories)
        {
            var current = root; var parts = raw.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts) current = current.Children.TryGetValue(part, out var next) ? next : current.Children[part] = new Mutable(part, string.IsNullOrEmpty(current.Path) ? part : current.Path + "/" + part);
        }
        return Freeze(root);
    }
    private static RemoteDirectoryNode Freeze(Mutable node) => new(node.Name, node.Path, node.Children.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(Freeze).ToList());
    private sealed class Mutable(string name, string path) { public string Name { get; } = name; public string Path { get; } = path; public Dictionary<string, Mutable> Children { get; } = new(StringComparer.OrdinalIgnoreCase); }
}
