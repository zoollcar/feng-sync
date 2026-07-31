namespace FengSync.Core.Mount;

/// <summary>Where a cloud remote should be mounted on the local Windows filesystem.</summary>
public enum MountTargetKind
{
    /// <summary>An unused drive letter like <c>X:</c>.</summary>
    DriveLetter,
    /// <summary>An empty directory path. Feng Sync never auto-creates it.</summary>
    Directory
}

/// <summary>Mount intent supplied by the UI. The remote name is the rclone.conf remote id.</summary>
public sealed record MountTarget(string RemoteName, string Provider, string MountPoint, MountTargetKind Kind);