namespace FengSync.Core;

/// <summary>Runs a batch job as independent Profile tasks in parallel.</summary>
public sealed class BatchRunner
{
    public async Task<IReadOnlyList<ProfileRunResult>> RunAsync(IEnumerable<SyncProfile> profiles, CancellationToken ct = default)
    {
        var selected = profiles.Where(x => x.Enabled).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("批处理作业没有启用的 Profile。");
        return await Task.WhenAll(selected.Select(profile => new ProfileRunner().RunAsync(profile, ct: ct)));
    }
}
