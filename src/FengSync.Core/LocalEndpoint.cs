using System.Security.Cryptography;

namespace FengSync.Core;
public sealed class LocalEndpoint(string root) : IEndpoint
{
    public string Root { get; } = Path.GetFullPath(root);
    public EndpointProfile Profile { get; } = new(Guid.NewGuid(), EndpointType.Local, Path.GetFullPath(root), Identity: Path.GetFullPath(root));
    public EndpointCapabilities Capabilities { get; } = new(true, true, true, TimeSpan.Zero);
    public IEnumerable<EntrySnapshot> Scan()
    {
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
        foreach (var path in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
            if (relative.Equals("sync.fengdb", StringComparison.OrdinalIgnoreCase) || relative.Contains(".fengsync-") || relative.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) continue;
            var attr = File.GetAttributes(path); if (attr.HasFlag(FileAttributes.System) || attr.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (attr.HasFlag(FileAttributes.Directory)) yield return new(relative, EntryKind.Directory, null);
            else { var f = new FileInfo(path); yield return new(relative, EntryKind.File, new(f.Length, f.LastWriteTimeUtc, Hash(path))); }
        }
    }
    public string PhysicalPath(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    public Task<IReadOnlyList<EntrySnapshot>> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<EntrySnapshot>>(Scan().ToList());
    public Task<IReadOnlyList<EntrySnapshot>> ScanAsync(IProgress<ScanProgress> progress, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
        return Task.Run<IReadOnlyList<EntrySnapshot>>(() =>
        {
            var entries = new List<EntrySnapshot>();
            foreach (var path in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
                if (relative.Equals("sync.fengdb", StringComparison.OrdinalIgnoreCase) || relative.Contains(".fengsync-") || relative.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) continue;
                var attr = File.GetAttributes(path); if (attr.HasFlag(FileAttributes.System) || attr.HasFlag(FileAttributes.ReparsePoint)) continue;
                entries.Add(attr.HasFlag(FileAttributes.Directory)
                    ? new(relative, EntryKind.Directory, null)
                    : new EntrySnapshot(relative, EntryKind.File, new Fingerprint(new FileInfo(path).Length, File.GetLastWriteTimeUtc(path), Hash(path))));
                if (entries.Count == 1 || entries.Count % 25 == 0) progress.Report(new(entries.Count, relative));
            }
            progress.Report(new(entries.Count, entries.LastOrDefault()?.Path, true));
            return entries;
        }, cancellationToken);
    }
    public async Task CopyToAsync(string relativePath, IEndpoint target, string temporaryPath, CancellationToken cancellationToken = default)
    {
        if (target is not LocalEndpoint localTarget) throw new NotSupportedException("本地端点需要通过统一端点执行器传输到远程端点。");
        var source = PhysicalPath(relativePath); var destination = localTarget.PhysicalPath(temporaryPath); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = File.OpenRead(source); await using var output = File.Create(destination); await input.CopyToAsync(output, cancellationToken);
    }
    public Task MoveAsync(string from, string to, CancellationToken cancellationToken = default)
    { var target = PhysicalPath(to); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Move(PhysicalPath(from), target, true); return Task.CompletedTask; }
    public Task DeleteAsync(string relativePath, bool directory, CancellationToken cancellationToken = default)
    { var path = PhysicalPath(relativePath); if (directory) { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); } else if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; }
    public Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default) { Directory.CreateDirectory(PhysicalPath(relativePath)); return Task.CompletedTask; }
    private static string Hash(string path) { using var sha = SHA256.Create(); using var s = File.OpenRead(path); return Convert.ToHexString(sha.ComputeHash(s)); }
}
