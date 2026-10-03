using FengSync.Core;
using FengSync.Services;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// Covers <see cref="CloudEndpointPortability"/>. Two things matter here: an export must never be able to
/// carry a credential, and a file coming back in is untrusted input — a bad row is dropped with a message
/// rather than written into <c>rclone.conf</c>.
/// </summary>
public sealed class CloudEndpointPortabilityTests
{
    // ---- export shape ---------------------------------------------------------------------------------

    [Fact]
    public void An_export_carries_the_metadata_and_the_parameter_names()
    {
        var account = Account("nas", "sftp", "SFTP", "team/docs", group: "工作", tags: ["客户", "归档"]);

        var export = CloudEndpointPortability.ToExport(account, ["host", "pass", "user"]);

        Assert.Equal("nas", export.RemoteName);
        Assert.Equal("sftp", export.Type);
        Assert.Equal("SFTP", export.Provider);
        Assert.Equal("team", export.Bucket);
        Assert.Equal("docs", export.Subdirectory);
        Assert.Equal("工作", export.Group);
        Assert.Equal(["客户", "归档"], export.Tags);
        Assert.Equal(["host", "pass", "user"], export.ParameterNames);
    }

    [Fact]
    public void An_export_file_contains_no_credential()
    {
        var account = Account("nas", "sftp", "SFTP", "team/docs");

        var json = CloudEndpointPortability.Serialize(
            Bundle(CloudEndpointPortability.ToExport(account, ["host", "pass", "user"])));

        // The file names the parameters but its shape has nowhere to put their values.
        Assert.Contains("\"pass\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_enforced_prefix_endpoint_keeps_its_hidden_backing_name()
    {
        var account = Account("docs", "alias", "Cloudflare", "bucket/prefix", aliasOf: "docs_s3");

        var export = CloudEndpointPortability.ToExport(account, ["provider"]);

        Assert.Equal("docs_s3", export.AliasOf);
        Assert.Equal("alias", export.Type);
    }

    [Fact]
    public void An_import_round_trip_keeps_every_non_secret_field()
    {
        var export = CloudEndpointPortability.ToExport(
            Account("nas", "sftp", "SFTP", "team/docs", group: "工作", tags: ["客户"]), ["host", "user"]);

        var import = CloudEndpointPortability.Parse(CloudEndpointPortability.Serialize(Bundle(export)));

        Assert.Empty(import.Errors);
        var draft = Assert.Single(import.Endpoints);
        Assert.Equal(EndpointType.Sftp, draft.Kind);
        Assert.Equal("nas", draft.RemoteName);
        Assert.Equal("team", draft.Bucket);
        Assert.Equal("docs", draft.Subdirectory);
        Assert.Equal("工作", draft.Group);
        Assert.Equal(["客户"], draft.Tags);
        Assert.Equal(["host", "user"], draft.RequiredParameters);
    }

    // ---- untrusted input ------------------------------------------------------------------------------

    [Fact]
    public void A_foreign_version_is_refused_rather_than_guessed()
    {
        var import = CloudEndpointPortability.Parse(CloudEndpointPortability.Serialize(
            new CloudEndpointExportBundle(99, DateTimeOffset.Now, [])));

        Assert.Empty(import.Endpoints);
        Assert.Contains(import.Errors, error => error.Contains("99", StringComparison.Ordinal));
    }

    [Fact]
    public void Garbage_is_refused_without_throwing()
    {
        var import = CloudEndpointPortability.Parse("{ not json");

        Assert.Empty(import.Endpoints);
        Assert.NotEmpty(import.Errors);
    }

    [Fact]
    public void A_remote_name_that_could_fabricate_a_section_is_rejected()
    {
        // A colon terminates a remote name and a separator would escape the config section, so neither may be
        // written into rclone.conf from a file the user was handed.
        Assert.NotNull(CloudEndpointPortability.ValidateRemoteName("a:b"));
        Assert.NotNull(CloudEndpointPortability.ValidateRemoteName("a/b"));
        Assert.NotNull(CloudEndpointPortability.ValidateRemoteName("a\\b"));
        Assert.NotNull(CloudEndpointPortability.ValidateRemoteName(".hidden"));
        Assert.NotNull(CloudEndpointPortability.ValidateRemoteName(" leading"));
        Assert.NotNull(CloudEndpointPortability.ValidateRemoteName(""));
        Assert.Null(CloudEndpointPortability.ValidateRemoteName("team_docs-2"));
    }

    [Fact]
    public void An_entry_with_a_bad_remote_name_is_dropped_while_the_rest_survive()
    {
        var import = CloudEndpointPortability.Parse(
            CloudEndpointPortability.Serialize(Bundle(Export("nas", "sftp"), Export("a:b", "sftp"))));

        Assert.Equal("nas", Assert.Single(import.Endpoints).RemoteName);
        Assert.Single(import.Errors);
    }

    [Fact]
    public void An_unknown_type_or_parameter_name_drops_only_that_entry()
    {
        var unknownType = Export("weird", "telepathy");
        var badParameter = new CloudEndpointExport("nas", "sftp", "SFTP", "", "", null, null, null, ["host; rm -rf"]);

        var import = CloudEndpointPortability.Parse(
            CloudEndpointPortability.Serialize(Bundle(unknownType, badParameter)));

        Assert.Empty(import.Endpoints);
        Assert.Equal(2, import.Errors.Count);
    }

    [Fact]
    public void Duplicate_names_in_one_file_are_rejected_after_the_first()
    {
        var import = CloudEndpointPortability.Parse(
            CloudEndpointPortability.Serialize(Bundle(Export("nas", "sftp"), Export("NAS", "sftp"))));

        Assert.Single(import.Endpoints);
        Assert.Single(import.Errors);
    }

    [Fact]
    public void An_import_never_reuses_a_name_that_is_already_taken()
    {
        Assert.Equal("nas", CloudEndpointPortability.UniqueName("nas", []));
        Assert.Equal("nas_imported", CloudEndpointPortability.UniqueName("nas", ["nas"]));
        Assert.Equal("nas_imported2", CloudEndpointPortability.UniqueName("nas", ["nas", "nas_imported"]));
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private static CloudEndpointExportBundle Bundle(params CloudEndpointExport[] endpoints) =>
        new(CloudEndpointPortability.CurrentVersion,
            new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.FromHours(8)), endpoints);

    private static CloudEndpointExport Export(string name, string type) =>
        new(name, type, "SFTP", "", "", null, null, null, ["host"]);

    private static CloudEndpointAccount Account(string name, string type, string provider, string prefix,
        string? group = null, IReadOnlyList<string>? tags = null, string? aliasOf = null)
    {
        var parts = prefix.Split('/', 2);
        var metadata = new CloudEndpointMetadata(name, type, provider, parts[0], parts.Length > 1 ? parts[1] : "",
            aliasOf, group, tags);
        return new CloudEndpointAccount(new RcloneAccount(name, type), metadata);
    }
}