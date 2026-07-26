namespace FengSync.Core;

using System.Text.Json;
using FengSync.Core.Scanning;

public sealed record EndpointIdentity(string Provider, string Root, string StableId)
{
    // Remote endpoint Profile.Id is an in-memory construction detail.  The rclone remote
    // name is the durable identity across application restarts; using the random Id would
    // make every remote comparison look like a different endpoint.
    public static EndpointIdentity From(IEndpoint endpoint) => new(endpoint.Profile.Type.ToString(), endpoint.Profile.Root,
        endpoint.Profile.Identity ?? endpoint.Profile.Remote ?? endpoint.Profile.Id.ToString("N"));
}
public enum BaselineTransactionState { Started, Staging, Committed, RolledBack, NeedsRecovery }
public sealed record BaselineTransaction(Guid Id, EndpointIdentity Left, EndpointIdentity Right, DateTimeOffset StartedUtc, BaselineTransactionState State = BaselineTransactionState.Started, IReadOnlyList<string>? CommittedPaths = null)
{
    public BaselineTransaction RecordCommitted(string path) => this with { State = BaselineTransactionState.Staging, CommittedPaths = (CommittedPaths ?? []).Append(path).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
    public BaselineTransaction Complete() => this with { State = BaselineTransactionState.Committed };
    public BaselineTransaction Rollback(bool needsRecovery = false) => this with { State = needsRecovery ? BaselineTransactionState.NeedsRecovery : BaselineTransactionState.RolledBack };
}

/// <summary>Repository for the endpoint-neutral paired SQLite state archive.</summary>
public sealed class BaselineRepository
{
    private readonly EndpointBaselineStore _store;
    private readonly BaselineTransactionStore _transactions;
    public BaselineRepository(EndpointBaselineStore? store = null, BaselineTransactionStore? transactionStore = null)
        => (_store, _transactions) = (store ?? new EndpointBaselineStore(), transactionStore ?? new BaselineTransactionStore());
    public Task<IReadOnlyList<BaselineEntry>?> LoadAsync(LocalEndpoint left, LocalEndpoint right, CancellationToken ct = default) => _store.LoadAsync(left, right, ct);
    public Task<IReadOnlyList<BaselineEntry>?> LoadAsync(IEndpoint left, IEndpoint right, CancellationToken ct = default) => _store.LoadAsync(left, right, ct);
    public string? LastLoadWarning => _store.LastLoadWarning;
    public BaselineTransaction Begin(IEndpoint left, IEndpoint right) => new(Guid.NewGuid(), EndpointIdentity.From(left), EndpointIdentity.From(right), DateTimeOffset.UtcNow);
    public async Task<BaselineTransaction> BeginAsync(IEndpoint left, IEndpoint right, CancellationToken ct = default)
    {
        var transaction = Begin(left, right);
        await _transactions.SaveAsync(transaction, ct);
        return transaction;
    }
    public Task SaveAsync(BaselineTransaction transaction, CancellationToken ct = default) => _transactions.SaveAsync(transaction, ct);
    public async Task<BaselineTransaction> CommitAsync(BaselineTransaction transaction, LocalEndpoint left, LocalEndpoint right, bool allOperationsSucceeded, CancellationToken ct = default)
        => await CommitCoreAsync(transaction, left, right, allOperationsSucceeded, () => _store.CommitAsync(left, right, ct), ct);
    public async Task<BaselineTransaction> CommitAsync(BaselineTransaction transaction, IEndpoint left, IEndpoint right, bool allOperationsSucceeded, CancellationToken ct = default)
        => await CommitCoreAsync(transaction, left, right, allOperationsSucceeded, () => _store.CommitAsync(left, right, ct), ct);
    public Task CommitFromSnapshotAsync(IEndpoint left, IEndpoint right, ComparisonSnapshot snapshot, CancellationToken ct = default)
        => _store.CommitFromSnapshotAsync(left, right, snapshot, ct);
    public Task CommitFromResultsAsync(IEndpoint left, IEndpoint right, BaselineCommitInput input, CancellationToken ct = default)
        => _store.CommitFromResultsAsync(left, right, input, ct);
    private async Task<BaselineTransaction> CommitCoreAsync(BaselineTransaction transaction, IEndpoint left, IEndpoint right, bool allOperationsSucceeded, Func<Task> publish, CancellationToken ct)
    {
        if (!allOperationsSucceeded)
        {
            var failed = transaction.Rollback(needsRecovery: true);
            await _transactions.SaveAsync(failed, ct);
            return failed;
        }
        if (transaction.Left != EndpointIdentity.From(left) || transaction.Right != EndpointIdentity.From(right)) throw new InvalidOperationException("端点身份已变化，不能提交基线。");
        await publish();
        var committed = transaction.Complete();
        await _transactions.RemoveAsync(committed.Id, ct);
        return committed;
    }
}

/// <summary>Durable transaction intents are kept separately from the committed baseline itself.</summary>
public sealed class BaselineTransactionStore(string? root = null)
{
    private readonly string _root = root ?? Path.Combine(AppDataPaths.Root, "baseline-transactions");
    public async Task SaveAsync(BaselineTransaction transaction, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, transaction.Id + ".json"); var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(transaction), ct);
        File.Move(temp, path, true);
    }
    public async Task<IReadOnlyList<BaselineTransaction>> LoadRecoveryRequiredAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_root)) return [];
        var items = new List<BaselineTransaction>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
        {
            var transaction = JsonSerializer.Deserialize<BaselineTransaction>(await File.ReadAllTextAsync(path, ct));
            if (transaction?.State is BaselineTransactionState.Started or BaselineTransactionState.Staging or BaselineTransactionState.NeedsRecovery)
                items.Add(transaction);
        }
        return items.OrderByDescending(x => x.StartedUtc).ToList();
    }
    public Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = Path.Combine(_root, id + ".json");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Result-driven baseline commit input. The next two-way state is derived from
/// the verified post-publish fingerprints captured by the executor rather than
/// from the pre-sync comparison snapshot, so a successful copy cannot leave
/// the target recorded with the old (missing or stale) fingerprint.
/// </summary>
public sealed record BaselineCommitInput(
    ComparisonSnapshot Snapshot,
    IReadOnlyDictionary<Guid, OperationRunResult> Results,
    BaselineTransaction Transaction);

/// <summary>
/// Computes the next paired baseline from the snapshot, the previous baseline
/// and the operation results. Rules follow 05-baseline-state.md:
///   * committed copy => both sides reflect the verified target fingerprint
///   * committed delete => remove the path or record both sides as missing
///   * failed, cancelled, conflict, unselected or filtered => keep old baseline
///   * neither side has the path and old baseline had it => drop the entry
/// </summary>
public static class BaselineStateBuilder
{
    public static IReadOnlyList<BaselineEntry> BuildNextState(BaselineCommitInput input, IReadOnlyList<BaselineEntry>? previousBaseline)
    {
        // Use the common plan semantics here too. Otherwise a successful run
        // against a case-sensitive endpoint would collapse A.txt and a.txt while
        // publishing the next baseline.
        var leftPaths = input.Snapshot.Left.Paths;
        var rightPaths = input.Snapshot.Right.Paths;
        var paths = new EndpointPathSemantics(leftPaths.CaseSensitive || rightPaths.CaseSensitive, leftPaths.UnicodeNormalization, '/');
        var pathComparer = paths.CreateComparer();
        var byPath = previousBaseline?.ToDictionary(x => x.Path, pathComparer)
            ?? new Dictionary<string, BaselineEntry>(pathComparer);

        var leftSnapshot = input.Snapshot.Left.ByPath;
        var rightSnapshot = input.Snapshot.Right.ByPath;
        var plan = input.Snapshot.Plan ?? throw new InvalidOperationException("比较快照缺少已执行计划。");
        var opIndex = plan.Operations.ToDictionary(x => x.OperationId);
        var touchedPaths = new HashSet<string>(pathComparer);

        // 1. Update entries the operations touched. Unselected, conflict, failed
        //    and filtered paths are not in input.Results so they fall through
        //    and keep their previous baseline untouched.
        foreach (var (id, result) in input.Results)
        {
            if (result.Stage != TransferStage.Committed || !result.Published) continue;
            if (!opIndex.TryGetValue(id, out var op)) continue;
            touchedPaths.Add(op.Path);
            if (op.Kind is OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft)
            {
                // After a successful copy both sides share the verified target
                // fingerprint; using the pre-sync snapshot would have left the
                // destination recorded with its old (missing/stale) state.
                var verified = BuildFileEntry(op.Path, result.TargetAfter);
                byPath[op.Path] = new BaselineEntry(op.Path, verified, verified);
            }
            else if (op.Kind == OperationKind.Move && op.Move is { } move)
            {
                // A move re-keys state; endpoint identities remain endpoint-local and
                // are taken from the post-operation snapshots where available.
                byPath.Remove(move.FromPath);
                var leftAfter = leftSnapshot.TryGetValue(move.ToPath, out var l) ? l : BuildFileEntry(move.ToPath, result.TargetAfter);
                var rightAfter = rightSnapshot.TryGetValue(move.ToPath, out var r) ? r : BuildFileEntry(move.ToPath, result.TargetAfter);
                byPath[move.ToPath] = new BaselineEntry(move.ToPath, leftAfter, rightAfter);
                touchedPaths.Add(move.FromPath);
            }
            else if (op.Kind is OperationKind.DeleteLeft)
            {
                var rightEntry = rightSnapshot.TryGetValue(op.Path, out var r) ? r : null;
                byPath[op.Path] = new BaselineEntry(op.Path, null, rightEntry);
            }
            else if (op.Kind is OperationKind.DeleteRight)
            {
                var leftEntry = leftSnapshot.TryGetValue(op.Path, out var l) ? l : null;
                byPath[op.Path] = new BaselineEntry(op.Path, leftEntry, null);
            }
            else if (op.Kind is OperationKind.CreateLeftDirectory or OperationKind.CreateRightDirectory)
            {
                var left = leftSnapshot.TryGetValue(op.Path, out var l) ? l : new EntrySnapshot(op.Path, EntryKind.Directory, null);
                var right = rightSnapshot.TryGetValue(op.Path, out var r) ? r : new EntrySnapshot(op.Path, EntryKind.Directory, null);
                byPath[op.Path] = new BaselineEntry(op.Path, left, right);
            }
        }

        // 2. For paths the run did not touch, align with the snapshot if the
        //    snapshot proves both sides are now absent; otherwise keep the
        //    previous baseline so we never invent a deletion authority.
        var seen = new HashSet<string>(pathComparer);
        foreach (var path in leftSnapshot.Keys.Union(rightSnapshot.Keys, pathComparer))
        {
            seen.Add(path);
            var leftHas = leftSnapshot.ContainsKey(path);
            var rightHas = rightSnapshot.ContainsKey(path);
            byPath.TryGetValue(path, out var existing);
            if (!leftHas && !rightHas && existing is not null && !touchedPaths.Contains(path))
                byPath.Remove(path);
        }

        // 3. Drop baseline entries that reference paths neither side has ever
        //    seen and the run did not touch. This keeps the payload bounded.
        var stale = byPath.Keys.Where(p => !seen.Contains(p) && !touchedPaths.Contains(p)).ToList();
        foreach (var p in stale) byPath.Remove(p);

        return byPath.Values.OrderBy(x => paths.Canonicalize(x.Path), StringComparer.Ordinal).ToList();
    }

    private static EntrySnapshot? BuildFileEntry(string path, Fingerprint? targetAfter) =>
        targetAfter is null ? null : new EntrySnapshot(path, EntryKind.File, targetAfter);
}

public sealed record RecoveryItem(SyncJournal? Journal, BaselineTransaction? Transaction, string Detail)
{
    public IEnumerable<string> LocalEndpointRoots => (Journal?.EndpointRoots ?? Enumerable.Empty<string>())
        .Concat(Transaction is null ? Enumerable.Empty<string>() : new[] { Transaction.Left.Root, Transaction.Right.Root })
        .Where(x => !x.Contains("://", StringComparison.Ordinal));
}

public sealed class RecoveryCoordinator
{
    private readonly TaskJournalStore _journals;
    private readonly BaselineTransactionStore _transactions;
    public RecoveryCoordinator(TaskJournalStore? journals = null, BaselineTransactionStore? transactions = null)
        => (_journals, _transactions) = (journals ?? new TaskJournalStore(), transactions ?? new BaselineTransactionStore());
    public async Task<IReadOnlyList<RecoveryItem>> FindRecoveryRequiredAsync(CancellationToken ct = default)
    {
        var journals = await _journals.LoadIncompleteAsync(ct);
        var transactions = await _transactions.LoadRecoveryRequiredAsync(ct);
        var journalItems = journals.Select(journal => new RecoveryItem(journal, null, Describe(journal)));
        var transactionItems = transactions.Select(transaction => new RecoveryItem(null, transaction,
            $"双向基线事务处于 {transaction.State}；正式基线未发布。已提交 {transaction.CommittedPaths?.Count ?? 0} 项。"));
        return journalItems.Concat(transactionItems).OrderByDescending(x => x.Journal?.CreatedUtc ?? x.Transaction!.StartedUtc).ToList();
    }
    public int RemoveSafeLocalTemporaryFiles(IEnumerable<RecoveryItem> items)
    {
        var roots = items.SelectMany(x => x.LocalEndpointRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return _journals.RemoveOrphanedPartialFiles(roots);
    }
    private static string Describe(SyncJournal journal)
    {
        var failures = journal.Items.Where(x => x.State is JournalState.Failed or JournalState.Cancelled).ToList();
        var detail = failures.FirstOrDefault()?.Error;
        return $"作业 {journal.JobId} 有 {journal.Items.Count(x => x.State is not JournalState.Committed)} 个未完成操作。" + (string.IsNullOrWhiteSpace(detail) ? "" : " " + detail);
    }
}
