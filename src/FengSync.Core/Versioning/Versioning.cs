namespace FengSync.Core;

public sealed record RetentionPolicy(int? KeepDays = null, int? MaxVersionsPerFile = null, long? MaxTotalBytes = null)
{
    public void Validate()
    {
        if (KeepDays is < 0 || MaxVersionsPerFile is < 0 || MaxTotalBytes is < 0) throw new ArgumentOutOfRangeException(nameof(KeepDays), "保留限制不能为负数。");
    }
}
public sealed record RetentionCandidate(string Path, long Size, DateTimeOffset ModifiedUtc, string Reason);

public static class ArchivePathValidator
{
    public static SafetyValidationResult Validate(string archiveDirectory, IEnumerable<string> syncRoots)
    {
        if (String.IsNullOrWhiteSpace(archiveDirectory)) return PathTopologyValidator.Block("archive.missing", "版本目录不能为空。");
        var archive = PathTopologyValidator.Canonical(archiveDirectory);
        foreach (var root in syncRoots)
        {
            var canonicalRoot = PathTopologyValidator.Canonical(root);
            if (StringComparer.OrdinalIgnoreCase.Equals(archive, canonicalRoot) || PathTopologyValidator.Contains(canonicalRoot, archive))
                return PathTopologyValidator.Block("archive.nested", "版本目录不能位于任一同步目录内，否则会被再次扫描。", archive);
        }
        return SafetyValidationResult.Pass;
    }
}

public interface IDeletionStrategy
{
    Task DeleteAsync(IEndpoint endpoint, string relativePath, bool directory, CancellationToken ct = default);
}
public sealed class PermanentDeleteStrategy : IDeletionStrategy
{
    public Task DeleteAsync(IEndpoint endpoint, string relativePath, bool directory, CancellationToken ct = default) => endpoint.DeleteAsync(relativePath, directory, ct);
}
public sealed class ArchiveStrategy(string archiveDirectory) : IDeletionStrategy
{
    private readonly string _archiveDirectory = PathTopologyValidator.Canonical(archiveDirectory);
    public async Task DeleteAsync(IEndpoint endpoint, string relativePath, bool directory, CancellationToken ct = default)
    {
        if (endpoint is not LocalEndpoint local) throw new NotSupportedException("当前远程端点不支持本地版本目录归档。");
        var source = local.PhysicalPath(relativePath);
        if (!File.Exists(source) && !Directory.Exists(source)) return;
        var destination = Path.Combine(_archiveDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (directory) { Directory.Move(source, destination); return; }
        try { File.Move(source, destination, false); }
        catch (IOException) // Cross-volume moves are copy-then-delete.
        {
            await using var input = File.OpenRead(source); await using var output = File.Create(destination);
            await input.CopyToAsync(output, ct); await output.FlushAsync(ct);
            if (new FileInfo(source).Length != new FileInfo(destination).Length) { File.Delete(destination); throw new IOException("归档校验失败。"); }
            File.Delete(source);
        }
    }
}
public sealed class RecycleBinStrategy : IDeletionStrategy
{
    public Task DeleteAsync(IEndpoint endpoint, string relativePath, bool directory, CancellationToken ct = default)
    {
        if (endpoint is not LocalEndpoint local) throw new NotSupportedException("远程端点不支持 Windows 回收站。");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("回收站仅支持 Windows 本地端点。");
        var path = local.PhysicalPath(relativePath);
        if (!File.Exists(path) && !Directory.Exists(path)) return Task.CompletedTask;
        var op = new SHFILEOPSTRUCT { wFunc = 3, pFrom = path + '\0' + '\0', fFlags = 0x0040 | 0x0010 | 0x0400 };
        if (SHFileOperation(ref op) != 0 || op.fAnyOperationsAborted) throw new IOException("无法移入回收站。");
        return Task.CompletedTask;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct SHFILEOPSTRUCT { public IntPtr hwnd; public uint wFunc; public string pFrom; public string? pTo; public ushort fFlags; public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string? lpszProgressTitle; }
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}

public sealed class RetentionCleanupService
{
    public Task<IReadOnlyList<RetentionCandidate>> PreviewAsync(string archiveDirectory, RetentionPolicy policy, CancellationToken ct = default)
    {
        policy.Validate(); if (!Directory.Exists(archiveDirectory)) return Task.FromResult<IReadOnlyList<RetentionCandidate>>([]);
        var candidates = new Dictionary<string, RetentionCandidate>(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(archiveDirectory, "*", SearchOption.AllDirectories).Select(x => new FileInfo(x)).OrderByDescending(x => x.LastWriteTimeUtc).ToList();
        if (policy.KeepDays is int days) foreach (var file in files.Where(x => x.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-days))) candidates[file.FullName] = new(file.FullName, file.Length, file.LastWriteTimeUtc, "超过保留天数");
        if (policy.MaxVersionsPerFile is int count)
            // Archives are stored under <timestamp>/<original-relative-path>. Grouping only by
            // file name would incorrectly evict distinct files such as a/readme.txt and b/readme.txt.
            foreach (var group in files.GroupBy(ArchivedRelativePath, StringComparer.OrdinalIgnoreCase))
                foreach (var file in group.Skip(count))
                    candidates[file.FullName] = new(file.FullName, file.Length, file.LastWriteTimeUtc, "超过每文件版本数");
        if (policy.MaxTotalBytes is long max)
        {
            long total = files.Sum(x => x.Length);
            foreach (var file in files.OrderBy(x => x.LastWriteTimeUtc)) { if (total <= max) break; candidates[file.FullName] = new(file.FullName, file.Length, file.LastWriteTimeUtc, "超过总容量"); total -= file.Length; }
        }
        return Task.FromResult<IReadOnlyList<RetentionCandidate>>(candidates.Values.ToList());
    }
    public async Task<int> CleanupAsync(string archiveDirectory, RetentionPolicy policy, CancellationToken ct = default)
    {
        var candidates = await PreviewAsync(archiveDirectory, policy, ct); foreach (var file in candidates) { ct.ThrowIfCancellationRequested(); File.Delete(file.Path); } return candidates.Count;
    }

    private static string ArchivedRelativePath(FileInfo file)
    {
        var timestampDirectory = file.Directory;
        if (timestampDirectory?.Parent is null) return file.Name;
        return Path.GetRelativePath(timestampDirectory.Parent.FullName, file.FullName);
    }
}
