using System.Runtime.Versioning;
using System.Text.Json;
using FengSync.Core.Rclone.Lifecycle;
using FengSync.Core.Rclone.Diagnostics;

namespace FengSync.Core.Mount;

/// <summary>Outcome of an unmount attempt; failures are already safe for UI and log display.</summary>
public sealed record MountStopResult(IReadOnlyList<MountStopFailure> Failures)
{
    public bool AllStopped => Failures.Count == 0;
}

public sealed record MountStopFailure(string MountPoint, int Pid, string Reason);

/// <summary>
/// Owns mounts through rclone's mount/* JSON RC API. The private RC daemon is the source of truth for
/// Feng Sync mounts. WMI is retained only to show externally-owned mounts and is never used to stop one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RcloneMountService : IAsyncDisposable
{
    private static readonly TimeSpan MountStateTimeout = TimeSpan.FromSeconds(8);
    private readonly IRcloneLifecycleClient _rc;
    private readonly IProcessEnumerator _externalEnumerator;
    private readonly bool _ownsClient;
    private readonly Dictionary<string, ManagedMountMetadata> _managed = new(StringComparer.OrdinalIgnoreCase);

    public RcloneMountService(IRcloneLifecycleClient rc, IProcessEnumerator? externalEnumerator = null)
    {
        _rc = rc;
        _externalEnumerator = externalEnumerator ?? new WmiProcessEnumerator();
    }

    /// <summary>Compatibility constructor. Application code should inject its shared lifecycle host.</summary>
    public RcloneMountService(IProcessEnumerator? enumerator = null, MountSessionStore? store = null, string? rcloneExecutable = null, string? configPath = null)
    {
        _rc = new RcloneLifecycleHost();
        _externalEnumerator = enumerator ?? new WmiProcessEnumerator();
        _ownsClient = true;
    }

    public async Task<IReadOnlyList<MountInfo>> ScanAsync(CancellationToken ct = default)
    {
        var rcMounts = await ListManagedMountPointsAsync(ct).ConfigureAwait(false);
        var result = new List<MountInfo>();
        var managedPoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mount in rcMounts)
        {
            var point = NormalizeMountPoint(mount.MountPoint);
            managedPoints.Add(point);
            _managed.TryGetValue(point, out var metadata);
            var remoteName = metadata?.RemoteName ?? RemoteNameFromFs(mount.Fs);
            var kind = KindFromMountPoint(point);
            result.Add(new MountInfo(null, remoteName, metadata?.Provider ?? "rclone", point, kind,
                metadata?.StartedUtc, MountOrigin.FengSyncManaged, IsMountPointHealthy(point, kind)));
        }

        // External discovery is informational only. We parse enough command-line state to display it,
        // but never treat that text as authority and never terminate the process from this service.
        foreach (var process in _externalEnumerator.EnumerateRcloneProcesses())
        {
            if (process.Pid < 0) continue;
            if (!process.CommandLineReadable)
            {
                result.Add(new MountInfo(process.Pid, "(unreadable)", "?", "", MountTargetKind.Directory,
                    process.StartedUtc, MountOrigin.Unreadable, false));
                continue;
            }
            if (!RcloneCommandLineParser.TryParse(process.CommandLine, out var parsed)) continue;
            var point = NormalizeMountPoint(parsed.MountPoint);
            if (managedPoints.Contains(point)) continue;
            var kind = KindFromMountPoint(point);
            result.Add(new MountInfo(process.Pid, RemoteNameFromFs(parsed.RemoteSpec), "rclone", point, kind,
                process.StartedUtc, MountOrigin.External, IsMountPointHealthy(point, kind)));
        }

        return result.OrderBy(x => x.Origin == MountOrigin.FengSyncManaged ? 0 : 1)
            .ThenBy(x => x.RemoteName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<MountTarget> ValidateAsync(MountTarget target, CancellationToken ct = default)
    {
        var winFsp = WinFspDetector.Detect();
        if (!winFsp.Installed) throw new InvalidOperationException(winFsp.Summary);
        var existing = await ScanAsync(ct).ConfigureAwait(false);
        var validation = MountPointInspector.Validate(target.MountPoint, target.Kind, existing.Select(x => x.MountPoint).ToList());
        if (!validation.IsValid) throw new InvalidOperationException(validation.Error);

        var types = await _rc.CallAsync("mount/types", new { }, ct).ConfigureAwait(false);
        if (SelectMountImplementation(types) is null)
            throw new InvalidOperationException("当前 rclone 运行时没有可用的 mount 实现。请确认 WinFsp 已正确安装后重试。");
        return target;
    }

    public async Task<Guid> MountAsync(MountTarget target, CancellationToken ct = default)
    {
        await ValidateAsync(target, ct).ConfigureAwait(false);
        var mountTypes = await _rc.CallAsync("mount/types", new { }, ct).ConfigureAwait(false);
        var mountType = SelectMountImplementation(mountTypes)
            ?? throw new InvalidOperationException("当前 rclone 运行时没有可用的 mount 实现。");
        var sessionId = Guid.NewGuid();
        var cacheDirectory = MountOptions.CacheDirectoryFor(sessionId);
        Directory.CreateDirectory(cacheDirectory);
        JsonElement response;
        try
        {
            response = await _rc.CallAsync("mount/mount", new
            {
                fs = target.RemoteName + ":",
                mountPoint = target.MountPoint,
                mountType,
                mountOpt = new { },
                vfsOpt = MountOptions.CreateVfsOptions(),
                _config = MountOptions.CreateLocalConfiguration(cacheDirectory)
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteCache(cacheDirectory);
            throw;
        }

        var actualPoint = response.TryGetProperty("mountPoint", out var pointValue)
            ? pointValue.GetString() ?? target.MountPoint
            : target.MountPoint;
        actualPoint = NormalizeMountPoint(actualPoint);
        _managed[actualPoint] = new(target.RemoteName, target.Provider, sessionId, DateTimeOffset.UtcNow);
        if (!await WaitForManagedMountAsync(actualPoint, shouldExist: true, ct).ConfigureAwait(false))
        {
            _managed.Remove(actualPoint);
            try { await _rc.CallAsync("mount/unmount", new { mountPoint = actualPoint }, CancellationToken.None).ConfigureAwait(false); }
            catch { /* preserve the original structured failure */ }
            TryDeleteCache(cacheDirectory);
            throw new InvalidOperationException($"rclone 已接受挂载请求，但挂载点“{actualPoint}”未在预期时间内就绪。");
        }
        return sessionId;
    }

    public async Task<MountStopResult> UnmountAsync(MountInfo info, CancellationToken ct = default)
    {
        if (!info.CanUnmount)
            return new([new MountStopFailure(info.MountPoint, info.Pid ?? -1, "该挂载由外部程序管理；Feng Sync 仅显示它，不会结束或卸载它。")]);

        try
        {
            await _rc.CallAsync("mount/unmount", new { mountPoint = info.MountPoint }, ct).ConfigureAwait(false);
            if (!await WaitForManagedMountAsync(info.MountPoint, shouldExist: false, ct).ConfigureAwait(false))
                return new([new MountStopFailure(info.MountPoint, -1, "rclone 已接受取消请求，但挂载点仍然存在。")]);
            if (_managed.Remove(NormalizeMountPoint(info.MountPoint), out var metadata))
                TryDeleteCache(MountOptions.CacheDirectoryFor(metadata.SessionId));
            return new([]);
        }
        catch (Exception ex)
        {
            return new([new MountStopFailure(info.MountPoint, -1, DescribeFailure(ex, "mount/unmount"))]);
        }
    }

    public async Task<MountStopResult> StopAllFengSyncMountsAsync(CancellationToken ct = default)
    {
        var failures = new List<MountStopFailure>();
        IReadOnlyList<RcMount> mounts;
        try { mounts = await ListManagedMountPointsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { return new([new MountStopFailure("(RC)", -1, DescribeFailure(ex, "mount/listmounts"))]); }

        foreach (var mount in mounts)
        {
            var point = NormalizeMountPoint(mount.MountPoint);
            try { await _rc.CallAsync("mount/unmount", new { mountPoint = point }, ct).ConfigureAwait(false); }
            catch (Exception ex) { failures.Add(new MountStopFailure(point, -1, DescribeFailure(ex, "mount/unmount"))); }
            if (_managed.Remove(point, out var metadata)) TryDeleteCache(MountOptions.CacheDirectoryFor(metadata.SessionId));
        }
        return new(failures);
    }

    private async Task<IReadOnlyList<RcMount>> ListManagedMountPointsAsync(CancellationToken ct)
    {
        var response = await _rc.CallAsync("mount/listmounts", new { }, ct).ConfigureAwait(false);
        if (!response.TryGetProperty("mountPoints", out var values) || values.ValueKind != JsonValueKind.Array) return [];
        var result = new List<RcMount>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                var point = value.GetString();
                if (!string.IsNullOrWhiteSpace(point)) result.Add(new(point, ""));
                continue;
            }
            if (value.ValueKind != JsonValueKind.Object) continue;
            var pointValue = GetString(value, "MountPoint", "mountPoint", "MountedOn");
            if (string.IsNullOrWhiteSpace(pointValue)) continue;
            result.Add(new(pointValue, GetString(value, "Fs", "fs") ?? ""));
        }
        return result;
    }

    private async Task<bool> WaitForManagedMountAsync(string mountPoint, bool shouldExist, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + MountStateTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var mounts = await ListManagedMountPointsAsync(ct).ConfigureAwait(false);
            var exists = mounts.Any(x => NormalizeMountPoint(x.MountPoint).Equals(NormalizeMountPoint(mountPoint), StringComparison.OrdinalIgnoreCase));
            if (exists == shouldExist) return true;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static string? SelectMountImplementation(JsonElement response)
    {
        if (!response.TryGetProperty("mountTypes", out var types) || types.ValueKind != JsonValueKind.Array)
            return null;
        var available = types.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Where(value => value is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preference = OperatingSystem.IsWindows()
            ? new[] { "cmount", "mount", "mount2" }
            : new[] { "mount", "mount2", "cmount" };
        return preference.FirstOrDefault(available.Contains);
    }

    private static string? GetString(JsonElement value, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
            if (value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
                return property.GetString();
        return null;
    }

    private static MountTargetKind KindFromMountPoint(string point) =>
        point.Length == 2 && point[1] == ':' ? MountTargetKind.DriveLetter : MountTargetKind.Directory;

    private static string NormalizeMountPoint(string mountPoint) => mountPoint.TrimEnd('\\', '/');
    private static string RemoteNameFromFs(string fs)
    {
        var colon = fs.IndexOf(':');
        return colon > 0 ? fs[..colon] : string.IsNullOrWhiteSpace(fs) ? "(rclone)" : fs;
    }

    private static bool IsMountPointHealthy(string mountPoint, MountTargetKind kind)
    {
        try
        {
            if (kind == MountTargetKind.Directory) return Directory.Exists(mountPoint);
            return DriveInfo.GetDrives().Any(d => d.Name.TrimEnd('\\').Equals(mountPoint, StringComparison.OrdinalIgnoreCase) && d.IsReady);
        }
        catch { return false; }
    }

    private static void TryDeleteCache(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* cache cleanup is best effort */ }
    }

    private static string DescribeFailure(Exception exception, string context)
    {
        if (exception is not RcloneException rclone) return exception.Message;
        RcloneFailureLog.Write(rclone.Failure, context);
        return $"{rclone.Failure.UserMessage} {rclone.Failure.SuggestedAction}（诊断 ID：{rclone.Failure.CorrelationId}）";
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsClient && _rc is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record ManagedMountMetadata(string RemoteName, string Provider, Guid SessionId, DateTimeOffset StartedUtc);
    private sealed record RcMount(string MountPoint, string Fs);
}
