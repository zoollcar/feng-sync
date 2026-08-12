namespace FengSync.Core.Capabilities;

/// <summary>One compatibility policy shared by UI, batch and runner.</summary>
public sealed class FeatureCapabilityService
{
    public ProfileCompatibilityResult Evaluate(SyncProfile profile)
    {
        var blockers = new List<string>(); var warnings = new List<string>();
        var remote = IsRemote(profile.LeftPath) || IsRemote(profile.RightPath);
        if (profile.Mode == SyncMode.Custom) blockers.Add("自定义同步模式尚未实现。");
        var versioning = profile.Settings?.Versioning ?? profile.Versioning;
        if (versioning?.Mode == VersioningMode.RecycleBin && (!OperatingSystem.IsWindows() || remote))
            blockers.Add("回收站仅支持 Windows 本地端点；远端端点不能安全使用此策略。");
        if (profile.MaxDeletes < 0) blockers.Add("最大删除数量不能小于 0。");
        if (profile.MaxDeleteRatio is < 0 or > 1) blockers.Add("最大删除比例必须介于 0 和 1 之间。");
        if (!profile.Enabled) warnings.Add("Profile 已禁用。");
        return new(blockers, warnings);
    }
    public static bool IsRemote(string path) => path.Contains("://", StringComparison.Ordinal);
}
