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
public sealed class LanDeviceIdentity(string DeviceId, string DeviceName, string Kind)
{
    public string DeviceId { get; } = DeviceId;
    public string DeviceName { get; } = DeviceName;

    /// <summary>Either <see cref="LanProtocol.KindWindows"/> or <see cref="LanProtocol.KindPhone"/>.</summary>
    public string Kind { get; } = Kind;

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
                        return new(stored.DeviceId, NameOf(stored.DeviceName), stored.Kind is LanProtocol.KindPhone ? LanProtocol.KindPhone : LanProtocol.KindWindows);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Fall through and mint a fresh identity.
        }

        var created = new LanDeviceRecord(Guid.NewGuid().ToString("N"), Environment.MachineName, LanProtocol.KindWindows);
        await WriteAsync(file, created, ct).ConfigureAwait(false);
        return new(created.DeviceId, NameOf(created.DeviceName), created.Kind);
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
