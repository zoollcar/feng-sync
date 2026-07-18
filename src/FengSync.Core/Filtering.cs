using System.Text.RegularExpressions;
using System.Text.Json;
using System.Collections.Concurrent;

namespace FengSync.Core;

public enum FilterRuleKind { Include, Exclude }

/// <summary>A single, ordered filtering rule. Later matching rules intentionally override earlier rules.</summary>
public sealed record FilterRule(
    FilterRuleKind Kind,
    string Pattern,
    string? Comment = null,
    bool Enabled = true,
    long? MinimumSizeBytes = null,
    long? MaximumSizeBytes = null,
    DateTimeOffset? ModifiedAfter = null,
    DateTimeOffset? ModifiedBefore = null,
    bool? Hidden = null,
    bool? SymbolicLink = null);

public sealed record FilterEntryAttributes(long? Size = null, DateTimeOffset? ModifiedUtc = null, bool IsHidden = false, bool IsSymbolicLink = false);
public sealed record FilterDecision(bool Included, FilterRule? MatchedRule, string Reason);

/// <summary>Shared matcher used by editors, planners and scan adapters so a rule preview cannot disagree with a run.</summary>
public sealed class FilterEngine(IEnumerable<FilterRule>? rules = null)
{
    private readonly IReadOnlyList<FilterRule> _rules = rules?.ToList() ?? [];
    public IReadOnlyList<FilterRule> Rules => _rules;
    public FilterDecision Evaluate(string relativePath, FilterEntryAttributes? attributes = null)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Replace('\\', '/').Split('/').Any(x => x is "." or ".."))
            return new(false, null, "路径不是安全的相对路径。");
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var included = true; FilterRule? last = null;
        foreach (var rule in _rules.Where(x => x.Enabled))
            if (Matches(rule, normalized, attributes)) { included = rule.Kind == FilterRuleKind.Include; last = rule; }
        return new(included, last, last is null ? "没有匹配规则，使用默认包含。" : last.Comment ?? $"匹配{(last.Kind == FilterRuleKind.Include ? "包含" : "排除")}规则：{last.Pattern}");
    }
    private static bool Matches(FilterRule rule, string path, FilterEntryAttributes? attributes)
    {
        if (!Regex.IsMatch(path, Glob(rule.Pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        if (rule.MinimumSizeBytes is not null && (attributes?.Size is null || attributes.Size < rule.MinimumSizeBytes)) return false;
        if (rule.MaximumSizeBytes is not null && (attributes?.Size is null || attributes.Size > rule.MaximumSizeBytes)) return false;
        if (rule.ModifiedAfter is not null && (attributes?.ModifiedUtc is null || attributes.ModifiedUtc < rule.ModifiedAfter)) return false;
        if (rule.ModifiedBefore is not null && (attributes?.ModifiedUtc is null || attributes.ModifiedUtc > rule.ModifiedBefore)) return false;
        if (rule.Hidden is not null && attributes?.IsHidden != rule.Hidden) return false;
        return rule.SymbolicLink is null || attributes?.IsSymbolicLink == rule.SymbolicLink;
    }
    private static string Glob(string pattern)
    {
        pattern = pattern.Trim().Replace('\\', '/').TrimStart('/');
        // A bare filename is intentionally recursive, matching the familiar .gitignore/
        // file-manager expectation. A slash makes the expression root-relative.
        var prefix = pattern.Contains('/') ? "" : "(?:.*/)?";
        var escaped = Regex.Escape(pattern)
            // A directory rule also filters the directory entry itself, avoiding creation
            // of an otherwise empty excluded directory during a mirror operation.
            .Replace("/\\*\\*", "(?:/.*)?")
            // **/ matches zero or more directories, including none for a root file.
            .Replace("\\*\\*/", "(?:.*/)?")
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*")
            .Replace("\\?", "[^/]");
        return "^" + prefix + escaped + "$";
    }
}

public enum RunOutcome { Succeeded, PartialSuccess, Failed, Cancelled }
public sealed record RunHistoryEntry(string ProfileId, RunOutcome Outcome, DateTimeOffset CompletedUtc, int Planned, int Succeeded, int Failed, long TransferredBytes, string? Detail = null, Guid? RunId = null);

/// <summary>Small durable, queryable history store. It is deliberately independent of incomplete-operation journals.</summary>
public sealed class RunHistoryRepository
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly int _maximumEntries;
    public RunHistoryRepository(string? path = null, int maximumEntries = 500)
    {
        if (maximumEntries < 1) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        _path = path ?? Path.Combine(AppDataPaths.Root, "run-history.json");
        _maximumEntries = maximumEntries;
    }
    public async Task AppendAsync(RunHistoryEntry entry, CancellationToken ct = default)
    {
        var writeLock = WriteLocks.GetOrAdd(Path.GetFullPath(_path), _ => new SemaphoreSlim(1, 1));
        await writeLock.WaitAsync(ct);
        try
        {
            var entries = (await ReadAsync(ct)).ToList(); entries.Add(entry with { RunId = entry.RunId ?? Guid.NewGuid() });
            entries = entries.OrderByDescending(x => x.CompletedUtc).Take(_maximumEntries).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!); var temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(entries), ct); File.Move(temp, _path, true);
        }
        finally { writeLock.Release(); }
    }
    public async Task<IReadOnlyList<RunHistoryEntry>> QueryAsync(string? profileId = null, RunOutcome? outcome = null, DateTimeOffset? since = null, CancellationToken ct = default)
        => (await ReadAsync(ct)).Where(x => (profileId is null || x.ProfileId == profileId) && (outcome is null || x.Outcome == outcome) && (since is null || x.CompletedUtc >= since)).OrderByDescending(x => x.CompletedUtc).ToList();
    private async Task<IReadOnlyList<RunHistoryEntry>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<RunHistoryEntry>>(stream, cancellationToken: ct) ?? [];
    }
}
