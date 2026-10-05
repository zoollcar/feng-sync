using FengSync.Core.Configuration;

namespace FengSync.Services;

/// <summary>
/// Pushes the settings that govern the transfer queue onto it.
/// </summary>
/// <remarks>
/// One place rather than two, because the queue is application-owned and both the browser window and the
/// transfer window reach it: if each applied the settings itself, whichever loaded last would win and the
/// difference would show up as a limit that sometimes applies. The one setting deliberately absent is
/// 传输完成后动作 — that one belongs to the window that has to confirm it.
/// </remarks>
public static class CloudTransferPolicy
{
    public static void Apply(CloudTransferQueue queue, ApplicationSettings settings)
    {
        queue.Concurrency = settings.CloudMaxConcurrentTransfers;
        queue.MaxRetries = settings.CloudRetryCount;
        queue.Limits = new CloudTransferLimits(settings.CloudUploadLimitBytesPerSecond, settings.CloudDownloadLimitBytesPerSecond);
        queue.TimeWindow = settings.CloudTransferTimeWindow;
    }
}
