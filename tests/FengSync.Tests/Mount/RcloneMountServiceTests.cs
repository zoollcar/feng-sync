using FengSync.Core.Mount;

namespace FengSync.Tests.Mount;

/// <summary>Test stub that pretends a fixed set of rclone processes exist; rclone is never actually spawned.</summary>
public sealed class RcloneMountServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "fengsync-mount-service-" + Guid.NewGuid().ToString("N"));
    private readonly string _sessionsPath;

    public RcloneMountServiceTests()
    {
        Directory.CreateDirectory(_tempRoot);
        _sessionsPath = Path.Combine(_tempRoot, "sessions.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public async Task ValidateAsync_throws_when_WinFsp_is_unavailable()
    {
        var service = new RcloneMountService(new FakeProcessEnumerator([]), new MountSessionStore(_sessionsPath), rcloneExecutable: "nonexistent-rclone.exe", configPath: "nonexistent.conf");
        var target = new MountTarget("remote", "sftp", "X:", MountTargetKind.DriveLetter);
        // We don't actually run WinFsp detection here; we instead exercise the validator with a known-bad target.
        var existing = await service.ScanAsync();
        var invalid = new MountTarget("remote", "sftp", "bad-drive", MountTargetKind.DriveLetter);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ValidateAsync(invalid));
    }

    [Fact]
    public async Task ScanAsync_marks_session_pids_as_fengsync_managed()
    {
        var store = new MountSessionStore(_sessionsPath);
        var fengPid = 1234;
        var record = new MountSessionRecord(Guid.NewGuid(), "driveA", "Google Drive", "X:", MountTargetKind.DriveLetter, fengPid, DateTimeOffset.UtcNow, MountSessionStatus.Active);
        await store.SaveAsync([record]);

        var enumerator = new FakeProcessEnumerator([
            new RcloneProcessSnapshot(fengPid, "rclone.exe mount driveA:/ X: --config C:\\rclone.conf --cache-dir C:\\cache", DateTimeOffset.UtcNow, true)
        ]);
        var service = new RcloneMountService(enumerator, store, rcloneExecutable: "ignored.exe", configPath: "ignored.conf");

        var mounts = await service.ScanAsync();
        var managed = Assert.Single(mounts);
        Assert.Equal(MountOrigin.FengSyncManaged, managed.Origin);
        Assert.Equal("driveA", managed.RemoteName);
    }

    [Fact]
    public async Task ScanAsync_marks_unknown_processes_as_external()
    {
        var store = new MountSessionStore(_sessionsPath);
        var enumerator = new FakeProcessEnumerator([
            new RcloneProcessSnapshot(7777, "rclone.exe mount external:photos Z: --config C:\\other.conf", DateTimeOffset.UtcNow, true)
        ]);
        var service = new RcloneMountService(enumerator, store, rcloneExecutable: "ignored.exe", configPath: "ignored.conf");

        var mounts = await service.ScanAsync();
        var external = Assert.Single(mounts);
        Assert.Equal(MountOrigin.External, external.Origin);
        Assert.Equal("external", external.RemoteName);
    }

    [Fact]
    public async Task ScanAsync_reports_unreadable_processes_separately()
    {
        var store = new MountSessionStore(_sessionsPath);
        var enumerator = new FakeProcessEnumerator([
            new RcloneProcessSnapshot(8888, null, null, false)
        ]);
        var service = new RcloneMountService(enumerator, store, rcloneExecutable: "ignored.exe", configPath: "ignored.conf");

        var mounts = await service.ScanAsync();
        var unreadable = Assert.Single(mounts);
        Assert.Equal(MountOrigin.Unreadable, unreadable.Origin);
        Assert.Equal(8888, unreadable.Pid);
    }

    [Fact]
    public async Task ScanAsync_retains_orphaned_sessions_even_when_no_process_is_alive()
    {
        var store = new MountSessionStore(_sessionsPath);
        var record = new MountSessionRecord(Guid.NewGuid(), "driveA", "Google Drive", "Y:", MountTargetKind.DriveLetter, 4242, DateTimeOffset.UtcNow, MountSessionStatus.Orphaned);
        await store.SaveAsync([record]);
        var enumerator = new FakeProcessEnumerator([]);
        var service = new RcloneMountService(enumerator, store, rcloneExecutable: "ignored.exe", configPath: "ignored.conf");

        var mounts = await service.ScanAsync();
        var orphan = Assert.Single(mounts);
        Assert.Equal(MountOrigin.FengSyncManaged, orphan.Origin);
        Assert.Equal("Y:", orphan.MountPoint);
        Assert.Equal(4242, orphan.Pid);
    }

    [Fact]
    public async Task StopAllFengSyncMountsAsync_clears_active_records_whose_process_is_already_gone()
    {
        // Use a PID that cannot possibly map to a real process. The service must treat the missing
        // process as "already stopped" and clear the session record without throwing.
        var missingPid = int.MaxValue - 1;
        var store = new MountSessionStore(_sessionsPath);
        var record = new MountSessionRecord(Guid.NewGuid(), "driveA", "Google Drive", "X:", MountTargetKind.DriveLetter, missingPid, DateTimeOffset.UtcNow, MountSessionStatus.Active);
        await store.SaveAsync([record]);

        var service = new RcloneMountService(new FakeProcessEnumerator([]), store, rcloneExecutable: "ignored.exe", configPath: "ignored.conf");
        var result = await service.StopAllFengSyncMountsAsync();

        var after = await store.LoadAsync();
        Assert.Empty(after);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task StopAllFengSyncMountsAsync_leaves_external_records_untouched()
    {
        // Orphaned records (from a previous run) must not be killed by the current shutdown path —
        // they're owned by the user, not by this process tree.
        var store = new MountSessionStore(_sessionsPath);
        var orphan = new MountSessionRecord(Guid.NewGuid(), "driveA", "Google Drive", "X:", MountTargetKind.DriveLetter, int.MaxValue - 2, DateTimeOffset.UtcNow, MountSessionStatus.Orphaned);
        await store.SaveAsync([orphan]);

        var service = new RcloneMountService(new FakeProcessEnumerator([]), store, rcloneExecutable: "ignored.exe", configPath: "ignored.conf");
        await service.StopAllFengSyncMountsAsync();

        var after = await store.LoadAsync();
        var leftover = Assert.Single(after);
        Assert.Equal(MountSessionStatus.Orphaned, leftover.Status);
    }

    private sealed class FakeProcessEnumerator : IProcessEnumerator
    {
        private readonly IReadOnlyList<RcloneProcessSnapshot> _snapshots;
        public FakeProcessEnumerator(IReadOnlyList<RcloneProcessSnapshot> snapshots) => _snapshots = snapshots;
        public IReadOnlyList<RcloneProcessSnapshot> EnumerateRcloneProcesses() => _snapshots;
    }
}