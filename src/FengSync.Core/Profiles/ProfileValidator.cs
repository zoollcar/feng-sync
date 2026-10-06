using FengSync.Core.Configuration;

namespace FengSync.Core;

/// <summary>
/// One refusal, and which part of the profile it is about. <see cref="Field"/> is a stable id, never a
/// sentence: the shell picks which editor page to open by reading it, and a shell that had to look for 端点
/// inside the message would stop working the moment the message is translated.
/// </summary>
public sealed record ProfileValidationError(ProfileValidationField Field, string Message);

public enum ProfileValidationField
{
    /// <summary>The profile's own name and description.</summary>
    Identity,
    /// <summary>The left/right endpoints.</summary>
    Endpoints,
    /// <summary>Versioning and archiving.</summary>
    Versioning
}

public sealed record ProfileValidationResult(IReadOnlyList<ProfileValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;

    /// <summary>Just the sentences, for a caller that only wants to show them.</summary>
    public IReadOnlyList<string> Messages => [.. Errors.Select(error => error.Message)];
}

public static class ProfileValidator
{
    public static ProfileValidationResult Validate(SyncProfile profile)
    {
        var errors = new List<ProfileValidationError>();
        if (string.IsNullOrWhiteSpace(profile.Name)) errors.Add(new(ProfileValidationField.Identity, "Profile 名称不能为空。"));
        if (string.IsNullOrWhiteSpace(profile.LeftPath) || string.IsNullOrWhiteSpace(profile.RightPath)) errors.Add(new(ProfileValidationField.Endpoints, "必须填写左右端点。"));
        ValidateEndpoint(profile.LeftPath, "左侧", errors);
        ValidateEndpoint(profile.RightPath, "右侧", errors);
        if (!string.IsNullOrWhiteSpace(profile.LeftPath) && string.Equals(profile.LeftPath.TrimEnd('\\', '/'), profile.RightPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) errors.Add(new(ProfileValidationField.Endpoints, "左右端点不能是同一目录。"));
        var versioning = profile.Settings?.Versioning ?? profile.Versioning;
        if (versioning?.Mode == VersioningMode.TimestampedArchive)
        {
            if (string.IsNullOrWhiteSpace(versioning.ArchiveDirectory)) errors.Add(new(ProfileValidationField.Versioning, "版本管理需要归档目录。"));
            else if (IsNested(versioning.ArchiveDirectory, profile.LeftPath) || IsNested(versioning.ArchiveDirectory, profile.RightPath)) errors.Add(new(ProfileValidationField.Versioning, "归档目录不能嵌套在同步端点内。"));
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
    private static void ValidateEndpoint(string endpoint, string side, ICollection<ProfileValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || !endpoint.Contains("://", StringComparison.Ordinal)) return;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            errors.Add(new(ProfileValidationField.Endpoints, $"{side}端点地址或端口无效。"));
            return;
        }
        if (!uri.Scheme.Equals("sftp", StringComparison.OrdinalIgnoreCase) && !uri.Scheme.Equals("gdrive", StringComparison.OrdinalIgnoreCase) && !uri.Scheme.Equals("s3", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new(ProfileValidationField.Endpoints, $"{side}端点协议不受支持；目前仅支持 sftp://、gdrive:// 和 s3://。"));
            return;
        }
        if (uri.Port is 0 or > 65535) errors.Add(new(ProfileValidationField.Endpoints, $"{side}端点端口必须介于 1 和 65535 之间。"));
    }
}
