namespace FengSync.Core.Automation;

/// <summary>Serializes file-watcher requests: changes during a run are coalesced into one follow-up run,
/// while the post-run cooldown suppresses the sync's own write notifications.</summary>
public sealed class RealtimeRunCoordinator : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> _run;
    private readonly TimeSpan _cooldown;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private bool _running, _pending, _disposed;
    private DateTimeOffset _completedUtc = DateTimeOffset.MinValue;
    public event Action<string>? StatusChanged;

    public RealtimeRunCoordinator(Func<CancellationToken, Task> run, TimeSpan? cooldown = null)
    {
        _run = run; _cooldown = cooldown ?? TimeSpan.FromSeconds(5);
    }

    public void NotifyChanged()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_running) { _pending = true; StatusChanged?.Invoke("运行中检测到变更，已排队一次后续同步。"); return; }
            if (DateTimeOffset.UtcNow - _completedUtc < _cooldown) { StatusChanged?.Invoke("已抑制同步自身产生的循环变更。"); return; }
            _running = true;
        }
        _ = DrainAsync();
    }

    private async Task DrainAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { StatusChanged?.Invoke("正在执行实时同步…"); await _run(_stop.Token).ConfigureAwait(false); StatusChanged?.Invoke("实时同步完成。"); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) { StatusChanged?.Invoke("实时同步失败：" + ex.Message); }
            lock (_gate)
            {
                _completedUtc = DateTimeOffset.UtcNow;
                if (_pending) { _pending = false; continue; }
                _running = false; return;
            }
        }
        lock (_gate) _running = false;
    }

    public ValueTask DisposeAsync() { _disposed = true; _stop.Cancel(); _stop.Dispose(); return ValueTask.CompletedTask; }
}
