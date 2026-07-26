using System.Text.Json;

namespace FengSync.Core;
public enum JournalState { Pending, Running, Transferred, Verified, Committed, Failed, Cancelled }
public sealed record JournalItem(Guid OperationId, string Path, OperationKind Kind, JournalState State, string? Error = null);
/// <param name="EndpointRoots">Optional local roots, persisted solely so recovery can remove recognized temporary files safely.</param>
public sealed record SyncJournal(Guid JobId, DateTimeOffset CreatedUtc, IReadOnlyList<JournalItem> Items, IReadOnlyList<string>? EndpointRoots = null);

/// <summary>Crash-recovery journal held locally; committed endpoint databases are never used as an in-progress transaction log.</summary>
public sealed class TaskJournalStore(string? root = null)
{
    private readonly string _root = root ?? Path.Combine(AppDataPaths.Root, "jobs");
    public async Task SaveAsync(SyncJournal journal, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root); var target = Path.Combine(_root, journal.JobId + ".json"); var temp = target + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(journal), ct);
        // On Windows a diagnostics reader can briefly open the current journal
        // without delete sharing. Replacing it must wait for that transient read
        // lock instead of turning an otherwise successful transfer into a failed
        // operation.
        const int attempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            try { File.Move(temp, target, true); break; }
            catch (Exception ex) when (attempt < attempts && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(25, ct);
            }
        }
    }
    public async Task<IReadOnlyList<SyncJournal>> LoadIncompleteAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_root)) return [];
        var journals = new List<SyncJournal>(); foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
        { var item = JsonSerializer.Deserialize<SyncJournal>(await File.ReadAllTextAsync(path, ct)); if (item is not null && item.Items.Any(x => x.State is not JournalState.Committed)) journals.Add(item); }
        return journals;
    }
    public int RemoveOrphanedPartialFiles(params string[] endpointRoots)
    {
        var active = Directory.Exists(_root) ? Directory.EnumerateFiles(_root, "*.json").SelectMany(file => JsonSerializer.Deserialize<SyncJournal>(File.ReadAllText(file))?.Items ?? []).Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        var removed = 0; foreach (var root in endpointRoots.Where(Directory.Exists)) foreach (var file in Directory.EnumerateFiles(root, "*.fengsync-*.partial", SearchOption.AllDirectories))
        { var relative = Path.GetRelativePath(root, file).Replace('\\', '/'); if (!active.Contains(relative)) { File.Delete(file); removed++; } }
        return removed;
    }
}
