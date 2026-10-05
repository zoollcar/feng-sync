using System.Runtime.Versioning;

namespace FengSync.Core.Mount;

/// <summary>Validates a mount point candidate against the host filesystem and existing rclone mounts.</summary>
[SupportedOSPlatform("windows")]
public static class MountPointInspector
{
    /// <summary>26 uppercase drive letters; <c>IsAvailable</c> reflects current usage.</summary>
    public sealed record DriveLetterOption(string Letter, bool IsAvailable);

    /// <summary>Walk A:–Z: and report which letters are not currently claimed by Windows.</summary>
    public static IReadOnlyList<DriveLetterOption> EnumerateDriveLetters()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            // Normalize "C:\" to "C:" so we can compare directly with the user-provided mount point.
            var name = drive.Name.TrimEnd('\\', '/');
            if (name.Length >= 2) used.Add(name[..2]);
        }
        var result = new List<DriveLetterOption>(26);
        for (var c = 'A'; c <= 'Z'; c++) result.Add(new(c + ":", !used.Contains(c + ":")));
        return result;
    }

    /// <summary>
    /// Whether a candidate is a UNC path (<c>\\server\share</c>) rather than a local directory.
    /// </summary>
    /// <remarks>
    /// Worth its own predicate because a UNC path is <b>not</b> a directory mount with a different spelling:
    /// rclone hands it to Windows, which allocates a drive letter, so the two cases validate differently and
    /// the caller has to expect a mount point that differs from the one it asked for.
    /// </remarks>
    public static bool IsNetworkShare(string mountPoint) =>
        mountPoint.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>
    /// Verify a proposed mount point is well-formed and the target is free. We never auto-create the
    /// directory — the user must select an existing empty directory.
    /// </summary>
    public static MountPointValidation Validate(string mountPoint, MountTargetKind kind, IReadOnlyCollection<string> occupiedMountPoints)
    {
        if (string.IsNullOrWhiteSpace(mountPoint)) return MountPointValidation.Fail("请输入挂载点。");
        if (kind == MountTargetKind.DriveLetter)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(mountPoint, "^[A-Za-z]:$"))
                return MountPointValidation.Fail("盘符必须形如 X:。");
            var letter = mountPoint.ToUpperInvariant();
            if (occupiedMountPoints.Any(x => x.Equals(letter, StringComparison.OrdinalIgnoreCase)))
                return MountPointValidation.Fail($"盘符 {letter} 已被现有挂载占用。");
            var available = EnumerateDriveLetters();
            var slot = available.FirstOrDefault(x => x.Letter.Equals(letter, StringComparison.OrdinalIgnoreCase));
            if (slot is null) return MountPointValidation.Fail("无法枚举盘符。");
            if (!slot.IsAvailable) return MountPointValidation.Fail($"盘符 {letter} 已被 Windows 使用。");
            return MountPointValidation.Ok();
        }

        // A UNC mount point is validated on its shape alone. Nothing local exists to check: rclone accepts a
        // share that is not reachable and still mounts (measured), and the drive letter it ends up using is
        // chosen by Windows, so "is this path free" is not a question this side can answer.
        if (kind == MountTargetKind.NetworkShare || IsNetworkShare(mountPoint))
            return ValidateNetworkShare(mountPoint);

        // Directory mount: target must NOT exist (rclone mount requires it empty/missing), parent MUST exist.
        try
        {
            if (!Path.IsPathRooted(mountPoint)) return MountPointValidation.Fail("目录挂载点必须是绝对路径。");
            var full = Path.GetFullPath(mountPoint);
            if (Directory.Exists(full) || File.Exists(full))
                return MountPointValidation.Fail($"目标路径已存在：{full}");
            var parent = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                return MountPointValidation.Fail($"父目录不存在：{parent}");
            if (occupiedMountPoints.Any(x => x.Equals(full, StringComparison.OrdinalIgnoreCase)))
                return MountPointValidation.Fail($"目录挂载点已被现有挂载占用：{full}");
            return MountPointValidation.Ok();
        }
        catch (Exception ex) { return MountPointValidation.Fail("路径无效：" + ex.Message); }
    }

    /// <summary>
    /// Shape check for <c>\\server\share</c>, optionally with a subdirectory. The server and share must both
    /// be present; a trailing separator is fine because rclone normalises it.
    /// </summary>
    private static MountPointValidation ValidateNetworkShare(string mountPoint)
    {
        var trimmed = mountPoint.Trim();
        if (!trimmed.StartsWith(@"\\", StringComparison.Ordinal))
            return MountPointValidation.Fail(@"网络共享挂载点必须形如 \\服务器\共享名。");
        var parts = trimmed[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return MountPointValidation.Fail(@"网络共享挂载点必须同时给出服务器与共享名，例如 \\server\share。");
        if (parts.Any(part => part.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 || part.Any(char.IsWhiteSpace)))
            return MountPointValidation.Fail("服务器名或共享名含有 Windows 不接受的字符。");
        if (parts[0] is "." or ".." || parts.Any(part => part is "." or ".."))
            return MountPointValidation.Fail("网络共享挂载点不能包含 . 或 .. 段。");
        return MountPointValidation.Ok();
    }
}

public sealed record MountPointValidation(bool IsValid, string? Error)
{
    public static MountPointValidation Ok() => new(true, null);
    public static MountPointValidation Fail(string error) => new(false, error);
}