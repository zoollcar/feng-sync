using System.Text.Json;
using System.Net.Http.Json;
using System.Text;
using FengSync.Core.Diagnostics;

namespace FengSync.Core;

public enum EndpointType { Local, Sftp, GoogleDrive, S3 }
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
    public async Task<JsonElement> CallAsync(string operation, object payload, CancellationToken ct = default)
    {
        SyncRunMetricsHub.Current.IncrementRcRequest();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, operation))
        { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}")));
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"rclone {operation} failed ({(int)response.StatusCode}): {detail}");
        }
        using var body = await response.Content.ReadAsStreamAsync(ct); using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        return doc.RootElement.Clone();
    }
    public Task<JsonElement> ListAsync(string fs, string remote, CancellationToken ct = default) => CallAsync("operations/list", new { fs, remote, opt = new { recurse = true } }, ct);
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
    public Task CopyFileAsync(string sourceFs, string sourceRemote, string targetFs, string targetRemote, CancellationToken ct = default) => CallAsync("operations/copyfile", new { srcFs = sourceFs, srcRemote = sourceRemote, dstFs = targetFs, dstRemote = targetRemote }, ct);
    public Task MoveFileAsync(string fs, string source, string target, CancellationToken ct = default) => CallAsync("operations/movefile", new { srcFs = fs, srcRemote = source, dstFs = fs, dstRemote = target }, ct);
    public Task MoveDirectoryAsync(string sourceFs, string targetFs, CancellationToken ct = default) =>
        CallAsync("sync/move", new { srcFs = sourceFs, dstFs = targetFs, createEmptySrcDirs = true, deleteEmptySrcDirs = true }, ct);
    public Task DeleteFileAsync(string fs, string remote, CancellationToken ct = default) => CallAsync("operations/deletefile", new { fs, remote }, ct);
    public Task MakeDirectoryAsync(string fs, string remote, CancellationToken ct = default) => CallAsync("operations/mkdir", new { fs, remote }, ct);
    public Task PurgeAsync(string fs, string remote, CancellationToken ct = default) => CallAsync("operations/purge", new { fs, remote }, ct);
}

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
        SyncRunMetricsHub.Current.IncrementDirectoryScan();
        var response = await client.ListAsync(Fs, Profile.Root, cancellationToken);
        if (!response.TryGetProperty("list", out var list)) return [];
        var items = new List<EntrySnapshot>();
        foreach (var x in list.EnumerateArray())
        {
            // RC list may return either a path relative to `remote` or a path prefixed by
            // that remote. Normalize once here; every planner operation must be relative to
            // Profile.Root so At() never produces `root/root/file` on a later copy.
            var path = RelativeToRoot(x.GetProperty("Path").GetString() ?? "");
            if (Excluded(path)) continue;
            var directory = x.TryGetProperty("IsDir", out var isDir) && isDir.GetBoolean();
            if (directory) { items.Add(new(path, EntryKind.Directory, null)); continue; }
            var size = x.TryGetProperty("Size", out var s) ? s.GetInt64() : 0;
            var mod = x.TryGetProperty("ModTime", out var m) && DateTimeOffset.TryParse(m.GetString(), out var parsed) ? parsed : DateTimeOffset.MinValue;
            string? hash = null;
            if (x.TryGetProperty("Hashes", out var hashes))
                foreach (var candidate in new[] { "md5", "sha1", "sha256" }) if (hashes.TryGetProperty(candidate, out var h)) { hash = h.GetString(); break; }
            items.Add(new(path, EntryKind.File, new(size, mod, hash)));
        }
        return items;
    }
    public async Task CopyToAsync(string relativePath, IEndpoint target, string temporaryPath, CancellationToken cancellationToken = default)
    {
        if (target is not RcloneEndpoint remoteTarget) throw new NotSupportedException("跨本地/远程传输由 SyncExecutor 的传输适配器处理。");
        await client.CopyFileAsync(Fs, At(relativePath), remoteTarget.Fs, remoteTarget.At(temporaryPath), cancellationToken);
    }
    public Task MoveAsync(string from, string to, CancellationToken cancellationToken = default) => client.MoveFileAsync(Fs, At(from), At(to), cancellationToken);
    public Task MoveDirectoryAsync(string from, string to, CancellationToken cancellationToken = default) =>
        client.MoveDirectoryAsync(Fs + At(from), Fs + At(to), cancellationToken);
    public Task DeleteAsync(string relativePath, bool directory, CancellationToken cancellationToken = default) => directory ? client.PurgeAsync(Fs, At(relativePath), cancellationToken) : client.DeleteFileAsync(Fs, At(relativePath), cancellationToken);
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
                    return new(relativePath, EntryKind.File, new(size, mod, null));
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
