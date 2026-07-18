namespace FengSync.Core.Automation;

/// <summary>Bounds batch concurrency and records each item's outcome without abandoning later items.</summary>
public sealed class BatchScheduler
{
    private readonly int _maxConcurrency;

    public BatchScheduler(int maxConcurrency)
    {
        if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        _maxConcurrency = maxConcurrency;
    }

    public async Task<IReadOnlyList<BatchItemResult<T>>> RunAsync<T>(IEnumerable<Func<CancellationToken, Task<T>>> jobs, CancellationToken ct = default)
    {
        var jobList = jobs.ToList();
        using var gate = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        var tasks = jobList.Select((job, index) => RunOneAsync(index, job, gate, ct));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task<BatchItemResult<T>> RunOneAsync<T>(int index, Func<CancellationToken, Task<T>> job, SemaphoreSlim gate, CancellationToken ct)
    {
        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new(index, default, new OperationCanceledException()); }

        try { return new(index, await job(ct).ConfigureAwait(false), null); }
        catch (Exception ex) { return new(index, default, ex); }
        finally { gate.Release(); }
    }
}

public sealed record BatchItemResult<T>(int Index, T? Value, Exception? Error)
{
    public bool Succeeded => Error is null;
}
