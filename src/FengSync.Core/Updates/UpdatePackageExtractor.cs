using System.IO.Compression;

namespace FengSync.Core.Updates;

public sealed class UpdatePackageExtractor
{
    public const int MaximumFiles = 20_000; public const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    public async Task<string> ExtractAndValidateAsync(string zipPath, string taskDirectory, string releaseTag, CancellationToken cancellationToken = default)
    {
        var staging = Path.Combine(taskDirectory, "staging"); if (Directory.Exists(staging)) throw new InvalidOperationException("staging 必须是新建空目录。"); Directory.CreateDirectory(staging); var root = ReleaseManifestValidator.EnsureTrailing(Path.GetFullPath(staging)); long total = 0; int count = 0;
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested(); if (++count > MaximumFiles) throw new InvalidDataException("ZIP 文件数超过上限。");
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var normalized = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(normalized) || normalized.Split(Path.DirectorySeparatorChar).Any(x => x == "..") || entry.ExternalAttributes >> 16 is 0xA000) throw new InvalidDataException("ZIP 包含不安全条目。");
                total += entry.Length; if (total > MaximumExpandedBytes) throw new InvalidDataException("解压大小超过上限。");
                var destination = Path.GetFullPath(Path.Combine(root, normalized)); if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ZIP 路径穿越。");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); await using var input = entry.Open(); await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true); await input.CopyToAsync(output, cancellationToken);
            }
            var dirs = Directory.GetDirectories(staging); var files = Directory.GetFiles(staging); var payload = dirs.Length == 1 && files.Length == 0 ? dirs[0] : staging;
            if (Directory.EnumerateFiles(payload, "FengSync.exe", SearchOption.TopDirectoryOnly).Count() != 1 || !File.Exists(Path.Combine(payload, "FengSync.Updater.exe")) || !File.Exists(Path.Combine(payload, "release-manifest.json"))) throw new InvalidDataException("更新包缺少必要程序文件。");
            var manifest = await ReleaseManifest.LoadAsync(Path.Combine(payload, "release-manifest.json"), cancellationToken); var errors = (await ReleaseManifestValidator.ValidateFilesAsync(manifest, payload, cancellationToken)).Concat(ReleaseManifestValidator.Validate(manifest, releaseTag)).ToList(); if (errors.Count > 0) throw new InvalidDataException(string.Join(" ", errors));
            return payload;
        }
        catch { try { Directory.Delete(staging, true); } catch { } throw; }
    }
}
