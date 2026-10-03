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
    /// endpoint's name, provider, type, prefix, group or tags. Requiring all terms is what makes a second
    /// word narrow the result instead of widening it, and matching the group and tags is what makes them
    /// worth setting: filing an endpoint under "工作" or tagging it "客户" is only useful if the search box
    /// finds it by that word.
    /// </summary>
    public static bool Matches(CloudEndpointAccount account, string? query)
    {
        var terms = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return true;
        var haystack = string.Join('\n', account.Name, account.Provider, account.Type, account.Prefix, account.Display,
            account.Metadata?.Group, CloudEndpointOrganization.FormatTags(account.Metadata?.Tags));
        return terms.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The rows the list should show: filtered by <paramref name="query"/>, ordered, and with the first row of
    /// each group marked so the list can render its heading. The name is always the final tie-break and is
    /// never reversed, so a descending list is still readable top to bottom.
    /// </summary>
    /// <remarks>
    /// The group is the primary key even though it is not one of the sort columns, because the list renders a
    /// heading above the first row of each group and nothing else. Rows therefore have to arrive contiguous
    /// per group; ordering them by anything else first would render the same heading several times.
    /// </remarks>
    public static IReadOnlyList<CloudEndpointRow> Arrange(
        IEnumerable<CloudEndpointRow> rows,
        string? query,
        CloudEndpointSortColumn column,
        bool descending)
    {
        var grouped = rows.Where(row => Matches(row.Account, query))
            .OrderBy(row => row.IsUngrouped ? 1 : 0)
            .ThenBy(row => row.GroupLabel, StringComparer.CurrentCulture);
        var arranged = column switch
        {
            CloudEndpointSortColumn.Provider => (descending
                    ? grouped.ThenByDescending(row => row.Account.Provider, StringComparer.OrdinalIgnoreCase)
                    : grouped.ThenBy(row => row.Account.Provider, StringComparer.OrdinalIgnoreCase))
                .ThenBy(row => row.Account.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            CloudEndpointSortColumn.Health => (descending
                    ? grouped.ThenByDescending(row => Rank(row.Health))
                    : grouped.ThenBy(row => Rank(row.Health)))
                .ThenBy(row => row.Account.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => (descending
                    ? grouped.ThenByDescending(row => row.Account.Name, StringComparer.OrdinalIgnoreCase)
                    : grouped.ThenBy(row => row.Account.Name, StringComparer.OrdinalIgnoreCase)).ToList()
        };
        for (var index = 0; index < arranged.Count; index++)
            arranged[index].MarkGroupStart(index == 0 || arranged[index - 1].GroupLabel != arranged[index].GroupLabel);
        return arranged;
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
