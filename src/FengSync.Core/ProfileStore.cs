using System.Text.Json;

namespace FengSync.Core;

/// <summary>Portable profile storage. Credentials deliberately stay outside profiles.</summary>
public sealed class ProfileStore
{
    private readonly string _path;
    public ProfileStore(string? path = null) => _path = path ?? Path.Combine(AppDataPaths.Root, "profiles.json");
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
    public async Task UpdateAsync(SyncProfile profile, CancellationToken ct = default)
    {
        var profiles = (await LoadAsync(ct)).ToList();
        var index = profiles.FindIndex(x => x.Id == profile.Id);
        if (index < 0) throw new InvalidOperationException("找不到要更新的 Profile。");
        if (profiles.Where(x => x.Id != profile.Id).Any(x => string.Equals(x.Name, profile.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("已存在相同名称的 Profile。");
        profiles[index] = profile;
        await SaveAsync(profiles, ct);
    }
}
