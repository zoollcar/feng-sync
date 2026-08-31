using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FengSync.Core;

namespace FengSync.Services;

public enum EndpointProbeStatus { NotTested, Succeeded, Failed }

public sealed record S3EndpointValues(string DisplayName, string Provider, string AccessKey, string Secret,
    string Region, string Endpoint, string Bucket, string Subdirectory)
{
    public string RootPath => CloudEndpointMetadata.NormalizeRoot(Bucket, Subdirectory);
}

public sealed class S3EndpointDraft
{
    public int Revision { get; private set; }
    public int? TestedRevision { get; private set; }
    public EndpointProbeStatus ProbeStatus { get; private set; }
    public S3EndpointValues? Values { get; private set; }
    public bool HasCurrentSuccessfulTest => ProbeStatus == EndpointProbeStatus.Succeeded && TestedRevision == Revision;

    public void Update(S3EndpointValues values)
    {
        if (Values == values) return;
        Values = values;
        Revision++;
        ProbeStatus = EndpointProbeStatus.NotTested;
        TestedRevision = null;
    }

    public void RecordProbe(bool succeeded)
    {
        TestedRevision = Revision;
        ProbeStatus = succeeded ? EndpointProbeStatus.Succeeded : EndpointProbeStatus.Failed;
    }
}

public sealed record CloudEndpointMetadata(string RemoteName, string Type, string Provider, string Bucket, string Subdirectory)
{
    [JsonIgnore] public string RootPath => NormalizeRoot(Bucket, Subdirectory);
    public static string NormalizeRoot(string? bucket, string? subdirectory) =>
        string.Join('/', new[] { bucket, subdirectory }.Select(value => (value ?? "").Trim().Trim('/')).Where(value => value.Length > 0));
}

public sealed record CloudEndpointAccount(RcloneAccount Remote, CloudEndpointMetadata? Metadata)
{
    public string Name => Remote.Name;
    public string Type => Remote.Type;
    public string Provider => Metadata?.Provider ?? Remote.Provider;
    public string RootPath => Metadata?.RootPath ?? "";
    public string Display => string.IsNullOrWhiteSpace(RootPath)
        ? $"{Remote.Display}  ·  未设置默认根目录"
        : $"{Remote.Display}  ·  /{RootPath}";
}

public sealed class CloudEndpointMetadataStore(string? path = null)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _path = path ?? Path.Combine(AppDataPaths.Root, "cloud-endpoints.json");

    public async Task<IReadOnlyDictionary<string, CloudEndpointMetadata>> LoadAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try { return await ReadUnsafeAsync(ct); }
        finally { Gate.Release(); }
    }

    public async Task UpsertAsync(CloudEndpointMetadata metadata, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var values = new Dictionary<string, CloudEndpointMetadata>(await ReadUnsafeAsync(ct), StringComparer.OrdinalIgnoreCase)
            { [metadata.RemoteName] = metadata };
            await WriteUnsafeAsync(values.Values, ct);
        }
        finally { Gate.Release(); }
    }

    public async Task DeleteAsync(string remoteName, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var values = new Dictionary<string, CloudEndpointMetadata>(await ReadUnsafeAsync(ct), StringComparer.OrdinalIgnoreCase);
            if (values.Remove(remoteName)) await WriteUnsafeAsync(values.Values, ct);
        }
        finally { Gate.Release(); }
    }

    private async Task<IReadOnlyDictionary<string, CloudEndpointMetadata>> ReadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new Dictionary<string, CloudEndpointMetadata>(StringComparer.OrdinalIgnoreCase);
        await using var stream = File.OpenRead(_path);
        var values = await JsonSerializer.DeserializeAsync(stream, CloudEndpointJsonContext.Default.ListCloudEndpointMetadata, ct) ?? [];
        return values.ToDictionary(value => value.RemoteName, StringComparer.OrdinalIgnoreCase);
    }

    private async Task WriteUnsafeAsync(IEnumerable<CloudEndpointMetadata> values, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, values.OrderBy(value => value.RemoteName).ToList(), CloudEndpointJsonContext.Default.ListCloudEndpointMetadata, ct);
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

[JsonSerializable(typeof(List<CloudEndpointMetadata>))]
internal partial class CloudEndpointJsonContext : JsonSerializerContext;

public sealed record EndpointProbeResult(IReadOnlyList<string> Directories);
