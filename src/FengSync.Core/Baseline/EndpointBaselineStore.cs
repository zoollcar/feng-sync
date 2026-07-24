using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FengSync.Core.Scanning;
using Microsoft.Data.Sqlite;

namespace FengSync.Core;

public enum SessionRole { Lead = 0, Follower = 1 }

/// <summary>
/// Endpoint-neutral two-fragment baseline archive.  A fragment alone is deliberately
/// insufficient to recover a deletion baseline; only an opposite-role pair is accepted.
/// </summary>
public sealed class EndpointBaselineStore
{
    private const int SchemaVersion = 2;
    private const int StreamVersion = 1;
    public string? LastLoadWarning { get; private set; }

    public async Task<IReadOnlyList<BaselineEntry>?> LoadAsync(IEndpoint left, IEndpoint right, CancellationToken ct = default)
    {
        LastLoadWarning = null;
        var directory = CreateWorkDirectory();
        try
        {
            var files = await Task.WhenAll(DownloadAsync(left, directory, ct), DownloadAsync(right, directory, ct));
            if (files[0] is null && files[1] is null) return null;
            // Validate any existing archive even when it is lone: a user-owned/corrupt
            // sync.fengdb must never be silently treated as an absent baseline.
            var leftRead = files[0] is null ? new ArchiveRead(Archive.Empty, false) : await TryReadArchiveAsync(files[0]!, ct);
            var rightRead = files[1] is null ? new ArchiveRead(Archive.Empty, false) : await TryReadArchiveAsync(files[1]!, ct);
            if (leftRead.Invalid || rightRead.Invalid)
            {
                LastLoadWarning = "检测到旧版或无效的 sync.fengdb；本次将不使用删除基线，成功同步后会重建状态数据库。";
                return null;
            }
            var a = leftRead.Archive; var b = rightRead.Archive;
            if (files[0] is null || files[1] is null) return null; // a lone archive is never deletion authority
            var pairs = (from x in a.Sessions from y in b.Sessions
                         where x.Id == y.Id && x.Role != y.Role select (x, y)).ToList();
            if (pairs.Count == 0)
                return null;
            if (pairs.Count != 1) throw new InvalidDataException("两端 sync.fengdb 包含多个可配对 session；为避免误删已停止比较。");
            var (first, second) = pairs[0];
            var lead = first.Role == SessionRole.Lead ? first : second;
            var follower = first.Role == SessionRole.Follower ? first : second;
            var payload = Join(lead, follower);
            return Decode(payload);
        }
        finally { TryDelete(directory); }
    }

    public async Task CommitAsync(IEndpoint left, IEndpoint right, CancellationToken ct = default)
    {
        if (left is not IEndpointStateStorage || right is not IEndpointStateStorage)
            throw new NotSupportedException("端点未实现 Feng Sync 状态存储接口。");
        var directory = CreateWorkDirectory();
        try
        {
            var downloaded = await Task.WhenAll(DownloadAsync(left, directory, ct), DownloadAsync(right, directory, ct));
            var leftArchive = downloaded[0] is null ? Archive.Empty : (await TryReadArchiveAsync(downloaded[0]!, ct)).Archive;
            var rightArchive = downloaded[1] is null ? Archive.Empty : (await TryReadArchiveAsync(downloaded[1]!, ct)).Archive;
            var scans = await Task.WhenAll(left.ScanAsync(ct), right.ScanAsync(ct));
            var entries = scans[0].Concat(scans[1]).Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(path => new BaselineEntry(path, scans[0].FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)), scans[1].FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))).ToList();
            var payload = Encode(entries); var id = Guid.NewGuid(); var split = payload.Length / 2;
            var lead = Fragment.Create(id, SessionRole.Lead, payload, payload[..split]);
            var follower = Fragment.Create(id, SessionRole.Follower, payload, payload[split..]);
            var pairIds = MatchingIds(leftArchive, rightArchive);
            var leftSessions = leftArchive.Sessions.Where(x => !pairIds.Contains(x.Id)).Append(lead).ToList();
            var rightSessions = rightArchive.Sessions.Where(x => !pairIds.Contains(x.Id)).Append(follower).ToList();
            var leftFile = Path.Combine(directory, "left.db"); var rightFile = Path.Combine(directory, "right.db");
            await WriteArchiveAsync(leftFile, leftSessions, ct); await WriteArchiveAsync(rightFile, rightSessions, ct);
            // Validate both candidates before either formal publication.
            _ = Join((await ReadArchiveAsync(leftFile, ct)).Sessions.Single(x => x.Id == id), (await ReadArchiveAsync(rightFile, ct)).Sessions.Single(x => x.Id == id));
            await Task.WhenAll(PublishAsync(left, leftFile, ct), PublishAsync(right, rightFile, ct));
        }
        finally { TryDelete(directory); }
    }

    /// <summary>
    /// Commits the baseline directly from the planner's <see cref="ComparisonSnapshot"/>
    /// instead of re-enumerating either endpoint. M3 keeps the existing on-disk
    /// format so consumers stay compatible; only the data source changes.
    /// </summary>
    public async Task CommitFromSnapshotAsync(IEndpoint left, IEndpoint right, ComparisonSnapshot snapshot, CancellationToken ct = default)
    {
        if (left is not IEndpointStateStorage || right is not IEndpointStateStorage)
            throw new NotSupportedException("端点未实现 Feng Sync 状态存储接口。");
        var directory = CreateWorkDirectory();
        try
        {
            var downloaded = await Task.WhenAll(DownloadAsync(left, directory, ct), DownloadAsync(right, directory, ct));
            var leftArchive = downloaded[0] is null ? Archive.Empty : (await TryReadArchiveAsync(downloaded[0]!, ct)).Archive;
            var rightArchive = downloaded[1] is null ? Archive.Empty : (await TryReadArchiveAsync(downloaded[1]!, ct)).Archive;

            // M3: build the next-state entries from the snapshot rather than
            // scanning again. Path lookups use the per-side ByPath index to
            // avoid the FirstOrDefault nested loops the previous implementation
            // performed over both entry lists.
            var leftPath = snapshot.Left.ByPath;
            var rightPath = snapshot.Right.ByPath;
            var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in leftPath.Keys) allPaths.Add(k);
            foreach (var k in rightPath.Keys) allPaths.Add(k);
            var entries = allPaths
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(path => new BaselineEntry(path, leftPath.TryGetValue(path, out var l) ? l : null, rightPath.TryGetValue(path, out var r) ? r : null))
                .ToList();

            var payload = Encode(entries); var id = Guid.NewGuid(); var split = payload.Length / 2;
            var lead = Fragment.Create(id, SessionRole.Lead, payload, payload[..split]);
            var follower = Fragment.Create(id, SessionRole.Follower, payload, payload[split..]);
            var pairIds = MatchingIds(leftArchive, rightArchive);
            var leftSessions = leftArchive.Sessions.Where(x => !pairIds.Contains(x.Id)).Append(lead).ToList();
            var rightSessions = rightArchive.Sessions.Where(x => !pairIds.Contains(x.Id)).Append(follower).ToList();
            var leftFile = Path.Combine(directory, "left.db"); var rightFile = Path.Combine(directory, "right.db");
            await WriteArchiveAsync(leftFile, leftSessions, ct); await WriteArchiveAsync(rightFile, rightSessions, ct);
            _ = Join((await ReadArchiveAsync(leftFile, ct)).Sessions.Single(x => x.Id == id), (await ReadArchiveAsync(rightFile, ct)).Sessions.Single(x => x.Id == id));
            await Task.WhenAll(PublishAsync(left, leftFile, ct), PublishAsync(right, rightFile, ct));
        }
        finally { TryDelete(directory); }
    }

    /// <summary>
    /// M5: commits the next baseline derived from <see cref="BaselineStateBuilder"/>
    /// rather than from the raw snapshot. This is the only safe source of
    /// truth after a successful sync; using the pre-sync snapshot would leave
    /// the destination recorded with its old (missing or stale) fingerprint.
    /// </summary>
    public async Task CommitFromResultsAsync(IEndpoint left, IEndpoint right, BaselineCommitInput input, CancellationToken ct = default)
    {
        if (left is not IEndpointStateStorage || right is not IEndpointStateStorage)
            throw new NotSupportedException("端点未实现 Feng Sync 状态存储接口。");
        var directory = CreateWorkDirectory();
        try
        {
            var downloaded = await Task.WhenAll(DownloadAsync(left, directory, ct), DownloadAsync(right, directory, ct));
            var leftArchive = downloaded[0] is null ? Archive.Empty : (await TryReadArchiveAsync(downloaded[0]!, ct)).Archive;
            var rightArchive = downloaded[1] is null ? Archive.Empty : (await TryReadArchiveAsync(downloaded[1]!, ct)).Archive;

            IReadOnlyList<BaselineEntry>? previous = null;
            var leftRead = downloaded[0] is null ? new ArchiveRead(Archive.Empty, false) : await TryReadArchiveAsync(downloaded[0]!, ct);
            var rightRead = downloaded[1] is null ? new ArchiveRead(Archive.Empty, false) : await TryReadArchiveAsync(downloaded[1]!, ct);
            if (leftRead.Archive.Sessions.Count > 0 && rightRead.Archive.Sessions.Count > 0)
            {
                var leftPair = leftRead.Archive.Sessions.FirstOrDefault();
                var rightPair = rightRead.Archive.Sessions.FirstOrDefault(r => r.Id != leftPair?.Id);
                if (leftPair is not null && rightPair is not null)
                {
                    try { previous = Decode(Join(leftPair, rightPair)); }
                    catch (InvalidDataException) { previous = null; }
                }
            }

            var entries = BaselineStateBuilder.BuildNextState(input, previous);
            var payload = Encode(entries); var id = Guid.NewGuid(); var split = payload.Length / 2;
            var lead = Fragment.Create(id, SessionRole.Lead, payload, payload[..split]);
            var follower = Fragment.Create(id, SessionRole.Follower, payload, payload[split..]);
            var pairIds = MatchingIds(leftArchive, rightArchive);
            var leftSessions = leftArchive.Sessions.Where(x => !pairIds.Contains(x.Id)).Append(lead).ToList();
            var rightSessions = rightArchive.Sessions.Where(x => !pairIds.Contains(x.Id)).Append(follower).ToList();
            var leftFile = Path.Combine(directory, "left.db"); var rightFile = Path.Combine(directory, "right.db");
            await WriteArchiveAsync(leftFile, leftSessions, ct); await WriteArchiveAsync(rightFile, rightSessions, ct);
            _ = Join((await ReadArchiveAsync(leftFile, ct)).Sessions.Single(x => x.Id == id), (await ReadArchiveAsync(rightFile, ct)).Sessions.Single(x => x.Id == id));
            await Task.WhenAll(PublishAsync(left, leftFile, ct), PublishAsync(right, rightFile, ct));
        }
        finally { TryDelete(directory); }
    }

    private static HashSet<Guid> MatchingIds(Archive a, Archive b) => (from x in a.Sessions from y in b.Sessions where x.Id == y.Id && x.Role != y.Role select x.Id).ToHashSet();
    private static async Task<string?> DownloadAsync(IEndpoint endpoint, string directory, CancellationToken ct)
        => endpoint is IEndpointStateStorage state ? await state.DownloadStateAsync(SyncInternalPaths.StateDatabase, directory, ct) : throw new NotSupportedException();
    private static Task PublishAsync(IEndpoint endpoint, string database, CancellationToken ct)
        => ((IEndpointStateStorage)endpoint).UploadAndPublishStateAsync(database, SyncInternalPaths.StateTemporary(Guid.NewGuid()), ct);

    private static string CreateWorkDirectory() { var path = Path.Combine(Path.GetTempPath(), "fengsync-state-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void TryDelete(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
    private static SqliteConnection Open(string path) => new($"Data Source={path};Mode=ReadWriteCreate;Cache=Private;Pooling=False");
    private static async Task<ArchiveRead> TryReadArchiveAsync(string path, CancellationToken ct)
    {
        try { return new(await ReadArchiveAsync(path, ct), false); }
        catch (InvalidDataException) { return new(Archive.Empty, true); }
    }
    private static async Task WriteArchiveAsync(string path, IReadOnlyList<Fragment> sessions, CancellationToken ct)
    {
        await using var con = Open(path); await con.OpenAsync(ct); await using var command = con.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE; CREATE TABLE database_meta(format_magic TEXT NOT NULL, database_version INTEGER NOT NULL, stream_version INTEGER NOT NULL, created_utc TEXT NOT NULL); CREATE TABLE sessions(session_id TEXT NOT NULL, role INTEGER NOT NULL, stream_version INTEGER NOT NULL, payload_size INTEGER NOT NULL, payload_sha256 TEXT NOT NULL, fragment_sha256 TEXT NOT NULL, fragment_blob BLOB NOT NULL, created_utc TEXT NOT NULL, PRIMARY KEY(session_id, role));";
        await command.ExecuteNonQueryAsync(ct);
        foreach (var s in sessions)
        {
            command.Parameters.Clear(); command.CommandText = "INSERT INTO sessions VALUES($id,$role,$version,$size,$payload,$fragment,$blob,$created)";
            command.Parameters.AddWithValue("$id", s.Id.ToString("N")); command.Parameters.AddWithValue("$role", (int)s.Role); command.Parameters.AddWithValue("$version", StreamVersion); command.Parameters.AddWithValue("$size", s.PayloadSize); command.Parameters.AddWithValue("$payload", s.PayloadHash); command.Parameters.AddWithValue("$fragment", s.FragmentHash); command.Parameters.AddWithValue("$blob", s.Bytes); command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O")); await command.ExecuteNonQueryAsync(ct);
        }
        command.Parameters.Clear(); command.CommandText = "INSERT INTO database_meta VALUES('FengSync', $version, $stream, $created)"; command.Parameters.AddWithValue("$version", SchemaVersion); command.Parameters.AddWithValue("$stream", StreamVersion); command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O")); await command.ExecuteNonQueryAsync(ct);
        command.Parameters.Clear(); command.CommandText = "PRAGMA quick_check;"; if (!string.Equals((string?)await command.ExecuteScalarAsync(ct), "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("候选 sync.fengdb 完整性校验失败。");
    }
    private static async Task<Archive> ReadArchiveAsync(string path, CancellationToken ct)
    {
        await using var con = Open(path); await con.OpenAsync(ct); await using var command = con.CreateCommand(); command.CommandText = "PRAGMA quick_check;";
        if (!string.Equals((string?)await command.ExecuteScalarAsync(ct), "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("sync.fengdb 完整性校验失败。");
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='sessions'";
        if (await command.ExecuteScalarAsync(ct) is null) throw new InvalidDataException("sync.fengdb 是旧版或不受支持的状态格式。");
        command.CommandText = "SELECT format_magic, database_version FROM database_meta LIMIT 1"; await using var meta = await command.ExecuteReaderAsync(ct);
        if (!await meta.ReadAsync(ct) || meta.GetString(0) != "FengSync" || meta.GetInt32(1) != SchemaVersion) throw new InvalidDataException("sync.fengdb 不是受支持的 Feng Sync 状态库。");
        await meta.DisposeAsync(); command.CommandText = "SELECT session_id, role, stream_version, payload_size, payload_sha256, fragment_sha256, fragment_blob FROM sessions"; await using var reader = await command.ExecuteReaderAsync(ct); var sessions = new List<Fragment>();
        while (await reader.ReadAsync(ct)) sessions.Add(new(Guid.ParseExact(reader.GetString(0), "N"), (SessionRole)reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5), (byte[])reader[6]));
        return new(sessions);
    }
    private static byte[] Encode(IReadOnlyList<BaselineEntry> entries)
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(entries.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)); using var output = new MemoryStream(); using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(raw); return output.ToArray();
    }
    private static IReadOnlyList<BaselineEntry> Decode(byte[] payload)
    {
        using var input = new MemoryStream(payload); using var gzip = new GZipStream(input, CompressionMode.Decompress); return JsonSerializer.Deserialize<List<BaselineEntry>>(gzip) ?? throw new InvalidDataException("同步状态负载为空。");
    }
    private static byte[] Join(Fragment lead, Fragment follower)
    {
        if (lead.StreamVersion != StreamVersion || follower.StreamVersion != StreamVersion || lead.PayloadSize != follower.PayloadSize || lead.PayloadHash != follower.PayloadHash || Hash(lead.Bytes) != lead.FragmentHash || Hash(follower.Bytes) != follower.FragmentHash) throw new InvalidDataException("sync.fengdb session 片段校验失败。");
        var payload = lead.Bytes.Concat(follower.Bytes).ToArray(); if (payload.Length != lead.PayloadSize || Hash(payload) != lead.PayloadHash) throw new InvalidDataException("sync.fengdb 完整负载校验失败。"); return payload;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private sealed record Archive(IReadOnlyList<Fragment> Sessions) { public static Archive Empty { get; } = new([]); }
    private sealed record ArchiveRead(Archive Archive, bool Invalid);
    private sealed record Fragment(Guid Id, SessionRole Role, int StreamVersion, int PayloadSize, string PayloadHash, string FragmentHash, byte[] Bytes)
    { public static Fragment Create(Guid id, SessionRole role, byte[] payload, byte[] bytes) => new(id, role, EndpointBaselineStore.StreamVersion, payload.Length, Hash(payload), Hash(bytes), bytes); }
}
