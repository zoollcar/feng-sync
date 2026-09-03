using System.Security.Cryptography;

namespace FengSync.Core.Updates;

public static class ReleaseManifestValidator
{
    public static IReadOnlyList<string> Validate(ReleaseManifest manifest, string? expectedVersion = null)
    {
        var errors = new List<string>();
        if (manifest.Product != "FengSync") errors.Add("product 必须是 FengSync。");
        if (manifest.Runtime != "win-x64") errors.Add("runtime 必须是 win-x64。");
        if (!ReleaseVersion.TryParse(manifest.Version, out var v) || v.IsPrerelease) errors.Add("version 必须是正式三段语义版本。");
        if (expectedVersion is not null && !string.Equals(manifest.Version, expectedVersion.TrimStart('v'), StringComparison.Ordinal)) errors.Add("清单版本与 Release tag 不一致。");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        string? previous = null;
        foreach (var file in manifest.Files)
        {
            if (!IsSafeRelativePath(file.Path)) errors.Add($"文件路径不安全：{file.Path}");
            if (!paths.Add(file.Path)) errors.Add($"文件路径重复：{file.Path}");
            if (previous is not null && StringComparer.Ordinal.Compare(previous, file.Path) >= 0) errors.Add("files 必须按 path 排序。");
            previous = file.Path;
            if (file.Size < 0) errors.Add($"文件大小无效：{file.Path}");
            if (file.Sha256.Length != 64 || !file.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) errors.Add($"SHA-256 必须为小写十六进制：{file.Path}");
        }
        if (manifest.Files.Count == 0) errors.Add("发布清单不得为空。");
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.Contains(':')) return false;
        var segments = path.Split('/', StringSplitOptions.None);
        return segments.All(x => x.Length > 0 && x != "." && x != ".." && !x.Contains('\\'));
    }

    public static async Task<IReadOnlyList<string>> ValidateFilesAsync(ReleaseManifest manifest, string root, CancellationToken cancellationToken = default)
    {
        var errors = Validate(manifest).ToList(); var rootFull = EnsureTrailing(Path.GetFullPath(root));
        var expected = new HashSet<string>(manifest.Files.Select(x => x.Path.Replace('/', Path.DirectorySeparatorChar)), StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var target = Path.GetFullPath(Path.Combine(rootFull, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) { errors.Add($"清单文件不存在：{file.Path}"); continue; }
            if (new FileInfo(target).Length != file.Size) errors.Add($"文件大小不匹配：{file.Path}");
            await using var stream = File.OpenRead(target);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(file.Sha256))) errors.Add($"文件 SHA-256 不匹配：{file.Path}");
        }
        // A packaged executable/dll must be accounted for; user data is not considered here.
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Equals("release-manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
            if ((relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) && !expected.Contains(relative.Replace('/', Path.DirectorySeparatorChar))) errors.Add($"发现清单外程序文件：{relative}");
        }
        return errors;
    }
    internal static string EnsureTrailing(string path) => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
