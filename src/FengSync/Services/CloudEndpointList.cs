namespace FengSync.Services;

/// <summary>What the endpoint list orders by.</summary>
public enum CloudEndpointSortColumn { Name, Provider, Health }

/// <summary>
/// Searching and ordering for the endpoint list. Kept as pure functions because the rules are the kind that
/// look obvious and are not: ordering by health only helps if it is a to-do list, and a search that ignores
/// the enforced prefix would hide the endpoint the user is looking for behind its bucket name.
/// </summary>
public static class CloudEndpointList
{
    /// <summary>
    /// True when every whitespace-separated term of <paramref name="query"/> appears somewhere in the
    /// endpoint's name, provider, type or prefix. Requiring all terms is what makes a second word narrow the
    /// result instead of widening it.
    /// </summary>
    public static bool Matches(CloudEndpointAccount account, string? query)
    {
        var terms = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return true;
        var haystack = string.Join('\n', account.Name, account.Provider, account.Type, account.Prefix, account.Display);
        return terms.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The rows the list should show: filtered by <paramref name="query"/>, then ordered. The name is always
    /// the final tie-break and is never reversed, so a descending list is still readable top to bottom.
    /// </summary>
    public static IReadOnlyList<CloudEndpointRow> Arrange(
        IEnumerable<CloudEndpointRow> rows,
        string? query,
        CloudEndpointSortColumn column,
        bool descending)
    {
        var matched = rows.Where(row => Matches(row.Account, query));
        return column switch
        {
            CloudEndpointSortColumn.Provider => (descending
                    ? matched.OrderByDescending(row => row.Account.Provider, StringComparer.OrdinalIgnoreCase)
                    : matched.OrderBy(row => row.Account.Provider, StringComparer.OrdinalIgnoreCase))
                .ThenBy(row => row.Account.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            CloudEndpointSortColumn.Health => (descending
                    ? matched.OrderByDescending(row => Rank(row.Health))
                    : matched.OrderBy(row => Rank(row.Health)))
                .ThenBy(row => row.Account.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => (descending
                    ? matched.OrderByDescending(row => row.Account.Name, StringComparer.OrdinalIgnoreCase)
                    : matched.OrderBy(row => row.Account.Name, StringComparer.OrdinalIgnoreCase)).ToList()
        };
    }

    /// <summary>
    /// Ascending health means "what needs attention first", not "alphabetical by enum name": the point of
    /// ordering by health is to put the endpoints that stopped working where the user will see them.
    /// </summary>
    internal static int Rank(CloudEndpointHealth health) => health switch
    {
        CloudEndpointHealth.NeedsLogin => 0,
        CloudEndpointHealth.Unreachable => 1,
        CloudEndpointHealth.Unknown => 2,
        CloudEndpointHealth.Checking => 3,
        _ => 4
    };
}
