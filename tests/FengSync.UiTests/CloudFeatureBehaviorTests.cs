using System.IO;
using FengSync.Core;
using FengSync.Core.Configuration;
using FengSync.Core.Rclone.Diagnostics;
using FengSync.Services;
using FengSync.Views;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// Covers the cloud-drive features added after the gap analysis in
/// <c>docs/cloud-drive-feature-plan.md</c>: the browse/navigate rules, the file-operation safety rules,
/// the endpoint health classification, and the transfer queue's new pause/concurrency/retry state machine.
/// Every one of these is a decision that is easy to get subtly wrong and impossible to notice from the UI,
/// so they are pinned here rather than only exercised through the window.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudFeatureBehaviorTests : IDisposable
{
    // ---------------------------------------------------------------- browse: address normalisation

    [Fact]
    public void A_typed_address_cannot_escape_the_endpoint_root()
    {
        // ".." at the root is clamped rather than rejected: the endpoint root is a boundary the user
        // cannot cross anyway, and refusing the input would only be a worse way to say the same thing.
        Assert.Equal("team", CloudBrowse.Normalize("../..", "team"));
        // Walking above the root lands back on the root, not on whatever is above it.
        Assert.Equal("team", CloudBrowse.Normalize("team/docs/../../..", "team"));
        // With no configured root the floor is the remote root itself.
        Assert.Equal("", CloudBrowse.Normalize("team/../..", null));
        Assert.Equal("shared", CloudBrowse.Normalize("team/../../shared", null));
    }

    [Fact]
    public void A_typed_address_is_read_relative_to_the_endpoint_root_but_an_absolute_one_is_kept()
    {
        Assert.Equal("team/docs", CloudBrowse.Normalize("docs", "team"));
        Assert.Equal("team/docs", CloudBrowse.Normalize("team/docs", "team"));
        Assert.Equal("team", CloudBrowse.Normalize("", "team"));
    }

    [Fact]
    public void A_pasted_windows_path_and_a_dot_segment_both_normalise()
    {
        Assert.Equal("team/docs", CloudBrowse.Normalize(@"team\docs", null));
        Assert.Equal("team/docs", CloudBrowse.Normalize("./team/./docs/", null));
    }

    [Fact]
    public void The_breadcrumb_trail_always_starts_at_the_endpoint_root()
    {
        var trail = CloudBrowse.Breadcrumbs("team", "team/docs/reports", "归档");

        Assert.Equal(["归档", "docs", "reports"], trail.Select(crumb => crumb.Name));
        // The first crumb is the endpoint's own boundary, which is the one place the user cannot go above.
        Assert.Equal("team", trail[0].Path);
        Assert.Equal("team/docs", trail[1].Path);
        Assert.Equal("team/docs/reports", trail[2].Path);
    }

    [Fact]
    public void A_breadcrumb_trail_for_an_endpoint_with_no_root_is_just_the_endpoint()
    {
        var trail = CloudBrowse.Breadcrumbs(null, "", "我的网盘");

        Assert.Single(trail);
        Assert.Equal("我的网盘", trail[0].Name);
    }

    // ---------------------------------------------------------------- browse: ordering and filtering

    [Fact]
    public void Directories_stay_ahead_of_files_in_both_sort_directions()
    {
        var entries = new[] { FileEntry("a.txt", 10), FolderEntry("zeta"), FileEntry("b.txt", 20), FolderEntry("alpha") };

        var ascending = CloudBrowse.Sort(entries, CloudSortColumn.Name, descending: false);
        var descending = CloudBrowse.Sort(entries, CloudSortColumn.Name, descending: true);

        // Sorting a folder by size — always zero — would bury every folder under every file, so the
        // directory/file split is applied before the direction and the reversal only reorders within each.
        Assert.Equal(["alpha", "zeta", "a.txt", "b.txt"], ascending.Select(entry => entry.Name));
        Assert.Equal(["zeta", "alpha", "b.txt", "a.txt"], descending.Select(entry => entry.Name));
    }

    [Fact]
    public void Sorting_by_size_falls_back_to_the_name_for_equal_sizes()
    {
        var entries = new[] { FileEntry("b.txt", 10), FileEntry("a.txt", 10), FileEntry("c.txt", 30) };

        var ordered = CloudBrowse.Sort(entries, CloudSortColumn.Size, descending: false);

        Assert.Equal(["a.txt", "b.txt", "c.txt"], ordered.Select(entry => entry.Name));
    }

    [Fact]
    public void A_missing_timestamp_sorts_last_instead_of_pretending_to_be_1970()
    {
        var entries = new[]
        {
            FileEntry("unknown.txt", 1, modified: null),
            FileEntry("newer.txt", 1, modified: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero)),
            FileEntry("older.txt", 1, modified: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero))
        };

        var ordered = CloudBrowse.Sort(entries, CloudSortColumn.Modified, descending: false);

        Assert.Equal(["older.txt", "newer.txt", "unknown.txt"], ordered.Select(entry => entry.Name));
    }

    [Fact]
    public void Clearing_the_filter_box_restores_the_listing_instead_of_emptying_it()
    {
        var entries = new[] { FileEntry("report.pdf", 1), FileEntry("notes.txt", 1) };

        Assert.Equal(2, CloudBrowse.Filter(entries, "   ").Count);
        Assert.Single(CloudBrowse.Filter(entries, "REPORT"));
        Assert.Empty(CloudBrowse.Filter(entries, "missing"));
    }

    [Fact]
    public void A_search_matches_the_folder_part_of_a_path_and_not_only_the_file_name()
    {
        var nested = FileEntry("summary.txt", 1, path: "archive/2026/reports/summary.txt");

        // A user looking for "reports" under a large tree means the folder too.
        Assert.True(CloudBrowse.MatchesQuery(nested, "reports"));
        Assert.True(CloudBrowse.MatchesQuery(nested, "summary"));
        Assert.False(CloudBrowse.MatchesQuery(nested, "budget"));
    }

    [Fact]
    public void A_search_result_shows_its_full_path_while_a_plain_listing_shows_the_name()
    {
        var entry = FileEntry("summary.txt", 1, path: "archive/summary.txt");

        Assert.Equal("summary.txt", entry.NameDisplay);
        Assert.Equal("archive/summary.txt", (entry with { ShowFullPath = true }).NameDisplay);
    }

    // ---------------------------------------------------------------- file names

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" padded")]
    [InlineData("padded ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("a\tb")]
    public void A_name_that_rclone_would_read_as_a_path_is_rejected(string name)
    {
        // rclone treats a separator inside a name as a path, so an unvalidated name would silently create
        // the file somewhere else than the user pointed at.
        Assert.False(CloudFileNames.IsValid(name));
        Assert.NotNull(CloudFileNames.Validate(name));
    }

    [Fact]
    public void A_name_longer_than_the_common_backend_limit_is_rejected()
    {
        Assert.True(CloudFileNames.IsValid(new string('a', CloudFileNames.MaxLength)));
        Assert.False(CloudFileNames.IsValid(new string('a', CloudFileNames.MaxLength + 1)));
    }

    [Fact]
    public void An_accepted_name_reports_no_reason()
    {
        Assert.Null(CloudFileNames.Validate("季度报告 2026.pdf"));
        Assert.True(CloudFileNames.IsValid("季度报告 2026.pdf"));
    }

    [Fact]
    public void A_rename_stays_in_the_directory_the_entry_came_from()
    {
        // A rename is not a move, so the destination must be the original directory under the new name.
        Assert.Equal("archive/report.txt", CloudFileNames.RenameTarget("archive/old.txt", "report.txt"));
        Assert.Equal("report.txt", CloudFileNames.RenameTarget("old.txt", "report.txt"));
    }

    // ---------------------------------------------------------------- delete safety

    [Fact]
    public void Only_a_backend_with_a_trash_is_presented_as_recoverable()
    {
        Assert.True(CloudDeleteSafety.SupportsTrash(EndpointType.GoogleDrive));
        Assert.False(CloudDeleteSafety.SupportsTrash(EndpointType.Sftp));
        Assert.False(CloudDeleteSafety.SupportsTrash(EndpointType.S3));
    }

    [Fact]
    public void An_irreversible_delete_says_so_and_a_recoverable_one_does_not()
    {
        var entries = new[] { FileEntry("a.txt", 1) };

        var irreversible = CloudDeleteSafety.Describe(entries, supportsTrash: false);
        var recoverable = CloudDeleteSafety.Describe(entries, supportsTrash: true);

        Assert.True(irreversible.IsIrreversible);
        Assert.Contains("无法恢复", irreversible.Message, StringComparison.Ordinal);
        Assert.False(recoverable.IsIrreversible);
        Assert.Contains("回收站", recoverable.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_delete_warns_that_its_contents_go_with_it()
    {
        var confirmation = CloudDeleteSafety.Describe([FolderEntry("docs")], supportsTrash: false);

        // The count is of the things the user pointed at, not of everything that will disappear, so saying
        // so is the difference between an informed confirmation and a misleading one.
        Assert.Contains("连同其中的全部内容", confirmation.Message, StringComparison.Ordinal);
        Assert.Contains("文件夹 1 个", confirmation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_large_batch_is_flagged_for_a_second_acknowledgement()
    {
        var atThreshold = Enumerable.Range(0, CloudDeleteSafety.BulkThreshold).Select(index => FileEntry($"f{index}.txt", 1)).ToArray();
        var aboveThreshold = Enumerable.Range(0, CloudDeleteSafety.BulkThreshold + 1).Select(index => FileEntry($"f{index}.txt", 1)).ToArray();

        Assert.False(CloudDeleteSafety.Describe(atThreshold, supportsTrash: false).IsBulk);
        Assert.True(CloudDeleteSafety.Describe(aboveThreshold, supportsTrash: false).IsBulk);
        // A long list is previewed and the remainder summarised rather than printed in full.
        Assert.Contains("以及另外 3 项", CloudDeleteSafety.Describe(aboveThreshold, supportsTrash: false).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_upload_conflict_policy_leaves_the_question_open_only_when_it_says_ask()
    {
        Assert.Null(CloudDeleteSafety.OverwriteFor(CloudUploadConflictPolicy.Ask));
        Assert.True(CloudDeleteSafety.OverwriteFor(CloudUploadConflictPolicy.Overwrite));
        Assert.False(CloudDeleteSafety.OverwriteFor(CloudUploadConflictPolicy.Skip));
    }

    // ---------------------------------------------------------------- endpoint health

    [Fact]
    public void A_rejected_credential_is_reported_as_needing_a_new_login()
    {
        Assert.Equal(CloudEndpointHealth.NeedsLogin, CloudEndpointStatusService.Classify(Rclone(RcloneFailureCategory.Authentication)));
        Assert.Equal(CloudEndpointHealth.NeedsLogin, CloudEndpointStatusService.Classify(Rclone(RcloneFailureCategory.Permission)));
    }

    [Fact]
    public void A_plain_network_outage_is_not_reported_as_a_login_problem()
    {
        // Calling an outage "needs re-login" would send the user through a browser authorization that
        // cannot fix anything.
        Assert.Equal(CloudEndpointHealth.Unreachable, CloudEndpointStatusService.Classify(new IOException("connection reset by peer")));
        Assert.Equal(CloudEndpointHealth.Unreachable, CloudEndpointStatusService.Classify(Rclone(RcloneFailureCategory.Dns)));
    }

    [Fact]
    public void An_unclassified_error_that_mentions_an_expired_token_is_still_a_login_problem()
    {
        Assert.Equal(CloudEndpointHealth.NeedsLogin, CloudEndpointStatusService.ClassifyMessage("oauth2: cannot fetch token: invalid_grant"));
        Assert.Equal(CloudEndpointHealth.NeedsLogin, CloudEndpointStatusService.ClassifyMessage("401 Unauthorized"));
        Assert.Equal(CloudEndpointHealth.Unreachable, CloudEndpointStatusService.ClassifyMessage("dial tcp: i/o timeout"));
    }

    [Fact]
    public void A_backend_that_reports_no_quota_shows_nothing_extra()
    {
        // Most backends answer nothing at all, and an absent quota is "unknown", never "0 of 0".
        Assert.Equal("", new CloudEndpointStatus(CloudEndpointHealth.Connected, "").QuotaText);
        Assert.Equal("", new CloudEndpointStatus(CloudEndpointHealth.Connected, "", TotalBytes: 0).QuotaText);
        Assert.Equal("", CloudEndpointStatus.Unknown.QuotaText);
    }

    [Fact]
    public void A_reported_quota_is_summarised_in_readable_units()
    {
        var status = new CloudEndpointStatus(CloudEndpointHealth.Connected, "", 100L * 1024 * 1024 * 1024, 25L * 1024 * 1024 * 1024);

        Assert.Contains("已用", status.QuotaText, StringComparison.Ordinal);
        Assert.Contains("100", status.QuotaText, StringComparison.Ordinal);
        Assert.Contains("GB", status.QuotaText, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_health_state_reads_as_the_answer_the_user_asked_for()
    {
        Assert.Equal("需要重新登录", new CloudEndpointStatus(CloudEndpointHealth.NeedsLogin, "").HealthText);
        Assert.Equal("已连接", new CloudEndpointStatus(CloudEndpointHealth.Connected, "").HealthText);
        Assert.Equal("无法连接", new CloudEndpointStatus(CloudEndpointHealth.Unreachable, "").HealthText);
        Assert.Equal("未检查", CloudEndpointStatus.Unknown.HealthText);
    }

    // ---------------------------------------------------------------- transfer queue

    [Fact]
    public void The_concurrency_limit_cannot_be_driven_outside_its_supported_range()
    {
        var queue = NewQueue();

        queue.Concurrency = 0;
        Assert.Equal(CloudTransferQueue.MinimumConcurrency, queue.Concurrency);
        queue.Concurrency = 999;
        Assert.Equal(CloudTransferQueue.MaximumConcurrency, queue.Concurrency);
        queue.Concurrency = 4;
        Assert.Equal(4, queue.Concurrency);
    }

    [Fact]
    public void The_automatic_retry_count_cannot_be_driven_outside_its_supported_range()
    {
        var queue = NewQueue();

        queue.MaxRetries = -3;
        Assert.Equal(0, queue.MaxRetries);
        queue.MaxRetries = 99;
        Assert.Equal(10, queue.MaxRetries);
    }

    [Fact]
    public void A_failure_that_waiting_cannot_fix_is_not_retried()
    {
        // Retrying these would only delay the error the user needs to see — and for an expired login it
        // would delay it three times over.
        Assert.False(CloudTransferQueue.IsRetryable(new FileNotFoundException("gone", "a.txt")));
        Assert.False(CloudTransferQueue.IsRetryable(new DirectoryNotFoundException("gone")));
        Assert.False(CloudTransferQueue.IsRetryable(new UnauthorizedAccessException("denied")));
        Assert.False(CloudTransferQueue.IsRetryable(Rclone(RcloneFailureCategory.Authentication)));
        Assert.False(CloudTransferQueue.IsRetryable(Rclone(RcloneFailureCategory.NotFound)));

        Assert.True(CloudTransferQueue.IsRetryable(new IOException("connection reset")));
        Assert.True(CloudTransferQueue.IsRetryable(Rclone(RcloneFailureCategory.Temporary)));
        Assert.True(CloudTransferQueue.IsRetryable(Rclone(RcloneFailureCategory.RateLimit)));
    }

    [Fact]
    public void A_paused_transfer_is_distinct_from_a_cancelled_one_and_is_resumable()
    {
        var queue = NewQueue();
        var task = Add(queue, "docs/a.txt");

        queue.Pause(task);
        Assert.Equal(CloudTransferState.Paused, task.State);
        Assert.True(task.IsResumable);
        Assert.True(queue.HasPausedTasks);
        // A parked transfer is the newest way to be stopped, so it is offered by the continue action.
        Assert.True(queue.HasResumableTasks);
        Assert.Equal("继续", task.RestartText);

        queue.Cancel(task);
        Assert.Equal(CloudTransferState.Cancelled, task.State);
        // Cancelling is still something the user can undo, but it is not a pause.
        Assert.True(task.IsResumable);
        Assert.False(queue.HasPausedTasks);
        Assert.Equal("重试", task.RestartText);
    }

    [Fact]
    public void Pausing_everything_counts_only_the_transfers_that_can_be_paused()
    {
        var queue = NewQueue();
        var pending = Add(queue, "docs/a.txt");
        var completed = Add(queue, "docs/b.txt", CloudTransferState.Completed);

        Assert.Equal(1, queue.PauseAll());
        Assert.Equal(CloudTransferState.Paused, pending.State);
        Assert.Equal(CloudTransferState.Completed, completed.State);
    }

    [Fact]
    public void Clearing_finished_transfers_keeps_everything_the_user_can_still_continue()
    {
        var queue = NewQueue();
        var completed = Add(queue, "docs/done.txt", CloudTransferState.Completed);
        var cancelled = Add(queue, "docs/stopped.txt", CloudTransferState.Cancelled);
        var failed = Add(queue, "docs/failed.txt", CloudTransferState.Failed);

        // "清除已完成" must not silently drop the queue of things the user still means to finish.
        Assert.True(queue.HasCompletedTasks);
        queue.ClearFinished();

        Assert.Equal(["docs/stopped.txt", "docs/failed.txt"], queue.Tasks.Select(task => task.RemotePath));
        Assert.False(queue.HasCompletedTasks);
        Assert.NotNull(completed);
    }

    [Fact]
    public void A_queue_of_only_stopped_transfers_does_not_offer_to_clear_anything()
    {
        var queue = NewQueue();
        queue.Cancel(Add(queue, "docs/a.txt"));

        Assert.False(queue.HasCompletedTasks);
    }

    [Fact]
    public async Task A_restart_brings_back_an_interrupted_transfer_as_cancelled_and_a_parked_one_as_paused()
    {
        var root = NewDirectory();
        _roots.Add(root);
        // The journal root is removed when this test class is disposed.
        {
            var journal = new CloudTransferJournalStore(root);
            var interrupted = new CloudTransferRecord(Guid.NewGuid(), CloudTransferDirection.Download,
                "s3://archive", "docs/big.bin", @"C:\tmp\big.bin", 4096, CloudTransferState.Running, "connection reset");
            var parked = new CloudTransferRecord(Guid.NewGuid(), CloudTransferDirection.Upload,
                "s3://archive", "docs/parked.bin", @"C:\tmp\parked.bin", 4096, CloudTransferState.Paused, null);
            var done = new CloudTransferRecord(Guid.NewGuid(), CloudTransferDirection.Download,
                "s3://archive", "docs/done.bin", @"C:\tmp\done.bin", 4096, CloudTransferState.Completed, null);
            await journal.SaveAsync(interrupted);
            await journal.SaveAsync(parked);
            await journal.SaveAsync(done);

            var queue = new CloudTransferQueue(journal);
            var restored = await queue.LoadResumableAsync();

            // A finished transfer is not worth restoring; a running one is factually a cancelled one, while
            // a pause was a deliberate act that must survive the restart.
            Assert.Equal(2, restored);
            Assert.Equal(CloudTransferState.Cancelled, queue.Tasks.Single(task => task.Id == interrupted.Id).State);
            Assert.Equal(CloudTransferState.Paused, queue.Tasks.Single(task => task.Id == parked.Id).State);
            Assert.DoesNotContain(queue.Tasks, task => task.Id == done.Id);
            // Restoring lists the work; it deliberately does not start any network transfer on its own.
            Assert.False(queue.HasActiveTransfers);
        }
    }

    [Fact]
    public async Task A_restored_failure_keeps_its_full_diagnostic_not_just_the_one_line_message()
    {
        var root = NewDirectory();
        _roots.Add(root);
        var detail = string.Join(Environment.NewLine, "上下文：cloud-transfer", "操作：operations/copyfile", "诊断 ID：abc123");
        {
            var journal = new CloudTransferJournalStore(root);
            await journal.SaveAsync(new CloudTransferRecord(Guid.NewGuid(), CloudTransferDirection.Download,
                "s3://archive", "docs/big.bin", @"C:\tmp\big.bin", 4096, CloudTransferState.Failed, "网络中断", detail));

            var queue = new CloudTransferQueue(journal);
            Assert.Equal(1, await queue.LoadResumableAsync());

            // The short message is what the row shows; the detail is what a bug report needs, and a failure
            // noticed after a restart must still be diagnosable.
            var restored = Assert.Single(queue.Tasks);
            Assert.Equal("网络中断", restored.Error);
            Assert.Equal(detail, restored.ErrorDetail);
            Assert.True(restored.HasErrorDetail);
        }
    }

    [Fact]
    public async Task A_journal_written_before_the_detail_field_existed_still_loads()
    {
        var root = NewDirectory();
        _roots.Add(root);
        {
            var journal = new CloudTransferJournalStore(root);
            // Shape of a journal file produced by the previous schema: no errorDetail property at all.
            var id = Guid.NewGuid();
            await File.WriteAllTextAsync(Path.Combine(root, id + ".json"),
                $$"""{"Id":"{{id}}","Direction":1,"RemoteUri":"s3://archive","RemotePath":"docs/a.bin","LocalPath":"C:\\tmp\\a.bin","TotalBytes":10,"State":5,"Error":"failed"}""");

            var queue = new CloudTransferQueue(journal);
            Assert.Equal(1, await queue.LoadResumableAsync());

            var restored = Assert.Single(queue.Tasks);
            Assert.Equal("failed", restored.Error);
            Assert.Null(restored.ErrorDetail);
            Assert.False(restored.HasErrorDetail);
        }
    }

    [Fact]
    public void The_copyable_diagnostic_carries_what_a_log_search_needs()
    {
        var failure = new RcloneFailure(RcloneFailureCategory.Authentication, false, "operations/list", 401, null,
            "授权已过期", "请重新登录", "invalid_grant", """{"remote":"root"}""", "diag-77");
        var text = RcloneUiError.DescribeDetail(new RcloneException(failure), "cloud-transfer");

        // Without the correlation id and the operation the log line cannot be found again.
        Assert.Contains("diag-77", text);
        Assert.Contains("operations/list", text);
        Assert.Contains("Authentication", text);
        Assert.Contains("cloud-transfer", text);
        Assert.Contains("401", text);
    }

    [Fact]
    public void The_copyable_diagnostic_never_echoes_a_credential()
    {
        var text = RcloneUiError.DescribeDetail(new InvalidOperationException("connect failed for password=hunter2"));

        Assert.DoesNotContain("hunter2", text);
    }

    [Fact]
    public async Task Restoring_the_same_journal_twice_does_not_duplicate_a_row()
    {
        var root = NewDirectory();
        _roots.Add(root);
        // The journal root is removed when this test class is disposed.
        {
            var journal = new CloudTransferJournalStore(root);
            await journal.SaveAsync(new CloudTransferRecord(Guid.NewGuid(), CloudTransferDirection.Download,
                "s3://archive", "docs/big.bin", @"C:\tmp\big.bin", 4096, CloudTransferState.Running, null));

            var queue = new CloudTransferQueue(journal);
            Assert.Equal(1, await queue.LoadResumableAsync());
            Assert.Equal(0, await queue.LoadResumableAsync());
            Assert.Single(queue.Tasks);
        }
    }

    [Fact]
    public void A_queue_with_nothing_left_to_do_reports_a_full_progress_bar()
    {
        Assert.Equal(0, NewQueue().OverallPercentage);

        // A parked transfer's bytes are not done, so a queue that is merely paused must not read as 100%.
        var paused = NewQueue();
        var parked = Add(paused, "docs/a.txt");
        paused.Pause(parked);
        Assert.Equal(0, paused.OverallPercentage);

        // Only a batch that actually drained reports the bar as full.
        var drained = NewQueue();
        Add(drained, "docs/b.txt", CloudTransferState.Completed);
        Assert.Equal(100, drained.OverallPercentage);
    }

    // ---------------------------------------------------------------- completion reporting

    [Fact]
    public void A_batch_the_user_stopped_themselves_reports_nothing()
    {
        // A notification that fires on the user's own action is noise.
        Assert.Equal("", CloudTransferSummary.Describe(new CloudTransferBatch(3, 0, 0, 3)));
        Assert.Equal("", CloudTransferSummary.Describe(new CloudTransferBatch(0, 0, 0, 0)));
    }

    [Fact]
    public void A_batch_that_finished_reports_how_many()
    {
        Assert.Equal("云盘传输完成：2 项。", CloudTransferSummary.Describe(new CloudTransferBatch(2, 2, 0, 0)));
    }

    [Fact]
    public void A_mixed_batch_reports_success_failure_and_stops_separately()
    {
        var message = CloudTransferSummary.Describe(new CloudTransferBatch(4, 1, 2, 1));

        Assert.Contains("1 项成功", message, StringComparison.Ordinal);
        Assert.Contains("2 项失败", message, StringComparison.Ordinal);
        Assert.Contains("1 项已停止", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_completion_notification_honours_the_setting()
    {
        var queue = NewQueue();
        var notifier = new CloudCompletionNotifier(queue) { Enabled = false };
        string? reported = null;
        notifier.Notified += (_, message) => reported = message;

        notifier.Report(new CloudTransferBatch(1, 1, 0, 0));
        Assert.Null(reported);
        Assert.Null(notifier.LastMessage);

        notifier.Enabled = true;
        notifier.Report(new CloudTransferBatch(1, 1, 0, 0));
        Assert.Equal("云盘传输完成：1 项。", reported);
        Assert.Equal(reported, notifier.LastMessage);
        notifier.Dispose();
    }

    // ---------------------------------------------------------------- settings

    [Fact]
    public void The_cloud_defaults_are_the_values_an_unconfigured_installation_expects()
    {
        var settings = new ApplicationSettings();

        // Two matches the concurrency the queue had before it became adjustable, so an existing
        // installation behaves exactly as it did.
        Assert.Equal(CloudTransferQueue.DefaultConcurrency, settings.CloudMaxConcurrentTransfers);
        Assert.Equal(2, settings.CloudRetryCount);
        Assert.Null(settings.CloudDownloadDirectory);
        Assert.Equal(CloudUploadConflictPolicy.Ask, settings.CloudUploadConflict);
        Assert.True(settings.CloudNotifyOnCompletion);
        Assert.True(settings.CloudResumeOnStartup);
    }

    [Theory]
    [InlineData(0, 2, false)]
    [InlineData(17, 2, false)]
    [InlineData(4, -1, false)]
    [InlineData(4, 11, false)]
    [InlineData(4, 2, true)]
    public void The_cloud_settings_are_validated_where_they_are_loaded(int concurrency, int retries, bool expectedValid)
    {
        var settings = new ApplicationSettings { CloudMaxConcurrentTransfers = concurrency, CloudRetryCount = retries };

        var errors = ConfigurationValidator.Validate(settings);

        Assert.Equal(expectedValid, errors.Count == 0);
    }

    [Theory]
    [InlineData(CloudUploadConflictPolicy.Ask, 0)]
    [InlineData(CloudUploadConflictPolicy.Overwrite, 1)]
    [InlineData(CloudUploadConflictPolicy.Skip, 2)]
    public void The_conflict_picker_round_trips_its_selection(CloudUploadConflictPolicy policy, int index)
    {
        Assert.Equal(index, SettingsWindow.ConflictIndex(policy));
        Assert.Equal(policy, SettingsWindow.ConflictPolicy(index));
    }

    [Fact]
    public void An_unrecognised_conflict_selection_falls_back_to_asking()
    {
        // The picker is the only way to set this, so an index outside its range means the list changed
        // under a saved value; asking is the safe answer.
        Assert.Equal(CloudUploadConflictPolicy.Ask, SettingsWindow.ConflictPolicy(-1));
        Assert.Equal(CloudUploadConflictPolicy.Ask, SettingsWindow.ConflictPolicy(7));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A queue whose journal lives in its own temp directory. Tasks are added through <see cref="Add"/>
    /// rather than <c>Enqueue</c>, because enqueueing starts a real rclone transfer — these tests pin the
    /// state machine, not the byte movement.
    /// </summary>
    private CloudTransferQueue NewQueue()
    {
        var root = NewDirectory();
        _roots.Add(root);
        return new CloudTransferQueue(new CloudTransferJournalStore(root));
    }

    /// <summary>Queues one transfer in the state the test needs, without starting it.</summary>
    private static CloudTransferTask Add(CloudTransferQueue queue, string remotePath, CloudTransferState state = CloudTransferState.Pending)
    {
        var task = new CloudTransferTask(CloudTransferDirection.Download, "s3://archive", remotePath,
            Path.Combine(@"C:\tmp", CloudTransferTask.NameOf(remotePath)), 2048) { State = state };
        queue.Tasks.Add(task);
        return task;
    }

    private static CloudFileEntry FileEntry(string name, long size, string? path = null, DateTimeOffset? modified = null) =>
        new(name, path ?? name, IsDirectory: false, size, modified);

    private static CloudFileEntry FolderEntry(string name) => new(name, name, IsDirectory: true, 0, null);

    /// <summary>An endpoint account with the given name and configured root folder.</summary>
    private static CloudEndpointAccount Account(string name, string root) =>
        new(new RcloneAccount(name, "sftp"), new CloudEndpointMetadata(name, "sftp", "SFTP", root, ""));

    // ---------------------------------------------------------------- paste: what a clipboard row means

    [Fact]
    public void A_paste_recognises_the_same_file_on_the_same_endpoint_however_it_is_spelled()
    {
        // Whether a path's case is significant is the backend's answer, not Feng Sync's, so the browser's
        // identity rule has always been one remote plus one path, compared the way the rest of the app
        // compares paths. A different remote is a different file even when the paths match exactly.
        var nas = Account("nas", "team");
        var ontoItself = CloudPastePlanner.Build(
            [new CloudClipboardItem(nas, FileEntry("A.TXT", 10, path: "TEAM/a.txt"))], nas, "team", []);
        Assert.Empty(ontoItself.Targets);
        Assert.Contains("已经在目标目录里", string.Join(" ", ontoItself.Skipped), StringComparison.Ordinal);

        var otherEndpoint = CloudPastePlanner.Build(
            [new CloudClipboardItem(nas, FileEntry("a.txt", 10, path: "team/a.txt"))],
            Account("files", "team"), "team", []);
        var target = Assert.Single(otherEndpoint.Targets);
        Assert.Equal("team/a.txt", target.DestinationPath);
        Assert.False(target.SameEndpoint);
    }

    [Fact]
    public void A_paste_reports_the_rows_it_cannot_paste_rather_than_dropping_them_silently()
    {
        // A folder is not a paste target, because the transfer unit is one object, and the reason belongs in
        // the plan: a plan that quietly returned one target would read like a paste that lost the user's files.
        // One line for the folders, not one per row, because the answer is the same for all of them.
        var nas = Account("nas", "team");
        var plan = CloudPastePlanner.Build(
            [
                new CloudClipboardItem(nas, FolderEntry("docs")),
                new CloudClipboardItem(nas, FolderEntry("photos")),
                new CloudClipboardItem(nas, FileEntry("c.txt", 10, path: "team/c.txt")),
            ],
            nas, "team/incoming", [FileEntry("c.txt", 10, path: "team/incoming/c.txt")]);

        var target = Assert.Single(plan.Targets);
        Assert.True(target.OverwritesExisting);
        Assert.Equal(1, plan.Conflicts);
        var skipped = Assert.Single(plan.Skipped);
        Assert.StartsWith("2 个文件夹没有被复制", skipped, StringComparison.Ordinal);
    }

    [Fact]
    public void A_paste_strips_each_endpoints_own_root_and_only_once()
    {
        // The queue addresses an endpoint URI, so both sides of a paste have to be relative to their own
        // endpoint's root — and an endpoint whose prefix lives in an rclone alias has no root to strip,
        // because the alias already applies it. Composing either wrong copies to team/team/a.txt.
        var nas = Account("nas", "team");
        var betweenRoots = CloudPastePlanner.Build(
            [new CloudClipboardItem(nas, FileEntry("a.txt", 10, path: "team/a.txt"))],
            Account("files", "shared"), "shared/incoming", []);
        var across = Assert.Single(betweenRoots.Targets);
        Assert.Equal("a.txt", across.SourceRelativePath);
        Assert.Equal("incoming/a.txt", across.DestinationRelativePath);

        var aliasBacked = new CloudEndpointAccount(new RcloneAccount("docs", "alias"),
            new CloudEndpointMetadata("docs", "alias", "Backblaze", "bucket", "team/files", AliasOf: "docs_s3"));
        var insideAlias = CloudPastePlanner.Build(
            [new CloudClipboardItem(aliasBacked, FileEntry("a.txt", 10, path: "team/files/a.txt"))],
            aliasBacked, "team/files/backup", []);
        var kept = Assert.Single(insideAlias.Targets);
        Assert.Equal("team/files/a.txt", kept.SourceRelativePath);
        Assert.Equal("team/files/backup/a.txt", kept.DestinationRelativePath);
        Assert.Equal("team/files/backup/a.txt", kept.DestinationPath);
        Assert.True(kept.SameEndpoint);
    }

    private static RcloneException Rclone(RcloneFailureCategory category) =>
        new(new RcloneFailure(category, false, "test", null, null, $"rclone failed: {category}", "", "", "", ""));

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fengsync-cloud-feature-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
