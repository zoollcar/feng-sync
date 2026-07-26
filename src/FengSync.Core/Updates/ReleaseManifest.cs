using System.Text.Json;

namespace FengSync.Core.Updates;

public sealed record ReleaseManifest(string Product, string Version, string Runtime, IReadOnlyList<ReleaseManifestFile> Files)
{
    public static async Task<ReleaseManifest> LoadAsync(string path, CancellationToken cancellationToken = default)
        => JsonSerializer.Deserialize<ReleaseManifest>(await File.ReadAllTextAsync(path, cancellationToken), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
           ?? throw new InvalidDataException("发布清单为空。");

    public Task SaveAsync(string path, CancellationToken cancellationToken = default)
        => File.WriteAllTextAsync(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
}

public sealed record ReleaseManifestFile(string Path, long Size, string Sha256);
