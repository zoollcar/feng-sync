using System.Net;
using System.Text;
using System.Text.Json;
using FengSync.Core;
using FengSync.Services;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// The queue's remote→remote copy direction (云端之间复制), driven against a stand-in for rclone's RC API.
/// A copy is the one direction that must stay out of Feng Sync's staging machinery: rclone moves the bytes
/// between two remotes, so this process never holds them, and all the queue controls is the payload it sends
/// and what it refuses to send. A fake is the honest instrument for that; what real rclone leaves at its
/// destination is pinned in <c>ServerSideCopyTests</c>.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudTransferCopyTests
{
    private const string SourceUri = "sftp://src/team";
    private const string TargetUri = "sftp://dst/archive";
    private const string SourcePath = "big.bin";
    private const string TargetPath = "copied/big.bin";
    private const long Size = 4 * 1024 * 1024;

    [Fact]
    public async Task A_queued_copy_asks_rclone_to_copy_the_object_straight_to_its_destination()
    {
        var fake = new FakeRclone();
        var root = NewDirectory();
        try
        {
            var queue = NewQueue(root, fake);
            var task = queue.EnqueueCopy(SourceUri, SourcePath, TargetUri, TargetPath, Size);

            await WaitUntilFinished(task);

            Assert.Equal(CloudTransferState.Completed, task.State);
            Assert.NotNull(fake.Copy);
            var copy = fake.Copy!;
            // The two remotes are built from the two URIs, and the destination is the user-visible path: not a
            // staging name, and not the target endpoint's own root repeated in front of it.
            Assert.Equal("src:", copy.SourceFs);
            Assert.Equal("team/big.bin", copy.SourceRemote);
            Assert.Equal("dst:", copy.DestinationFs);
            Assert.Equal("archive/copied/big.bin", copy.DestinationRemote);
            // The stats group is the task's own id, which is what makes the row's progress readable.
            Assert.Equal($"fengsync-{task.Id:N}", copy.StatsGroup);
            // The destination directory is created at the target, not at the source.
            Assert.Equal("archive/copied", Assert.Single(fake.CreatedDirectories));
            // A copy that succeeded has nothing to publish and nothing to clean up, while an upload or a
            // download does: those calls would rename or delete the user's object.
            Assert.False(fake.Sent("operations/movefile"));
            Assert.False(fake.Sent("operations/deletefile"));
            Assert.DoesNotContain(fake.Bodies, body => body.Contains("fengsync.partial", StringComparison.Ordinal));
            // Only a job that finished successfully materialises the destination object.
            Assert.Equal(["archive/copied/big.bin"], fake.Materialised);
            Assert.True(queue.HasCopyTasks);
            Assert.True(queue.HasCompletedTasks);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task A_queued_copy_reads_its_progress_from_rclone_under_its_own_group()
    {
        // A copy's bytes never pass through this process, so core/stats for the task's group is the only
        // progress there is. Without this the row would sit at 0% for the whole transfer and then jump.
        var fake = new FakeRclone { RunningPolls = 6 };
        var root = NewDirectory();
        try
        {
            var queue = NewQueue(root, fake);
            var task = queue.EnqueueCopy(SourceUri, SourcePath, TargetUri, TargetPath, Size);

            await WaitUntilFinished(task);

            Assert.NotNull(fake.Copy);
            var copy = fake.Copy!;
            Assert.True(fake.Sent("core/stats"), "the queue never polled the copy's progress.");
            Assert.Equal(copy.StatsGroup, fake.StatsGroup);
            Assert.Equal(Size, task.CompletedBytes);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task A_failed_copy_reports_the_backend_and_writes_no_destination()
    {
        // The safety question B0 has to answer: a copy skips staging, so a failure must not leave a
        // half-written object under the user-visible name. The stand-in models what the bundled rclone was
        // measured to do — the destination name appears only when the job finishes — and the queue must not
        // add a publish or a delete of its own on top of that.
        var fake = new FakeRclone { JobFails = "sftp: server refused the copy" };
        var root = NewDirectory();
        try
        {
            var queue = NewQueue(root, fake);
            queue.MaxRetries = 0;
            var task = queue.EnqueueCopy(SourceUri, SourcePath, TargetUri, TargetPath, Size);

            await WaitUntilFinished(task);

            Assert.Equal(CloudTransferState.Failed, task.State);
            Assert.False(string.IsNullOrWhiteSpace(task.Error));
            Assert.Contains("server refused the copy", task.ErrorDetail ?? "", StringComparison.Ordinal);
            Assert.Empty(fake.Materialised);
            Assert.False(fake.Sent("operations/movefile"));
            Assert.False(fake.Sent("operations/deletefile"));
            // Nothing of ours is discarded either, because nothing of ours was ever created.
            Assert.Equal("重试", task.RestartText);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task A_cancelled_copy_stops_rclone_s_job_and_leaves_nothing_to_continue()
    {
        // Cancelling has to reach rclone, or the job keeps moving bytes between the two clouds after the row
        // says 已取消. The stand-in never finishes the job on its own, so the transfer can only end by way of
        // job/stop.
        var fake = new FakeRclone { RunningPolls = int.MaxValue };
        var root = NewDirectory();
        try
        {
            var queue = NewQueue(root, fake);
            var task = queue.EnqueueCopy(SourceUri, SourcePath, TargetUri, TargetPath, Size);
            await WaitUntil(() => fake.Sent("core/stats"), "the queue never started polling the copy's stats");

            queue.Cancel(task);

            await WaitUntilFinished(task);
            Assert.Equal(CloudTransferState.Cancelled, task.State);
            await WaitUntil(() => fake.Sent("job/stop"), "cancelling the copy never told rclone to stop the job");
            Assert.Empty(fake.Materialised);
            // A cancelled copy has no bytes a later attempt could build on, so the row promises a retry.
            task.State = CloudTransferState.Paused;
            Assert.Equal("重试", task.RestartText);
            Assert.False(task.CanContinueFromStaging);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task A_copy_without_a_destination_is_refused_before_rclone_is_asked()
    {
        // A copy gets here only through a hand-edited journal file, and an empty destination would address
        // the target endpoint's root — so it must fail rather than send a payload rclone reads as "copy into /".
        var fake = new FakeRclone();
        var root = NewDirectory();
        try
        {
            var queue = NewQueue(root, fake);
            var task = new CloudTransferTask(CloudTransferDirection.Copy, SourceUri, SourcePath, string.Empty, Size);
            queue.Tasks.Add(task);
            task.State = CloudTransferState.Cancelled;

            // A restored journal row is how a task with no live transfer is started.
            queue.Retry(task);

            await WaitUntilFinished(task);
            Assert.Equal(CloudTransferState.Failed, task.State);
            Assert.Contains("缺少目标", task.Error ?? "", StringComparison.Ordinal);
            Assert.Empty(fake.Calls);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task A_copy_returns_from_its_journal_with_both_ends()
    {
        // The destination is a second endpoint, not a local path: storing it in LocalPath would make the next
        // release read the row as a download and create a directory named after a remote.
        var root = NewDirectory();
        try
        {
            var journal = new CloudTransferJournalStore(root);
            var record = new CloudTransferRecord(Guid.NewGuid(), CloudTransferDirection.Copy, SourceUri, SourcePath,
                string.Empty, Size, CloudTransferState.Cancelled, null, null, TargetUri, TargetPath);
            await journal.SaveAsync(record);

            var queue = new CloudTransferQueue(journal);
            Assert.Equal(1, await queue.LoadResumableAsync());

            var restored = Assert.Single(queue.Tasks);
            Assert.Equal(CloudTransferDirection.Copy, restored.Direction);
            Assert.Equal(TargetUri, restored.TargetUri);
            Assert.Equal(TargetPath, restored.TargetPath);
            Assert.Equal("", restored.LocalPath);
            Assert.True(queue.HasCopyTasks);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void A_copy_row_says_what_a_copy_can_and_cannot_do()
    {
        var task = new CloudTransferTask(CloudTransferDirection.Copy, SourceUri, "team/report.pdf", string.Empty,
            2 * 1024 * 1024, TargetUri, TargetPath);

        // The name shown is the object being copied; a copy has no local file to name.
        Assert.Equal("复制 · report.pdf", task.Display);
        Assert.Equal("复制", task.DirectionText);
        Assert.Equal("report.pdf", task.FileName);
        Assert.False(task.CanContinueFromStaging);
        // Only a download leaves bytes behind, so only a paused download is 继续.
        task.State = CloudTransferState.Paused;
        Assert.Equal("重试", task.RestartText);
        task.State = CloudTransferState.Cancelled;
        Assert.Contains("从头", task.StatusText, StringComparison.Ordinal);

        // The two directions that do continue from a pause must keep saying so.
        var download = new CloudTransferTask(CloudTransferDirection.Download, SourceUri, "a.txt", @"C:\tmp\a.txt", 10)
        {
            State = CloudTransferState.Paused
        };
        Assert.Equal("继续", download.RestartText);
        Assert.True(download.CanContinueFromStaging);
        Assert.Equal("下载", download.DirectionText);
        Assert.Equal("上传", new CloudTransferTask(CloudTransferDirection.Upload, SourceUri, "a.txt", @"C:\tmp\a.txt", 10).DirectionText);
    }

    [Fact]
    public void A_journal_written_before_the_destination_existed_still_reads()
    {
        // The new record fields are optional for the same reason ErrorDetail was: a journal file written by
        // the previous release carries neither, and reading one must not fail the whole restart.
        var legacy = JsonSerializer.Serialize(new
        {
            Id = Guid.NewGuid(),
            Direction = CloudTransferDirection.Download,
            RemoteUri = "s3://archive",
            RemotePath = "a.txt",
            LocalPath = @"C:\tmp\a.txt",
            TotalBytes = 10L,
            State = CloudTransferState.Cancelled,
            Error = (string?)null
        });

        var record = JsonSerializer.Deserialize<CloudTransferRecord>(legacy);
        Assert.NotNull(record);
        Assert.Null(record!.TargetUri);
        Assert.Null(record.TargetPath);

        var restored = new CloudTransferTask(record.Direction, record.RemoteUri, record.RemotePath, record.LocalPath,
            record.TotalBytes, record.TargetUri, record.TargetPath);
        Assert.Equal(CloudTransferDirection.Download, restored.Direction);
        Assert.True(restored.CanContinueFromStaging);
        Assert.Equal("继续", new CloudTransferTask(record.Direction, record.RemoteUri, record.RemotePath,
            record.LocalPath, record.TotalBytes, record.TargetUri, record.TargetPath)
        {
            State = CloudTransferState.Paused
        }.RestartText);
    }

    private static CloudTransferQueue NewQueue(string journalRoot, FakeRclone fake)
    {
        var http = new HttpClient(fake) { BaseAddress = new Uri("http://rc.test/") };
        var client = new RcloneRcClient(http, new Uri("http://rc.test/"), "user", "pass");
        // An isolated journal and an injected RC client: the queue's default factory would resolve the
        // application's daemon, and these tests are about the payload the queue sends.
        return new CloudTransferQueue(new CloudTransferJournalStore(journalRoot), _ => Task.FromResult(client));
    }

    /// <summary>Waits for the queue to report this transfer settled; the state itself is what a test asserts.</summary>
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
        var path = Path.Combine(Path.GetTempPath(), "fengsync-cloud-copy-tests", Guid.NewGuid().ToString("N"));
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
    /// A stand-in for rclone's RC endpoint that models what the bundled v1.75.0 was measured to do for
    /// <c>operations/copyfile</c>: an async job polled through <c>job/status</c>, whose bytes are reported by
    /// <c>core/stats</c> for its group, and a destination object that exists only once the job has finished.
    /// Every call is recorded so the payload — and the calls that must never be made — can be asserted.
    /// </summary>
    private sealed class FakeRclone : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<(string Operation, string Body)> _recorded = [];
        private readonly List<string> _directories = [];
        private readonly List<string> _materialised = [];
        private int _pollsLeft;

        /// <summary>When set, the copy job finishes unsuccessful with this error text.</summary>
        public string? JobFails { get; init; }
        /// <summary>How many <c>job/status</c> polls answer "still running" before the job finishes.</summary>
        public int RunningPolls { get; init; }
        public CopyPayload? Copy { get; private set; }
        public string? StatsGroup { get; private set; }

        public IReadOnlyList<(string Operation, string Body)> Calls
        {
            get { lock (_gate) return _recorded.ToArray(); }
        }
        public IReadOnlyList<string> Bodies
        {
            get { lock (_gate) return _recorded.Select(call => call.Body).ToArray(); }
        }
        public IReadOnlyList<string> CreatedDirectories
        {
            get { lock (_gate) return _directories.ToArray(); }
        }
        /// <summary>Destination objects that exist, in the order the backend would have written them.</summary>
        public IReadOnlyList<string> Materialised
        {
            get { lock (_gate) return _materialised.ToArray(); }
        }

        public bool Sent(string operation) => Calls.Any(call => call.Operation.EndsWith(operation, StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var operation = request.RequestUri!.AbsolutePath.TrimStart('/');
            lock (_gate) _recorded.Add((operation, body));
            var answer = operation switch
            {
                "operations/copyfile" => CopyFile(body),
                "job/status" => JobStatus(),
                "core/stats" => Stats(body),
                "operations/mkdir" => Mkdir(body),
                _ => "{}"
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(answer, Encoding.UTF8, "application/json")
            };
        }

        private string CopyFile(string body)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            lock (_gate)
            {
                Copy = new CopyPayload(Read(root, "srcFs"), Read(root, "srcRemote"), Read(root, "dstFs"),
                    Read(root, "dstRemote"), Read(root, "_group"));
                _pollsLeft = RunningPolls;
            }
            // rclone answers an _async call with a job id and does the work in the background.
            return "{\"jobid\":1}";
        }

        private string JobStatus()
        {
            string? failure;
            lock (_gate)
            {
                if (_pollsLeft > 0)
                {
                    if (_pollsLeft != int.MaxValue) _pollsLeft--;
                    return "{\"running\":true,\"finished\":false}";
                }
                failure = JobFails;
                if (failure is null && Copy is not null) _materialised.Add(Copy.DestinationRemote);
            }
            return failure is null
                ? "{\"finished\":true,\"success\":true,\"output\":{}}"
                : $"{{\"finished\":true,\"success\":false,\"error\":{JsonSerializer.Serialize(failure)}}}";
        }

        private string Stats(string body)
        {
            using var document = JsonDocument.Parse(body);
            lock (_gate) StatsGroup = Read(document.RootElement, "group");
            // The counters rclone keeps for one group are a copy's only source of progress.
            return "{\"bytes\":1048576,\"totalBytes\":4194304,\"speed\":1048576}";
        }

        private string Mkdir(string body)
        {
            using var document = JsonDocument.Parse(body);
            lock (_gate) _directories.Add(Read(document.RootElement, "remote"));
            return "{}";
        }

        private static string Read(JsonElement source, string name) =>
            source.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
    }

    /// <summary>The payload of an <c>operations/copyfile</c> job.</summary>
    public sealed record CopyPayload(string SourceFs, string SourceRemote, string DestinationFs, string DestinationRemote, string StatsGroup);
}
