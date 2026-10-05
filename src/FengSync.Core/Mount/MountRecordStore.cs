using System.Text.Json;

namespace FengSync.Core.Mount;

/// <summary>
/// One remembered mount. Deliberately holds no credential: the remote is named and rclone resolves the
/// credentials from <c>rclone.conf</c> at mount time, which is the same rule the endpoint export follows.
/// </summary>
public sealed record MountRecord(
    string RemoteName,
    string Provider,
    string MountPoint,
    MountTargetKind Kind,
    string RootPath = "",
    MountOptionsSettings? Options = null,
    bool AutoMount = false,
    DateTimeOffset? CreatedUtc = null);

/// <summary>
/// Remembers the mounts Feng Sync started, so the next launch can offer to put them back.
/// </summary>
/// <remarks>
/// <para>
/// It survives a normal exit on purpose. Feng Sync unmounts everything it owns while shutting down — that is
/// what <c>StopAllFengSyncMountsAsync</c> is for — so "there are records and no live mounts" is the normal
/// state at startup, not a leftover. Only an explicit user unmount deletes a record.
/// </para>
/// <para>
/// Atomic replace and a serialising gate, mirroring <c>CloudEndpointMetadataStore</c>. A corrupt file is
/// treated as "nothing remembered" rather than as an error, because losing the memory of a mount must never
/// block the mount list from opening.
/// </para>
/// </remarks>
public sealed class MountRecordStore(string? path = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Path { get; } = path ?? System.IO.Path.Combine(AppDataPaths.Root, "mounts.json");

    public async Task<IReadOnlyList<MountRecord>> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(Path)) return [];
            try
            {
                await using var stream = File.OpenRead(Path);
                if (stream.Length == 0) return [];
                return await JsonSerializer.DeserializeAsync<List<MountRecord>>(stream, cancellationToken: ct).ConfigureAwait(false) ?? [];
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return []; }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Adds or replaces the record for a mount point, keyed case-insensitively.</summary>
    public async Task UpsertAsync(MountRecord record, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = (await ReadUnlockedAsync(ct).ConfigureAwait(false)).Where(x => !Same(x.MountPoint, record.MountPoint)).ToList();
            records.Add(record with { CreatedUtc = record.CreatedUtc ?? DateTimeOffset.UtcNow });
            await WriteUnlockedAsync(records, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Forgets a mount point. Used for an unmount the user asked for, never for a shutdown.</summary>
    public async Task<bool> RemoveAsync(string mountPoint, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await ReadUnlockedAsync(ct).ConfigureAwait(false);
            var kept = records.Where(x => !Same(x.MountPoint, mountPoint)).ToList();
            if (kept.Count == records.Count) return false;
            await WriteUnlockedAsync(kept, ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Turns automatic restore on or off for one remembered mount.</summary>
    public async Task<bool> SetAutoMountAsync(string mountPoint, bool autoMount, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await ReadUnlockedAsync(ct).ConfigureAwait(false);
            var index = records.FindIndex(x => Same(x.MountPoint, mountPoint));
            if (index < 0) return false;
            records[index] = records[index] with { AutoMount = autoMount };
            await WriteUnlockedAsync(records, ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Records a mount that was just started, keeping the choices already stored for that mount point.
    /// </summary>
    /// <remarks>
    /// A separate verb from <see cref="UpsertAsync"/> because mounting the same point again — a reconnect, or a
    /// restore — must not quietly turn auto-mount off or reset when the mount was first created. Only an
    /// explicit <see cref="SetAutoMountAsync"/> and an explicit unmount change those, so the merge lives here
    /// rather than in every caller.
    /// </remarks>
    public async Task RememberAsync(MountRecord record, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await ReadUnlockedAsync(ct).ConfigureAwait(false);
            var index = records.FindIndex(x => Same(x.MountPoint, record.MountPoint));
            var merged = record with { CreatedUtc = record.CreatedUtc ?? DateTimeOffset.UtcNow };
            if (index < 0) records.Add(merged);
            else records[index] = merged with
            {
                AutoMount = records[index].AutoMount,
                CreatedUtc = records[index].CreatedUtc ?? merged.CreatedUtc
            };
            await WriteUnlockedAsync(records, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<MountRecord>> ReadUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(Path)) return [];
        try
        {
            await using var stream = File.OpenRead(Path);
            if (stream.Length == 0) return [];
            return await JsonSerializer.DeserializeAsync<List<MountRecord>>(stream, cancellationToken: ct).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return []; }
    }

    private async Task WriteUnlockedAsync(List<MountRecord> records, CancellationToken ct)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = Path + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, records, cancellationToken: ct).ConfigureAwait(false);
        File.Move(temporary, Path, true);
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What to do with one remembered mount at startup.</summary>
public sealed record MountRestorePlanItem(MountRecord Record, bool Automatic, string? SkipReason)
{
    /// <summary>Whether this record can be put back at all.</summary>
    public bool CanRestore => SkipReason is null;
}

/// <summary>
/// The startup policy for remembered mounts: put back the ones the user marked, offer the rest, and never
/// restore anything silently that cannot work.
/// </summary>
/// <remarks>
/// Auto-mounting everything is the wrong default: it slows startup, WinFsp may not be installed, and a
/// network may be down — and a mount that fails during startup is exactly the kind of failure nobody can
/// diagnose. So the default is to <b>say</b> "N mounts from last time are not back" and offer one button.
/// </remarks>
public static class MountRecovery
{
    /// <summary>
    /// Classifies every remembered mount. A record whose mount point is already live is skipped — the same
    /// drive letter cannot be mounted twice, and reporting it as "not restored" would be untrue.
    /// </summary>
    public static IReadOnlyList<MountRestorePlanItem> Plan(
        IReadOnlyList<MountRecord> records,
        IReadOnlyCollection<string> liveMountPoints,
        bool winFspInstalled)
    {
        var live = new HashSet<string>(liveMountPoints.Select(Trim), StringComparer.OrdinalIgnoreCase);
        return records.Select(record =>
        {
            var reason = !winFspInstalled
                ? "未检测到 WinFsp，无法挂载。"
                : live.Contains(Trim(record.MountPoint)) ? "已经处于挂载状态。" : null;
            return new MountRestorePlanItem(record, record.AutoMount && reason is null, reason);
        }).ToList();
    }

    /// <summary>The line the mount list shows when there is something to put back.</summary>
    public static string DescribePending(int count) => count switch
    {
        <= 0 => "",
        1 => "上次有 1 个挂载未恢复，可一键恢复。",
        _ => $"上次有 {count} 个挂载未恢复，可一键恢复。"
    };

    /// <summary>The line shown after a restore attempt. Partial success is reported as partial, never as success.</summary>
    public static string DescribeOutcome(int restored, int failed) => (restored, failed) switch
    {
        (0, 0) => "没有需要恢复的挂载。",
        (_, 0) => $"已恢复 {restored} 个挂载。",
        (0, _) => $"恢复失败：{failed} 个挂载未能挂载。",
        _ => $"已恢复 {restored} 个挂载，{failed} 个失败。"
    };

    private static string Trim(string mountPoint) => mountPoint.TrimEnd('\\', '/');
}

/// <summary>How a restore attempt went. Partial success is reported as partial, never as success.</summary>
/// <param name="Restored">How many mounts came back.</param>
/// <param name="Failures">One line per mount that did not, already safe to show.</param>
public sealed record MountRestoreOutcome(int Restored, IReadOnlyList<string> Failures)
{
    public int Failed => Failures.Count;
    public string Describe() => MountRecovery.DescribeOutcome(Restored, Failed);
}

/// <summary>
/// Puts remembered mounts back. One implementation for the three callers — the transfer-free startup hook in
/// each shell and the mount list's 一键恢复 button — because a restore that behaves differently depending on
/// who asked for it is exactly the kind of divergence the rest of this application avoids.
/// </summary>
public static class MountRestore
{
    /// <summary>
    /// Mounts the planned records one at a time and reports what happened.
    /// </summary>
    /// <param name="automaticOnly">
    /// True for the startup hook: only the records the user explicitly marked are mounted, and the rest are
    /// left for the mount list to offer. Mounting everything at startup is what this design refuses to do.
    /// </param>
    /// <remarks>
    /// Sequential and failure-tolerant on purpose. A failure is collected rather than thrown, because one
    /// unreachable endpoint must not stop the others from coming back, and the caller needs the count to say
    /// "3 of 5" instead of claiming success.
    /// </remarks>
    public static async Task<MountRestoreOutcome> RunAsync(
        IMountStarter service,
        IReadOnlyList<MountRestorePlanItem> plan,
        bool automaticOnly = false,
        CancellationToken ct = default)
    {
        var restored = 0;
        var failures = new List<string>();
        foreach (var item in plan)
        {
            if (!item.CanRestore) continue;
            if (automaticOnly && !item.Automatic) continue;
            var record = item.Record;
            var target = new MountTarget(record.RemoteName, record.Provider, record.MountPoint, record.Kind, record.RootPath);
            try
            {
                await service.MountAsync(new MountRequest(target, record.Options), ct).ConfigureAwait(false);
                restored++;
            }
            catch (Exception ex)
            {
                failures.Add($"{record.RemoteName} → {record.MountPoint}：{ex.Message}");
            }
        }
        return new(restored, failures);
    }
}
