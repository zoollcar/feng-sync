using System.Text.Json;
using System.Net.Http.Json;

namespace FengSync.Core;

public enum EndpointType { Local, Sftp, GoogleDrive }
public sealed record EndpointProfile(Guid Id, EndpointType Type, string Root, string? Remote = null, string? Identity = null);
public sealed record EndpointCapabilities(bool StableIds, bool ServerMove, bool EmptyDirectories, TimeSpan ModifiedTimePrecision);

/// <summary>Transport boundary: the planner never needs to know rclone, SFTP or Drive details.</summary>
public interface IEndpoint
{
    EndpointProfile Profile { get; }
    EndpointCapabilities Capabilities { get; }
    Task<IReadOnlyList<EntrySnapshot>> ScanAsync(CancellationToken cancellationToken = default);
    Task CopyToAsync(string relativePath, IEndpoint target, string temporaryPath, CancellationToken cancellationToken = default);
    Task MoveAsync(string from, string to, CancellationToken cancellationToken = default);
    Task DeleteAsync(string relativePath, bool directory, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default);
}

/// <summary>Minimal strongly typed wrapper around rclone's loopback RC API. It deliberately never puts credentials in command lines.</summary>
public sealed class RcloneRcClient(HttpClient http, Uri baseUri, string user, string password)
{
    public async Task<JsonElement> CallAsync(string operation, object payload, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, operation))
        { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}")));
        using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        using var body = await response.Content.ReadAsStreamAsync(ct); using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        return doc.RootElement.Clone();
    }
    public Task<JsonElement> ListAsync(string fs, string remote, CancellationToken ct = default) => CallAsync("operations/list", new { fs, remote, opt = new { recurse = true } }, ct);
    public Task CopyFileAsync(string sourceFs, string sourceRemote, string targetFs, string targetRemote, CancellationToken ct = default) => CallAsync("operations/copyfile", new { srcFs = sourceFs, srcRemote = sourceRemote, dstFs = targetFs, dstRemote = targetRemote }, ct);
    public Task MoveFileAsync(string fs, string source, string target, CancellationToken ct = default) => CallAsync("operations/movefile", new { srcFs = fs, srcRemote = source, dstFs = fs, dstRemote = target }, ct);
    public Task DeleteFileAsync(string fs, string remote, CancellationToken ct = default) => CallAsync("operations/deletefile", new { fs, remote }, ct);
    public Task MakeDirectoryAsync(string fs, string remote, CancellationToken ct = default) => CallAsync("operations/mkdir", new { fs, remote }, ct);
    public Task PurgeAsync(string fs, string remote, CancellationToken ct = default) => CallAsync("operations/purge", new { fs, remote }, ct);
}

/// <summary>rclone-backed SFTP or Google Drive endpoint. Authentication is supplied exclusively by rclone.conf.</summary>
public sealed class RcloneEndpoint(RcloneRcClient client, EndpointProfile profile, EndpointCapabilities capabilities) : IEndpoint
{
    public EndpointProfile Profile { get; } = profile;
    public EndpointCapabilities Capabilities { get; } = capabilities;
    private string Fs => Profile.Remote ?? throw new InvalidOperationException("远程端点缺少 rclone remote 名称。");
    private string At(string relative) => string.IsNullOrWhiteSpace(Profile.Root) ? relative : $"{Profile.Root.TrimEnd('/')}/{relative.TrimStart('/')}";
    internal RcloneRcClient Client => client;
    internal string FileSystem => Fs;
    internal string RemotePath(string relative) => At(relative);

    public async Task<IReadOnlyList<EntrySnapshot>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.ListAsync(Fs, Profile.Root, cancellationToken);
        if (!response.TryGetProperty("list", out var list)) return [];
        var items = new List<EntrySnapshot>();
        foreach (var x in list.EnumerateArray())
        {
            var path = x.GetProperty("Path").GetString() ?? "";
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
    public Task DeleteAsync(string relativePath, bool directory, CancellationToken cancellationToken = default) => directory ? client.PurgeAsync(Fs, At(relativePath), cancellationToken) : client.DeleteFileAsync(Fs, At(relativePath), cancellationToken);
    public Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default) => client.MakeDirectoryAsync(Fs, At(relativePath), cancellationToken);
    private static bool Excluded(string path) => path.Equals("sync.fengdb", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) || path.Contains(".fengsync-", StringComparison.OrdinalIgnoreCase);
}
