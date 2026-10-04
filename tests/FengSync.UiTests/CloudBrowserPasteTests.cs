using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using FengSync.Core;
using FengSync.Core.Mount;
using FengSync.Core.Rclone.Lifecycle;
using FengSync.Services;
using FengSync.Views;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// The file browser's 复制 / 剪切 / 粘贴. What the queue sends rclone for a remote→remote copy is pinned by
/// <see cref="CloudTransferCopyTests"/> and where the bytes land by <c>ServerSideCopyTests</c>; what belongs
/// here is the browser's own decisions — which endpoint a row came from, where in the destination it lands,
/// which rows cannot be pasted, and that a cut deletes a source only once its copy has actually completed.
/// Every case drives the real window over a stand-in rclone, so no daemon starts and no credential exists.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudBrowserPasteTests
{
    /// <summary>The root folder of the endpoint the rows are copied out of.</summary>
    private const string Root = "team";

    /// <summary>The root folder of the endpoint rows are pasted into, deliberately a different one so a paste
    /// has to compose each side against its own endpoint's root.</summary>
    private const string Other = "shared";

    /// <summary>The one name the stand-in destination always already holds, so a conflict exists to be asked about.</summary>
    private const string Existing = "a.txt";

    [Fact]
    public async Task An_empty_clipboard_keeps_the_paste_command_off_and_says_why()
    {
        await WithWindow((window, _, _) =>
        {
            // A desktop scenario finds the button by this id rather than by its label, so the id is part of
            // the feature's contract, not a naming detail.
            Assert.Equal("CloudPaste", AutomationProperties.GetAutomationId(window.PasteButton));
            Assert.False(window.PasteButton.IsEnabled);
            window.PasteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Contains("剪贴板是空的", window.StatusText.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Copying_a_selection_holds_it_with_the_endpoint_it_came_from()
    {
        await WithWindow((window, _, _) =>
        {
            window.SetClipboard(CloudClipboardMode.Copy, [File($"{Root}/b.txt", 1024)]);

            Assert.True(window.PasteButton.IsEnabled);
            var held = Assert.Single(window.ClipboardItems);
            Assert.Equal("team/b.txt", held.Entry.Path);
            // The row means nothing without its remote, and the paste may happen on another endpoint entirely.
            Assert.Equal("nas", held.Source.Name);
            Assert.Equal(CloudClipboardMode.Copy, window.ClipboardMode);
            Assert.Contains("已复制 1 项", window.StatusText.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_cut_says_out_loud_that_its_paste_ends_in_a_delete()
    {
        await WithWindow((window, _, _) =>
        {
            window.SetClipboard(CloudClipboardMode.Cut, [File($"{Root}/b.txt", 1024), Folder($"{Root}/docs")]);

            Assert.Equal(CloudClipboardMode.Cut, window.ClipboardMode);
            Assert.Contains("已剪切 2 项", window.StatusText.Text, StringComparison.Ordinal);
            // Both half-truths a user could otherwise act on: the folder will not be pasted, and the delete
            // is not undoable.
            Assert.Contains("文件夹不会被粘贴", window.StatusText.Text, StringComparison.Ordinal);
            Assert.Contains("无法恢复", window.StatusText.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_folder_is_never_pasted_because_the_transfer_unit_is_one_file()
    {
        await WithWindow(async (window, _, _) =>
        {
            window.SetClipboard(CloudClipboardMode.Copy, [Folder($"{Root}/docs")]);
            window.ShowListing("team/incoming", []);

            var plan = await window.PlanPasteAsync();

            Assert.NotNull(plan);
            Assert.Empty(plan!.Targets);
            Assert.Contains("文件夹", string.Join(" ", plan.Skipped), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_paste_lands_in_the_directory_on_screen_with_both_sides_relative_to_their_own_root()
    {
        // The destination is the directory being browsed, not the one the rows came from, and each side is
        // addressed relative to its own endpoint root. Getting either wrong copies to the wrong place.
        await WithWindow(async (window, _, _) =>
        {
            window.SetClipboard(CloudClipboardMode.Copy, [File("team/reports/report.pdf", 4096)]);
            SelectEndpoint(window, "files");
            window.ShowListing($"{Other}/incoming", []);

            var plan = await window.PlanPasteAsync();

            Assert.NotNull(plan);
            var target = Assert.Single(plan!.Targets);
            // Each side is stripped against its own endpoint's root: the source stays inside team/, the
            // destination inside shared/, and neither carries the other's prefix.
            Assert.Equal("shared/incoming/report.pdf", target.DestinationPath);
            Assert.Equal("reports/report.pdf", target.SourceRelativePath);
            Assert.Equal("incoming/report.pdf", target.DestinationRelativePath);
            Assert.False(target.SameEndpoint);
        });
    }

    [Fact]
    public async Task Copying_a_file_onto_its_own_directory_is_skipped_rather_than_queued()
    {
        await WithWindow(async (window, _, _) =>
        {
            window.SetClipboard(CloudClipboardMode.Copy, [File($"{Root}/{Existing}", 1024)]);
            window.ShowListing(Root, []);

            var plan = await window.PlanPasteAsync();

            Assert.NotNull(plan);
            Assert.Empty(plan!.Targets);
            Assert.Contains("已经在目标目录里", string.Join(" ", plan.Skipped), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_paste_onto_an_existing_name_is_reported_as_the_conflict_it_is()
    {
        await WithWindow(async (window, _, _) =>
        {
            window.SetClipboard(CloudClipboardMode.Copy, [File($"{Root}/{Existing}", 1024)]);
            SelectEndpoint(window, "files");
            window.ShowListing($"{Other}/incoming", []);

            var plan = await window.PlanPasteAsync();

            Assert.NotNull(plan);
            var target = Assert.Single(plan!.Targets);
            // The destination's own listing holds "a.txt", so pasting that name is the case the user has to
            // choose about before anything is queued.
            Assert.True(target.OverwritesExisting);
            Assert.Equal(1, plan.Conflicts);
        });
    }

    [Fact]
    public async Task A_paste_queues_one_remote_to_remote_copy_per_row()
    {
        await WithWindow(async (window, rclone, _) =>
        {
            window.SetClipboard(CloudClipboardMode.Copy, [File($"{Root}/b.txt", 1024), File($"{Root}/c.txt", 2048)]);
            SelectEndpoint(window, "files");
            window.ShowListing($"{Other}/incoming", []);
            var plan = await window.PlanPasteAsync();
            Assert.NotNull(plan);

            Assert.Equal(2, await window.ExecutePasteAsync(plan!, overwrite: true));

            // Each copy runs on the queue's own worker, so the two rows arrive in no particular order.
            await WaitUntil(() => rclone.Copies.Count == 2, "the browser queued two copies");
            var copies = rclone.Copies;
            Assert.All(copies, copy => Assert.Equal(("nas:", "files:"), (copy.SourceFs, copy.TargetFs)));
            Assert.Equal(["team/b.txt", "team/c.txt"], copies.Select(copy => copy.SourceRemote).Order(StringComparer.Ordinal));
            Assert.Equal([$"{Other}/incoming/b.txt", $"{Other}/incoming/c.txt"],
                copies.Select(copy => copy.TargetRemote).Order(StringComparer.Ordinal));
            Assert.Contains("已排队复制 2 个文件", window.StatusText.Text, StringComparison.Ordinal);
            // A copy has no local file, so nothing may be written under a name Feng Sync reads as one.
            Assert.DoesNotContain(rclone.Bodies, body => body.Contains("fengsync.partial", StringComparison.Ordinal));
            // 复制 leaves the source alone; the calls that would not belong here are the ones that remove or
            // rename it.
            Assert.False(rclone.Sent("operations/deletefile"));
            Assert.False(rclone.Sent("operations/movefile"));
        });
    }

    [Fact]
    public async Task A_paste_that_would_overwrite_skips_the_conflicts_when_the_user_chose_skip()
    {
        await WithWindow(async (window, rclone, _) =>
        {
            window.SetClipboard(CloudClipboardMode.Copy, [File($"{Root}/{Existing}", 1024), File($"{Root}/new.txt", 1024)]);
            SelectEndpoint(window, "files");
            window.ShowListing($"{Other}/incoming", []);
            var plan = await window.PlanPasteAsync();
            Assert.NotNull(plan);

            Assert.Equal(1, await window.ExecutePasteAsync(plan!, overwrite: false));

            await WaitUntil(() => rclone.Copies.Count == 1, "the browser queued one copy");
            // "a.txt" is the name already present, so the row that moved is the one that was not.
            Assert.Equal("team/new.txt", Assert.Single(rclone.Copies).SourceRemote);
        });
    }

    [Fact]
    public async Task A_cut_inside_one_endpoint_moves_instead_of_copying_and_empties_the_clipboard()
    {
        await WithWindow(async (window, rclone, _) =>
        {
            window.SetClipboard(CloudClipboardMode.Cut, [File($"{Root}/{Existing}", 1024)]);
            window.ShowListing("team/incoming", []);
            var plan = await window.PlanPasteAsync();
            Assert.NotNull(plan);

            Assert.Equal(1, await window.ExecutePasteAsync(plan!, overwrite: true));

            // Inside one endpoint rclone answers a move without carrying the bytes, so there is no copy to
            // wait for and no source delete to schedule.
            var move = Assert.Single(rclone.Moves);
            Assert.Equal(("nas:", "nas:"), (move.SourceFs, move.TargetFs));
            Assert.Equal(($"team/{Existing}", $"team/incoming/{Existing}"), (move.SourceRemote, move.TargetRemote));
            Assert.Empty(rclone.Copies);
            Assert.Empty(rclone.Deleted);
            Assert.Empty(window.ClipboardItems);
            Assert.Contains("已移动 1 项", window.StatusText.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_cut_between_endpoints_deletes_the_source_only_after_its_copy_landed()
    {
        await WithWindow(async (window, rclone, _) =>
        {
            window.SetClipboard(CloudClipboardMode.Cut, [File($"{Root}/{Existing}", 1024)]);
            // The paste happens on the other endpoint, which is what makes this a copy plus a delete rather
            // than a move: rclone cannot move an object between two backends.
            SelectEndpoint(window, "files");
            window.ShowListing($"{Other}/incoming", []);
            var plan = await window.PlanPasteAsync();
            Assert.NotNull(plan);

            Assert.Equal(1, await window.ExecutePasteAsync(plan!, overwrite: true));

            // The delete rides on the copy's completion, so it is a later call rather than an earlier one.
            await WaitUntil(() => rclone.Deleted.Count == 1, "the cut deleted its source");
            Assert.Equal($"nas: team/{Existing}", Assert.Single(rclone.Deleted));
            Assert.Single(rclone.Copies);
            Assert.Empty(rclone.Moves);
            await WaitUntil(() => window.StatusText.Text.Contains("已剪切到 files", StringComparison.Ordinal),
                "the cut never reported where the file went");
        });
    }

    [Fact]
    public async Task A_cut_whose_copy_was_refused_keeps_its_source()
    {
        // The whole safety argument for cutting through a copy: if rclone refused the copy, the file must still
        // be where it was. A delete that ran anyway would turn a failed paste into data loss.
        await WithWindow(async (window, rclone, transfers) =>
        {
            // Without a retry the row settles once, which is what makes the following silence meaningful.
            transfers.MaxRetries = 0;
            window.SetClipboard(CloudClipboardMode.Cut, [File($"{Root}/{Existing}", 1024)]);
            SelectEndpoint(window, "files");
            window.ShowListing("team/incoming", []);
            var plan = await window.PlanPasteAsync();
            Assert.NotNull(plan);

            await window.ExecutePasteAsync(plan!, overwrite: true);

            var task = Assert.Single(transfers.Tasks);
            await WaitUntil(() => task.IsFinished, "the refused copy never settled");
            Assert.Equal(CloudTransferState.Failed, task.State);
            await WaitUntil(() => window.StatusText.Text.Contains("源文件仍然保留", StringComparison.Ordinal),
                "a refused cut did not say its source survived");
            Assert.Empty(rclone.Deleted);
        }, new FakeRclone { RefusesCopies = true });
    }

    [Fact]
    public async Task An_endpoint_pastes_into_the_directory_on_screen_after_leaving_its_own()
    {
        await WithWindow(async (window, rclone, _) =>
        {
            window.SetClipboard(CloudClipboardMode.Copy, [File($"{Root}/{Existing}", 1024)]);
            // Moving on is the normal gesture: the clipboard outlives the listing it was made from.
            SelectEndpoint(window, "files");
            window.ShowListing($"{Other}/archive", []);

            await window.ExecutePasteAsync((await window.PlanPasteAsync())!, overwrite: true);

            await WaitUntil(() => rclone.Copies.Count == 1, "the browser queued a copy");
            var copy = Assert.Single(rclone.Copies);
            Assert.Equal("nas:", copy.SourceFs);
            Assert.Equal("files:", copy.TargetFs);
            Assert.Equal("shared/archive/a.txt", copy.TargetRemote);
        });
    }

    // ------------------------------------------------------------------ harness

    private static CloudFileEntry File(string path, long size) =>
        new(path.Split('/').Last(), path, false, size, new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero));

    private static CloudFileEntry Folder(string path) =>
        new(path.Split('/').Last(), path, true, 0, null);

    private static CloudEndpointRow Row(string name, string root) =>
        new(new CloudEndpointAccount(new RcloneAccount(name, "sftp"), new CloudEndpointMetadata(name, "sftp", "SFTP", root, "")));

    /// <summary>Moves the browser to another endpoint without re-listing it: the clipboard must survive.</summary>
    private static void SelectEndpoint(RemoteEndpointManagerWindow window, string name) =>
        window.LoadEndpointsForTest([Row("nas", Root), Row("files", Other)], selectName: name);

    /// <summary>
    /// Runs one action against the real browser window, over a file service and a transfer queue that both talk
    /// to the same stand-in rclone. The window is never shown, so its <c>Loaded</c> handler — which would read
    /// the application's own settings and journal — is not reached, and every RC call is recorded. The queue is
    /// handed to the caller because a paste reports through it: a test that claims a source survived has to
    /// read the transfer's settled state rather than infer it from a delay.
    /// </summary>
    private static Task WithWindow(Func<RemoteEndpointManagerWindow, FakeRclone, CloudTransferQueue, Task> test,
        FakeRclone? rclone = null) => RunSta(async () =>
    {
        var journalRoot = NewDirectory();
        var previous = AppServices.TryCurrent;
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
            window.LoadEndpointsForTest([Row("nas", Root), Row("files", Other)], selectName: "nas");
            window.ShowListing(Root, []);
            await test(window, standIn, queue);
        }
        finally
        {
            AppServices.Install(previous);
            // A test may close the window itself, and WPF rejects closing a closed window.
            try { window?.Close(); }
            catch (InvalidOperationException) { }
            TryDelete(journalRoot);
        }
    });

    /// <summary>The synchronous cases, which still run on the window's own dispatcher thread.</summary>
    private static Task WithWindow(Action<RemoteEndpointManagerWindow, FakeRclone, CloudTransferQueue> test) =>
        WithWindow((window, standIn, queue) =>
        {
            test(window, standIn, queue);
            return Task.CompletedTask;
        });

    /// <summary>Polls until the stand-in or the queue has recorded what the browser should have produced.</summary>
    private static async Task WaitUntil(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"超时：{message}");
            await Task.Delay(25);
        }
    }

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
        var path = Path.Combine(Path.GetTempPath(), "fengsync-browser-paste-tests", Guid.NewGuid().ToString("N"));
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
    /// in flight. Pumping is what a paste needs: the status line a cut writes once its queued copy has finished
    /// is a continuation the window posted to that dispatcher, and a thread parked in <c>Join</c> would never
    /// run it. The dispatcher is then shut down explicitly, because the process allows only one WPF
    /// <c>Application</c> and WPF releases it when the dispatcher that owns it closes — without that, the next
    /// window test to start would find no <c>Application.Current</c> on its own thread and fail trying to build
    /// a second one. The theme dictionaries a browser window resolves through <c>StaticResource</c> live on
    /// that Application, so the thread that builds the window is the one that builds it. A run that stops making
    /// progress is cut off by a watchdog rather than left to block the rest of the suite.
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
            // The deadline is a watchdog on the pumped frame, not on the thread: an action that never completes
            // would hold this frame open forever, and the suite would end with no result at all rather than a
            // failure naming the test that hung. It stops the frame from outside, which is what makes the check
            // below able to report an unfinished run instead of accepting it.
            var watchdog = new System.Threading.Timer(
                _ => dispatcher.BeginInvoke(() => frame.Continue = false),
                null, TimeSpan.FromSeconds(90), System.Threading.Timeout.InfiniteTimeSpan);
            Dispatcher.PushFrame(frame);
            watchdog.Dispose();
            if (failure is null && run is { IsCompleted: false })
                failure = new TimeoutException("云端粘贴测试没有在规定时间内完成（窗口线程被卡住了）。");
            // The Application this thread built is released again here, because WPF only disposes it when its
            // own dispatcher closes and the process allows exactly one: leaving it alive would make the next
            // window test fail on `new Application` instead of on its own assertion.
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
    /// thread also owns it. An Application already in the process is reused rather than configured: it belongs
    /// to another thread, and touching its <see cref="Application.ShutdownMode"/> from here is a cross-thread
    /// access WPF refuses. One built here gets its shutdown mode raised to explicit, because the default ends
    /// the Application — and with it this thread's dispatcher, and every continuation the paste still has to
    /// run — the moment its last window closes.
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

    /// <summary>
    /// One recorded RC call of the shapes a paste uses: the two fs/path pairs of a copy or a move. A delete has
    /// only one, and is recorded as <c>fs: path</c>.
    /// </summary>
    private sealed record Call(string SourceFs, string SourceRemote, string TargetFs, string TargetRemote);

    /// <summary>
    /// A stand-in for rclone's RC endpoint, answering the calls a paste makes and recording the three that
    /// matter. <c>operations/list</c> always reports one same-name file already in the browsed directory,
    /// because a paste's other decision — whether to overwrite — needs a conflict to exist. A successful copy
    /// answers without a job id, the way a synchronous backend reply does, so the queue settles the row as soon
    /// as the call returns; only <see cref="RefusesCopies"/> needs the polling and job paths.
    /// </summary>
    private sealed class FakeRclone : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<Call> _copies = [];
        private readonly List<Call> _moves = [];
        private readonly List<string> _deleted = [];
        private readonly List<string> _operations = [];
        private readonly List<string> _bodies = [];

        /// <summary>When true, <c>operations/copyfile</c> is refused, the way a backend refuses a bad copy.</summary>
        public bool RefusesCopies { get; init; }

        public IReadOnlyList<Call> Copies
        {
            get { lock (_gate) return _copies.ToArray(); }
        }
        public IReadOnlyList<Call> Moves
        {
            get { lock (_gate) return _moves.ToArray(); }
        }
        public IReadOnlyList<string> Deleted
        {
            get { lock (_gate) return _deleted.ToArray(); }
        }
        public IReadOnlyList<string> Bodies
        {
            get { lock (_gate) return _bodies.ToArray(); }
        }

        public bool Sent(string operation)
        {
            lock (_gate) return _operations.Contains(operation, StringComparer.Ordinal);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var operation = request.RequestUri!.AbsolutePath.TrimStart('/');
            var refused = RefusesCopies && operation == "operations/copyfile";
            var answer = operation switch
            {
                "operations/list" => $$"""
                    {"list":[{"Path":"{{Existing}}","Name":"{{Existing}}","IsDir":false,"Size":1}]}
                    """,
                "operations/copyfile" => refused ? """{"error":"sftp: 拒绝了这个复制"}""" : Record(_copies, body),
                "operations/movefile" => Record(_moves, body),
                "operations/deletefile" => Delete(body),
                _ => "{}"
            };
            lock (_gate)
            {
                _operations.Add(operation);
                _bodies.Add(operation + " " + body);
            }
            return new HttpResponseMessage(refused ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
            {
                Content = new StringContent(answer, Encoding.UTF8, refused ? "text/plain" : "application/json")
            };
        }

        private string Delete(string body)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            lock (_gate) _deleted.Add($@"{Read(root, "fs")} {Read(root, "remote")}");
            return "{}";
        }

        private static string Record(List<Call> into, string body)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var call = new Call(Read(root, "srcFs"), Read(root, "srcRemote"), Read(root, "dstFs"), Read(root, "dstRemote"));
            lock (into) into.Add(call);
            return "{}";
        }

        private static string Read(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
    }
}
