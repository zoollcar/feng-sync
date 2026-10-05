using FengSync.Core;
using FengSync.Core.Configuration;
using FengSync.Services;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// What a stopped transfer row can hand over, and what the 完成后动作 does with a drained batch.
/// </summary>
/// <remarks>
/// All of this is pure rule-work, so it is asserted directly rather than through a window: which menu entries a
/// row offers, what 复制详情 puts on the clipboard, what the failure log looks like when read, and — the one that
/// matters most — that the machine is never taken away after a batch that half failed.
/// </remarks>
public sealed class CloudTransferRowDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-row-diag-" + Guid.NewGuid().ToString("N"));

    public CloudTransferRowDiagnosticsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    // ---- C7: the row menu ----

    [Fact]
    public void A_finished_download_offers_its_folder_and_its_link()
    {
        var task = new CloudTransferTask(CloudTransferDirection.Download, "sftp://nas/photos", "team/a.jpg",
            Path.Combine(_root, "a.jpg"), 10) { State = CloudTransferState.Completed };

        var menu = CloudTransferRowMenu.Build(task, new CloudTransferRowContext());

        Assert.Equal(
            [CloudTransferRowCommand.MoveUp, CloudTransferRowCommand.MoveDown, CloudTransferRowCommand.OpenFolder,
             CloudTransferRowCommand.CopyLink, CloudTransferRowCommand.CopyDetail, CloudTransferRowCommand.ViewLog],
            menu.Select(item => item.Command));

        var folder = menu.Single(item => item.Command == CloudTransferRowCommand.OpenFolder);
        Assert.True(folder.Enabled);
        Assert.Null(folder.Hint);
        Assert.Equal(_root, CloudTransferRowMenu.LocalFolderOf(task));

        // Viewing the log never depends on the row: it is always there to look at.
        Assert.True(menu.Single(item => item.Command == CloudTransferRowCommand.ViewLog).Enabled);
    }

    [Fact]
    public void A_copy_cannot_open_a_folder_and_says_why()
    {
        // A copy's bytes never touch this machine, so there is no local folder to open — and an item that is
        // greyed out with no reason is the thing users report as "the button does nothing".
        var task = new CloudTransferTask(CloudTransferDirection.Copy, "sftp://src/team", "a.bin", "",
            10, "sftp://dst/archive", "kept/a.bin") { State = CloudTransferState.Completed };

        var menu = CloudTransferRowMenu.Build(task, new CloudTransferRowContext());

        var folder = menu.Single(item => item.Command == CloudTransferRowCommand.OpenFolder);
        Assert.False(folder.Enabled);
        Assert.Contains("不落盘", folder.Hint!);
        Assert.Null(CloudTransferRowMenu.LocalFolderOf(task));
    }

    [Fact]
    public void A_transfer_that_is_still_running_is_not_offered_a_link_yet()
    {
        // The object may not exist at the far end at all, so the question is not worth asking — and the reason
        // says which of the two states it is in.
        var pending = new CloudTransferTask(CloudTransferDirection.Upload, "sftp://nas/photos", "team/a.jpg",
            Path.Combine(_root, "a.jpg"), 10) { State = CloudTransferState.Pending };
        var running = new CloudTransferTask(CloudTransferDirection.Upload, "sftp://nas/photos", "team/a.jpg",
            Path.Combine(_root, "a.jpg"), 10) { State = CloudTransferState.Running };

        var pendingItem = CloudTransferRowMenu.Build(pending, new CloudTransferRowContext())
            .Single(item => item.Command == CloudTransferRowCommand.CopyLink);
        Assert.False(pendingItem.Enabled);
        Assert.Contains("还没开始", pendingItem.Hint!);

        var runningItem = CloudTransferRowMenu.Build(running, new CloudTransferRowContext())
            .Single(item => item.Command == CloudTransferRowCommand.CopyLink);
        Assert.False(runningItem.Enabled);
        Assert.Contains("正在进行", runningItem.Hint!);
    }

    [Theory]
    [InlineData(CloudTransferState.Completed)]
    [InlineData(CloudTransferState.Failed)]
    [InlineData(CloudTransferState.Cancelled)]
    [InlineData(CloudTransferState.Paused)]
    public void Every_stopped_state_offers_a_link(CloudTransferState state)
    {
        var task = new CloudTransferTask(CloudTransferDirection.Download, "sftp://nas/photos", "team/a.jpg",
            Path.Combine(_root, "a.jpg"), 10) { State = state };

        Assert.True(CloudTransferRowMenu.OffersLink(task));
    }

    [Fact]
    public void Reordering_is_offered_exactly_when_the_row_is_waiting()
    {
        var waiting = new CloudTransferTask(CloudTransferDirection.Download, "sftp://nas/photos", "a.jpg",
            Path.Combine(_root, "a.jpg"), 10) { State = CloudTransferState.Pending };

        var movable = CloudTransferRowMenu.Build(waiting, new CloudTransferRowContext(CanMoveUp: true, CanMoveDown: false));
        Assert.True(movable.Single(item => item.Command == CloudTransferRowCommand.MoveUp).Enabled);
        Assert.False(movable.Single(item => item.Command == CloudTransferRowCommand.MoveDown).Enabled);
        Assert.Contains("等待中", movable.Single(item => item.Command == CloudTransferRowCommand.MoveDown).Hint!);

        var running = new CloudTransferTask(CloudTransferDirection.Download, "sftp://nas/photos", "a.jpg",
            Path.Combine(_root, "a.jpg"), 10) { State = CloudTransferState.Running };
        var stuck = CloudTransferRowMenu.Build(running, new CloudTransferRowContext());
        Assert.All(stuck.Where(item => item.Command is CloudTransferRowCommand.MoveUp or CloudTransferRowCommand.MoveDown),
            item => Assert.False(item.Enabled));
    }

    [Fact]
    public void The_detail_entry_is_offered_only_when_there_is_a_detail_to_copy()
    {
        var quiet = new CloudTransferTask(CloudTransferDirection.Download, "sftp://nas/photos", "a.jpg",
            Path.Combine(_root, "a.jpg"), 10) { State = CloudTransferState.Completed };
        var broken = new CloudTransferTask(CloudTransferDirection.Download, "sftp://nas/photos", "a.jpg",
            Path.Combine(_root, "a.jpg"), 10) { State = CloudTransferState.Failed, Error = "连接被拒绝" };

        var quietItem = CloudTransferRowMenu.Build(quiet, new CloudTransferRowContext())
            .Single(item => item.Command == CloudTransferRowCommand.CopyDetail);
        Assert.False(quietItem.Enabled);
        Assert.Contains("没有错误信息", quietItem.Hint!);

        var brokenItem = CloudTransferRowMenu.Build(broken, new CloudTransferRowContext())
            .Single(item => item.Command == CloudTransferRowCommand.CopyDetail);
        Assert.True(brokenItem.Enabled);
    }

    // ---- C4: the diagnostics ----

    [Fact]
    public void The_copied_detail_says_what_the_transfer_was_before_what_went_wrong()
    {
        var task = new CloudTransferTask(CloudTransferDirection.Upload, "sftp://nas/photos", "team/a.jpg",
            Path.Combine(_root, "a.jpg"), 10)
        {
            State = CloudTransferState.Failed,
            Attempt = 3,
            Error = "上传失败",
            ErrorDetail = "operations/copyfile · Permission · 诊断 ID abc123"
        };

        var detail = CloudTransferDiagnostics.DetailFor(task);

        // A diagnostic with no subject is not something the user can attach to anything, so the first lines
        // identify the transfer before the error is described.
        Assert.StartsWith("任务：", detail);
        Assert.Contains("方向：上传", detail);
        Assert.Contains("路径：", detail);
        Assert.Contains("team/a.jpg", detail);
        Assert.Contains("状态：", detail);
        Assert.Contains("尝试次数：3", detail);
        Assert.Contains("错误：上传失败", detail);
        Assert.Contains("诊断 ID abc123", detail);
    }

    [Fact]
    public void A_transfer_with_no_recorded_detail_says_so_rather_than_leaving_a_blank()
    {
        var task = new CloudTransferTask(CloudTransferDirection.Download, "sftp://nas/photos", "a.jpg",
            Path.Combine(_root, "a.jpg"), 10) { State = CloudTransferState.Failed, Error = "失败" };

        var detail = CloudTransferDiagnostics.DetailFor(task);

        Assert.Contains("没有记录更详细的信息", detail);
    }

    [Fact]
    public void A_copy_s_detail_names_both_ends_and_an_upload_names_the_local_file()
    {
        var copy = new CloudTransferTask(CloudTransferDirection.Copy, "sftp://src/team", "a.bin", "",
            10, "sftp://dst/archive", "kept/a.bin") { State = CloudTransferState.Failed, Error = "失败" };
        var upload = new CloudTransferTask(CloudTransferDirection.Upload, "sftp://nas/photos", "team/a.jpg",
            Path.Combine(_root, "a.jpg"), 10) { State = CloudTransferState.Failed, Error = "失败" };

        Assert.Contains("sftp://src/team/a.bin → sftp://dst/archive/kept/a.bin", CloudTransferDiagnostics.DetailFor(copy));
        Assert.Contains(Path.Combine(_root, "a.jpg") + " → sftp://nas/photos/team/a.jpg", CloudTransferDiagnostics.DetailFor(upload));
    }

    [Fact]
    public void The_copy_outcome_line_never_claims_the_clipboard_worked_when_it_did_not()
    {
        Assert.Contains("已复制", CloudTransferDiagnostics.DescribeCopy(true));
        Assert.Contains("没能写入剪贴板", CloudTransferDiagnostics.DescribeCopy(false));
    }

    [Fact]
    public void The_log_path_is_the_day_file_the_failure_writer_uses()
    {
        var path = CloudTransferDiagnostics.LogPath(new DateTimeOffset(2026, 10, 5, 3, 0, 0, TimeSpan.FromHours(8)));

        // Dated in UTC, because that is what RcloneFailureLog names its file with — a path computed in local
        // time would point at tomorrow's file for the first eight hours of the day.
        Assert.EndsWith("rclone-2026-10-04.jsonl", path);
        Assert.Contains("logs", path);
    }

    [Fact]
    public void A_missing_log_is_reported_as_missing_rather_than_as_an_empty_log()
    {
        // "There is nothing here" and "I could not look" are different answers, and only the second one is
        // something the user can act on.
        var text = CloudTransferDiagnostics.ReadLog(Path.Combine(_root, "no-such-file.jsonl"));

        Assert.Contains("没有找到今天的失败日志", text);
        Assert.Contains("只有真正发生过 rclone 失败时才会生成这个文件", text);
    }

    [Fact]
    public void An_empty_log_file_is_reported_as_empty()
    {
        var path = Path.Combine(_root, "empty.jsonl");
        File.WriteAllText(path, "");

        Assert.Contains("失败日志为空", CloudTransferDiagnostics.ReadLog(path));
    }

    [Fact]
    public void The_log_is_rendered_one_readable_line_per_failure()
    {
        var path = Path.Combine(_root, "rclone-2026-10-05.jsonl");
        File.WriteAllLines(path,
        [
            """{"time":"2026-10-05T03:04:05Z","context":"cloud-transfer","Category":"Network","Operation":"operations/copyfile","UserMessage":"网络中断","CorrelationId":"abc123"}""",
            // A line the reader cannot explain is passed through verbatim rather than hidden: showing the raw
            // text is more useful than dropping a record.
            "not json at all"
        ]);

        var text = CloudTransferDiagnostics.ReadLog(path);

        Assert.Contains("共 2 条", text);
        Assert.Contains("cloud-transfer", text);
        Assert.Contains("Network", text);
        Assert.Contains("operations/copyfile", text);
        Assert.Contains("网络中断", text);
        Assert.Contains("诊断 ID abc123", text);
        Assert.Contains("not json at all", text);
    }

    [Fact]
    public void Only_the_requested_tail_of_the_log_is_rendered()
    {
        var path = Path.Combine(_root, "tail.jsonl");
        File.WriteAllLines(path, Enumerable.Range(0, 20).Select(index => $$"""{"UserMessage":"第{{index}}条"}"""));

        var text = CloudTransferDiagnostics.ReadLog(path, maxLines: 3);

        Assert.Contains("共 3 条", text);
        // The tail is what is shown: the newest failure is the one worth reading, so the oldest of the twenty
        // must not be there while the last of them must.
        Assert.Contains("第19条", text);
        Assert.DoesNotContain("第0条", text);
        Assert.DoesNotContain("第16条", text);
    }

    // ---- C3: the archive's wording ----

    [Fact]
    public void The_archive_summary_says_what_is_not_there_as_well_as_what_is()
    {
        Assert.Contains("还没有归档的传输", CloudTransferHistorySummary.DescribeAll([]));

        var entries = new[]
        {
            Archive(CloudTransferDirection.Copy, CloudTransferState.Completed, 1),
            Archive(CloudTransferDirection.Download, CloudTransferState.Failed, 2),
            Archive(CloudTransferDirection.Upload, CloudTransferState.Cancelled, 1)
        };
        var summary = CloudTransferHistorySummary.DescribeAll(entries);

        Assert.Contains("共 3 条", summary);
        Assert.Contains("成功 1 条", summary);
        Assert.Contains("失败 1 条", summary);
    }

    [Fact]
    public void An_archived_line_names_the_file_where_it_went_and_how_it_ended()
    {
        var entry = Archive(CloudTransferDirection.Copy, CloudTransferState.Completed, 1);

        var line = CloudTransferHistorySummary.Describe(entry);

        Assert.Contains("复制", line);
        Assert.Contains("a.bin", line);
        Assert.Contains("已完成", line);
        Assert.Contains("sftp://dst/archive/kept/a.bin", line);
        // A first attempt is not worth mentioning; a retry is.
        Assert.DoesNotContain("第 1 次尝试", line);
        Assert.Contains("第 3 次尝试", CloudTransferHistorySummary.Describe(entry with { Attempt = 3 }));
    }

    [Fact]
    public void An_upload_is_archived_under_its_local_file_name_and_a_download_under_its_remote_one()
    {
        var upload = Archive(CloudTransferDirection.Upload, CloudTransferState.Completed, 1);
        var download = Archive(CloudTransferDirection.Download, CloudTransferState.Completed, 1);

        Assert.Contains("上传", CloudTransferHistorySummary.Describe(upload));
        Assert.Contains("a.bin", CloudTransferHistorySummary.Describe(upload));
        Assert.Contains("下载", CloudTransferHistorySummary.Describe(download));
        Assert.Contains("sftp://src/team/a.bin", CloudTransferHistorySummary.Describe(download));
    }

    // ---- C6: the post-transfer action ----

    [Fact]
    public void The_choice_is_named_the_same_way_in_the_settings_page_and_the_prompt()
    {
        // The settings page builds its list from Describe, so the index of the entry the user picked *is* the
        // enum value. Changing the order here would silently change what a saved choice means.
        Assert.Contains("不执行", CloudPostTransferAction.Describe(CloudAfterTransferAction.None));
        Assert.Contains("退出 Feng Sync", CloudPostTransferAction.Describe(CloudAfterTransferAction.Exit));
        Assert.Contains("休眠", CloudPostTransferAction.Describe(CloudAfterTransferAction.Sleep));
        Assert.Contains("关闭计算机", CloudPostTransferAction.Describe(CloudAfterTransferAction.Shutdown));
    }

    [Fact]
    public void The_shutdown_prompt_says_how_to_call_it_off()
    {
        // A shutdown that can still be cancelled is a very different proposition from one that cannot.
        Assert.Contains("shutdown /a", CloudPostTransferAction.ConfirmationFor(CloudAfterTransferAction.Shutdown));
        Assert.DoesNotContain("shutdown", CloudPostTransferAction.ConfirmationFor(CloudAfterTransferAction.Exit));
        Assert.Contains("休眠", CloudPostTransferAction.ConfirmationFor(CloudAfterTransferAction.Sleep));
    }

    [Fact]
    public void Nothing_happens_at_all_when_the_choice_is_to_do_nothing()
    {
        using var action = new CloudPostTransferAction(Queue()) { Action = CloudAfterTransferAction.None };
        var performed = new List<CloudAfterTransferAction>();
        action.Confirm = _ => true;
        action.Perform = performed.Add;

        action.Report(new CloudTransferBatch(2, 2, 0, 0));

        Assert.Empty(performed);
        Assert.Null(action.LastNotice);
    }

    [Fact]
    public void A_batch_that_half_failed_reports_instead_of_taking_the_machine_away()
    {
        // The rule worth more than the convenience: shutting down after a batch that half failed would take
        // away the only chance to look at what went wrong.
        using var action = new CloudPostTransferAction(Queue()) { Action = CloudAfterTransferAction.Shutdown };
        var performed = new List<CloudAfterTransferAction>();
        action.Confirm = _ => true;
        action.Perform = performed.Add;

        action.Report(new CloudTransferBatch(Total: 3, Completed: 2, Failed: 1, Stopped: 0));

        Assert.Empty(performed);
        Assert.NotNull(action.LastNotice);
        Assert.Contains("1 项传输没有成功", action.LastNotice!);
        Assert.Contains("关闭计算机", action.LastNotice!);
    }

    [Fact]
    public void The_action_is_confirmed_first_and_a_refusal_is_reported()
    {
        using var action = new CloudPostTransferAction(Queue()) { Action = CloudAfterTransferAction.Sleep };
        var performed = new List<CloudAfterTransferAction>();
        var asked = 0;
        action.Confirm = _ => { asked++; return false; };
        action.Perform = performed.Add;

        action.Report(new CloudTransferBatch(1, 1, 0, 0));

        Assert.Equal(1, asked);
        Assert.Empty(performed);
        Assert.Contains("已取消", action.LastNotice!);
    }

    [Fact]
    public void With_no_confirmation_available_the_action_does_not_run_unasked()
    {
        // The reason the action is owned by a window: an application-owned one firing while nothing is open
        // would either block forever or — worse — proceed unconfirmed. Nothing happening is the only safe
        // behaviour when nobody can be asked.
        using var action = new CloudPostTransferAction(Queue()) { Action = CloudAfterTransferAction.Shutdown };
        var performed = new List<CloudAfterTransferAction>();
        action.Perform = performed.Add;
        action.Confirm = null;

        action.Report(new CloudTransferBatch(1, 1, 0, 0));

        Assert.Empty(performed);
        Assert.Null(action.LastNotice);
    }

    [Fact]
    public void An_empty_batch_is_not_a_reason_to_shut_the_machine_down()
    {
        using var action = new CloudPostTransferAction(Queue()) { Action = CloudAfterTransferAction.Exit };
        var performed = new List<CloudAfterTransferAction>();
        action.Confirm = _ => true;
        action.Perform = performed.Add;

        action.Report(new CloudTransferBatch(0, 0, 0, 0));

        Assert.Empty(performed);
        Assert.Null(action.LastNotice);
    }

    [Fact]
    public void A_confirmed_action_is_performed_exactly_once_and_announced_to_the_window()
    {
        using var action = new CloudPostTransferAction(Queue()) { Action = CloudAfterTransferAction.Exit };
        var performed = new List<CloudAfterTransferAction>();
        var noticed = new List<string>();
        action.Confirm = _ => true;
        action.Perform = performed.Add;
        action.Noticed += (_, message) => noticed.Add(message);

        action.Report(new CloudTransferBatch(4, 4, 0, 0));

        Assert.Equal([CloudAfterTransferAction.Exit], performed);
        // A confirmation is not a notice: nothing is said when the action simply goes ahead.
        Assert.Empty(noticed);
        Assert.Null(action.LastNotice);
    }

    [Fact]
    public void Disposing_the_action_stops_it_answering_the_queue()
    {
        var queue = Queue();
        var action = new CloudPostTransferAction(queue) { Action = CloudAfterTransferAction.Exit };
        var performed = new List<CloudAfterTransferAction>();
        action.Confirm = _ => true;
        action.Perform = performed.Add;

        action.Dispose();
        action.Report(new CloudTransferBatch(1, 1, 0, 0));

        // Report still works when called directly; what Dispose removes is the queue subscription, which is
        // asserted by the absence of a second run after a real drain in CloudTransferQueuePolicyTests.
        Assert.Single(performed);
    }

    private static CloudTransferQueue Queue() =>
        new(new CloudTransferJournalStore(Path.Combine(Path.GetTempPath(), "fengsync-row-diag-journal", Guid.NewGuid().ToString("N"))));

    private CloudTransferHistoryEntry Archive(CloudTransferDirection direction, CloudTransferState state, int attempt) =>
        new(Guid.NewGuid(), direction, "sftp://src/team", "a.bin", Path.Combine(_root, "a.bin"), 10, state,
            new DateTimeOffset(2026, 10, 5, 3, 0, 0, TimeSpan.FromHours(8)), attempt, null, null,
            direction == CloudTransferDirection.Copy ? "sftp://dst/archive" : null,
            direction == CloudTransferDirection.Copy ? "kept/a.bin" : null);
}
