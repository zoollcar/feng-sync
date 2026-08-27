namespace FengSync.Core.Execution;

/// <summary>
/// Keeps operation-state updates in memory and periodically persists one complete
/// recovery snapshot. The executor's workers never wait for journal I/O.
/// </summary>
internal sealed class JournalCheckpointWriter : IAsyncDisposable
{
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromMilliseconds(500);
    private readonly TaskJournalStore _store;
    private readonly Guid _runId;
    private readonly IReadOnlyList<string> _endpointRoots;
    private readonly Dictionary<Guid, JournalItem> _states;
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _pump;
    private long _version;
    private long _persistedVersion;

    private JournalCheckpointWriter(TaskJournalStore store, Guid runId, IReadOnlyList<string> endpointRoots,
        Dictionary<Guid, JournalItem> initialStates)
    {
        _store = store;
        _runId = runId;
        _endpointRoots = endpointRoots;
        _states = initialStates;
        _pump = RunAsync();
    }

    public static async Task<JournalCheckpointWriter> CreateAsync(TaskJournalStore store, Guid runId,
        IReadOnlyList<string> endpointRoots, Dictionary<Guid, JournalItem> initialStates)
    {
        // Persist the complete pending plan before endpoint mutations begin, so
        // recovery still has a durable record if the process exits immediately.
        await store.SaveAsync(new(runId, DateTimeOffset.UtcNow, initialStates.Values.ToList(), endpointRoots), CancellationToken.None);
        return new(store, runId, endpointRoots, initialStates);
    }

    public void Update(JournalItem item)
    {
        lock (_stateLock)
        {
            _states[item.OperationId] = item;
            _version++;
        }
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(CheckpointInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token))
            {
                try { await FlushAsync(); }
                catch
                {
                    // Keep the snapshot dirty and retry on the next tick. Dispose
                    // performs a final flush and surfaces a persistent I/O error.
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    private async Task FlushAsync()
    {
        SyncJournal snapshot;
        long snapshotVersion;
        lock (_stateLock)
        {
            if (_version == _persistedVersion) return;
            snapshotVersion = _version;
            snapshot = new(_runId, DateTimeOffset.UtcNow, _states.Values.ToList(), _endpointRoots);
        }

        await _store.SaveAsync(snapshot, CancellationToken.None);
        lock (_stateLock)
            _persistedVersion = Math.Max(_persistedVersion, snapshotVersion);
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        await _pump;
        try { await FlushAsync(); }
        finally { _stopping.Dispose(); }
    }
}
