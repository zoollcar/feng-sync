using FengSync.Core;
using FengSync.Services;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// Grouping and tagging. Both live in this application's metadata file — rclone has nowhere to keep them —
/// and both are rebuilt whenever an endpoint is created or edited, so the risk is not that they are hard to
/// compute but that a path which does not know about them quietly drops them.
/// </summary>
public sealed class CloudEndpointOrganizationTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" 工作 ", "工作")]
    public void A_group_name_is_trimmed_and_blank_means_ungrouped(string? input, string? expected) =>
        Assert.Equal(expected, CloudEndpointOrganization.NormalizeGroup(input));

    [Fact]
    public void Tags_are_split_trimmed_deduplicated_and_blank_means_none()
    {
        Assert.Equal(new[] { "工作", "客户" }, CloudEndpointOrganization.ParseTags(" 工作 , 客户 "));
        // A Chinese input method produces the full-width separators without the user noticing, and 、 is what
        // the box shows, so a value read back out of it has to parse the same way it was written.
        Assert.Equal(new[] { "工作", "客户" }, CloudEndpointOrganization.ParseTags("工作；客户"));
        Assert.Equal(new[] { "工作", "客户" }, CloudEndpointOrganization.ParseTags("工作、客户"));
        Assert.Equal(new[] { "工作", "客户" }, CloudEndpointOrganization.ParseTags("工作，客户"));
        Assert.Equal(new[] { "a" }, CloudEndpointOrganization.ParseTags("a, A, a"));
        Assert.Null(CloudEndpointOrganization.ParseTags(""));
        Assert.Null(CloudEndpointOrganization.ParseTags(" , ,; "));
    }

    [Fact]
    public void Formatting_and_parsing_tags_round_trip()
    {
        var tags = CloudEndpointOrganization.ParseTags("工作，客户");

        Assert.Equal("工作、客户", CloudEndpointOrganization.FormatTags(tags));
        Assert.Equal(tags, CloudEndpointOrganization.ParseTags(CloudEndpointOrganization.FormatTags(tags)));
        Assert.Equal("", CloudEndpointOrganization.FormatTags(null));
    }

    [Fact]
    public void Every_row_has_a_heading_including_the_ungrouped_ones() =>
        Assert.Equal("未分组", CloudEndpointOrganization.GroupLabel(null));

    [Fact]
    public void Merging_keeps_everything_the_endpoint_already_recorded()
    {
        var existing = new CloudEndpointMetadata("nas", "alias", "SFTP", "team", "docs", "fengsync_backing_nas");

        var merged = CloudEndpointOrganization.Merge(existing, Account("nas", "sftp", "SFTP", "team", "docs"), "工作", ["客户"]);

        // The bucket, prefix and backing remote are what the endpoint's reach depends on; only the filing is
        // this call's business.
        Assert.Equal("team", merged.Bucket);
        Assert.Equal("docs", merged.Subdirectory);
        Assert.Equal("fengsync_backing_nas", merged.AliasOf);
        Assert.Equal("工作", merged.Group);
        Assert.Equal(new[] { "客户" }, merged.Tags!);
    }

    [Fact]
    public void Merging_falls_back_to_what_the_list_is_holding_when_the_store_has_no_record()
    {
        // An endpoint added to rclone.conf by hand has no metadata record. Saving its group must still work,
        // and must not lose the prefix the list is already showing.
        var account = Account("manual", "sftp", "SFTP", "team", "docs");

        var merged = CloudEndpointOrganization.Merge(null, account, "临时", null);

        Assert.Equal("team", merged.Bucket);
        Assert.Equal("docs", merged.Subdirectory);
        Assert.Equal("sftp", merged.Type);
        Assert.Equal("临时", merged.Group);
    }

    [Fact]
    public void A_rebuilt_record_carries_the_filing_and_the_backing_remote_across()
    {
        var existing = new CloudEndpointMetadata("nas", "alias", "SFTP", "team", "docs", "fengsync_backing_nas", "工作", ["客户"]);

        // What a create-or-edit path produces: it knows the provider, bucket and prefix, and nothing else.
        var rebuilt = CloudEndpointOrganization.Preserve(existing, new CloudEndpointMetadata("nas", "sftp", "SFTP", "team", "archive"));

        Assert.Equal("archive", rebuilt.Subdirectory);
        Assert.Equal("工作", rebuilt.Group);
        Assert.Equal(new[] { "客户" }, rebuilt.Tags!);
        Assert.Equal("fengsync_backing_nas", rebuilt.AliasOf);
    }

    [Fact]
    public void A_rebuilt_record_keeps_its_own_backing_remote_when_it_has_one()
    {
        var existing = new CloudEndpointMetadata("nas", "sftp", "SFTP", "team", "docs", null, "工作", null);

        var rebuilt = CloudEndpointOrganization.Preserve(existing, new CloudEndpointMetadata("nas", "alias", "S3", "b", "p", "fengsync_backing_nas"));

        Assert.Equal("fengsync_backing_nas", rebuilt.AliasOf);
        Assert.Equal("工作", rebuilt.Group);
    }

    [Theory]
    [InlineData("工作", null, "已将端点“nas”归入分组“工作”。")]
    [InlineData(null, null, "已清除端点“nas”的分组与标签。")]
    public void The_status_line_names_what_changed(string? group, IReadOnlyList<string>? tags, string expected) =>
        Assert.Equal(expected, CloudEndpointOrganization.Describe("nas", group, tags));

    [Fact]
    public void The_status_line_counts_the_tags_when_only_tags_were_set() =>
        Assert.Equal("已为端点“nas”设置 2 个标签。", CloudEndpointOrganization.Describe("nas", null, ["a", "b"]));

    [Fact]
    public void The_status_line_says_both_when_both_were_set() =>
        Assert.Equal("已更新端点“nas”的分组与标签。", CloudEndpointOrganization.Describe("nas", "工作", ["a"]));

    [Fact]
    public async Task Groups_and_tags_survive_a_write_and_a_read()
    {
        var path = NewMetadataPath();
        try
        {
            var store = new CloudEndpointMetadataStore(path);
            await store.UpsertAsync(new CloudEndpointMetadata("nas", "sftp", "SFTP", "team", "docs", null, "工作", ["客户", "归档"]));

            var loaded = (await store.LoadAsync())["nas"];

            Assert.Equal("工作", loaded.Group);
            Assert.Equal(new[] { "客户", "归档" }, loaded.Tags!);
            // The prefix is what the endpoint can reach; the filing must not have displaced it.
            Assert.Equal("team/docs", loaded.Prefix);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_file_written_before_grouping_existed_still_loads()
    {
        var path = NewMetadataPath();
        try
        {
            await File.WriteAllTextAsync(path,
                """[{"RemoteName":"nas","Type":"sftp","Provider":"SFTP","Bucket":"team","Subdirectory":"docs"}]""");

            var loaded = (await new CloudEndpointMetadataStore(path).LoadAsync())["nas"];

            Assert.Equal("team/docs", loaded.Prefix);
            Assert.Null(loaded.Group);
            Assert.Null(loaded.Tags);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Filing_an_endpoint_that_has_no_record_yet_still_saves()
    {
        // The regression this guards: an update that only mutated an existing record would silently do
        // nothing for an endpoint added to rclone.conf by hand, and the user would watch the dialog save a
        // group that never appears.
        var path = NewMetadataPath();
        try
        {
            var store = new CloudEndpointMetadataStore(path);
            var account = Account("manual", "sftp", "SFTP", "team", "docs");

            await store.UpsertAsync(CloudEndpointOrganization.Merge(await store.FindAsync("manual"), account, "临时", ["待清理"]));

            var loaded = (await store.LoadAsync())["manual"];
            Assert.Equal("临时", loaded.Group);
            Assert.Equal(new[] { "待清理" }, loaded.Tags!);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Looking_up_an_endpoint_that_was_never_recorded_answers_null()
    {
        var path = NewMetadataPath();
        try { Assert.Null(await new CloudEndpointMetadataStore(path).FindAsync("nothing")); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static string NewMetadataPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fengsync-endpoint-metadata", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "cloud-endpoints.json");
    }

    private static CloudEndpointAccount Account(string name, string type, string provider, string bucket, string subdirectory) =>
        new(new RcloneAccount(name, type), new CloudEndpointMetadata(name, type, provider, bucket, subdirectory));
}
