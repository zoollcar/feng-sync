using System.IO;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FengSync.Core;
using FengSync.Core.Mount;
using FengSync.Core.Rclone.Lifecycle;
using FengSync.Services;
using FengSync.Views;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// The file browser's keyboard. Which keystroke means which command, and which keystrokes a text field keeps, is
/// pinned by the pure table at the top of this file; what belongs here is the two things a table cannot show —
/// that the handler really sits on the browser pane (a keystroke raised there arrives, and the same keystroke
/// raised in the endpoint list does not), and that every command it dispatches is the command the toolbar button
/// already runs, so F5/Del/Shift+Del/F2/Ctrl+A/C/X/V inherit the refusals, the questions and the delete threshold
/// instead of growing a second, cheaper copy of them. Every case runs against a stand-in rclone that answers the
/// way the bundled v1.75.0 was measured to answer, so no daemon starts and no credential exists.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudBrowserShortcutTests
{
    /// <summary>The root folder of the endpoint the window is showing.</summary>
    private const string Root = "team";

    private const string Trash = Root + "/.fengsync-trash";

    private const string FileA = Root + "/a.txt";

    private const string FileB = Root + "/b.txt";

    private const string FileC = Root + "/c.pdf";

    private const string Docs = Root + "/docs";

    private const string TrashedFile = Trash + "/a.txt";

    // ------------------------------------------------------------------ the table

    [Fact]
    public void The_table_gives_one_command_per_keystroke_and_leaves_everything_else_alone()
    {
        Assert.Equal(CloudBrowserShortcut.Refresh, CloudBrowserShortcuts.Resolve(Key.F5, false, false, false));
        Assert.Equal(CloudBrowserShortcut.Recycle, CloudBrowserShortcuts.Resolve(Key.Delete, false, false, false));
        Assert.Equal(CloudBrowserShortcut.Rename, CloudBrowserShortcuts.Resolve(Key.F2, false, false, false));
        Assert.Equal(CloudBrowserShortcut.SelectAll, CloudBrowserShortcuts.Resolve(Key.A, true, false, false));
        Assert.Equal(CloudBrowserShortcut.Copy, CloudBrowserShortcuts.Resolve(Key.C, true, false, false));
        Assert.Equal(CloudBrowserShortcut.Cut, CloudBrowserShortcuts.Resolve(Key.X, true, false, false));
        Assert.Equal(CloudBrowserShortcut.Paste, CloudBrowserShortcuts.Resolve(Key.V, true, false, false));

        // The list's type-to-select keystroke survives: a bare letter is not a command, or typing "d" to jump to
        // a folder would silently become something else.
        Assert.Null(CloudBrowserShortcuts.Resolve(Key.A, false, false, false));
        Assert.Null(CloudBrowserShortcuts.Resolve(Key.C, false, false, false));
        Assert.Null(CloudBrowserShortcuts.Resolve(Key.Z, false, false, false));
        Assert.Null(CloudBrowserShortcuts.Resolve(Key.Enter, false, false, false));
        // Keys this app has no browser command for stay with whoever asked for them.
        Assert.Null(CloudBrowserShortcuts.Resolve(Key.Z, true, false, false));
        Assert.Null(CloudBrowserShortcuts.Resolve(Key.F4, false, false, false));
    }

    [Fact]
    public void Delete_is_the_one_keystroke_whose_two_other_spellings_mean_the_irreversible_command()
    {
        Assert.Equal(CloudBrowserShortcut.DeleteForever, CloudBrowserShortcuts.Resolve(Key.Delete, false, true, false));
        Assert.Equal(CloudBrowserShortcut.DeleteForever, CloudBrowserShortcuts.Resolve(Key.Delete, true, false, false));
        // Shift only distinguishes Delete's two commands, so a stray Shift cannot turn a clipboard keystroke
        // into nothing at all.
        Assert.Equal(CloudBrowserShortcut.Copy, CloudBrowserShortcuts.Resolve(Key.C, true, true, false));
        Assert.Equal(CloudBrowserShortcut.SelectAll, CloudBrowserShortcuts.Resolve(Key.A, true, true, false));
    }

    [Fact]
    public void A_text_field_keeps_every_editing_keystroke_and_only_the_refresh_leaves_it()
    {
        // The gate is checked after F5 and before everything else, so no modifier combination can reach the
        // list commands from a field — which is also why Ctrl+A never needs the TextBox to decline first.
        Assert.Equal(CloudBrowserShortcut.Refresh, CloudBrowserShortcuts.Resolve(Key.F5, false, false, true));
        Assert.Equal(CloudBrowserShortcut.Refresh, CloudBrowserShortcuts.Resolve(Key.F5, true, true, true));
        Assert.Null(CloudBrowserShortcuts.Resolve(Key.Delete, false, false, true));
        Assert.Null(CloudBrowserShortcuts.Resolve(Key.Delete, true, false, true));
        Assert.Null(CloudBrowserShortcuts.Resolve(Key.Delete, false, true, true));
        Assert.Null(CloudBrowserShortcuts.Resolve(Key.F2, false, false, true));
        Assert.Null(CloudBrowserShortcuts.Resolve(Key.A, true, false, true));
        Assert.Null(CloudBrowserShortcuts.Resolve(Key.C, true, false, true));
        Assert.Null(CloudBrowserShortcuts.Resolve(Key.X, true, false, true));
        Assert.Null(CloudBrowserShortcuts.Resolve(Key.V, true, false, true));
    }

    // ------------------------------------------------------------------ the pane receives them

    [Fact]
    public async Task F5_on_the_pane_rereads_the_directory_and_the_rows_on_screen_are_the_clouds_answer()
    {
        await WithWindow(async (window, standIn) =>
        {
            // A row the cloud does not hold, so a listing that replaced it is proof the read really happened.
            window.ShowListing(Root, [File($"{Root}/stale.txt", 1)]);

            RaiseKey(window.FileList, Key.F5);
            await WaitUntil(() => window.VisibleEntries.Count == 4, "F5 没有重新读取目录");

            Assert.DoesNotContain(window.VisibleEntries, entry => entry.Name == "stale.txt");
            Assert.Equal(FileA, window.VisibleEntries.First(entry => entry.Name == "a.txt").Path);
            Assert.True(standIn.Sent("operations/list"));
        });
    }

    [Fact]
    public async Task F2_on_the_pane_renames_the_row_that_is_selected_and_only_that_row()
    {
        await WithWindow(async (window, standIn) =>
        {
            var asked = new List<string>();
            window.PromptRename = entry =>
            {
                asked.Add(entry.Path);
                return "renamed.txt";
            };
            Select(window, FileA);

            var args = RaiseKey(window.FileList, Key.F2);
            await WaitUntil(() => standIn.FileMoves.Count == 1, "F2 没有走出重命名");
            // Read after the handler has run, not right after raising it: FileBrowser_KeyDown claims the key on
            // the continuation that follows `await HandleKeyAsync(...)`, so the whole command — the move, the
            // re-listing and the visit it records — has to finish first. Waiting for the move alone used to be
            // enough by luck; a keystroke that is still in flight is not "unclaimed".
            await WaitUntil(() => args.Handled, "F2 走完命令后没有被认领");

            Assert.True(args.Handled);
            Assert.Equal([FileA], asked);
            var move = Assert.Single(standIn.FileMoves);
            // A rename stays in its own directory; only the last segment changes.
            Assert.Equal((FileA, $"{Root}/renamed.txt"), (move.SourceRemote, move.TargetRemote));
            Assert.False(standIn.Sent("operations/deletefile"), "重命名不该删除任何东西");
        });
    }

    [Fact]
    public async Task A_keystroke_the_table_does_not_own_is_left_unhandled_so_the_list_keeps_using_it()
    {
        await WithWindow((window, standIn) =>
        {
            Select(window, FileA);

            var args = RaiseKey(window.FileList, Key.Z);

            // Marking an unowned keystroke handled would be how a shortcut feature quietly breaks the control it
            // sits on — here the list's own type-to-select.
            Assert.False(args.Handled);
            Assert.False(standIn.AnythingSent());
        });
    }

    [Fact]
    public async Task The_address_bar_keeps_its_own_editing_keystrokes_and_still_answers_the_refresh()
    {
        await WithWindow(async (window, standIn) =>
        {
            var opened = false;
            window.PromptRename = _ =>
            {
                opened = true;
                return null;
            };
            Select(window, FileA);

            // Two different lines of defence, and this case shows both are needed: the TextBox consumes Delete
            // itself, so the keystroke never reaches the pane at all — while F2 has no binding in a TextBox, so
            // it does bubble, and only the typing gate is left to decline it. Either way the browser sends
            // nothing, and the text the user is typing keeps its edit.
            var deleted = RaiseKey(window.AddressBox, Key.Delete);
            Assert.True(deleted.Handled);

            RaiseKey(window.AddressBox, Key.F2);

            Assert.False(opened);
            Assert.False(standIn.AnythingSent());

            window.ShowListing(Root, [File($"{Root}/stale.txt", 1)]);
            RaiseKey(window.AddressBox, Key.F5);
            await WaitUntil(() => window.VisibleEntries.Count == 4, "在地址栏里按 F5 没有刷新目录");
        });
    }

    [Fact]
    public async Task A_keystroke_in_the_endpoint_list_reaches_no_browser_command_at_all()
    {
        await WithWindow((window, _) =>
        {
            // The browser's shortcuts are scoped to the browser pane. F2 is the probe because it needs no
            // modifier: were the handler on the window rather than on the pane, this would open a rename.
            var opened = false;
            window.PromptRename = _ =>
            {
                opened = true;
                return null;
            };
            Select(window, FileA);

            var args = RaiseKey(window.EndpointList, Key.F2);

            Assert.False(args.Handled);
            Assert.False(opened);
        });
    }

    // ------------------------------------------------------------------ what each command does

    [Fact]
    public async Task Del_moves_the_selection_to_the_trash_and_is_never_answered_by_a_delete()
    {
        await WithWindow(async (window, standIn) =>
        {
            window.ConfirmRecycle = _ => true;
            Select(window, FileA, FileB);

            Assert.True(await window.HandleKeyAsync(Key.Delete, false, false, typing: false));

            Assert.Equal([FileA, FileB], standIn.FileMoves.Select(move => move.SourceRemote));
            Assert.Equal([Trash + "/a.txt", Trash + "/b.txt"], standIn.FileMoves.Select(move => move.TargetRemote));
            Assert.Empty(standIn.Deleted);
            Assert.Contains("已把 2 项移入", window.StatusText.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Del_with_nothing_selected_names_the_command_it_was_asked_for()
    {
        await WithWindow(async (window, standIn) =>
        {
            var recycleAsked = false;
            window.ConfirmRecycle = _ => recycleAsked = true;

            await window.HandleKeyAsync(Key.Delete, false, false, typing: false);

            // The empty-selection sentence belongs to the keystroke that was pressed, not to a generic "nothing
            // selected": a user who meant 删除 has to learn they asked for 移到回收站.
            Assert.Equal("请先选择要移到回收站的项目。", window.StatusText.Text);
            Assert.False(recycleAsked);
            Assert.Empty(standIn.FileMoves);
        });
    }

    [Fact]
    public async Task Shift_del_and_ctrl_del_ask_the_delete_question_once_and_take_the_bytes_away()
    {
        foreach (var (ctrl, shift) in new[] { (false, true), (true, false) })
        {
            var asked = new List<string>();
            await WithWindow(async (window, standIn) =>
            {
                window.ConfirmDelete = message =>
                {
                    asked.Add(message);
                    return true;
                };
                Select(window, FileA, FileB, FileC);

                await window.HandleKeyAsync(Key.Delete, ctrl, shift, typing: false);

                var question = Assert.Single(asked);
                Assert.Contains("确定删除以下 3 项", question, StringComparison.Ordinal);
                Assert.Contains("该端点没有回收站", question, StringComparison.Ordinal);
                Assert.Equal([FileA, FileB, FileC], standIn.Deleted);
                Assert.Empty(standIn.FileMoves);
                Assert.Contains("已删除 3 项", window.StatusText.Text, StringComparison.Ordinal);
            });
        }
    }

    [Fact]
    public async Task The_row_menu_the_button_and_the_keyboard_ask_one_delete_question_in_a_trash_view()
    {
        await WithWindow(async (window, _) =>
        {
            var asked = new List<string>();
            window.ConfirmDelete = message =>
            {
                asked.Add(message);
                return false;
            };
            window.ShowListing(Trash, [File(TrashedFile, 1024)]);
            Select(window, TrashedFile);

            // Three routes to the same command, so the trash's extra line must not be a feature of only one of
            // them: a row on screen in a trash view is trash's own content whoever clicked or pressed, and the
            // net the user is throwing away is the fact the confirmation has to carry.
            var menu = window.BuildRowMenu()!;
            var item = menu.Items.OfType<MenuItem>()
                .Single(entry => entry.Header?.ToString()?.StartsWith("删除") == true);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntil(() => asked.Count == 1, "行菜单的「删除」没有问出问题");

            await window.HandleKeyAsync(Key.Delete, false, true, typing: false);

            Assert.Equal(2, asked.Count);
            Assert.All(asked, question =>
                Assert.Contains($"这些项目已经在 {CloudRecycleBin.FolderName} 里", question, StringComparison.Ordinal));
            Assert.All(asked, question => Assert.Contains("确定删除以下 1 项", question, StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Del_standing_in_the_trash_says_the_row_is_already_there_instead_of_moving_it_deeper()
    {
        await WithWindow(async (window, standIn) =>
        {
            var asked = false;
            window.ConfirmRecycle = _ =>
            {
                asked = true;
                return true;
            };
            window.ShowListing(Trash, [File(TrashedFile, 1024)]);
            Select(window, TrashedFile);

            await window.HandleKeyAsync(Key.Delete, false, false, typing: false);

            // The keyboard inherits the refusal the button already had, so it asks nothing and moves nothing.
            Assert.False(asked);
            Assert.Empty(standIn.FileMoves);
            Assert.Contains("已经在回收站里", window.StatusText.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task F2_with_more_than_one_row_selected_opens_no_question()
    {
        await WithWindow(async (window, _) =>
        {
            var opened = false;
            window.PromptRename = _ =>
            {
                opened = true;
                return "ignored";
            };
            Select(window, FileA, FileB);

            await window.HandleKeyAsync(Key.F2, false, false, typing: false);

            Assert.False(opened);
            Assert.Equal("请先选择要重命名的单个项目。", window.StatusText.Text);
        });
    }

    [Fact]
    public async Task Ctrl_a_selects_every_row_the_filter_left_on_screen()
    {
        await WithWindow(async (window, _) =>
        {
            window.SearchBox.Text = ".";

            Assert.Equal(3, window.VisibleEntries.Count);
            Assert.Equal("显示 3 / 4 项", window.ViewSummary.Text);

            Assert.True(await window.HandleKeyAsync(Key.A, true, false, typing: false));

            // Ctrl+A that ignored an active filter would select rows the user cannot see and then delete them
            // from under the search box.
            Assert.Equal(3, window.Selection.Count);
            Assert.Equal([.. window.VisibleEntries.Select(entry => entry.Path).Order()],
                [.. window.Selection.Select(entry => entry.Path).Order()]);
            Assert.True(window.DeleteEntryButton.IsEnabled);
            Assert.True(window.RecycleButton.IsEnabled);
        });
    }

    [Fact]
    public async Task Ctrl_a_then_Ctrl_c_copies_the_whole_visible_selection_into_the_one_clipboard()
    {
        await WithWindow(async (window, _) =>
        {
            await window.HandleKeyAsync(Key.A, true, false, typing: false);
            await window.HandleKeyAsync(Key.C, true, false, typing: false);

            Assert.Equal(4, window.ClipboardItems.Count);
            Assert.Equal(CloudClipboardMode.Copy, window.ClipboardMode);
            Assert.Contains("已复制 4 项", window.StatusText.Text, StringComparison.Ordinal);
            // The clipboard the paste button reads is the same object the keystroke filled: one clipboard, not
            // a keyboard-shaped second one.
            Assert.True(window.PasteButton.IsEnabled);
        });
    }

    [Fact]
    public async Task Ctrl_x_promises_the_delete_a_paste_ends_in()
    {
        await WithWindow(async (window, _) =>
        {
            Select(window, FileA);

            await window.HandleKeyAsync(Key.X, true, false, typing: false);

            Assert.Equal(CloudClipboardMode.Cut, window.ClipboardMode);
            var held = Assert.Single(window.ClipboardItems);
            Assert.Equal(FileA, held.Entry.Path);
            Assert.Equal("nas", held.Source.Name);
            Assert.Contains("已剪切 1 项", window.StatusText.Text, StringComparison.Ordinal);
            Assert.Contains("无法恢复", window.StatusText.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Ctrl_v_with_an_empty_clipboard_says_what_is_missing()
    {
        await WithWindow(async (window, standIn) =>
        {
            await window.HandleKeyAsync(Key.V, true, false, typing: false);

            Assert.Contains("剪贴板是空的", window.StatusText.Text, StringComparison.Ordinal);
            Assert.False(standIn.Sent("operations/copyto"), "空剪贴板的 Ctrl+V 不该向云端复制任何东西");
        });
    }

    [Fact]
    public async Task Ctrl_v_after_Ctrl_c_of_a_folder_pastes_nothing_and_says_why_in_the_same_sentence()
    {
        await WithWindow(async (window, standIn) =>
        {
            Select(window, Docs);
            await window.HandleKeyAsync(Key.C, true, false, typing: false);
            Assert.True(window.PasteButton.IsEnabled);

            await window.HandleKeyAsync(Key.V, true, false, typing: false);

            // The paste path ran: it read the destination, found nothing it may copy, and said the reason the
            // button gives rather than a keyboard-specific one.
            Assert.Contains("没有可粘贴的项目", window.StatusText.Text, StringComparison.Ordinal);
            Assert.Contains("没有被复制", window.StatusText.Text, StringComparison.Ordinal);
            Assert.True(standIn.Sent("operations/list"));
            Assert.False(standIn.Sent("operations/copyto"), "文件夹不该被粘贴进队列");
        });
    }

    // ------------------------------------------------------------------ harness

    /// <summary>
    /// Sends a real bubbling KeyDown from inside the browser pane, the way the keyboard would, and hands back the
    /// event so a test can read whether the pane claimed it. A <see cref="KeyEventArgs"/> cannot be made without a
    /// <see cref="PresentationSource"/>, so a hidden zero-size one is built for the call and released straight
    /// after; routing is finished by then. The physical keyboard's modifier state is deliberately not part of
    /// these cases — a raised event asks the handler to read <see cref="Keyboard.Modifiers"/>, which no test can
    /// pin down — so only keystrokes whose meaning cannot change with a modifier are raised here, and
    /// <see cref="RemoteEndpointManagerWindow.HandleKeyAsync"/> carries the rest with the facts handed in.
    /// </summary>
    private static KeyEventArgs RaiseKey(FrameworkElement target, Key key)
    {
        var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters());
        try
        {
            var args = new KeyEventArgs(InputManager.Current.PrimaryKeyboardDevice, source, 0, key)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            };
            target.RaiseEvent(args);
            return args;
        }
        finally { source.Dispose(); }
    }

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

    private static CloudEndpointRow Row(string name, string root) =>
        new(new CloudEndpointAccount(new RcloneAccount(name, "sftp"), new CloudEndpointMetadata(name, "sftp", "SFTP", root, "")));

    /// <summary>One name in the modelled tree, and whether the cloud reports it as a directory.</summary>
    private sealed record Node(string Path, bool IsDirectory);

    private static Node FileNode(string path) => new(path, false);

    private static Node DirNode(string path) => new(path, true);

    /// <summary>
    /// The tree a stand-in models, written as the paths of the nodes it holds and grouped by the directory each
    /// one sits in. A directory not present here does not exist, which is the state a first-ever recycle meets.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<Node>> Tree(params Node[] nodes)
    {
        var directories = new Dictionary<string, List<Node>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            var directory = CloudFileManagerService.Parent(node.Path);
            if (!directories.TryGetValue(directory, out var held)) directories[directory] = held = [];
            held.Add(node);
        }
        return directories.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<Node>)entry.Value);
    }

    /// <summary>
    /// Runs one action against the real browser window over a stand-in rclone. The window is never shown, so its
    /// <c>Loaded</c> handler — which would read the application's own settings — is not reached. The directory on
    /// screen starts as four rows, and the stand-in's tree holds those same four names, so a refresh returns what
    /// was already there rather than an empty folder.
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
        var standIn = rclone ?? new FakeRclone { Directories = Tree(FileNode(FileA), FileNode(FileB),
            FileNode(FileC), DirNode(Docs)) };
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
            window.ShowListing(Root, [File(FileA, 1024), File(FileB, 2048), File(FileC, 512), Folder(Docs)]);
            await test(window, standIn);
        }
        finally
        {
            AppServices.Install(previous);
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", previousDataDir);
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

    /// <summary>Polls until the pane has taken the keystroke in, pumping the dispatcher while it waits.</summary>
    private static async Task WaitUntil(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"超时：{message}");
            await Task.Delay(25);
        }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fengsync-browser-shortcut-tests", Guid.NewGuid().ToString("N"));
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
                failure = new TimeoutException("云端快捷键测试没有在规定时间内完成（窗口线程被卡住了）。");
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

    /// <summary>One recorded file move: the two fs/path pairs the leg carries.</summary>
    private sealed record Call(string SourceFs, string SourceRemote, string TargetFs, string TargetRemote);

    /// <summary>
    /// A stand-in for rclone's RC endpoint that answers the way the bundled v1.75.0 was measured to: a directory
    /// the model does not hold is a 404 "directory not found", an existing empty one is <code>{"list": []}</code>,
    /// <c>operations/movefile</c> carries the two path pairs and creates its destination parents,
    /// <c>operations/stat</c> answers <c>{"item": null}</c> for anything missing, and a delete is a plain
    /// <c>operations/deletefile</c> or <c>operations/purge</c>. Everything is recorded, so a test can say that a
    /// keystroke which should not touch the cloud did not.
    /// </summary>
    private sealed class FakeRclone : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<Call> _fileMoves = [];
        private readonly List<string> _deleted = [];
        private readonly List<string> _operations = [];
        private readonly HashSet<string> _made = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The files and folders each modelled directory holds, keyed by trimmed directory path.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<Node>> Directories { get; init; } =
            new Dictionary<string, IReadOnlyList<Node>>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<Call> FileMoves
        {
            get { lock (_gate) return _fileMoves.ToArray(); }
        }
        public IReadOnlyList<string> Deleted
        {
            get { lock (_gate) return _deleted.ToArray(); }
        }

        public bool Sent(string operation)
        {
            lock (_gate) return _operations.Contains(operation, StringComparer.Ordinal);
        }

        /// <summary>True when the keystroke under test sent any RC call at all.</summary>
        public bool AnythingSent()
        {
            lock (_gate) return _operations.Count > 0;
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
                    answer = """{"item":null}""";
                    break;
                case "operations/movefile":
                    answer = MoveFile(body);
                    break;
                case "sync/move":
                    answer = "{\"jobid\":1}";
                    break;
                case "job/status":
                    answer = """{"finished":true,"success":true,"output":{}}""";
                    break;
                case "operations/mkdir":
                    // v1.75.0's mkdir creates missing parents and is idempotent, so the path now exists.
                    lock (_gate) _made.Add(Read(root, "remote"));
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

        private (string, HttpStatusCode) List(string remote)
        {
            var directory = remote.Trim('/');
            if (Directories.TryGetValue(directory, out var nodes)) return (Listed(nodes), HttpStatusCode.OK);
            lock (_gate)
            {
                if (_made.Contains(directory)) return ("""{"list":[]}""", HttpStatusCode.OK);
            }
            return ("""{"error":"sftp: directory not found","input":{},"path":"","status":404}""", HttpStatusCode.NotFound);
        }

        private string MoveFile(string body)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var destination = Read(root, "dstRemote");
            lock (_gate)
            {
                _fileMoves.Add(new Call(Read(root, "srcFs"), Read(root, "srcRemote"), Read(root, "dstFs"), destination));
                // The leg creates the parents it needs, and the model has to say so or the browser's next
                // listing would report a directory the move just made.
                _made.Add(CloudFileManagerService.Parent(destination));
            }
            return "{}";
        }

        private static string Listed(IReadOnlyList<Node> nodes) =>
            "{\"list\":[" + string.Join(",", nodes.Select(Item)) + "]}";

        /// <summary>One node in the shape v1.75.0 answers <c>operations/list</c> with: a path relative to the
        /// remote root, the same shape the browser puts in <see cref="CloudFileEntry.Path"/>.</summary>
        private static string Item(Node node) =>
            "{\"Path\":\"" + node.Path + "\",\"Name\":\"" + CloudFileManagerService.NameOf(node.Path)
            + "\",\"IsDir\":" + (node.IsDirectory ? "true" : "false")
            + ",\"Size\":" + (node.IsDirectory ? 0 : 4096)
            + ",\"ModTime\":\"2026-03-01T08:00:00Z\"}";

        private static string Read(JsonElement root, string name) =>
            root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)
                ? ""
                : value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }
}
