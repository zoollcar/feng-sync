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
/// 云盘管理里 E 组还留着入口的两项的真实路径：导出诊断（E3）与首启引导（E5）。规则本身在
/// <see cref="CloudDiagnosticsReportTests"/> / <see cref="CloudFirstRunGuideTests"/> 里；这里要证明的是
/// **点击真的走到了那里** —— 诊断文件真的落盘而且里面没有凭据、引导只问一次、「不再问」真的被记住。
/// </summary>
/// <remarks>
/// <para>E1（从端点建同步任务）与 E2（双向同步向导）的入口随左侧「同步」一行一起下线：主窗口已经有「新建同步」，
/// 云盘管理不再重复提供。它们的规则层（<c>CloudSyncTask</c> / <c>CloudSyncWizardWindow</c>）仍然保留，但这扇窗里
/// 已经没有控件能点到，所以这里不再有它们的用例。</para>
/// <para>两个对话框（诊断存到哪、要不要看引导）各自只接管**问**的那一下，写盘仍然走真的代码路径。用
/// <c>??</c> 兜回真弹窗会在测试里挂一个无人可答的模态框，所以接缝答 <c>null</c> 就是取消。</para>
/// </remarks>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudEndpointTaskEntryTests
{
    /// <summary>The endpoint the window has selected.</summary>
    private const string Endpoint = "work";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Browsing_without_a_synchronization_context_updates_the_window_on_its_dispatcher(bool succeeds)
    {
        await WithWindow(async (window, root) =>
        {
            var context = SynchronizationContext.Current;
            Task browse;
            try
            {
                // An STA caller can own the controls without installing a WPF context. Force the HTTP
                // answer to arrive later, so the continuation really does start on a pool thread.
                SynchronizationContext.SetSynchronizationContext(null);
                browse = window.NavigateToAsync("docs");
            }
            finally { SynchronizationContext.SetSynchronizationContext(context); }

            await browse;
            Assert.True(window.Dispatcher.CheckAccess());
            if (succeeds)
            {
                Assert.Equal("report.txt", Assert.Single(window.VisibleEntries).Name);
                Assert.Contains("docs", await File.ReadAllTextAsync(Path.Combine(root, "cloud-bookmarks.json")));
            }
            else
            {
                Assert.Empty(window.VisibleEntries);
                Assert.Contains("无法读取目录", window.StatusText.Text);
            }
        }, handler: new DelayedListing(succeeds));
    }

    private sealed class DelayedListing(bool succeeds) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(succeeds ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(succeeds
                    ? "{\"list\":[{\"Name\":\"report.txt\",\"Path\":\"report.txt\",\"Size\":12,\"IsDir\":false}]}"
                    : "{}")
            };
        }
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
    private static Task WithWindow(Func<RemoteEndpointManagerWindow, string, Task> test, bool loadEndpoints = true,
        HttpMessageHandler? handler = null) => RunSta(async () =>
    {
        var root = NewDirectory();
        var previousShell = AppServices.TryCurrent;
        var previousDataDir = Environment.GetEnvironmentVariable("FENGSYNC_DATA_DIR");
        Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", root);
        using var http = new HttpClient(handler ?? new SilentRclone()) { BaseAddress = new Uri("http://rc.test/") };
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
