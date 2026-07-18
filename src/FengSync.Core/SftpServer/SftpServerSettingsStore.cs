using System.Text.Json;

namespace FengSync.Core.SftpServer;

/// <summary>
/// Versioned persistence for the SFTP service configuration. Passwords are represented only by
/// PBKDF2 verifiers in <see cref="SftpAccount"/>; plaintext credentials are never accepted here.
/// The host private key is deliberately not serialized: it is owned by <see cref="SftpHostKeyStore"/>.
/// </summary>
public sealed class SftpServerSettingsStore
{
    private readonly string _path;

    public SftpServerSettingsStore(string? path = null) =>
        _path = path ?? Path.Combine(AppDataPaths.Root, "sftp", "sftp-server.json");

    public async Task<SftpServerOptions> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return new SftpServerOptions();
        await using var source = File.OpenRead(_path);
        var saved = await JsonSerializer.DeserializeAsync<PersistedSettings>(source, cancellationToken: ct).ConfigureAwait(false);
        return saved?.ToOptions() ?? new SftpServerOptions();
    }

    public async Task SaveAsync(SftpServerOptions options, CancellationToken ct = default)
    {
        options.Validate();
        var saved = PersistedSettings.From(options);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await using (var target = File.Create(temporary))
            await JsonSerializer.SerializeAsync(target, saved, cancellationToken: ct).ConfigureAwait(false);
        File.Move(temporary, _path, true);
    }

    private sealed record PersistedSettings(
        int SchemaVersion, bool Enabled, bool StartWithApplication, string ListenAddress, int Port,
        int MaxConnections, TimeSpan? IdleTimeout, IReadOnlyList<SftpAccount>? Accounts, IReadOnlyList<SftpShare>? Shares,
        string? NodeExecutablePath = null, string? NodeModulePath = null, long MaxUploadBytes = 1_073_741_824,
        int MaxAuthenticationFailures = 5, TimeSpan? AuthenticationBlockDuration = null)
    {
        public static PersistedSettings From(SftpServerOptions value) => new(3, value.Enabled, value.StartWithApplication,
            value.ListenAddress, value.Port, value.MaxConnections, value.IdleTimeout, value.Accounts, value.Shares,
            value.NodeExecutablePath, value.NodeModulePath, value.MaxUploadBytes, value.MaxAuthenticationFailures, value.AuthenticationBlockDuration);
        public SftpServerOptions ToOptions() => new(Enabled, StartWithApplication, ListenAddress, Port, MaxConnections,
            IdleTimeout, NodeExecutablePath, NodeModulePath, Accounts: Accounts, Shares: Shares, MaxUploadBytes: MaxUploadBytes,
            MaxAuthenticationFailures: MaxAuthenticationFailures, AuthenticationBlockDuration: AuthenticationBlockDuration);
    }
}
