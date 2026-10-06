using System.IO;
using FengSync.Core;
using FengSync.Localization;

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
            new(CloudTransferRowCommand.MoveUp, Tr.F("Transfer.MoveUp"), context.CanMoveUp,
                context.CanMoveUp ? null : Tr.F("Transfer.ReorderOnlyPending")),
            new(CloudTransferRowCommand.MoveDown, Tr.F("Transfer.MoveDown"), context.CanMoveDown,
                context.CanMoveDown ? null : Tr.F("Transfer.ReorderOnlyPending")),
            new(CloudTransferRowCommand.OpenFolder, Tr.F("Transfer.OpenFolder"), folder is not null, OpenFolderHint(task, folder), SeparatorBefore: true),
            new(CloudTransferRowCommand.CopyLink, Tr.F("Transfer.CopyLink"), shareable, shareable ? null : LinkHint(task)),
            new(CloudTransferRowCommand.CopyDetail, Tr.F("Transfer.CopyDetail"), CloudTransferDiagnostics.OffersDetail(task),
                CloudTransferDiagnostics.OffersDetail(task) ? null : Tr.F("Transfer.NoErrorInfo"), SeparatorBefore: true),
            new(CloudTransferRowCommand.ViewLog, Tr.F("Transfer.ViewLog"), true)
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
                ? Tr.F("Transfer.CopyNoLocalFolder")
                : Tr.F("Transfer.NoLocalPath");

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
        CloudTransferState.Pending => Tr.F("Transfer.LinkPending"),
        CloudTransferState.Running => Tr.F("Transfer.LinkRunning"),
        _ => Tr.F("Transfer.LinkUnavailable")
    };
}
