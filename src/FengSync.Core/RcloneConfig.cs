using System.Text.Json;

namespace FengSync.Core;

/// <summary>Reads the JSON emitted by <c>rclone config dump</c>.  Do not use <c>config show --json</c>: that flag is not supported by rclone.</summary>
public sealed record RcloneAccount(string Name, string Type)
{
    public bool IsGoogleDrive => Type.Equals("drive", StringComparison.OrdinalIgnoreCase);
    public bool IsS3 => Type.Equals("s3", StringComparison.OrdinalIgnoreCase);
    /// <summary>Human-friendly backend label used by the cloud-endpoint management UI.</summary>
    public string Provider => IsGoogleDrive ? "Google Drive" : IsS3 ? "S3 Bucket" : "SFTP";
    /// <summary>List display combining provider and remote name.</summary>
    public string Display => $"{Provider}  ·  {Name}";
}
public static class RcloneConfig
{
    public static IReadOnlyList<RcloneAccount> ParseDump(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
        return document.RootElement.EnumerateObject()
            .Select(x => new RcloneAccount(x.Name, x.Value.ValueKind == JsonValueKind.Object && x.Value.TryGetProperty("type", out var type) ? type.GetString() ?? "unknown" : "unknown"))
            .Where(x => x.Type is "drive" or "sftp" or "s3")
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
