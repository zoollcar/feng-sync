namespace FengSync.Services;

/// <summary>Which column the file browser orders by. Directories always sort ahead of files.</summary>
public enum CloudSortColumn { Name, Size, Modified }

/// <summary>One clickable step of the address trail. <see cref="Path"/> is what selecting it navigates to.</summary>
public sealed record CloudBreadcrumb(string Name, string Path);

/// <summary>
/// The path and ordering rules of the cloud file browser, kept free of WPF and of rclone so they can be
/// tested directly. The browser addresses paths relative to the rclone remote root, bounded below by the
/// endpoint's configured root; every rule here exists to keep a typed or dragged address inside that
/// window instead of letting it address something the endpoint was not meant to reach.
/// </summary>
public static class CloudBrowse
{
    /// <summary>
    /// Turns typed text — or a path assembled from a drop — into a path the browser can use. Backslashes
    /// become separators, empty and <c>.</c> segments disappear, <c>..</c> pops a segment, and anything that
    /// would land above <paramref name="root"/> is clamped back onto it. Clamping rather than rejecting is
    /// deliberate: the address bar is also how a user recovers from a typo, and an endpoint root is a
    /// boundary the user cannot cross anyway, so refusing the input would only be a worse way to say the same thing.
    /// </summary>
    public static string Normalize(string? input, string? root)
    {
        var rootSegments = Segments(root);
        var typed = Segments(input);
        var resolved = new List<string>();
        foreach (var segment in typed)
        {
            if (segment == "..")
            {
                if (resolved.Count > 0) resolved.RemoveAt(resolved.Count - 1);
                continue;
            }
            resolved.Add(segment);
        }

        // A path that already names the root is kept; anything else is read as relative to it. This is what
        // makes "b" mean "root/b" while "root/b" still means "root/b".
        var startsAtRoot = rootSegments.Count == 0
            || (resolved.Count >= rootSegments.Count
                && rootSegments.Select((segment, index) => resolved[index].Equals(segment, StringComparison.OrdinalIgnoreCase)).All(match => match));
        return string.Join('/', startsAtRoot ? resolved : rootSegments.Concat(resolved));
    }

    /// <summary>
    /// The address trail from the endpoint root down to <paramref name="current"/>. The first crumb is the
    /// root itself and is labelled <paramref name="rootLabel"/> (the endpoint name), so the trail always
    /// shows where the endpoint's own boundary is — the one place the user cannot navigate above.
    /// </summary>
    public static IReadOnlyList<CloudBreadcrumb> Breadcrumbs(string? root, string? current, string rootLabel)
    {
        var rootSegments = Segments(root);
        var currentSegments = Segments(Normalize(current, root));
        var crumbs = new List<CloudBreadcrumb> { new(rootLabel, string.Join('/', rootSegments)) };
        for (var index = rootSegments.Count; index < currentSegments.Count; index++)
            crumbs.Add(new(currentSegments[index], string.Join('/', currentSegments.Take(index + 1))));
        return crumbs;
    }

    /// <summary>
    /// Orders a listing. Directories stay ahead of files in both directions, because the alternative —
    /// sorting a folder by its size, which is always zero — buries every folder under every file.
    /// </summary>
    public static IReadOnlyList<CloudFileEntry> Sort(IEnumerable<CloudFileEntry> entries, CloudSortColumn column, bool descending)
    {
        var comparison = Comparer(column);
        var ordered = entries.ToList();
        var directories = ordered.Where(entry => entry.IsDirectory).ToList();
        var files = ordered.Where(entry => !entry.IsDirectory).ToList();
        directories.Sort((left, right) => Apply(comparison(left, right), descending));
        files.Sort((left, right) => Apply(comparison(left, right), descending));
        return [.. directories, .. files];
    }

    /// <summary>
    /// Name filter over an already-listed directory. A blank query matches everything, so clearing the box
    /// restores the listing instead of emptying it.
    /// </summary>
    public static IReadOnlyList<CloudFileEntry> Filter(IEnumerable<CloudFileEntry> entries, string? query)
    {
        var needle = (query ?? "").Trim();
        if (needle.Length == 0) return entries.ToList();
        return entries.Where(entry => entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Whether a recursive search result is worth showing. Search matches the whole path rather than only
    /// the file name, because a user looking for "report" under a large tree means the folder too.
    /// </summary>
    public static bool MatchesQuery(CloudFileEntry entry, string? query)
    {
        var needle = (query ?? "").Trim();
        if (needle.Length == 0) return true;
        return entry.Path.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static Comparison<CloudFileEntry> Comparer(CloudSortColumn column) => column switch
    {
        CloudSortColumn.Size => (left, right) => TieBreak(left.Size.CompareTo(right.Size), left, right),
        CloudSortColumn.Modified => (left, right) => TieBreak(CompareModified(left.ModifiedUtc, right.ModifiedUtc), left, right),
        _ => Name
    };

    /// <summary>Every column falls back to the name so equal values keep a stable, readable order.</summary>
    private static int TieBreak(int comparison, CloudFileEntry left, CloudFileEntry right) =>
        comparison != 0 ? comparison : Name(left, right);

    private static int Name(CloudFileEntry left, CloudFileEntry right)
    {
        var byName = string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
        return byName != 0 ? byName : string.CompareOrdinal(left.Name, right.Name);
    }

    /// <summary>A missing timestamp sorts last in ascending order rather than pretending to be 1970.</summary>
    private static int CompareModified(DateTimeOffset? left, DateTimeOffset? right) => (left, right) switch
    {
        (null, null) => 0,
        (null, _) => 1,
        (_, null) => -1,
        _ => left.Value.CompareTo(right.Value)
    };

    private static int Apply(int comparison, bool descending) => descending ? -comparison : comparison;

    private static List<string> Segments(string? path) =>
        (path ?? "")
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment != ".")
            .ToList();
}
