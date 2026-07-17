namespace FengSync.Core;

/// <summary>Debounced local change monitor. It never syncs on its own: callers receive a notification and may run a profile.</summary>
public sealed class RealtimeMonitor : IDisposable
{
    private readonly FileSystemWatcher _left;
    private readonly FileSystemWatcher _right;
    private readonly TimeSpan _quietPeriod;
    private readonly Action _changed;
    private readonly object _gate = new();
    private Timer? _timer;
    public RealtimeMonitor(string leftPath, string rightPath, Action changed, TimeSpan? quietPeriod = null)
    {
        _changed = changed; _quietPeriod = quietPeriod ?? TimeSpan.FromSeconds(2);
        _left = Watch(leftPath); _right = Watch(rightPath);
    }
    private FileSystemWatcher Watch(string path)
    {
        var watcher = new FileSystemWatcher(path) { IncludeSubdirectories = true, EnableRaisingEvents = true, Filter = "*" };
        watcher.Changed += OnChange; watcher.Created += OnChange; watcher.Deleted += OnChange; watcher.Renamed += OnRename;
        return watcher;
    }
    private void OnRename(object sender, RenamedEventArgs e) => Schedule();
    private void OnChange(object sender, FileSystemEventArgs e)
    {
        var name = e.Name ?? "";
        if (name.Equals("sync.fengdb", StringComparison.OrdinalIgnoreCase) || name.Contains(".fengsync-", StringComparison.OrdinalIgnoreCase)) return;
        Schedule();
    }
    private void Schedule()
    {
        lock (_gate) _timer ??= new Timer(_ => _changed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        lock (_gate) _timer.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
    }
    public void Dispose() { _left.Dispose(); _right.Dispose(); lock (_gate) { _timer?.Dispose(); _timer = null; } }
}
