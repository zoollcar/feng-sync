using System.Diagnostics;
using System.Windows;
using FengSync.Core.Configuration;
using FengSync.Localization;

namespace FengSync.Services;

/// <summary>
/// Runs the "when the transfers finish, do this to my computer" choice.
/// </summary>
/// <remarks>
/// <para>
/// It only ever fires for a batch in which <b>everything</b> succeeded. Shutting a machine down after a batch
/// that half failed would take away the only chance to look at what went wrong, so a partial batch reports and
/// stops there — the rule is worth more than the convenience.
/// </para>
/// <para>
/// It asks before acting, every time. The three actions all take the machine away from the user, and this is
/// the last moment at which that is still reversible.
/// </para>
/// <para>
/// Owned by the transfer window rather than by the application, and that is deliberate: the confirmation has to
/// be answerable by somebody, and an application-owned action firing while no window is open would either block
/// forever or — worse — proceed unconfirmed. The settings page says so out loud.
/// </para>
/// </remarks>
public sealed class CloudPostTransferAction : IDisposable
{
    private readonly CloudTransferQueue _queue;
    private bool _disposed;

    public CloudPostTransferAction(CloudTransferQueue queue)
    {
        _queue = queue;
        _queue.BatchCompleted += Queue_BatchCompleted;
    }

    /// <summary>Mirrors <see cref="ApplicationSettings.CloudAfterTransfer"/>.</summary>
    public CloudAfterTransferAction Action { get; set; } = CloudAfterTransferAction.None;

    /// <summary>
    /// Asks the user whether to go ahead. A seam, and the reason the action needs a live window: with no
    /// confirmation available nothing happens at all rather than the action running unasked.
    /// </summary>
    internal Func<CloudAfterTransferAction, bool>? Confirm { get; set; }

    /// <summary>Performs the action. A seam, so a test can assert the choice without shutting anything down.</summary>
    internal Action<CloudAfterTransferAction>? Perform { get; set; }

    /// <summary>The most recent thing that was reported, or <see langword="null"/> if nothing was.</summary>
    public string? LastNotice { get; private set; }

    /// <summary>Raised on the queue's thread when there is something to say.</summary>
    public event EventHandler<string>? Noticed;

    private void Queue_BatchCompleted(object? sender, CloudTransferBatch batch) => Report(batch);

    /// <summary>
    /// Decides what to do with a drained batch. Separate from the event handler so the decision — including the
    /// "everything succeeded" gate — can be driven without running a transfer.
    /// </summary>
    internal void Report(CloudTransferBatch batch)
    {
        if (Action == CloudAfterTransferAction.None) return;
        if (batch.Total <= 0) return;
        if (batch.Completed != batch.Total)
        {
            Announce(Tr.Format("AfterTransfer.Skipped", batch.Total - batch.Completed, Describe(Action)));
            return;
        }
        if (Confirm is not { } confirm) return;
        if (!confirm(Action)) { Announce(Tr.Format("AfterTransfer.Cancelled", Describe(Action))); return; }
        (Perform ?? PerformDefault)(Action);
    }

    private void Announce(string message)
    {
        LastNotice = message;
        Noticed?.Invoke(this, message);
    }

    /// <summary>What the choice is called, in the words the settings page and the prompt use.</summary>
    public static string Describe(CloudAfterTransferAction action) => action switch
    {
        CloudAfterTransferAction.Exit => Tr.F("AfterTransfer.DescribeExit"),
        CloudAfterTransferAction.Sleep => Tr.F("AfterTransfer.DescribeSleep"),
        CloudAfterTransferAction.Shutdown => Tr.F("AfterTransfer.DescribeShutdown"),
        _ => Tr.F("AfterTransfer.DescribeNone")
    };

    /// <summary>
    /// The question asked before the machine is taken away. Names the action, and for the two that affect the
    /// whole machine also names how to stop it — a shutdown that can still be called off is a very different
    /// proposition from one that cannot.
    /// </summary>
    public static string ConfirmationFor(CloudAfterTransferAction action) => action switch
    {
        CloudAfterTransferAction.Exit => Tr.F("AfterTransfer.ConfirmExit"),
        CloudAfterTransferAction.Sleep => Tr.F("AfterTransfer.ConfirmSleep"),
        _ => Tr.F("AfterTransfer.ConfirmShutdown")
    };

    private static void PerformDefault(CloudAfterTransferAction action)
    {
        try
        {
            switch (action)
            {
                case CloudAfterTransferAction.Exit:
                    Application.Current?.Shutdown();
                    break;
                case CloudAfterTransferAction.Sleep:
                    Process.Start(new ProcessStartInfo("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0") { UseShellExecute = false });
                    break;
                case CloudAfterTransferAction.Shutdown:
                    // A minute of grace rather than an immediate halt: the user may have queued this and walked
                    // away, and "shutdown /a" only helps if there is time to type it.
                    Process.Start(new ProcessStartInfo("shutdown", "/s /t 60") { UseShellExecute = false });
                    break;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError("Unable to perform the post-transfer action {0}: {1}", action, ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.BatchCompleted -= Queue_BatchCompleted;
    }
}
