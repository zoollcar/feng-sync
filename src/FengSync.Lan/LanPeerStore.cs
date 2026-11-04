using System.Text.Json;
using FengSync.Core;

namespace FengSync.Lan;

/// <summary>A device this application has paired with and is willing to talk to again.</summary>
/// <param name="DeviceId">The peer's own stable identifier — the key its token is checked against.</param>
/// <param name="SeenAddresses">Every address this device has answered from, newest last, kept because a
/// laptop that moved between two access points is still the same phone.</param>
/// <param name="GatewayDeviceId">
/// For a device that has no listener of its own, the member it talks through. A phone browser is reachable only
/// through the computer that serves its page, so a member that learned of it from that computer names it here —
/// which is what lets the row say 在 客厅电脑 上 rather than implying an address nothing could be dialled at.
/// Null for a device that answers for itself.
/// </param>
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
    IReadOnlyList<string>? SeenAddresses = null,
    string? GatewayDeviceId = null)
{
    public IReadOnlyList<string> SeenAddresses { get; init; } = SeenAddresses ?? [];

    /// <summary>
    /// How many places one device is remembered at. A cap, not a workflow limit: a phone that has used more
    /// addresses than this has been everywhere, and the extra ones are all older than anything worth
    /// suggesting.
    /// </summary>
    public const int MaximumSeenAddresses = 8;

    /// <summary>Records that this device just answered from this address, keeping the list newest last.</summary>
    /// <remarks>
    /// A port of 0 is not a number to keep. It is what a device that listens on nothing reports — a phone browser
    /// says nothing at all, and a peer built before the port header existed says nothing either — and writing it
    /// over a port this machine already learned would turn a dialable member into one nothing can reach. The
    /// address is still worth recording: where a device spoke from is true even when what it listens on is unknown.
    /// </remarks>
    public LanPeerRecord SeenFrom(string address, int port, DateTimeOffset now)
    {
        var addresses = SeenAddresses.ToList();
        // Removed before being appended rather than only added when absent: a phone that walked between two
        // access points is the same device at a recycled address, and "newest last" is what makes the
        // manual-address fallback suggest the one it actually used recently.
        addresses.RemoveAll(existing => string.Equals(existing, address, StringComparison.OrdinalIgnoreCase));
        addresses.Add(address);
        if (addresses.Count > MaximumSeenAddresses) addresses.RemoveRange(0, addresses.Count - MaximumSeenAddresses);

        return this with
        {
            LastSeenUtc = now,
            Port = port is >= LanOptions.MinimumPort and <= LanOptions.MaximumPort ? port : Port,
            SeenAddresses = addresses,
        };
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
/// <para>
/// Dropping a row is not by itself a revocation. The token lives in <see cref="LanPeerSecretStore"/>, so an
/// evicted device keeps a credential that still verifies — which is why <c>revokeEvicted</c> exists and why the
/// listener also refuses a credential whose row is gone.
/// </para>
/// </remarks>
/// <param name="path">Where the roster is kept. Null means the application's own LAN folder.</param>
/// <param name="maximumPeers">How many devices are remembered before the least recently seen one is dropped.</param>
/// <param name="revokeEvicted">
/// Called with the device id of a row this store just dropped, so the caller can delete the credential that
/// outlived it. A row is only dropped when the list is full, and the token for it lives in a different file,
/// so without this the roster silently stopped being the list of devices that may write.
/// </param>
public sealed class LanPeerStore(
    string? path = null,
    int maximumPeers = 15,
    Func<string, CancellationToken, Task>? revokeEvicted = null)
{
    /// <summary>
    /// How long a sighting may stay unwritten while it changes nothing the roster would show.
    /// </summary>
    /// <remarks>
    /// Every authenticated request passes through <see cref="RememberContactAsync"/>: a phone polls the room
    /// every three seconds, and the room itself ticks on the same beat. Persisting each sighting turned that
    /// into a read, a serialise and a rename on every poll, with every concurrent request queued behind the
    /// same gate while the disk caught up. A last-seen time that is thirty seconds stale is indistinguishable
    /// to the person reading the list; an address or port that moved is written immediately.
    /// </remarks>
    public static readonly TimeSpan ContactPersistInterval = TimeSpan.FromSeconds(30);

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

            LanPeerRecord? evicted = null;
            if (index < 0)
            {
                // One record is dropped per add, never a batch: a comparison against Min() would remove every
                // device that happened to share the oldest timestamp, and two devices seen in the same second
                // is ordinary, not a tie to break.
                if (records.Count >= maximumPeers)
                {
                    evicted = records.MinBy(older => older.LastSeenUtc);
                    if (evicted is not null) records.Remove(evicted);
                }
                records.Add(merged);
            }
            else records[index] = merged;

            await WriteUnlockedAsync(records, ct).ConfigureAwait(false);

            // Only after the write: a failure above leaves the row in place, and revoking the credential of a
            // device the list still shows would be a worse lie than the eviction itself. The evicted device's
            // token lives in a different file, so nothing else in this class would ever have touched it — and a
            // token whose row is gone still verifies, which is why the roster alone was not a real revocation.
            if (evicted is not null && revokeEvicted is not null)
                await revokeEvicted(evicted.DeviceId, ct).ConfigureAwait(false);

            return merged;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Records that a known device just spoke, and from where.</summary>
    /// <remarks>
    /// The in-memory row is always updated; the file is only rewritten when the sighting changes something the
    /// roster would show, or when the last write is older than <see cref="ContactPersistInterval"/>. This runs on
    /// every authenticated request, so writing unconditionally put a full serialise-and-rename on the hot path of
    /// a client that polls several times a second.
    /// </remarks>
    public async Task RememberContactAsync(string deviceId, string address, int port, DateTimeOffset now, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            var index = records.FindExisting(deviceId);
            if (index < 0) return;
            var before = records[index];
            var after = before.SeenFrom(address, port, now);
            records[index] = after;
            if (ContactNeedsPersisting(before, after, now))
                await WriteUnlockedAsync(records, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Whether a sighting is worth a disk write, or is the same row arriving again.</summary>
    private static bool ContactNeedsPersisting(LanPeerRecord before, LanPeerRecord after, DateTimeOffset now) =>
        after.Port != before.Port
        || !after.SeenAddresses.SequenceEqual(before.SeenAddresses, StringComparer.OrdinalIgnoreCase)
        || now - before.LastSeenUtc >= ContactPersistInterval;

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
