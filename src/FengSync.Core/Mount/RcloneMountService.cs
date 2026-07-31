using System.Diagnostics;
using System.Runtime.Versioning;

namespace FengSync.Core.Mount;

/// <summary>Outcome of an unmount attempt; the UI uses <see cref="Failures"/> to surface diagnostics.</summary>
public sealed record MountStopResult(IReadOnlyList<MountStopFailure> Failures)
{
    public bool AllStopped => Failures.Count == 0;
}

public sealed record MountStopFailure(string MountPoint, int Pid, string Reason);

/// <summary>
/// The single entry point for mount lifecycle operations. Reads the system process table via the injected
/// <see cref="IProcessEnumerator"/> and tracks Feng-Sync-created mounts through <see cref="MountSessionStore"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RcloneMountService
{
    /// <summary>Maximum time we wait for an rclone mount process to exit during shutdown.</summary>
    private static readonly TimeSpan StopProcessTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Maximum time we wait for a mount point to disappear after the process is gone.</summary>
    private static readonly TimeSpan MountPointGoneTimeout = TimeSpan.FromSeconds(5);

    private readonly IProcessEnumerator _enumerator;
    private readonly MountSessionStore _store;
    private readonly string _rcloneExecutable;
    private readonly string _configPath;

    public RcloneMountService(
        IProcessEnumerator? enumerator = null,
        MountSessionStore? store = null,
        string? rcloneExecutable = null,
        string? configPath = null)
    {
        _enumerator = enumerator ?? new WmiProcessEnumerator();
        _store = store ?? new MountSessionStore();
        _rcloneExecutable = rcloneExecutable ?? BundledRclone.ExecutablePath;
        _configPath = configPath ?? BundledRclone.ConfigPath;
    }

    /// <summary>
    /// Cross-reference live rclone processes with the persisted session store. Returns a list the UI can
    /// bind to directly; flags <see cref="MountOrigin.FengSyncManaged"/> when the PID matches a record.
    /// </summary>
    public async Task<IReadOnlyList<MountInfo>> ScanAsync(CancellationToken ct = default)
    {
        var sessions = await _store.LoadAsync(ct).ConfigureAwait(false);
        var procs = _enumerator.EnumerateRcloneProcesses();
        // Recover any sessions whose process disappeared between runs — only those should be reported as
        // Orphaned in the current view. We persist the promotion so a future stop on the same PID is safe.
        var alivePids = new HashSet<int>(procs.Where(p => p.Pid > 0).Select(p => p.Pid));
        await _store.PromoteActiveToOrphanedAsync(alivePids, ct).ConfigureAwait(false);
        sessions = await _store.LoadAsync(ct).ConfigureAwait(false);
        var fengSyncPids = sessions
            .Where(x => x.Status is MountSessionStatus.Active or MountSessionStatus.Orphaned)
            .ToDictionary(x => x.Pid, x => x, Int32Comparer.Instance);
        var result = new List<MountInfo>();
        foreach (var proc in procs)
        {
            if (proc.Pid < 0) continue; // enumerator-side error sentinel
            if (!proc.CommandLineReadable)
            {
                result.Add(new MountInfo(proc.Pid, "(unreadable)", "?", "", MountTargetKind.Directory, proc.StartedUtc, MountOrigin.Unreadable, false));
                continue;
            }
            if (!RcloneCommandLineParser.TryParse(proc.CommandLine, out var parsed)) continue;
            var remoteName = parsed.RemoteSpec[..parsed.RemoteSpec.IndexOf(':')];
            var provider = GuessProvider(parsed.RemoteSpec);
            var kind = parsed.MountPoint.Length == 2 && parsed.MountPoint[1] == ':' ? MountTargetKind.DriveLetter : MountTargetKind.Directory;
            var origin = fengSyncPids.ContainsKey(proc.Pid) ? MountOrigin.FengSyncManaged : MountOrigin.External;
            var healthy = IsMountPointHealthy(parsed.MountPoint, kind);
            var mountPoint = NormalizeMountPoint(parsed.MountPoint);
            result.Add(new MountInfo(proc.Pid, remoteName, provider, mountPoint, kind, proc.StartedUtc, origin, healthy));
        }
        // Add persisted sessions whose process disappeared so the UI can offer cleanup.
        foreach (var session in sessions.Where(s => s.Status == MountSessionStatus.Orphaned && !alivePids.Contains(s.Pid)))
        {
            var healthy = IsMountPointHealthy(session.MountPoint, session.Kind);
            result.Add(new MountInfo(session.Pid, session.RemoteName, session.Provider, session.MountPoint, session.Kind, session.StartedUtc, MountOrigin.FengSyncManaged, healthy));
        }
        return result
            .OrderBy(x => x.Origin == MountOrigin.FengSyncManaged ? 0 : 1)
            .ThenBy(x => x.RemoteName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Throw a helpful message if the requested target collides with an existing mount.</summary>
    public async Task<MountTarget> ValidateAsync(MountTarget target, CancellationToken ct = default)
    {
        var wfs = WinFspDetector.Detect();
        if (!wfs.Installed) throw new InvalidOperationException(wfs.Summary);
        var existing = await ScanAsync(ct).ConfigureAwait(false);
        var validation = MountPointInspector.Validate(target.MountPoint, target.Kind, existing.Select(x => x.MountPoint).ToList());
        if (!validation.IsValid) throw new InvalidOperationException(validation.Error);
        return target;
    }

    /// <summary>Start a new rclone mount and persist the session record. Returns the created session id.</summary>
    public async Task<Guid> MountAsync(MountTarget target, CancellationToken ct = default)
    {
        await ValidateAsync(target, ct).ConfigureAwait(false);
        var sessionId = Guid.NewGuid();
        var cacheDir = MountOptions.CacheDirectoryFor(sessionId);
        Directory.CreateDirectory(cacheDir);
        var args = new List<string>();
        MountOptions.AppendMountArguments(args, target.RemoteName + ":", target.MountPoint, cacheDir, _configPath);
        // CRITICAL: never redirect stdout/stderr without draining them, or rclone's long-running mount
        // process will block on a full pipe buffer (~4KB on Windows) before it can set up the mount.
        // We redirect stderr (for startup error capture) and drain it continuously in the background.
        var stderrCapture = new MountStderrCapture();
        var startInfo = new ProcessStartInfo(_rcloneExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        Process? process = null;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 rclone mount。");
        }
        catch (Exception ex)
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort */ }
            throw new InvalidOperationException("无法启动 rclone mount：" + ex.Message);
        }
        // Start draining stderr to keep the pipe from filling up.
        stderrCapture.StartDraining(process);
        var healthy = await WaitForHealthyAsync(target.MountPoint, target.Kind, process, ct).ConfigureAwait(false);
        if (!healthy)
        {
            var stderr = stderrCapture.Snapshot();
            TryKill(process);
            stderrCapture.Dispose();
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best-effort */ }
            throw new InvalidOperationException("rclone mount 未能建立挂载点。" + stderr);
        }
        var records = (await _store.LoadAsync(ct).ConfigureAwait(false)).ToList();
        var record = new MountSessionRecord(sessionId, target.RemoteName, target.Provider, target.MountPoint, target.Kind, process.Id, DateTimeOffset.UtcNow, MountSessionStatus.Active);
        records.Add(record);
        await _store.SaveAsync(records, ct).ConfigureAwait(false);
        return sessionId;
    }

    /// <summary>Stop one mount by killing its rclone process and waiting for the mount point to disappear.</summary>
    public async Task<MountStopResult> UnmountAsync(MountInfo info, CancellationToken ct = default)
    {
        if (info.Pid is not int pidValue || pidValue <= 0)
            return new([new MountStopFailure(info.MountPoint, info.Pid ?? -1, "无法识别进程 PID。")]);
        try
        {
            var proc = Process.GetProcessById(pidValue);
            var killError = TryKill(proc);
            if (killError is not null) return new([new MountStopFailure(info.MountPoint, pidValue, killError)]);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (Exception ex)
        {
            return new([new MountStopFailure(info.MountPoint, pidValue, ex.Message)]);
        }
        var gone = await WaitForMountPointGoneAsync(info.MountPoint, info.Kind, ct).ConfigureAwait(false);
        if (!gone) return new([new MountStopFailure(info.MountPoint, pidValue, "进程已退出但挂载点未消失。")]);
        await _store.RemoveByPidAsync(pidValue, ct).ConfigureAwait(false);
        return new([]);
    }

    /// <summary>Iterate Feng Sync's session store and stop everything we created. Used at app shutdown.</summary>
    public async Task<MountStopResult> StopAllFengSyncMountsAsync(CancellationToken ct = default)
    {
        var sessions = await _store.LoadAsync(ct).ConfigureAwait(false);
        // Only Active records represent something we started in the current process tree. Orphaned ones
        // belong to previous runs and are out of scope for clean shutdown; the user can still cancel
        // them from the UI.
        var failures = new List<MountStopFailure>();
        foreach (var session in sessions.Where(s => s.Status == MountSessionStatus.Active).ToList())
        {
            try
            {
                Process? proc = null;
                var procMissing = false;
                try { proc = Process.GetProcessById(session.Pid); }
                catch (ArgumentException) { procMissing = true; }
                if (!procMissing && proc is not null)
                {
                    var killError = TryKill(proc);
                    if (killError is not null)
                    {
                        failures.Add(new MountStopFailure(session.MountPoint, session.Pid, killError));
                    }
                    else
                    {
                        // Bounded wait so a stuck child can never freeze the whole app shutdown.
                        try { await WaitForExitBoundedAsync(proc, StopProcessTimeout).ConfigureAwait(false); }
                        catch { /* swallow — already reported via the timeout below */ }
                        if (!proc.HasExited)
                            failures.Add(new MountStopFailure(session.MountPoint, session.Pid, $"进程在 {StopProcessTimeout.TotalSeconds:0} 秒内未退出。"));
                        var gone = await WaitForMountPointGoneAsync(session.MountPoint, session.Kind, ct).ConfigureAwait(false);
                        if (!gone && proc.HasExited)
                            failures.Add(new MountStopFailure(session.MountPoint, session.Pid, "进程已退出但挂载点未消失。"));
                    }
                }
                await _store.RemoveByIdAsync(session.Id, ct).ConfigureAwait(false);
                TryDeleteCache(MountOptions.CacheDirectoryFor(session.Id));
            }
            catch (Exception ex)
            {
                failures.Add(new MountStopFailure(session.MountPoint, session.Pid, ex.Message));
            }
        }
        return new(failures);
    }

    /// <summary>
    /// Kill the process tree, returning null on success or a short reason string on failure so the caller
    /// can surface it instead of silently swallowing.
    /// </summary>
    private static string? TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return null;
        }
        catch (Exception ex) { return $"无法结束进程：{ex.Message}"; }
    }

    private static void TryDeleteCache(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
    }

    private static async Task WaitForExitBoundedAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* caller checks HasExited */ }
    }

    private static async Task<bool> WaitForHealthyAsync(string mountPoint, MountTargetKind kind, Process process, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) return false;
            if (IsMountPointHealthy(mountPoint, kind)) return true;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<bool> WaitForMountPointGoneAsync(string mountPoint, MountTargetKind kind, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + MountPointGoneTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsMountPointHealthy(mountPoint, kind)) return true;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static bool IsMountPointHealthy(string mountPoint, MountTargetKind kind)
    {
        try
        {
            if (kind == MountTargetKind.DriveLetter)
            {
                var letter = mountPoint.TrimEnd('\\', '/');
                if (letter.Length != 2 || letter[1] != ':') return false;
                return DriveInfo.GetDrives().Any(d => d.Name.TrimEnd('\\').Equals(letter, StringComparison.OrdinalIgnoreCase) && d.IsReady);
            }
            return Directory.Exists(mountPoint);
        }
        catch { return false; }
    }

    private static string NormalizeMountPoint(string mountPoint) => mountPoint.TrimEnd('\\', '/');

    private static string GuessProvider(string remoteSpec)
    {
        // Without parsing the rclone config we can't be certain; fall back to sftp which is the most common.
        // The UI displays this as a hint; clicking the originating endpoint refreshes it.
        return "sftp";
    }

    private sealed class Int32Comparer : IEqualityComparer<int>
    {
        public static readonly Int32Comparer Instance = new();
        public bool Equals(int x, int y) => x == y;
        public int GetHashCode(int obj) => obj;
    }
}

/// <summary>
/// Continuously drains an rclone mount process's stderr in the background so the pipe buffer can never
/// fill and deadlock the child. Captures the tail of the stream for diagnostics if the mount fails.
/// </summary>
public sealed class MountStderrCapture : IDisposable
{
    private readonly System.IO.StringWriter _tail = new();
    private System.IO.StreamReader? _reader;
    private CancellationTokenSource? _cts;
    private Task? _pump;

    public void StartDraining(Process process)
    {
        _reader = process.StandardError;
        _cts = new CancellationTokenSource();
        _pump = Task.Run(async () =>
        {
            var buf = new char[512];
            try
            {
                while (!_cts!.IsCancellationRequested)
                {
                    var read = await _reader.ReadAsync(buf.AsMemory()).ConfigureAwait(false);
                    if (read <= 0) break;
                    lock (_tail) { _tail.Write(buf, 0, read); }
                }
            }
            catch { /* pipe closed when the process exits — expected */ }
        });
    }

    public string Snapshot()
    {
        lock (_tail)
        {
            var text = _tail.ToString();
            // Trim to the last 2 KB so the surfaced error stays readable.
            return text.Length <= 2048 ? text : "…" + text[^(2048 - 1)..];
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _reader?.Dispose(); } catch { }
    }
}