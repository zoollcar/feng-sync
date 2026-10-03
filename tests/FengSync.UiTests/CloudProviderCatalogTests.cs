using FengSync.Core;
using FengSync.Services;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// Covers <see cref="CloudProviderCatalog"/>. The catalog exists to stop the knowledge of "which providers
/// exist" from being spread over a dozen switch statements, so these tests are mostly about the seams: that
/// the catalog, <see cref="EndpointSchemes"/> and the editor agree, and that a new provider cannot be added
/// half-way — a field with no AutomationId is invisible to the desktop scenario, and a provider the scheme
/// table has never heard of would be saved as a local folder.
/// </summary>
public sealed class CloudProviderCatalogTests
{
    [Fact]
    public void Every_provider_is_a_cloud_endpoint_type_that_the_scheme_table_also_knows()
    {
        Assert.NotEmpty(CloudProviderCatalog.Providers);
        foreach (var descriptor in CloudProviderCatalog.Providers)
        {
            Assert.Contains(descriptor.Kind, EndpointSchemes.CloudTypes);
            Assert.Equal(EndpointSchemes.SchemeFor(descriptor.Kind), descriptor.UriScheme);
            Assert.Equal(EndpointSchemes.RcloneTypeFor(descriptor.Kind), descriptor.RcloneType);
            Assert.Equal(EndpointSchemes.DisplayNameFor(descriptor.Kind), descriptor.DisplayName);
        }
        // And the other direction: a cloud type with no descriptor would be a provider the editor can never
        // offer, which is how a half-finished provider would show up.
        Assert.Equal(EndpointSchemes.CloudTypes.Count, CloudProviderCatalog.Providers.Count);
    }

    [Fact]
    public void The_provider_the_editor_lists_first_is_the_one_it_opens_on()
    {
        // The editor sets SelectedIndex = 0 and reads the descriptor off the item, so the order of the
        // catalogue is what a new endpoint defaults to. Google Drive is the historical default.
        Assert.Equal(EndpointType.GoogleDrive, CloudProviderCatalog.Providers[0].Kind);
    }

    [Theory]
    [InlineData(EndpointType.GoogleDrive, true)]
    [InlineData(EndpointType.OneDrive, true)]
    [InlineData(EndpointType.Dropbox, true)]
    [InlineData(EndpointType.Box, true)]
    [InlineData(EndpointType.WebDav, false)]
    [InlineData(EndpointType.Sftp, false)]
    [InlineData(EndpointType.S3, false)]
    public void Only_a_backend_that_authorizes_in_a_browser_requires_oauth(EndpointType kind, bool requiresOAuth)
    {
        Assert.Equal(requiresOAuth, CloudProviderCatalog.For(kind).RequiresOAuth);
        Assert.Equal(requiresOAuth, CloudProviderCatalog.RequiresOAuth(kind));
    }

    [Fact]
    public void Only_google_drive_claims_a_recycle_bin()
    {
        // The claim is asserted where it has been verified against the bundled rclone and nowhere else:
        // promising an undo that does not exist is worse than not offering one.
        Assert.True(CloudProviderCatalog.SupportsTrash(EndpointType.GoogleDrive));
        foreach (var kind in EndpointSchemes.CloudTypes.Where(kind => kind != EndpointType.GoogleDrive))
            Assert.False(CloudProviderCatalog.SupportsTrash(kind));
    }

    [Fact]
    public void A_type_that_is_not_a_provider_is_not_treated_as_one()
    {
        Assert.False(CloudProviderCatalog.IsProvider(EndpointType.Local));
        Assert.Null(CloudProviderCatalog.TryFor(EndpointType.Local));
        Assert.Throws<ArgumentOutOfRangeException>(() => CloudProviderCatalog.For(EndpointType.Local));
        // An unknown type answers "no" rather than throwing, because the delete confirmation asks about
        // endpoints it did not classify itself.
        Assert.False(CloudProviderCatalog.SupportsTrash(EndpointType.Local));
        Assert.False(CloudProviderCatalog.SupportsPublicLink(EndpointType.Local));
        Assert.False(CloudProviderCatalog.RequiresOAuth(EndpointType.Local));
    }

    [Fact]
    public void Every_field_carries_an_automation_id_unique_within_its_provider()
    {
        foreach (var descriptor in CloudProviderCatalog.Providers)
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.NameAutomationId));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.RootAutomationId));
            var ids = new[] { descriptor.NameAutomationId, descriptor.RootAutomationId }
                .Concat(descriptor.Fields.Select(field => field.AutomationId ?? ""))
                .ToList();
            foreach (var id in ids)
                // A field without an id cannot be driven by the desktop acceptance scenario, which is the
                // only thing that proves the generated form is really reachable.
                Assert.False(string.IsNullOrWhiteSpace(id), $"{descriptor.DisplayName} 有字段缺少 AutomationId。");
            // Uniqueness only has to hold inside one form, because only one provider's form is ever built.
            // The four OAuth providers deliberately share their client-credential ids.
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void Every_field_key_is_a_distinct_rclone_config_key()
    {
        foreach (var descriptor in CloudProviderCatalog.Providers)
        {
            var keys = descriptor.Fields.Select(field => field.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
            Assert.All(descriptor.Fields, field => Assert.False(string.IsNullOrWhiteSpace(field.Label)));
        }
    }

    [Fact]
    public void S3_has_no_descriptor_fields_because_its_editor_is_bespoke()
    {
        var s3 = CloudProviderCatalog.For(EndpointType.S3);
        Assert.True(s3.IsS3Compatible);
        Assert.Empty(s3.Fields);
        Assert.All(CloudProviderCatalog.Providers.Where(descriptor => descriptor.Kind != EndpointType.S3),
            descriptor => Assert.False(descriptor.IsS3Compatible));
    }

    [Fact]
    public void A_password_field_is_the_only_kind_that_needs_a_keep_the_stored_value_label()
    {
        foreach (var descriptor in CloudProviderCatalog.Providers)
        {
            foreach (var field in descriptor.Fields)
            {
                // A password box starts empty on an edit form because rclone stores the value obscured, so
                // its label has to say what empty means. Every other field is pre-filled.
                if (field.Kind == CloudFormFieldKind.Password)
                    Assert.False(string.IsNullOrWhiteSpace(field.EditLabel), $"{descriptor.DisplayName}/{field.Key} 缺少 EditLabel。");
            }
        }
    }

    [Fact]
    public void Merge_parameters_adds_the_constants_and_drops_blanks()
    {
        var drive = CloudProviderCatalog.For(EndpointType.GoogleDrive);
        var merged = drive.MergeParameters(new Dictionary<string, string>
        {
            ["client_id"] = "abc",
            ["client_secret"] = ""
        });

        // The scope is Google Drive's own constant, so the user is never asked about it.
        Assert.Equal("drive", merged["scope"]);
        Assert.Equal("abc", merged["client_id"]);
        // A blank value is dropped rather than written, so rclone keeps its own default for the option.
        Assert.False(merged.ContainsKey("client_secret"));
    }

    [Fact]
    public void The_sftp_password_or_key_file_rule_is_the_providers_own()
    {
        var rule = CloudProviderCatalog.For(EndpointType.Sftp).CrossFieldRule;
        Assert.NotNull(rule);
        var evaluate = rule!;

        Assert.NotNull(evaluate(new Dictionary<string, string> { ["pass"] = "", ["key_file"] = "" }));
        Assert.Null(evaluate(new Dictionary<string, string> { ["pass"] = "hunter2", ["key_file"] = "" }));
        Assert.Null(evaluate(new Dictionary<string, string> { ["pass"] = "", ["key_file"] = "/keys/id_rsa" }));
    }

    [Fact]
    public void Only_the_provider_that_needs_one_declares_a_cross_field_rule()
    {
        Assert.All(CloudProviderCatalog.Providers.Where(descriptor => descriptor.Kind != EndpointType.Sftp),
            descriptor => Assert.Null(descriptor.CrossFieldRule));
    }
}
