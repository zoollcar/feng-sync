using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using FengSync.Core;

namespace FengSync.Services;

/// <summary>
/// One archived transfer. It is the same shape as the live row plus the two things only an archive needs: when
/// it was filed, and the full diagnostic behind the one-line error.
/// </summary>
public sealed record CloudTransferHistoryEntry(
    Guid Id,
    CloudTransferDirection Direction,
    string RemoteUri,
    string RemotePath,
    string LocalPath,
    long TotalBytes,
    CloudTransferState State,
    DateTimeOffset ArchivedUtc,
    int Attempt = 1,
    string? Error = null,
    // Optional so an archive written before the field existed still deserializes.
    string? ErrorDetail = null,
    string? TargetUri = null,
    string? TargetPath = null)
{
    public static CloudTransferHistoryEntry From(CloudTransferTask task, DateTimeOffset archivedUtc) =>
        new(task.Id, task.Direction, task.RemoteUri, task.RemotePath, task.LocalPath, task.TotalBytes, task.State,
            archivedUtc, task.Attempt, task.Error, task.ErrorDetail, task.TargetUri, task.TargetPath);

    /// <summary>Whether the transfer ended well. Cancelled and paused are the user's own doing, not failures.</summary>
    public bool Succeeded => State == CloudTransferState.Completed;
}

/// <summary>
/// Append-only archive of transfers that have left the queue.
/// </summary>
/// <remarks>
/// <para>
/// It exists because "清除已完成" is a destructive action on the only record of what happened: the queue's
/// journal drops a task's entry the moment it succeeds, so clearing the list used to leave no trace that the
/// file was ever moved. The archive is written <b>before</b> the row is removed, and it is a single bounded
/// JSON file rather than a file per task — it is a history to read, not state to resume from.
/// </para>
/// <para>
/// Modelled on <c>RunHistoryRepository</c>: a per-path write lock, newest-first ordering, a cap, and a
/// corrupt or unreadable file read as an empty history so the window still opens.
/// </para>
/// </remarks>
public sealed class CloudTransferHistoryRepository(string? path = null, int maximumEntries = 500)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path = path ?? System.IO.Path.Combine(AppDataPaths.Root, "cloud-transfer-history.json");

    public string Path => _path;

    public async Task AppendAsync(IEnumerable<CloudTransferHistoryEntry> entries, CancellationToken ct = default)
    {
        var batch = entries.ToList();
        if (batch.Count == 0) return;
        var writeLock = WriteLocks.GetOrAdd(System.IO.Path.GetFullPath(_path), _ => new SemaphoreSlim(1, 1));
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = (await ReadAsync(ct).ConfigureAwait(false)).ToList();
            all.AddRange(batch);
            all = [.. all.OrderByDescending(x => x.ArchivedUtc).Take(Math.Max(1, maximumEntries))];
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(all), ct).ConfigureAwait(false);
            File.Move(temporary, _path, true);
        }
        finally { writeLock.Release(); }
    }

    public async Task<IReadOnlyList<CloudTransferHistoryEntry>> QueryAsync(CancellationToken ct = default) =>
        [.. (await ReadAsync(ct).ConfigureAwait(false)).OrderByDescending(x => x.ArchivedUtc)];

    /// <summary>Empties the archive. Only ever called because the user asked for it.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        var writeLock = WriteLocks.GetOrAdd(System.IO.Path.GetFullPath(_path), _ => new SemaphoreSlim(1, 1));
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { writeLock.Release(); }
    }

    private async Task<IReadOnlyList<CloudTransferHistoryEntry>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            await using var stream = File.OpenRead(_path);
            // An empty file is what a crash between create and write leaves behind; it must read as "nothing
            // archived" rather than throwing where the window would see it.
            if (stream.Length == 0) return [];
            return await JsonSerializer.DeserializeAsync<List<CloudTransferHistoryEntry>>(stream, cancellationToken: ct).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return []; }
    }
}

/// <summary>
/// The wording rules for the archive, split out because they are pure and therefore the cheapest thing in this
/// feature to pin down with a test.
/// </summary>
public static class CloudTransferHistorySummary
{
    /// <summary>One line per archived transfer: what it was, where it went, and how it ended.</summary>
    public static string Describe(CloudTransferHistoryEntry entry)
    {
        var direction = entry.Direction switch
        {
            CloudTransferDirection.Upload => "上传",
            CloudTransferDirection.Download => "下载",
            _ => "复制"
        };
        var name = entry.Direction == CloudTransferDirection.Upload
            ? System.IO.Path.GetFileName(entry.LocalPath)
            : CloudTransferTask.NameOf(entry.RemotePath);
        var outcome = entry.State switch
        {
            CloudTransferState.Completed => "已完成",
            CloudTransferState.Failed => "失败",
            CloudTransferState.Cancelled => "已取消",
            CloudTransferState.Paused => "已暂停",
            _ => entry.State.ToString()
        };
        var where = entry.Direction == CloudTransferDirection.Copy
            ? $"{entry.RemoteUri}/{entry.RemotePath} → {entry.TargetUri}/{entry.TargetPath}"
            : $"{entry.RemoteUri}/{entry.RemotePath}";
        return $"{entry.ArchivedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {direction}  {name}  {outcome}  {where}"
            + (entry.Attempt > 1 ? $"（第 {entry.Attempt} 次尝试）" : "");
    }

    /// <summary>The line above the list. Says what is not there rather than only what is.</summary>
    public static string DescribeAll(IReadOnlyList<CloudTransferHistoryEntry> entries) => entries.Count == 0
        ? "还没有归档的传输。清除已完成时会把它们记到这里。"
        : $"共 {entries.Count} 条归档记录，其中成功 {entries.Count(entry => entry.Succeeded)} 条、"
            + $"失败 {entries.Count(entry => entry.State == CloudTransferState.Failed)} 条。";
}
