using System.Security.Cryptography;
using FengSync.Core;
using FengSync.Core.Execution;

namespace FengSync.Tests;

/// <summary>
/// Resume against the bundled rclone. rclone restarts every interrupted transfer from zero and its RC
/// API has no offset read or offset write, so continuing a transfer is Feng Sync's own behaviour.
/// These tests pin that behaviour: a deterministic staging file, an append that continues it, and the
/// checks that refuse to continue one that is no longer a prefix of the source.
/// </summary>
public sealed class ResumableDownloadTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-resume-" + Guid.NewGuid().ToString("N"));
    private string RemoteDirectory => Path.Combine(_root, "remote");
    private string LocalDirectory => Path.Combine(_root, "local");
    private RcloneDaemon? _daemon;
    private byte[] _content = [];

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(RemoteDirectory);
        Directory.CreateDirectory(LocalDirectory);
        _content = RandomNumberGenerator.GetBytes(4 * 1024 * 1024);
        await File.WriteAllBytesAsync(SourceObject, _content);
        var config = Path.Combine(_root, "rclone.conf");
        // A local backend behind an alias: the transfer path is the remote one (the bytes arrive over
        // the daemon's RC endpoint), which is what the resume logic has to work against.
        await File.WriteAllTextAsync(config, $"[source]\ntype = alias\nremote = {RemoteDirectory.Replace('\\', '/')}\n");
        _daemon = await RcloneDaemon.StartAsync(RclonePath(), config);
    }

    public async Task DisposeAsync()
    {
        if (_daemon is not null) await _daemon.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string SourceObject => Path.Combine(RemoteDirectory, "big.bin");
    private string StagingFile => Path.Combine(LocalDirectory, "big.bin.fengsync.partial");

    [Fact]
    public async Task An_interrupted_download_continues_from_its_staging_file()
    {
        var interrupted = new InterruptAfter(512 * 1024);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EndpointTransfer.CopyToStagingAsync(
            Source(), Target(), "big.bin", null, Guid.NewGuid(), _content.Length,
            progress: interrupted, resumeMinimumBytes: 0, ct: interrupted.Token));

        var staged = new FileInfo(StagingFile).Length;
        Assert.InRange(staged, 1, _content.Length - 1);
        Assert.Equal(0, interrupted.FirstReported);

        var resumed = new InterruptAfter(long.MaxValue);
        var temporary = await EndpointTransfer.CopyToStagingAsync(Source(), Target(), "big.bin", null, Guid.NewGuid(),
            _content.Length, progress: resumed, resumeMinimumBytes: 0, ct: resumed.Token);

        Assert.Equal("big.bin.fengsync.partial", temporary);
        // An append reports the offset it starts from, so this proves the bytes already on disk were
        // kept rather than downloaded again. It can be slightly below what the interrupted transfer
        // reported: a cancellation that lands inside a write leaves a tail that must be cut away.
        Assert.NotNull(resumed.FirstReported);
        Assert.InRange(resumed.FirstReported.Value, staged - 3 * 128 * 1024, staged);
        await EndpointTransfer.PublishAsync(Target(), temporary, "big.bin", overwrite: true);
        Assert.Equal(Hash(_content), Hash(await File.ReadAllBytesAsync(Path.Combine(LocalDirectory, "big.bin"))));
        Assert.False(File.Exists(StagingFile));
    }

    [Fact]
    public async Task A_published_download_carries_the_source_modification_time()
    {
        // Publishing is a rename, so the staging file's timestamp becomes the user-visible one, and
        // the post-copy verification compares fingerprints — size plus modification time, never a
        // hash. A download that leaves "now" on the file is therefore reported as "传输验证失败" even
        // though every byte arrived. Backdating the source makes "now" impossible to mistake for it.
        var sourceStamp = DateTime.UtcNow.AddHours(-6);
        File.SetLastWriteTimeUtc(SourceObject, sourceStamp);

        var temporary = await EndpointTransfer.CopyToStagingAsync(Source(), Target(), "big.bin", null, Guid.NewGuid(),
            _content.Length, resumeMinimumBytes: 0);
        await EndpointTransfer.PublishAsync(Target(), temporary, "big.bin", overwrite: true);

        var published = File.GetLastWriteTimeUtc(Path.Combine(LocalDirectory, "big.bin"));
        Assert.True(Math.Abs((published - sourceStamp).TotalSeconds) < 2,
            $"发布后的修改时间是 {published:o}，源文件是 {sourceStamp:o}。");
    }

    [Fact]
    public async Task A_file_below_the_threshold_restarts_instead_of_continuing()
    {
        var interrupted = new InterruptAfter(512 * 1024);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EndpointTransfer.CopyToStagingAsync(
            Source(), Target(), "big.bin", null, Guid.NewGuid(), _content.Length,
            progress: interrupted, resumeMinimumBytes: 0, ct: interrupted.Token));
        Assert.True(File.Exists(StagingFile));

        // One byte above the file: the threshold decides, and below it the staging file is dropped so
        // the file is transferred in a single pass.
        var restarted = new InterruptAfter(long.MaxValue);
        await EndpointTransfer.CopyToStagingAsync(Source(), Target(), "big.bin", null, Guid.NewGuid(), _content.Length,
            progress: restarted, resumeMinimumBytes: _content.Length + 1, ct: restarted.Token);

        Assert.Equal(0, restarted.FirstReported);
    }

    [Fact]
    public async Task A_file_exactly_at_the_threshold_continues_instead_of_restarting()
    {
        // The rule is "at least this big", so the boundary itself has to continue. Treating it as
        // "bigger than" would silently restart a file the user just configured to be resumable.
        var interrupted = new InterruptAfter(512 * 1024);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EndpointTransfer.CopyToStagingAsync(
            Source(), Target(), "big.bin", null, Guid.NewGuid(), _content.Length,
            progress: interrupted, resumeMinimumBytes: _content.Length, ct: interrupted.Token));

        var resumed = new InterruptAfter(long.MaxValue);
        await EndpointTransfer.CopyToStagingAsync(Source(), Target(), "big.bin", null, Guid.NewGuid(), _content.Length,
            progress: resumed, resumeMinimumBytes: _content.Length, ct: resumed.Token);

        Assert.NotNull(resumed.FirstReported);
        Assert.True(resumed.FirstReported.Value > 0,
            $"与阈值同大的文件被从头重传了（起始偏移 {resumed.FirstReported.Value}）。");
    }

    [Fact]
    public async Task A_staging_file_from_a_source_that_changed_is_not_continued()
    {
        var interrupted = new InterruptAfter(512 * 1024);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EndpointTransfer.CopyToStagingAsync(
            Source(), Target(), "big.bin", null, Guid.NewGuid(), _content.Length,
            progress: interrupted, resumeMinimumBytes: 0, ct: interrupted.Token));
        var staged = new FileInfo(StagingFile).Length;
        Assert.True(staged > 0);

        // Touching the object leaves every byte identical, so only the recorded source identity can
        // refuse the resume. Continuing here would splice an older prefix onto newer content.
        File.SetLastWriteTimeUtc(SourceObject, DateTime.UtcNow.AddMinutes(5));

        var restarted = new InterruptAfter(long.MaxValue);
        await EndpointTransfer.CopyToStagingAsync(Source(), Target(), "big.bin", null, Guid.NewGuid(), _content.Length,
            progress: restarted, resumeMinimumBytes: 0, ct: restarted.Token);

        Assert.Equal(0, restarted.FirstReported);
    }

    [Fact]
    public async Task A_torn_tail_is_cut_away_instead_of_restarting_the_whole_transfer()
    {
        var interrupted = new InterruptAfter(512 * 1024);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EndpointTransfer.CopyToStagingAsync(
            Source(), Target(), "big.bin", null, Guid.NewGuid(), _content.Length,
            progress: interrupted, resumeMinimumBytes: 0, ct: interrupted.Token));
        var staged = new FileInfo(StagingFile).Length;
        Assert.True(staged > 0);

        // A write interrupted mid-flight: the length still looks like a valid prefix, but the bytes at
        // the append point were never stored. Size and modification time cannot see this.
        await using (var file = new FileStream(StagingFile, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            file.Seek(staged - 1024, SeekOrigin.Begin);
            await file.WriteAsync(new byte[1024]);
        }

        var resumed = new InterruptAfter(long.MaxValue);
        var temporary = await EndpointTransfer.CopyToStagingAsync(Source(), Target(), "big.bin", null, Guid.NewGuid(),
            _content.Length, progress: resumed, resumeMinimumBytes: 0, ct: resumed.Token);

        // The unusable tail is dropped and the transfer continues: losing a few kilobytes must not
        // cost the whole file, but the published result still has to be exact.
        Assert.NotNull(resumed.FirstReported);
        Assert.InRange(resumed.FirstReported.Value, staged - 3 * 128 * 1024, staged - 1);
        await EndpointTransfer.PublishAsync(Target(), temporary, "big.bin", overwrite: true);
        Assert.Equal(Hash(_content), Hash(await File.ReadAllBytesAsync(Path.Combine(LocalDirectory, "big.bin"))));
    }

    [Fact]
    public async Task A_staging_file_without_a_recorded_identity_restarts_from_zero()
    {
        // Same name, unrelated bytes, and no record of which source produced them — exactly what a
        // leftover from an older build or a cleared application-data directory looks like.
        await File.WriteAllBytesAsync(StagingFile, RandomNumberGenerator.GetBytes(256 * 1024));

        var restarted = new InterruptAfter(long.MaxValue);
        var temporary = await EndpointTransfer.CopyToStagingAsync(Source(), Target(), "big.bin", null, Guid.NewGuid(),
            _content.Length, progress: restarted, resumeMinimumBytes: 0, ct: restarted.Token);

        Assert.Equal(0, restarted.FirstReported);
        await EndpointTransfer.PublishAsync(Target(), temporary, "big.bin", overwrite: true);
        Assert.Equal(Hash(_content), Hash(await File.ReadAllBytesAsync(Path.Combine(LocalDirectory, "big.bin"))));
    }

    [Fact]
    public async Task An_upload_never_continues_a_remote_staging_object()
    {
        var uploadSource = Path.Combine(_root, "upload");
        Directory.CreateDirectory(uploadSource);
        await File.WriteAllBytesAsync(Path.Combine(uploadSource, "big.bin"), _content);
        var source = new LocalEndpoint(uploadSource);
        var target = Source();
        var stale = Path.Combine(RemoteDirectory, "missing.bin.fengsync.partial");
        await File.WriteAllBytesAsync(stale, new byte[1024]);

        // Preparation runs before any byte moves, so a copy that then fails still proves what
        // preparation did. rclone has no append or offset write, so there is nothing to continue here.
        await Assert.ThrowsAnyAsync<Exception>(() => EndpointTransfer.CopyToStagingAsync(
            source, target, "missing.bin", null, Guid.NewGuid(), 1024, resumeMinimumBytes: 0));
        Assert.False(File.Exists(stale));

        // A real upload still publishes the whole object.
        var temporary = await EndpointTransfer.CopyToStagingAsync(source, target, "big.bin", null, Guid.NewGuid(),
            _content.Length, resumeMinimumBytes: 0);
        await EndpointTransfer.PublishAsync(target, temporary, "published.bin", overwrite: true);
        Assert.Equal(Hash(_content), Hash(await File.ReadAllBytesAsync(Path.Combine(RemoteDirectory, "published.bin"))));
        Assert.Empty(await target.ListTransferTemporaryFilesAsync());
    }

    [Fact]
    public async Task A_download_published_through_the_executor_passes_copy_verification()
    {
        // The post-copy check compares fingerprints — size and modification time — not hashes. Publishing
        // is a rename, so the staging file's time is what the user ends up with, which means a download
        // that leaves "now" on the staging file fails every run as 传输验证失败. rclone's own copy path
        // preserves the source time, so this path has to as well.
        var local = new LocalEndpoint(LocalDirectory);
        var remote = Source();
        var plan = new SyncPlan([new SyncOperation("big.bin", OperationKind.CopyRightToLeft, "test")]);
        var result = await new SyncExecutorV2().ExecuteAsync(await PlanSnapshot.CaptureAsync(plan, local, remote), local, remote, verifyCopies: true);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Operations.Select(x => $"{x.Path}: {x.Error}")));
        var published = Path.Combine(LocalDirectory, "big.bin");
        Assert.Equal(Hash(_content), Hash(await File.ReadAllBytesAsync(published)));
        // HTTP dates carry whole seconds, so the published time is the source's truncated to the second
        // — which is inside the tolerance the fingerprint comparison allows.
        var drift = Math.Abs((new FileInfo(published).LastWriteTimeUtc - new FileInfo(SourceObject).LastWriteTimeUtc).TotalSeconds);
        Assert.True(drift < 2, $"Published download carried a modification time {drift:N3}s away from the source object.");
    }

    private RcloneEndpoint Source() => Assert.IsType<RcloneEndpoint>(EndpointFactory.CreateWithClient("sftp://source", _daemon!.Client));
    private LocalEndpoint Target() => new(LocalDirectory);
    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    private static string RclonePath()
    {
        try { return BundledRclone.ExecutablePath; }
        catch (FileNotFoundException) { }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "FengSync", "Assets", "rclone", "rclone.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("找不到内置 rclone.exe，无法运行续传测试。");
    }

    /// <summary>Reports progress synchronously and cancels the transfer once a byte threshold is reached.</summary>
    private sealed class InterruptAfter(long bytes) : IProgress<TransferProgress>
    {
        private readonly CancellationTokenSource _cancellation = new();
        public CancellationToken Token => _cancellation.Token;
        /// <summary>The first byte count reported, which is the offset the transfer started from.</summary>
        public long? FirstReported { get; private set; }

        public void Report(TransferProgress value)
        {
            FirstReported ??= value.BytesCompleted;
            if (value.BytesCompleted >= bytes) _cancellation.Cancel();
        }
    }
}
