using System.Text.Json;
using System.Text.RegularExpressions;

namespace FengSync.Core.SftpServer;

public sealed record SftpAuditRecord(string Account, string SourceAddress, string Action, string? VirtualPath, string Outcome, DateTimeOffset TimestampUtc);

/// <summary>Append-only structured audit trail. It intentionally strips credentials from outcomes.</summary>
public sealed class SftpAuditLog(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(AppDataPaths.Root, "sftp", "audit.jsonl");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task AppendAsync(SftpAuditRecord record, CancellationToken ct = default)
    {
        var safe = record with { Outcome = Sanitize(record.Outcome), VirtualPath = Sanitize(record.VirtualPath ?? "") };
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await File.AppendAllTextAsync(_path, JsonSerializer.Serialize(safe) + Environment.NewLine, ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    internal static string Sanitize(string value) => Regex.Replace(value, "(?i)(password|pass|credential)\\s*[=:]\\s*[^\\s,;]+", "credential=<redacted>");
}
