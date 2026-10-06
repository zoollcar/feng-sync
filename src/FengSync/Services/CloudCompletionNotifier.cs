using System.ComponentModel;
using FengSync.Localization;

namespace FengSync.Services;

/// <summary>
/// Turns a drained <see cref="CloudTransferQueue"/> into one sentence for the user, when the user asked to
/// be told. Kept apart from any window so the phrasing and the on/off rule can be tested without a desktop,
/// and deliberately limited to an in-app message: this application ships no tray icon, and a topmost popup
/// would be exactly the kind of thing that steals focus from whatever the user is doing.
/// </summary>
public sealed class CloudCompletionNotifier : IDisposable
{
    private readonly CloudTransferQueue _queue;
    private bool _disposed;

    public CloudCompletionNotifier(CloudTransferQueue queue)
    {
        _queue = queue;
        _queue.BatchCompleted += Queue_BatchCompleted;
    }

    /// <summary>Mirrors <see cref="Core.Configuration.ApplicationSettings.CloudNotifyOnCompletion"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The most recent message that was actually reported, or <see langword="null"/> if none was.</summary>
    public string? LastMessage { get; private set; }

    /// <summary>Raised on the queue's thread when a batch drains and a message is worth showing.</summary>
    public event EventHandler<string>? Notified;

    private void Queue_BatchCompleted(object? sender, CloudTransferBatch batch) => Report(batch);

    /// <summary>
    /// Reports a batch. Separate from the event handler so a test can drive the decision — including the
    /// <see cref="Enabled"/> gate — without running a real transfer through rclone.
    /// </summary>
    internal void Report(CloudTransferBatch batch)
    {
        if (!Enabled) return;
        var message = CloudTransferSummary.Describe(batch);
        if (message.Length == 0) return;
        LastMessage = message;
        Notified?.Invoke(this, message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.BatchCompleted -= Queue_BatchCompleted;
    }
}

/// <summary>
/// The wording rules for a finished batch, split out because they are pure and therefore the cheapest thing
/// in this feature to pin down with a test.
/// </summary>
public static class CloudTransferSummary
{
    /// <summary>
    /// One sentence, or the empty string when there is nothing worth saying. A batch the user stopped
    /// themselves — everything cancelled or paused — reports nothing: they already know, and a notification
    /// that fires on their own action is noise. A batch that ended with nothing finished and nothing failed
    /// (for example every task was cleared before it ran) is treated the same way.
    /// </summary>
    public static string Describe(CloudTransferBatch batch)
    {
        if (batch.Total <= 0) return "";
        if (batch.Completed == batch.Total) return Tr.Format("Notify.BatchAllDone", batch.Total);
        if (batch.Completed == 0 && batch.Failed == 0) return "";
        var parts = new List<string>(3);
        if (batch.Completed > 0) parts.Add(Tr.Format("Notify.Succeeded", batch.Completed));
        if (batch.Failed > 0) parts.Add(Tr.Format("Notify.Failed", batch.Failed));
        if (batch.Stopped > 0) parts.Add(Tr.Format("Notify.Stopped", batch.Stopped));
        return Tr.Format("Notify.BatchSummary", string.Join(Tr.F("Notify.ListSeparator"), parts));
    }
}
