using System.Text.Json;

namespace FengSync.Core;

/// <summary>Portable profile storage. Credentials deliberately stay outside profiles.</summary>
public sealed class ProfileStore
{
    private readonly string _path;
    public ProfileStore(string? path = null) => _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FengSync", "profiles.json");
    public async Task<IReadOnlyList<SyncProfile>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<SyncProfile>>(stream, cancellationToken: ct) ?? [];
    }
    public async Task SaveAsync(IEnumerable<SyncProfile> profiles, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, profiles, cancellationToken: ct);
        File.Move(temporary, _path, true);
    }
}
