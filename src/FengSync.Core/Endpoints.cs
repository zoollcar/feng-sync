using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Runtime.CompilerServices;
using FengSync.Core.Diagnostics;
using FengSync.Core.Rclone.Diagnostics;

namespace FengSync.Core;

/// <summary>
/// The flavours of endpoint Feng Sync can address. Values are appended, never reordered or renamed:
/// <see cref="Baseline.BaselineTransaction"/> and <see cref="Execution.TransferResumeStateStore"/> both
/// persist <c>EndpointType.ToString()</c> as part of an endpoint's identity, so a rename would silently
/// invalidate every existing baseline and resume record.
/// </summary>
public enum EndpointType { Local, Sftp, GoogleDrive, S3, OneDrive, Dropbox, Box, WebDav }
public sealed record EndpointProfile(Guid Id, EndpointType Type, string Root, string? Remote = null, string? Identity = null);
public sealed record EndpointPathSemantics(bool CaseSensitive, NormalizationForm UnicodeNormalization, char Separator = '/')
{
    public string Canonicalize(string path)
    {
        var value = path.Replace('\\', Separator).Trim(Separator).Normalize(UnicodeNormalization);
        return CaseSensitive ? value : value.ToUpperInvariant();
    }

    /// <summary>Dictionary comparer that applies this endpoint's path rules to
    /// every lookup. Keeping the original key spelling still lets plans display
    /// the provider's real path while avoiding unsafe Windows-only folding.</summary>
    public IEqualityComparer<string> CreateComparer() => new CanonicalPathComparer(this);

    private sealed class CanonicalPathComparer(EndpointPathSemantics semantics) : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => x is null ? y is null : y is not null &&
            string.Equals(semantics.Canonicalize(x), semantics.Canonicalize(y), StringComparison.Ordinal);
        public int GetHashCode(string obj) => StringComparer.Ordinal.GetHashCode(semantics.Canonicalize(obj));
    }
}
[Flags] public enum MoveEvidenceCapabilities { None = 0, StableId = 1, StrongHash = 2, ProviderToken = 4, SizeAndTime = 8 }
public sealed record EndpointMoveCapabilities(MoveEvidenceCapabilities Evidence, EndpointMoveExecution FileExecution,
    EndpointMoveExecution DirectoryExecution, bool RequiresRuntimeProbe = false, int MaxConcurrentMoves = 4);
public sealed record EndpointCapabilities(bool StableIds, bool ServerMove, bool EmptyDirectories, TimeSpan ModifiedTimePrecision,
    EndpointMoveCapabilities? Move = null, EndpointPathSemantics? Paths = null)
{
    public EndpointMoveCapabilities EffectiveMove => Move ?? new(StableIds ? MoveEvidenceCapabilities.StableId : MoveEvidenceCapabilities.SizeAndTime,
        ServerMove ? EndpointMoveExecution.NativeRename : EndpointMoveExecution.None,
        ServerMove ? EndpointMoveExecution.NativeRename : EndpointMoveExecution.None);
    public EndpointPathSemantics EffectivePaths => Paths ?? new(false, NormalizationForm.FormC);
}
/// <summary>Best-effort scan feedback. Remote providers can only report after a listing request completes.</summary>
public sealed record ScanProgress(int ItemsScanned, string? CurrentPath = null, bool Completed = false);
/// <summary>A Feng Sync-owned staging object, kept out of normal comparisons.</summary>
public sealed record TransferTemporaryFile(string RelativePath, string OriginalPath, long Size, DateTimeOffset ModifiedUtc);

/// <summary>Transport boundary: the planner never needs to know rclone, SFTP or Drive details.</summary>
public interface IEndpoint
{
    EndpointProfile Profile { get; }
    EndpointCapabilities Capabilities { get; }
    Task<IReadOnlyList<EntrySnapshot>> ScanAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Streams scan results so paired snapshot capture can index entries while
    /// enumeration is still in progress. Existing endpoint implementations keep
    /// working through this compatibility adapter; performance-sensitive endpoints
    /// should override it to avoid materialising an intermediate list.
    /// </summary>
    async IAsyncEnumerable<EntrySnapshot> ScanEntriesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var entries = await ScanAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }
    async Task<IReadOnlyList<EntrySnapshot>> ScanAsync(IProgress<ScanProgress> progress, CancellationToken cancellationToken = default)
    {
        progress.Report(new(0));
        var entries = await ScanAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(new(entries.Count, Completed: true));
        return entries;
    }
    Task CopyToAsync(string relativePath, IEndpoint target, string temporaryPath, CancellationToken cancellationToken = default);
    Task MoveAsync(string from, string to, CancellationToken cancellationToken = default);
    /// <summary>Moves a complete directory subtree. Implementations must not overwrite an existing destination.</summary>
    Task MoveDirectoryAsync(string from, string to, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("此端点不支持原生目录移动。");
    Task DeleteAsync(string relativePath, bool directory, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default);
    /// <summary>Lists only Feng Sync transfer staging objects. Normal scans must never include them.</summary>
    Task<IReadOnlyList<TransferTemporaryFile>> ListTransferTemporaryFilesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TransferTemporaryFile>>([]);

    /// <summary>
    /// Single-path metadata lookup. Endpoints should default to a single RC/FS call
    /// (or a parent-directory list) rather than recursing the entire root. The default
    /// implementation throws so a missing capability surfaces loudly; the planner
    /// falls back to ScanAsync only when the implementor has explicitly opted in.
    /// </summary>
    Task<EntrySnapshot?> StatAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("此端点未实现 StatAsync；不要在热路径回退到 ScanAsync。");
    }
}

/// <summary>
/// Optional endpoint capability for publishing a Feng Sync-owned staging file.
/// This is deliberately separate from user-visible MoveAsync: a logical move
/// must never overwrite its destination, while a verified copy update may
/// atomically replace the destination captured by the comparison snapshot.
/// </summary>
public interface IStagedPublishEndpoint
{
    Task PublishStagedAsync(string temporaryPath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default);
}

/// <summary>Private endpoint control plane used for sync.fengdb.  It is intentionally
/// separate from ScanAsync so state objects can never leak into normal sync plans.</summary>
public interface IEndpointStateStorage
{
    Task<string?> DownloadStateAsync(string relativePath, string localDirectory, CancellationToken cancellationToken = default);
    Task UploadAndPublishStateAsync(string localPath, string temporaryRelativePath, CancellationToken cancellationToken = default);
}

/// <summary>Minimal strongly typed wrapper around rclone's loopback RC API. It deliberately never puts credentials in command lines.</summary>
public sealed class RcloneRcClient(HttpClient http, Uri baseUri, string user, string password)
{
    /// <summary>
    /// Reads one object through <c>operations/stat</c>, without listing the directory that holds it.
    /// <paramref name="hashTypes"/> are rclone's own lower-case hash names and are deliberately opt-in: a
    /// backend that stores no checksum has to read the whole object to answer one, and each extra name costs
    /// about another pass over it (measured against the bundled v1.75.0 on a 256 MiB object: no hash 0.15 s,
    /// md5 0.49 s, md5+sha1+sha256 0.86 s). An object that is not there answers <c>null</c> — v1.75.0 reports
    /// a missing object as an empty <c>item</c> rather than as an error, so "gone" is never an exception.
    /// </summary>
    public async Task<RcloneStatEntry?> StatAsync(string fs, string remote, IReadOnlyList<string>? hashTypes, CancellationToken ct = default)
    {
        var response = await CallAsync("operations/stat", new { fs, remote, opt = new { hashTypes = hashTypes ?? [] } }, ct);
        if (!response.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
        var path = item.TryGetProperty("Path", out var pathValue) ? pathValue.GetString() ?? "" : "";
        var directory = item.TryGetProperty("IsDir", out var directoryValue) && directoryValue.GetBoolean();
        var size = item.TryGetProperty("Size", out var sizeValue) ? sizeValue.GetInt64() : 0;
        var modified = item.TryGetProperty("ModTime", out var modifiedValue) && DateTimeOffset.TryParse(modifiedValue.GetString(), out var parsed)
            ? parsed : (DateTimeOffset?)null;
        // rclone reports a MimeType for every object and for a directory (inode/directory), so the absence of
        // one here means an older or unusual reply shape, not "no type".
        var mimeType = item.TryGetProperty("MimeType", out var mimeValue) ? mimeValue.GetString() : null;
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (item.TryGetProperty("Hashes", out var hashValues) && hashValues.ValueKind == JsonValueKind.Object)
            foreach (var entry in hashValues.EnumerateObject())
                if (entry.Value.GetString() is { Length: > 0 } value) hashes[entry.Name] = value;
        return new RcloneStatEntry(path.Trim('/'), directory, size, modified, mimeType, hashes);
    }

    /// <summary>
    /// Lists one directory level for file-manager style views.  Unlike <see cref="ListAsync"/>, this must never recurse.
    /// </summary>
    public async Task<IReadOnlyList<RcloneDirectoryEntry>> ListDirectoryAsync(string fs, string remote, CancellationToken ct = default)
    {
        var response = await CallAsync("operations/list", new { fs, remote, opt = new { recurse = false } }, ct);
        return ParseDirectoryEntries(response);
    }

    /// <summary>
    /// Lists every object at or below <paramref name="remote"/>, for a recursive search. The payload is the
    /// whole subtree, so callers must filter it themselves rather than assuming it is small.
    /// </summary>
    public async Task<IReadOnlyList<RcloneDirectoryEntry>> ListRecursiveAsync(string fs, string remote, CancellationToken ct = default)
    {
        var response = await CallAsync("operations/list", new { fs, remote, opt = new { recurse = true } }, ct);
        return ParseDirectoryEntries(response);
    }

    /// <summary>
    /// Reads the <c>list</c> array of an <c>operations/list</c> reply. The <c>Path</c> rclone reports may or
    /// may not carry the browsed prefix depending on the backend, so callers that need an absolute path must
    /// normalize it rather than trusting this value's shape.
    /// </summary>
    private static IReadOnlyList<RcloneDirectoryEntry> ParseDirectoryEntries(JsonElement response)
    {
        if (!response.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray().Select(item =>
        {
            var path = item.TryGetProperty("Path", out var pathValue) ? pathValue.GetString() ?? "" : "";
            var directory = item.TryGetProperty("IsDir", out var directoryValue) && directoryValue.GetBoolean();
            var size = item.TryGetProperty("Size", out var sizeValue) ? sizeValue.GetInt64() : 0;
            var modified = item.TryGetProperty("ModTime", out var modifiedValue) && DateTimeOffset.TryParse(modifiedValue.GetString(), out var parsed) ? parsed : (DateTimeOffset?)null;
            return new RcloneDirectoryEntry(path.Trim('/'), directory, size, modified);
        }).Where(x => !string.IsNullOrWhiteSpace(x.Path)).ToList();
    }

    public async Task<JsonElement> CallAsync(string operation, object payload, CancellationToken ct = default)
    {
        SyncRunMetricsHub.Current.IncrementRcRequest();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, operation))
        { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}")));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new RcloneException(RcloneFailureClassifier.FromTransport(operation,
                new TimeoutException("rclone RC HTTP 请求超时。", ex)), ex);
        }
        catch (HttpRequestException ex)
        {
            throw new RcloneException(RcloneFailureClassifier.FromTransport(operation, ex), ex);
        }
        using (response)
        {
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new RcloneException(RcloneFailureClassifier.FromRc(operation, (int)response.StatusCode, detail));
        }
        using var body = await response.Content.ReadAsStreamAsync(ct); using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        return doc.RootElement.Clone();
        }
    }
    public Task<JsonElement> ListAsync(string fs, string remote, CancellationToken ct = default) =>
        RunJobAsync("operations/list", new { fs, remote, opt = new { recurse = true }, _async = true }, ct);
    public async Task<IReadOnlyList<string>> ListDirectoriesAsync(string fs, string remote, bool recurse = false, CancellationToken ct = default)
    {
        var response = await CallAsync("operations/list", new { fs, remote, opt = new { recurse } }, ct);
        if (!response.TryGetProperty("list", out var list)) return [];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list.EnumerateArray())
        {
            var path = item.TryGetProperty("Path", out var value) ? value.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (item.TryGetProperty("IsDir", out var isDir) && isDir.GetBoolean()) paths.Add(path.Trim('/'));
            var parent = path.Trim('/');
            while (parent.Contains('/')) { parent = parent[..parent.LastIndexOf('/')]; paths.Add(parent); }
        }
        return paths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }
    public Task CopyFileAsync(string sourceFs, string sourceRemote, string targetFs, string targetRemote, CancellationToken ct = default, string? statsGroup = null) =>
        RunJobAsync("operations/copyfile", statsGroup is null
            ? new { srcFs = sourceFs, srcRemote = sourceRemote, dstFs = targetFs, dstRemote = targetRemote, _async = true }
            : new { srcFs = sourceFs, srcRemote = sourceRemote, dstFs = targetFs, dstRemote = targetRemote, _group = statsGroup, _async = true }, ct);
    public async Task<RcloneTransferStats?> GetTransferStatsAsync(string statsGroup, CancellationToken ct = default)
    {
        var response = await CallAsync("core/stats", new { group = statsGroup, @short = true }, ct);
        if (!response.TryGetProperty("bytes", out var bytes) || !bytes.TryGetInt64(out var transferred)) return null;
        var total = response.TryGetProperty("totalBytes", out var totalBytes) && totalBytes.TryGetInt64(out var value) ? value : 0;
        var speed = response.TryGetProperty("speed", out var speedValue) && speedValue.TryGetDouble(out var rate) ? rate : 0;
        return new(transferred, total, speed);
    }

    /// <summary>
    /// Opens one object's bytes from <paramref name="offset"/> onward through the daemon's
    /// <c>--rc-serve</c> endpoint. Returns <c>null</c> when the daemon does not answer with a partial
    /// response, which is the caller's signal that the transfer cannot be continued.
    /// <para>
    /// This is the only byte-offset read rclone offers: the RC API's copy operations are whole-object
    /// only, and rclone's own copy path restarts every interrupted transfer from zero.
    /// </para>
    /// </summary>
    public async Task<RcloneRangeContent?> OpenRangeAsync(string fs, string remote, long offset, CancellationToken ct = default)
    {
        var response = await SendFileRequestAsync(HttpMethod.Get, fs, remote, offset, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent))
        {
            response.Dispose();
            return null;
        }
        var length = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
        var modified = response.Content.Headers.LastModified;
        var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return new RcloneRangeContent(content, length ?? -1, modified);
    }

    /// <summary>Object size and modification time, read without transferring any content.</summary>
    public async Task<RcloneFileIdentity?> ProbeAsync(string fs, string remote, CancellationToken ct = default)
    {
        using var response = await SendFileRequestAsync(HttpMethod.Head, fs, remote, 0, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var length = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
        return length is null ? null : new RcloneFileIdentity(length.Value, response.Content.Headers.LastModified);
    }

    private async Task<HttpResponseMessage> SendFileRequestAsync(HttpMethod method, string fs, string remote, long offset, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(baseUri, FileServePath(fs, remote)));
        request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the <c>--rc-serve</c> object path, which is <c>/[&lt;fs&gt;]/&lt;path&gt;</c>. The fs specifier keeps its
    /// colon inside the brackets because rclone reads an unqualified name there as a local path, and
    /// each remote segment is escaped so a file name holding a space, '#' or '%' still resolves.
    /// </summary>
    internal static string FileServePath(string fs, string remote)
    {
        var path = new StringBuilder("[");
        foreach (var segment in fs.Split('/', StringSplitOptions.RemoveEmptyEntries)) path.Append(EscapePathSegment(segment)).Append('/');
        path.Append(']');
        foreach (var segment in remote.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)) path.Append('/').Append(EscapePathSegment(segment));
        return path.ToString();
    }

    /// <summary>A colon is legal in a URL path segment, so it is left readable; rclone's fs syntax depends on it.</summary>
    private static string EscapePathSegment(string segment) =>
        Uri.EscapeDataString(segment).Replace("%3A", ":", StringComparison.OrdinalIgnoreCase);

    public Task MoveFileAsync(string fs, string source, string target, CancellationToken ct = default) => CallAsync("operations/movefile", new { srcFs = fs, srcRemote = source, dstFs = fs, dstRemote = target }, ct);    public Task MoveDirectoryAsync(string sourceFs, string targetFs, CancellationToken ct = default) =>
        RunJobAsync("sync/move", new { srcFs = sourceFs, dstFs = targetFs, createEmptySrcDirs = true, deleteEmptySrcDirs = true, _async = true }, ct);
    public Task DeleteFileAsync(string fs, string remote, CancellationToken ct = default) => CallAsync("operations/deletefile", new { fs, remote }, ct);
    public Task MakeDirectoryAsync(string fs, string remote, CancellationToken ct = default) => CallAsync("operations/mkdir", new { fs, remote }, ct);
    public Task RemoveDirectoryAsync(string fs, string remote, CancellationToken ct = default) => CallAsync("operations/rmdir", new { fs, remote }, ct);

    /// <summary>
    /// Removes a directory <em>and everything under it</em>. Deliberately separate from
    /// <see cref="RemoveDirectoryAsync"/>: the sync engine must never purge, because its confirmed plan
    /// enumerates every descendant explicitly and a purge would silently take filtered or deselected
    /// children with it. An interactive file browser has no such plan — "delete this folder" means the
    /// folder — and on S3, which has no directories at all, purging the key prefix is the only operation
    /// that can express it.
    /// </summary>
    public Task PurgeAsync(string fs, string remote, CancellationToken ct = default) =>
        RunJobAsync("operations/purge", new { fs, remote, _async = true }, ct);

    /// <summary>
    /// Space reported by the backend for <paramref name="fs"/>, through rclone's <c>operations/about</c>.
    /// Returns <see langword="null"/> when the backend reports no numbers at all, which is the normal
    /// answer for SFTP and for S3 buckets whose credentials cannot read the account quota — the caller must
    /// treat that as "unknown", never as "0 of 0".
    /// </summary>
    public async Task<RcloneAbout?> AboutAsync(string fs, CancellationToken ct = default)
    {
        var response = await CallAsync("operations/about", new { fs }, ct);
        if (response.ValueKind != JsonValueKind.Object) return null;
        var total = ReadOptionalLong(response, "total");
        var used = ReadOptionalLong(response, "used");
        var free = ReadOptionalLong(response, "free");
        if (total is null && used is null && free is null) return null;
        return new RcloneAbout(total, used, free);
    }

    private static long? ReadOptionalLong(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed) && parsed >= 0
            ? parsed
            : null;

    public async Task<JsonElement> RunJobAsync(string operation, object payload, CancellationToken ct = default)
    {
        var started = await CallAsync(operation, payload, ct).ConfigureAwait(false);
        // This compatibility path also makes the client usable with older rclone builds
        // and small fake RC servers which complete a request synchronously.
        if (!started.TryGetProperty("jobid", out var idElement) || !idElement.TryGetInt64(out var jobId)) return started;
        try
        {
            while (true)
            {
                var status = await CallAsync("job/status", new { jobid = jobId }, ct).ConfigureAwait(false);
                if (!status.TryGetProperty("finished", out var finished) || !finished.GetBoolean())
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
                    continue;
                }
                var success = !status.TryGetProperty("success", out var succeeded) || succeeded.GetBoolean();
                if (!success)
                {
                    var detail = status.TryGetProperty("error", out var error) ? error.GetString() : null;
                    throw new RcloneException(RcloneFailureClassifier.FromJob(operation, detail ?? "rclone 异步任务失败。"));
                }
                return status.TryGetProperty("output", out var output) ? output.Clone() : EmptyJson();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await StopJobBestEffortAsync(jobId).ConfigureAwait(false);
            throw;
        }
    }

    public Task StopJobAsync(long jobId, CancellationToken ct = default) =>
        CallAsync("job/stop", new { jobid = jobId }, ct);

    private async Task StopJobBestEffortAsync(long jobId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await StopJobAsync(jobId, timeout.Token).ConfigureAwait(false);
        }
        catch { }
    }

    private static JsonElement EmptyJson()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}

public sealed record RcloneTransferStats(long BytesTransferred, long TotalBytes, double BytesPerSecond);
public sealed record RcloneDirectoryEntry(string Path, bool IsDirectory, long Size, DateTimeOffset? ModifiedUtc);

/// <summary>
/// One object as <c>operations/stat</c> reported it. <paramref name="Hashes"/> is keyed by rclone's own
/// hash names (<c>md5</c>, <c>sha1</c>, …) and holds only the ones it was asked for and the backend could
/// give, so an empty map means "this backend told us nothing" rather than "the object is empty".
/// </summary>
public sealed record RcloneStatEntry(string Path, bool IsDirectory, long Size, DateTimeOffset? ModifiedUtc,
    string? MimeType, IReadOnlyDictionary<string, string> Hashes);

/// <summary>
/// Space reported by a backend through <c>operations/about</c>. Every field is optional because backends
/// answer with different subsets — Google Drive reports all three, S3 usually reports nothing.
/// </summary>
public sealed record RcloneAbout(long? Total, long? Used, long? Free);

/// <summary>A byte-range read of one remote object; <paramref name="Length"/> is the object's full size.</summary>
public sealed record RcloneRangeContent(Stream Content, long Length, DateTimeOffset? ModifiedUtc) : IDisposable
{
    public void Dispose() => Content.Dispose();
}

/// <summary>Size and modification time of one remote object, read without transferring content.</summary>
public sealed record RcloneFileIdentity(long Length, DateTimeOffset? ModifiedUtc);

/// <summary>rclone-backed SFTP, Google Drive, or S3 endpoint. Authentication is supplied exclusively by rclone.conf.</summary>
public sealed class RcloneEndpoint(RcloneRcClient client, EndpointProfile profile, EndpointCapabilities capabilities) : IEndpoint, IEndpointStateStorage
{
    public EndpointProfile Profile { get; } = profile;
    public EndpointCapabilities Capabilities { get; } = capabilities;
    // rclone RC's fs parameter is a filesystem specifier, e.g. "drive:"; an unqualified remote name is treated as a local path.
    private string Fs
    {
        get
        {
            var remote = Profile.Remote ?? throw new InvalidOperationException("远程端点缺少 rclone remote 名称。");
            return remote.EndsWith(':') ? remote : remote + ":";
        }
    }
    private string At(string relative) => string.IsNullOrWhiteSpace(Profile.Root) ? relative : $"{Profile.Root.TrimEnd('/')}/{relative.TrimStart('/')}";
    internal RcloneRcClient Client => client;
    internal string FileSystem => Fs;
    internal string RemotePath(string relative) => At(relative);

    public async Task<IReadOnlyList<EntrySnapshot>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<EntrySnapshot>();
        await foreach (var item in ScanEntriesAsync(cancellationToken).ConfigureAwait(false)) items.Add(item);
        return items;
    }

    public async IAsyncEnumerable<EntrySnapshot> ScanEntriesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        SyncRunMetricsHub.Current.IncrementDirectoryScan();
        var response = await client.ListAsync(Fs, Profile.Root, cancellationToken);
        if (!response.TryGetProperty("list", out var list)) yield break;
        foreach (var x in list.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            // RC list may return either a path relative to `remote` or a path prefixed by
            // that remote. Normalize once here; every planner operation must be relative to
            // Profile.Root so At() never produces `root/root/file` on a later copy.
            var path = RelativeToRoot(x.GetProperty("Path").GetString() ?? "");
            if (Excluded(path)) continue;
            var directory = x.TryGetProperty("IsDir", out var isDir) && isDir.GetBoolean();
            if (directory)
            {
                SyncRunMetricsHub.Current.AddEntriesEnumerated(1);
                yield return new(path, EntryKind.Directory, null);
                continue;
            }
            var size = x.TryGetProperty("Size", out var s) ? s.GetInt64() : 0;
            var mod = x.TryGetProperty("ModTime", out var m) && DateTimeOffset.TryParse(m.GetString(), out var parsed) ? parsed : DateTimeOffset.MinValue;
            SyncRunMetricsHub.Current.AddEntriesEnumerated(1);
            yield return new(path, EntryKind.File, new(size, mod, ReadPreferredHash(x)));
        }
    }
    public async Task CopyToAsync(string relativePath, IEndpoint target, string temporaryPath, CancellationToken cancellationToken = default)
    {
        if (target is not RcloneEndpoint remoteTarget) throw new NotSupportedException("跨本地/远程传输由统一传输适配器处理。");
        await client.CopyFileAsync(Fs, At(relativePath), remoteTarget.Fs, remoteTarget.At(temporaryPath), cancellationToken);
    }
    public Task MoveAsync(string from, string to, CancellationToken cancellationToken = default) => client.MoveFileAsync(Fs, At(from), At(to), cancellationToken);
    public Task MoveDirectoryAsync(string from, string to, CancellationToken cancellationToken = default) =>
        client.MoveDirectoryAsync(Fs + At(from), Fs + At(to), cancellationToken);
    public async Task DeleteAsync(string relativePath, bool directory, CancellationToken cancellationToken = default)
    {
        try
        {
            // Keep directory deletion non-recursive. A confirmed plan contains
            // explicit descendant deletes, which the executor runs first. Using
            // purge here could remove filtered or deselected children implicitly.
            if (directory) await client.RemoveDirectoryAsync(Fs, At(relativePath), cancellationToken).ConfigureAwait(false);
            else await client.DeleteFileAsync(Fs, At(relativePath), cancellationToken).ConfigureAwait(false);
        }
        catch (RcloneException ex) when (ex.Failure.Category == RcloneFailureCategory.NotFound)
        {
            // Delete is idempotent: an object that disappeared after comparison is
            // already in the requested state and must not require a preceding list.
        }
    }
    public Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default) => client.MakeDirectoryAsync(Fs, At(relativePath), cancellationToken);
    public async Task<IReadOnlyList<TransferTemporaryFile>> ListTransferTemporaryFilesAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.ListAsync(Fs, Profile.Root, cancellationToken);
        if (!response.TryGetProperty("list", out var list)) return [];
        var result = new List<TransferTemporaryFile>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.TryGetProperty("IsDir", out var directory) && directory.GetBoolean()) continue;
            var path = RelativeToRoot(item.GetProperty("Path").GetString() ?? "");
            if (!SyncInternalPaths.TryGetTransferTemporaryOriginalPath(path, out var original)) continue;
            var size = item.TryGetProperty("Size", out var value) ? value.GetInt64() : 0;
            var modified = item.TryGetProperty("ModTime", out var mod) && DateTimeOffset.TryParse(mod.GetString(), out var parsed) ? parsed : DateTimeOffset.MinValue;
            result.Add(new(path, original, size, modified));
        }
        return result;
    }
    private static bool Excluded(string path) => SyncInternalPaths.IsExcludedFromScan(path);
    public async Task<string?> DownloadStateAsync(string relativePath, string localDirectory, CancellationToken cancellationToken = default)
    {
        var response = await client.ListAsync(Fs, Profile.Root, cancellationToken);
        var exists = response.TryGetProperty("list", out var list) && list.EnumerateArray().Any(x =>
            !x.TryGetProperty("IsDir", out var isDir) || !isDir.GetBoolean()
                ? RelativeToRoot(x.GetProperty("Path").GetString() ?? "").Equals(relativePath, StringComparison.OrdinalIgnoreCase)
                : false);
        if (!exists) return null;
        Directory.CreateDirectory(localDirectory);
        var destination = Path.Combine(localDirectory, Guid.NewGuid().ToString("N") + ".db");
        await client.CopyFileAsync(Fs, At(relativePath), localDirectory, Path.GetFileName(destination), cancellationToken);
        return destination;
    }
    public async Task UploadAndPublishStateAsync(string localPath, string temporaryRelativePath, CancellationToken cancellationToken = default)
    {
        // Do not delete the old state object before publishing the new one. Besides
        // creating a data-loss window, Drive can retain a stale directory listing and
        // make a following movefile stall. rclone's normal copy path stages internally
        // and atomically overwrites the destination after a successful upload.
        await client.CopyFileAsync(Path.GetDirectoryName(localPath)!, Path.GetFileName(localPath), Fs, At(SyncInternalPaths.StateDatabase), cancellationToken);
    }
    private string RelativeToRoot(string path)
    {
        path = path.Trim('/');
        var root = Profile.Root.Trim('/');
        return !string.IsNullOrEmpty(root) && path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            ? path[(root.Length + 1)..]
            : path;
    }

    /// <summary>
    /// Picks the preferred checksum out of an RC list entry's <c>Hashes</c> object. Both the scan and the
    /// single-entry stat must agree on the preference order, otherwise a file's fingerprint would change
    /// depending on which one looked at it.
    /// </summary>
    private static string? ReadPreferredHash(JsonElement entry)
    {
        if (!entry.TryGetProperty("Hashes", out var hashes)) return null;
        foreach (var candidate in new[] { "md5", "sha1", "sha256" })
            if (hashes.TryGetProperty(candidate, out var hash)) return hash.GetString();
        return null;
    }

    public async Task<EntrySnapshot?> StatAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        SyncRunMetricsHub.Current.IncrementStatCall();
        var parent = relativePath.Contains('/') ? relativePath[..relativePath.LastIndexOf('/')] : "";
        var name = relativePath.Contains('/') ? relativePath[(relativePath.LastIndexOf('/') + 1)..] : relativePath;
        try
        {
            var response = await client.CallAsync("operations/list", new { fs = Fs, remote = string.IsNullOrEmpty(parent) ? Profile.Root : At(parent), opt = new { recurse = false } }, cancellationToken);
            if (!response.TryGetProperty("list", out var list)) return null;
            foreach (var x in list.EnumerateArray())
            {
                var candidate = x.TryGetProperty("Name", out var n) ? n.GetString() : x.TryGetProperty("Path", out var p) ? Path.GetFileName(p.GetString() ?? "") : null;
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                {
                    var directory = x.TryGetProperty("IsDir", out var isDir) && isDir.GetBoolean();
                    if (directory) return new(relativePath, EntryKind.Directory, null);
                    var size = x.TryGetProperty("Size", out var s) ? s.GetInt64() : 0;
                    var mod = x.TryGetProperty("ModTime", out var m) && DateTimeOffset.TryParse(m.GetString(), out var parsed) ? parsed : DateTimeOffset.MinValue;
                    return new(relativePath, EntryKind.File, new(size, mod, ReadPreferredHash(x)));
                }
            }
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
