using System.Text.Json;
using FengSync.Core;

namespace FengSync.Lan;

/// <summary>A device this application has paired with and is willing to talk to again.</summary>
/// <param name="DeviceId">The peer's own stable identifier — the key its token is checked against.</param>
/// <param name="SeenAddresses">Every address this device has answered from, newest last, kept because a
/// laptop that moved between two access points is still the same phone.</param>
/// <remarks>
/// There is deliberately no field that could hold a token. The secret lives in
/// <see cref="LanPeerSecretStore"/>, so this file can be read, copied into a bug report, or opened in an
/// editor without handing anybody the ability to post files as this device — the same split the endpoint
/// export follows, where names travel and values do not.
/// </remarks>
public sealed record LanPeerRecord(
    string DeviceId,
    string DeviceName,
    string Kind,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    int Port = LanOptions.DefaultPort,
    IReadOnlyList<string>? SeenAddresses = null)
{
    public IReadOnlyList<string> SeenAddresses { get; init; } = SeenAddresses ?? [];

    /// <summary>
    /// How many places one device is remembered at. A cap, not a workflow limit: a phone that has used more
    /// addresses than this has been everywhere, and the extra ones are all older than anything worth
    /// suggesting.
    /// </summary>
    public const int MaximumSeenAddresses = 8;

    /// <summary>Records that this device just answered from this address, keeping the list newest last.</summary>
    public LanPeerRecord SeenFrom(string address, int port, DateTimeOffset now)
    {
        var addresses = SeenAddresses.ToList();
        // Removed before being appended rather than only added when absent: a phone that walked between two
        // access points is the same device at a recycled address, and "newest last" is what makes the
        // manual-address fallback suggest the one it actually used recently.
        addresses.RemoveAll(existing => string.Equals(existing, address, StringComparison.OrdinalIgnoreCase));
        addresses.Add(address);
        if (addresses.Count > MaximumSeenAddresses) addresses.RemoveRange(0, addresses.Count - MaximumSeenAddresses);

        return this with { LastSeenUtc = now, Port = port, SeenAddresses = addresses };
    }
}

/// <summary>
/// Remembers which devices may talk to this listener again without a fresh code.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <c>MountRecordStore</c>: an instance gate, an atomic replace, and a corrupt file read as "no
/// device is trusted" rather than as an error. Failing to read the list must not stop the listener from
/// starting — the safe direction of that mistake is that the user re-pairs a phone, not that a damaged file
/// turns into a blanket refusal to serve anything.
/// </para>
/// <para>
/// The cap is a bound on a mistake, not on a workflow: fifteen paired devices is already more than a desk
/// has, and a file that grew past that is a file somebody else wrote.
/// </para>
/// </remarks>
public sealed class LanPeerStore(string? path = null, int maximumPeers = 15)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string Path { get; } = path ?? System.IO.Path.Combine(AppDataPaths.LanTransfer, "lan-peers.json");

    public async Task<IReadOnlyList<LanPeerRecord>> LoadAsync(CancellationToken ct = default) =>
        await LoadUnlockedAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Adds a device, or replaces the row for a device already known while keeping when it was first seen and
    /// the addresses it has used before.
    /// </summary>
    /// <remarks>
    /// A separate verb from a plain upsert because a reconnect must not silently rewrite history: a phone
    /// that paired last week still reads "上周配对", and the addresses it used before are what makes the
    /// manual-address fallback suggestible.
    /// </remarks>
    public async Task<LanPeerRecord> RememberAsync(LanPeerRecord record, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            var index = records.FindExisting(record.DeviceId);
            var merged = index < 0
                ? record
                : record with
                {
                    DeviceName = string.IsNullOrWhiteSpace(record.DeviceName) ? records[index].DeviceName : record.DeviceName,
                    FirstSeenUtc = records[index].FirstSeenUtc,
                    SeenAddresses = records[index].SeenAddresses,
                };

            if (index < 0)
            {
                // One record is dropped per add, never a batch: a comparison against Min() would remove every
                // device that happened to share the oldest timestamp, and two devices seen in the same second
                // is ordinary, not a tie to break.
                if (records.Count >= maximumPeers)
                {
                    var oldest = records.MinBy(older => older.LastSeenUtc);
                    if (oldest is not null) records.Remove(oldest);
                }
                records.Add(merged);
            }
            else records[index] = merged;

            await WriteUnlockedAsync(records, ct).ConfigureAwait(false);
            return merged;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Records that a known device just spoke, and from where.</summary>
    public async Task RememberContactAsync(string deviceId, string address, int port, DateTimeOffset now, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            var index = records.FindExisting(deviceId);
            if (index < 0) return;
            records[index] = records[index].SeenFrom(address, port, now);
            await WriteUnlockedAsync(records, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Renames a remembered device, which is the only way a stale 设备名 gets corrected.</summary>
    public async Task<bool> RenameAsync(string deviceId, string deviceName, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            var index = records.FindExisting(deviceId);
            if (index < 0) return false;
            records[index] = records[index] with { DeviceName = deviceName };
            await WriteUnlockedAsync(records, ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Un-trusts a device. The caller must delete its token too, which is why this returns nothing.</summary>
    public async Task<bool> ForgetAsync(string deviceId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            var index = records.FindExisting(deviceId);
            if (index < 0) return false;
            records.RemoveAt(index);
            await WriteUnlockedAsync(records, ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task<List<LanPeerRecord>> LoadUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(Path)) return [];
        try
        {
            await using var stream = File.OpenRead(Path);
            if (stream.Length == 0) return [];
            var records = await JsonSerializer.DeserializeAsync<List<LanPeerRecord>>(stream, cancellationToken: ct).ConfigureAwait(false);
            return records ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return []; }
    }

    private async Task WriteUnlockedAsync(List<LanPeerRecord> records, CancellationToken ct)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = Path + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, records, cancellationToken: ct).ConfigureAwait(false);
        File.Move(temporary, Path, true);
    }
}

file static class LanPeerList
{
    /// <summary>Device identifiers are generated hex GUIDs, so case is not a distinguishing feature.</summary>
    public static int FindExisting(this List<LanPeerRecord> records, string deviceId) =>
        records.FindIndex(record => string.Equals(record.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
}
