using System.Text.Json;
using FengSync.Core.Mount;
using FengSync.Core.Rclone.Lifecycle;

namespace FengSync.Tests.Mount;

public sealed class RcloneMountServiceTests
{
    [Fact]
    public async Task ScanAsync_uses_rc_mounts_as_managed_truth()
    {
        var rc = new FakeLifecycleClient();
        rc.Mounts.Add(new("driveA:", "X:"));
        var service = new RcloneMountService(rc, new FakeProcessEnumerator([]));

        var mount = Assert.Single(await service.ScanAsync());

        Assert.Equal(MountOrigin.FengSyncManaged, mount.Origin);
        Assert.Equal("driveA", mount.RemoteName);
        Assert.True(mount.CanUnmount);
        Assert.Null(mount.Pid);
        Assert.Equal("mount/listmounts", Assert.Single(rc.Calls).Operation);
    }

    [Fact]
    public async Task ScanAsync_keeps_external_processes_read_only()
    {
        var rc = new FakeLifecycleClient();
        var processes = new FakeProcessEnumerator([
            new RcloneProcessSnapshot(7777, "rclone.exe mount external:photos Z: --config C:\\other.conf", DateTimeOffset.UtcNow, true)
        ]);
        var service = new RcloneMountService(rc, processes);

        var mount = Assert.Single(await service.ScanAsync());

        Assert.Equal(MountOrigin.External, mount.Origin);
        Assert.False(mount.CanUnmount);
        Assert.Equal(7777, mount.Pid);
    }

    [Fact]
    public async Task UnmountAsync_refuses_external_mount_without_rc_call()
    {
        var rc = new FakeLifecycleClient();
        var service = new RcloneMountService(rc, new FakeProcessEnumerator([]));
        var external = new MountInfo(42, "other", "rclone", "Z:", MountTargetKind.DriveLetter,
            DateTimeOffset.UtcNow, MountOrigin.External, true);

        var result = await service.UnmountAsync(external);

        Assert.False(result.AllStopped);
        Assert.Contains("外部程序", Assert.Single(result.Failures).Reason);
        Assert.Empty(rc.Calls);
    }

    [Fact]
    public async Task UnmountAsync_uses_mount_unmount_for_managed_mount()
    {
        var rc = new FakeLifecycleClient();
        rc.Mounts.Add(new("driveA:", "X:"));
        var service = new RcloneMountService(rc, new FakeProcessEnumerator([]));
        var managed = new MountInfo(null, "driveA", "Google Drive", "X:", MountTargetKind.DriveLetter,
            DateTimeOffset.UtcNow, MountOrigin.FengSyncManaged, true);

        var result = await service.UnmountAsync(managed);

        Assert.True(result.AllStopped);
        Assert.Contains(rc.Calls, x => x.Operation == "mount/unmount");
        Assert.Empty(rc.Mounts);
    }

    [Fact]
    public async Task StopAllFengSyncMountsAsync_only_stops_rc_owned_mounts()
    {
        var rc = new FakeLifecycleClient();
        rc.Mounts.AddRange([new("driveA:", "X:"), new("driveB:", "Y:")]);
        var service = new RcloneMountService(rc, new FakeProcessEnumerator([
            new RcloneProcessSnapshot(100, "rclone.exe mount external: Z:", DateTimeOffset.UtcNow, true)
        ]));

        var result = await service.StopAllFengSyncMountsAsync();

        Assert.True(result.AllStopped);
        Assert.Empty(rc.Mounts);
        Assert.Equal(2, rc.Calls.Count(x => x.Operation == "mount/unmount"));
    }

    [Fact]
    public async Task StopAllFengSyncMountsAsync_keeps_failed_mount_available_for_retry()
    {
        var rc = new FakeLifecycleClient();
        rc.Mounts.AddRange([new("driveA:", "X:"), new("driveB:", "Y:")]);
        rc.UnmountFailures.Add("X:");
        var service = new RcloneMountService(rc, new FakeProcessEnumerator([]));

        var firstResult = await service.StopAllFengSyncMountsAsync();

        Assert.False(firstResult.AllStopped);
        Assert.Equal("X:", Assert.Single(firstResult.Failures).MountPoint);
        Assert.Equal("X:", Assert.Single(rc.Mounts).MountPoint);

        rc.UnmountFailures.Clear();
        var retryResult = await service.StopAllFengSyncMountsAsync();

        Assert.True(retryResult.AllStopped);
        Assert.Empty(rc.Mounts);
    }

    private sealed class FakeLifecycleClient : IRcloneLifecycleClient
    {
        public List<(string Fs, string MountPoint)> Mounts { get; } = [];
        public List<(string Operation, JsonElement Payload)> Calls { get; } = [];
        public HashSet<string> UnmountFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<JsonElement> CallAsync(string operation, object payload, CancellationToken cancellationToken = default)
        {
            var json = JsonSerializer.SerializeToElement(payload);
            Calls.Add((operation, json));
            return Task.FromResult(operation switch
            {
                "mount/listmounts" => JsonSerializer.SerializeToElement(new
                {
                    mountPoints = Mounts.Select(x => new { x.Fs, x.MountPoint }).ToArray()
                }),
                "mount/unmount" => Unmount(json),
                "mount/types" => JsonSerializer.SerializeToElement(new { mountTypes = new[] { "mount" } }),
                _ => JsonSerializer.SerializeToElement(new { })
            });
        }

        private JsonElement Unmount(JsonElement payload)
        {
            var point = payload.GetProperty("mountPoint").GetString();
            if (point is not null && UnmountFailures.Contains(point))
                throw new InvalidOperationException("Unmount failed.");
            Mounts.RemoveAll(x => string.Equals(x.MountPoint, point, StringComparison.OrdinalIgnoreCase));
            return JsonSerializer.SerializeToElement(new { });
        }
    }

    private sealed class FakeProcessEnumerator(IReadOnlyList<RcloneProcessSnapshot> snapshots) : IProcessEnumerator
    {
        public IReadOnlyList<RcloneProcessSnapshot> EnumerateRcloneProcesses() => snapshots;
    }
}
