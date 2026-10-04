using System.IO;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
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
/// The file browser's 回收站. Which landing name is free, where a row came home to and what each outcome is
/// called are pinned by <see cref="CloudFeatureBehaviorTests"/> against the pure rules; what belongs here is the
/// path a click actually takes over a real window: which selection earns which command, that a recycle is a move
/// rather than a delete, that a file travels by <c>operations/movefile</c> and a folder by the sync engine's
/// directory move, that the husk a merged directory move leaves behind is removed and one that was taken whole is
/// not, and that 清空回收站 asks 删除's question with one more line instead of inventing a second threshold.
/// Every case runs against a stand-in rclone that answers the way the bundled v1.75.0 was measured to answer, so
/// no daemon starts and no credential exists.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudBrowserRecycleTests
{
    /// <summary>The root folder of the endpoint the window is showing.</summary>
    private const string Root = "team";

    /// <summary>The endpoint's trash, addressed the way every other browser path is.</summary>
    private const string Trash = Root + "/.fengsync-trash";

    private const string FileA = Root + "/a.txt";

    private const string FileB = Root + "/b.txt";

    private const string TrashedFile = Trash + "/a.txt";

    /// <summary>A trashed row that mirrors a subdirectory, so its restore has to recompose that directory.</summary>
    private const string TrashedReport = Trash + "/reports/a.pdf";

    [Fact]
    public async Task The_trash_commands_carry_the_ids_a_desktop_scenario_looks_for_and_hide_until_the_trash_is_on_screen()
    {
        await WithWindow((window, _) =>
        {
            Assert.Equal("CloudRecycle", AutomationProperties.GetAutomationId(window.RecycleButton));
            Assert.Equal("CloudRestore", AutomationProperties.GetAutomationId(window.RestoreButton));
            Assert.Equal("CloudEmptyTrash", AutomationProperties.GetAutomationId(window.EmptyTrashButton));
            Assert.Equal("CloudOpenTrash", AutomationProperties.GetAutomationId(window.OpenTrashButton));
            Assert.False(window.RecycleButton.IsEnabled);
            Select(window, FileA);
            Assert.True(window.RecycleButton.IsEnabled);
            // Standing in the endpoint's root, 还原 and 清空回收站 would be aimed at rows that were never
            // recycled, so they appear only once the trash itself is the directory on screen.
            Assert.Equal(Visibility.Collapsed, window.RestoreButton.Visibility);
            Assert.Equal(Visibility.Collapsed, window.EmptyTrashButton.Visibility);

            window.ShowListing(Trash, [File(TrashedFile, 1024)]);
            Select(window, TrashedFile);
            Assert.Equal(Visibility.Visible, window.RestoreButton.Visibility);
            Assert.True(window.RestoreButton.IsEnabled);
            Assert.Equal(Visibility.Visible, window.EmptyTrashButton.Visibility);
        });
    }

    [Fact]
    public async Task Nothing_moves_before_the_user_agrees_and_the_question_says_the_bytes_stay()
    {
        await WithWindow(async (window, standIn) =>
        {
            var asked = new List<string>();
            window.ConfirmRecycle = targets =>
            {
                asked.Add(CloudRecycleBin.ConfirmFor(targets));
                return false;
            };

            var result = await window.RecycleAsync([Entry(window, FileA)]);

            Assert.Equal(CloudRecycleOutcome.Declined, result.Outcome);
            var question = Assert.Single(asked);
            Assert.Contains("不会释放空间", question, StringComparison.Ordinal);
            Assert.Contains(CloudRecycleBin.FolderName, question, StringComparison.Ordinal);
            Assert.Contains("名称 (1)", question, StringComparison.Ordinal);
            Assert.Empty(standIn.FileMoves);
            Assert.Empty(standIn.DirectoryMoves);
            // A refused recycle must not be mistaken for the delete it replaces.
            Assert.False(standIn.Sent("operations/deletefile"), "拒绝移到回收站不该向云端发出删除");
            Assert.Contains("已取消，云端没有任何东西被移动", window.StatusText.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_recycled_file_travels_by_one_server_side_move_and_is_never_deleted_or_mkdir_ed()
    {
        await WithWindow(async (window, standIn) =>
        {
            window.ConfirmRecycle = _ => true;

            var result = await window.RecycleAsync([Entry(window, FileA)]);

            Assert.Equal(CloudRecycleOutcome.Moved, result.Outcome);
            var move = Assert.Single(standIn.FileMoves);
            Assert.Equal(("nas:", "nas:"), (move.SourceFs, move.TargetFs));
            Assert.Equal((FileA, TrashedFile), (move.SourceRemote, move.TargetRemote));
            // Soft: nothing here takes bytes away, and the trash folder needs no mkdir because both move legs
            // create the parents they need.
            Assert.Empty(standIn.Deleted);
            Assert.Empty(standIn.CreatedDirectories);
            Assert.Contains("已把 1 项移入", window.StatusText.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_recycled_folder_goes_through_the_directory_move_rather_than_a_file_move_that_would_strand_its_contents()
    {
        await WithWindow(async (window, standIn) =>
        {
            window.ConfirmRecycle = _ => true;
            window.ShowListing(Root, [Folder($"{Root}/reports")]);

            var result = await window.RecycleAsync([Entry(window, $"{Root}/reports")]);

            Assert.Equal(CloudRecycleOutcome.Moved, result.Outcome);
            // v1.75.0 refuses to movefile a directory at all, and a plain file move would leave its contents
            // behind, so the folder travels through the sync engine's directory move.
            var move = Assert.Single(standIn.DirectoryMoves);
            Assert.Equal($"nas:{Root}/reports", move.SourceFs);
            Assert.Equal($"nas:{Trash}/reports", move.TargetFs);
            Assert.Empty(standIn.FileMoves);
        });
    }

    [Fact]
    public async Task A_directory_move_that_merged_leaves_an_empty_husk_and_the_browser_removes_it()
    {
        // Measured on v1.75.0: a directory move onto a destination that already exists merges and leaves the
        // source sitting there empty, because --delete-empty-src-dirs never removes the source root. Left alone,
        // the user's folder would still be on screen with nothing in it.
        await WithWindow(async (window, standIn) =>
        {
            window.ConfirmRecycle = _ => true;
            window.ShowListing(Root, [Folder($"{Root}/reports")]);

            await window.RecycleAsync([Entry(window, $"{Root}/reports")]);

            Assert.Contains($"{Root}/reports", standIn.Statted, StringComparer.Ordinal);
            Assert.Equal([$"{Root}/reports"], standIn.RemovedDirectories);
        }, new FakeRclone { MovesLeaveTheirSource = true });
    }

    [Fact]
    public async Task A_directory_move_that_took_the_source_with_it_is_not_answered_by_a_delete()
    {
        // The other half of the same measurement: onto a fresh path the move renames and the source is gone, so
        // removing it anyway would be a call against a path that is not there — and rmdir answers that with an
        // HTTP 500 the shared classifier reads as a temporary failure.
        await WithWindow(async (window, standIn) =>
        {
            window.ConfirmRecycle = _ => true;
            window.ShowListing(Root, [Folder($"{Root}/reports")]);

            await window.RecycleAsync([Entry(window, $"{Root}/reports")]);

            Assert.Contains($"{Root}/reports", standIn.Statted, StringComparer.Ordinal);
            Assert.Empty(standIn.RemovedDirectories);
        });
    }

    [Fact]
    public async Task A_name_the_trash_already_holds_is_not_written_over_but_lands_beside_it_and_is_named()
    {
        // Both move legs overwrite a destination that exists, so the landing name is chosen free from that
        // directory's own listing before anything moves.
        await WithWindow(async (window, standIn) =>
        {
            window.ConfirmRecycle = _ => true;

            var result = await window.RecycleAsync([Entry(window, FileA)]);

            var move = Assert.Single(standIn.FileMoves);
            Assert.Equal($"{Trash}/a (1).txt", move.TargetRemote);
            Assert.True(result.Placed[0].Renamed);
            Assert.Contains("改存为 a (1).txt", window.StatusText.Text, StringComparison.Ordinal);
        }, new FakeRclone { Directories = Tree(TrashedFile) });
    }

    [Fact]
    public async Task A_restore_returns_to_the_directory_the_row_came_from_and_says_where_it_landed()
    {
        // The mirror is the restore's whole mechanism: the row carries its own origin in its path, so a
        // directory the user deleted in the meantime is not a problem — the move creates it again.
        await WithWindow(async (window, standIn) =>
        {
            window.ShowListing($"{Trash}/reports", [File(TrashedReport, 1024)]);

            var result = await window.RestoreAsync([Entry(window, TrashedReport)]);

            Assert.Equal(CloudRecycleOutcome.Restored, result.Outcome);
            var move = Assert.Single(standIn.FileMoves);
            Assert.Equal((TrashedReport, $"{Root}/reports/a.pdf"), (move.SourceRemote, move.TargetRemote));
            Assert.Contains($"已回到 {Root}/reports/a.pdf", window.StatusText.Text, StringComparison.Ordinal);
            // A restore is not a delete.
            Assert.Empty(standIn.RemovedDirectories);
        });
    }

    [Fact]
    public async Task A_restore_whose_original_name_is_taken_yields_to_it_and_reports_the_name_the_row_actually_has()
    {
        await WithWindow(async (window, standIn) =>
        {
            window.ShowListing(Trash, [File(TrashedFile, 1024)]);

            var result = await window.RestoreAsync([Entry(window, TrashedFile)]);

            Assert.True(result.Placed[0].Renamed);
            Assert.Equal($"{Root}/a (1).txt", Assert.Single(standIn.FileMoves).TargetRemote);
            Assert.Contains("a (1).txt", window.StatusText.Text, StringComparison.Ordinal);
        }, new FakeRclone { Directories = Tree($"{Root}/a.txt") });
    }

    [Fact]
    public async Task A_row_that_was_never_recycled_has_no_place_to_return_to_and_moves_nothing()
    {
        await WithWindow(async (window, standIn) =>
        {
            var result = await window.RestoreAsync([Entry(window, FileA)]);

            Assert.Equal(CloudRecycleOutcome.NotInTrash, result.Outcome);
            Assert.Empty(standIn.FileMoves);
            Assert.Contains("没有可还原的来源", window.StatusText.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Rows_already_in_the_trash_are_not_shuffled_deeper_and_the_trash_folder_itself_is_refused()
    {
        await WithWindow(async (window, standIn) =>
        {
            window.ConfirmRecycle = _ => true;
            window.ShowListing(Trash, [File(TrashedFile, 1024)]);
            var inside = await window.RecycleAsync([Entry(window, TrashedFile)]);

            Assert.Equal(CloudRecycleOutcome.InsideTrash, inside.Outcome);

            window.ShowListing(Root, [File(FileA, 1024), Folder(Trash)]);
            var folder = await window.RecycleAsync([Entry(window, Trash)]);

            Assert.Equal(CloudRecycleOutcome.IsTrashFolder, folder.Outcome);
            Assert.Empty(standIn.FileMoves);
            Assert.Contains("回收站文件夹本身不能移进回收站", window.StatusText.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_batch_that_only_half_worked_is_said_as_a_half_work()
    {
        await WithWindow(async (window, standIn) =>
        {
            window.ConfirmRecycle = _ => true;
            window.ShowListing(Root, [File(FileA, 1024), File(FileB, 2048)]);

            var result = await window.RecycleAsync([Entry(window, FileA), Entry(window, FileB)]);

            Assert.Equal(CloudRecycleOutcome.Moved, result.Outcome);
            Assert.Equal(1, result.Failed);
            // The row the cloud refused is the one that never moved; the other one did.
            Assert.Equal(FileB, Assert.Single(standIn.FileMoves).SourceRemote);
            Assert.Contains("1 项没有成功", window.StatusText.Text, StringComparison.Ordinal);
        }, new FakeRclone { RefusesMoveOf = FileA });
    }

    [Fact]
    public async Task The_row_menu_offers_each_move_command_only_to_the_selection_that_can_use_it()
    {
        await WithWindow((window, _) =>
        {
            Select(window, FileA);
            var outside = Headers(window.BuildRowMenu()!);
            Assert.Contains("移到回收站…", outside);
            Assert.DoesNotContain("还原…", outside);
            Assert.DoesNotContain("清空回收站…", outside);

            window.ShowListing(Trash, [File(TrashedFile, 1024)]);
            Select(window, TrashedFile);
            var inside = Headers(window.BuildRowMenu()!);
            Assert.Contains("还原…", inside);
            Assert.Contains("清空回收站…", inside);
            Assert.DoesNotContain("移到回收站…", inside);
        });
    }

    [Fact]
    public async Task Emptying_the_trash_is_the_delete_question_with_one_more_line_and_the_same_threshold()
    {
        await WithWindow(async (window, standIn) =>
        {
            var asked = new List<string>();
            window.ConfirmDelete = message =>
            {
                asked.Add(message);
                return true;
            };

            await window.EmptyTrashAsync();

            // One question for three items, and it is CloudDeleteSafety's own text plus the trash's line rather
            // than a second rule written for this button.
            var question = Assert.Single(asked);
            Assert.Contains($"这些项目已经在 {CloudRecycleBin.FolderName} 里", question, StringComparison.Ordinal);
            Assert.Contains("没有回收站，删除后无法恢复", question, StringComparison.Ordinal);
            Assert.Equal(3, standIn.Deleted.Count);
            Assert.Contains("已删除 3 项", window.StatusText.Text, StringComparison.Ordinal);
        }, new FakeRclone { Directories = Tree(TrashedFile, $"{Trash}/b.txt", $"{Trash}/c.txt") });
    }

    [Fact]
    public async Task A_full_trash_still_answers_the_bulk_question_before_anything_is_deleted()
    {
        // Eleven is one past CloudDeleteSafety.BulkThreshold. A trash is exactly where the biggest pile the app
        // removes at once sits, so the second acknowledgement has to survive here.
        var trashed = Enumerable.Range(1, 11).Select(index => $"f{index}.txt").ToList();
        await WithWindow(async (window, standIn) =>
        {
            var asked = 0;
            window.ConfirmDelete = _ =>
            {
                asked++;
                return true;
            };

            await window.EmptyTrashAsync();

            Assert.Equal(2, asked);
            Assert.Equal(11, standIn.Deleted.Count);
        }, new FakeRclone { Directories = Tree([.. trashed.Select(name => Trash + "/" + name)]) });
    }

    [Fact]
    public async Task A_trash_that_was_never_used_is_empty_rather_than_an_error()
    {
        // The folder does not exist until the first object moves into it, and v1.75.0 answers a missing
        // directory with 404 "directory not found" while an existing empty one answers {"list": []} — so the two
        // shapes have to be told apart, and "not there yet" is not an error.
        await WithWindow(async (window, standIn) =>
        {
            await window.EmptyTrashAsync();

            Assert.Empty(standIn.Deleted);
            Assert.Contains("回收站是空的", window.StatusText.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Opening_the_trash_creates_it_on_the_way_in_so_a_first_click_never_reads_as_a_broken_feature()
    {
        await WithWindow(async (window, standIn) =>
        {
            await window.OpenTrashAsync();

            Assert.Equal([Trash], standIn.CreatedDirectories);
            Assert.Equal(Trash, window.CurrentPath);
            Assert.Empty(window.VisibleEntries);
            Assert.False(standIn.Sent("operations/rmdir"), "打开回收站不该删除任何目录");
        });
    }

    // ------------------------------------------------------------------ harness

    /// <summary>The row the window is holding for that path, so a test acts on what is on screen.</summary>
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

    /// <summary>
    /// The tree a stand-in models, written as the full paths of the files it holds: every file's directory
    /// exists and holds exactly the files given here. A directory not listed does not exist, which is the state
    /// a first-ever recycle meets.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Tree(params string[] paths)
    {
        var directories = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var directory = CloudFileManagerService.Parent(path);
            if (!directories.TryGetValue(directory, out var names))
            {
                names = [];
                directories[directory] = names;
            }
            names.Add(CloudFileManagerService.NameOf(path));
        }
        return directories.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<string>)entry.Value);
    }

    private static CloudFileEntry File(string path, long size) =>
        new(path.Split('/').Last(), path, false, size, new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero));

    private static CloudFileEntry Folder(string path) =>
        new(path.Split('/').Last(), path, true, 0, null);

    private static CloudEndpointRow Row(string name, string root) =>
        new(new CloudEndpointAccount(new RcloneAccount(name, "sftp"), new CloudEndpointMetadata(name, "sftp", "SFTP", root, "")));

    /// <summary>
    /// Runs one action against the real browser window over a stand-in rclone. The window is never shown, so its
    /// <c>Loaded</c> handler — which would read the application's own settings — is not reached, and every RC call
    /// is recorded. The listing is installed directly because a recycle starts from rows already on screen; the
    /// stand-in then answers the reads and the moves those commands make.
    /// </summary>
    private static Task WithWindow(Func<RemoteEndpointManagerWindow, FakeRclone, Task> test,
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
            window.LoadEndpointsForTest([Row("nas", Root)], selectName: "nas");
            window.ShowListing(Root, [File(FileA, 1024), File(FileB, 2048)]);
            await test(window, standIn);
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
    private static Task WithWindow(Action<RemoteEndpointManagerWindow, FakeRclone> test, FakeRclone? rclone = null) =>
        WithWindow((window, standIn) =>
        {
            test(window, standIn);
            return Task.CompletedTask;
        }, rclone);

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
        var path = Path.Combine(Path.GetTempPath(), "fengsync-browser-recycle-tests", Guid.NewGuid().ToString("N"));
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
    /// flight. The dispatcher is then shut down explicitly, because the process allows only one WPF
    /// <c>Application</c>. A run that stops making progress is cut off by a watchdog.
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
                failure = new TimeoutException("云端回收站测试没有在规定时间内完成（窗口线程被卡住了）。");
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

    /// <summary>One recorded file move: the two fs/path pairs the leg carries.</summary>
    private sealed record Call(string SourceFs, string SourceRemote, string TargetFs, string TargetRemote);

    /// <summary>
    /// A stand-in for rclone's RC endpoint that models what the bundled v1.75.0 was measured to do around a move:
    /// <c>operations/list</c> answers 404 "directory not found" for a directory that is not there and
    /// <code>{"list": []}</code> for one that is empty and was created; <c>operations/movefile</c> takes the two
    /// path pairs; <c>sync/move</c> is an async job polled through <c>job/status</c>; and <c>operations/stat</c>
    /// answers <c>{"item": null}</c> for anything missing, which is the guard the husk cleanup is built on.
    /// </summary>
    private sealed class FakeRclone : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<Call> _fileMoves = [];
        private readonly List<(string SourceFs, string TargetFs)> _directoryMoves = [];
        private readonly List<string> _deleted = [];
        private readonly List<string> _removed = [];
        private readonly List<string> _created = [];
        private readonly List<string> _statted = [];
        private readonly List<string> _operations = [];
        private readonly HashSet<string> _made = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The files each directory holds, keyed by trimmed directory path. A directory missing here does not
        /// exist, which is the state a first-ever recycle meets.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Directories { get; init; } =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// When true, a directory move merged into a destination that already exists and left the source sitting
        /// there empty, so <c>operations/stat</c> still reports it and the caller has to remove it.
        /// </summary>
        public bool MovesLeaveTheirSource { get; init; }

        /// <summary>A source path the backend refuses to move, the way it refuses a move onto a directory.</summary>
        public string? RefusesMoveOf { get; init; }

        public IReadOnlyList<Call> FileMoves
        {
            get { lock (_gate) return _fileMoves.ToArray(); }
        }
        public IReadOnlyList<(string SourceFs, string TargetFs)> DirectoryMoves
        {
            get { lock (_gate) return _directoryMoves.ToArray(); }
        }
        public IReadOnlyList<string> Deleted
        {
            get { lock (_gate) return _deleted.ToArray(); }
        }
        public IReadOnlyList<string> RemovedDirectories
        {
            get { lock (_gate) return _removed.ToArray(); }
        }
        public IReadOnlyList<string> CreatedDirectories
        {
            get { lock (_gate) return _created.ToArray(); }
        }
        public IReadOnlyList<string> Statted
        {
            get { lock (_gate) return _statted.ToArray(); }
        }

        public bool Sent(string operation)
        {
            lock (_gate) return _operations.Contains(operation, StringComparer.Ordinal);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var operation = request.RequestUri!.AbsolutePath.TrimStart('/');
            using var document = body.Length == 0 ? null : JsonDocument.Parse(body);
            var root = document?.RootElement ?? default;
            string answer;
            HttpStatusCode status = HttpStatusCode.OK;
            switch (operation)
            {
                case "operations/list":
                    (answer, status) = List(Read(root, "remote"));
                    break;
                case "operations/stat":
                    answer = Stat(Read(root, "remote"));
                    break;
                case "operations/movefile":
                    (answer, status) = MoveFile(body);
                    break;
                case "sync/move":
                    lock (_gate) _directoryMoves.Add((Read(root, "srcFs"), Read(root, "dstFs")));
                    answer = "{\"jobid\":1}";
                    break;
                case "job/status":
                    answer = """{"finished":true,"success":true,"output":{}}""";
                    break;
                case "operations/mkdir":
                    lock (_gate)
                    {
                        var created = Read(root, "remote");
                        _created.Add(created);
                        _made.Add(created);
                    }
                    answer = "{}";
                    break;
                case "operations/rmdir":
                    lock (_gate) _removed.Add(Read(root, "remote"));
                    answer = "{}";
                    break;
                case "operations/deletefile":
                case "operations/purge":
                    lock (_gate) _deleted.Add(Read(root, "remote"));
                    answer = "{}";
                    break;
                default:
                    answer = "{}";
                    break;
            }
            lock (_gate) _operations.Add(operation);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(answer, Encoding.UTF8,
                    status == HttpStatusCode.OK ? "application/json" : "text/plain")
            };
        }

        /// <summary>A directory the model does not hold does not exist, and mkdir is what makes one exist.</summary>
        private (string, HttpStatusCode) List(string remote)
        {
            var directory = remote.Trim('/');
            if (Directories.TryGetValue(directory, out var names)) return (Listed(names), HttpStatusCode.OK);
            lock (_gate)
            {
                if (_made.Contains(directory)) return ("""{"list":[]}""", HttpStatusCode.OK);
            }
            return ("""{"error":"sftp: directory not found","input":{},"path":"","status":404}""", HttpStatusCode.NotFound);
        }

        private string Stat(string remote)
        {
            var path = remote.Trim('/');
            lock (_gate) _statted.Add(path);
            if (!MovesLeaveTheirSource) return """{"item":null}""";
            // Anything the model does not hold is answered as missing, which is exactly how v1.75.0 reports it:
            // an empty item rather than an error. A merge leaves the source as a directory that is still there.
            return "{\"item\":{\"Path\":\"" + path + "\",\"Name\":\"" + CloudFileManagerService.NameOf(path)
                + "\",\"IsDir\":true,\"Size\":0,\"MimeType\":\"inode/directory\"}}";
        }

        private (string, HttpStatusCode) MoveFile(string body)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var source = Read(root, "srcRemote");
            if (RefusesMoveOf is not null && string.Equals(source, RefusesMoveOf, StringComparison.OrdinalIgnoreCase))
                // v1.75.0's own wording for the destination-is-a-directory refusal, which the browser has to
                // carry as a per-row failure rather than as a finished batch.
                return ("""{"error":"Failed to copy: is a directory not a file","input":{},"path":"","status":500}""",
                    HttpStatusCode.InternalServerError);
            lock (_gate)
            {
                _fileMoves.Add(new Call(Read(root, "srcFs"), source, Read(root, "dstFs"), Read(root, "dstRemote")));
                // Both legs create the parents they need, and the model has to say so or the browser's next
                // listing would report a directory that the move just made.
                _made.Add(CloudFileManagerService.Parent(Read(root, "dstRemote")));
            }
            return ("{}", HttpStatusCode.OK);
        }

        private static string Listed(IReadOnlyList<string> names) =>
            $$"""{"list":[{{string.Join(",", names.Select(name => $$"""{"Path":"{{name}}","Name":"{{name}}","IsDir":false,"Size":1}"""))}}]}""";

        private static string Read(JsonElement root, string name) =>
            root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)
                ? ""
                : value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }
}
