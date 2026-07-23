namespace FengSync.Core;

using System.Text.Json;

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
