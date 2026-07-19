using FengSync.Core.Configuration;

namespace FengSync.Core;

public sealed record ProfileValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static class ProfileValidator
{
    public static ProfileValidationResult Validate(SyncProfile profile)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.Name)) errors.Add("Profile 名称不能为空。");
        if (string.IsNullOrWhiteSpace(profile.LeftPath) || string.IsNullOrWhiteSpace(profile.RightPath)) errors.Add("必须填写左右端点。");
        ValidateEndpoint(profile.LeftPath, "左侧", errors);
        ValidateEndpoint(profile.RightPath, "右侧", errors);
        if (!string.IsNullOrWhiteSpace(profile.LeftPath) && string.Equals(profile.LeftPath.TrimEnd('\\', '/'), profile.RightPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) errors.Add("左右端点不能是同一目录。");
        var versioning = profile.Settings?.Versioning ?? profile.Versioning;
        if (versioning?.Mode == VersioningMode.TimestampedArchive)
        {
            if (string.IsNullOrWhiteSpace(versioning.ArchiveDirectory)) errors.Add("版本管理需要归档目录。");
            else if (IsNested(versioning.ArchiveDirectory, profile.LeftPath) || IsNested(versioning.ArchiveDirectory, profile.RightPath)) errors.Add("归档目录不能嵌套在同步端点内。");
        }
        return new(errors);
    }
    private static bool IsNested(string child, string parent)
    {
        if (child.Contains("://", StringComparison.Ordinal) || parent.Contains("://", StringComparison.Ordinal)) return false;
        try
        {
            var normalizedChild = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(normalizedChild, normalizedParent, StringComparison.OrdinalIgnoreCase)
                || normalizedChild.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }
    private static void ValidateEndpoint(string endpoint, string side, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || !endpoint.Contains("://", StringComparison.Ordinal)) return;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            errors.Add($"{side}端点地址或端口无效。");
            return;
        }
        if (!uri.Scheme.Equals("sftp", StringComparison.OrdinalIgnoreCase) && !uri.Scheme.Equals("gdrive", StringComparison.OrdinalIgnoreCase) && !uri.Scheme.Equals("s3", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{side}端点协议不受支持；目前仅支持 sftp://、gdrive:// 和 s3://。");
            return;
        }
        if (uri.Port is 0 or > 65535) errors.Add($"{side}端点端口必须介于 1 和 65535 之间。");
    }
}
