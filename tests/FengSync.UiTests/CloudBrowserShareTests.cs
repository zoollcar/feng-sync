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
using FengSync.Services;
using FengSync.Views;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// The file browser's 复制分享链接. What a link means and what each answer is called is pinned in
/// <c>CloudFeatureBehaviorTests</c>; what belongs here is the path a click actually takes over a real window:
/// which selection earns the command, that nothing is asked of the cloud before the user agrees, the exact
/// payload the request carries, which string ends up in the clipboard, and what is said when the backend
/// refuses, has dropped the object, or answers with nothing. The clipboard itself stays behind a seam — WPF's
/// clipboard is process-wide state, and a desktop-free suite must not fight the user's own copy buffer for it.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudBrowserShareTests
{
    /// <summary>The root folder of the endpoint the window is showing.</summary>
    private const string Root = "team";

    private const string Link = "https://cloud.example.test/team/a.txt?token=signed-value";

    [Fact]
    public async Task A_single_file_earns_the_share_command_and_a_folder_does_not()
    {
        // A prefix has no link to give — the bundled v1.75.0 answers 404 for one against a real S3 server — and
        // a link over a folder would grant a subtree the selection never named.
        await WithWindow((window, _) =>
        {
            // A row has to be selected for there to be a menu at all, so the case starts where the click does.
            Select(window, $"{Root}/a.txt");
            Assert.Contains("复制分享链接…", Headers(window.BuildRowMenu()!));
            Select(window, $"{Root}/incoming");
            Assert.DoesNotContain("复制分享链接…", Headers(window.BuildRowMenu()!));
            Select(window, $"{Root}/a.txt", $"{Root}/b.txt");
            Assert.DoesNotContain("复制分享链接…", Headers(window.BuildRowMenu()!));
        });
    }

    [Fact]
    public async Task The_link_is_asked_for_on_the_object_the_row_points_at()
    {
        await WithWindow(async (window, standIn) =>
        {
            window.ConfirmShare = _ => true;
            var result = await window.ShareLinkAsync(Entry(window, $"{Root}/a.txt"));

            var call = Assert.Single(standIn.Shares);
            Assert.Equal("nas:", call.Fs);
            Assert.Equal($"{Root}/a.txt", call.Remote);
            Assert.Equal(CloudShareLinkOutcome.Copied, result.Outcome);
            Assert.Equal(Link, result.Url);
        });
    }

    [Fact]
    public async Task Nothing_is_asked_of_the_cloud_until_the_user_agrees()
    {
        await WithWindow(async (window, standIn) =>
        {
            var copied = new List<string>();
            window.ConfirmShare = _ => false;
            window.CopyLink = copied.Add;

            var result = await window.ShareLinkAsync(Entry(window, $"{Root}/a.txt"));

            Assert.Equal(CloudShareLinkOutcome.Declined, result.Outcome);
            Assert.Empty(standIn.Shares);
            Assert.Empty(copied);
        });
    }

    [Fact]
    public async Task The_generated_link_is_the_exact_string_that_reaches_the_clipboard()
    {
        await WithWindow(async (window, standIn) =>
        {
            var copied = new List<string>();
            window.ConfirmShare = _ => true;
            window.CopyLink = copied.Add;

            await window.ShareLinkAsync(Entry(window, $"{Root}/a.txt"));

            Assert.Equal([Link], copied);
        });
    }

    [Fact]
    public async Task A_backend_without_links_is_reported_as_unsupported_and_copies_nothing()
    {
        await WithWindow(async (window, standIn) =>
        {
            var copied = new List<string>();
            window.ConfirmShare = _ => true;
            window.CopyLink = copied.Add;

            var result = await window.ShareLinkAsync(Entry(window, $"{Root}/a.txt"));

            Assert.Equal(CloudShareLinkOutcome.Unsupported, result.Outcome);
            Assert.Empty(copied);
            // The call really was made and the backend really said no — a refusal the window never asked about
            // would report the same outcome for the wrong reason.
            Assert.Contains("support public links", Assert.Single(standIn.Errors), StringComparison.Ordinal);
        }, new FakeRclone { RefusesLink = true });
    }

    [Fact]
    public async Task An_object_the_cloud_has_dropped_is_reported_as_missing()
    {
        await WithWindow(async (window, _) =>
        {
            window.ConfirmShare = _ => true;
            var result = await window.ShareLinkAsync(Entry(window, $"{Root}/a.txt"));

            Assert.Equal(CloudShareLinkOutcome.Missing, result.Outcome);
        }, new FakeRclone { Gone = true });
    }

    [Fact]
    public async Task An_answer_that_carries_no_link_copies_nothing()
    {
        await WithWindow(async (window, _) =>
        {
            var copied = new List<string>();
            window.ConfirmShare = _ => true;
            window.CopyLink = copied.Add;

            var result = await window.ShareLinkAsync(Entry(window, $"{Root}/a.txt"));

            Assert.Equal(CloudShareLinkOutcome.Failed, result.Outcome);
            Assert.Empty(copied);
        }, new FakeRclone { AnswersWithNoLink = true });
    }

    [Fact]
    public async Task A_folder_row_is_answered_without_troubling_the_backend()
    {
        await WithWindow(async (window, standIn) =>
        {
            window.ConfirmShare = _ => true;

            var result = await window.ShareLinkAsync(Entry(window, $"{Root}/incoming"));

            Assert.Equal(CloudShareLinkOutcome.NotAFile, result.Outcome);
            Assert.Empty(standIn.Shares);
        });
    }

    [Fact]
    public async Task The_status_line_says_what_the_link_grants_as_well_as_where_it_went()
    {
        // The link is now in the user's clipboard and readable by anyone who gets it; a status line that only
        // said "已复制" would leave the grant unmentioned.
        await WithWindow(async (window, _) =>
        {
            window.ConfirmShare = _ => true;
            window.CopyLink = _ => { };

            await window.ShareAsync(Entry(window, $"{Root}/a.txt"));

            Assert.Contains("已复制到剪贴板", window.StatusText.Text, StringComparison.Ordinal);
            Assert.Contains("无需登录", window.StatusText.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_link_the_clipboard_refused_is_still_told_to_the_user_as_a_link()
    {
        await WithWindow(async (window, _) =>
        {
            window.ConfirmShare = _ => true;
            window.CopyLink = _ => throw new IOException("剪贴板正被其他程序占用。");

            await window.ShareAsync(Entry(window, $"{Root}/a.txt"));

            Assert.Contains("云端已经生成链接", window.StatusText.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("没有取到分享链接", window.StatusText.Text, StringComparison.Ordinal);
        });
    }

    // ------------------------------------------------------------------ harness

    /// <summary>The row the window is holding for that path, so a test asks about what is on screen.</summary>
    private static CloudFileEntry Entry(RemoteEndpointManagerWindow window, string path) =>
        window.VisibleEntries.Single(entry => entry.Path == path);

    private static IEnumerable<string> Headers(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? "");

    private static void Select(RemoteEndpointManagerWindow window, params string[] paths)
    {
        window.FileList.SelectedItems.Clear();
        foreach (var path in paths)
            window.FileList.SelectedItems.Add(window.VisibleEntries.Single(entry => entry.Path == path));
    }

    private static CloudFileEntry File(string path, long size) =>
        new(path.Split('/').Last(), path, false, size, new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero));

    private static CloudFileEntry Folder(string path) =>
        new(path.Split('/').Last(), path, true, 0, null);

    private static readonly CloudEndpointRow[] Rows =
    [
        new(new CloudEndpointAccount(new RcloneAccount("nas", "sftp"), new CloudEndpointMetadata("nas", "sftp", "SFTP", Root, ""))),
    ];

    /// <summary>
    /// Runs one action against the real browser window over a stand-in rclone that answers
    /// <c>operations/publiclink</c>. The window is never shown, so its <c>Loaded</c> handler — which would read
    /// the application's own settings — is not reached, and every RC call is recorded. The listing is installed
    /// directly because a share request starts from a row that is already on screen.
    /// </summary>
    private static Task WithWindow(Func<RemoteEndpointManagerWindow, FakeRclone, Task> test,
        FakeRclone? rclone = null) => RunSta(async () =>
    {
        var journalRoot = NewDirectory();
        var previous = AppServices.TryCurrent;
        // The browser writes its 收藏 / 最近访问 file under the application's data root, so a window driven through
        // a real listing would otherwise write into the developer's own app data and read it back next run.
        var previousDataDir = Environment.GetEnvironmentVariable("FENGSYNC_DATA_DIR");
        Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", journalRoot);
        var standIn = rclone ?? new FakeRclone();
        var http = new HttpClient(standIn) { BaseAddress = new Uri("http://rc.test/") };
        var client = new RcloneRcClient(http, new Uri("http://rc.test/"), "user", "pass");
        var queue = new CloudTransferQueue(new CloudTransferJournalStore(journalRoot), _ => Task.FromResult(client));
        // The window resolves the mount service from the installed shell, and the rclone behind that shell is
        // unreachable on purpose: a test that reaches it fails loudly instead of starting a daemon.
        AppServices.Install(new StubShell(queue));
        RemoteEndpointManagerWindow? window = null;
        try
        {
            window = new RemoteEndpointManagerWindow(new CloudFileManagerService(_ => Task.FromResult(client)), queue);
            window.LoadEndpointsForTest(Rows, selectName: "nas");
            window.ShowListing(Root, [File($"{Root}/a.txt", 1024), File($"{Root}/b.txt", 2048), Folder($"{Root}/incoming")]);
            await test(window, standIn);
        }
        finally
        {
            AppServices.Install(previous);
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", previousDataDir);
            try { window?.Close(); }
            catch (InvalidOperationException) { }
            TryDelete(journalRoot);
        }
    });

    /// <summary>The synchronous cases, which still run on the window's own dispatcher thread.</summary>
    private static Task WithWindow(Action<RemoteEndpointManagerWindow, FakeRclone> test) =>
        WithWindow((window, standIn) =>
        {
            test(window, standIn);
            return Task.CompletedTask;
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

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fengsync-browser-share-tests", Guid.NewGuid().ToString("N"));
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
    /// Runs one action against a window on its own STA thread, with the dispatcher pumped while the action is in
    /// flight — a share request is a continuation the window posted to that dispatcher, and a thread parked in
    /// <c>Join</c> would never run it. The dispatcher is then shut down explicitly, because the process allows
    /// only one WPF <c>Application</c>. A run that stops making progress is cut off by a watchdog.
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
                failure = new TimeoutException("云端分享测试没有在规定时间内完成（窗口线程被卡住了）。");
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

    /// <summary>
    /// Gives this thread an Application to resolve a window's theme dictionaries through, and says whether the
    /// thread also owns it.
    /// </summary>
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

    /// <summary>One recorded <c>operations/publiclink</c>: the endpoint it was asked of and the object it was asked about.</summary>
    private sealed record ShareCall(string Fs, string Remote);

    /// <summary>
    /// A stand-in for rclone's RC endpoint, answering <c>operations/publiclink</c> the way the bundled v1.75.0
    /// does and recording every ask. <see cref="RefusesLink"/> is a backend that has no links (HTTP 500 with
    /// rclone's own wording), <see cref="Gone"/> is an object that is no longer there (HTTP 404 “object not
    /// found”), and <see cref="AnswersWithNoLink"/> is the answer a test must never let pass as a success.
    /// </summary>
    private sealed class FakeRclone : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<ShareCall> _shares = [];
        private readonly List<string> _errors = [];

        public bool RefusesLink { get; init; }

        public bool Gone { get; init; }

        /// <summary>When true, the call succeeds and carries no url — the shape an older or unusual reply has.</summary>
        public bool AnswersWithNoLink { get; init; }

        public IReadOnlyList<ShareCall> Shares
        {
            get { lock (_gate) return _shares.ToArray(); }
        }

        /// <summary>Diagnosis text the window recorded, so a test can say what was never told to the user.</summary>
        public IReadOnlyList<string> Errors
        {
            get { lock (_gate) return _errors.ToArray(); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var operation = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (operation == "operations/publiclink")
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var remote = Read(root, "remote");
                lock (_gate) _shares.Add(new ShareCall(Read(root, "fs"), remote));
                if (RefusesLink) return Failure(500, "Local file system at //?/C:/files doesn't support public links");
                if (Gone) return Failure(404, "object not found");
                return Answer(AnswersWithNoLink ? "{}" : "{\"url\":\"" + Link + "\"}");
            }
            if (operation == "operations/list") return Answer("""{"list":[]}""");
            return Answer("{}");
        }

        private HttpResponseMessage Failure(int status, string error)
        {
            lock (_gate) _errors.Add(error);
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(
                    "{\"error\":\"" + error + "\",\"input\":{},\"path\":\"operations/publiclink\",\"status\":" + status + "}",
                    Encoding.UTF8, "application/json")
            };
            return response;
        }

        private static HttpResponseMessage Answer(string json)
        {
            // Round-tripped through a parser so a mistyped answer above fails as an invalid test double here,
            // rather than as a JSON error the window reports and the test misreads as a backend refusal.
            using var document = JsonDocument.Parse(json);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(document.RootElement.GetRawText(), Encoding.UTF8, "application/json")
            };
        }

        private static string Read(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
    }
}
