namespace FengSync.Core.Configuration;

public static class ConfigurationValidator
{
    public static IReadOnlyList<string> Validate(ApplicationSettings settings)
    {
        var errors = new List<string>();
        if (settings.DefaultMaxConcurrentCopies is < 1 or > 64) errors.Add("默认并发数必须介于 1 和 64 之间。");
        if (settings.DefaultResumeMinimumBytes is < 0 or > 1L << 50) errors.Add("续传最小文件大小必须介于 0 和 1 PB 之间（0 表示所有文件都续传）。");
        if (settings.DefaultTimeToleranceSeconds is < 0 or > 86400) errors.Add("默认时间容差必须介于 0 和 86400 秒之间。");
        if (settings.DefaultVersioning.KeepDays < 1) errors.Add("版本保留天数必须至少为 1 天。");
        if (settings.LogRetentionDays is < 1 or > 3650) errors.Add("日志保留天数必须介于 1 和 3650 天之间。");
        if (settings.NetworkRetryCount is < 0 or > 20) errors.Add("网络重试次数必须介于 0 和 20 之间。");
        if (settings.CloudMaxConcurrentTransfers is < 1 or > 16) errors.Add("云盘同时传输数必须介于 1 和 16 之间。");
        if (settings.CloudRetryCount is < 0 or > 10) errors.Add("云盘自动重试次数必须介于 0 和 10 之间。");
        if (!Enum.IsDefined(settings.CloudBrowserView)) errors.Add("文件视图设置无效。");
        if (!TryValidateProxy(settings.RcloneProxy, out var proxyError)) errors.Add(proxyError);
        if (double.IsNaN(settings.MainWindowSidebarWidth) || double.IsInfinity(settings.MainWindowSidebarWidth) || settings.MainWindowSidebarWidth is < 220 or > 320) errors.Add("主窗口侧栏宽度必须介于 220 和 320 之间。");
        if (!string.IsNullOrWhiteSpace(settings.SkippedUpdateVersion) && !Updates.ReleaseVersion.TryParse(settings.SkippedUpdateVersion, out _)) errors.Add("忽略的更新版本无效。");
        return errors;
    }

    /// <summary>
    /// Accepts an empty value, or a host:port / scheme://host:port address. The scheme is deliberately not
    /// policed: rclone's HTTP client hands anything that is not <c>socks5</c>/<c>socks5h</c> to its HTTP
    /// proxy path, so rejecting an unusual scheme here would refuse an address the child would happily use.
    /// What is rejected is input that cannot become an address at all — an embedded space, or no host.
    /// </summary>
    private static bool TryValidateProxy(string? value, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(value)) return true;
        var text = value.Trim();
        if (text.Any(char.IsWhiteSpace))
        {
            error = "代理服务器地址不能包含空格。";
            return false;
        }
        var candidate = text.Contains("://", StringComparison.Ordinal) ? text : "http://" + text;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            error = "代理服务器地址无效，请填写 host:port（如 proxy.corp:8080）或带协议的完整地址。";
            return false;
        }
        return true;
    }
}
