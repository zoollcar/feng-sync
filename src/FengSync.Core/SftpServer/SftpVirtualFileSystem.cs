namespace FengSync.Core.SftpServer;

public enum SftpFileAccess { Read, Write }

/// <summary>Resolves only normalized virtual paths contained by an explicitly configured share.</summary>
public sealed class SftpVirtualFileSystem
{
    private readonly Dictionary<string, SftpShare> _shares;

    public SftpVirtualFileSystem(IEnumerable<SftpShare> shares)
    {
        _shares = shares.ToDictionary(x => x.VirtualName, StringComparer.OrdinalIgnoreCase);
        if (_shares.Count == 0 || _shares.Values.Any(x => string.IsNullOrWhiteSpace(x.VirtualName) || !Directory.Exists(x.PhysicalPath)))
            throw new InvalidOperationException("SFTP 共享目录必须存在且具有唯一虚拟名称。");
    }

    public string Resolve(string virtualPath, SftpFileAccess access)
    {
        if (string.IsNullOrWhiteSpace(virtualPath) || !virtualPath.StartsWith('/')) throw new UnauthorizedAccessException("虚拟路径无效。");
        var parts = virtualPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !_shares.TryGetValue(parts[0], out var share)) throw new UnauthorizedAccessException("未授权的共享目录。");
        if (access == SftpFileAccess.Write && share.Permission != SftpPermission.ReadWrite) throw new UnauthorizedAccessException("共享目录为只读。");
        if (parts.Skip(1).Any(x => x is "." or ".." || x.Contains(':') || x.Contains(Path.DirectorySeparatorChar) || x.Contains(Path.AltDirectorySeparatorChar))) throw new UnauthorizedAccessException("路径穿越被拒绝。");

        var root = Path.GetFullPath(share.PhysicalPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, Path.Combine(parts.Skip(1).ToArray())));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("路径越过共享根目录。");
        RejectLinkedAncestors(root, candidate);
        return candidate;
    }

    private static void RejectLinkedAncestors(string root, string candidate)
    {
        var current = root.TrimEnd(Path.DirectorySeparatorChar);
        var relative = Path.GetRelativePath(root, candidate);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new UnauthorizedAccessException("符号链接或 junction 不允许用于 SFTP 路径。");
        }
    }
}
