namespace FengSync.Core;
public sealed class LocalExecutor
{
    public async Task ExecuteAsync(SyncPlan plan, LocalEndpoint left, LocalEndpoint right, IProgress<string>? progress = null, CancellationToken ct = default, TaskJournalStore? journals = null, int maxConcurrentCopies = 3, bool verifyCopies = true, VersioningPolicy? versioning = null)
    {
        if (!plan.CanExecute) throw new InvalidOperationException("计划含未裁决的冲突、或未选择任何动作。");
        if (maxConcurrentCopies is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maxConcurrentCopies), "并发数必须在 1 到 32 之间。");
        var selected = plan.Operations.Where(x => x.Selected).ToList();
        var state = selected.ToDictionary(x => x.OperationId, x => new JournalItem(x.OperationId, x.Path, x.Kind, JournalState.Pending)); var jobId = Guid.NewGuid();
        using var stateLock = new SemaphoreSlim(1, 1);
        async Task Save() { if (journals is not null) await journals.SaveAsync(new(jobId, DateTimeOffset.UtcNow, state.Values.ToList()), ct); }
        async Task Mark(SyncOperation op, JournalState value, string? error = null) { await stateLock.WaitAsync(ct); try { state[op.OperationId] = new(op.OperationId, op.Path, op.Kind, value, error); await Save(); } finally { stateLock.Release(); } }
        await stateLock.WaitAsync(ct); try { await Save(); } finally { stateLock.Release(); }
        try
        {
            foreach (var op in selected.Where(x => x.Kind is OperationKind.CreateLeftDirectory or OperationKind.CreateRightDirectory)) { await Mark(op, JournalState.Running); MakeDir(op, left, right); await Mark(op, JournalState.Committed); }
            using (var transferGate = new SemaphoreSlim(maxConcurrentCopies, maxConcurrentCopies))
                await Task.WhenAll(selected.Where(x => x.Kind is OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft).Select(async op => { await transferGate.WaitAsync(ct); try { ct.ThrowIfCancellationRequested(); await Mark(op, JournalState.Running); await CopyAsync(op, left, right, ct, verifyCopies); await Mark(op, JournalState.Transferred); if (verifyCopies) await Mark(op, JournalState.Verified); await Mark(op, JournalState.Committed); progress?.Report(op.Path); } finally { transferGate.Release(); } }));
            // Deletes only begin after every copy has committed.
            foreach (var op in selected.Where(x => x.Kind is OperationKind.DeleteLeft or OperationKind.DeleteRight).OrderByDescending(x => x.Path.Length)) { await Mark(op, JournalState.Running); Delete(op, left, right, versioning); await Mark(op, JournalState.Committed); }
        }
        catch (OperationCanceledException) { await stateLock.WaitAsync(CancellationToken.None); try { foreach (var op in selected.Where(x => state[x.OperationId].State is JournalState.Pending or JournalState.Running)) state[op.OperationId] = state[op.OperationId] with { State = JournalState.Cancelled }; await Save(); } finally { stateLock.Release(); } throw; }
        catch (Exception ex) { await stateLock.WaitAsync(CancellationToken.None); try { var running = state.Values.FirstOrDefault(x => x.State == JournalState.Running); if (running is not null) state[running.OperationId] = running with { State = JournalState.Failed, Error = ex.Message }; await Save(); } finally { stateLock.Release(); } throw; }
    }
    private static void MakeDir(SyncOperation op, LocalEndpoint l, LocalEndpoint r) => Directory.CreateDirectory((op.Kind == OperationKind.CreateLeftDirectory ? l : r).PhysicalPath(op.Path));
    private static async Task CopyAsync(SyncOperation op, LocalEndpoint l, LocalEndpoint r, CancellationToken ct, bool verify)
    {
        var source = (op.Kind == OperationKind.CopyLeftToRight ? l : r).PhysicalPath(op.Path); var target = (op.Kind == OperationKind.CopyLeftToRight ? r : l).PhysicalPath(op.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); var temp = target + ".fengsync-" + Guid.NewGuid().ToString("N") + ".partial";
        await using (var input = File.OpenRead(source)) await using (var output = File.Create(temp)) await input.CopyToAsync(output, ct);
        if (verify && new FileInfo(source).Length != new FileInfo(temp).Length) { File.Delete(temp); throw new IOException("传输验证失败：" + op.Path); }
        File.Move(temp, target, true); File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
    }
    private static void Delete(SyncOperation op, LocalEndpoint l, LocalEndpoint r, VersioningPolicy? versioning)
    {
        var p = (op.Kind == OperationKind.DeleteLeft ? l : r).PhysicalPath(op.Path);
        if (File.Exists(p))
        {
            if (versioning?.Mode == VersioningMode.TimestampedArchive)
            {
                if (string.IsNullOrWhiteSpace(versioning.ArchiveDirectory)) throw new InvalidOperationException("版本保留需要设置归档目录。");
                var archived = Path.Combine(versioning.ArchiveDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"), op.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(archived)!); File.Move(p, archived, true);
            }
            else File.Delete(p);
        }
        else if (Directory.Exists(p) && !Directory.EnumerateFileSystemEntries(p).Any()) Directory.Delete(p);
    }
}
