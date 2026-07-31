namespace FengSync.Core.Mount;

/// <summary>Lifecycle status of a persisted mount session.</summary>
public enum MountSessionStatus
{
    /// <summary>The session was started by the current Feng Sync process and has not yet been confirmed stopped.</summary>
    Active,
    /// <summary>The session was confirmed stopped (process exited and the mount point went away).</summary>
    Stopped,
    /// <summary>The session was active when Feng Sync last shut down and has not been cleaned up yet.</summary>
    Orphaned
}

/// <summary>
/// Persistent record of a mount that this Feng Sync installation created. Feng Sync only ever kills PIDs
/// it actually wrote here; records are deleted after the mount point disappears so an abnormal shutdown
/// never hides an externally-owned rclone mount from the user.
/// </summary>
public sealed record MountSessionRecord(
    Guid Id,
    string RemoteName,
    string Provider,
    string MountPoint,
    MountTargetKind Kind,
    int Pid,
    DateTimeOffset StartedUtc,
    MountSessionStatus Status);