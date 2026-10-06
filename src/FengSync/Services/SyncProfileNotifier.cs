using FengSync.Core;

namespace FengSync.Services;

/// <summary>
/// 「有一个 Profile 存好了」的进程内广播。E1 让云盘管理窗能直接建同步任务，于是同一份
/// <c>profiles.json</c> 有了第二个写者，而主窗口把 Profile 列表读进了内存并显示在
/// <c>ProfileList</c> 上 —— 只写文件不通知，等于让用户建完任务回到主窗口却看不到它。
/// </summary>
/// <remarks>
/// 做成静态事件而不是让云盘管理窗去 <c>Application.Current</c> 上找主窗口：那条 cast 正是
/// <c>AppServices</c> 存在的原因（两个外壳共用这些窗），而 DriveHub 这个外壳**根本没有**主窗口。
/// 没人订阅就只是一次没人听的通知，两种情况都不需要这一层做判断。
/// </remarks>
public static class SyncProfileNotifier
{
    /// <summary>Raised after a profile reached <c>profiles.json</c>, carrying the profile that was saved.</summary>
    public static event Action<SyncProfile>? Saved;

    internal static void RaiseSaved(SyncProfile profile) => Saved?.Invoke(profile);
}
