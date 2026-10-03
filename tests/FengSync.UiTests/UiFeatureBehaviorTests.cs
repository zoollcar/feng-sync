using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using FengSync.Core;
using FengSync.Core.Mount;
using FengSync.Core.Rclone.Configuration;
using FengSync.Core.Rclone.Lifecycle;
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
            CloudEndpointService.BuildUri(EndpointType.S3, "r2", "/feng-sync-e2e-test/path/"));
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
    public void The_s3_compatible_chinese_providers_rclone_reports_are_surfaced_not_hidden()
    {
        // Tencent COS, Alibaba OSS and the rest are not separate backends in Feng Sync: rclone's s3 backend
        // already lists them as provider values, so adding one is a row in the ordering, not a code path.
        // This asserts the ordering drops nothing and only reorders.
        var reported = new[]
        {
            new RcloneS3Provider("Wasabi", "Wasabi", []),
            new RcloneS3Provider("TencentCOS", "Tencent Cloud Object Storage (COS)", []),
            new RcloneS3Provider("Alibaba", "Alibaba Cloud Object Storage System (OSS)", []),
            new RcloneS3Provider("AWS", "Amazon Web Services (AWS) S3", ["us-east-1"])
        };

        var merged = CloudEndpointService.MergeS3Providers(reported);

        Assert.Equal(merged.Count, merged.Select(provider => provider.Name).Distinct().Count());
        Assert.All(reported, provider => Assert.Contains(merged, entry => entry.Name == provider.Name));
        // The ones a Feng Sync user is most likely to want come first — including the Backblaze preset
        // Feng Sync adds — and the rest keep alphabetical order.
        Assert.Equal(["AWS", "Backblaze", "Alibaba", "TencentCOS", "Wasabi"], merged.Select(provider => provider.Name));
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

    [Fact]
    public void Only_a_provider_that_authorizes_in_a_browser_offers_re_login()
    {
        var drive = new CloudEndpointAccount(new RcloneAccount("personal", "drive"), null);
        var oneDrive = new CloudEndpointAccount(new RcloneAccount("work", "onedrive"), null);
        var dropbox = new CloudEndpointAccount(new RcloneAccount("dropbox", "dropbox"), null);
        var box = new CloudEndpointAccount(new RcloneAccount("box", "box"), null);
        var sftp = new CloudEndpointAccount(new RcloneAccount("nas", "sftp"), null);
        var webDav = new CloudEndpointAccount(new RcloneAccount("nextcloud", "webdav"), null);
        var s3 = new CloudEndpointAccount(new RcloneAccount("archive", "s3"), null);
        var alias = new CloudEndpointAccount(new RcloneAccount("docs", "alias"), null);

        // The rule comes from the provider descriptor, so every OAuth backend offers re-login and every
        // parameter-described one does not — a hand-written list here is what would go stale.
        Assert.True(RemoteEndpointManagerWindow.NeedsReconnect(drive));
        Assert.True(RemoteEndpointManagerWindow.NeedsReconnect(oneDrive));
        Assert.True(RemoteEndpointManagerWindow.NeedsReconnect(dropbox));
        Assert.True(RemoteEndpointManagerWindow.NeedsReconnect(box));
        Assert.False(RemoteEndpointManagerWindow.NeedsReconnect(sftp));
        Assert.False(RemoteEndpointManagerWindow.NeedsReconnect(webDav));
        Assert.False(RemoteEndpointManagerWindow.NeedsReconnect(s3));
        Assert.False(RemoteEndpointManagerWindow.NeedsReconnect(alias));
        // The manager opens with nothing selected when no endpoint exists yet.
        Assert.False(RemoteEndpointManagerWindow.NeedsReconnect(null));
    }

    [Fact]
    public void The_endpoint_manager_offers_renaming_beside_the_other_endpoint_actions()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var previous = AppServices.TryCurrent;
            AppServices.Install(new HeadlessShell());
            try
            {
                var window = new RemoteEndpointManagerWindow();

                // Renaming is a distinct action from editing: it moves the endpoint's identity, so it has its
                // own AutomationId for the desktop scenario to drive, and it uses a whitelisted button style.
                Assert.Equal("RenameCloudEndpoint", AutomationProperties.GetAutomationId(window.RenameEndpointButton));
                Assert.NotNull(window.RenameEndpointButton.Style);
                Assert.Equal("重命名", window.RenameEndpointButton.Content);
                // Editing keeps its own button: the two must not be merged, because a rename migrates profiles
                // while an edit does not.
                Assert.Equal("EditCloudEndpoint", AutomationProperties.GetAutomationId(window.EditButton));
                window.Close();
            }
            finally { AppServices.Install(previous); }
        });
    }

    [Fact]
    public void The_rename_status_line_reports_how_many_sync_tasks_moved()
    {
        // Zero is reported differently on purpose: saying "0 个同步任务" reads like something failed.
        Assert.DoesNotContain("0 个", RemoteEndpointManagerWindow.DescribeRename(new("nas", "archive", 0)));
        Assert.Contains("nas", RemoteEndpointManagerWindow.DescribeRename(new("nas", "archive", 0)), StringComparison.Ordinal);
        Assert.Contains("archive", RemoteEndpointManagerWindow.DescribeRename(new("nas", "archive", 0)), StringComparison.Ordinal);
        Assert.Contains("2 个同步任务", RemoteEndpointManagerWindow.DescribeRename(new("nas", "archive", 2)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_endpoint_list_searches_and_orders_through_its_own_controls()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var previous = AppServices.TryCurrent;
            AppServices.Install(new HeadlessShell());
            try
            {
                var window = new RemoteEndpointManagerWindow();
                // Installing rows does not start rclone, and the search box is live: nothing below reaches the
                // network, which is what lets this cover the wiring rather than the pure rules again.
                window.LoadEndpointsForTest(
                [
                    EndpointRow("nas", "sftp", "SFTP", "team/docs", CloudEndpointHealth.Connected),
                    EndpointRow("archive", "s3", "Cloudflare", "team/files", CloudEndpointHealth.Unknown),
                    EndpointRow("work", "drive", "Google Drive", "team/work", CloudEndpointHealth.NeedsLogin)
                ]);

                // The controls the desktop scenario drives, each with its own reachable id.
                Assert.Equal("CloudEndpointSearch", AutomationProperties.GetAutomationId(window.EndpointSearchBox));
                Assert.Equal("CloudEndpointSort", AutomationProperties.GetAutomationId(window.EndpointSortBox));
                Assert.Equal("CloudEndpointSortDirection", AutomationProperties.GetAutomationId(window.EndpointSortDirectionButton));
                Assert.Equal("CheckAllCloudEndpoints", AutomationProperties.GetAutomationId(window.CheckAllButton));
                Assert.Equal(["状态", "名称", "类型"], window.EndpointSortBox.Items.Cast<string>());
                Assert.Equal("升序", window.EndpointSortDirectionButton.Content);
                AssertNoDirectoryListing(window);

                // The default order is by health, ascending: the broken endpoint is what the user came for.
                Assert.Equal(["work", "archive", "nas"], VisibleNames(window));

                window.EndpointSearchBox.Text = "work";
                Assert.Equal(["work"], VisibleNames(window));
                // A filter that hides rows says how many it hid, so an unexpectedly short list is explained.
                Assert.Contains("1 / 3", window.EndpointSearchBox.ToolTip?.ToString() ?? "", StringComparison.Ordinal);
                window.EndpointSearchBox.Text = "";
                Assert.Equal(["work", "archive", "nas"], VisibleNames(window));
                Assert.Null(window.EndpointSearchBox.ToolTip);

                window.EndpointSortBox.SelectedIndex = 1;
                Assert.Equal(["archive", "nas", "work"], VisibleNames(window));
                Click(window.EndpointSortDirectionButton);
                Assert.Equal("降序", window.EndpointSortDirectionButton.Content);
                Assert.Equal(["work", "nas", "archive"], VisibleNames(window));
                // Re-ordering and filtering never move the browser off the endpoint it is showing: doing so
                // would be one round trip to the cloud service per keystroke.
                Assert.Equal("work", SelectedName(window));
                AssertNoDirectoryListing(window);

                // A filter that matches nothing empties the list, the file pane, and the check-everything action
                // together, and says so instead of leaving a stale pane beside an empty list.
                window.EndpointSearchBox.Text = "nothing-matches";
                Assert.Empty(window.VisibleEndpoints);
                Assert.Equal("", window.CurrentPath);
                Assert.False(window.CheckAllButton.IsEnabled);
                Click(window.CheckAllButton);
                Assert.Equal("没有可检查的端点。", window.StatusText.Text);
                window.Close();
            }
            finally { AppServices.Install(previous); }
        });
    }

    [Fact]
    public void The_endpoint_list_files_rows_under_group_headings()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var previous = AppServices.TryCurrent;
            AppServices.Install(new HeadlessShell());
            try
            {
                var window = new RemoteEndpointManagerWindow();
                window.LoadEndpointsForTest(
                [
                    EndpointRow("nas", "sftp", "SFTP", "team/docs", CloudEndpointHealth.Connected, "beta"),
                    EndpointRow("archive", "s3", "Cloudflare", "team/files", CloudEndpointHealth.Unknown),
                    EndpointRow("work", "drive", "Google Drive", "team/work", CloudEndpointHealth.NeedsLogin, "beta"),
                    EndpointRow("home", "sftp", "SFTP", "", CloudEndpointHealth.Connected, "alpha")
                ]);

                var view = CollectionViewSource.GetDefaultView(window.EndpointList.ItemsSource);
                // The list is deliberately not grouped through the view: the heading is part of the row, so
                // the selector's items stay the rows. Grouping would turn them into the group objects and
                // break the selection the whole file browser hangs off.
                Assert.Empty(view.GroupDescriptions);

                // Rows arrive group-first, health-ascending inside each group, ungrouped last, and only the
                // first row of each group carries the heading.
                Assert.Equal(["home", "work", "nas", "archive"], VisibleNames(window));
                Assert.Equal(["alpha", "beta", "", "未分组"], window.VisibleEndpoints.Select(row => row.GroupHeading));

                // The list always has a selection, so the file pane is never empty without a visible cause.
                Assert.Equal("home", SelectedName(window));
                Assert.Equal("OrganizeCloudEndpoint", AutomationProperties.GetAutomationId(window.OrganizeEndpointButton));
                Assert.True(window.OrganizeEndpointButton.IsEnabled);
                // Installing the rows is not a gesture, so nothing has asked the remote service for anything.
                AssertNoDirectoryListing(window);

                // Filtering selects a real endpoint rather than a heading, and clearing the filter leaves the
                // selection where it was: a keystroke must never move the browser off the endpoint it shows.
                window.EndpointSearchBox.Text = "work";
                Assert.Equal("work", SelectedName(window));
                window.EndpointSearchBox.Text = "";
                Assert.Equal("work", SelectedName(window));

                // A filter that hides the selected endpoint falls back to the first survivor rather than
                // clearing the list.
                window.EndpointSearchBox.Text = "archive";
                Assert.Equal("archive", SelectedName(window));
                window.EndpointSearchBox.Text = "";
                window.Close();
            }
            finally { AppServices.Install(previous); }
        });
    }

    [Fact]
    public void The_endpoint_list_keeps_the_grouped_order_when_the_sort_is_reversed()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var previous = AppServices.TryCurrent;
            AppServices.Install(new HeadlessShell());
            try
            {
                var window = new RemoteEndpointManagerWindow();
                window.LoadEndpointsForTest(
                [
                    EndpointRow("nas", "sftp", "SFTP", "team/docs", CloudEndpointHealth.Connected, "beta"),
                    EndpointRow("archive", "s3", "Cloudflare", "team/files", CloudEndpointHealth.Unknown, "alpha"),
                    EndpointRow("loose", "sftp", "SFTP", "", CloudEndpointHealth.Connected)
                ]);

                // Reversing the sort reverses the rows inside each heading, not the headings themselves:
                // reversing the group order too would put 未分组 at the top and read as a different list.
                window.EndpointSortBox.SelectedIndex = 1;
                Click(window.EndpointSortDirectionButton);
                Assert.Equal("降序", window.EndpointSortDirectionButton.Content);

                Assert.Equal(["archive", "nas", "loose"], VisibleNames(window));
                Assert.Equal(["alpha", "beta", "未分组"], window.VisibleEndpoints.Select(row => row.GroupHeading));
                AssertNoDirectoryListing(window);
                window.Close();
            }
            finally { AppServices.Install(previous); }
        });
    }

    [Fact]
    public void An_edit_form_keeps_the_stored_sftp_password_unless_one_is_typed()
    {
        const string stored = "obscured-value-read-from-rclone";

        // A blank box re-sends the obscured value and says so, because obscuring an obscured password
        // corrupts it. A filled box is a new plaintext password and must be obscured by the write.
        Assert.Equal((stored, true), CloudEndpointService.ResolveStoredPassword("", stored));
        Assert.Equal(("hunter2", false), CloudEndpointService.ResolveStoredPassword("hunter2", stored));
        // Key-file authentication with no stored password stays empty rather than writing a blank secret.
        Assert.Equal(("", false), CloudEndpointService.ResolveStoredPassword("", ""));
        Assert.Equal(("", false), CloudEndpointService.ResolveStoredPassword("", null));
    }

    [Fact]
    public void The_endpoint_editor_opens_an_existing_endpoint_on_its_connection_step()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var account = new CloudEndpointAccount(
                new RcloneAccount("nas", "sftp"),
                new CloudEndpointMetadata("nas", "sftp", "SFTP", "share", "team", null));
            var target = new CloudEndpointEditTarget(account, EndpointType.Sftp,
                new Dictionary<string, string>
                {
                    ["host"] = "nas.local",
                    ["port"] = "2222",
                    ["user"] = "bob",
                    ["pass"] = "obscured-value",
                    ["key_file"] = "/keys/id_rsa"
                });

            var window = new CloudEndpointEditorWindow(target);

            // Editing starts on the connection step with Save available. The service picker is out of
            // reach because an endpoint's backend is part of its identity, and its name is fixed because
            // renaming would strand every profile that references it.
            Assert.Equal("编辑云端端点", window.Title);
            Assert.Equal("编辑云端端点", window.HeadingText.Text);
            Assert.Equal(Visibility.Collapsed, window.NextButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.BackButton.Visibility);
            Assert.Equal(Visibility.Visible, window.SaveButton.Visibility);
            Assert.Equal(Visibility.Visible, window.ConnectionStep.Visibility);
            Assert.Equal(Visibility.Collapsed, window.ChooseServiceStep.Visibility);
            Assert.False(window.ServiceBox.IsEnabled);
            Assert.Equal(EndpointType.Sftp, window.SelectedKindForTest);
            Assert.True(window.NameInput!.IsReadOnly);

            Assert.Equal("nas", window.NameInput.Text);
            Assert.Equal("nas.local", ((TextBox)window.FieldInput("host")!).Text);
            Assert.Equal("2222", ((TextBox)window.FieldInput("port")!).Text);
            Assert.Equal("bob", ((TextBox)window.FieldInput("user")!).Text);
            Assert.Equal("/keys/id_rsa", ((TextBox)window.FieldInput("key_file")!).Text);
            Assert.Equal("share/team", window.RootInput!.Text);
            // rclone stores the password obscured, so the box stays empty and the label says what that means.
            Assert.Equal("", ((PasswordBox)window.FieldInput("pass")!).Password);
            Assert.Contains("保持原密码", window.FieldLabel("pass"), StringComparison.Ordinal);
            window.Close();
        });
    }

    [Fact]
    public void The_endpoint_editor_generates_a_form_for_a_provider_with_no_hand_written_panel()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var account = new CloudEndpointAccount(
                new RcloneAccount("nextcloud", "webdav"),
                new CloudEndpointMetadata("nextcloud", "webdav", "WebDAV", "remote.php/dav/files/bob", "", null));
            var target = new CloudEndpointEditTarget(account, EndpointType.WebDav,
                new Dictionary<string, string>
                {
                    ["url"] = "https://cloud.example.com/remote.php/dav/files/bob",
                    ["vendor"] = "nextcloud",
                    ["user"] = "bob",
                    ["pass"] = "obscured-value"
                });

            var window = new CloudEndpointEditorWindow(target);

            // WebDAV has no panel in the XAML: the whole form comes from its descriptor, which is what makes
            // adding a provider a data change instead of a markup change.
            Assert.Equal(EndpointType.WebDav, window.SelectedKindForTest);
            Assert.Equal(Visibility.Visible, window.ProviderFieldsPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, window.S3Panel.Visibility);
            Assert.Equal("nextcloud", window.NameInput!.Text);
            Assert.Equal("https://cloud.example.com/remote.php/dav/files/bob", ((TextBox)window.FieldInput("url")!).Text);
            Assert.Equal("nextcloud", ((TextBox)window.FieldInput("vendor")!).Text);
            Assert.Equal("bob", ((TextBox)window.FieldInput("user")!).Text);
            Assert.Equal("remote.php/dav/files/bob", window.RootInput!.Text);
            // The descriptor's AutomationIds are what the desktop scenario drives the form by.
            Assert.Equal("WebDavUrl", AutomationProperties.GetAutomationId(window.FieldInput("url")));
            Assert.Equal("WebDavRemoteName", AutomationProperties.GetAutomationId(window.NameInput));
            Assert.Equal("WebDavRemoteRoot", AutomationProperties.GetAutomationId(window.RootInput));
            // A password box starts empty on an edit form; see the SFTP case for why.
            Assert.Equal("", ((PasswordBox)window.FieldInput("pass")!).Password);
            window.Close();
        });
    }

    [Fact]
    public void The_endpoint_editor_pre_fills_an_s3_endpoint_from_its_saved_parameters()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var account = new CloudEndpointAccount(
                new RcloneAccount("docs", "alias"),
                new CloudEndpointMetadata("docs", "alias", "Backblaze", "bucket", "team/files", "docs_s3"));
            var target = new CloudEndpointEditTarget(account, EndpointType.S3,
                new Dictionary<string, string>
                {
                    ["provider"] = "Other",
                    ["access_key_id"] = "key-id",
                    ["secret_access_key"] = "secret-value",
                    ["region"] = "us-west-004",
                    ["endpoint"] = "https://s3.us-west-004.backblazeb2.com"
                });

            var window = new CloudEndpointEditorWindow(target);

            // The displayed provider comes from Feng Sync's metadata, because rclone only knows Backblaze
            // as its generic "Other". The saved region must survive as-is: replacing it with a provider
            // default would silently retarget the endpoint at a different region.
            Assert.Equal("Backblaze", window.S3ProviderBox.Text);
            Assert.Equal("key-id", window.S3AccessKeyBox.Text);
            Assert.Equal("secret-value", window.S3SecretBox.Password);
            Assert.Equal("us-west-004", window.S3RegionBox.Text);
            Assert.Equal("https://s3.us-west-004.backblazeb2.com", window.S3EndpointBox.Text);
            // The bucket and prefix are not in the remote at all: an alias target is opaque to rclone, so
            // they come from metadata.
            Assert.Equal("bucket", window.S3BucketBox.Text);
            Assert.Equal("team/files", window.S3SubdirectoryBox.Text);
            Assert.True(window.S3NameBox.IsReadOnly);

            // Setting the provider text is handled through the dispatcher, and that handler also refreshes
            // the region suggestions. Pumping the queue proves the saved region survives it.
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.Equal("us-west-004", window.S3RegionBox.Text);
            window.Close();
        });
    }

    /// <summary>One row of the endpoint list, in the state the list's ordering rules read.</summary>
    private static CloudEndpointRow EndpointRow(string name, string type, string provider, string prefix,
        CloudEndpointHealth health, string? group = null, IReadOnlyList<string>? tags = null)
    {
        var parts = prefix.Split('/', 2);
        var metadata = new CloudEndpointMetadata(name, type, provider, parts[0], parts.Length > 1 ? parts[1] : "",
            null, group, tags);
        var row = new CloudEndpointRow(new CloudEndpointAccount(new RcloneAccount(name, type), metadata));
        row.Status = new CloudEndpointStatus(health, "");
        return row;
    }

    [Fact]
    public void An_import_draft_prefills_the_service_the_name_and_the_root()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var draft = new CloudEndpointDraft(EndpointType.Sftp, "nas", "SFTP", "", "team/docs",
                "工作", ["客户"], ["host", "user"]);

            var window = new CloudEndpointEditorWindow(draft);

            // The non-secret values are on screen and the name stays editable — an import may have renamed the
            // endpoint to avoid a collision — while the form is already past the service step.
            Assert.Equal("nas", window.NameInput?.Text);
            Assert.Equal("team/docs", window.RootInput?.Text);
            Assert.False(window.NameInput?.IsReadOnly ?? true);
            Assert.Equal(Visibility.Visible, window.SaveButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.NextButton.Visibility);
            window.Close();
        });
    }

    [Fact]
    public void An_import_draft_for_s3_prefills_the_bucket_and_prefix()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var draft = new CloudEndpointDraft(EndpointType.S3, "docs", "Cloudflare", "bucket", "prefix",
                null, null, ["provider"]);

            var window = new CloudEndpointEditorWindow(draft);

            Assert.Equal("docs", window.S3NameBox.Text);
            Assert.Equal("Cloudflare", window.S3ProviderBox.Text);
            Assert.Equal("bucket", window.S3BucketBox.Text);
            Assert.Equal("prefix", window.S3SubdirectoryBox.Text);
            window.Close();
        });
    }

    /// <summary>The endpoint names the list is showing, in the order it is showing them.</summary>
    private static IReadOnlyList<string> VisibleNames(RemoteEndpointManagerWindow window) =>
        [.. window.VisibleEndpoints.Select(row => row.Account.Name)];

    private static string? SelectedName(RemoteEndpointManagerWindow window) =>
        (window.EndpointList.SelectedItem as CloudEndpointRow)?.Account.Name;

    /// <summary>Raises a button's click, which is how the desktop harness drives it too.</summary>
    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    /// <summary>
    /// The status line is the first thing a directory listing writes to, so an untouched one is the proof that
    /// re-arranging the list did not start one — which is the rule under test, and the difference between a
    /// keystroke and a network round trip to the cloud service.
    /// </summary>
    private static void AssertNoDirectoryListing(RemoteEndpointManagerWindow window) =>
        Assert.Equal("", window.StatusText.Text);

    /// <summary>
    /// Just enough of a shell to construct a window that asks for application services. The rclone side
    /// throws on use rather than starting a daemon, so a test that accidentally reaches the network fails
    /// loudly instead of quietly opening a process.
    /// </summary>
    private sealed class HeadlessShell : IAppServiceHost
    {
        private readonly IRcloneLifecycleClient _rclone = new UnreachableRclone();

        public HeadlessShell() => MountService = new RcloneMountService(_rclone);

        public RcloneLifecycleHost RcloneHost { get; } = new();
        public IRcloneLifecycleClient RcloneLifecycle => _rclone;
        public RcloneMountService MountService { get; }
        public CloudTransferQueue CloudTransfers { get; } = new();

        private sealed class UnreachableRclone : IRcloneLifecycleClient
        {
            public Task<JsonElement> CallAsync(string operation, object payload, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException($"这个测试不调用 rclone（{operation}）。");
        }
    }

    private static void EnsureApplicationResources()
    {
        var application = Application.Current ?? new Application();
        if (application.Resources.MergedDictionaries.Count > 0) return;
        foreach (var resource in new[] { "DesignTokens.xaml", "Typography.xaml", "Icons.xaml", "Controls.xaml" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/FengSync;component/Themes/{resource}")
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
