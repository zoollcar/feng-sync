namespace FengSync.Core.Mount;

/// <summary>
/// Decides which running mounts look broken and what to say about putting them back.
/// </summary>
/// <remarks>
/// Detection only. Feng Sync deliberately does <b>not</b> reconnect on its own: a silent background remount
/// would hide the failure the user needs to see, and a mount whose backing network is down would be re-created
/// on every poll forever. The scan already tells us whether a mount point is healthy; this turns that into a
/// sentence and a button.
/// </remarks>
public static class MountReconnect
{
    /// <summary>
    /// The mounts worth offering a reconnect for: Feng Sync's own, and not healthy. External and unreadable
    /// mounts are left out on purpose — this program does not own them and must not touch them.
    /// </summary>
    public static IReadOnlyList<MountInfo> NeedsReconnect(IReadOnlyList<MountInfo> mounts) =>
        [.. mounts.Where(mount => mount.Origin == MountOrigin.FengSyncManaged && !mount.IsHealthy)];

    /// <summary>The line the mount list shows when something is broken.</summary>
    public static string Describe(int count) => count switch
    {
        <= 0 => "",
        1 => "有 1 个挂载点已失效（可能网络中断或端点已断开）。重新挂载会先取消它再挂载一次。",
        _ => $"有 {count} 个挂载点已失效（可能网络中断或端点已断开）。重新挂载会先取消它们再挂载一次。"
    };

    /// <summary>
    /// Why a reconnect is being offered for this mount, named so the user recognises the row. A drive letter
    /// that Windows no longer reports ready and a directory that has vanished are different symptoms.
    /// </summary>
    public static string Explain(MountInfo mount) => mount.Kind == MountTargetKind.DriveLetter
        ? $"挂载点 {mount.MountPoint}（{mount.RemoteName}）已不可用，可能端点已断开或网络中断。"
        : $"挂载点 {mount.MountPoint}（{mount.RemoteName}）已不存在，可能端点已断开或网络中断。";
}
