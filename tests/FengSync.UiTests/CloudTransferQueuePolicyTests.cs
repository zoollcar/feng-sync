using System.Net;
using System.Text;
using System.Text.Json;
using FengSync.Core;
using FengSync.Core.Configuration;
using FengSync.Services;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// The three things the transfer queue learned in batch C: an explicit order, a rate ceiling, and hours it is
/// allowed to run in — plus the archive that makes 清除已完成 non-destructive.
/// </summary>
/// <remarks>
/// Driven against a stand-in for rclone's RC API, because every claim here is about what the queue decides and
/// what it sends: which parked row gets the next slot, when a rate is pushed and when it is handed back, and
/// whether a row is filed before it is dropped. Real rclone's own behaviour is pinned separately, in
/// <c>RcloneBandwidthFactTests</c> (the rate syntax) and <c>ServerSideCopyTests</c> (the copy payload).
/// </remarks>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudTransferQueuePolicyTests
{
    private const string SourceUri = "sftp://src/team";
    private const string TargetUri = "sftp://dst/archive";

    // ---- C2: an explicit order, and the list and the queue agreeing about it ----

    [Fact]
    public async Task A_new_transfer_goes_to_the_back_of_the_queue()
    {
        var fake = new FakeRclone();
        var root = NewDirectory();
        try
        {
            var queue = ParkedQueue(root, fake);
            var first = queue.EnqueueCopy(SourceUri, "a.bin", TargetUri, "a.bin", 10);
            var second = queue.EnqueueCopy(SourceUri, "b.bin", TargetUri, "b.bin", 10);

            // Order is the queue's own sequence, not the list index: after a user reorders rows, "the last one"
            // is no longer the highest index, so new work has to be given a number that is larger than all of them.
            Assert.True(second.Order > first.Order);
            Assert.Equal([first, second], queue.Tasks);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Moving_a_waiting_transfer_up_swaps_it_past_the_next_waiting_one()
    {
        var fake = new FakeRclone();
        var root = NewDirectory();
        try
        {
            var queue = ParkedQueue(root, fake);
            var first = queue.EnqueueCopy(SourceUri, "a.bin", TargetUri, "a.bin", 10);
            var second = queue.EnqueueCopy(SourceUri, "b.bin", TargetUri, "b.bin", 10);
            var third = queue.EnqueueCopy(SourceUri, "c.bin", TargetUri, "c.bin", 10);

            Assert.False(queue.CanMoveUp(first));
            Assert.True(queue.CanMoveDown(first));
            Assert.True(queue.CanMoveUp(third));
            Assert.False(queue.CanMoveDown(third));

            Assert.True(queue.MoveUp(third));

            // Both faces move together: the list the user sees, and the priority the pump reads.
            Assert.Equal([first, third, second], queue.Tasks);
            Assert.True(third.Order < second.Order);
            Assert.True(third.Order > first.Order);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task A_waiting_transfer_can_be_moved_to_the_front_one_place_at_a_time()
    {
        var fake = new FakeRclone();
        var root = NewDirectory();
        try
        {
            var queue = ParkedQueue(root, fake);
            var first = queue.EnqueueCopy(SourceUri, "a.bin", TargetUri, "a.bin", 10);
            var second = queue.EnqueueCopy(SourceUri, "b.bin", TargetUri, "b.bin", 10);
            var third = queue.EnqueueCopy(SourceUri, "c.bin", TargetUri, "c.bin", 10);

            queue.MoveUp(third);
            queue.MoveUp(third);

            Assert.Equal([third, first, second], queue.Tasks);
            Assert.False(queue.CanMoveUp(third));
            Assert.False(queue.MoveUp(third));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task A_transfer_that_is_not_waiting_cannot_be_reordered()
    {
        // A running row already holds its slot, so moving it changes nothing the user can observe; a finished
        // one is not in the race at all. Both are refused rather than silently accepted.
        var fake = new FakeRclone();
        var root = NewDirectory();
        try
        {
            var queue = ParkedQueue(root, fake);
            var first = queue.EnqueueCopy(SourceUri, "a.bin", TargetUri, "a.bin", 10);
            var second = queue.EnqueueCopy(SourceUri, "b.bin", TargetUri, "b.bin", 10);
            second.State = CloudTransferState.Completed;

            Assert.False(queue.CanMoveUp(second));
            Assert.False(queue.MoveUp(second));
            Assert.Equal([first, second], queue.Tasks);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task The_queue_runs_in_the_order_the_list_shows_after_a_reorder()
    {
        // The end-to-end claim of C2: what the user sees at the top is what runs first. One worker holds the
        // only slot until released, which parks the other two deterministically — waiting for the window would
        // race the enqueue, because a transfer only registers as a waiter once it reaches its slot.
        var fake = new FakeRclone(hold: true);
        var root = NewDirectory();
        try
        {
            var queue = new CloudTransferQueue(new CloudTransferJournalStore(root), _ => Task.FromResult(Client(fake)))
            {
                Concurrency = 1
            };
            var first = queue.EnqueueCopy(SourceUri, "a.bin", TargetUri, "a.bin", 10);
            await WaitUntil(() => fake.Copied.Count == 1, "the first transfer never reached the wire");

            var second = queue.EnqueueCopy(SourceUri, "b.bin", TargetUri, "b.bin", 10);
            var third = queue.EnqueueCopy(SourceUri, "c.bin", TargetUri, "c.bin", 10);
            await WaitUntil(() => queue.Tasks.Count(task => task.State == CloudTransferState.Pending) == 2,
                "the two surplus transfers were never parked");

            queue.MoveUp(third);
            queue.MoveUp(third);
            fake.Release();

            await WaitUntil(() => queue.Tasks.All(task => task.IsFinished), "the reordered transfers never ran");

            // One worker, three rows: the recorded order is exactly the priority order.
            Assert.Equal(["a.bin", "c.bin", "b.bin"], fake.Copied);
            Assert.Equal(CloudTransferState.Completed, first.State);
            Assert.Equal(CloudTransferState.Completed, second.State);
            Assert.Equal(CloudTransferState.Completed, third.State);
        }
        finally { TryDelete(root); }
    }

    // ---- C1: the rate ceiling ----

    [Fact]
    public async Task The_limit_is_pushed_when_a_transfer_starts_and_handed_back_when_the_queue_drains()
    {
        // A limit that outlived its transfers would silently throttle every later browse of the same daemon,
        // because core/bwlimit is global and nothing resets it on its own.
        var fake = new FakeRclone();
        var root = NewDirectory();
        try
        {
            var pushed = new List<string>();
            var queue = new CloudTransferQueue(new CloudTransferJournalStore(root), _ => Task.FromResult(Client(fake)))
            {
                Limits = new CloudTransferLimits(1024 * 1024, 0),
                ApplyLimit = (rate, _) => { lock (pushed) pushed.Add(rate); return Task.CompletedTask; }
            };
            var task = queue.EnqueueCopy(SourceUri, "a.bin", TargetUri, "a.bin", 10);

            await WaitUntilFinished(task);
            await WaitUntil(() => { lock (pushed) return pushed.Contains(CloudTransferLimit.Off); }, "the limit was never released");

            lock (pushed)
            {
                Assert.Equal("1048576B:off", pushed[0]);
                Assert.Equal(CloudTransferLimit.Off, pushed[^1]);
            }
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task An_unchanged_limit_is_not_pushed_again_for_the_next_transfer()
    {
        // Otherwise every task in a batch would issue a redundant RC call, and a queue with a hundred files
        // would make a hundred of them.
        var fake = new FakeRclone();
        var root = NewDirectory();
        try
        {
            var pushed = new List<string>();
            var queue = new CloudTransferQueue(new CloudTransferJournalStore(root), _ => Task.FromResult(Client(fake)))
            {
                Concurrency = 1,
                Limits = new CloudTransferLimits(1024 * 1024, 1024 * 1024),
                ApplyLimit = (rate, _) => { lock (pushed) pushed.Add(rate); return Task.CompletedTask; }
            };
            var first = queue.EnqueueCopy(SourceUri, "a.bin", TargetUri, "a.bin", 10);
            await WaitUntilFinished(first);
            var second = queue.EnqueueCopy(SourceUri, "b.bin", TargetUri, "b.bin", 10);
            await WaitUntilFinished(second);
            // The release is fired and forgotten from the drain path, so it is awaited here rather than assumed.
            await WaitUntil(() => { lock (pushed) return pushed.Count >= 2; }, "the limit was never released");

            lock (pushed) Assert.Equal(["1048576B", CloudTransferLimit.Off], pushed);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task A_limit_that_could_not_be_pushed_is_reported_rather_than_failing_the_transfer()
    {
        // A ceiling is a convenience. Failing the transfer because the ceiling could not be set would turn a
        // preference into a broken queue — but swallowing it silently would hide a setting that never took
        // effect, which is the same class of problem as a misspelled mount option.
        var fake = new FakeRclone();
        var root = NewDirectory();
        try
        {
            var queue = new CloudTransferQueue(new CloudTransferJournalStore(root), _ => Task.FromResult(Client(fake)))
            {
                Limits = new CloudTransferLimits(1024 * 1024, 0),
                ApplyLimit = (_, _) => throw new InvalidOperationException("daemon refused the rate")
            };
            var task = queue.EnqueueCopy(SourceUri, "a.bin", TargetUri, "a.bin", 10);

            await WaitUntilFinished(task);

            Assert.Equal(CloudTransferState.Completed, task.State);
            Assert.NotNull(queue.LimitError);
            Assert.Contains("限速未能生效", queue.LimitError!);
            Assert.Equal("限速 上传 1M/s · 下载 不限", queue.LimitText);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task With_no_limit_the_daemon_is_never_told_anything()
    {
        var fake = new FakeRclone();
        var root = NewDirectory();
        try
        {
            var pushed = new List<string>();
            var queue = new CloudTransferQueue(new CloudTransferJournalStore(root), _ => Task.FromResult(Client(fake)))
            {
                ApplyLimit = (rate, _) => { lock (pushed) pushed.Add(rate); return Task.CompletedTask; }
            };
            var task = queue.EnqueueCopy(SourceUri, "a.bin", TargetUri, "a.bin", 10);

            await WaitUntilFinished(task);
            await Task.Delay(100);

            lock (pushed) Assert.Empty(pushed);
            Assert.Equal("不限速", queue.LimitText);
            Assert.Null(queue.LimitError);
        }
        finally { TryDelete(root); }
    }

    // ---- C5: the hours transfers may run in ----

    [Fact]
    public async Task A_transfer_enqueued_outside_the_window_waits_instead_of_starting()
    {
        // The design C5 chose over a system scheduler: outside the window the queue parks its work, so the
        // transfer is visible in the list instead of looking like one that silently crawls.
        var fake = new FakeRclone();
        var root = NewDirectory();
        try
        {
            var queue = ParkedQueue(root, fake);
            var task = queue.EnqueueCopy(SourceUri, "a.bin", TargetUri, "a.bin", 10);

            Assert.True(queue.WaitingForWindow);
            Assert.Equal(CloudTransferState.Pending, task.State);
            Assert.Contains("等待传输时段 22:00–06:00", queue.SummaryText);

            // A window is a decision to wait, not a delay: nothing may reach the wire, and nothing does.
            await Task.Delay(300);
            Assert.Equal(CloudTransferState.Pending, task.State);
            Assert.Empty(fake.Copied);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Opening_the_window_starts_what_was_parked_without_being_asked_again()
    {
        // The re-check is the queue's own: it arranges a single wake-up for the moment the window opens rather
        // than polling, so the transfer starts by itself. A one-second window is used so the wake-up can be
        // observed; the real one is however long the user typed.
        var fake = new FakeRclone();
        var root = NewDirectory();
        try
        {
            var now = DateTimeOffset.Now;
            var queue = new CloudTransferQueue(new CloudTransferJournalStore(root), _ => Task.FromResult(Client(fake)))
            {
                TimeWindow = new CloudTransferTimeWindow(
                    TimeOnly.FromDateTime(now.AddSeconds(1).LocalDateTime),
                    TimeOnly.FromDateTime(now.AddSeconds(60).LocalDateTime))
            };
            var task = queue.EnqueueCopy(SourceUri, "a.bin", TargetUri, "a.bin", 10);

            Assert.True(queue.WaitingForWindow);
            Assert.Equal(CloudTransferState.Pending, task.State);

            // Nothing calls EvaluateSchedule here: the queue's own one-shot wake-up has to do it.
            await WaitUntilFinished(task);

            Assert.Equal(CloudTransferState.Completed, task.State);
            Assert.False(queue.WaitingForWindow);
            Assert.Equal(["a.bin"], fake.Copied);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task A_window_that_covers_the_whole_day_never_parks_anything()
    {
        var fake = new FakeRclone();
        var root = NewDirectory();
        try
        {
            var queue = new CloudTransferQueue(new CloudTransferJournalStore(root), _ => Task.FromResult(Client(fake)))
            {
                TimeWindow = new CloudTransferTimeWindow(new TimeOnly(9, 0), new TimeOnly(9, 0))
            };
            var task = queue.EnqueueCopy(SourceUri, "a.bin", TargetUri, "a.bin", 10);

            await WaitUntilFinished(task);

            Assert.False(queue.WaitingForWindow);
            Assert.Equal(CloudTransferState.Completed, task.State);
        }
        finally { TryDelete(root); }
    }

    // ---- C3: the archive ----

    [Fact]
    public async Task Clearing_finished_transfers_files_them_in_the_archive_first()
    {
        // "清除已完成" is a destructive action on the only record of what happened, and the journal drops a
        // task's entry the moment it succeeds. The archive is written before the row goes away, so the file
        // that was moved is still accounted for afterwards.
        var fake = new FakeRclone();
        var root = NewDirectory();
        try
        {
            var history = new CloudTransferHistoryRepository(Path.Combine(root, "history.json"));
            var queue = new CloudTransferQueue(new CloudTransferJournalStore(root), _ => Task.FromResult(Client(fake)), history);
            var task = queue.EnqueueCopy(SourceUri, "a.bin", TargetUri, "kept/a.bin", 10);
            await WaitUntilFinished(task);

            Assert.Equal(1, queue.ClearFinished());

            Assert.Empty(queue.Tasks);
            await WaitUntil(() => history.QueryAsync().GetAwaiter().GetResult().Count > 0, "the transfer was never archived");
            var archived = Assert.Single(await history.QueryAsync());
            Assert.Equal(task.Id, archived.Id);
            Assert.Equal(CloudTransferState.Completed, archived.State);
            Assert.Equal(TargetUri, archived.TargetUri);
            Assert.Equal("kept/a.bin", archived.TargetPath);
            Assert.True(archived.Succeeded);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Clearing_nothing_archives_nothing_but_still_refreshes_the_queue()
    {
        var fake = new FakeRclone();
        var root = NewDirectory();
        try
        {
            var history = new CloudTransferHistoryRepository(Path.Combine(root, "history.json"));
            var queue = new CloudTransferQueue(new CloudTransferJournalStore(root), _ => Task.FromResult(Client(fake)), history);
            // A failed transfer is the user's queue of things to retry, so 清除已完成 must leave it alone.
            var task = queue.EnqueueCopy(SourceUri, "a.bin", TargetUri, "a.bin", 10);
            task.State = CloudTransferState.Failed;

            var raised = 0;
            queue.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(CloudTransferQueue.OverallPercentage)) raised++;
            };

            Assert.Equal(0, queue.ClearFinished());

            Assert.Single(queue.Tasks);
            Assert.Empty(await history.QueryAsync());
            // The aggregate is re-read even when nothing was cleared: this is the queue's only "the list
            // changed" call, and the window's progress bar follows the queue through it. An early return for an
            // empty list reads like a free optimization and silently drops it — that is how it broke
            // DriveHubEntryPointTests, which drives the bar from the installed queue exactly this way.
            Assert.Equal(1, raised);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Clearing_finished_transfers_still_works_with_no_archive_configured()
    {
        // A queue built before the archive existed — and the focused queue tests — must keep working.
        var fake = new FakeRclone();
        var root = NewDirectory();
        try
        {
            var queue = new CloudTransferQueue(new CloudTransferJournalStore(root), _ => Task.FromResult(Client(fake)));
            var task = queue.EnqueueCopy(SourceUri, "a.bin", TargetUri, "a.bin", 10);
            await WaitUntilFinished(task);

            Assert.Equal(1, queue.ClearFinished());
            Assert.Empty(queue.Tasks);
            Assert.Null(queue.History);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task The_archive_round_trips_everything_a_bug_report_needs()
    {
        var root = NewDirectory();
        try
        {
            var history = new CloudTransferHistoryRepository(Path.Combine(root, "history.json"));
            var id = Guid.NewGuid();
            await history.AppendAsync([
                new CloudTransferHistoryEntry(id, CloudTransferDirection.Copy, SourceUri, "a.bin", "", 10,
                    CloudTransferState.Failed, DateTimeOffset.UtcNow, 3, "上传失败", "operations/copyfile · NotFound · 诊断 ID abc",
                    TargetUri, "kept/a.bin")
            ]);

            var entry = Assert.Single(await history.QueryAsync());
            Assert.Equal(id, entry.Id);
            Assert.Equal(3, entry.Attempt);
            Assert.Equal("上传失败", entry.Error);
            Assert.Contains("诊断 ID abc", entry.ErrorDetail!);
            Assert.Equal("kept/a.bin", entry.TargetPath);
            Assert.False(entry.Succeeded);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task The_archive_is_newest_first_and_capped()
    {
        var root = NewDirectory();
        try
        {
            var history = new CloudTransferHistoryRepository(Path.Combine(root, "history.json"), maximumEntries: 3);
            var start = DateTimeOffset.UtcNow;
            for (var index = 0; index < 5; index++)
            {
                await history.AppendAsync([Entry(start.AddMinutes(index), index)]);
            }

            var entries = await history.QueryAsync();

            // Newest first, and the two oldest fell off the end rather than the file growing without bound.
            Assert.Equal(3, entries.Count);
            Assert.Equal([4, 3, 2], entries.Select(entry => (int)entry.TotalBytes));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task A_corrupt_or_empty_archive_reads_as_empty_so_the_window_still_opens()
    {
        var root = NewDirectory();
        try
        {
            var path = Path.Combine(root, "history.json");
            var history = new CloudTransferHistoryRepository(path);
            Assert.Empty(await history.QueryAsync());

            await File.WriteAllTextAsync(path, "{ not json at all");
            Assert.Empty(await history.QueryAsync());

            await File.WriteAllTextAsync(path, "");
            Assert.Empty(await history.QueryAsync());

            await history.ClearAsync();
            Assert.False(File.Exists(path));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task An_entry_written_before_the_new_fields_existed_still_reads()
    {
        // The archive is a file in the user's app data directory, so a previous release's copy has to keep
        // loading rather than being treated as corrupt.
        var root = NewDirectory();
        try
        {
            var path = Path.Combine(root, "history.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[]
            {
                new
                {
                    Id = Guid.NewGuid(),
                    Direction = CloudTransferDirection.Download,
                    RemoteUri = SourceUri,
                    RemotePath = "a.bin",
                    LocalPath = @"C:\tmp\a.bin",
                    TotalBytes = 10L,
                    State = CloudTransferState.Completed,
                    ArchivedUtc = DateTimeOffset.UtcNow
                }
            }));

            var entry = Assert.Single(await new CloudTransferHistoryRepository(path).QueryAsync());

            Assert.Equal(1, entry.Attempt);
            Assert.Null(entry.ErrorDetail);
            Assert.Null(entry.TargetUri);
            Assert.True(entry.Succeeded);
        }
        finally { TryDelete(root); }
    }

    // ---- The one place the settings reach the queue ----

    [Fact]
    public void Applying_the_settings_puts_the_rate_the_window_and_the_retries_on_the_queue()
    {
        // One place rather than two: the queue is application-owned and both the browser window and the
        // transfer window reach it, so if each applied the settings itself whichever loaded last would win.
        var queue = new CloudTransferQueue(new CloudTransferJournalStore(NewDirectory()));
        var settings = new ApplicationSettings
        {
            CloudMaxConcurrentTransfers = 5,
            CloudRetryCount = 4,
            CloudUploadLimitBytesPerSecond = 1024 * 1024,
            CloudDownloadLimitBytesPerSecond = 0,
            CloudTransferTimeWindow = new CloudTransferTimeWindow(new TimeOnly(1, 0), new TimeOnly(5, 0))
        };

        CloudTransferPolicy.Apply(queue, settings);

        Assert.Equal(5, queue.Concurrency);
        Assert.Equal(4, queue.MaxRetries);
        Assert.Equal(new CloudTransferLimits(1024 * 1024, 0), queue.Limits);
        Assert.Equal(new CloudTransferTimeWindow(new TimeOnly(1, 0), new TimeOnly(5, 0)), queue.TimeWindow);
        // 传输完成后动作 is deliberately not applied here: it belongs to the window that has to confirm it.
        Assert.Equal("限速 上传 1M/s · 下载 不限", queue.LimitText);
    }

    private static CloudTransferHistoryEntry Entry(DateTimeOffset archivedUtc, int index) =>
        new(Guid.NewGuid(), CloudTransferDirection.Copy, SourceUri, $"{index}.bin", "", index,
            CloudTransferState.Completed, archivedUtc);

    /// <summary>A queue whose transfers are all parked by a window the clock is currently outside.</summary>
    private static CloudTransferQueue ParkedQueue(string journalRoot, FakeRclone fake) =>
        new(new CloudTransferJournalStore(journalRoot), _ => Task.FromResult(Client(fake)))
        {
            Clock = () => new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero),
            TimeWindow = new CloudTransferTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0))
        };

    private static RcloneRcClient Client(FakeRclone fake) =>
        new(new HttpClient(fake) { BaseAddress = new Uri("http://rc.test/") }, new Uri("http://rc.test/"), "user", "pass");

    private static Task WaitUntilFinished(CloudTransferTask task) =>
        WaitUntil(() => task.IsFinished, $"transfer {task.Display} never settled.");

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
        var path = Path.Combine(Path.GetTempPath(), "fengsync-queue-policy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A stand-in for rclone's RC API. Copies complete immediately unless the test holds them, and every
    /// destination is recorded in the order it was asked for — which is how "what the user sees at the top runs
    /// first" becomes assertable.
    /// </summary>
    /// <param name="hold">When true, jobs stay running until <see cref="Release"/> is called.</param>
    private sealed class FakeRclone(bool hold = false) : HttpMessageHandler
    {
        private readonly ManualResetEventSlim _released = new(!hold);
        private readonly object _gate = new();
        private readonly List<string> _copied = [];

        /// <summary>Destinations asked for, in order.</summary>
        public IReadOnlyList<string> Copied
        {
            get { lock (_gate) return _copied.ToArray(); }
        }

        /// <summary>Lets held jobs finish.</summary>
        public void Release() => _released.Set();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var operation = request.RequestUri!.AbsolutePath.TrimStart('/');
            var answer = operation switch
            {
                "operations/copyfile" => Copy(body),
                "job/status" => JobStatus(),
                "core/stats" => "{\"bytes\":10,\"totalBytes\":10}",
                _ => "{}"
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(answer, Encoding.UTF8, "application/json")
            };
        }

        private string Copy(string body)
        {
            using var document = JsonDocument.Parse(body);
            var remote = document.RootElement.TryGetProperty("dstRemote", out var value) ? value.GetString() ?? "" : "";
            lock (_gate) _copied.Add(Path.GetFileName(remote));
            return "{\"jobid\":1}";
        }

        private string JobStatus() => _released.IsSet
            ? "{\"finished\":true,\"success\":true,\"output\":{}}"
            : "{\"running\":true,\"finished\":false}";
    }
}
