namespace FengSync.Core;

/// <summary>Explicit maintenance only: startup never removes resumable staging files.</summary>
public static class TransferTemporaryMaintenance
{
    public static int RemoveExpiredLocalFiles(IEnumerable<string> roots, TimeSpan minimumAge, DateTimeOffset now)
    {
        var removed = 0;
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (var path in Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!SyncInternalPaths.IsTransferTemporary(relative)) continue;
            if (now - File.GetLastWriteTimeUtc(path) < minimumAge) continue;
            File.Delete(path); removed++;
        }
        return removed;
    }
}
