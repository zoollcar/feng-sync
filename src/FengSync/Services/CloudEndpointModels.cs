using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FengSync.Core;
using FengSync.Localization;

namespace FengSync.Services;

public enum EndpointProbeStatus { NotTested, Succeeded, Failed }

/// <param name="Provider">Name shown to the user, and what the endpoint metadata records.</param>
/// <param name="RcloneProvider">
/// Value rclone's <c>provider</c> option receives when it differs from <paramref name="Provider"/>,
/// for example the Backblaze preset that rclone only knows as <c>Other</c>.
/// </param>
public sealed record S3EndpointValues(string DisplayName, string Provider, string AccessKey, string Secret,
    string Region, string Endpoint, string Bucket, string Subdirectory, string? RcloneProvider = null)
{
    /// <summary>
    /// The bucket/prefix this endpoint is confined to: <c>bucket/subdirectory</c>. S3 has no directories,
    /// so the "subdirectory" is literally a key prefix, and the endpoint may never address a key outside it.
    /// </summary>
    public string Prefix => CloudEndpointMetadata.NormalizeRoot(Bucket, Subdirectory);
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

/// <param name="AliasOf">
/// Name of the hidden backing <c>s3</c> remote when this endpoint enforces its prefix through an rclone
/// <c>alias</c>. <see langword="null"/> for every endpoint that keeps its prefix only in metadata.
/// </param>
/// <param name="Group">
/// The heading this endpoint is filed under in the manager's list. Empty means ungrouped. Organisation is
/// this application's own concept — rclone has nowhere to store it — so it lives here and never in
/// <c>rclone.conf</c>.
/// </param>
/// <param name="Tags">Free-form labels, matched by the endpoint search.</param>
public sealed record CloudEndpointMetadata(string RemoteName, string Type, string Provider, string Bucket, string Subdirectory,
    string? AliasOf = null, string? Group = null, IReadOnlyList<string>? Tags = null)
{
    /// <summary>The bucket/prefix the user configured, as shown in the UI.</summary>
    [JsonIgnore] public string Prefix => NormalizeRoot(Bucket, Subdirectory);

    /// <summary>
    /// The path relative to the rclone remote that browsing and endpoint URIs use. Empty for an
    /// alias-backed endpoint: rclone already applies the prefix, so repeating it would address
    /// <c>bucket/prefix/bucket/prefix</c>.
    /// </summary>
    [JsonIgnore] public string RootPath => AliasOf is null ? Prefix : "";

    /// <summary>True when rclone — not this application's UI — enforces the prefix.</summary>
    [JsonIgnore] public bool PrefixIsEnforced => AliasOf is not null;

    public static string NormalizeRoot(string? bucket, string? subdirectory) =>
        string.Join('/', new[] { bucket, subdirectory }.Select(value => (value ?? "").Trim().Trim('/')).Where(value => value.Length > 0));
}

public sealed record CloudEndpointAccount(RcloneAccount Remote, CloudEndpointMetadata? Metadata)
{
    public string Name => Remote.Name;
    public string Type => Remote.Type;
    public string Provider => Metadata?.Provider ?? Remote.Provider;
    /// <summary>Path relative to the rclone remote; empty when the prefix lives in an alias.</summary>
    public string RootPath => Metadata?.RootPath ?? "";
    /// <summary>The bucket/prefix shown to the user, and the only thing the endpoint can reach.</summary>
    public string Prefix => Metadata?.Prefix ?? "";
    /// <summary>True when rclone enforces the prefix, so no path can address anything outside it.</summary>
    public bool PrefixIsEnforced => Metadata?.PrefixIsEnforced ?? false;
    public string Display => string.IsNullOrWhiteSpace(Prefix)
        ? Tr.Format("Endpoint.DisplayNoRoot", Remote.Display)
        : $"{Remote.Display}  ·  /{Prefix}";
}

/// <summary>
/// What the endpoint editor needs to open in edit mode: the endpoint as the manager lists it, its
/// flavour, and the rclone parameters currently behind it. An enforced-prefix S3 endpoint keeps its
/// credentials in the hidden backing remote, so <see cref="Parameters"/> is read from there.
/// </summary>
/// <param name="Parameters">
/// The raw values rclone stores. A password field (<c>sftp.pass</c>) is obscured; a merely sensitive
/// field (<c>s3.secret_access_key</c>) is in the clear.
/// </param>
public sealed record CloudEndpointEditTarget(
    CloudEndpointAccount Account,
    EndpointType Kind,
    IReadOnlyDictionary<string, string> Parameters)
{
    /// <summary>The name the endpoint keeps: renaming it would strand every profile that references it.</summary>
    public string RemoteName => Account.Name;

    /// <summary>One stored parameter, or an empty string when rclone does not hold it.</summary>
    public string Parameter(string key) => Parameters.TryGetValue(key, out var value) ? value : "";
}

public sealed class CloudEndpointMetadataStore(string? path = null)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _path = path ?? Path.Combine(AppDataPaths.Root, "cloud-endpoints.json");

    /// <summary>Where the store persists. Exposed so a change that spans several files can back it up first.</summary>
    public string FilePath => _path;

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

    /// <summary>
    /// Drops one entry and writes another in a single atomic write. A rename has to move the record rather
    /// than delete and re-add it: two separate writes would leave the endpoint with no metadata at all if
    /// the process died between them, and the metadata is what remembers an enforced prefix.
    /// </summary>
    public async Task RenameAsync(string remoteName, CloudEndpointMetadata? replacement, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var values = new Dictionary<string, CloudEndpointMetadata>(await ReadUnsafeAsync(ct), StringComparer.OrdinalIgnoreCase);
            values.Remove(remoteName);
            if (replacement is not null) values[replacement.RemoteName] = replacement;
            await WriteUnsafeAsync(values.Values, ct);
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// The stored metadata for one endpoint, or null. Callers that rewrite a record need the current one
    /// first: the fields they do not own (an enforced prefix, the group) would otherwise be dropped.
    /// </summary>
    public async Task<CloudEndpointMetadata?> FindAsync(string remoteName, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var values = await ReadUnsafeAsync(ct);
            return values.TryGetValue(remoteName, out var existing) ? existing : null;
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
