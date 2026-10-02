using System.IO;
using System.Windows;
using FengSync.Services;
using FengSync.Views;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// Covers the cloud manager's drag-in/drag-out additions and the transfer queue's user-visible state.
/// The queue's actual byte movement needs a live rclone, so these tests pin the parts that must not
/// regress silently: drop-payload mapping, the drag cache, progress math, and journal-backed continuation.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudTransferBehaviorTests
{
    [Fact]
    public void Loose_files_dropped_onto_the_browser_upload_into_the_current_directory()
    {
        var root = NewDirectory();
        try
        {
            var first = Path.Combine(root, "report.pdf");
            var second = Path.Combine(root, "notes.txt");
            File.WriteAllText(first, "a");
            File.WriteAllText(second, "b");

            var items = CloudDropPlanner.Expand([first, second], "team/docs");

            Assert.Equal(new[] { "team/docs/report.pdf", "team/docs/notes.txt" }, items.Select(item => item.RemotePath));
            Assert.Equal(new[] { first, second }, items.Select(item => item.LocalPath));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void A_dropped_folder_keeps_its_name_and_relative_structure()
    {
        var root = NewDirectory();
        try
        {
            var folder = Path.Combine(root, "docs");
            Directory.CreateDirectory(Path.Combine(folder, "sub"));
            File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
            File.WriteAllText(Path.Combine(folder, "sub", "b.txt"), "b");

            var items = CloudDropPlanner.Expand([folder], "team");

            // A trailing separator must not turn the folder name into an empty segment.
            Assert.Equal(new[] { "team/docs/a.txt", "team/docs/sub/b.txt" }, items.Select(item => item.RemotePath));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void A_drop_at_the_remote_root_does_not_leave_a_leading_slash()
    {
        var root = NewDirectory();
        try
        {
            var file = Path.Combine(root, "a.txt");
            File.WriteAllText(file, "a");

            var items = CloudDropPlanner.Expand([file, Path.Combine(root, "missing.txt"), ""], "");

            var item = Assert.Single(items);
            Assert.Equal("a.txt", item.RemotePath);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void A_mixed_drop_keeps_loose_files_beside_a_dropped_folder()
    {
        var root = NewDirectory();
        try
        {
            var folder = Path.Combine(root, "docs");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "inside.txt"), "i");
            var loose = Path.Combine(root, "loose.txt");
            File.WriteAllText(loose, "l");

            var items = CloudDropPlanner.Expand([folder, loose], "team");

            // A dropped folder keeps its own name as a subdirectory while a loose file lands directly in
            // the current directory; one must not be flattened into the other.
            Assert.Equal(new[] { "team/docs/inside.txt", "team/loose.txt" }, items.Select(item => item.RemotePath));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void A_dropped_folder_that_holds_no_files_contributes_nothing()
    {
        var root = NewDirectory();
        try
        {
            var folder = Path.Combine(root, "empty");
            Directory.CreateDirectory(Path.Combine(folder, "nested"));

            // Only files are uploadable, so an empty tree yields an empty batch — which is what makes the
            // manager report "没有可上传的文件" instead of opening the transfer window for nothing.
            Assert.Empty(CloudDropPlanner.Expand([folder], "team"));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void Join_normalizes_separators_without_doubling_them()
    {
        Assert.Equal("a/b", CloudDropPlanner.Join("a", "/b/"));
        Assert.Equal("a", CloudDropPlanner.Join("a", ""));
        Assert.Equal("b", CloudDropPlanner.Join("", "b"));
        Assert.Equal("a/b/c", CloudDropPlanner.Join("/a/", "b/c"));
    }

    [Fact]
    public void The_drag_cache_keeps_one_local_copy_per_remote_file_and_rejects_stale_bytes()
    {
        var local = CloudDragCache.LocalPathFor("archive", "team/docs/report.pdf");

        Assert.StartsWith(Path.Combine(CloudDragCache.Root, "archive"), local, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("team", "docs", "report.pdf"), local, StringComparison.OrdinalIgnoreCase);

        // A cached copy is only reused when its length still matches the cloud listing.
        var root = NewDirectory();
        try
        {
            var file = Path.Combine(root, "report.pdf");
            File.WriteAllText(file, "12345");
            Assert.True(CloudDragCache.IsUsable(file, 5));
            Assert.False(CloudDragCache.IsUsable(file, 6));
            Assert.False(CloudDragCache.IsUsable(Path.Combine(root, "absent.pdf"), 5));
            Assert.False(CloudDragCache.IsUsable(file, 0));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void A_transfer_task_reports_progress_cancellability_and_resumability()
    {
        var upload = new CloudTransferTask(CloudTransferDirection.Upload, "s3://archive", "docs/report.pdf", @"C:\tmp\report.pdf", 400)
        {
            CompletedBytes = 100
        };

        Assert.Equal(25, upload.Percentage);
        Assert.Equal("上传 · report.pdf", upload.Display);
        Assert.Equal("等待中", upload.StatusText);
        Assert.True(upload.CanCancel);
        Assert.False(upload.IsResumable);

        upload.State = CloudTransferState.Running;
        Assert.Contains("25", upload.StatusText, StringComparison.Ordinal);

        upload.State = CloudTransferState.Cancelled;
        Assert.False(upload.CanCancel);
        Assert.True(upload.IsResumable);
        Assert.True(upload.IsFinished);

        // A zero-length source must not divide by zero.
        Assert.Equal(0, new CloudTransferTask(CloudTransferDirection.Download, "s3://archive", "a.txt", @"C:\tmp\a.txt", 0).Percentage);

        // A backend can report more bytes than the listing predicted; the bar must stay in range.
        var overshoot = new CloudTransferTask(CloudTransferDirection.Upload, "s3://archive", "a.txt", @"C:\tmp\a.txt", 100) { CompletedBytes = 250 };
        Assert.Equal(100, overshoot.Percentage);
    }

    [Fact]
    public async Task The_queue_reloads_unfinished_transfers_from_its_journal_as_continuable()
    {
        var root = NewDirectory();
        try
        {
            var journal = new CloudTransferJournalStore(root);
            var cancelled = new CloudTransferRecord(Guid.NewGuid(), CloudTransferDirection.Upload, "s3://archive", "docs/report.pdf", @"C:\tmp\report.pdf", 400, CloudTransferState.Cancelled, null);
            var failed = new CloudTransferRecord(Guid.NewGuid(), CloudTransferDirection.Download, "s3://archive", "docs/photo.jpg", @"C:\tmp\photo.jpg", 800, CloudTransferState.Failed, "网络中断");
            var completed = new CloudTransferRecord(Guid.NewGuid(), CloudTransferDirection.Upload, "s3://archive", "done.txt", @"C:\tmp\done.txt", 10, CloudTransferState.Completed, null);
            await journal.SaveAsync(cancelled);
            await journal.SaveAsync(failed);
            await journal.SaveAsync(completed);

            var queue = new CloudTransferQueue(journal);
            var loaded = await queue.LoadResumableAsync();

            Assert.Equal(2, loaded);
            Assert.DoesNotContain(queue.Tasks, task => task.Id == completed.Id);
            Assert.True(queue.HasResumableTasks);
            Assert.True(queue.HasTasks);
            Assert.Equal(2, queue.Tasks.Count(task => task.State == CloudTransferState.Cancelled));
            Assert.Contains("可续传 2", queue.SummaryText, StringComparison.Ordinal);
            // A restored task carries its persisted identity so a later save replaces the same entry.
            Assert.Contains(queue.Tasks, task => task.Id == failed.Id && task.Error == "网络中断");
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void Cancelling_a_pending_transfer_marks_it_continuable_and_clears_completed_entries()
    {
        var root = NewDirectory();
        try
        {
            var queue = new CloudTransferQueue(new CloudTransferJournalStore(root));
            var pending = new CloudTransferTask(CloudTransferDirection.Upload, "s3://archive", "docs/a.txt", @"C:\tmp\a.txt", 100);
            var finished = new CloudTransferTask(CloudTransferDirection.Upload, "s3://archive", "docs/b.txt", @"C:\tmp\b.txt", 100)
            {
                State = CloudTransferState.Completed
            };
            queue.Tasks.Add(pending);
            queue.Tasks.Add(finished);

            queue.Cancel(pending);
            Assert.Equal(CloudTransferState.Cancelled, pending.State);

            queue.ClearFinished();
            Assert.DoesNotContain(finished, queue.Tasks);
            Assert.Same(pending, Assert.Single(queue.Tasks));

            // The cancelled task stays continuable, which is what the transfer window's resume acts on.
            Assert.True(queue.HasResumableTasks);
            Assert.False(queue.HasCompletedTasks);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void The_queue_reports_aggregate_progress_for_the_manager_progress_bar()
    {
        var root = NewDirectory();
        try
        {
            var queue = new CloudTransferQueue(new CloudTransferJournalStore(root));
            Assert.Equal(0, queue.OverallPercentage);

            queue.Tasks.Add(new CloudTransferTask(CloudTransferDirection.Upload, "s3://archive", "a.txt", @"C:\tmp\a.txt", 200)
            {
                State = CloudTransferState.Running,
                CompletedBytes = 50
            });
            queue.Tasks.Add(new CloudTransferTask(CloudTransferDirection.Upload, "s3://archive", "b.txt", @"C:\tmp\b.txt", 200)
            {
                State = CloudTransferState.Running,
                CompletedBytes = 150
            });

            // 25% and 75% in flight average to 50%; finished work is not averaged back in.
            Assert.Equal(50, queue.OverallPercentage);

            queue.Tasks.Add(new CloudTransferTask(CloudTransferDirection.Upload, "s3://archive", "c.txt", @"C:\tmp\c.txt", 200)
            {
                State = CloudTransferState.Completed
            });
            Assert.Equal(50, queue.OverallPercentage);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void The_transfer_window_lists_the_shared_queue_and_gates_its_buttons()
    {
        RunSta(() =>
        {
            var application = Application.Current ?? new Application();
            if (application.Resources.MergedDictionaries.Count == 0)
                foreach (var resource in new[] { "DesignTokens.xaml", "Typography.xaml", "Icons.xaml", "Controls.xaml" })
                    application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/FengSync;component/Themes/{resource}") });

            var root = NewDirectory();
            try
            {
                var queue = new CloudTransferQueue(new CloudTransferJournalStore(root));
                var window = new CloudTransferWindow(queue);

                Assert.Same(queue.Tasks, window.TaskList.ItemsSource);
                Assert.Equal(queue.SummaryText, window.SummaryText.Text);
                // Nothing is resumable or finished yet, so only the explanatory hint applies.
                Assert.False(window.ResumeButton.IsEnabled);
                Assert.False(window.CancelAllButton.IsEnabled);
                Assert.False(window.ClearButton.IsEnabled);
                Assert.Contains("拖", window.HintText.Text, StringComparison.Ordinal);

                // The queue is application-owned, so the window must follow changes it did not make.
                queue.Tasks.Add(new CloudTransferTask(CloudTransferDirection.Upload, "s3://archive", "a.txt", @"C:\tmp\a.txt", 100)
                {
                    State = CloudTransferState.Cancelled
                });
                Assert.Contains("可续传 1", window.SummaryText.Text, StringComparison.Ordinal);
                Assert.True(window.ResumeButton.IsEnabled);
                // A cancelled task is continuable, not clearable, so the clear action stays off.
                Assert.False(window.ClearButton.IsEnabled);

                window.Close();
            }
            finally { TryDelete(root); }
        });
    }

    [Theory]
    [InlineData("docs/report.pdf", "docs")]
    [InlineData("docs/nested/report.pdf", "docs/nested")]
    [InlineData("report.pdf", "")]
    [InlineData("/docs/report.pdf", "docs")]
    [InlineData(@"docs\report.pdf", "docs")]
    [InlineData("docs", "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    public void The_upload_path_helper_derives_the_destination_directory(string path, string expected)
    {
        // Conflict listing and directory creation both address this value, so a folder drop must not list
        // or create the wrong directory.
        Assert.Equal(expected, CloudTransferQueue.ParentOf(path));
    }

    [Fact]
    public async Task Waiting_on_a_finished_transfer_reports_whether_it_succeeded()
    {
        var root = NewDirectory();
        try
        {
            var queue = new CloudTransferQueue(new CloudTransferJournalStore(root));

            // Drag-out and "open in the default application" both block on this, and neither may treat a
            // cancelled or failed transfer as a file that is ready on disk.
            Assert.True(await queue.WaitForAsync(Frozen(CloudTransferState.Completed)));
            Assert.False(await queue.WaitForAsync(Frozen(CloudTransferState.Cancelled)));
            Assert.False(await queue.WaitForAsync(Frozen(CloudTransferState.Failed)));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void Resuming_reports_nothing_to_do_when_no_transfer_is_continuable()
    {
        var root = NewDirectory();
        try
        {
            var queue = new CloudTransferQueue(new CloudTransferJournalStore(root));
            Assert.Equal(0, queue.ResumeFinished());

            var completed = new CloudTransferTask(CloudTransferDirection.Upload, "s3://archive", "a.txt", @"C:\tmp\a.txt", 10)
            {
                State = CloudTransferState.Completed
            };
            queue.Tasks.Add(completed);

            // The transfer window shows its "没有可断点续传的任务" hint on a zero, so a completed transfer
            // must never be counted as something the continue action can restart.
            Assert.Equal(0, queue.ResumeFinished());
            Assert.False(queue.HasResumableTasks);
        }
        finally { TryDelete(root); }
    }

    private static CloudTransferTask Frozen(CloudTransferState state)
    {
        var task = new CloudTransferTask(CloudTransferDirection.Download, "s3://archive", "docs/a.txt", @"C:\tmp\a.txt", 10)
        {
            State = state
        };
        return task;
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fengsync-cloud-tests", Guid.NewGuid().ToString("N"));
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
