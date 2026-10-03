using FengSync.Core;
using FengSync.Services;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// Covers <see cref="CloudEndpointList"/>. Both rules here look obvious and are not: a search that ignored
/// the enforced prefix would hide the endpoint the user is looking for behind its bucket name, and an
/// ordering by health is only worth offering if it reads as a to-do list rather than as alphabetical by an
/// enum's name.
/// </summary>
public sealed class CloudEndpointListTests
{
    // ---- search ---------------------------------------------------------------------------------------

    [Fact]
    public void An_empty_query_keeps_every_endpoint()
    {
        var account = Account("nas", "sftp", "SFTP", "team/docs");

        Assert.True(CloudEndpointList.Matches(account, null));
        Assert.True(CloudEndpointList.Matches(account, ""));
        // Whitespace is not a term: a stray space must not hide the whole list.
        Assert.True(CloudEndpointList.Matches(account, "   "));
    }

    [Fact]
    public void A_term_matches_the_name_the_provider_the_type_and_the_prefix()
    {
        var account = Account("nas", "sftp", "SFTP", "team/docs");

        Assert.True(CloudEndpointList.Matches(account, "nas"));
        Assert.True(CloudEndpointList.Matches(account, "SFTP"));
        Assert.True(CloudEndpointList.Matches(account, "sftp"));
        Assert.True(CloudEndpointList.Matches(account, "docs"));
        Assert.False(CloudEndpointList.Matches(account, "dropbox"));
    }

    [Fact]
    public void The_search_ignores_case()
    {
        var account = Account("Archive", "s3", "Cloudflare", "Team/Files");

        Assert.True(CloudEndpointList.Matches(account, "ARCHIVE"));
        Assert.True(CloudEndpointList.Matches(account, "cloudflare"));
        Assert.True(CloudEndpointList.Matches(account, "team/files"));
    }

    [Fact]
    public void A_second_term_narrows_the_result_instead_of_widening_it()
    {
        var archive = Account("archive", "s3", "Cloudflare", "team/files");
        var work = Account("work", "drive", "Google Drive", "team/work");

        // "team" alone matches both, because both live under a team prefix.
        Assert.True(CloudEndpointList.Matches(archive, "team"));
        Assert.True(CloudEndpointList.Matches(work, "team"));
        // The second term is what tells them apart — and both terms are about the same endpoint, so "team
        // drive" must not match the Cloudflare one through its name alone.
        Assert.False(CloudEndpointList.Matches(archive, "team drive"));
        Assert.True(CloudEndpointList.Matches(work, "team drive"));
        Assert.True(CloudEndpointList.Matches(work, "team   drive"));
    }

    [Fact]
    public void The_bucket_a_prefix_lives_in_is_searchable()
    {
        // An S3 endpoint named "archive" whose prefix is a bucket/prefix path: the endpoint the user is
        // looking for must be findable by either half of what the list shows next to it.
        var account = Account("archive", "s3", "Cloudflare", "feng-sync-e2e-test/reports");

        Assert.True(CloudEndpointList.Matches(account, "feng-sync-e2e-test"));
        Assert.True(CloudEndpointList.Matches(account, "reports"));
        Assert.True(CloudEndpointList.Matches(account, "archive reports"));
    }

    [Fact]
    public void An_endpoint_with_no_prefix_is_still_searchable_by_its_own_name()
    {
        var account = Account("nas", "sftp", "SFTP", "");

        Assert.True(CloudEndpointList.Matches(account, "nas"));
        Assert.True(CloudEndpointList.Matches(account, "sftp"));
    }

    [Fact]
    public void The_group_and_the_tags_are_searchable()
    {
        // Filing an endpoint under a group, or tagging it, is only worth doing if the search box finds it by
        // that word — otherwise the labels are decoration.
        var account = Account("nas", "sftp", "SFTP", "team/docs", group: "工作", tags: ["客户", "归档"]);

        Assert.True(CloudEndpointList.Matches(account, "工作"));
        Assert.True(CloudEndpointList.Matches(account, "客户"));
        // The group and a tag are different fields, so a two-word query still narrows rather than widening.
        Assert.True(CloudEndpointList.Matches(account, "工作 归档"));
        Assert.False(CloudEndpointList.Matches(account, "私人"));
    }

    // ---- ordering -------------------------------------------------------------------------------------

    [Fact]
    public void Ordering_by_name_is_alphabetical_in_both_directions()
    {
        var rows = Fixture();

        Assert.Equal(["archive", "attic", "busy", "legacy", "nas", "work"], Names(rows, CloudEndpointSortColumn.Name, descending: false));
        Assert.Equal(["work", "nas", "legacy", "busy", "attic", "archive"], Names(rows, CloudEndpointSortColumn.Name, descending: true));
    }

    [Fact]
    public void Ordering_by_health_ascending_is_a_to_do_list_not_an_alphabetical_enum()
    {
        // The point of offering this column is to put the endpoints that stopped working where the user will
        // see them, so ascending means "needs attention first" — not the enum's declaration order.
        Assert.Equal(["work", "legacy", "archive", "busy", "attic", "nas"], Names(Fixture(), CloudEndpointSortColumn.Health, descending: false));
        Assert.Equal(["attic", "nas", "busy", "archive", "legacy", "work"], Names(Fixture(), CloudEndpointSortColumn.Health, descending: true));
    }

    [Fact]
    public void Ordering_by_provider_groups_the_endpoints_of_one_service()
    {
        var rows = Fixture();

        Assert.Equal(["archive", "work", "attic", "legacy", "nas", "busy"], Names(rows, CloudEndpointSortColumn.Provider, descending: false));
    }

    [Fact]
    public void The_name_breaks_a_tie_in_the_same_direction_whichever_way_the_list_is_ordered()
    {
        // Reversing a grouped list must not also reverse the group: two SFTP endpoints listed as "nas, legacy"
        // in a descending view would be unreadable, and the name is what the user actually reads within a group.
        var ascending = Names(Fixture(), CloudEndpointSortColumn.Provider, descending: false);
        var descending = Names(Fixture(), CloudEndpointSortColumn.Provider, descending: true);

        Assert.Equal(["attic", "legacy", "nas"], ascending.Where(name => name is "attic" or "legacy" or "nas"));
        Assert.Equal(["attic", "legacy", "nas"], descending.Where(name => name is "attic" or "legacy" or "nas"));

        // The same holds when the tie is a health state rather than a provider.
        var healthTie = Names(Fixture(), CloudEndpointSortColumn.Health, descending: true);
        Assert.Equal(["attic", "nas"], healthTie.Where(name => name is "attic" or "nas"));
    }

    [Fact]
    public void The_search_is_applied_before_the_order()
    {
        // "team" is the shared prefix of four of the six, so it filters without reordering the survivors.
        var rows = Fixture();

        Assert.Equal(["work", "archive", "busy", "nas"], Names(rows, CloudEndpointSortColumn.Health, descending: false, query: "team"));
        Assert.Equal(["archive", "busy", "nas", "work"], Names(rows, CloudEndpointSortColumn.Name, descending: false, query: "team"));
        Assert.Empty(CloudEndpointList.Arrange(rows, "nothing-matches", CloudEndpointSortColumn.Name, false));
    }

    [Fact]
    public void Every_health_state_has_its_own_place_in_the_order()
    {
        // The ranks have to be distinct, or a stable sort would make the order depend on the input order.
        var ranks = Enum.GetValues<CloudEndpointHealth>().Select(CloudEndpointList.Rank).ToList();

        Assert.Equal(ranks.Count, ranks.Distinct().Count());
        Assert.True(CloudEndpointList.Rank(CloudEndpointHealth.NeedsLogin) < CloudEndpointList.Rank(CloudEndpointHealth.Unknown));
        Assert.True(CloudEndpointList.Rank(CloudEndpointHealth.Unreachable) < CloudEndpointList.Rank(CloudEndpointHealth.Connected));
        // An endpoint nobody has looked at yet sits above one that is being looked at right now, and both sit
        // above a healthy one: the bottom of the list is the list of things that are fine.
        Assert.True(CloudEndpointList.Rank(CloudEndpointHealth.Unknown) < CloudEndpointList.Rank(CloudEndpointHealth.Checking));
        Assert.True(CloudEndpointList.Rank(CloudEndpointHealth.Checking) < CloudEndpointList.Rank(CloudEndpointHealth.Connected));
    }

    [Fact]
    public void An_empty_list_arranges_to_an_empty_list()
    {
        Assert.Empty(CloudEndpointList.Arrange([], "nas", CloudEndpointSortColumn.Health, false));
        Assert.Empty(CloudEndpointList.Arrange([], null, CloudEndpointSortColumn.Name, true));
    }

    // ---- grouping -------------------------------------------------------------------------------------

    [Fact]
    public void Rows_arrive_grouped_so_each_heading_is_rendered_once()
    {
        // The list renders the heading above the first row of each group and nothing else, so the rows have to
        // arrive contiguous per group; a group whose rows were scattered would render the same heading several
        // times. The group is therefore the primary sort key even though it is not a column the user picks.
        List<CloudEndpointRow> rows =
        [
            Row("nas", "sftp", "SFTP", "team/docs", CloudEndpointHealth.Connected, "beta"),
            Row("archive", "s3", "Cloudflare", "team/files", CloudEndpointHealth.Unknown),
            Row("work", "drive", "Google Drive", "team/work", CloudEndpointHealth.NeedsLogin, "beta"),
            Row("home", "sftp", "SFTP", "", CloudEndpointHealth.Connected, "alpha")
        ];

        var arranged = CloudEndpointList.Arrange(rows, null, CloudEndpointSortColumn.Name, descending: false);

        Assert.Equal(["home", "nas", "work", "archive"], arranged.Select(row => row.Account.Name));
        // One heading per group, carried by the row that starts it; every other row carries none. Ordering by
        // name puts "nas" first inside the beta group, so "work" is the continuation.
        Assert.Equal(["alpha", "beta", "", "未分组"], arranged.Select(row => row.GroupHeading));
    }

    [Fact]
    public void A_row_carries_the_heading_of_the_group_it_starts_in_every_order()
    {
        List<CloudEndpointRow> rows =
        [
            Row("nas", "sftp", "SFTP", "", CloudEndpointHealth.Connected, "beta"),
            Row("home", "sftp", "SFTP", "", CloudEndpointHealth.Connected, "alpha"),
            Row("loose", "sftp", "SFTP", "", CloudEndpointHealth.Connected)
        ];

        foreach (var column in Enum.GetValues<CloudEndpointSortColumn>())
            foreach (var descending in new[] { false, true })
                Assert.Equal(["alpha", "beta", "未分组"],
                    CloudEndpointList.Arrange(rows, null, column, descending).Select(row => row.GroupHeading));
    }

    [Fact]
    public void A_filter_that_removes_a_groups_first_row_promotes_the_next_one()
    {
        // The heading belongs to whichever row now starts the group, not to a fixed row: filtering must not
        // leave a group with no heading at all.
        List<CloudEndpointRow> rows =
        [
            Row("nas", "sftp", "SFTP", "", CloudEndpointHealth.Connected, "beta"),
            Row("work", "sftp", "SFTP", "", CloudEndpointHealth.Connected, "beta")
        ];

        Assert.Equal(["beta", ""], CloudEndpointList.Arrange(rows, null, CloudEndpointSortColumn.Name, false).Select(row => row.GroupHeading));
        Assert.Equal(["beta"], CloudEndpointList.Arrange(rows, "work", CloudEndpointSortColumn.Name, false).Select(row => row.GroupHeading));
    }

    [Fact]
    public void The_ungrouped_rows_are_collected_under_one_heading_of_their_own()
    {
        List<CloudEndpointRow> rows =
        [
            Row("loose", "sftp", "SFTP", "", CloudEndpointHealth.Connected),
            Row("filed", "sftp", "SFTP", "", CloudEndpointHealth.Connected, "工作")
        ];

        Assert.Equal(["filed", "loose"], Names(rows, CloudEndpointSortColumn.Name, descending: false));
    }

    [Fact]
    public void The_chosen_sort_still_decides_the_order_inside_a_group()
    {
        // Grouping must not become the only ordering: inside a group the column the user picked still rules.
        List<CloudEndpointRow> rows =
        [
            Row("a-healthy", "sftp", "SFTP", "", CloudEndpointHealth.Connected, "工作"),
            Row("z-broken", "sftp", "SFTP", "", CloudEndpointHealth.NeedsLogin, "工作")
        ];

        Assert.Equal(["a-healthy", "z-broken"], Names(rows, CloudEndpointSortColumn.Name, descending: false));
        Assert.Equal(["z-broken", "a-healthy"], Names(rows, CloudEndpointSortColumn.Health, descending: false));
    }

    [Fact]
    public void A_group_name_is_kept_exactly_as_the_user_wrote_it()
    {
        // The heading is the user's own label; the list must not normalise it into something else.
        var row = Row("nas", "sftp", "SFTP", "", CloudEndpointHealth.Connected, "  工作  ");

        Assert.Equal("工作", row.Group);
        Assert.Equal("工作", row.GroupLabel);
    }

    // ---- helpers --------------------------------------------------------------------------------------

    /// <summary>
    /// One endpoint of each shape the list has to cope with: three sharing a provider (so a tie is possible),
    /// two with no prefix at all, and every health state the column can order by.
    /// </summary>
    private static IReadOnlyList<CloudEndpointRow> Fixture() =>
    [
        Row("nas", "sftp", "SFTP", "team/docs", CloudEndpointHealth.Connected),
        Row("archive", "s3", "Cloudflare", "team/files", CloudEndpointHealth.Unknown),
        Row("work", "drive", "Google Drive", "team/work", CloudEndpointHealth.NeedsLogin),
        Row("legacy", "sftp", "SFTP", "", CloudEndpointHealth.Unreachable),
        Row("busy", "webdav", "WebDAV", "team/webdav", CloudEndpointHealth.Checking),
        Row("attic", "sftp", "SFTP", "", CloudEndpointHealth.Connected)
    ];

    private static IReadOnlyList<string> Names(
        IReadOnlyList<CloudEndpointRow> rows,
        CloudEndpointSortColumn column,
        bool descending,
        string? query = null) =>
        [.. CloudEndpointList.Arrange(rows, query, column, descending).Select(row => row.Account.Name)];

    private static CloudEndpointRow Row(string name, string type, string provider, string prefix,
        CloudEndpointHealth health, string? group = null, IReadOnlyList<string>? tags = null)
    {
        var row = new CloudEndpointRow(Account(name, type, provider, prefix, group, tags));
        row.Status = new CloudEndpointStatus(health, "");
        return row;
    }

    /// <summary>An account whose metadata carries the prefix, which is where the list reads it from.</summary>
    private static CloudEndpointAccount Account(string name, string type, string provider, string prefix,
        string? group = null, IReadOnlyList<string>? tags = null)
    {
        var parts = prefix.Split('/', 2);
        var metadata = new CloudEndpointMetadata(name, type, provider, parts[0], parts.Length > 1 ? parts[1] : "",
            null, group, tags);
        return new CloudEndpointAccount(new RcloneAccount(name, type), metadata);
    }
}
