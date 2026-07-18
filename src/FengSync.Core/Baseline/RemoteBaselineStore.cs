using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FengSync.Core;

/// <summary>
/// Durable, local authority for a pair containing a remote endpoint.  Unlike the legacy
/// sync.fengdb copy stored in two local folders, this record is keyed by both stable endpoint
/// identities and is atomically replaced only after the transfer transaction has succeeded.
/// Credentials and file content never enter this store.
/// </summary>
public sealed class RemoteBaselineStore(string? root = null)
{
    private readonly string _root = root ?? Path.Combine(AppDataPaths.Root, "remote-baselines");

    public async Task<IReadOnlyList<BaselineEntry>?> LoadAsync(IEndpoint left, IEndpoint right, CancellationToken ct = default)
    {
        var path = PathFor(left, right);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        var record = await JsonSerializer.DeserializeAsync<RemoteBaselineRecord>(stream, cancellationToken: ct)
            ?? throw new InvalidDataException("远端同步基线文件为空或已损坏。");
        if (record.Left != EndpointIdentity.From(left) || record.Right != EndpointIdentity.From(right))
            throw new InvalidDataException("远端端点身份或根目录已变化；为避免误判删除，请重新建立基线。");
        return record.Entries;
    }

    public async Task CommitAsync(IEndpoint left, IEndpoint right, CancellationToken ct = default)
    {
        var scans = await Task.WhenAll(left.ScanAsync(ct), right.ScanAsync(ct));
        var l = scans[0].ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var r = scans[1].ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var entries = l.Keys.Concat(r.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(path => new BaselineEntry(path, l.GetValueOrDefault(path), r.GetValueOrDefault(path))).ToList();
        var record = new RemoteBaselineRecord(EndpointIdentity.From(left), EndpointIdentity.From(right), DateTimeOffset.UtcNow, entries);
        Directory.CreateDirectory(_root);
        var destination = PathFor(left, right); var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, record, cancellationToken: ct);
        File.Move(temporary, destination, true);
    }

    private string PathFor(IEndpoint left, IEndpoint right)
    {
        var pair = JsonSerializer.Serialize(new[] { EndpointIdentity.From(left), EndpointIdentity.From(right) });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pair))).ToLowerInvariant();
        return Path.Combine(_root, hash + ".json");
    }
}

public sealed record RemoteBaselineRecord(EndpointIdentity Left, EndpointIdentity Right, DateTimeOffset CommittedUtc, IReadOnlyList<BaselineEntry> Entries);
