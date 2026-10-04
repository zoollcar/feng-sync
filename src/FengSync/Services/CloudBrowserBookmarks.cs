namespace FengSync.Services;

/// <summary>One remembered place in the browser: which endpoint, and which directory inside it. <see cref="Path"/> is
/// relative to the rclone remote exactly as the browser addresses it, so the empty string is the endpoint's own
/// root — not "no path was recorded".</summary>
/// <param name="Remote">The rclone remote name, without the trailing colon.</param>
/// <param name="Path">The directory inside that endpoint, or empty for its root.</param>
public sealed record CloudBookmark(string Remote, string Path);

/// <summary>One row of a jump list: the place remembered, and the text that row shows.</summary>
public sealed record CloudJumpItem(string Label, CloudBookmark Bookmark);

/// <summary>
/// The 收藏 and 最近访问 rules. Both lists remember a place, but they are two different promises: a favourite is
/// something the user asked to keep, so its order is theirs and nothing here ever reorders or trims it; a recent
/// visit is a queue the application wrote, so it is newest-first and bounded. Keeping the two rules apart is what
/// stops "the user curated a list" from being quietly overwritten by "somebody browsed past".
/// </summary>
public static class CloudBrowserBookmarks
{
    /// <summary>
    /// How many visited directories are kept. Twenty is roughly one open dropdown on screen; past that the list is
    /// a scroll through everywhere the user has been rather than a choice of where to go back to, and the browser
    /// already has 后退 / 前进 for the current session.
    /// </summary>
    public const int RecentLimit = 20;

    /// <summary>
    /// Turns a remote name and a path into something worth remembering, or <see langword="null"/>. The name is
    /// checked rather than trusted because this record comes back from a file the user may have edited: a name
    /// carrying a colon or a slash would address a section rclone does not have, or escape the endpoint the
    /// bookmark was filed under. The path goes through the browser's own address rule, so a remembered place and
    /// a typed one cannot mean two different things.
    /// </summary>
    public static CloudBookmark? Normalize(string? remote, string? path)
    {
        var name = (remote ?? "").Trim().TrimEnd(':');
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains(':')) return null;
        var directory = CloudBrowse.Normalize(path, "");
        return new CloudBookmark(name, directory);
    }

    /// <summary>True when this place is already among the favourites.</summary>
    public static bool IsFavorite(IReadOnlyList<CloudBookmark> favorites, CloudBookmark place) =>
        favorites.Any(favorite => SamePlace(favorite, place));

    /// <summary>
    /// Adds a favourite. An existing one is left exactly where it is rather than moved to the end: the order of a
    /// curated list belongs to whoever curated it, and re-adding is not a request to reorder.
    /// </summary>
    public static IReadOnlyList<CloudBookmark> AddFavorite(IReadOnlyList<CloudBookmark> favorites, CloudBookmark place) =>
        IsFavorite(favorites, place) ? favorites : [.. favorites, place];

    public static IReadOnlyList<CloudBookmark> RemoveFavorite(IReadOnlyList<CloudBookmark> favorites, CloudBookmark place) =>
        [.. favorites.Where(favorite => !SamePlace(favorite, place))];

    /// <summary>
    /// True when the newest recorded visit already is this place. Refreshing the directory the user is standing in
    /// is not a new visit, and without this gate every 刷新 would rewrite the bookmark file for the same answer.
    /// </summary>
    public static bool IsNewestVisit(IReadOnlyList<CloudBookmark> recent, CloudBookmark place) =>
        recent.Count > 0 && SamePlace(recent[0], place);

    /// <summary>Records a visit: newest first, one entry per place, bounded by <see cref="RecentLimit"/>.</summary>
    public static IReadOnlyList<CloudBookmark> RecordVisit(IReadOnlyList<CloudBookmark> recent, CloudBookmark place)
    {
        var kept = recent.Where(entry => !SamePlace(entry, place)).Take(RecentLimit - 1).ToList();
        return [place, .. kept];
    }

    /// <summary>
    /// The bookmarks whose endpoint the browser can still offer. An entry for a missing endpoint is hidden rather
    /// than deleted: the endpoint can be absent because rclone.conf could not be read this once, and a favourite
    /// the user chose should still be there when it comes back.
    /// </summary>
    public static IReadOnlyList<CloudBookmark> VisibleFor(IReadOnlyList<CloudBookmark> bookmarks, IReadOnlyCollection<string> knownRemotes) =>
        [.. bookmarks.Where(bookmark => knownRemotes.Contains(bookmark.Remote, StringComparer.OrdinalIgnoreCase))];

    /// <summary>How many remembered places the visible list had to leave out — enough to say it, not to guess at it.</summary>
    public static int HiddenCount(IReadOnlyList<CloudBookmark> bookmarks, IReadOnlyCollection<string> knownRemotes) =>
        bookmarks.Count - VisibleFor(bookmarks, knownRemotes).Count;

    /// <summary>
    /// The rows a jump list shows. Nothing is re-ordered here: a favourite keeps its curator's order and a recent
    /// visit keeps the visit order, so the only decision this makes is which remembered places the browser can
    /// actually jump to, and what the endpoint is called in the text it shows — the name as the endpoint list has
    /// it, because a bookmark written before a case change should still read the way the list reads.
    /// </summary>
    public static IReadOnlyList<CloudJumpItem> JumpItems(IReadOnlyList<CloudBookmark> bookmarks, IReadOnlyCollection<string> endpointNames)
    {
        var items = new List<CloudJumpItem>();
        foreach (var bookmark in VisibleFor(bookmarks, endpointNames))
            items.Add(new CloudJumpItem(LabelFor(bookmark,
                endpointNames.FirstOrDefault(name => SameRemote(name, bookmark.Remote)) ?? bookmark.Remote), bookmark));
        return items;
    }

    /// <summary>
    /// Moves remembered places onto a renamed endpoint. A bookmark is a pointer, so a rename that left it pointing
    /// at a name rclone no longer has would empty the 收藏 list for reasons the user cannot see. Two entries that
    /// collide after the rewrite collapse into the first one, because one place is one entry in either list.
    /// </summary>
    public static IReadOnlyList<CloudBookmark> Renamed(IReadOnlyList<CloudBookmark> bookmarks, string oldRemote, string newRemote)
    {
        var moved = new List<CloudBookmark>();
        foreach (var bookmark in bookmarks)
        {
            var candidate = SameRemote(bookmark.Remote, oldRemote)
                ? bookmark with { Remote = newRemote }
                : bookmark;
            if (!moved.Any(existing => SamePlace(existing, candidate))) moved.Add(candidate);
        }

        return moved;
    }

    /// <summary>
    /// What one row of a jump list says. The endpoint is named first because the same directory name can exist on
    /// five endpoints, and the path is the part that tells them apart.
    /// </summary>
    public static string LabelFor(CloudBookmark bookmark, string endpointName) =>
        bookmark.Path.Length == 0 ? $"{endpointName}  ·  根目录" : $"{endpointName}  ·  /{bookmark.Path}";

    /// <summary>
    /// Whether a remembered place is the directory the browser is showing. Endpoint names are matched the way the
    /// endpoint list matches them (case-insensitively, because rclone's own section lookup is) while paths are
    /// matched exactly: a backend may be case-sensitive, and two directories whose names differ only by case are
    /// two places, not one written twice.
    /// </summary>
    public static bool SamePlace(CloudBookmark left, CloudBookmark right) =>
        SameRemote(left.Remote, right.Remote)
        && string.Equals(left.Path, right.Path, StringComparison.Ordinal);

    /// <summary>
    /// Whether two names address the same rclone section. Case-insensitive because that is how rclone looks a
    /// section up, so a bookmark written before an endpoint's case changed still means the endpoint it named.
    /// </summary>
    private static bool SameRemote(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
