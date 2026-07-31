using System.Text.Json;

namespace FengSync.Core.SftpServer;

/// <summary>Persists non-secret rclone SFTP settings. Legacy multi-share configuration is intentionally discarded.</summary>
public sealed class SftpServerSettingsStore
{
    private readonly string _path;
    public bool LegacyConfigurationRemoved { get; private set; }
    public SftpServerSettingsStore(string? path = null) => _path = path ?? Path.Combine(AppDataPaths.Root, "sftp", "sftp-server.json");

    public async Task<SftpServerOptions> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return new SftpServerOptions();
        var json = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var version = document.RootElement.TryGetProperty("SchemaVersion", out var schema) && schema.TryGetInt32(out var value) ? value : 0;
        if (version < 4)
        {
            File.Delete(_path);
            new SftpPasswordStore(Path.Combine(Path.GetDirectoryName(_path)!, "server-password.dat")).Clear();
            LegacyConfigurationRemoved = true;
            return new SftpServerOptions();
        }
        return JsonSerializer.Deserialize<PersistedSettings>(json)?.ToOptions() ?? new SftpServerOptions();
    }

    public async Task SaveAsync(SftpServerOptions options, CancellationToken ct = default)
    {
        options.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await using (var target = File.Create(temporary)) await JsonSerializer.SerializeAsync(target, PersistedSettings.From(options), cancellationToken: ct).ConfigureAwait(false);
        File.Move(temporary, _path, true);
    }

    private sealed record PersistedSettings(int SchemaVersion, bool Enabled, bool StartWithApplication, string ListenAddress, int Port, string? RootPath, string? UserName, string? HostKeyPath, long CacheMaxSizeBytes, bool PasswordConfigured)
    {
        public static PersistedSettings From(SftpServerOptions value) => new(4, value.Enabled, value.StartWithApplication, value.ListenAddress, value.Port, value.RootPath, value.UserName, value.HostKeyPath, value.CacheMaxSizeBytes, value.PasswordConfigured);
        public SftpServerOptions ToOptions() => new(Enabled, StartWithApplication, ListenAddress, Port, RootPath, UserName, HostKeyPath, CacheMaxSizeBytes, PasswordConfigured);
    }
}
