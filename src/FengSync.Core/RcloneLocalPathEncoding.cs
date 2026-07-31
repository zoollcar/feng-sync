namespace FengSync.Core;

/// <summary>
/// Translates Windows' full-width stand-ins for forbidden filename characters
/// to rclone's logical local-backend path spelling.  rclone lists a physical
/// <c>：</c> as <c>:</c>; sending the physical spelling back to its RC API
/// makes it look for a different object and produces a misleading 404.
/// </summary>
internal static class RcloneLocalPathEncoding
{
    public static string ToRclonePath(string relativePath) => relativePath.Replace('：', ':');
}
