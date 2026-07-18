namespace FengSync.Core;

/// <summary>Runs a batch job as independent Profile tasks in parallel.</summary>
public sealed class BatchRunner
{
    private readonly int _maxConcurrency;
    public BatchRunner(int maxConcurrency = 3) => _maxConcurrency = maxConcurrency;

    public async Task<IReadOnlyList<ProfileRunResult>> RunAsync(IEnumerable<SyncProfile> profiles, CancellationToken ct = default)
    {
        var selected = profiles.Where(x => x.Enabled).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("批处理作业没有启用的 Profile。");
        var results = await new Automation.BatchScheduler(_maxConcurrency).RunAsync(selected.Select(profile => (Func<CancellationToken, Task<ProfileRunResult>>)(token => new ProfileRunner().RunAsync(profile, ct: token))), ct);
        return results.Where(x => x.Succeeded).Select(x => x.Value!).ToList();
    }
}
