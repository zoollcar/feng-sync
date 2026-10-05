namespace FengSync.Core.Mount;

/// <summary>Where a cloud remote should be mounted on the local Windows filesystem.</summary>
public enum MountTargetKind
{
    /// <summary>An unused drive letter like <c>X:</c>.</summary>
    DriveLetter,
    /// <summary>An empty directory path. Feng Sync never auto-creates it.</summary>
    Directory,
    /// <summary>
    /// A UNC path such as <c>\\server\share</c>. Appended last because <c>MountRecordStore</c> persists the
    /// value as a number. Measured: rclone accepts it and answers with the drive letter Windows allocated —
    /// asking for <c>\\localhost\nonexistent-share-xyz\mnt</c> returned <c>{"mountPoint": "Z:"}</c> — so the
    /// requested string is not the mount point that ends up existing, and the caller has to use the answer.
    /// </summary>
    NetworkShare
}

/// <summary>Mount intent supplied by the UI. The remote name is the rclone.conf remote id.</summary>
public sealed record MountTarget(string RemoteName, string Provider, string MountPoint, MountTargetKind Kind, string RootPath = "");
