namespace FengSync.Core.Mount;

/// <summary>
/// The line shown after a mount has started.
/// </summary>
/// <remarks>
/// <para>
/// It reports the mount point rclone <b>answered</b> with, not the one that was requested, because those two are
/// not always the same. Measured against the bundled v1.75.0: a UNC target (<c>\\host\share\dir</c>) is accepted
/// by <c>mount/mount</c> and comes back as the drive letter Windows allocated — the answer to a request for
/// <c>\\localhost\nonexistent-share-xyz\mnt</c> was <c>{"mountPoint": "Z:"}</c>, and <c>*</c> answered <c>"Y:"</c>.
/// Announcing the requested path there would be a statement about a mount point that does not exist.
/// </para>
/// <para>
/// A pure rule in Core rather than a line inside the window, so that this — the whole visible consequence of
/// D6 — can be asserted without a driver, a share, or WinFsp.
/// </para>
/// </remarks>
public static class MountStatusLine
{
    /// <summary>What to say about a mount that just started.</summary>
    public static string Describe(MountTarget target, MountResult result)
    {
        var headline = string.Equals(target.MountPoint, result.MountPoint, StringComparison.OrdinalIgnoreCase)
            ? $"已启动挂载：{target.RemoteName} → {result.MountPoint}。"
            : $"已启动挂载：{target.RemoteName} → {result.MountPoint}"
                + $"（请求的是 {target.MountPoint}，Windows 改用了 {result.MountPoint}）。";
        return result.DiskSpaceTotalSize is null
            ? headline
            : headline + " " + MountCapacity.Describe(result.DiskSpaceTotalSize);
    }
}
