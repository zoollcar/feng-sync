namespace FengSync.Core.Mount;

/// <summary>How much disk the mount VFS cache is holding.</summary>
public sealed record MountCacheUsage(string Root, long Bytes, int Files)
{
    public bool IsEmpty => Files == 0;
}

/// <summary>
/// Measures and clears the mount VFS cache. The cache is daemon-wide (rclone resolves its cache root once at
/// process start), so clearing it is an all-mounts action and is described that way.
/// </summary>
/// <remarks>
/// <see cref="Clear"/> refuses any root that is not inside the application data directory. That guard is the
/// whole safety story: the caller passes a path that came from configuration, and a mis-set value must not turn
/// "clear the cache" into a recursive delete somewhere else. Deletion is file-by-file rather than
/// <c>Directory.Delete(root, recursive: true)</c>, and a file that cannot be deleted (a mount is using it) is
/// counted as kept rather than failing the whole operation.
/// </remarks>
public static class MountCacheInspector
{
    /// <summary>Sizes the cache, or reports an empty one when the directory does not exist yet.</summary>
    public static MountCacheUsage Measure(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return new(root, 0, 0);
            long bytes = 0;
            var files = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(file).Length; files++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return new(root, bytes, files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(root, 0, 0); }
    }

    /// <summary>
    /// Deletes every cached file under <paramref name="root"/>. Returns how many files went away; files still
    /// held open by a mount stay behind, which is the honest outcome rather than a failure.
    /// </summary>
    public static int Clear(string root, string? dataRoot = null)
    {
        var allowed = Path.GetFullPath(dataRoot ?? AppDataPaths.Root);
        var target = Path.GetFullPath(root);
        if (!IsInside(allowed, target)) return 0;
        if (!Directory.Exists(target)) return 0;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            try { File.Delete(file); removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    /// <summary>Whether <paramref name="candidate"/> is <paramref name="root"/> or sits under it.</summary>
    public static bool IsInside(string root, string candidate)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedRoot, candidate, StringComparison.OrdinalIgnoreCase)) return true;
        return candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What the mount list says about the cache, including that the ceiling is a mount-time setting.</summary>
    public static string Describe(MountCacheUsage usage, long? limitBytes)
    {
        var size = Configuration.ByteSize.Format(usage.Bytes);
        var limit = limitBytes is > 0 ? Configuration.ByteSize.Format(limitBytes.Value) : "不限";
        return usage.IsEmpty
            ? $"挂载缓存：{usage.Root}（当前为空，上限 {limit}）"
            : $"挂载缓存：{size} / {limit}，{usage.Files} 个文件（{usage.Root}）";
    }
}
