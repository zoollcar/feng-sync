using System.Text.Json;
using System.Text.Json.Serialization;
using FengSync.Core;
using FengSync.Localization;

namespace FengSync.Services;

/// <summary>
/// One endpoint as an export file carries it: everything Feng Sync knows about it that is not a credential,
/// plus the names of the rclone parameters the user has to provide again on import. The values behind those
/// names never leave <c>rclone.conf</c>, so the file is safe to keep in a repository or send to a colleague.
/// </summary>
public sealed record CloudEndpointExport(
    string RemoteName,
    string Type,
    string Provider,
    string Bucket,
    string Subdirectory,
    string? AliasOf,
    string? Group,
    IReadOnlyList<string>? Tags,
    IReadOnlyList<string> ParameterNames);

/// <param name="Version">The export format the file was written with; a reader refuses anything else.</param>
public sealed record CloudEndpointExportBundle(
    int Version,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<CloudEndpointExport> Endpoints);

/// <summary>
/// A new endpoint the import flow has to create, carrying only what an export could provide. The editor
/// opens pre-filled with these values and leaves every credential blank for the user to type.
/// </summary>
public sealed record CloudEndpointDraft(
    EndpointType Kind,
    string RemoteName,
    string Provider,
    string Bucket,
    string Subdirectory,
    string? Group,
    IReadOnlyList<string>? Tags,
    IReadOnlyList<string> RequiredParameters);

/// <summary>What an import file yielded, and everything that made an entry unusable.</summary>
public sealed record CloudEndpointImport(IReadOnlyList<CloudEndpointDraft> Endpoints, IReadOnlyList<string> Errors);

/// <summary>
/// Reads and writes the endpoint export file and validates it on the way back in. Kept apart from the RC
/// calls so the rules — what can be exported without leaking a credential, and what an untrusted file is
/// allowed to become — are testable without rclone.
/// </summary>
public static class CloudEndpointPortability
{
    public const int CurrentVersion = 1;

    public static string Serialize(CloudEndpointExportBundle bundle) =>
        JsonSerializer.Serialize(bundle, CloudEndpointPortabilityJsonContext.Default.CloudEndpointExportBundle);

    /// <summary>Projects one live endpoint into its export shape. Values are never read, only key names.</summary>
    public static CloudEndpointExport ToExport(CloudEndpointAccount account, IReadOnlyList<string> parameterNames) =>
        new(account.Name, account.Type, account.Provider, account.Metadata?.Bucket ?? "",
            account.Metadata?.Subdirectory ?? "", account.Metadata?.AliasOf, account.Metadata?.Group,
            account.Metadata?.Tags, parameterNames);

    /// <summary>
    /// Parses an export file. Every entry is validated on its own so one bad row does not throw away a whole
    /// migration: the good ones come back as drafts and the bad ones as messages the caller can show.
    /// </summary>
    public static CloudEndpointImport Parse(string? json)
    {
        CloudEndpointExportBundle? bundle;
        try { bundle = JsonSerializer.Deserialize(json ?? "", CloudEndpointPortabilityJsonContext.Default.CloudEndpointExportBundle); }
        catch (JsonException ex) { return new([], [Tr.Format("Export.NotValidExport", ex.Message)]); }
        if (bundle is null) return new([], [Tr.F("Export.EmptyOrUnrecognized")]);
        if (bundle.Version != CurrentVersion)
            return new([], [Tr.Format("Export.UnsupportedVersion", bundle.Version, CurrentVersion)]);

        var drafts = new List<CloudEndpointDraft>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in bundle.Endpoints ?? [])
        {
            var kind = EndpointSchemes.TypeForRcloneType(entry.Type);
            if (kind is null || CloudProviderCatalog.TryFor(kind.Value) is null)
            { errors.Add(Tr.Format("Export.UnknownType", entry.RemoteName, entry.Type)); continue; }
            if (ValidateRemoteName(entry.RemoteName) is { } nameError) { errors.Add(nameError); continue; }
            if (!seen.Add(entry.RemoteName)) { errors.Add(Tr.Format("Export.DuplicateName", entry.RemoteName)); continue; }
            if ((entry.ParameterNames ?? []).FirstOrDefault(name => ValidateParameterName(name) is not null) is { } badParameter)
            { errors.Add(Tr.Format("Export.BadParameterName", entry.RemoteName, badParameter)); continue; }
            drafts.Add(new(kind.Value, entry.RemoteName, entry.Provider ?? "", entry.Bucket ?? "",
                entry.Subdirectory ?? "", CloudEndpointOrganization.NormalizeGroup(entry.Group),
                CloudEndpointOrganization.NormalizeTags(entry.Tags), entry.ParameterNames ?? []));
        }
        return new(drafts, errors);
    }

    /// <summary>
    /// The rules rclone itself states for a remote name: it addresses the remote as <c>name:</c>, so a colon
    /// or a path separator would change what the name means. Applied on import because the file is untrusted
    /// input — a crafted name written into the config could fabricate a section.
    /// </summary>
    public static string? ValidateRemoteName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Tr.F("Export.NameEmpty");
        var value = name.Trim();
        if (value != name) return Tr.Format("Export.NameSurroundingSpace", name);
        if (value.Length > 64) return Tr.Format("Export.NameTooLong", value);
        if (value.StartsWith('.')) return Tr.Format("Export.NameStartsWithDot", value);
        foreach (var c in value)
            if (!char.IsLetterOrDigit(c) && c is not ('_' or '-' or '.'))
                return Tr.Format("Export.NameBadCharacter", value, c);
        return null;
    }

    /// <summary>Keys rclone accepts are lower-case identifiers; anything else is not a parameter name.</summary>
    public static string? ValidateParameterName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-')
            ? null
            : Tr.Format("Export.ParameterNameInvalid", name);

    /// <summary>A name that does not collide with what is already configured, so an import never overwrites.</summary>
    public static string UniqueName(string desired, IReadOnlyCollection<string> existing)
    {
        if (!existing.Contains(desired, StringComparer.OrdinalIgnoreCase)) return desired;
        var baseName = desired + "_imported";
        if (!existing.Contains(baseName, StringComparer.OrdinalIgnoreCase)) return baseName;
        for (var index = 2; ; index++)
        {
            var candidate = baseName + index;
            if (!existing.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return candidate;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CloudEndpointExportBundle))]
internal partial class CloudEndpointPortabilityJsonContext : JsonSerializerContext;