namespace FengSync.Core.Configuration;

public static class ConfigurationValidator
{
    public static IReadOnlyList<string> Validate(ApplicationSettings settings)
    {
        var errors = new List<string>();
        if (settings.DefaultMaxConcurrentCopies is < 1 or > 64) errors.Add("默认并发数必须介于 1 和 64 之间。");
        if (settings.DefaultTimeToleranceSeconds is < 0 or > 86400) errors.Add("默认时间容差必须介于 0 和 86400 秒之间。");
        if (settings.DefaultVersioning.KeepDays < 1) errors.Add("版本保留天数必须至少为 1 天。");
        if (settings.LogRetentionDays is < 1 or > 3650) errors.Add("日志保留天数必须介于 1 和 3650 天之间。");
        if (settings.NetworkRetryCount is < 0 or > 20) errors.Add("网络重试次数必须介于 0 和 20 之间。");
        return errors;
    }
}
