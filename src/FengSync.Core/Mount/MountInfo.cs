namespace FengSync.Core.Mount;

/// <summary>How a running mount came to exist relative to this Feng Sync installation.</summary>
public enum MountOrigin
{
    /// <summary>The PID matches a record we wrote in <see cref="MountSessionStore"/>.</summary>
    FengSyncManaged,
    /// <summary>The PID was not written by us but the mount point looks healthy.</summary>
    External,
    /// <summary>Process exists but we couldn't read its command line; do not kill without explicit consent.</summary>
    Unreadable
}

/// <summary>UI-facing view of a single rclone mount. Combines process and session signals.</summary>
public sealed record MountInfo(
    int? Pid,
    string RemoteName,
    string Provider,
    string MountPoint,
    MountTargetKind Kind,
    DateTimeOffset? StartedUtc,
    MountOrigin Origin,
    bool IsHealthy)
{
    /// <summary>Only RC-managed mounts can be safely unmounted by Feng Sync.</summary>
    public bool CanUnmount => Origin == MountOrigin.FengSyncManaged;

    /// <summary>Compact list display like <c>[本应用] drive:Google  →  X:</c>.</summary>
    public string Display
    {
        get
        {
            var prefix = Origin switch
            {
                MountOrigin.FengSyncManaged => "[本应用]",
                MountOrigin.Unreadable => "[无权限]",
                _ => "[外部]"
            };
            var target = Kind == MountTargetKind.DriveLetter ? MountPoint : MountPoint;
            return $"{prefix} {RemoteName}  →  {target}";
        }
    }
}
