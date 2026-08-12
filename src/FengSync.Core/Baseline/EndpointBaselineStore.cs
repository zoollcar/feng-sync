using System.Security.Cryptography;
using FengSync.Core.Scanning;
using Microsoft.Data.Sqlite;

namespace FengSync.Core;

public enum SessionRole { Lead = 0, Follower = 1 }
public enum BaselineLoadStatus { Missing, Available, Legacy, MissingPair, UnsupportedVersion, Corrupted, Ambiguous }
public enum BaselineCommitStatus { Updated, Unchanged }

public sealed record BaselineLoadResult(
    IReadOnlyList<BaselineEntry>? Entries,
    BaselineLoadStatus Status,
    int? StreamVersion = null,
    string? Warning = null)
{
    public bool CanPropagateDeletes => Status is BaselineLoadStatus.Available or BaselineLoadStatus.Legacy;
}

public sealed record BaselineCommitResult(BaselineCommitStatus Status, int StreamVersion = 3);

/// <summary>
/// Endpoint-neutral two-fragment baseline archive. A fragment alone is never
/// sufficient deletion authority; only a validated opposite-role pair is used.
/// </summary>
public sealed class EndpointBaselineStore
{
    private const int SchemaVersion = 2;
    public string? LastLoadWarning { get; private set; }

    public async Task<IReadOnlyList<BaselineEntry>?> LoadAsync(IEndpoint left, IEndpoint right, CancellationToken ct = default)
        => (await LoadDetailedAsync(left, right, ct)).Entries;

    public async Task<BaselineLoadResult> LoadDetailedAsync(IEndpoint left, IEndpoint right, CancellationToken ct = default)
    {
        LastLoadWarning = null;
        var directory = CreateWorkDirectory();
        try
        {
            var files = await Task.WhenAll(DownloadAsync(left, directory, ct), DownloadAsync(right, directory, ct));
            var result = await LoadDownloadedAsync(files[0], files[1], ct);
            LastLoadWarning = result.Warning;
            return result;
        }
        finally { TryDelete(directory); }
    }

    public async Task<BaselineCommitResult> CommitAsync(IEndpoint left, IEndpoint right, CancellationToken ct = default)
    {
        var scans = await Task.WhenAll(left.ScanAsync(ct), right.ScanAsync(ct));
        var comparer = CommonComparer(left, right);
        var leftByPath = scans[0].ToDictionary(x => x.Path, comparer);
        var rightByPath = scans[1].ToDictionary(x => x.Path, comparer);
        var paths = leftByPath.Keys.Union(rightByPath.Keys, comparer).OrderBy(x => x, StringComparer.Ordinal);
        var entries = paths.Select(path => new BaselineEntry(path, leftByPath.GetValueOrDefault(path), rightByPath.GetValueOrDefault(path))).ToList();
        return await CommitEntriesAsync(left, right, entries, ct);
    }

    public Task<BaselineCommitResult> CommitFromSnapshotAsync(IEndpoint left, IEndpoint right, ComparisonSnapshot snapshot, CancellationToken ct = default)
    {
        var comparer = CommonComparer(left, right);
        var paths = snapshot.Left.ByPath.Keys.Union(snapshot.Right.ByPath.Keys, comparer).OrderBy(x => x, StringComparer.Ordinal);
        var entries = paths.Select(path => new BaselineEntry(path,
            snapshot.Left.ByPath.TryGetValue(path, out var l) ? l : null,
            snapshot.Right.ByPath.TryGetValue(path, out var r) ? r : null)).ToList();
        return CommitEntriesAsync(left, right, entries, ct);
    }

    public async Task<BaselineCommitResult> CommitFromResultsAsync(IEndpoint left, IEndpoint right, BaselineCommitInput input, CancellationToken ct = default)
    {
        var previous = (await LoadDetailedAsync(left, right, ct)).Entries;
        var entries = BaselineStateBuilder.BuildNextState(input, previous);
        return await CommitEntriesAsync(left, right, entries, ct);
    }

    private async Task<BaselineCommitResult> CommitEntriesAsync(IEndpoint left, IEndpoint right, IReadOnlyList<BaselineEntry> entries, CancellationToken ct)
    {
        if (left is not IEndpointStateStorage || right is not IEndpointStateStorage)
            throw new NotSupportedException("端点未实现 Feng Sync 状态存储接口。");
        var directory = CreateWorkDirectory();
        try
        {
            var downloaded = await Task.WhenAll(DownloadAsync(left, directory, ct), DownloadAsync(right, directory, ct));
            var leftRead = downloaded[0] is null ? new ArchiveRead(Archive.Empty, false) : await TryReadArchiveAsync(downloaded[0]!, ct);
            var rightRead = downloaded[1] is null ? new ArchiveRead(Archive.Empty, false) : await TryReadArchiveAsync(downloaded[1]!, ct);
            var leftArchive = leftRead.Invalid ? Archive.Empty : leftRead.Archive;
            var rightArchive = rightRead.Invalid ? Archive.Empty : rightRead.Archive;
            var payload = BaselinePayloadCodec.Encode(entries);
            var currentPairs = CommonPairs(leftArchive, rightArchive);
            var latest = currentPairs.OrderByDescending(PairCreatedUtc).FirstOrDefault();
            if (latest.Left is not null && latest.Left.StreamVersion == BaselinePayloadCodec.CurrentVersion &&
                latest.Right.StreamVersion == BaselinePayloadCodec.CurrentVersion &&
                string.Equals(latest.Left.PayloadHash, Hash(payload), StringComparison.Ordinal))
                return new(BaselineCommitStatus.Unchanged);

            var id = Guid.NewGuid();
            var created = DateTimeOffset.UtcNow;
            var split = payload.Length / 2;
            var lead = Fragment.Create(id, SessionRole.Lead, payload, payload[..split], created);
            var follower = Fragment.Create(id, SessionRole.Follower, payload, payload[split..], created);

            // Retain the newest previous common session so a one-sided publish
            // failure leaves an intact deletion baseline. Sessions belonging to
            // other endpoint pairs are retained unchanged.
            var commonIds = currentPairs.Select(x => x.Left.Id).ToHashSet();
            var retainedId = latest.Left?.Id;
            var leftSessions = leftArchive.Sessions.Where(x => !commonIds.Contains(x.Id) || x.Id == retainedId).Append(lead).ToList();
            var rightSessions = rightArchive.Sessions.Where(x => !commonIds.Contains(x.Id) || x.Id == retainedId).Append(follower).ToList();
            var leftFile = Path.Combine(directory, "left.db");
            var rightFile = Path.Combine(directory, "right.db");
            await WriteArchiveAsync(leftFile, leftSessions, ct);
            await WriteArchiveAsync(rightFile, rightSessions, ct);
            _ = DecodePair((await ReadArchiveAsync(leftFile, ct)).Sessions.Single(x => x.Id == id),
                (await ReadArchiveAsync(rightFile, ct)).Sessions.Single(x => x.Id == id));
            await Task.WhenAll(PublishAsync(left, leftFile, ct), PublishAsync(right, rightFile, ct));
            return new(BaselineCommitStatus.Updated);
        }
        finally { TryDelete(directory); }
    }

    private static async Task<BaselineLoadResult> LoadDownloadedAsync(string? leftFile, string? rightFile, CancellationToken ct)
    {
        if (leftFile is null && rightFile is null) return new(null, BaselineLoadStatus.Missing);
        var leftRead = leftFile is null ? new ArchiveRead(Archive.Empty, false) : await TryReadArchiveAsync(leftFile, ct);
        var rightRead = rightFile is null ? new ArchiveRead(Archive.Empty, false) : await TryReadArchiveAsync(rightFile, ct);
        if (leftRead.Invalid || rightRead.Invalid)
            return new(null, BaselineLoadStatus.Corrupted, Warning: "检测到损坏或无效的 sync.fengdb；本次停用删除基线，成功同步后会重建状态数据库。");
        if (leftFile is null || rightFile is null)
            return new(null, BaselineLoadStatus.MissingPair, Warning: "仅一端存在 sync.fengdb；本次停用删除基线。");

        var pairs = CommonPairs(leftRead.Archive, rightRead.Archive);
        if (pairs.Count == 0)
            return new(null, BaselineLoadStatus.MissingPair, Warning: "两端 sync.fengdb 没有可配对 session；本次停用删除基线。");
        var ordered = pairs.OrderByDescending(PairCreatedUtc).ToList();
        if (ordered.Count > 1 && PairCreatedUtc(ordered[0]) == PairCreatedUtc(ordered[1]))
            return new(null, BaselineLoadStatus.Ambiguous, Warning: "两端 sync.fengdb 无法唯一确定最新 session；已停止使用删除基线。");
        try
        {
            var pair = ordered[0];
            var entries = DecodePair(pair.Left, pair.Right);
            var version = pair.Left.StreamVersion;
            return version == BaselinePayloadCodec.CurrentVersion
                ? new(entries, BaselineLoadStatus.Available, version)
                : new(entries, BaselineLoadStatus.Legacy, version, "已安全读取 sync.fengdb v2；下次成功同步将升级为 v3。");
        }
        catch (BaselineUnsupportedVersionException ex)
        {
            return new(null, BaselineLoadStatus.UnsupportedVersion, ex.Version,
                $"sync.fengdb stream v{ex.Version} 已不受支持；本次停用删除基线，成功同步后会重建 v3。");
        }
        catch (InvalidDataException)
        {
            return new(null, BaselineLoadStatus.Corrupted, Warning: "sync.fengdb 配对负载损坏；本次停用删除基线，成功同步后会重建状态数据库。");
        }
    }

    private static IReadOnlyList<BaselineEntry> DecodePair(Fragment first, Fragment second)
    {
        var lead = first.Role == SessionRole.Lead ? first : second;
        var follower = first.Role == SessionRole.Follower ? first : second;
        var payload = Join(lead, follower);
        return BaselinePayloadCodec.Decode(lead.StreamVersion, payload);
    }

    private static List<(Fragment Left, Fragment Right)> CommonPairs(Archive left, Archive right) =>
        (from l in left.Sessions from r in right.Sessions where l.Id == r.Id && l.Role != r.Role select (l, r)).ToList();
    private static DateTimeOffset PairCreatedUtc((Fragment Left, Fragment Right) pair) => pair.Left.CreatedUtc > pair.Right.CreatedUtc ? pair.Left.CreatedUtc : pair.Right.CreatedUtc;
    private static IEqualityComparer<string> CommonComparer(IEndpoint left, IEndpoint right) =>
        new EndpointPathSemantics(left.Capabilities.EffectivePaths.CaseSensitive || right.Capabilities.EffectivePaths.CaseSensitive,
            left.Capabilities.EffectivePaths.UnicodeNormalization, '/').CreateComparer();
    private static async Task<string?> DownloadAsync(IEndpoint endpoint, string directory, CancellationToken ct) =>
        endpoint is IEndpointStateStorage state ? await state.DownloadStateAsync(SyncInternalPaths.StateDatabase, directory, ct) : throw new NotSupportedException();
    private static Task PublishAsync(IEndpoint endpoint, string database, CancellationToken ct) =>
        ((IEndpointStateStorage)endpoint).UploadAndPublishStateAsync(database, SyncInternalPaths.StateTemporary(Guid.NewGuid()), ct);
    private static string CreateWorkDirectory() { var path = Path.Combine(Path.GetTempPath(), "fengsync-state-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void TryDelete(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
    private static SqliteConnection Open(string path) => new($"Data Source={path};Mode=ReadWriteCreate;Cache=Private;Pooling=False");

    private static async Task<ArchiveRead> TryReadArchiveAsync(string path, CancellationToken ct)
    {
        try { return new(await ReadArchiveAsync(path, ct), false); }
        catch (Exception ex) when (ex is InvalidDataException or SqliteException or FormatException) { return new(Archive.Empty, true); }
    }

    private static async Task WriteArchiveAsync(string path, IReadOnlyList<Fragment> sessions, CancellationToken ct)
    {
        await using var con = Open(path); await con.OpenAsync(ct); await using var command = con.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE; CREATE TABLE database_meta(format_magic TEXT NOT NULL, database_version INTEGER NOT NULL, stream_version INTEGER NOT NULL, created_utc TEXT NOT NULL); CREATE TABLE sessions(session_id TEXT NOT NULL, role INTEGER NOT NULL, stream_version INTEGER NOT NULL, payload_size INTEGER NOT NULL, payload_sha256 TEXT NOT NULL, fragment_sha256 TEXT NOT NULL, fragment_blob BLOB NOT NULL, created_utc TEXT NOT NULL, PRIMARY KEY(session_id, role));";
        await command.ExecuteNonQueryAsync(ct);
        foreach (var session in sessions)
        {
            command.Parameters.Clear(); command.CommandText = "INSERT INTO sessions VALUES($id,$role,$version,$size,$payload,$fragment,$blob,$created)";
            command.Parameters.AddWithValue("$id", session.Id.ToString("N")); command.Parameters.AddWithValue("$role", (int)session.Role);
            command.Parameters.AddWithValue("$version", session.StreamVersion); command.Parameters.AddWithValue("$size", session.PayloadSize);
            command.Parameters.AddWithValue("$payload", session.PayloadHash); command.Parameters.AddWithValue("$fragment", session.FragmentHash);
            command.Parameters.AddWithValue("$blob", session.Bytes); command.Parameters.AddWithValue("$created", session.CreatedUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }
        command.Parameters.Clear(); command.CommandText = "INSERT INTO database_meta VALUES('FengSync', $version, $stream, $created)";
        command.Parameters.AddWithValue("$version", SchemaVersion); command.Parameters.AddWithValue("$stream", BaselinePayloadCodec.CurrentVersion);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O")); await command.ExecuteNonQueryAsync(ct);
        command.Parameters.Clear(); command.CommandText = "PRAGMA quick_check;";
        if (!string.Equals((string?)await command.ExecuteScalarAsync(ct), "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("候选 sync.fengdb 完整性校验失败。");
    }

    private static async Task<Archive> ReadArchiveAsync(string path, CancellationToken ct)
    {
        await using var con = Open(path); await con.OpenAsync(ct); await using var command = con.CreateCommand(); command.CommandText = "PRAGMA quick_check;";
        if (!string.Equals((string?)await command.ExecuteScalarAsync(ct), "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("sync.fengdb 完整性校验失败。");
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='sessions'";
        if (await command.ExecuteScalarAsync(ct) is null) throw new InvalidDataException("sync.fengdb 是不受支持的状态格式。");
        command.CommandText = "SELECT format_magic, database_version FROM database_meta LIMIT 1"; await using var meta = await command.ExecuteReaderAsync(ct);
        if (!await meta.ReadAsync(ct) || meta.GetString(0) != "FengSync" || meta.GetInt32(1) != SchemaVersion) throw new InvalidDataException("sync.fengdb 不是受支持的 Feng Sync 状态库。");
        await meta.DisposeAsync();
        command.CommandText = "SELECT session_id, role, stream_version, payload_size, payload_sha256, fragment_sha256, fragment_blob, created_utc FROM sessions";
        await using var reader = await command.ExecuteReaderAsync(ct); var sessions = new List<Fragment>();
        while (await reader.ReadAsync(ct))
        {
            var role = reader.GetInt32(1);
            if (!Enum.IsDefined(typeof(SessionRole), role)) throw new InvalidDataException("sync.fengdb session 角色无效。");
            sessions.Add(new(Guid.ParseExact(reader.GetString(0), "N"), (SessionRole)role, reader.GetInt32(2), reader.GetInt32(3),
                reader.GetString(4), reader.GetString(5), (byte[])reader[6], DateTimeOffset.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        return new(sessions);
    }

    private static byte[] Join(Fragment lead, Fragment follower)
    {
        if (lead.Role != SessionRole.Lead || follower.Role != SessionRole.Follower || lead.StreamVersion != follower.StreamVersion ||
            lead.PayloadSize != follower.PayloadSize || lead.PayloadHash != follower.PayloadHash || Hash(lead.Bytes) != lead.FragmentHash || Hash(follower.Bytes) != follower.FragmentHash)
            throw new InvalidDataException("sync.fengdb session 片段校验失败。");
        var payload = lead.Bytes.Concat(follower.Bytes).ToArray();
        if (payload.Length != lead.PayloadSize || Hash(payload) != lead.PayloadHash) throw new InvalidDataException("sync.fengdb 完整负载校验失败。");
        return payload;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private sealed record Archive(IReadOnlyList<Fragment> Sessions) { public static Archive Empty { get; } = new([]); }
    private sealed record ArchiveRead(Archive Archive, bool Invalid);
    private sealed record Fragment(Guid Id, SessionRole Role, int StreamVersion, int PayloadSize, string PayloadHash, string FragmentHash, byte[] Bytes, DateTimeOffset CreatedUtc)
    {
        public static Fragment Create(Guid id, SessionRole role, byte[] payload, byte[] bytes, DateTimeOffset createdUtc) =>
            new(id, role, BaselinePayloadCodec.CurrentVersion, payload.Length, Hash(payload), Hash(bytes), bytes, createdUtc);
    }
}
