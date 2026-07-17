namespace FengSync.Core;

/// <summary>Windows-compatible destination validation performed before a plan can expose destructive operations.</summary>
public static class PathRules
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
    public static IReadOnlyList<SyncOperation> FindBlockers(IEnumerable<EntrySnapshot> left, IEnumerable<EntrySnapshot> right)
    {
        var result = new List<SyncOperation>();
        foreach (var item in left.Concat(right))
            if (!IsValid(item.Path, out var reason)) result.Add(new(item.Path, OperationKind.Blocked, reason, false));
        foreach (var side in new[] { left, right })
            foreach (var collision in side.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
                result.Add(new(collision.Key, OperationKind.Blocked, "同一端点存在大小写折叠后的重名路径。", false));
        return result.GroupBy(x => x.Path + "\u001f" + x.Reason, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
    }
    private static bool IsValid(string path, out string reason)
    {
        reason = ""; if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Split('/').Any(x => x is "." or "..")) { reason = "路径不是安全的相对路径。"; return false; }
        foreach (var part in path.Split('/'))
        {
            var name = Path.GetFileNameWithoutExtension(part);
            if (part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Reserved.Contains(name)) { reason = "路径无法映射到 Windows 文件名。"; return false; }
        }
        return true;
    }
}
