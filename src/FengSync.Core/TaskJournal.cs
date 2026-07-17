using System.Text.Json;

namespace FengSync.Core;
public enum JournalState { Pending, Running, Transferred, Verified, Committed, Failed, Cancelled }
public sealed record JournalItem(Guid OperationId, string Path, OperationKind Kind, JournalState State, string? Error = null);
public sealed record SyncJournal(Guid JobId, DateTimeOffset CreatedUtc, IReadOnlyList<JournalItem> Items);

/// <summary>Crash-recovery journal held locally; committed endpoint databases are never used as an in-progress transaction log.</summary>
public sealed class TaskJournalStore(string? root = null)
{
    private readonly string _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FengSync", "jobs");
    public async Task SaveAsync(SyncJournal journal, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root); var target = Path.Combine(_root, journal.JobId + ".json"); var temp = target + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(journal), ct); File.Move(temp, target, true);
    }
    public async Task<IReadOnlyList<SyncJournal>> LoadIncompleteAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_root)) return [];
        var journals = new List<SyncJournal>(); foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
        { var item = JsonSerializer.Deserialize<SyncJournal>(await File.ReadAllTextAsync(path, ct)); if (item is not null && item.Items.Any(x => x.State is not JournalState.Committed and not JournalState.Cancelled)) journals.Add(item); }
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
