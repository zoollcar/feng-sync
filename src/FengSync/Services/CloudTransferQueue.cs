using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FengSync.Core;
using FengSync.Core.Configuration;
using FengSync.Core.Execution;
using FengSync.Core.Rclone.Diagnostics;

namespace FengSync.Services;

/// <summary>
/// What the queue is moving. <see cref="Copy"/> is a remote→remote copy: both ends are cloud endpoints, no
/// local file is involved, and it is appended last because <see cref="CloudTransferRecord"/> persists the
/// value in a journal that older files must still read.
/// </summary>
public enum CloudTransferDirection { Upload, Download, Copy }

/// <summary>
/// <see cref="Paused"/> is deliberately distinct from <see cref="Cancelled"/>: both stop a transfer, but a
/// cancelled one is the user's queue of things to continue later, while a paused one is parked on purpose
/// and is expected to be let go again.
/// </summary>
public enum CloudTransferState { Pending, Running, Paused, Completed, Cancelled, Failed }

/// <summary>
/// The outcome of one batch of transfers — everything the user started together, reported once the queue
/// has drained. Counts rather than the tasks themselves, because a host only needs to phrase a sentence.
/// </summary>
public sealed record CloudTransferBatch(int Total, int Completed, int Failed, int Stopped)
{
    /// <summary>
    /// Whether the batch stopped without finishing everything. A batch that was cancelled or paused on
    /// purpose is <see cref="Stopped"/> rather than <see cref="Failed"/>: the user knows why it stopped.
    /// </summary>
    public bool HasFailures => Failed > 0;
}

/// <summary>
/// One user-visible upload, download, or remote→remote copy of a single file. The unit is deliberately a
/// file, matching <see cref="EndpointTransfer"/>'s staging/resume contract. A download keeps its staging
/// file and can be continued from where it stopped; an upload cannot, because rclone has no way to append to
/// a partially written remote object, so a retry starts that file again. A <see cref="CloudTransferDirection.Copy"/>
/// cannot be continued either, and for a sharper reason: both legs are rclone's, so Feng Sync never holds the
/// bytes and has no seam to verify an append against.
/// </summary>
public sealed class CloudTransferTask : INotifyPropertyChanged
{
    private CloudTransferState _state = CloudTransferState.Pending;
    private long _completedBytes;
    private double _bytesPerSecond;
    private string? _error;
    private string? _errorDetail;
    private int _attempt;

    public CloudTransferTask(CloudTransferDirection direction, string remoteUri, string remotePath, string localPath, long totalBytes, string? targetUri = null, string? targetPath = null)
    {
        Direction = direction;
        RemoteUri = remoteUri;
        RemotePath = remotePath.Trim('/');
        LocalPath = localPath;
        TotalBytes = totalBytes;
        TargetUri = targetUri;
        TargetPath = targetPath?.Trim('/');
    }

    public Guid Id { get; init; } = Guid.NewGuid();
    public CloudTransferDirection Direction { get; }
    /// <summary>
    /// Remote root in Feng Sync URI form, e.g. <c>s3://archive</c>. Credentials stay in rclone.conf.
    /// For a <see cref="CloudTransferDirection.Copy"/> this is the source.
    /// </summary>
    public string RemoteUri { get; }
    /// <summary>Path relative to the remote root, e.g. <c>team/docs/report.pdf</c>.</summary>
    public string RemotePath { get; }
    public string LocalPath { get; }
    /// <summary>
    /// The destination endpoint of a <see cref="CloudTransferDirection.Copy"/>, in the same URI form as
    /// <see cref="RemoteUri"/>. It is never carried in <see cref="LocalPath"/>: a copy has no local file, and
    /// a journal entry whose "local path" is really a remote would be read by the next release as one.
    /// </summary>
    public string? TargetUri { get; }
    /// <summary>Destination path relative to <see cref="TargetUri"/>'s root; the target's own root is not repeated.</summary>
    public string? TargetPath { get; }
    public long TotalBytes { get; internal set; }

    public CloudTransferState State
    {
        get => _state;
        internal set { if (!Set(ref _state, value)) return; Raise(nameof(IsFinished)); Raise(nameof(CanCancel)); Raise(nameof(CanPause)); Raise(nameof(IsResumable)); Raise(nameof(RestartText)); Raise(nameof(StatusText)); }
    }
    public long CompletedBytes
    {
        get => _completedBytes;
        internal set { if (!Set(ref _completedBytes, value)) return; Raise(nameof(Percentage)); Raise(nameof(StatusText)); }
    }
    public double BytesPerSecond
    {
        get => _bytesPerSecond;
        internal set { if (!Set(ref _bytesPerSecond, value)) return; Raise(nameof(StatusText)); }
    }
    public string? Error
    {
        get => _error;
        internal set { if (!Set(ref _error, value)) return; Raise(nameof(StatusText)); }
    }

    /// <summary>
    /// The full diagnostic behind <see cref="Error"/>. The one-line message is what the row shows; this is what
    /// "copy details" hands to the user, and it survives a restart so a failure found later is still diagnosable.
    /// </summary>
    public string? ErrorDetail
    {
        get => _errorDetail;
        internal set { if (!Set(ref _errorDetail, value)) return; Raise(nameof(HasErrorDetail)); }
    }

    public bool HasErrorDetail => !string.IsNullOrWhiteSpace(_errorDetail);

    /// <summary>Which attempt is in flight, counting from 1, so a retry is visible instead of silent.</summary>
    public int Attempt
    {
        get => _attempt;
        internal set { if (!Set(ref _attempt, value)) return; Raise(nameof(StatusText)); }
    }

    /// <summary>
    /// Where this task sits in the queue. Lower runs first, and the value is what the row's 上移 / 下移 swap —
    /// the list order the user sees and the order the queue hands out slots are the same order, which is the
    /// only way "move this one up" can mean anything.
    /// </summary>
    public int Order
    {
        get => _order;
        internal set { if (Set(ref _order, value)) Raise(nameof(Order)); }
    }
    private int _order;

    public double Percentage => TotalBytes <= 0 ? 0 : Math.Min(100, _completedBytes * 100d / TotalBytes);
    /// <summary>A paused transfer is not finished: the user still has to decide what happens to it.</summary>
    public bool IsFinished => State is CloudTransferState.Completed or CloudTransferState.Cancelled or CloudTransferState.Failed;
    public bool CanCancel => State is CloudTransferState.Pending or CloudTransferState.Running;
    public bool CanPause => State is CloudTransferState.Pending or CloudTransferState.Running;
    /// <summary>Whether a stopped transfer can be started again — paused, cancelled, or failed alike.</summary>
    public bool IsResumable => State is CloudTransferState.Paused or CloudTransferState.Cancelled or CloudTransferState.Failed;

    /// <summary>
    /// Label for the row's restart action. A paused transfer that left usable bytes behind is continued and
    /// one that stopped on its own is retried: the operation is the same, but the two words are what the user
    /// is actually choosing between — and only a download leaves bytes behind.
    /// </summary>
    public string RestartText => State == CloudTransferState.Paused && CanContinueFromStaging ? "继续" : "重试";
    /// <summary>
    /// Whether stopping this transfer leaves bytes a later run can build on. Only a download does, because
    /// only a download has Feng Sync's own staging file to append to; an upload cannot append to a remote
    /// object and a remote→remote copy never routes the bytes through this process at all.
    /// </summary>
    public bool CanContinueFromStaging => Direction == CloudTransferDirection.Download;
    public string DirectionText => Direction switch
    {
        CloudTransferDirection.Upload => "上传",
        CloudTransferDirection.Download => "下载",
        _ => "复制"
    };
    public string FileName => Direction == CloudTransferDirection.Upload ? Path.GetFileName(LocalPath) : NameOf(RemotePath);
    public string Display => $"{DirectionText} · {FileName}";
    public string SizeText => TotalBytes <= 0 ? "" : $"{Megabytes(_completedBytes):N1} / {Megabytes(TotalBytes):N1} MB";

    public string StatusText => State switch
    {
        CloudTransferState.Pending => "等待中",
        CloudTransferState.Running => RunText(),
        CloudTransferState.Paused => "已暂停",
        CloudTransferState.Completed => $"已完成  ·  {Megabytes(TotalBytes):N1} MB",
        CloudTransferState.Cancelled => Direction switch
        {
            CloudTransferDirection.Download => "已取消，可继续下载",
            CloudTransferDirection.Upload => "已取消，可重新上传",
            _ => "已取消，重新复制会从头开始"
        },
        _ => "失败：" + (_error ?? "未知错误")
    };

    private string RunText()
    {
        var progress = string.IsNullOrEmpty(SizeText)
            ? $"{Percentage:N1}%"
            : $"{Percentage:N1}%  ·  {SizeText}" + (_bytesPerSecond > 0 ? $"  ·  {Megabytes(_bytesPerSecond):N1} MB/s" : "");
        return _attempt > 1 ? $"第 {_attempt} 次尝试  ·  {progress}" : progress;
    }

    internal static string NameOf(string path) => path.Trim('/').Split('/').Last();
    private static double Megabytes(double bytes) => bytes / 1024d / 1024d;

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The rate ceiling the queue pushes to the daemon while transfers are running. Both values are bytes per
/// second, and 0 means "no limit" — the state every installation was in before this existed.
/// </summary>
public sealed record CloudTransferLimits(long UploadBytesPerSecond = 0, long DownloadBytesPerSecond = 0)
{
    public static readonly CloudTransferLimits None = new();

    /// <summary>The string <c>core/bwlimit</c> takes. See <see cref="CloudTransferLimit"/> for why it is not a number.</summary>
    public string Rate => CloudTransferLimit.Rate(UploadBytesPerSecond, DownloadBytesPerSecond);

    public bool IsLimited => CloudTransferLimit.IsLimited(UploadBytesPerSecond, DownloadBytesPerSecond);

    public string Describe() => CloudTransferLimit.Describe(UploadBytesPerSecond, DownloadBytesPerSecond);
}

/// <summary>
/// Serialises cloud uploads, downloads, and remote→remote copies: bounded concurrency, per-task progress,
/// cancellation and pausing, automatic retries, and a journal so cancelled or failed transfers survive an
/// application restart and can be continued.
/// </summary>
public sealed class CloudTransferQueue : INotifyPropertyChanged
{
    /// <summary>Matches the historical fixed limit, so an unconfigured queue behaves as it always did.</summary>
    public const int DefaultConcurrency = 2;
    public const int MinimumConcurrency = 1;
    public const int MaximumConcurrency = 16;

    /// <summary>
    /// One transfer's claim on a slot. The task id travels with the ticket so the pump can order the queue by
    /// each waiter's <i>current</i> priority: a user who moves a waiting row up while it is already parked has
    /// to be moved ahead of the others, which a ticket holding a copy of the old order could not express.
    /// </summary>
    private sealed class SlotWaiter(Guid taskId)
    {
        public Guid TaskId { get; } = taskId;
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly CloudTransferJournalStore _journal;
    private readonly CloudTransferHistoryRepository? _history;
    /// <summary>
    /// Resolves the RC client lazily, exactly as <see cref="CloudFileManagerService"/> does, so a queue can be
    /// built and its copy branch driven before any shell has started a daemon — which is what lets the
    /// remote→remote payload be asserted without rclone.
    /// </summary>
    private readonly Func<CancellationToken, Task<RcloneRcClient>> _client;
    /// <summary>
    /// A hand-rolled gate rather than a <see cref="SemaphoreSlim"/>, because the concurrency limit is
    /// user-adjustable at runtime and a semaphore's capacity cannot be changed after construction.
    /// </summary>
    private readonly object _gate = new();
    private readonly List<SlotWaiter> _waiters = [];
    /// <summary>Ids of the transfers the current batch covers, so a drained queue can report the batch.</summary>
    private readonly object _batchGate = new();
    private readonly HashSet<Guid> _batchIds = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _cancellations = [];
    private readonly Dictionary<Guid, TaskCompletionSource<bool>> _completions = [];
    /// <summary>Tasks the user asked to park. Read when a cancellation turns out to be a pause.</summary>
    private readonly HashSet<Guid> _pauseRequests = [];
    private int _running;
    private int _concurrency = DefaultConcurrency;
    private int _maxRetries = 2;
    private int _activeCount;
    private int _nextOrder = 1;
    private CloudTransferLimits _limits = CloudTransferLimits.None;
    /// <summary>The rate this queue last pushed, so an unchanged limit is not re-sent before every task.</summary>
    private string? _appliedRate;
    private CloudTransferTimeWindow? _timeWindow;

    public CloudTransferQueue(
        CloudTransferJournalStore? journal = null,
        Func<CancellationToken, Task<RcloneRcClient>>? client = null,
        CloudTransferHistoryRepository? history = null)
    {
        _journal = journal ?? new CloudTransferJournalStore();
        _client = client ?? (ct => AppServices.Current.RcloneHost.GetClientAsync(ct));
        _history = history;
    }

    /// <summary>
    /// Where transfers that leave the list are archived. Absent means 清除已完成 really does discard them, which
    /// is what the focused queue tests want; both shells supply one.
    /// </summary>
    public CloudTransferHistoryRepository? History => _history;

    /// <summary>
    /// Files at or above this size keep a resumable staging file, so an interrupted download is
    /// continued instead of restarted. Set from the application settings; the initial value matches
    /// <see cref="ApplicationSettings.DefaultResumeMinimumBytes"/> so a queue built before settings
    /// load still behaves like a default installation.
    /// </summary>
    public long ResumeMinimumBytes { get; set; } = new ApplicationSettings().DefaultResumeMinimumBytes;

    /// <summary>
    /// The rate ceiling to hold the daemon to while transfers run. It is applied when a transfer starts and
    /// released when the queue drains, so a limit never outlives the work that needed it and browsing is never
    /// throttled by a setting about transfers.
    /// </summary>
    public CloudTransferLimits Limits
    {
        get => _limits;
        set
        {
            if (_limits == value) return;
            _limits = value;
            Raise(nameof(Limits));
            Raise(nameof(LimitText));
            Raise(nameof(LimitError));
        }
    }

    /// <summary>
    /// The last failure while pushing the limit. Surfaced rather than swallowed: a limit that silently did not
    /// take effect is the same class of problem as a misspelled mount option.
    /// </summary>
    public string? LimitError { get; private set; }

    public string LimitText => _limits.IsLimited ? "限速 " + _limits.Describe() : "不限速";

    /// <summary>
    /// Applies a rate to the daemon. A seam so the decision — when a limit is pushed and when it is released —
    /// is testable without a daemon, while the default goes through <see cref="RcloneRcClient"/>.
    /// </summary>
    internal Func<string, CancellationToken, Task>? ApplyLimit { get; set; }

    /// <summary>
    /// The daily window transfers may run in. <see langword="null"/> means any time. Outside the window the
    /// queue parks its waiting transfers instead of starting them; nothing is cancelled, so the work resumes
    /// by itself when the window opens.
    /// </summary>
    public CloudTransferTimeWindow? TimeWindow
    {
        get => _timeWindow;
        set
        {
            if (_timeWindow == value) return;
            _timeWindow = value;
            Raise(nameof(TimeWindow));
            Raise(nameof(WaitingForWindow));
            RaiseSummary();
        }
    }

    /// <summary>The clock the window is evaluated against. A seam so a test can be at 03:00 without waiting for it.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    /// <summary>Whether a waiting transfer is parked because the clock is outside <see cref="TimeWindow"/>.</summary>
    public bool WaitingForWindow => _timeWindow is { } window && !window.Contains(TimeOnly.FromDateTime(Clock().LocalDateTime));

    /// <summary>
    /// Re-reads the clock and hands out slots if the window has opened. Called by the queue's own one-shot timer
    /// and directly by tests, which is why it is not private.
    /// </summary>
    internal void EvaluateSchedule()
    {
        if (_timeWindow is not null) Raise(nameof(WaitingForWindow));
        lock (_gate)
        {
            if (WaitingForWindow) { ScheduleWindowRecheck(); return; }
            PumpWaiters();
        }
        RaiseSummary();
    }

    /// <summary>
    /// Arranges for one re-check when the window next opens. A one-shot delay rather than a periodic timer: it
    /// cannot outlive the wait that scheduled it, and there is nothing to dispose.
    /// </summary>
    private void ScheduleWindowRecheck()
    {
        if (_timeWindow is not { } window) return;
        var delay = window.DelayUntilOpen(TimeOnly.FromDateTime(Clock().LocalDateTime));
        if (delay <= TimeSpan.Zero) delay = TimeSpan.FromSeconds(1);
        if (delay > TimeSpan.FromHours(1)) delay = TimeSpan.FromHours(1);
        _ = Task.Delay(delay).ContinueWith(_ => EvaluateSchedule(), TaskScheduler.Default);
    }

    private async Task EnsureLimitAppliedAsync(CancellationToken ct)
    {
        var rate = _limits.Rate;
        if (rate == _appliedRate) return;
        // Nothing to push and nothing to undo: telling a daemon that was never given a rate to go back to "off"
        // is a round trip that changes nothing, and this is the state of every installation that never sets one.
        if (rate == CloudTransferLimit.Off && _appliedRate is null) return;
        try
        {
            if (ApplyLimit is { } seam) await seam(rate, ct);
            else
            {
                var client = await _client(ct);
                await client.SetBandwidthLimitAsync(rate, ct);
            }
            _appliedRate = rate;
            if (LimitError is not null) { LimitError = null; Raise(nameof(LimitError)); }
        }
        catch (Exception ex)
        {
            // A ceiling is a convenience: failing to set it must not fail the transfer. It is reported instead,
            // because a limit the user asked for that never took effect would otherwise be invisible.
            LimitError = "限速未能生效：" + RcloneUiError.Describe(ex, "cloud-transfer-limit");
            Raise(nameof(LimitError));
        }
    }

    /// <summary>Gives the daemon its full bandwidth back once nothing is left to throttle.</summary>
    private async Task ReleaseLimitAsync()
    {
        if (_appliedRate is null || _appliedRate == CloudTransferLimit.Off) return;
        try
        {
            if (ApplyLimit is { } seam) await seam(CloudTransferLimit.Off, CancellationToken.None);
            else
            {
                var client = await _client(CancellationToken.None);
                await client.SetBandwidthLimitAsync(CloudTransferLimit.Off, CancellationToken.None);
            }
            _appliedRate = null;
        }
        catch { /* the next batch re-applies whatever it needs */ }
    }

    /// <summary>
    /// How many transfers may run at once. Lowering it never disturbs the transfers already in flight —
    /// they finish, and the surplus slots simply are not handed out again.
    /// </summary>
    public int Concurrency
    {
        get => _concurrency;
        set
        {
            var next = Math.Clamp(value, MinimumConcurrency, MaximumConcurrency);
            lock (_gate)
            {
                if (next == _concurrency) return;
                _concurrency = next;
                // Raising the limit has to wake waiters itself; nothing else will until a slot is released.
                PumpWaiters();
            }
            Raise(nameof(Concurrency));
        }
    }

    /// <summary>Automatic retries per transfer before it is reported as failed. 0 disables retrying.</summary>
    public int MaxRetries
    {
        get => _maxRetries;
        set
        {
            var next = Math.Clamp(value, 0, 10);
            if (next == _maxRetries) return;
            _maxRetries = next;
            Raise(nameof(MaxRetries));
        }
    }

    /// <summary>
    /// How long to wait before attempt <c>attempt + 1</c>. A settable function rather than a constant so a
    /// test can drive the retry path without waiting out a real backoff.
    /// </summary>
    public Func<int, TimeSpan> RetryDelay { get; set; } = attempt => TimeSpan.FromSeconds(Math.Min(30, 1 << attempt));

    public ObservableCollection<CloudTransferTask> Tasks { get; } = [];

    public int ActiveCount
    {
        get => _activeCount;
        private set { if (_activeCount == value) return; _activeCount = value; Raise(nameof(ActiveCount)); Raise(nameof(HasActiveTransfers)); Raise(nameof(SummaryText)); }
    }
    public bool HasActiveTransfers => _activeCount > 0;
    /// <summary>
    /// Deliberately only completed tasks: a cancelled or failed task is the user's queue of things to
    /// continue, so it must not enable a "clear finished" action that would silently drop it.
    /// </summary>
    public bool HasCompletedTasks => Tasks.Any(task => task.State == CloudTransferState.Completed);
    public bool HasResumableTasks => Tasks.Any(task => task.IsResumable);
    public bool HasPausedTasks => Tasks.Any(task => task.State == CloudTransferState.Paused);
    /// <summary>
    /// Whether the queue holds a remote→remote copy. The transfer window reads it to say out loud that a copy
    /// starts over: rclone moves those bytes between two remotes, so there is no Feng Sync staging file to
    /// continue from, and a user who paused one expecting a 继续 would otherwise be misled by the row.
    /// </summary>
    public bool HasCopyTasks => Tasks.Any(task => task.Direction == CloudTransferDirection.Copy);
    public bool HasTasks => Tasks.Count > 0;

    /// <summary>
    /// Raised once when a batch of transfers has drained — every transfer that was running has stopped and
    /// nothing is left waiting for a slot. A host uses it to report the outcome without watching every task.
    /// Not raised when the queue was only ever idle, and not when a transfer is restarted, so a single user
    /// action produces a single notification.
    /// </summary>
    public event EventHandler<CloudTransferBatch>? BatchCompleted;

    /// <summary>
    /// Average progress of the tasks that have not finished, so a manager-window progress bar can show
    /// aggregate activity without subscribing to every task. 100 once the queue drained successfully.
    /// Paused tasks count as outstanding: their bytes are not done, and reporting 100% for a queue that is
    /// merely parked would be a lie the user acts on.
    /// </summary>
    public double OverallPercentage
    {
        get
        {
            if (Tasks.Count == 0) return 0;
            var outstanding = Tasks.Where(task => task.State is CloudTransferState.Pending or CloudTransferState.Running or CloudTransferState.Paused).ToList();
            if (outstanding.Count > 0) return outstanding.Average(task => task.Percentage);
            return Tasks.Any(task => task.State == CloudTransferState.Completed) ? 100 : 0;
        }
    }

    public string SummaryText
    {
        get
        {
            if (Tasks.Count == 0) return "暂无传输任务。把文件拖到文件浏览器即可上传，把文件拖出即可下载。";
            var running = Tasks.Count(task => task.State == CloudTransferState.Running);
            var pending = Tasks.Count(task => task.State == CloudTransferState.Pending);
            var done = Tasks.Count(task => task.State == CloudTransferState.Completed);
            var paused = Tasks.Count(task => task.State == CloudTransferState.Paused);
            // Paused tasks are resumable but are reported on their own line, so they are excluded here to
            // avoid counting the same task twice in one sentence.
            var resumable = Tasks.Count(task => task.IsResumable && task.State != CloudTransferState.Paused);
            var parts = new List<string>();
            if (WaitingForWindow) parts.Add($"等待传输时段 {_timeWindow!.Describe()}");
            if (running > 0) parts.Add($"进行中 {running}");
            if (pending > 0) parts.Add($"等待 {pending}");
            if (paused > 0) parts.Add($"已暂停 {paused}");
            if (done > 0) parts.Add($"已完成 {done}");
            if (resumable > 0) parts.Add($"可续传 {resumable}");
            return string.Join(" · ", parts);
        }
    }

    public CloudTransferTask Enqueue(CloudTransferDirection direction, string remoteUri, string remotePath, string localPath, long totalBytes)
        => Enqueue(new CloudTransferTask(direction, remoteUri, remotePath, localPath, totalBytes));

    /// <summary>
    /// Queues one remote→remote copy. The destination is a URI plus a path because a copy has two remotes and
    /// no local file: <see cref="CloudTransferTask.LocalPath"/> stays empty rather than being bent to carry an
    /// endpoint, which the journal and every local-path consumer would read literally.
    /// </summary>
    public CloudTransferTask EnqueueCopy(string sourceUri, string sourcePath, string targetUri, string targetPath, long totalBytes)
        => Enqueue(new CloudTransferTask(CloudTransferDirection.Copy, sourceUri, sourcePath, string.Empty, totalBytes, targetUri, targetPath));

    private CloudTransferTask Enqueue(CloudTransferTask task)
    {
        // New work goes to the back of the queue, expressed as the largest order so far rather than the count:
        // after a user reorders rows, "the last one" is no longer the highest index.
        task.Order = _nextOrder++;
        Track(task);
        Tasks.Add(task);
        RaiseSummary();
        // Through Start rather than straight to RunAsync, because Start is what registers the task with the
        // current batch. A batch that never learned about its own transfers never reports itself drained, so
        // 传输完成后动作 would silently never fire for a queue the user filled by hand — the very case it
        // exists for.
        Start(task);
        return task;
    }

    /// <summary>
    /// Moves a waiting transfer one place earlier, past its nearest neighbour. Only a transfer that has not
    /// started can be moved: one that already holds a slot is running now, and reordering behind it would
    /// change nothing the user can observe.
    /// </summary>
    public bool MoveUp(CloudTransferTask task) => Move(task, -1);

    /// <summary>Moves a waiting transfer one place later.</summary>
    public bool MoveDown(CloudTransferTask task) => Move(task, +1);

    public bool CanMoveUp(CloudTransferTask task) => Neighbour(task, -1) is not null;

    public bool CanMoveDown(CloudTransferTask task) => Neighbour(task, +1) is not null;

    private bool Move(CloudTransferTask task, int offset)
    {
        var neighbour = Neighbour(task, offset);
        if (neighbour is null) return false;
        // The visible list and the priority are two faces of the same thing, so both move together: swapping
        // only Order would leave the list claiming an order the queue does not use, and swapping only the list
        // would leave the queue running the row the user moved down.
        (task.Order, neighbour.Order) = (neighbour.Order, task.Order);
        var from = Tasks.IndexOf(task);
        var to = Tasks.IndexOf(neighbour);
        if (from >= 0 && to >= 0) Tasks.Move(from, to);
        RaiseSummary();
        return true;
    }

    /// <summary>
    /// The neighbour a move would trade places with, or <see langword="null"/> at the end of the queue. A
    /// running or finished row is not a neighbour to trade with: the row the user is trying to get ahead of is
    /// the next <i>waiting</i> one, not whichever row happens to be drawn above it.
    /// </summary>
    private CloudTransferTask? Neighbour(CloudTransferTask task, int offset)
    {
        if (task.State != CloudTransferState.Pending) return null;
        var waiting = Tasks.Where(x => x.State == CloudTransferState.Pending).ToList();
        var index = waiting.IndexOf(task);
        if (index < 0) return null;
        var target = index + offset;
        return target >= 0 && target < waiting.Count ? waiting[target] : null;
    }

    /// <summary>
    /// Mirrors each task's progress and lifecycle onto the queue so a view can bind one property
    /// instead of attaching a handler per task. Progress ticks only move the aggregate bar; the
    /// summary counts change on lifecycle transitions, which are far rarer.
    /// </summary>
    private void Track(CloudTransferTask task)
    {
        task.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CloudTransferTask.Percentage)) Raise(nameof(OverallPercentage));
            else if (args.PropertyName == nameof(CloudTransferTask.State)) RaiseSummary();
        };
    }

    /// <summary>
    /// Lists the tasks a previous session left unfinished without starting them, so the user decides
    /// when to resume. They surface as cancelled entries, which is what they factually are — except a task
    /// that was explicitly paused, which comes back paused so the user's intent survives the restart.
    /// </summary>
    public async Task<int> LoadResumableAsync(CancellationToken ct = default)
    {
        var records = await _journal.LoadIncompleteAsync(ct);
        var loaded = 0;
        foreach (var record in records)
        {
            if (Tasks.Any(task => task.Id == record.Id)) continue;
            var restored = new CloudTransferTask(record.Direction, record.RemoteUri, record.RemotePath, record.LocalPath, record.TotalBytes, record.TargetUri, record.TargetPath)
            {
                Id = record.Id,
                State = record.State == CloudTransferState.Paused ? CloudTransferState.Paused : CloudTransferState.Cancelled,
                Error = record.Error,
                ErrorDetail = record.ErrorDetail
            };
            Track(restored);
            Tasks.Add(restored);
            loaded++;
        }
        if (loaded > 0) RaiseSummary();
        return loaded;
    }

    /// <summary>
    /// Starts every stopped transfer currently listed — paused, cancelled or failed. Named for the action
    /// the transfer window has always offered; a paused transfer is simply the newest way to be stopped.
    /// </summary>
    public int ResumeFinished()
    {
        var resumed = 0;
        foreach (var task in Tasks.Where(task => task.IsResumable).ToList())
        {
            // A paused transfer may still be unwinding its cancellation; restarting it now would run the
            // same task twice and hand its slot to a transfer that is already gone.
            if (_cancellations.ContainsKey(task.Id)) continue;
            Start(task);
            resumed++;
        }
        if (resumed > 0) RaiseSummary();
        return resumed;
    }

    /// <summary>Restarts one stopped transfer. Named for the row action, which reads "重试".</summary>
    public void Retry(CloudTransferTask task)
    {
        if (!task.IsResumable || _cancellations.ContainsKey(task.Id)) return;
        Start(task);
        RaiseSummary();
    }

    /// <summary>
    /// Parks a transfer. A pending one is parked immediately; a running one is stopped at its next
    /// cancellation point, which is why its state only changes once the transfer actually stops. Whatever
    /// staging bytes exist are deliberately kept — that is what makes continuing pick up where it left off.
    /// </summary>
    public void Pause(CloudTransferTask task)
    {
        if (!task.CanPause) return;
        _pauseRequests.Add(task.Id);
        if (_cancellations.TryGetValue(task.Id, out var cancellation)) cancellation.Cancel();
        else { task.State = CloudTransferState.Paused; Persist(task); }
        RaiseSummary();
    }

    /// <summary>Lets a parked transfer go again, from wherever it stopped.</summary>
    public void Resume(CloudTransferTask task)
    {
        if (!task.IsResumable || _cancellations.ContainsKey(task.Id)) return;
        Start(task);
        RaiseSummary();
    }

    public int PauseAll()
    {
        var paused = 0;
        foreach (var task in Tasks.Where(task => task.CanPause).ToList()) { Pause(task); paused++; }
        return paused;
    }

    public void Cancel(CloudTransferTask task)
    {
        if (_cancellations.TryGetValue(task.Id, out var cancellation)) cancellation.Cancel();
        else if (task.State is CloudTransferState.Pending or CloudTransferState.Paused) { task.State = CloudTransferState.Cancelled; Persist(task); }
    }

    public void CancelAll()
    {
        foreach (var task in Tasks.Where(task => task.CanCancel).ToList()) Cancel(task);
    }

    /// <summary>
    /// Removes the completed rows, filing them in the archive first. The archive write is issued before the row
    /// goes away and is not awaited: it is a record of something that already happened, so failing to file it
    /// must not leave the row stuck on screen.
    /// </summary>
    /// <remarks>
    /// <see cref="RaiseSummary"/> runs even when there was nothing to clear, and that is load-bearing rather
    /// than incidental: this is the queue's one "the list changed, re-read my numbers" call, and both the
    /// window's 清除已完成 and the entry-point test that checks the progress bar reads the *installed* queue
    /// rely on it. An early return for an empty list looks like a free optimization and silently drops it.
    /// </remarks>
    public int ClearFinished()
    {
        var finished = Tasks.Where(task => task.State == CloudTransferState.Completed).ToList();
        if (_history is not null && finished.Count > 0)
        {
            var archived = finished.Select(task => CloudTransferHistoryEntry.From(task, Clock())).ToList();
            _ = ArchiveAsync(archived);
        }
        foreach (var task in finished)
        {
            Tasks.Remove(task);
            _ = _journal.RemoveAsync(task.Id);
        }
        RaiseSummary();
        return finished.Count;
    }

    private async Task ArchiveAsync(IReadOnlyList<CloudTransferHistoryEntry> entries)
    {
        try { await _history!.AppendAsync(entries); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Completes when the task leaves the queue's active set. Used by drag-out downloads.</summary>
    public Task<bool> WaitForAsync(CloudTransferTask task)
    {
        if (task.IsFinished) return System.Threading.Tasks.Task.FromResult(task.State == CloudTransferState.Completed);
        lock (_completions)
        {
            if (!_completions.TryGetValue(task.Id, out var completion))
            {
                completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _completions[task.Id] = completion;
            }
            return completion.Task;
        }
    }

    private void Start(CloudTransferTask task)
    {
        task.State = CloudTransferState.Pending;
        task.Error = null;
        task.ErrorDetail = null;
        _pauseRequests.Remove(task.Id);
        lock (_batchGate) _batchIds.Add(task.Id);
        _ = RunAsync(task);
    }

    /// <summary>
    /// Whether a failure is worth retrying. A missing local source, an unusable local directory, or a
    /// rejected credential cannot be fixed by waiting, so retrying those would only delay the error the
    /// user needs to see — and for an expired login it would delay it three times over.
    /// </summary>
    internal static bool IsRetryable(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException => false,
        RcloneException { Failure.Category: RcloneFailureCategory.Authentication or RcloneFailureCategory.Permission
            or RcloneFailureCategory.Configuration or RcloneFailureCategory.NotFound } => false,
        _ => true
    };

    /// <summary>
    /// The endpoint a download reads from. A download stages under the file's own name — the shared staging
    /// path is a bare name, not the remote-relative path — so the source has to be addressed the same way,
    /// by rooting its endpoint at the file's directory. Addressing that bare name against the endpoint root
    /// instead asks rclone for a same-named object one or more directories up: measured at the wire, a task
    /// whose path was <c>docs/a.txt</c> read <c>[nas:/]/team/a.txt</c> from an endpoint rooted at <c>team</c>,
    /// which is a different object and, when it exists, silently the wrong file. An upload needs no such
    /// adjustment: its source is already the local file's own directory, and its destination carries the
    /// whole remote path. A local URI is returned untouched, since it has no root to compose against.
    /// </summary>
    internal static string DownloadSourceUri(CloudTransferTask task)
    {
        if (!EndpointFactory.IsRemote(task.RemoteUri)) return task.RemoteUri;
        var separator = task.RemotePath.LastIndexOf('/');
        return separator < 0 ? task.RemoteUri : task.RemoteUri.TrimEnd('/') + "/" + task.RemotePath[..separator];
    }

    private async Task RunAsync(CloudTransferTask task)
    {
        var cancellation = new CancellationTokenSource();
        _cancellations[task.Id] = cancellation;
        var ct = cancellation.Token;
        var committed = false;
        var slotAcquired = false;
        IEndpoint? target = null;
        // A staging object is named after the file at its own endpoint. For an upload the local file is
        // the source, so the name is the local file name, while the published destination is the full
        // remote-relative path. For a download both collapse to the remote file name. A remote→remote copy
        // has neither: the bytes stay inside rclone, so its destination is the target path itself and there
        // is no staging object to name, publish, or discard.
        var isCopy = task.Direction == CloudTransferDirection.Copy;
        var stagingPath = isCopy
            ? ""
            : task.Direction == CloudTransferDirection.Upload ? Path.GetFileName(task.LocalPath) : CloudTransferTask.NameOf(task.RemotePath);
        var destination = isCopy
            ? task.TargetPath ?? ""
            : task.Direction == CloudTransferDirection.Upload ? task.RemotePath : stagingPath;
        try
        {
            task.State = CloudTransferState.Pending;
            task.CompletedBytes = 0;
            task.BytesPerSecond = 0;
            task.Error = null;
        task.ErrorDetail = null;
            task.Attempt = 0;
            // A task can only arrive here without a destination through a hand-edited journal file, and
            // copying to an empty path would address the target endpoint's root.
            if (isCopy && (string.IsNullOrWhiteSpace(task.TargetUri) || string.IsNullOrWhiteSpace(destination)))
                throw new InvalidOperationException("云端复制缺少目标端点或目标路径。");
            Persist(task);
            // The ceiling is pushed before the slot is taken so it is already in force for the very first
            // byte, and before the client is resolved so the seam can be replaced without a daemon.
            await EnsureLimitAppliedAsync(ct);
            await AcquireSlotAsync(task, ct);
            slotAcquired = true;
            ActiveCount++;
            task.State = CloudTransferState.Running;

            var client = await _client(ct);
            IEndpoint source;
            if (isCopy)
            {
                source = EndpointFactory.CreateWithClient(task.RemoteUri, client);
                target = EndpointFactory.CreateWithClient(task.TargetUri!, client);
            }
            else
            {
                var localDirectory = Path.GetDirectoryName(task.LocalPath) ?? throw new InvalidOperationException($"本地路径缺少目录：{task.LocalPath}");
                Directory.CreateDirectory(localDirectory);
                if (task.Direction == CloudTransferDirection.Upload)
                {
                    if (!File.Exists(task.LocalPath)) throw new FileNotFoundException("要上传的本地文件已不存在。", task.LocalPath);
                    task.TotalBytes = new FileInfo(task.LocalPath).Length;
                    source = new LocalEndpoint(localDirectory);
                    target = EndpointFactory.CreateWithClient(task.RemoteUri, client);
                }
                else
                {
                    source = EndpointFactory.CreateWithClient(DownloadSourceUri(task), client);
                    target = new LocalEndpoint(localDirectory);
                }
            }

            for (var attempt = 0; ; attempt++)
            {
                task.Attempt = attempt + 1;
                try
                {
                    if (attempt > 0) await PrepareRetryAsync(task, target, stagingPath);

                    var lastBytes = 0L;
                    var lastStamp = Stopwatch.GetTimestamp();
                    var progress = new Progress<TransferProgress>(update =>
                    {
                        task.CompletedBytes = update.BytesCompleted;
                        if (update.TotalBytes > 0) task.TotalBytes = update.TotalBytes;
                        var elapsed = Stopwatch.GetElapsedTime(lastStamp).TotalSeconds;
                        if (elapsed < 0.25) return;
                        task.BytesPerSecond = Math.Max(0, (update.BytesCompleted - lastBytes) / elapsed);
                        lastBytes = update.BytesCompleted;
                        lastStamp = Stopwatch.GetTimestamp();
                    });

                    if (isCopy)
                        await EndpointTransfer.CopyServerSideAsync(source, target, task.RemotePath, destination, task.Id,
                            task.TotalBytes, ActiveCount, progress, EnsureParentAsync(target, destination), ct);
                    else
                    {
                        // Passing no resume candidates lets TransferResume search a local target itself.
                        var temporary = await EndpointTransfer.CopyToStagingAsync(source, target, stagingPath, null, task.Id,
                            task.TotalBytes, ActiveCount, progress, EnsureParentAsync(target, destination), ResumeMinimumBytes, ct);
                        await EndpointTransfer.PublishAsync(target, temporary, destination, overwrite: true, ct);
                    }
                    task.CompletedBytes = task.TotalBytes;
                    committed = true;
                    task.State = CloudTransferState.Completed;
                    task.BytesPerSecond = 0;
                    await _journal.RemoveAsync(task.Id);
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (attempt < MaxRetries && IsRetryable(ex))
                {
                    task.Error = RcloneUiError.Describe(ex, "cloud-transfer");
                    task.ErrorDetail = RcloneUiError.DescribeDetail(ex, "cloud-transfer");
                    await Task.Delay(RetryDelay(attempt), ct);
                }
                catch (Exception ex)
                {
                    task.State = CloudTransferState.Failed;
                    task.BytesPerSecond = 0;
                    task.Error = RcloneUiError.Describe(ex, "cloud-transfer");
                    task.ErrorDetail = RcloneUiError.DescribeDetail(ex, "cloud-transfer");
                    Persist(task);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Pausing and cancelling are both a cancellation; which one this was is recorded before the
            // cancel was issued, because the two leave the user with different next steps.
            var paused = _pauseRequests.Remove(task.Id);
            task.State = paused ? CloudTransferState.Paused : CloudTransferState.Cancelled;
            task.BytesPerSecond = 0;
            Persist(task);
        }
        catch (Exception ex)
        {
            task.State = CloudTransferState.Failed;
            task.BytesPerSecond = 0;
            task.Error = RcloneUiError.Describe(ex, "cloud-transfer");
            task.ErrorDetail = RcloneUiError.DescribeDetail(ex, "cloud-transfer");
            Persist(task);
        }
        finally
        {
            // Mirrors the executor: local staging survives so a cancelled download can resume, while a
            // remote staging object is removed because rclone cannot resume it across runs. A copy created
            // no staging object at all, so there is nothing of ours to remove — and rclone's own partial
            // object is not a name Feng Sync may delete.
            if (!isCopy && !committed && target is not null && target is not LocalEndpoint)
            {
                try { await EndpointTransfer.DiscardStagingAsync(target, stagingPath); }
                catch { /* preserve the original failure */ }
            }
            if (slotAcquired)
            {
                ActiveCount = Math.Max(0, ActiveCount - 1);
                ReleaseSlot();
            }
            _cancellations.Remove(task.Id);
            _pauseRequests.Remove(task.Id);
            cancellation.Dispose();
            RaiseSummary();
            lock (_completions)
            {
                if (_completions.Remove(task.Id, out var completion)) completion.TrySetResult(task.State == CloudTransferState.Completed);
            }
        }
    }

    /// <summary>
    /// Clears the way for another attempt. An upload's staging object cannot be appended to — rclone has no
    /// offset write — so a retry starts that file over and the partial object has to go first. A download's
    /// local staging file is deliberately kept: continuing it is the whole point of the resume machinery.
    /// A remote→remote copy stages nothing at all, so there is no object of ours to discard.
    /// </summary>
    private static async Task PrepareRetryAsync(CloudTransferTask task, IEndpoint? target, string stagingPath)
    {
        task.CompletedBytes = 0;
        task.BytesPerSecond = 0;
        task.Error = null;
        task.ErrorDetail = null;
        if (task.Direction == CloudTransferDirection.Copy || target is null || target is LocalEndpoint) return;
        try { await EndpointTransfer.DiscardStagingAsync(target, stagingPath); }
        catch { /* a missing staging object is the state a retry wants anyway */ }
    }

    /// <summary>
    /// Takes one transfer slot, waiting for one when the queue is already at its concurrency limit. The wait
    /// is cancellable, and a cancelled wait gives its ticket up in a way the pump understands — otherwise the
    /// pump would hand a slot to a waiter that had already given up, and the queue would slowly starve.
    /// A transfer parked outside the configured time window is the same kind of wait, and is woken by
    /// <see cref="EvaluateSchedule"/> instead of by another transfer finishing.
    /// </summary>
    private async Task AcquireSlotAsync(CloudTransferTask task, CancellationToken ct)
    {
        SlotWaiter waiter;
        lock (_gate)
        {
            if (_running < _concurrency && !WaitingForWindow) { _running++; return; }
            waiter = new SlotWaiter(task.Id);
            _waiters.Add(waiter);
        }
        if (WaitingForWindow) ScheduleWindowRecheck();
        try
        {
            await waiter.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var drained = false;
            lock (_gate)
            {
                // Marking the ticket cancelled makes the pump skip it. If the pump granted it first, hand the
                // slot straight back; either way exactly one of the two paths owns the slot. This transfer
                // never reached ActiveCount, so its slot has to be returned — and a drain detected — here
                // rather than in the run loop's finally block.
                if (!waiter.Completion.TrySetCanceled(ct))
                {
                    _running = Math.Max(0, _running - 1);
                    PumpWaiters();
                    drained = _running == 0 && _waiters.Count == 0;
                }
            }
            if (drained) RaiseBatchCompleted();
            throw;
        }
    }

    private void ReleaseSlot()
    {
        bool drained;
        lock (_gate)
        {
            _running = Math.Max(0, _running - 1);
            PumpWaiters();
            // The queue is drained when no transfer holds a slot and none is waiting for one. Checked under
            // the gate so a transfer that was handed a slot a moment ago cannot be mistaken for a finished batch.
            drained = _running == 0 && _waiters.Count == 0;
        }
        if (drained) RaiseBatchCompleted();
    }

    /// <summary>
    /// Reports the batch that just drained. Reads the tasks on the calling thread, which is a worker thread
    /// for the normal path, so subscribers that touch UI must marshal — the same contract as
    /// <see cref="PropertyChanged"/> on this type.
    /// </summary>
    private void RaiseBatchCompleted()
    {
        Guid[] ids;
        lock (_batchGate)
        {
            ids = [.. _batchIds];
            _batchIds.Clear();
        }
        if (ids.Length == 0) return;
        var batch = Tasks.Where(task => ids.Contains(task.Id)).ToList();
        if (batch.Count == 0) return;
        // The ceiling belonged to this batch, so it goes away with it. Fired and forgotten: the drain path is
        // synchronous and a failure to release a limit must not surface as a transfer failure.
        _ = ReleaseLimitAsync();
        BatchCompleted?.Invoke(this, new CloudTransferBatch(
            batch.Count,
            batch.Count(task => task.State == CloudTransferState.Completed),
            batch.Count(task => task.State == CloudTransferState.Failed),
            batch.Count(task => task.State is CloudTransferState.Cancelled or CloudTransferState.Paused)));
    }

    /// <summary>
    /// Hands slots to waiting transfers, lowest <see cref="CloudTransferTask.Order"/> first. Must be called
    /// while holding <see cref="_gate"/>. The scan picks the first minimum rather than sorting, so waiters with
    /// equal orders — which is every queue nobody has reordered — are still served in the order they arrived.
    /// </summary>
    private void PumpWaiters()
    {
        while (_waiters.Count > 0 && _running < _concurrency)
        {
            if (WaitingForWindow) return;
            var pick = 0;
            for (var index = 1; index < _waiters.Count; index++)
                if (OrderOf(_waiters[index].TaskId) < OrderOf(_waiters[pick].TaskId)) pick = index;
            var waiter = _waiters[pick];
            _waiters.RemoveAt(pick);
            if (waiter.Completion.TrySetResult(true)) _running++;
        }
    }

    /// <summary>
    /// A waiting task's current priority. Read live rather than captured, because the user may reorder a row
    /// while it is parked; a task that has since been removed from the list sorts last rather than throwing.
    /// </summary>
    private int OrderOf(Guid taskId) =>
        Tasks.FirstOrDefault(task => task.Id == taskId)?.Order ?? int.MaxValue;

    private static Func<CancellationToken, Task> EnsureParentAsync(IEndpoint target, string path) => async ct =>
    {
        var parent = ParentOf(path);
        if (parent.Length > 0) await target.CreateDirectoryAsync(parent, ct);
    };

    internal static string ParentOf(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        var separator = normalized.LastIndexOf('/');
        return separator <= 0 ? "" : normalized[..separator];
    }

    private void Persist(CloudTransferTask task)
    {
        var record = new CloudTransferRecord(task.Id, task.Direction, task.RemoteUri, task.RemotePath, task.LocalPath, task.TotalBytes, task.State, task.Error, task.ErrorDetail, task.TargetUri, task.TargetPath);
        _ = SaveIgnoringFailureAsync(record);
    }

    /// <summary>The journal is a convenience for resume; a failed write must never fail a transfer.</summary>
    private async Task SaveIgnoringFailureAsync(CloudTransferRecord record)
    {
        try { await _journal.SaveAsync(record); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void RaiseSummary()
    {
        Raise(nameof(HasTasks));
        Raise(nameof(HasCompletedTasks));
        Raise(nameof(HasResumableTasks));
        Raise(nameof(HasPausedTasks));
        Raise(nameof(HasCopyTasks));
        Raise(nameof(WaitingForWindow));
        Raise(nameof(SummaryText));
        Raise(nameof(OverallPercentage));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Persisted state of one cloud transfer, so an interrupted queue can be rebuilt after a restart.</summary>
public sealed record CloudTransferRecord(
    Guid Id,
    CloudTransferDirection Direction,
    string RemoteUri,
    string RemotePath,
    string LocalPath,
    long TotalBytes,
    CloudTransferState State,
    string? Error,
    // Optional so journals written before the field existed still deserialize.
    string? ErrorDetail = null,
    // Optional for the same reason: only a remote→remote copy carries a destination endpoint.
    string? TargetUri = null,
    string? TargetPath = null);

/// <summary>
/// Journal for cloud transfers. It mirrors <see cref="TaskJournalStore"/>'s contract — one JSON file per
/// task under the app data directory, replaced atomically and never holding credentials — because the
/// sync journal's schema is keyed to a planned run and cannot describe a standalone cloud transfer.
/// </summary>
public sealed class CloudTransferJournalStore(string? root = null)
{
    private readonly string _root = root ?? Path.Combine(AppDataPaths.Root, "cloud-transfers");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task SaveAsync(CloudTransferRecord record, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_root);
            var target = Path.Combine(_root, record.Id + ".json");
            var temporary = target + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(record), ct);
            // A diagnostics reader can briefly hold the previous file open without delete sharing;
            // replacing it must wait for that transient read lock instead of failing the transfer.
            const int attempts = 20;
            for (var attempt = 1; ; attempt++)
            {
                try { File.Move(temporary, target, true); break; }
                catch (Exception ex) when (attempt < attempts && ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(25, ct);
                }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var target = Path.Combine(_root, id + ".json");
            if (File.Exists(target)) File.Delete(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<CloudTransferRecord>> LoadIncompleteAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_root)) return [];
        var records = new List<CloudTransferRecord>();
        foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var record = JsonSerializer.Deserialize<CloudTransferRecord>(await File.ReadAllTextAsync(file, ct));
                if (record is not null && record.State != CloudTransferState.Completed) records.Add(record);
            }
            catch (Exception ex) when (ex is JsonException or IOException) { /* skip an unreadable journal entry */ }
        }
        return records.OrderBy(record => record.RemotePath, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
