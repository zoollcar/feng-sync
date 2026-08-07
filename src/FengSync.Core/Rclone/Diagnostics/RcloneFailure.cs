using System.Text.Json;
using System.Text.RegularExpressions;

namespace FengSync.Core.Rclone.Diagnostics;

public enum RcloneFailureCategory
{
    Unknown, Proxy, Dns, Authentication, Permission, NotFound, Quota, RateLimit,
    Temporary, Configuration, Mount, Cancelled
}

public sealed record RcloneRcErrorResponse(string? Error, JsonElement Input, int? Status, string? Path);

public sealed record RcloneFailure(RcloneFailureCategory Category, bool Retryable, string Operation,
    int? HttpStatus, int? RcStatus, string UserMessage, string SuggestedAction, string TechnicalMessage,
    string SanitizedInput, string CorrelationId);

public sealed class RcloneException : Exception
{
    public RcloneException(RcloneFailure failure, Exception? innerException = null)
        : base(failure.UserMessage, innerException) => Failure = failure;
    public RcloneFailure Failure { get; }
}

public static class RcloneFailureClassifier
{
    private static readonly string[] SensitiveKeys =
    ["pass", "password", "secret", "token", "authorization", "credential", "client_secret", "state"];

    public static RcloneFailure FromRc(string operation, int httpStatus, string body)
    {
        RcloneRcErrorResponse? error = null;
        try { error = JsonSerializer.Deserialize<RcloneRcErrorResponse>(body, JsonOptions); } catch (JsonException) { }
        var technical = RedactText(error?.Error ?? body);
        return Create(operation, httpStatus, error?.Status, technical,
            error is null ? "{}" : Redact(error.Input));
    }

    public static RcloneFailure FromTransport(string operation, Exception exception) =>
        Create(operation, null, null, RedactText(exception is TimeoutException
            ? "timeout: " + exception.Message : exception.Message), "{}");

    public static RcloneFailure FromJob(string operation, string error) => Create(operation, 200, null, error, "{}");

    private static RcloneFailure Create(string operation, int? httpStatus, int? rcStatus,
        string technical, string input)
    {
        var value = technical.ToLowerInvariant();
        var category = value.Contains("proxy") || value.Contains("connection refused") ? RcloneFailureCategory.Proxy :
            value.Contains("no such host") || value.Contains("dns") || value.Contains("name resolution") ? RcloneFailureCategory.Dns :
            value.Contains("unauthorized") || value.Contains("invalid_grant") || value.Contains("token") || httpStatus == 401 ? RcloneFailureCategory.Authentication :
            value.Contains("forbidden") || value.Contains("permission") || httpStatus == 403 ? RcloneFailureCategory.Permission :
            value.Contains("not found") || value.Contains("directory not found") || httpStatus == 404 ? RcloneFailureCategory.NotFound :
            value.Contains("quota") || value.Contains("storagequota") ? RcloneFailureCategory.Quota :
            value.Contains("rate limit") || value.Contains("too many requests") || httpStatus == 429 ? RcloneFailureCategory.RateLimit :
            value.Contains("cancel") ? RcloneFailureCategory.Cancelled :
            value.Contains("config") || value.Contains("didn't find section") ? RcloneFailureCategory.Configuration :
            httpStatus >= 500 || value.Contains("timeout") || value.Contains("temporar") || value.Contains("connection reset") ? RcloneFailureCategory.Temporary :
            RcloneFailureCategory.Unknown;
        var retryable = category is RcloneFailureCategory.Proxy or RcloneFailureCategory.Dns or
            RcloneFailureCategory.RateLimit or RcloneFailureCategory.Temporary;
        var (message, action) = Describe(category, operation);
        return new(category, retryable, operation, httpStatus, rcStatus, message, action,
            technical, input, Guid.NewGuid().ToString("N"));
    }

    private static (string, string) Describe(RcloneFailureCategory category, string operation) => category switch
    {
        RcloneFailureCategory.Proxy => ("rclone 无法通过代理连接到云端。", "检查应用代理、Windows 代理及代理端口后重试。"),
        RcloneFailureCategory.Dns => ("rclone 无法解析云服务地址。", "检查 DNS、代理或网络连接后重试。"),
        RcloneFailureCategory.Authentication => ("云端登录已失效或凭据不正确。", "请重新授权或更新远程端点凭据。"),
        RcloneFailureCategory.Permission => ("云端拒绝了此操作。", "检查目标目录权限和远程端点授权范围。"),
        RcloneFailureCategory.NotFound => ("云端文件或目录已不存在。", "刷新比较结果后重试。"),
        RcloneFailureCategory.Quota => ("云端存储配额不足。", "释放空间或调整云端配额后重试。"),
        RcloneFailureCategory.RateLimit => ("云服务请求过于频繁。", "稍后重试；应用会将此错误标记为可重试。"),
        RcloneFailureCategory.Cancelled => ("rclone 操作已取消。", "如需继续，请重新运行。"),
        RcloneFailureCategory.Configuration => ("rclone 远程端点配置无效。", "检查远程端点配置后重试。"),
        RcloneFailureCategory.Temporary => ("rclone 与云端的连接暂时中断。", "检查网络后重试。"),
        _ => ($"rclone 操作失败：{operation}。", "查看诊断详情并重试。")
    };

    public static string Redact(JsonElement input)
    {
        object? Sanitize(JsonElement element, string? key = null)
        {
            if (key is not null && SensitiveKeys.Any(x => key.Contains(x, StringComparison.OrdinalIgnoreCase))) return "***";
            return element.ValueKind switch
            {
                JsonValueKind.Object => element.EnumerateObject().ToDictionary(x => x.Name, x => Sanitize(x.Value, x.Name)),
                JsonValueKind.Array => element.EnumerateArray().Select(x => Sanitize(x)).ToArray(),
                JsonValueKind.String => RedactText(element.GetString() ?? ""),
                JsonValueKind.Number => element.TryGetInt64(out var number) ? number : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }
        return JsonSerializer.Serialize(Sanitize(input));
    }

    public static string RedactText(string value) => SensitiveValuePattern.Replace(value, "$1***");

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex SensitiveValuePattern = new(
        @"(?i)(\b(?:pass(?:word)?|secret|token|authorization|credential|client_secret|state)\b\s*[=:]\s*|[?&](?:access_token|refresh_token|code|state)=)[^\s&,}\""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
