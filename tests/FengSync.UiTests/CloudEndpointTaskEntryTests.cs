using System.IO;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FengSync.Core;
using FengSync.Core.Mount;
using FengSync.Core.Rclone.Lifecycle;
using FengSync.Core.Configuration;
using FengSync.Services;
using FengSync.Views;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// 云盘管理里 E 组四个入口的真实路径：从一个端点建同步任务（E1）、双向同步向导（E2）、导出诊断（E3）、
/// 首启引导（E5）。规则本身在 <see cref="CloudSyncTaskTests"/> / <see cref="CloudDiagnosticsReportTests"/> /
/// <see cref="CloudFirstRunGuideTests"/> 里；这里要证明的是**点击真的走到了那里** —— 草稿那一侧填对了、
/// 存盘是读—加—写而不是覆盖、取消不写、诊断文件真的落盘而且里面没有凭据。
/// </summary>
/// <remarks>
/// 三个对话框（编辑同步任务、双向同步向导、诊断存到哪）各自只接管**问**的那一下，写盘与建任务仍然走
/// 真的代码路径。用 <c>??</c> 兜回真弹窗会在测试里挂一个无人可答的模态框，所以接缝答 <c>null</c> 就是取消。
/// </remarks>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudEndpointTaskEntryTests
{
    /// <summary>The endpoint the window has selected, and the folder the browser is sitting in.</summary>
    private const string Endpoint = "work";
    private const string Folder = "team/docs";

    // ------------------------------------------------------------------ E1

    [Fact]
    public async Task Creating_a_task_from_an_endpoint_prefills_the_cloud_side_with_the_folder_being_browsed()
    {
        await WithWindow(async (window, root) =>
        {
            SyncProfile? draft = null;
            window.EditSyncProfile = profile => { draft = profile; return profile; };
            window.ShowListing(Folder, []);

            await Click(window.CreateSyncTaskButton);

            Assert.NotNull(draft);
            // 云端那侧是浏览到的目录，本地那侧留空等用户自己点「浏览…」。
            Assert.Equal("gdrive://work/team/docs", draft!.RightPath);
            Assert.Equal("", draft.LeftPath);
            Assert.Equal(SyncMode.TwoWay, draft.Mode);
            Assert.Contains(Folder, draft.Description, StringComparison.Ordinal);

            // 真的存下来了，而且名字是默认名。
            var saved = await Saved(root, expectedCount: 1);
            var mine = Assert.Single(saved, profile => profile.Name == draft.Name);
            Assert.Equal("gdrive://work/team/docs", mine.RightPath);
        });
    }

    [Fact]
    public async Task Creating_a_task_keeps_the_ones_that_were_already_there()
    {
        // SaveAsync 是整份覆盖，所以「加一个」必须是读—加—写三步。少读一步就是把别人的任务弄没了。
        await WithWindow(async (window, root) =>
        {
            var store = new ProfileStore(ProfilesPath(root));
            var existing = SyncProfile.Create("早已存在的任务", @"C:\a", "gdrive://nas/a");
            await store.SaveAsync([existing]);
            window.EditSyncProfile = profile => profile;

            await Click(window.CreateSyncTaskButton);

            var saved = await Saved(root, expectedCount: 2);
            Assert.Equal(2, saved.Count);
            Assert.Contains(saved, profile => profile.Id == existing.Id);
        });
    }

    [Fact]
    public async Task Cancelling_the_editor_saves_nothing()
    {
        await WithWindow(async (window, root) =>
        {
            window.EditSyncProfile = _ => null;

            await Click(window.CreateSyncTaskButton);

            Assert.Empty(await Saved(root, expectedCount: 0));
            Assert.False(string.IsNullOrWhiteSpace(window.StatusText.Text));
        });
    }

    [Fact]
    public async Task The_create_button_is_disabled_when_no_endpoint_is_selected()
    {
        // 没有选中行的时候按钮按不动 —— 没有端点就问不出「云端那一侧填什么」。
        await WithWindow(async (window, _) =>
        {
            window.LoadEndpointsForTest([]);
            Assert.False(window.CreateSyncTaskButton.IsEnabled);
        });
    }

    // ------------------------------------------------------------------ E2

    [Fact]
    public async Task The_wizard_saves_a_bidirectional_task_as_soon_as_it_is_answered()
    {
        // 「一键」的含义就是不再开 Profile 编辑器：两格都已经在向导里挑过了。
        await WithWindow(async (window, root) =>
        {
            window.ShowSyncWizard = () => new CloudSyncTask.Plan("reports ↔ work", @"C:\reports", "gdrive://work/reports", "");

            await Click(window.SyncWizardButton);

            var mine = Assert.Single(await Saved(root, expectedCount: 1));
            Assert.Equal("reports ↔ work", mine.Name);
            Assert.Equal(@"C:\reports", mine.LeftPath);
            Assert.Equal("gdrive://work/reports", mine.RightPath);
            Assert.Equal(SyncMode.TwoWay, mine.Mode);
        });
    }

    [Fact]
    public async Task A_wizard_answer_with_two_remote_sides_is_refused_before_anything_is_written()
    {
        // 接缝可以被测试装、也可以被以后某个调用方装，所以校验在存盘之前还要再做一次 ——
        // profiles.json 是这个程序里写坏了最贵的一个文件。
        await WithWindow(async (window, root) =>
        {
            window.ShowSyncWizard = () => new CloudSyncTask.Plan("两端都是云端", "gdrive://work", "s3://photos", "");

            await Click(window.SyncWizardButton);

            Assert.Empty(await Saved(root, expectedCount: 0));
            Assert.False(string.IsNullOrWhiteSpace(window.StatusText.Text));
        });
    }

    [Fact]
    public async Task Cancelling_the_wizard_saves_nothing()
    {
        await WithWindow(async (window, root) =>
        {
            window.ShowSyncWizard = () => null;

            await Click(window.SyncWizardButton);

            Assert.Empty(await Saved(root, expectedCount: 0));
        });
    }

    // ------------------------------------------------------------------ E3

    [Fact]
    public async Task The_diagnostics_export_lands_on_disk_and_carries_no_credential()
    {
        await WithWindow(async (window, root) =>
        {
            var path = Path.Combine(root, "diagnostics.txt");
            window.AskDiagnosticsFile = _ => path;

            await Click(window.ExportDiagnosticsButton);

            Assert.True(File.Exists(path), "the diagnostics report was never written");
            var report = await File.ReadAllTextAsync(path);
            Assert.Contains("Feng Sync", report, StringComparison.Ordinal);
            // 读报告的人必须被明确告知这份文件里没有什么，否则他无法判断能不能发出去。
            Assert.Contains("rclone.conf", report, StringComparison.Ordinal);
            // 端点只有名字与类型，参数值一个都不在。
            Assert.Contains(Endpoint, report, StringComparison.Ordinal);
            Assert.DoesNotContain("s3cret", report, StringComparison.Ordinal);
            // 写成没有 BOM 的 UTF-8：记事本会照着 BOM 猜编码，猜错时这份文件在别人机器上是乱码。
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        });
    }

    [Fact]
    public async Task The_diagnostics_export_offers_a_file_name_windows_accepts()
    {
        await WithWindow(async (window, root) =>
        {
            string? offered = null;
            window.AskDiagnosticsFile = name => { offered = name; return null; };

            await Click(window.ExportDiagnosticsButton);

            Assert.NotNull(offered);
            Assert.DoesNotContain(offered!, Path.GetInvalidFileNameChars());
        });
    }

    [Fact]
    public async Task Cancelling_the_diagnostics_export_writes_no_file()
    {
        await WithWindow(async (window, root) =>
        {
            window.AskDiagnosticsFile = _ => null;

            await Click(window.ExportDiagnosticsButton);

            Assert.Empty(Directory.GetFiles(root, "*.txt"));
        });
    }

    // ------------------------------------------------------------------ E5

    [Fact]
    public async Task The_first_run_guide_asks_once_and_later_means_again_next_time()
    {
        await WithWindow(async (window, root) =>
        {
            var asked = 0;
            window.ShowFirstRunGuide = () => { asked++; return CloudFirstRunGuide.Choice.Later; };

            await window.MaybeShowFirstRunGuideAsync();
            await window.MaybeShowFirstRunGuideAsync();

            Assert.Equal(2, asked);
            Assert.Equal(CloudFirstRunGuide.Choice.Later, window.GuideAnswer);
            // 稍后再说什么都不写：下次打开还会再问一次。
            Assert.False((await new SettingsStore(SettingsPath(root)).LoadAsync()).Settings.CloudGuideSeen);
        }, loadEndpoints: false);
    }

    [Fact]
    public async Task Never_again_is_written_down_so_it_never_asks_again()
    {
        await WithWindow(async (window, root) =>
        {
            window.ShowFirstRunGuide = () => CloudFirstRunGuide.Choice.Never;

            await window.MaybeShowFirstRunGuideAsync();

            Assert.Equal(CloudFirstRunGuide.Choice.Never, window.GuideAnswer);
            Assert.True((await new SettingsStore(SettingsPath(root)).LoadAsync()).Settings.CloudGuideSeen);
        }, loadEndpoints: false);
    }

    [Fact]
    public async Task Somebody_who_already_has_endpoints_is_never_asked()
    {
        // 引导讲的是「先连一个 Google Drive」。已经建了端点的人看到这句话只会觉得吵 ——
        // 而这份安静靠的是端点**数量**，不是「他大概不需要」的猜测。
        await WithWindow(async (window, _) =>
        {
            var asked = false;
            window.ShowFirstRunGuide = () => { asked = true; return CloudFirstRunGuide.Choice.Later; };
            window.LoadEndpointsForTest(Rows);

            await window.MaybeShowFirstRunGuideAsync();

            Assert.False(asked);
            Assert.Null(window.GuideAnswer);
        });
    }

    // ------------------------------------------------------------------ harness

    private static readonly CloudEndpointRow[] Rows =
    [
        new(new CloudEndpointAccount(new RcloneAccount(Endpoint, "drive"),
            new CloudEndpointMetadata(Endpoint, "drive", "Google Drive", "", ""))),
    ];

    /// <summary>
    /// One click on a real window. Returns immediately after the click event has been dispatched; the
    /// handler is <c>async void</c>, so tests poll for the outcome they care about (file written, status
    /// text changed, button re-enabled). Polling for the outcome rather than the click's own life cycle
    /// keeps the test from depending on a button whose content is a binding that may not have evaluated
    /// yet — the binding can be empty at idle and a non-empty busy text, and tracking that is fragile.
    /// </summary>
    private static Task Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return Task.CompletedTask;
    }

    /// <summary>Waits for <paramref name="condition"/>, pumping the dispatcher so continuations can run.</summary>
    private static async Task WaitUntil(Func<bool> condition, string what = "the condition")
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException($"等待「{what}」超时。");
    }

    /// <summary>The profiles as they are on disk, once the write has landed. Pass the number to wait for so a
    /// pre-existing file does not return early and mask a missing save.</summary>
    private static async Task<IReadOnlyList<SyncProfile>> Saved(string root, int expectedCount)
    {
        var path = ProfilesPath(root);
        string? text = null;
        await WaitUntil(() =>
        {
            text = Read(path);
            if (text is null) return expectedCount == 0; // a missing file satisfies an "empty" expectation
            var parsed = JsonSerializer.Deserialize<List<SyncProfile>>(text);
            return parsed is not null && parsed.Count >= expectedCount;
        }, "the profile store to be written");
        return text is null ? [] : JsonSerializer.Deserialize<List<SyncProfile>>(text) ?? [];
    }

    private static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
    }

    private static string ProfilesPath(string root) => Path.Combine(root, "profiles.json");
    private static string SettingsPath(string root) => Path.Combine(root, "FengSync.local.json");

    /// <summary>
    /// Runs one action against the real window over a stand-in rclone, in an isolated data root so a test
    /// never writes into the developer's own app data and never reads back what a previous run left there.
    /// The window is never shown, so its <c>Loaded</c> handler — which would read the application's own
    /// settings — is not reached.
    /// </summary>
    /// <param name="loadEndpoints">Whether to pre-populate the endpoint list. Tests that need an empty
    /// state (the first-run guide path is one) pass <see langword="false"/>; everything else gets the
    /// default rows.</param>
    private static Task WithWindow(Func<RemoteEndpointManagerWindow, string, Task> test, bool loadEndpoints = true) => RunSta(async () =>
    {
        var root = NewDirectory();
        var previousShell = AppServices.TryCurrent;
        var previousDataDir = Environment.GetEnvironmentVariable("FENGSYNC_DATA_DIR");
        Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", root);
        var http = new HttpClient(new SilentRclone()) { BaseAddress = new Uri("http://rc.test/") };
        var client = new RcloneRcClient(http, new Uri("http://rc.test/"), "user", "pass");
        var queue = new CloudTransferQueue(new CloudTransferJournalStore(root), _ => Task.FromResult(client));
        // The window resolves the mount service from the installed shell, and the rclone behind that shell is
        // unreachable on purpose: a test that reaches it fails loudly instead of starting a daemon.
        AppServices.Install(new StubShell(queue));
        RemoteEndpointManagerWindow? window = null;
        try
        {
            window = new RemoteEndpointManagerWindow(new CloudFileManagerService(_ => Task.FromResult(client)), queue);
            if (loadEndpoints) window.LoadEndpointsForTest(Rows, selectName: Endpoint);
            await test(window, root);
        }
        finally
        {
            AppServices.Install(previousShell);
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", previousDataDir);
            try { window?.Close(); }
            catch (InvalidOperationException) { }
            TryDelete(root);
        }
    });

    /// <summary>Just enough of a shell to build the window; nothing here owns a daemon or a journal.</summary>
    private sealed class StubShell : IAppServiceHost
    {
        private readonly RcloneLifecycleHost _rclone = new();

        public StubShell(CloudTransferQueue transfers)
        {
            CloudTransfers = transfers;
            MountService = new RcloneMountService(new UnreachableRclone());
        }

        public RcloneLifecycleHost RcloneHost => _rclone;
        public IRcloneLifecycleClient RcloneLifecycle => _rclone;
        public RcloneMountService MountService { get; }
        public CloudTransferQueue CloudTransfers { get; }

        private sealed class UnreachableRclone : IRcloneLifecycleClient
        {
            public Task<JsonElement> CallAsync(string operation, object payload, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException($"这个测试不调用 rclone（{operation}）。");
        }
    }

    /// <summary>
    /// A stand-in for rclone's RC endpoint. It answers nothing, because none of these four entries needs a
    /// cloud answer: they read the endpoint list, the profile store and the log file, and the diagnostics
    /// gather wraps every remote probe in its own fallback.
    /// </summary>
    private sealed class SilentRclone : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") });
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fengsync-task-entry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Windows can hold a directory a few milliseconds after its last handle closes.</summary>
    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Runs one action against a window on its own STA thread, with the dispatcher pumped while the action is
    /// in flight — the handlers post continuations to that dispatcher, and a thread parked in <c>Join</c>
    /// would never run them. The dispatcher is then shut down explicitly, because the process allows only one
    /// WPF <c>Application</c>. A run that stops making progress is cut off by a watchdog.
    /// </summary>
    private static Task RunSta(Func<Task> action)
    {
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            Exception? failure = null;
            Task? run = null;
            var ownsApplication = MergeApplicationThemes();
            try
            {
                run = action();
                _ = run.ContinueWith(completed =>
                {
                    failure = completed.IsFaulted ? completed.Exception!.GetBaseException() : null;
                    frame.Continue = false;
                }, CancellationToken.None, TaskContinuationOptions.None,
                    TaskScheduler.FromCurrentSynchronizationContext());
            }
            catch (Exception exception)
            {
                failure = exception;
                frame.Continue = false;
            }
            var watchdog = new System.Threading.Timer(
                _ => dispatcher.BeginInvoke(() => frame.Continue = false),
                null, TimeSpan.FromSeconds(90), System.Threading.Timeout.InfiniteTimeSpan);
            Dispatcher.PushFrame(frame);
            watchdog.Dispose();
            if (failure is null && run is { IsCompleted: false })
                failure = new TimeoutException("云盘端点入口测试没有在规定时间内完成（窗口线程被卡住了）。");
            if (ownsApplication)
                try { Application.Current?.Shutdown(); }
                catch (InvalidOperationException) { }
            dispatcher.InvokeShutdown();
            finished.TrySetResult(failure);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return Task.Run(async () =>
        {
            var exception = await finished.Task;
            thread.Join(TimeSpan.FromSeconds(30));
            if (exception is not null) ExceptionDispatchInfo.Capture(exception).Throw();
        });
    }

    private static bool MergeApplicationThemes()
    {
        var ownsApplication = Application.Current is null;
        if (ownsApplication) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var application = Application.Current!;
        if (application.Resources.MergedDictionaries.Count == 0)
            foreach (var resource in new[] { "DesignTokens.xaml", "Typography.xaml", "Icons.xaml", "Controls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/FengSync;component/Themes/{resource}")
                });
        return ownsApplication;
    }
}
