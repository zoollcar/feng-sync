using System.Text.Json;

namespace FengSync.Core.Mount;

/// <summary>
/// Persists mount sessions Feng Sync itself created. Designed so an abnormal shutdown never hides an
/// externally-owned rclone mount — we only ever kill processes whose PID is recorded here. Recovery
/// (promoting Active records to Orphaned when the previous run died unexpectedly) is performed by the
/// caller, not by <see cref="LoadAsync"/>; this lets tests round-trip without surprise mutations.
/// </summary>
public sealed class MountSessionStore
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _path;

    public MountSessionStore(string? path = null) =>
        _path = path ?? Path.Combine(AppDataPaths.Root, "mount", "sessions.json");

    public string FilePath => _path;

    /// <summary>Load every persisted session, preserving the persisted status values verbatim.</summary>
    public async Task<IReadOnlyList<MountSessionRecord>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var json = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var result = new List<MountSessionRecord>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var record = ReadRecord(element);
                if (record is not null) result.Add(record);
            }
            return result;
        }
        catch (JsonException) { return []; }
    }

    /// <summary>Write the session list atomically: tmp file + File.Move over the destination.</summary>
    public async Task SaveAsync(IReadOnlyList<MountSessionRecord> records, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, records
                .Select(x => new Persisted(x.Id, x.RemoteName, x.Provider, x.MountPoint, (int)x.Kind, x.Pid, x.StartedUtc, (int)x.Status))
                .ToList(), cancellationToken: ct).ConfigureAwait(false);
        }
        File.Move(temporary, _path, overwrite: true);
    }

    /// <summary>
    /// Promote any <see cref="MountSessionStatus.Active"/> record whose PID is not in
    /// <paramref name="alivePids"/> to <see cref="MountSessionStatus.Orphaned"/>. Called by
    /// <see cref="RcloneMountService"/> after each process scan to detect abnormal shutdowns.
    /// </summary>
    public async Task<int> PromoteActiveToOrphanedAsync(IReadOnlySet<int> alivePids, CancellationToken ct = default)
    {
        var records = (await LoadAsync(ct).ConfigureAwait(false)).ToList();
        var changed = 0;
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (r.Status == MountSessionStatus.Active && !alivePids.Contains(r.Pid))
            {
                records[i] = r with { Status = MountSessionStatus.Orphaned };
                changed++;
            }
        }
        if (changed > 0) await SaveAsync(records, ct).ConfigureAwait(false);
        return changed;
    }

    /// <summary>Remove the record with the given PID. Used after a confirmed stop.</summary>
    public async Task RemoveByPidAsync(int pid, CancellationToken ct = default)
    {
        var records = (await LoadAsync(ct).ConfigureAwait(false)).ToList();
        var before = records.Count;
        records.RemoveAll(x => x.Pid == pid);
        if (records.Count != before) await SaveAsync(records, ct).ConfigureAwait(false);
    }

    /// <summary>Remove the record by its session id.</summary>
    public async Task RemoveByIdAsync(Guid id, CancellationToken ct = default)
    {
        var records = (await LoadAsync(ct).ConfigureAwait(false)).ToList();
        var before = records.Count;
        records.RemoveAll(x => x.Id == id);
        if (records.Count != before) await SaveAsync(records, ct).ConfigureAwait(false);
    }

    private static MountSessionRecord? ReadRecord(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        Guid id; string remote, provider, mountPoint; int kindInt, pid, statusInt; DateTimeOffset started;
        try
        {
            id = element.TryGetProperty("Id", out var idProp) && idProp.TryGetGuid(out var g) ? g : Guid.Empty;
            remote = element.GetProperty("RemoteName").GetString() ?? "";
            provider = element.GetProperty("Provider").GetString() ?? "sftp";
            mountPoint = element.GetProperty("MountPoint").GetString() ?? "";
            kindInt = element.TryGetProperty("Kind", out var k) ? k.GetInt32() : 0;
            pid = element.TryGetProperty("Pid", out var p) ? p.GetInt32() : 0;
            started = element.TryGetProperty("StartedUtc", out var s) && s.TryGetDateTimeOffset(out var so) ? so : DateTimeOffset.MinValue;
            statusInt = element.TryGetProperty("Status", out var st) ? st.GetInt32() : 0;
        }
        catch (Exception) { return null; }
        if (string.IsNullOrEmpty(remote) || string.IsNullOrEmpty(mountPoint) || pid <= 0) return null;
        return new MountSessionRecord(id, remote, provider, mountPoint, (MountTargetKind)kindInt, pid, started, (MountSessionStatus)statusInt);
    }

    private sealed record Persisted(Guid Id, string RemoteName, string Provider, string MountPoint, int Kind, int Pid, DateTimeOffset StartedUtc, int Status);
}