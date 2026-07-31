using FengSync.Core.Mount;

namespace FengSync.Tests.Mount;

public sealed class MountSessionStoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "fengsync-mount-sessions-" + Guid.NewGuid().ToString("N"));
    private readonly string _sessionsPath;

    public MountSessionStoreTests()
    {
        Directory.CreateDirectory(_tempRoot);
        _sessionsPath = Path.Combine(_tempRoot, "sessions.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public async Task LoadAsync_returns_empty_list_when_file_does_not_exist()
    {
        var store = new MountSessionStore(_sessionsPath);
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task SaveAsync_writes_records_atomically_and_round_trips()
    {
        var store = new MountSessionStore(_sessionsPath);
        var record = new MountSessionRecord(Guid.NewGuid(), "driveA", "Google Drive", "X:", MountTargetKind.DriveLetter, 4242, DateTimeOffset.UtcNow, MountSessionStatus.Active);
        await store.SaveAsync([record]);

        Assert.True(File.Exists(_sessionsPath));
        var loaded = await store.LoadAsync();
        var match = Assert.Single(loaded);
        Assert.Equal(record, match);
    }

    [Fact]
    public async Task LoadAsync_preserves_active_status_so_normal_round_trips_remain_intact()
    {
        // Active records written in this run should NOT be silently mutated on read; promotion to
        // Orphaned is the caller's responsibility and happens only when the PID is no longer alive.
        var record = new MountSessionRecord(Guid.NewGuid(), "driveA", "Google Drive", "X:", MountTargetKind.DriveLetter, 9999, DateTimeOffset.UtcNow, MountSessionStatus.Active);
        await File.WriteAllTextAsync(_sessionsPath, "[{\"Id\":\"" + record.Id + "\",\"RemoteName\":\"driveA\",\"Provider\":\"Google Drive\",\"MountPoint\":\"X:\",\"Kind\":0,\"Pid\":9999,\"StartedUtc\":\"2024-01-01T00:00:00Z\",\"Status\":0}]");

        var store = new MountSessionStore(_sessionsPath);
        var loaded = await store.LoadAsync();
        var match = Assert.Single(loaded);
        Assert.Equal(MountSessionStatus.Active, match.Status);
    }

    [Fact]
    public async Task PromoteActiveToOrphanedAsync_promotes_records_with_dead_pids_only()
    {
        var alive = new MountSessionRecord(Guid.NewGuid(), "alive", "Google Drive", "X:", MountTargetKind.DriveLetter, 100, DateTimeOffset.UtcNow, MountSessionStatus.Active);
        var dead = new MountSessionRecord(Guid.NewGuid(), "dead", "SFTP", "Y:", MountTargetKind.DriveLetter, 200, DateTimeOffset.UtcNow, MountSessionStatus.Active);
        var store = new MountSessionStore(_sessionsPath);
        await store.SaveAsync([alive, dead]);

        var changed = await store.PromoteActiveToOrphanedAsync(new HashSet<int> { 100 });
        Assert.Equal(1, changed);

        var loaded = (await store.LoadAsync()).ToDictionary(x => x.RemoteName);
        Assert.Equal(MountSessionStatus.Active, loaded["alive"].Status);
        Assert.Equal(MountSessionStatus.Orphaned, loaded["dead"].Status);
    }

    [Fact]
    public async Task SaveAsync_uses_a_temporary_file_during_write()
    {
        var store = new MountSessionStore(_sessionsPath);
        await store.SaveAsync([]);
        Assert.False(File.Exists(_sessionsPath + ".tmp"));
    }

    [Fact]
    public async Task LoadAsync_returns_empty_list_when_corrupt_json_is_encountered()
    {
        await File.WriteAllTextAsync(_sessionsPath, "{not-json}");
        var store = new MountSessionStore(_sessionsPath);
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task RemoveByPidAsync_removes_only_matching_record()
    {
        var a = new MountSessionRecord(Guid.NewGuid(), "a", "SFTP", "X:", MountTargetKind.DriveLetter, 1, DateTimeOffset.UtcNow, MountSessionStatus.Active);
        var b = new MountSessionRecord(Guid.NewGuid(), "b", "SFTP", "Y:", MountTargetKind.DriveLetter, 2, DateTimeOffset.UtcNow, MountSessionStatus.Active);
        var store = new MountSessionStore(_sessionsPath);
        await store.SaveAsync([a, b]);

        await store.RemoveByPidAsync(1);

        var loaded = await store.LoadAsync();
        var remaining = Assert.Single(loaded);
        Assert.Equal("b", remaining.RemoteName);
    }
}