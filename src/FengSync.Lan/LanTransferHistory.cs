using System.Text.Json;
using FengSync.Core;

namespace FengSync.Lan;

/// <summary>Which way the bytes went, from this machine's point of view.</summary>
public enum LanTransferDirection
{
    /// <summary>A peer sent bytes to this machine.</summary>
    Received,

    /// <summary>This machine handed bytes to a peer.</summary>
    Sent
}

/// <summary>How a transfer ended. Deliberately four states, of which only two are this application's fault.</summary>
public enum LanTransferState
{
    Completed,
    Failed,

    /// <summary>Cut off by the user or by the window closing.</summary>
    Cancelled,

    /// <summary>Refused at the confirm gate — a stranger's first item the user would not accept.</summary>
    Declined
}

/// <summary>
/// One archived transfer.
/// </summary>
/// <param name="Path">
/// What moved: the path it landed on for a receive, the name it went out under for a send. It is a description
/// rather than a resolvable location: a received file the user later moved is still the transfer that happened.
/// </param>
/// <param name="ArchivedUtc">When it was filed, which is the ordering key and not when it started.</param>
public sealed record LanTransferHistoryEntry(
    Guid Id,
    LanTransferDirection Direction,
    string DeviceId,
    string DeviceName,
    string Path,
    long Bytes,
    LanTransferState State,
    DateTimeOffset ArchivedUtc,
    string? Error = null);

/// <summary>
/// Append-only archive of LAN transfers that have left the progress list.
/// </summary>
/// <remarks>
/// <para>
/// The list on screen is live state and it empties itself; this file is the answer to "which phone sent me
/// what, and did it finish". It is a history to read, not state to resume from, so it is one bounded JSON
/// file rather than one file per transfer.
/// </para>
/// <para>
/// Modelled on the cloud transfer archive: a per-path write lock, newest-first ordering, a cap, and a corrupt,
/// empty or absent file read as an empty history so the window still opens. An unreadable history is not a
/// reason to refuse a transfer.
/// </para>
/// </remarks>
public sealed class LanTransferHistoryRepository(string? path = null, int maximumEntries = 300)
{
    private static readonly Dictionary<string, SemaphoreSlim> WriteLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path = path ?? System.IO.Path.Combine(AppDataPaths.LanTransfer, "lan-transfer-history.json");

    public string Path => _path;

    public async Task AppendAsync(IEnumerable<LanTransferHistoryEntry> entries, CancellationToken ct = default)
    {
        var batch = entries.ToList();
        if (batch.Count == 0) return;
        var writeLock = LockFor(_path);
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = (await ReadAsync(ct).ConfigureAwait(false)).ToList();
            all.AddRange(batch);
            all = [.. all.OrderByDescending(entry => entry.ArchivedUtc).Take(Math.Max(1, maximumEntries))];
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(all), ct).ConfigureAwait(false);
            File.Move(temporary, _path, true);
        }
        finally { writeLock.Release(); }
    }

    public async Task<IReadOnlyList<LanTransferHistoryEntry>> QueryAsync(CancellationToken ct = default) =>
        [.. (await ReadAsync(ct).ConfigureAwait(false)).OrderByDescending(entry => entry.ArchivedUtc)];

    /// <summary>Empties the archive. Only ever called because the user asked for it.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        var writeLock = LockFor(_path);
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { writeLock.Release(); }
    }

    private async Task<IReadOnlyList<LanTransferHistoryEntry>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            await using var stream = File.OpenRead(_path);
            // A crash between creating the file and writing it leaves zero bytes, which has to read as "no
            // history" rather than as an exception at the window that only wanted to show a list.
            if (stream.Length == 0) return [];
            return await JsonSerializer
                .DeserializeAsync<List<LanTransferHistoryEntry>>(stream, cancellationToken: ct)
                .ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return []; }
    }

    private static SemaphoreSlim LockFor(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        lock (WriteLocks)
        {
            if (!WriteLocks.TryGetValue(full, out var lockValue)) WriteLocks[full] = lockValue = new SemaphoreSlim(1, 1);
            return lockValue;
        }
    }
}
