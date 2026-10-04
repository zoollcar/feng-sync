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
/// The file browser's 属性. The rules behind the rows — what a folder may claim about its size, which word a
/// missing checksum gets, what a gone object says — are pinned in <c>CloudFeatureBehaviorTests</c>; what
/// belongs here is the path a real click takes: the <c>operations/stat</c> the window sends, the answer it
/// turns into rows, the single <c>operations/size</c> the 统计大小 button starts and what that walk leaves behind
/// in the panel, what it does when the backend refuses or has dropped the object, and which commands the row
/// menu hands a selection. Every case drives the real window over a stand-in rclone, so no daemon starts and no
/// credential exists. The panel itself is not opened: a modal dialog cannot be answered from inside a test,
/// which is why the window exposes <see cref="RemoteEndpointManagerWindow.DetailsAsync"/> and
/// <see cref="RemoteEndpointManagerWindow.BuildRowMenu"/> as the seams.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudBrowserDetailsTests
{
    /// <summary>The root folder of the endpoint the window is showing.</summary>
    private const string Root = "team";

    /// <summary>The checksum the stand-in holds for the file, ready to report the moment it is asked for.</summary>
    private const string Md5 = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task The_details_read_asks_the_backend_about_the_object_instead_of_trusting_the_row()
    {
        await WithWindow(async (window, standIn) =>
        {
            // The row on screen was listed when the directory was opened and says 1 KB and March 1; the object
            // is 4 KB and was touched April 2. Every value the backend can still answer comes from the answer.
            var panel = await window.DetailsAsync(File($"{Root}/report.txt", 1024));

            var call = Assert.Single(standIn.Stats);
            Assert.Equal($"{Root}/report.txt", call.Remote);
            Assert.Equal("4.0 KB（4,096 字节）", Value(panel.Rows, "大小"));
            Assert.Equal(new DateTimeOffset(2026, 4, 2, 9, 30, 0, TimeSpan.Zero).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                Value(panel.Rows, "修改时间"));
        });
    }

    [Fact]
    public async Task Opening_the_panel_asks_for_no_checksum_so_the_first_answer_is_the_cheap_one()
    {
        await WithWindow(async (window, standIn) =>
        {
            var panel = await window.DetailsAsync(File($"{Root}/report.txt", 1024));

            // Asking costs a pass over the object — measured at roughly 330 ms per type for a 256 MiB file on
            // the bundled rclone — so opening 属性 must not ask, and the panel says 未计算 and offers the read.
            var call = Assert.Single(standIn.Stats);
            Assert.Empty(call.HashTypes);
            Assert.Equal("未计算", Value(panel.Rows, "校验值"));
            Assert.Equal("计算校验值", panel.Action?.Label);
        });
    }

    [Fact]
    public async Task The_read_the_button_starts_names_the_three_checksums_the_engine_trusts()
    {
        await WithWindow(async (window, standIn) =>
        {
            var panel = await window.DetailsAsync(File($"{Root}/report.txt", 1024), withHash: true);

            var call = Assert.Single(standIn.Stats);
            Assert.Equal(["md5", "sha1", "sha256"], call.HashTypes);
            Assert.Equal(Md5, Value(panel.Rows, "校验值（MD5）"));
        });
    }

    [Fact]
    public async Task A_backend_that_has_no_checksum_keeps_saying_so_after_being_asked()
    {
        await WithWindow(async (window, _) =>
        {
            var file = File($"{Root}/report.txt", 1024);
            Assert.Equal("未计算", Value((await window.DetailsAsync(file)).Rows, "校验值"));

            // The second answer is a fact about the endpoint rather than about this dialog, and only the ask
            // can produce it: before it the application cannot tell "not computed" from "this backend has none".
            var asked = await window.DetailsAsync(file, withHash: true);
            Assert.Equal("此后端未提供校验值", Value(asked.Rows, "校验值"));
            // The ask was the answer: an endpoint that reported none after reading the object will report none
            // again, so the panel that learned it stops offering the read that costs the pass.
            Assert.Null(asked.Action);
        }, new FakeRclone { HoldsNoChecksums = true });
    }

    [Fact]
    public async Task The_type_only_the_backend_knows_reaches_the_panel()
    {
        await WithWindow(async (window, _) =>
        {
            // The listing carries no MIME type at all, so this row exists only because the window asked the
            // object itself — which is also the one kind of detail a user cannot read off the extension.
            var panel = await window.DetailsAsync(File($"{Root}/scan.pdf", 2048));

            Assert.Equal("application/pdf", Value(panel.Rows, "MIME 类型"));
        }, new FakeRclone { MimeType = "application/pdf" });
    }

    [Fact]
    public async Task An_object_the_backend_has_dropped_shows_no_size_and_asks_for_nothing()
    {
        await WithWindow(async (window, _) =>
        {
            var panel = await window.DetailsAsync(File($"{Root}/report.txt", 1024));

            Assert.Contains("云端已没有这个文件", Value(panel.Rows, "状态"), StringComparison.Ordinal);
            // A panel of values for a file that is not there is the failure this row exists to prevent, so the
            // byte count the stale row still carries must not be presented as the object's size.
            Assert.DoesNotContain(panel.Rows, row => row.Label == "大小");
            Assert.Null(panel.Action);
        }, new FakeRclone { Gone = true });
    }

    [Fact]
    public async Task A_refused_read_still_shows_the_listing_and_says_who_refused_it()
    {
        await WithWindow(async (window, _) =>
        {
            var panel = await window.DetailsAsync(File($"{Root}/report.txt", 1024));

            // The name, the type and the size the row already held are still true, so an unreadable detail is a
            // smaller answer rather than no answer — with the backend's refusal recorded as its own row.
            Assert.Equal("report.txt", Value(panel.Rows, "名称"));
            Assert.Equal("1.0 KB（1,024 字节）", Value(panel.Rows, "大小"));
            Assert.Contains("云端拒绝了此操作", Value(panel.Rows, "读取详情"), StringComparison.Ordinal);
            // With no answer about the object there is no fact to build an offer on: the panel would be asking to
            // read a checksum for an object it cannot even confirm is there.
            Assert.Null(panel.Action);
        }, new FakeRclone { RefusesStat = true });
    }

    [Fact]
    public async Task A_folder_is_answered_as_a_folder_and_never_offered_a_checksum_read()
    {
        await WithWindow(async (window, _) =>
        {
            var panel = await window.DetailsAsync(Folder($"{Root}/incoming"));

            Assert.Equal("未统计", Value(panel.Rows, "大小"));
            Assert.Equal("文件夹没有校验值", Value(panel.Rows, "校验值"));
            // The one read a folder can still be offered is the walk, never the checksum — and it is the only
            // button the panel gets, because a file has no subtree and a folder has no checksum to compute.
            Assert.Equal("统计大小", panel.Action?.Label);
        }, new FakeRclone { Directory = true, MimeType = "inode/directory" });
    }

    [Fact]
    public async Task The_size_button_walks_the_directory_the_row_named_and_says_what_it_found()
    {
        await WithWindow(async (window, standIn) =>
        {
            var panel = await window.DetailsAsync(Folder($"{Root}/incoming"), withSize: true);

            var call = Assert.Single(standIn.Sizes);
            Assert.Equal("nas:team/incoming", call.Fs);
            // Measured against the bundled v1.75.0: operations/size ignores a remote field completely, so an ask
            // that put the directory there would size the whole endpoint and report a number nobody asked for.
            Assert.False(call.CarriesRemote);
            Assert.Equal("46.3 GB（49,688,615,745 字节）· 205,034 个对象", Value(panel.Rows, "大小"));
        }, new FakeRclone { Directory = true, MimeType = "inode/directory" });
    }

    [Fact]
    public async Task A_counted_directory_is_shown_and_then_left_alone()
    {
        await WithWindow(async (window, standIn) =>
        {
            var counted = await window.DetailsAsync(Folder($"{Root}/incoming"), withSize: true);

            // The panel has its number; asking the cloud to walk the same tree again would be a second bill for
            // the answer already on screen.
            Assert.Null(counted.Action);
            Assert.Single(standIn.Sizes);
        }, new FakeRclone { Directory = true, MimeType = "inode/directory" });
    }

    [Fact]
    public async Task A_counted_size_is_not_kept_so_the_next_panel_asks_the_cloud_again()
    {
        await WithWindow(async (window, standIn) =>
        {
            var folder = Folder($"{Root}/incoming");
            Assert.Equal("未统计", Value((await window.DetailsAsync(folder)).Rows, "大小"));

            Assert.Contains("205,034 个对象", Value((await window.DetailsAsync(folder, withSize: true)).Rows, "大小"),
                StringComparison.Ordinal);

            // Re-opening 属性 is a new panel, and the tree may have grown since: the number from the last one is
            // not quoted back as if it had just been counted, and the walk runs again when asked.
            var again = await window.DetailsAsync(folder);
            Assert.Equal("未统计", Value(again.Rows, "大小"));
            Assert.Equal("统计大小", again.Action?.Label);
            Assert.Contains("205,034 个对象",
                Value((await window.DetailsAsync(folder, withSize: true)).Rows, "大小"), StringComparison.Ordinal);
            Assert.Equal(2, standIn.Sizes.Count);
        }, new FakeRclone { Directory = true, MimeType = "inode/directory" });
    }

    [Fact]
    public async Task Objects_the_cloud_would_not_size_are_counted_as_zero_and_said_out_loud()
    {
        await WithWindow(async (window, _) =>
        {
            var panel = await window.DetailsAsync(Folder($"{Root}/incoming"), withSize: true);

            // The bytes are a floor, not a total, and a Google Drive folder of Docs answers 0 for each one — a
            // user who reads "0 字节" as "nothing stored here" is reading a number the cloud never gave.
            Assert.Equal("1.2 GB（1,288,490,189 字节）· 40 个对象（其中 12 个云端不报大小，按 0 计入）",
                Value(panel.Rows, "大小"));
        }, new FakeRclone { Directory = true, Bytes = 1_288_490_189, Count = 40, Sizeless = 12 });
    }

    [Fact]
    public async Task An_empty_directory_is_an_answer_rather_than_a_failure()
    {
        await WithWindow(async (window, _) =>
        {
            var panel = await window.DetailsAsync(Folder($"{Root}/incoming"), withSize: true);

            Assert.Equal("0 个对象，空目录", Value(panel.Rows, "大小"));
        }, new FakeRclone { Directory = true, Bytes = 0, Count = 0, Sizeless = 0 });
    }

    [Fact]
    public async Task A_directory_the_cloud_dropped_mid_walk_says_so_instead_of_claiming_zero()
    {
        await WithWindow(async (window, _) =>
        {
            var panel = await window.DetailsAsync(Folder($"{Root}/incoming"), withSize: true);

            // v1.75.0 answers a missing directory with HTTP 404 "directory not found"; the category alone would
            // come back as a connection problem, and the row would say the folder holds nothing.
            Assert.Equal("云端已没有这个文件夹", Value(panel.Rows, "大小"));
        }, new FakeRclone { Directory = true, RefusesSizeWith = "directory not found" });
    }

    [Fact]
    public async Task A_walk_the_cloud_refuses_for_a_reason_no_one_named_keeps_the_reason_visible()
    {
        await WithWindow(async (window, _) =>
        {
            var panel = await window.DetailsAsync(Folder($"{Root}/incoming"), withSize: true);

            Assert.StartsWith("未能统计：", Value(panel.Rows, "大小"), StringComparison.Ordinal);
            Assert.Contains("quota", Value(panel.Rows, "大小"), StringComparison.OrdinalIgnoreCase);
        }, new FakeRclone { Directory = true, RefusesSizeWith = "Remote quota exceeded" });
    }

    [Fact]
    public async Task Counting_a_folder_never_lists_a_directory_and_never_visits_one()
    {
        await WithWindow(async (window, standIn) =>
        {
            // The control first: a listing that arrived is remembered as a visit, so the untouched list below is
            // a fact about the walk rather than about a harness that never records anything at all.
            await window.NavigateToAsync($"{Root}/incoming");
            Assert.NotEmpty(window.RecentPlaces);
            var visited = window.RecentPlaces;
            var calls = standIn.Operations.Count;

            await window.DetailsAsync(Folder($"{Root}/incoming"), withSize: true);

            // 收藏 / 最近访问 belongs to a directory that was listed. A subtree walk counts bytes inside the
            // backend — measured: operations/size does its own recursion and never goes through
            // operations/list — so it must not leave a folder in the recent list as a side effect.
            Assert.NotEmpty(standIn.Operations.Skip(calls));
            Assert.DoesNotContain("operations/list", standIn.Operations.Skip(calls));
            Assert.Equal(visited, window.RecentPlaces);
        }, new FakeRclone { Directory = true });
    }

    [Fact]
    public async Task A_file_never_offers_the_walk_it_cannot_answer()
    {
        await WithWindow(async (window, standIn) =>
        {
            var panel = await window.DetailsAsync(File($"{Root}/report.txt", 1024));

            Assert.Equal("计算校验值", panel.Action?.Label);
            Assert.Empty(standIn.Sizes);
        });
    }

    [Fact]
    public async Task The_panel_names_the_endpoint_and_the_address_rclone_would_take()
    {
        await WithWindow(async (window, _) =>
        {
            var panel = await window.DetailsAsync(File($"{Root}/report.txt", 1024));

            Assert.Equal($"nas:{Root}/report.txt", Value(panel.Rows, "路径"));
            Assert.Equal("nas · SFTP · sftp", Value(panel.Rows, "所在端点"));
        });
    }

    [Fact]
    public async Task One_row_earns_the_properties_command_and_many_earn_it_with_a_count()
    {
        await WithWindow((window, _) =>
        {
            Select(window, $"{Root}/a.txt");
            Assert.Contains("属性", Headers(window.BuildRowMenu()!));

            Select(window, $"{Root}/a.txt", $"{Root}/b.txt");
            Assert.Contains("查看所选 2 项的属性", Headers(window.BuildRowMenu()!));
        });
    }

    [Fact]
    public async Task The_read_only_answer_sits_last_in_the_row_menu_behind_a_separator()
    {
        await WithWindow((window, _) =>
        {
            Select(window, $"{Root}/a.txt");
            var items = window.BuildRowMenu()!.Items.Cast<object>().ToList();

            // The read-only answer goes last, after a separator, as Explorer's does: placed among the actions
            // that change the cloud it would be the item a hurried click takes by mistake.
            Assert.IsType<Separator>(items[^2]);
            Assert.Equal("属性", Assert.IsType<MenuItem>(items[^1]).Header);
        });
    }

    [Fact]
    public async Task Nothing_selected_means_there_is_no_row_menu_to_build()
    {
        await WithWindow((window, _) => Assert.Null(window.BuildRowMenu()));
    }

    // ------------------------------------------------------------------ harness

    private static string Value(IReadOnlyList<CloudDetailRow> rows, string label) =>
        rows.Single(row => row.Label == label).Value;

    private static IEnumerable<string> Headers(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? "");

    /// <summary>Selects rows by path, the way the list holds them after a listing.</summary>
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
    /// <c>operations/stat</c>. The window is never shown, so its <c>Loaded</c> handler — which would read the
    /// application's own settings — is not reached, and every RC call is recorded. The listing is installed
    /// directly because a details read starts from rows that are already on screen.
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
        var path = Path.Combine(Path.GetTempPath(), "fengsync-browser-details-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>The queue persists asynchronously, so a journal write can still be in flight at teardown.</summary>
    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Runs one action against a window on its own STA thread, with the dispatcher pumped while the action is
    /// in flight — a details read is a continuation the window posted to that dispatcher, and a thread parked
    /// in <c>Join</c> would never run it. The dispatcher is then shut down explicitly, because the process
    /// allows only one WPF <c>Application</c> and WPF releases it when the dispatcher that owns it closes. A
    /// run that stops making progress is cut off by a watchdog rather than left to block the rest of the suite.
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
                failure = new TimeoutException("云端属性测试没有在规定时间内完成（窗口线程被卡住了）。");
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
    /// thread also owns it. One built here gets its shutdown mode raised to explicit, because the default ends
    /// the Application the moment its last window closes.
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

    /// <summary>One recorded <c>operations/stat</c>: the object asked about, and the checksums asked for with it.</summary>
    private sealed record StatCall(string Fs, string Remote, IReadOnlyList<string> HashTypes);

    /// <summary>
    /// One recorded <c>operations/size</c>: the <c>fs</c> it was pointed at, and whether the ask also carried a
    /// <c>remote</c>. The second field exists because the measured answer to such an ask is the whole endpoint's
    /// total rather than an error — a number that looks perfectly plausible on a folder row.
    /// </summary>
    private sealed record SizeCall(string Fs, bool CarriesRemote);

    /// <summary>
    /// A stand-in for rclone's RC endpoint, answering <c>operations/stat</c> and <c>operations/size</c> the way
    /// the bundled v1.75.0 does and recording every ask. It reports its checksums only when the call asked for
    /// them, because that is what rclone does: an answer that handed out a hash nobody requested would let a test
    /// pass while the application still paid for nothing. <see cref="Gone"/> is the missing object, which v1.75.0
    /// answers as <c>{"item":null}</c> rather than as an error, and <see cref="RefusesStat"/> is a backend that
    /// says no. The size shapes are the measured ones: <c>{"bytes":…,"count":…,"sizeless":…}</c> with the
    /// directory inside <c>fs</c>, and a refusal as an error sentence — <see cref="RefusesSizeWith"/> is what it
    /// answers with, because which refusal this was is read from those words and not from the status code.
    /// </summary>
    private sealed class FakeRclone : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<StatCall> _stats = [];
        private readonly List<SizeCall> _sizes = [];
        private readonly List<string> _operations = [];

        /// <summary>The size reported for the object, deliberately different from the size on screen.</summary>
        public long Size { get; init; } = 4096;

        public bool Directory { get; init; }

        public string? MimeType { get; init; } = "text/plain";

        /// <summary>The subtree total <c>operations/size</c> reports, chosen to be a real folder's numbers.</summary>
        public long Bytes { get; init; } = 49_688_615_745;

        /// <summary>How many objects the walk counted — directories excluded, as rclone excludes them.</summary>
        public long Count { get; init; } = 205_034;

        /// <summary>How many of those the backend would not size, which rclone counts as zero bytes.</summary>
        public long Sizeless { get; init; }

        /// <summary>When set, <c>operations/size</c> is refused with this sentence instead of answering.</summary>
        public string? RefusesSizeWith { get; init; }

        /// <summary>When true, the object has no checksum to report even when asked for one.</summary>
        public bool HoldsNoChecksums { get; init; }

        /// <summary>When true, the object is not there: rclone answers a null item, not an error.</summary>
        public bool Gone { get; init; }

        /// <summary>When true, <c>operations/stat</c> is refused the way a backend refuses a bad read.</summary>
        public bool RefusesStat { get; init; }

        public IReadOnlyList<StatCall> Stats
        {
            get { lock (_gate) return _stats.ToArray(); }
        }

        public IReadOnlyList<SizeCall> Sizes
        {
            get { lock (_gate) return _sizes.ToArray(); }
        }

        /// <summary>Every RC endpoint this stand-in was called on, in order.</summary>
        public IReadOnlyList<string> Operations
        {
            get { lock (_gate) return _operations.ToArray(); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var operation = request.RequestUri!.AbsolutePath.TrimStart('/');
            lock (_gate) _operations.Add(operation);
            var refused = RefusesStat && operation == "operations/stat";
            var answer = operation switch
            {
                "operations/list" => """{"list":[]}""",
                "operations/stat" => Stat(body),
                "operations/size" => SizeAnswer(body),
                _ => "{}",
            };
            if (refused)
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("""{"error":"sftp: 拒绝了这个读取"}""", Encoding.UTF8, "text/plain")
                };
            // v1.75.0 answers a failed read with the HTTP status rclone itself chose — 404 for a directory that
            // is gone, 500 for one that turns out to be a file — and the body holds the sentence the outcome is
            // read from.
            if (operation == "operations/size" && RefusesSizeWith is { Length: > 0 } refusal)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent($$"""{"error":"{{refusal}}","input":{},"status":500,"path":"/operations/size"}""",
                        Encoding.UTF8, "application/json")
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(answer, Encoding.UTF8, "application/json")
            };
        }

        private string SizeAnswer(string body)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            lock (_gate) _sizes.Add(new SizeCall(Read(root, "fs"), root.TryGetProperty("remote", out _)));
            return $$"""{"bytes":{{Bytes}},"count":{{Count}},"sizeless":{{Sizeless}}}""";
        }

        private string Stat(string body)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var remote = Read(root, "remote");
            var hashTypes = root.TryGetProperty("opt", out var opt)
                && opt.TryGetProperty("hashTypes", out var types)
                && types.ValueKind == JsonValueKind.Array
                    ? types.EnumerateArray().Select(value => value.GetString() ?? "").ToList()
                    : [];
            lock (_gate) _stats.Add(new StatCall(Read(root, "fs"), remote, hashTypes));

            if (RefusesStat) return """{"error":"sftp: 拒绝了这个读取"}""";
            if (Gone) return """{"item":null}""";

            // Built field by field because the two optional ones are what backends genuinely vary: a real
            // rclone always reports a MIME type, and a Hashes object only appears once it was asked for.
            var fields = new List<string>
            {
                "\"Path\":\"" + remote + "\"",
                "\"Name\":\"" + remote.Split('/').Last() + "\"",
                "\"IsDir\":" + (Directory ? "true" : "false"),
                "\"Size\":" + Size,
                "\"ModTime\":\"2026-04-02T09:30:00Z\"",
            };
            if (MimeType is { Length: > 0 } mime) fields.Add("\"MimeType\":\"" + mime + "\"");
            if (!HoldsNoChecksums && hashTypes.Count > 0) fields.Add("\"Hashes\":{\"md5\":\"" + Md5 + "\"}");
            // Round-tripped through a parser so a mistyped field above fails as an invalid test double here,
            // rather than as a JSON error the window reports and the test misreads as a backend refusal.
            using var answer = JsonDocument.Parse("{\"item\":{" + string.Join(",", fields) + "}}");
            return answer.RootElement.GetRawText();
        }

        private static string Read(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
    }
}
