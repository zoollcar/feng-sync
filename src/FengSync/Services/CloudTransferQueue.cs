using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FengSync.Core;
using FengSync.Core.Configuration;
using FengSync.Core.Execution;

namespace FengSync.Services;

public enum CloudTransferDirection { Upload, Download }

public enum CloudTransferState { Pending, Running, Completed, Cancelled, Failed }

/// <summary>
/// One user-visible upload or download of a single file. The unit is deliberately a file, matching
/// <see cref="EndpointTransfer"/>'s staging/resume contract. A download keeps its staging file and can
/// be continued from where it stopped; an upload cannot, because rclone has no way to append to a
/// partially written remote object, so a retry starts that file again.
/// </summary>
public sealed class CloudTransferTask : INotifyPropertyChanged
{
    private CloudTransferState _state = CloudTransferState.Pending;
    private long _completedBytes;
    private double _bytesPerSecond;
    private string? _error;

    public CloudTransferTask(CloudTransferDirection direction, string remoteUri, string remotePath, string localPath, long totalBytes)
    {
        Direction = direction;
        RemoteUri = remoteUri;
        RemotePath = remotePath.Trim('/');
        LocalPath = localPath;
        TotalBytes = totalBytes;
    }

    public Guid Id { get; init; } = Guid.NewGuid();
    public CloudTransferDirection Direction { get; }
    /// <summary>Remote root in Feng Sync URI form, e.g. <c>s3://archive</c>. Credentials stay in rclone.conf.</summary>
    public string RemoteUri { get; }
    /// <summary>Path relative to the remote root, e.g. <c>team/docs/report.pdf</c>.</summary>
    public string RemotePath { get; }
    public string LocalPath { get; }
    public long TotalBytes { get; internal set; }

    public CloudTransferState State
    {
        get => _state;
        internal set { if (!Set(ref _state, value)) return; Raise(nameof(State)); Raise(nameof(IsFinished)); Raise(nameof(CanCancel)); Raise(nameof(IsResumable)); Raise(nameof(StatusText)); }
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

    public double Percentage => TotalBytes <= 0 ? 0 : Math.Min(100, _completedBytes * 100d / TotalBytes);
    public bool IsFinished => State is CloudTransferState.Completed or CloudTransferState.Cancelled or CloudTransferState.Failed;
    public bool CanCancel => State is CloudTransferState.Pending or CloudTransferState.Running;
    public bool IsResumable => State is CloudTransferState.Cancelled or CloudTransferState.Failed;
    public string DirectionText => Direction == CloudTransferDirection.Upload ? "上传" : "下载";
    public string FileName => Direction == CloudTransferDirection.Upload ? Path.GetFileName(LocalPath) : NameOf(RemotePath);
    public string Display => $"{DirectionText} · {FileName}";
    public string SizeText => TotalBytes <= 0 ? "" : $"{Megabytes(_completedBytes):N1} / {Megabytes(TotalBytes):N1} MB";

    public string StatusText => State switch
    {
        CloudTransferState.Pending => "等待中",
        CloudTransferState.Running => string.IsNullOrEmpty(SizeText)
            ? $"{Percentage:N1}%"
            : $"{Percentage:N1}%  ·  {SizeText}" + (_bytesPerSecond > 0 ? $"  ·  {Megabytes(_bytesPerSecond):N1} MB/s" : ""),
        CloudTransferState.Completed => $"已完成  ·  {Megabytes(TotalBytes):N1} MB",
        CloudTransferState.Cancelled => Direction == CloudTransferDirection.Download ? "已取消，可继续下载" : "已取消，可重新上传",
        _ => "失败：" + (_error ?? "未知错误")
    };

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
/// Serialises cloud uploads/downloads: bounded concurrency, per-task progress and cancellation, and a
/// journal so cancelled or failed transfers survive an application restart and can be continued.
/// </summary>
public sealed class CloudTransferQueue : INotifyPropertyChanged
{
    /// <summary>Mirrors the sync executor's default copy concurrency so a folder drop cannot fan out.</summary>
    public const int MaxConcurrentTransfers = 2;

    private readonly CloudTransferJournalStore _journal;
    private readonly SemaphoreSlim _slots = new(MaxConcurrentTransfers, MaxConcurrentTransfers);
    private readonly Dictionary<Guid, CancellationTokenSource> _cancellations = [];
    private readonly Dictionary<Guid, TaskCompletionSource<bool>> _completions = [];
    private int _activeCount;

    public CloudTransferQueue(CloudTransferJournalStore? journal = null) => _journal = journal ?? new CloudTransferJournalStore();

    /// <summary>
    /// Files at or above this size keep a resumable staging file, so an interrupted download is
    /// continued instead of restarted. Set from the application settings; the initial value matches
    /// <see cref="ApplicationSettings.DefaultResumeMinimumBytes"/> so a queue built before settings
    /// load still behaves like a default installation.
    /// </summary>
    public long ResumeMinimumBytes { get; set; } = new ApplicationSettings().DefaultResumeMinimumBytes;

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
    public bool HasTasks => Tasks.Count > 0;

    /// <summary>
    /// Average progress of the tasks that have not finished, so a manager-window progress bar can show
    /// aggregate activity without subscribing to every task. 100 once the queue drained successfully.
    /// </summary>
    public double OverallPercentage
    {
        get
        {
            if (Tasks.Count == 0) return 0;
            var outstanding = Tasks.Where(task => task.State is CloudTransferState.Pending or CloudTransferState.Running).ToList();
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
            var resumable = Tasks.Count(task => task.IsResumable);
            var parts = new List<string>();
            if (running > 0) parts.Add($"进行中 {running}");
            if (pending > 0) parts.Add($"等待 {pending}");
            if (done > 0) parts.Add($"已完成 {done}");
            if (resumable > 0) parts.Add($"可续传 {resumable}");
            return string.Join(" · ", parts);
        }
    }

    public CloudTransferTask Enqueue(CloudTransferDirection direction, string remoteUri, string remotePath, string localPath, long totalBytes)
    {
        var task = new CloudTransferTask(direction, remoteUri, remotePath, localPath, totalBytes);
        Track(task);
        Tasks.Add(task);
        Raise(nameof(HasTasks)); Raise(nameof(HasCompletedTasks)); Raise(nameof(SummaryText)); Raise(nameof(OverallPercentage));
        _ = RunAsync(task);
        return task;
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
            else if (args.PropertyName == nameof(CloudTransferTask.State))
            {
                Raise(nameof(HasCompletedTasks)); Raise(nameof(HasResumableTasks));
                Raise(nameof(SummaryText)); Raise(nameof(OverallPercentage));
            }
        };
    }

    /// <summary>
    /// Lists the tasks a previous session left unfinished without starting them, so the user decides
    /// when to resume. They surface as cancelled entries, which is what they factually are.
    /// </summary>
    public async Task<int> LoadResumableAsync(CancellationToken ct = default)
    {
        var records = await _journal.LoadIncompleteAsync(ct);
        var loaded = 0;
        foreach (var record in records)
        {
            if (Tasks.Any(task => task.Id == record.Id)) continue;
            var restored = new CloudTransferTask(record.Direction, record.RemoteUri, record.RemotePath, record.LocalPath, record.TotalBytes)
            {
                Id = record.Id,
                State = CloudTransferState.Cancelled,
                Error = record.Error
            };
            Track(restored);
            Tasks.Add(restored);
            loaded++;
        }
        if (loaded > 0) { Raise(nameof(HasTasks)); Raise(nameof(HasResumableTasks)); Raise(nameof(SummaryText)); Raise(nameof(OverallPercentage)); }
        return loaded;
    }

    /// <summary>Starts every cancelled or failed task currently listed.</summary>
    public int ResumeFinished()
    {
        var resumed = 0;
        foreach (var task in Tasks.Where(task => task.IsResumable).ToList())
        {
            task.State = CloudTransferState.Pending;
            task.Error = null;
            resumed++;
            _ = RunAsync(task);
        }
        if (resumed > 0) Raise(nameof(SummaryText));
        return resumed;
    }

    public void Cancel(CloudTransferTask task)
    {
        if (_cancellations.TryGetValue(task.Id, out var cancellation)) cancellation.Cancel();
        else if (task.State == CloudTransferState.Pending) { task.State = CloudTransferState.Cancelled; Persist(task); }
    }

    public void CancelAll()
    {
        foreach (var task in Tasks.Where(task => task.CanCancel).ToList()) Cancel(task);
    }

    public void ClearFinished()
    {
        foreach (var task in Tasks.Where(task => task.State == CloudTransferState.Completed).ToList())
        {
            Tasks.Remove(task);
            _ = _journal.RemoveAsync(task.Id);
        }
        Raise(nameof(HasTasks)); Raise(nameof(HasCompletedTasks)); Raise(nameof(SummaryText)); Raise(nameof(OverallPercentage));
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
        // remote-relative path. For a download both collapse to the remote file name.
        var stagingPath = task.Direction == CloudTransferDirection.Upload ? Path.GetFileName(task.LocalPath) : CloudTransferTask.NameOf(task.RemotePath);
        var destination = task.Direction == CloudTransferDirection.Upload ? task.RemotePath : stagingPath;
        try
        {
            task.State = CloudTransferState.Pending;
            task.CompletedBytes = 0;
            task.BytesPerSecond = 0;
            task.Error = null;
            Persist(task);
            await _slots.WaitAsync(ct);
            slotAcquired = true;
            ActiveCount++;
            task.State = CloudTransferState.Running;

            var client = await AppServices.Current.RcloneHost.GetClientAsync(ct);
            var localDirectory = Path.GetDirectoryName(task.LocalPath) ?? throw new InvalidOperationException($"本地路径缺少目录：{task.LocalPath}");
            Directory.CreateDirectory(localDirectory);

            IEndpoint source;
            if (task.Direction == CloudTransferDirection.Upload)
            {
                if (!File.Exists(task.LocalPath)) throw new FileNotFoundException("要上传的本地文件已不存在。", task.LocalPath);
                task.TotalBytes = new FileInfo(task.LocalPath).Length;
                source = new LocalEndpoint(localDirectory);
                target = EndpointFactory.CreateWithClient(task.RemoteUri, client);
            }
            else
            {
                source = EndpointFactory.CreateWithClient(task.RemoteUri, client);
                target = new LocalEndpoint(localDirectory);
            }

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

            // Passing no resume candidates lets TransferResume search a local target itself.
            var temporary = await EndpointTransfer.CopyToStagingAsync(source, target, stagingPath, null, task.Id,
                task.TotalBytes, ActiveCount, progress, EnsureParentAsync(target, destination), ResumeMinimumBytes, ct);
            task.CompletedBytes = task.TotalBytes;
            await EndpointTransfer.PublishAsync(target, temporary, destination, overwrite: true, ct);
            committed = true;
            task.State = CloudTransferState.Completed;
            task.BytesPerSecond = 0;
            await _journal.RemoveAsync(task.Id);
        }
        catch (OperationCanceledException)
        {
            task.State = CloudTransferState.Cancelled;
            task.BytesPerSecond = 0;
            Persist(task);
        }
        catch (Exception ex)
        {
            task.State = CloudTransferState.Failed;
            task.BytesPerSecond = 0;
            task.Error = RcloneUiError.Describe(ex, "cloud-transfer");
            Persist(task);
        }
        finally
        {
            // Mirrors the executor: local staging survives so a cancelled download can resume, while a
            // remote staging object is removed because rclone cannot resume it across runs.
            if (!committed && target is not null && target is not LocalEndpoint)
            {
                try { await EndpointTransfer.DiscardStagingAsync(target, stagingPath); }
                catch { /* preserve the original failure */ }
            }
            if (slotAcquired)
            {
                ActiveCount = Math.Max(0, ActiveCount - 1);
                _slots.Release();
            }
            _cancellations.Remove(task.Id);
            cancellation.Dispose();
            Raise(nameof(HasCompletedTasks)); Raise(nameof(SummaryText));
            lock (_completions)
            {
                if (_completions.Remove(task.Id, out var completion)) completion.TrySetResult(task.State == CloudTransferState.Completed);
            }
        }
    }

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
        var record = new CloudTransferRecord(task.Id, task.Direction, task.RemoteUri, task.RemotePath, task.LocalPath, task.TotalBytes, task.State, task.Error);
        _ = SaveIgnoringFailureAsync(record);
    }

    /// <summary>The journal is a convenience for resume; a failed write must never fail a transfer.</summary>
    private async Task SaveIgnoringFailureAsync(CloudTransferRecord record)
    {
        try { await _journal.SaveAsync(record); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
    string? Error);

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
