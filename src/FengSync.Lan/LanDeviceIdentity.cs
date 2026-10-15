using System.Text.Json;
using FengSync.Core;

namespace FengSync.Lan;

/// <summary>
/// Who this machine is on the LAN, as far as its peers are concerned.
/// </summary>
/// <remarks>
/// The identifier is created once and kept, because a peer's trust is keyed on it: a new identifier every
/// launch would turn every returning phone into a stranger needing a fresh code and a fresh confirmation.
/// It carries no secret — the tokens live in <see cref="LanPeerSecretStore"/> — so this file could be read
/// out loud without handing anybody access to anything.
/// </remarks>
public sealed class LanDeviceIdentity
{
    private readonly string _file;

    public LanDeviceIdentity(string deviceId, string deviceName, string kind, string file)
    {
        DeviceId = deviceId;
        DeviceName = NameOf(deviceName);
        Kind = kind is LanProtocol.KindPhone ? LanProtocol.KindPhone : LanProtocol.KindWindows;
        _file = file;
    }

    public string DeviceId { get; }

    /// <summary>
    /// The name peers read. Writeable because 改名 is a choice the user makes while the window is already open,
    /// and every holder of this object — the hello reply, the QR, the discovery beacon, the room's own author
    /// rows — must answer with whatever it is at that moment rather than with the name the machine had at start-up.
    /// </summary>
    public string DeviceName { get; private set; }

    /// <summary>Either <see cref="LanProtocol.KindWindows"/> or <see cref="LanProtocol.KindPhone"/>.</summary>
    public string Kind { get; }

    /// <summary>
    /// Reads the persisted identity, creating it on first use.
    /// </summary>
    /// <remarks>
    /// An unreadable file is replaced rather than quarantined, unlike the settings store: losing this file
    /// costs the user one re-pairing on each device, whereas the settings file holds choices nobody can
    /// restate. A half-written identity is never loadable, so trusting the machine name and minting a new
    /// identifier is the only way to keep the listener starting at all.
    /// </remarks>
    public static async Task<LanDeviceIdentity> LoadOrCreateAsync(string? path = null, CancellationToken ct = default)
    {
        var file = path ?? System.IO.Path.Combine(AppDataPaths.LanTransfer, "device.json");
        try
        {
            if (File.Exists(file))
            {
                await using var stream = File.OpenRead(file);
                if (stream.Length > 0)
                {
                    var stored = await JsonSerializer.DeserializeAsync<LanDeviceRecord>(stream, cancellationToken: ct).ConfigureAwait(false);
                    if (stored is not null && !string.IsNullOrWhiteSpace(stored.DeviceId))
                        return new(stored.DeviceId, stored.DeviceName, stored.Kind, file);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Fall through and mint a fresh identity.
        }

        var created = new LanDeviceRecord(Guid.NewGuid().ToString("N"), Environment.MachineName, LanProtocol.KindWindows);
        await WriteAsync(file, created, ct).ConfigureAwait(false);
        return new(created.DeviceId, created.DeviceName, created.Kind, file);
    }

    /// <summary>
    /// Chooses a new name, keeps it, and answers with the name this machine will now be listed under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This object is mutated rather than replaced, on purpose: the listener holds the instance it was started
    /// with, so an identity that came back new would leave the old one answering <c>/hello</c> with a name the
    /// user has just thrown away. One reference write is what a peer arriving mid-rename reads, so it sees either
    /// the old name or the new one and never a mixture of the two.
    /// </para>
    /// <para>
    /// A blank is not a rename, and the current name comes back instead: an empty row in a peer's list is worse
    /// than a stale one, and which name to fall back to is the window's decision rather than a sentence this
    /// library could invent. The file is the only store for a device name, so a later window reads exactly this
    /// choice instead of the machine name again.
    /// </para>
    /// </remarks>
    public async Task<string> RenameAsync(string? name, CancellationToken ct = default)
    {
        var chosen = LanProtocol.SanitizePeerName(name);
        if (chosen.Length == 0) return DeviceName;

        DeviceName = chosen;
        await WriteAsync(_file, new LanDeviceRecord(DeviceId, chosen, Kind), ct).ConfigureAwait(false);
        return chosen;
    }

    /// <summary>The name a peer sees. A blank machine name would render as an empty row in the other window.</summary>
    private static string NameOf(string? stored) => string.IsNullOrWhiteSpace(stored) ? Environment.MachineName : stored;

    private static async Task WriteAsync(string file, LanDeviceRecord record, CancellationToken ct)
    {
        var directory = System.IO.Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = file + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, record, cancellationToken: ct).ConfigureAwait(false);
        File.Move(temporary, file, true);
    }

    private sealed record LanDeviceRecord(string DeviceId, string DeviceName, string Kind);
}
