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
        Assert.Equal(50, new CloudTransferProgress("proof.pdf", 5, 10, 0).Percentage);
        Assert.Equal(0, new CloudTransferProgress("proof.pdf", 5, 0, 0).Percentage);
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
    public void Update_window_exposes_release_state_and_determinate_download_progress()
    {
        RunSta(() =>
        {
            var application = new Application();
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
