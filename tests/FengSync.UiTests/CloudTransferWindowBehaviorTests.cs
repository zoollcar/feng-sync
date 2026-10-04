using System.IO;
using System.Windows;
using System.Windows.Controls;
using FengSync.Services;
using FengSync.Views;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// The standalone transfer window (独立传输窗口) is the only surface that owns per-task progress,
/// cancellation and 断点续传, and the queue behind it is application-owned and outlives the window. These
/// tests drive the window's actions in-process, so "the buttons do what they say, and the window stops
/// following a queue it no longer shows" is checked without a desktop session or a live rclone.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudTransferWindowBehaviorTests
{
    [Fact]
    public void An_empty_window_offers_nothing_to_continue_cancel_or_clear()
    {
        WithWindow((window, _) =>
        {
            Assert.False(window.ResumeButton.IsEnabled);
            Assert.False(window.CancelAllButton.IsEnabled);
            Assert.False(window.ClearButton.IsEnabled);
            // Nothing else on this window tells a first-time user how a transfer starts.
            Assert.Contains("拖入", window.HintText.Text, StringComparison.Ordinal);
            Assert.Contains("拖到", window.HintText.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Continuing_with_nothing_to_continue_says_so_instead_of_doing_nothing()
    {
        WithWindow((window, _) =>
        {
            // The action is disabled while the queue is empty, so this drives the handler directly to pin
            // the message the user sees if the queue drained between the last layout pass and the click.
            window.ResumeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal("当前没有可断点续传的任务。", window.HintText.Text);
        });
    }

    [Fact]
    public void Cancelling_everything_turns_the_continue_action_on()
    {
        WithWindow((window, queue) =>
        {
            queue.Tasks.Add(Pending("docs/a.txt"));
            queue.Tasks.Add(Pending("docs/b.txt"));

            // "全部取消" is gated on a transfer actually holding a slot, not on the list being non-empty:
            // a task that is still waiting for one can never be the only thing left to cancel. Both rows
            // are still individually cancellable, which is what the per-row button binds to.
            Assert.False(window.CancelAllButton.IsEnabled);
            Assert.All(queue.Tasks, task => Assert.True(task.CanCancel));

            // Drives the same handler the button does, so the queue transition and the state the window
            // re-renders afterwards are both covered without a live rclone.
            window.CancelAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.All(queue.Tasks, task => Assert.Equal(CloudTransferState.Cancelled, task.State));
            // Cancelling is not clearing: the two transfers become the user's queue of things to continue.
            Assert.False(window.CancelAllButton.IsEnabled);
            Assert.True(window.ResumeButton.IsEnabled);
            Assert.False(window.ClearButton.IsEnabled);
            Assert.Contains("可续传 2", window.SummaryText.Text, StringComparison.Ordinal);
            // The hint has to state the boundary too: a download continues, a cloud upload restarts.
            Assert.Contains("断点", window.HintText.Text, StringComparison.Ordinal);
            Assert.Contains("重新开始", window.HintText.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Clearing_removes_only_completed_transfers()
    {
        WithWindow((window, queue) =>
        {
            var completed = Pending("docs/done.txt");
            completed.State = CloudTransferState.Completed;
            var cancelled = Pending("docs/stopped.txt");
            cancelled.State = CloudTransferState.Cancelled;
            queue.Tasks.Add(completed);
            queue.Tasks.Add(cancelled);
            Assert.True(window.ClearButton.IsEnabled);

            window.ClearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            // A cancelled transfer is the one thing the user still has to act on, so it must survive.
            Assert.Same(cancelled, Assert.Single(queue.Tasks));
            Assert.False(window.ClearButton.IsEnabled);
            Assert.True(window.ResumeButton.IsEnabled);
            Assert.Contains("可续传 1", window.SummaryText.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_queued_copy_is_announced_as_one_that_cannot_be_continued()
    {
        WithWindow((window, queue) =>
        {
            queue.Tasks.Add(new CloudTransferTask(CloudTransferDirection.Upload, "s3://archive", "a.txt",
                @"C:\tmp\a.txt", 1024));
            // Without a copy in the list the window says nothing about one.
            Assert.DoesNotContain("云盘之间", window.HintText.Text, StringComparison.Ordinal);

            queue.Tasks.Add(new CloudTransferTask(CloudTransferDirection.Copy, "s3://archive", "a.txt",
                string.Empty, 1024, "sftp://files", "incoming/a.txt"));

            // A copy moves bytes between two remotes, so this program never holds them and has no verified
            // prefix to append to. The generic sentence above hints at that; the row's 重试 is where the user
            // finds out, which is why the hint has to say it first.
            Assert.Contains("云盘之间", window.HintText.Text, StringComparison.Ordinal);
            Assert.Contains("重新复制", window.HintText.Text, StringComparison.Ordinal);
            Assert.True(queue.HasCopyTasks);
        });
    }

    [Fact]
    public void A_closed_window_stops_following_the_shared_queue()
    {
        WithWindow((window, queue) =>
        {
            window.Close();
            var summary = window.SummaryText.Text;

            // The queue is application-owned, so another window can enqueue after this one closes. A window
            // that kept its subscriptions would leak and would fight the window that replaced it.
            queue.Tasks.Add(Pending("docs/after-close.txt"));

            Assert.Equal(summary, window.SummaryText.Text);
        });
    }

    private static CloudTransferTask Pending(string remotePath) =>
        new(CloudTransferDirection.Upload, "s3://archive", remotePath,
            Path.Combine(@"C:\tmp", CloudTransferTask.NameOf(remotePath)), 1024);

    /// <summary>
    /// Builds the window against an isolated journal root. The constructor alone is enough: it wires the
    /// shared queue, while the journal is only read on <c>Loaded</c>, which a window that is never shown
    /// does not reach — so the starting state is exactly "nothing queued yet".
    /// </summary>
    private static void WithWindow(Action<CloudTransferWindow, CloudTransferQueue> test)
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var root = NewDirectory();
            try
            {
                var queue = new CloudTransferQueue(new CloudTransferJournalStore(root));
                var window = new CloudTransferWindow(queue);
                try { test(window, queue); }
                // A test may close the window itself, and WPF rejects closing a closed window.
                finally { try { window.Close(); } catch (InvalidOperationException) { } }
            }
            finally { TryDelete(root); }
        });
    }

    /// <summary>The window relies on the theme dictionaries App.xaml merges; a test process has no App.</summary>
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

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fengsync-transfer-window-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>The queue persists asynchronously, so a journal write can still be in flight at teardown.</summary>
    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
