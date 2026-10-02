using System.Runtime.ExceptionServices;
using System.Windows;
using FengSync.Core.Updates;
using FengSync.Services;
using FengSync.Views;
using Xunit;

namespace FengSync.UiTests;

public sealed class UiFeatureBehaviorTests
{
    [Fact]
    public void Cloud_file_manager_normalizes_navigation_and_formats_entries()
    {
        Assert.Equal("folder/file.txt", CloudFileManagerService.Join("/folder/", "file.txt"));
        Assert.Equal("folder", CloudFileManagerService.Parent("/folder/file.txt/"));
        Assert.Equal("", CloudFileManagerService.Parent("file.txt"));
        var directory = new CloudFileEntry("docs", "docs", true, 123, null);
        var file = new CloudFileEntry("proof.pdf", "proof.pdf", false, 2 * 1024 * 1024, DateTimeOffset.UtcNow);
        Assert.Equal("文件夹", directory.Type);
        Assert.Equal("", directory.SizeDisplay);
        Assert.Equal("文件", file.Type);
        Assert.Equal("2.0 MB", file.SizeDisplay);
    }

    [Fact]
    public void S3_endpoint_uri_keeps_the_bucket_out_of_the_service_endpoint()
    {
        Assert.Equal("s3://r2/feng-sync-e2e-test/path",
            CloudEndpointService.BuildUri(CloudEndpointService.ProviderKind.S3, "r2", "/feng-sync-e2e-test/path/"));
    }

    [Fact]
    public void S3_settings_reject_unknown_providers_and_bucket_paths_inside_endpoint()
    {
        var providers = new[] { "Cloudflare", "AWS" };
        var unknown = CloudEndpointService.ValidateS3Settings(new("R2", "R2", "key", "secret", "auto", "https://example.test", "bucket", ""), providers);
        var endpoint = CloudEndpointService.ValidateS3Settings(new("R2", "Cloudflare", "key", "secret", "auto", "https://example.test/bucket", "bucket", ""), providers);
        var valid = CloudEndpointService.ValidateS3Settings(new("R2", "Cloudflare", "key", "secret", "auto", "https://example.test", "bucket", "path"), providers);
        Assert.Contains("provider", unknown.Keys);
        Assert.Contains("endpoint", endpoint.Keys);
        Assert.Empty(valid);
    }

    [Fact]
    public void S3_draft_invalidates_a_successful_probe_after_any_field_change()
    {
        var draft = new S3EndpointDraft();
        var initial = new S3EndpointValues("R2", "Cloudflare", "key", "secret", "auto", "https://example.test", "bucket", "path");
        draft.Update(initial); draft.RecordProbe(true);
        Assert.True(draft.HasCurrentSuccessfulTest);
        draft.Update(initial with { Bucket = "other" });
        Assert.False(draft.HasCurrentSuccessfulTest);
    }

    [Fact]
    public async Task Cloud_endpoint_metadata_round_trips_bucket_and_subdirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FengSync-metadata-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "cloud-endpoints.json");
        try
        {
            var store = new CloudEndpointMetadataStore(path);
            await store.UpsertAsync(new("r2", "s3", "Cloudflare", "bucket", "team/files"));
            var restored = Assert.Single(await store.LoadAsync()).Value;
            Assert.Equal("bucket/team/files", restored.RootPath);
            await store.DeleteAsync("r2");
            Assert.Empty(await store.LoadAsync());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void S3_provider_presets_add_backblaze_without_shadowing_rclone_metadata()
    {
        // rclone 1.75's s3 backend has 53 providers and none of them is Backblaze: B2 has its own
        // dedicated `b2` backend, so its S3-compatible API is rclone's generic "Other" provider.
        var merged = CloudEndpointService.MergeS3Providers([new("AWS", "Amazon", ["us-east-1"]), new("Minio", "Minio", [])]);

        var backblaze = Assert.Single(merged, provider => provider.Name == "Backblaze");
        Assert.Equal("Other", backblaze.RcloneValue);
        Assert.NotEmpty(backblaze.Description);
        Assert.Equal("AWS", merged[0].Name);
        Assert.Equal("Backblaze", merged[1].Name);
    }

    [Fact]
    public void A_backblaze_provider_reported_by_rclone_replaces_the_fengsync_preset()
    {
        var merged = CloudEndpointService.MergeS3Providers([new("Backblaze", "rclone's own help", ["us-west-004"], "Backblaze")]);

        var entry = Assert.Single(merged, provider => provider.Name == "Backblaze");
        Assert.Equal("Backblaze", entry.RcloneValue);
        Assert.Equal("rclone's own help", entry.Description);
        Assert.Contains("us-west-004", entry.RegionSuggestions);
    }

    [Fact]
    public void Backblaze_endpoint_is_required_and_the_rclone_provider_value_is_generic()
    {
        var providers = new[] { "AWS", "Backblaze" };

        // A blank endpoint would silently send rclone to the Amazon S3 host, so it must be rejected.
        var missingEndpoint = CloudEndpointService.ValidateS3Settings(
            new("B2", "Backblaze", "key", "secret", "us-west-004", "", "bucket", ""), providers);
        Assert.Contains("endpoint", missingEndpoint.Keys);

        var valid = new S3EndpointValues("B2", "Backblaze", "key", "secret", "us-west-004",
            "https://s3.us-west-004.backblazeb2.com", "bucket", "", "Other");
        Assert.Empty(CloudEndpointService.ValidateS3Settings(valid, providers));

        // `provider = Backblaze` would make rclone log 's3 provider "Backblaze" not known' on every use.
        var parameters = CloudEndpointService.S3Parameters(valid);
        Assert.Equal("Other", parameters["provider"]);
        Assert.Equal("https://s3.us-west-004.backblazeb2.com", parameters["endpoint"]);
        Assert.Equal("us-west-004", parameters["region"]);
    }

    [Fact]
    public void An_rclone_reported_provider_keeps_its_own_name_in_the_rclone_parameters()
    {
        var parameters = CloudEndpointService.S3Parameters(new("R2", "Cloudflare", "key", "secret", "auto", "https://example.test", "bucket", ""));
        Assert.Equal("Cloudflare", parameters["provider"]);
    }

    [Fact]
    public void An_empty_rclone_report_withholds_presets_so_the_ui_still_signals_no_providers()
    {
        // GetS3ProvidersAsync returns nothing when rclone cannot describe its s3 backend. Offering the
        // Backblaze preset there would leave it as the only choice and hide the real failure.
        Assert.Empty(CloudEndpointService.MergeS3Providers([]));
    }

    [Fact]
    public void S3_settings_reject_a_prefix_that_would_escape_its_own_boundary()
    {
        var providers = new[] { "Cloudflare" };
        string[] InvalidFields(string prefix) => [.. CloudEndpointService.ValidateS3Settings(
            new("R2", "Cloudflare", "key", "secret", "auto", "https://example.test", "bucket", prefix), providers).Keys];

        // The prefix is enforced by an rclone alias, so a ".." segment would aim the alias above the prefix
        // the user chose — and a backslash would make one key name that no rclone path can address.
        Assert.Contains("subdirectory", InvalidFields("team/../other"));
        Assert.Contains("subdirectory", InvalidFields(@"team\files"));

        // The prefix stays optional: an empty one means the whole bucket, which is a valid endpoint.
        Assert.Empty(InvalidFields("team/files"));
        Assert.Empty(InvalidFields(""));
    }

    [Fact]
    public void Update_window_exposes_release_state_and_determinate_download_progress()
    {
        RunSta(() =>
        {
            // WPF allows exactly one Application per AppDomain, and xunit decides which STA test runs
            // first, so this must adopt an existing instance instead of assuming it is the first.
            var application = Application.Current ?? new Application();
            if (application.Resources.MergedDictionaries.Count == 0)
                foreach (var resource in new[] { "DesignTokens.xaml", "Typography.xaml", "Icons.xaml", "Controls.xaml" })
                    application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/FengSync;component/Themes/{resource}")
                    });
            var release = new GitHubReleaseInfo("Stable", "v9.9.9", "Important fixes", new Uri("https://example.test/release"),
                new Uri("https://example.test/package"), new Uri("https://example.test/hash"), 2 * 1024 * 1024, null);
            var window = new UpdateWindow("1.0.0", release);

            Assert.Contains("1.0.0", window.CurrentVersion.Text, StringComparison.Ordinal);
            Assert.Contains("v9.9.9", window.LatestVersion.Text, StringComparison.Ordinal);
            Assert.Equal("Important fixes", window.ReleaseNotes.Text);
            window.SetDownloadProgress(new UpdateDownloadProgress(1024 * 1024, 2 * 1024 * 1024));
            Assert.Contains("50%", window.Progress.Text, StringComparison.Ordinal);
            Assert.Equal(Visibility.Visible, window.DownloadProgressBar.Visibility);
            Assert.Equal(50, window.DownloadProgressBar.Value);
            window.Close();
        });
    }

    [Fact]
    public void Cloud_endpoint_editor_constructs_as_a_two_step_window()
    {
        RunSta(() =>
        {
            var application = Application.Current ?? new Application();
            if (application.Resources.MergedDictionaries.Count == 0)
                foreach (var resource in new[] { "DesignTokens.xaml", "Typography.xaml", "Icons.xaml", "Controls.xaml" })
                    application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/FengSync;component/Themes/{resource}") });
            var window = new CloudEndpointEditorWindow();
            Assert.Equal(Visibility.Visible, window.NextButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.SaveButton.Visibility);
            window.Close();
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
