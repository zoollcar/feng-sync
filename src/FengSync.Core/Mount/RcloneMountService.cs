using System.Diagnostics;
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
public sealed class RcloneMountService : IMountStarter
{
    private static readonly TimeSpan MountStateTimeout = TimeSpan.FromSeconds(8);
    private readonly IRcloneLifecycleClient _rc;
    private readonly IProcessEnumerator _externalEnumerator;
    private readonly MountRecordStore? _records;
    private readonly Dictionary<string, ManagedMountMetadata> _managed = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="records">
    /// Optional memory of the mounts this application starts. Absent means mounts are not remembered at all,
    /// which is what the focused unit tests and the real-WinFsp integration test want.
    /// </param>
    public RcloneMountService(IRcloneLifecycleClient rc, IProcessEnumerator? externalEnumerator = null, MountRecordStore? records = null)
    {
        _rc = rc;
        _externalEnumerator = externalEnumerator ?? new WmiProcessEnumerator();
        _records = records;
    }

    /// <summary>Where the mounts this application starts are remembered, or <see langword="null"/> when not.</summary>
    public MountRecordStore? Records => _records;

    public async Task<IReadOnlyList<MountInfo>> ScanAsync(CancellationToken ct = default)
    {
        var rcMounts = await ListManagedMountPointsAsync(ct).ConfigureAwait(false);
        var drives = GetDriveIndex();
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
                metadata?.StartedUtc, MountOrigin.FengSyncManaged, IsMountPointHealthy(point, kind, drives)));
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
                process.StartedUtc, MountOrigin.External, IsMountPointHealthy(point, kind, drives)));
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

    public async Task MountAsync(MountTarget target, CancellationToken ct = default)
        => await MountAsync(new MountRequest(target), ct).ConfigureAwait(false);
    /// <summary>
    /// Mounts a remote with this mount's own options. The target is validated first, the endpoint is asked how
    /// big it is, and the record of the mount is written only after rclone confirms the mount point exists —
    /// a mount that failed must not come back on the next launch.
    /// </summary>
    public async Task<MountResult> MountAsync(MountRequest request, CancellationToken ct = default)
    {
        var target = request.Target;
        await ValidateAsync(target, ct).ConfigureAwait(false);
        var mountTypes = await _rc.CallAsync("mount/types", new { }, ct).ConfigureAwait(false);
        var mountType = SelectMountImplementation(mountTypes)
            ?? throw new InvalidOperationException("当前 rclone 运行时没有可用的 mount 实现。");
        var options = await WithCapacityAsync(request, ct).ConfigureAwait(false);
        // No _config here: the VFS cache root belongs to the daemon (see RcloneDaemon.BuildArguments), and a
        // per-call CacheDir override is silently ignored by rclone.
        var response = await _rc.CallAsync("mount/mount", new
        {
            fs = target.RemoteName + ":" + target.RootPath.Trim('/'),
            mountPoint = target.MountPoint,
            mountType,
            mountOpt = MountOptions.CreateMountOptions(options),
            vfsOpt = MountOptions.CreateVfsOptions(options)
        }, ct).ConfigureAwait(false);

        var actualPoint = response.TryGetProperty("mountPoint", out var pointValue)
            ? pointValue.GetString() ?? target.MountPoint
            : target.MountPoint;
        actualPoint = NormalizeMountPoint(actualPoint);
        _managed[actualPoint] = new(target.RemoteName, target.Provider, DateTimeOffset.UtcNow);
        if (!await WaitForManagedMountAsync(actualPoint, shouldExist: true, ct).ConfigureAwait(false))
        {
            _managed.Remove(actualPoint);
            try { await _rc.CallAsync("mount/unmount", new { mountPoint = actualPoint }, CancellationToken.None).ConfigureAwait(false); }
            catch { /* preserve the original structured failure */ }
            throw new InvalidOperationException($"rclone 已接受挂载请求，但挂载点“{actualPoint}”未在预期时间内就绪。");
        }
        await RecordAsync(request, actualPoint, ct).ConfigureAwait(false);
        // The reported mount point is not always the requested one (a UNC request comes back as the drive
        // letter Windows allocated), so the caller has to be told what actually exists rather than assume.
        return new MountResult(actualPoint, options?.DiskSpaceTotalSize);
    }

    /// <summary>
    /// Adds the endpoint's own reported size when the caller did not choose one, so the mounted volume shows
    /// real numbers instead of a fabricated one. A backend that cannot answer leaves the key out entirely —
    /// see <see cref="MountCapacity"/> for why writing 0 would be worse than writing nothing.
    /// </summary>
    private async Task<MountOptionsSettings?> WithCapacityAsync(MountRequest request, CancellationToken ct)
    {
        if (request.Options?.DiskSpaceTotalSize is not null) return request.Options;
        long? total;
        try
        {
            var fs = request.Target.RemoteName + ":" + request.Target.RootPath.Trim('/');
            total = MountCapacity.TotalBytes(await _rc.CallAsync("operations/about", new { fs }, ct).ConfigureAwait(false));
        }
        catch (Exception ex) { Trace.TraceInformation("Mount capacity unavailable: {0}", ex.Message); return request.Options; }
        if (total is null) return request.Options;
        return (request.Options ?? new MountOptionsSettings()) with { DiskSpaceTotalSize = total };
    }

    /// <summary>
    /// Remembers a successful mount. Recorded against the <b>actual</b> mount point rclone answered with, which
    /// is not always the requested one: a UNC request comes back as the drive letter Windows allocated.
    /// </summary>
    private async Task RecordAsync(MountRequest request, string actualPoint, CancellationToken ct)
    {
        if (_records is null) return;
        try
        {
            // RememberAsync keeps an existing record's AutoMount choice and creation time: mounting the same
            // point again — a reconnect, or a mount the list restored — must not quietly turn auto-mount off.
            await _records.RememberAsync(new MountRecord(
                request.Target.RemoteName, request.Target.Provider, actualPoint, request.Target.Kind,
                request.Target.RootPath, request.Options), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the memory of a mount is a convenience failure, not a mount failure.
            Trace.TraceWarning("Unable to record the mount: {0}", ex.Message);
        }
    }

    /// <param name="forget">
    /// Whether the unmount also deletes the record of the mount. True for the unmount the user asked for — that
    /// is a decision to forget — and false for the unmount a reconnect does on its way to mounting it again,
    /// which must leave the record (and its auto-mount choice) intact.
    /// </param>
    public async Task<MountStopResult> UnmountAsync(MountInfo info, bool forget = true, CancellationToken ct = default)
    {
        if (!info.CanUnmount)
            return new([new MountStopFailure(info.MountPoint, info.Pid ?? -1, "该挂载由外部程序管理；Feng Sync 仅显示它，不会结束或卸载它。")]);

        try
        {
            await _rc.CallAsync("mount/unmount", new { mountPoint = info.MountPoint }, ct).ConfigureAwait(false);
            if (!await WaitForManagedMountAsync(info.MountPoint, shouldExist: false, ct).ConfigureAwait(false))
                return new([new MountStopFailure(info.MountPoint, -1, "rclone 已接受取消请求，但挂载点仍然存在。")]);
            // The VFS cache is daemon-wide, so unmounting one mount must not clear it; a later mount of the
            // same remote would otherwise re-download everything.
            _managed.Remove(NormalizeMountPoint(info.MountPoint));
            // An unmount the user asked for is also a decision to forget the mount. The shutdown path below
            // deliberately does not do this: stopping everything while exiting is not the same as the user
            // saying they no longer want it back.
            if (forget && _records is not null)
            {
                try { await _records.RemoveAsync(info.MountPoint, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Trace.TraceWarning("Unable to forget the unmounted mount: {0}", ex.Message);
                }
            }
            return new([]);
        }
        catch (Exception ex)
        {
            return new([new MountStopFailure(info.MountPoint, -1, DescribeFailure(ex, "mount/unmount"))]);
        }
    }

    /// <summary>
    /// Stops every mount this process owns. Called while shutting down, so it deliberately <b>keeps</b> the
    /// remembered records: everything Feng Sync starts is unmounted on exit, and forgetting them here would
    /// mean a mount the user set up never comes back and never gets offered either.
    /// </summary>
    public async Task<MountStopResult> StopAllFengSyncMountsAsync(CancellationToken ct = default)
    {
        var failures = new List<MountStopFailure>();
        IReadOnlyList<RcMount> mounts;
        try { mounts = await ListManagedMountPointsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { return new([new MountStopFailure("(RC)", -1, DescribeFailure(ex, "mount/listmounts"))]); }

        foreach (var mount in mounts)
        {
            var point = NormalizeMountPoint(mount.MountPoint);
            try
            {
                await _rc.CallAsync("mount/unmount", new { mountPoint = point }, ct).ConfigureAwait(false);
                if (!await WaitForManagedMountAsync(point, shouldExist: false, ct).ConfigureAwait(false))
                {
                    failures.Add(new MountStopFailure(point, -1, "rclone 已接受取消请求，但挂载点仍然存在。"));
                    continue;
                }
                _managed.Remove(point);
            }
            catch (Exception ex)
            {
                failures.Add(new MountStopFailure(point, -1, DescribeFailure(ex, "mount/unmount")));
            }
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

    private static IReadOnlyDictionary<string, DriveInfo> GetDriveIndex()
    {
        try
        {
            var drives = new Dictionary<string, DriveInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var drive in DriveInfo.GetDrives())
                drives[NormalizeMountPoint(drive.Name)] = drive;
            return drives;
        }
        catch
        {
            return new Dictionary<string, DriveInfo>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool IsMountPointHealthy(
        string mountPoint,
        MountTargetKind kind,
        IReadOnlyDictionary<string, DriveInfo> drives)
    {
        try
        {
            if (kind == MountTargetKind.Directory) return Directory.Exists(mountPoint);
            return drives.TryGetValue(NormalizeMountPoint(mountPoint), out var drive) && drive.IsReady;
        }
        catch { return false; }
    }

    private static string DescribeFailure(Exception exception, string context)
    {
        if (exception is not RcloneException rclone) return exception.Message;
        RcloneFailureLog.Write(rclone.Failure, context);
        return $"{rclone.Failure.UserMessage} {rclone.Failure.SuggestedAction}（诊断 ID：{rclone.Failure.CorrelationId}）";
    }

    private sealed record ManagedMountMetadata(string RemoteName, string Provider, DateTimeOffset StartedUtc);
    private sealed record RcMount(string MountPoint, string Fs);
}
