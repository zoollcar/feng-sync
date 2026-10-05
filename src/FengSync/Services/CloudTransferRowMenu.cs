using System.IO;
using FengSync.Core;

namespace FengSync.Services;

/// <summary>What a transfer row's menu can do.</summary>
public enum CloudTransferRowCommand
{
    /// <summary>Move a waiting transfer one place earlier.</summary>
    MoveUp,
    /// <summary>Move a waiting transfer one place later.</summary>
    MoveDown,
    /// <summary>Show the task's local folder in Explorer.</summary>
    OpenFolder,
    /// <summary>Ask the cloud for a public link to the task's remote object and copy it.</summary>
    CopyLink,
    /// <summary>Copy the full diagnostic to the clipboard.</summary>
    CopyDetail,
    /// <summary>Open the day's rclone failure log.</summary>
    ViewLog
}

/// <summary>One menu entry: what it does, what it says, and why it is unavailable when it is.</summary>
public sealed record CloudTransferRowMenuItem(
    CloudTransferRowCommand Command,
    string Label,
    bool Enabled,
    string? Hint = null,
    bool SeparatorBefore = false);

/// <summary>What the window knows about the queue that the row itself does not.</summary>
public sealed record CloudTransferRowContext(bool CanMoveUp = false, bool CanMoveDown = false);

/// <summary>
/// The transfer row's menu, as rules rather than as event handlers. Every entry is either available with a
/// reason or unavailable with a reason: a disabled item with no explanation is the thing users report as "the
/// button does nothing".
/// </summary>
/// <remarks>
/// 复制链接 deliberately reuses <see cref="CloudShareLinks"/> and the <c>PublicLinkAsync</c> call B4 already
/// built, and 打开所在文件夹 opens the folder Explorer would open for the same file — the point of this menu is
/// to reach the things the user already has elsewhere without inventing a second implementation of any of them.
/// </remarks>
public static class CloudTransferRowMenu
{
    public static IReadOnlyList<CloudTransferRowMenuItem> Build(CloudTransferTask task, CloudTransferRowContext context)
    {
        var folder = LocalFolderOf(task);
        var shareable = OffersLink(task);
        return
        [
            new(CloudTransferRowCommand.MoveUp, "上移", context.CanMoveUp,
                context.CanMoveUp ? null : "只有等待中的传输可以调整顺序。"),
            new(CloudTransferRowCommand.MoveDown, "下移", context.CanMoveDown,
                context.CanMoveDown ? null : "只有等待中的传输可以调整顺序。"),
            new(CloudTransferRowCommand.OpenFolder, "打开所在文件夹", folder is not null, OpenFolderHint(task, folder), SeparatorBefore: true),
            new(CloudTransferRowCommand.CopyLink, "复制分享链接…", shareable, shareable ? null : LinkHint(task)),
            new(CloudTransferRowCommand.CopyDetail, "复制详情", CloudTransferDiagnostics.OffersDetail(task),
                CloudTransferDiagnostics.OffersDetail(task) ? null : "这个任务还没有错误信息。", SeparatorBefore: true),
            new(CloudTransferRowCommand.ViewLog, "查看日志", true)
        ];
    }

    /// <summary>
    /// The local folder this task can be shown in. A remote→remote copy has none — its bytes never touch this
    /// machine — and saying so is the honest answer rather than opening the cloud manager at a guess.
    /// </summary>
    public static string? LocalFolderOf(CloudTransferTask task)
    {
        if (task.Direction == CloudTransferDirection.Copy) return null;
        if (string.IsNullOrWhiteSpace(task.LocalPath)) return null;
        var folder = Path.GetDirectoryName(task.LocalPath);
        return string.IsNullOrWhiteSpace(folder) ? null : folder;
    }

    /// <summary>The reason 打开所在文件夹 is unavailable, or <see langword="null"/> when it is available.</summary>
    private static string? OpenFolderHint(CloudTransferTask task, string? folder) =>
        folder is not null
            ? null
            : task.Direction == CloudTransferDirection.Copy
                ? "复制任务没有本地文件夹：字节由 rclone 在两端之间移动，本机不落盘。"
                : "这个任务没有可打开的本地路径。";

    /// <summary>
    /// Whether asking the cloud for a link makes sense yet. A transfer that is still pending or running may have
    /// no object at the far end at all, so the question is not worth asking; once it has stopped, the backend
    /// answers for itself and B4's outcomes say what that answer means.
    /// </summary>
    public static bool OffersLink(CloudTransferTask task) =>
        task.State is CloudTransferState.Completed or CloudTransferState.Failed
            or CloudTransferState.Cancelled or CloudTransferState.Paused;

    private static string LinkHint(CloudTransferTask task) => task.State switch
    {
        CloudTransferState.Pending => "传输还没开始，云端现在可能还没有这个文件。",
        CloudTransferState.Running => "传输正在进行，等它停下来再生成链接。",
        _ => "这个任务没有可分享的云端文件。"
    };
}
