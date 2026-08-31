using System.Text.Json;
using FengSync.Core.Rclone.Lifecycle;

namespace FengSync.Core.Rclone.Configuration;

/// <summary>
/// Strongly typed access to rclone's configuration RC endpoints. Configuration values are kept
/// inside request bodies and are never returned by this adapter except for the non-secret backend type.
/// </summary>
public sealed class RcloneConfigurationClient : IRcloneConfigurationApi
{
    private readonly Func<string, object, CancellationToken, Task<JsonElement>> _call;

    public RcloneConfigurationClient(RcloneRcClient client) => _call = client.CallAsync;
    public RcloneConfigurationClient(IRcloneLifecycleClient client) => _call = client.CallAsync;

    public async Task<bool> SupportsOAuthControlAsync(CancellationToken cancellationToken = default)
    {
        var response = await _call("rc/list", new { }, cancellationToken);
        if (!TryGet(response, "commands", out var commands) || commands.ValueKind != JsonValueKind.Array) return false;
        var available = commands.EnumerateArray().Select(value => value.ValueKind switch
        {
            // Current rclone returns command descriptors with a Path field. Retain the
            // string form for older test doubles and any older compatible build.
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object when TryGet(value, "Path", out var path) => path.GetString(),
            _ => null
        }).Where(value => value is not null).ToHashSet(StringComparer.Ordinal);
        return available.Contains("config/oauthstatus") && available.Contains("config/oauthstop");
    }

    public async Task<IReadOnlyList<string>> ListRemoteNamesAsync(CancellationToken cancellationToken = default)
    {
        var response = await _call("config/listremotes", new { }, cancellationToken);
        if (!TryGet(response, "remotes", out var remotes) || remotes.ValueKind != JsonValueKind.Array) return [];
        return remotes.EnumerateArray()
            .Select(value => (value.GetString() ?? "").TrimEnd(':'))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string?> GetRemoteTypeAsync(string name, CancellationToken cancellationToken = default)
    {
        var response = await _call("config/get", new { name }, cancellationToken);
        return TryGet(response, "type", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;
    }

    public async Task<IReadOnlyList<RcloneS3Provider>> GetS3ProvidersAsync(CancellationToken cancellationToken = default)
    {
        var response = await _call("config/providers", new { }, cancellationToken);
        var backends = response.ValueKind == JsonValueKind.Array ? response
            : TryGet(response, "providers", out var providerValues) ? providerValues : default;
        if (backends.ValueKind != JsonValueKind.Array) return [];
        var s3 = backends.EnumerateArray().FirstOrDefault(value =>
            TryGet(value, "Name", out var name) && name.GetString()?.Equals("s3", StringComparison.OrdinalIgnoreCase) == true);
        if (s3.ValueKind != JsonValueKind.Object || !TryGet(s3, "Options", out var options) || options.ValueKind != JsonValueKind.Array) return [];
        var providerOption = options.EnumerateArray().FirstOrDefault(value =>
            TryGet(value, "Name", out var name) && name.GetString()?.Equals("provider", StringComparison.OrdinalIgnoreCase) == true);
        var regionOption = options.EnumerateArray().FirstOrDefault(value =>
            TryGet(value, "Name", out var name) && name.GetString()?.Equals("region", StringComparison.OrdinalIgnoreCase) == true);
        if (!TryGet(providerOption, "Examples", out var providers) || providers.ValueKind != JsonValueKind.Array) return [];

        var regions = TryGet(regionOption, "Examples", out var regionExamples) && regionExamples.ValueKind == JsonValueKind.Array
            ? regionExamples.EnumerateArray().ToList() : [];
        return providers.EnumerateArray().Select(example =>
        {
            var name = TryGet(example, "Value", out var value) ? value.GetString() ?? "" : "";
            var help = TryGet(example, "Help", out var helpValue) ? helpValue.GetString() ?? "" : "";
            var suggestions = regions.Where(region =>
            {
                if (!TryGet(region, "Provider", out var applies) || applies.ValueKind != JsonValueKind.String) return false;
                return (applies.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Contains(name, StringComparer.OrdinalIgnoreCase);
            }).Select(region => TryGet(region, "Value", out var regionValue) ? regionValue.GetString() ?? "" : "")
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new RcloneS3Provider(name, help, suggestions);
        }).Where(provider => !string.IsNullOrWhiteSpace(provider.Name)).ToList();
    }

    public Task<RcloneConfigState> CreateAsync(
        string name,
        string type,
        IReadOnlyDictionary<string, string> parameters,
        RcloneConfigOptions options,
        CancellationToken cancellationToken = default)
        => ConfigureAsync("config/create", name, type, parameters, options, cancellationToken);

    public Task<RcloneConfigState> UpdateAsync(
        string name,
        IReadOnlyDictionary<string, string> parameters,
        RcloneConfigOptions options,
        CancellationToken cancellationToken = default)
        => ConfigureAsync("config/update", name, null, parameters, options, cancellationToken);

    public async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
        => _ = await _call("config/delete", new { name }, cancellationToken);

    public async Task VerifyAsync(string name, CancellationToken cancellationToken = default)
        => _ = await _call("operations/list", new
        {
            fs = name.EndsWith(':') ? name : name + ":",
            remote = "",
            opt = new { recurse = false, dirsOnly = true }
        }, cancellationToken);

    public async Task<RcloneOAuthStatus> GetOAuthStatusAsync(CancellationToken cancellationToken = default)
    {
        var response = await _call("config/oauthstatus", new { }, cancellationToken);
        var status = TryGet(response, "status", out var statusValue) ? statusValue.GetString() ?? "stopped" : "stopped";
        Uri? authorizationUri = null;
        if (TryGet(response, "authUrl", out var urlValue) &&
            Uri.TryCreate(urlValue.GetString(), UriKind.Absolute, out var parsed))
            authorizationUri = parsed;
        return new(status, authorizationUri);
    }

    public async Task StopOAuthAsync(CancellationToken cancellationToken = default)
        => _ = await _call("config/oauthstop", new { }, cancellationToken);

    private async Task<RcloneConfigState> ConfigureAsync(
        string operation,
        string name,
        string? type,
        IReadOnlyDictionary<string, string> parameters,
        RcloneConfigOptions options,
        CancellationToken cancellationToken)
    {
        object payload = type is null
            ? new { name, parameters, opt = options.ToPayload() }
            : new { name, type, parameters, opt = options.ToPayload() };
        var response = await _call(operation, payload, cancellationToken);
        var state = TryGet(response, "State", out var stateValue) ? stateValue.GetString() ?? "" : "";
        var error = TryGet(response, "Error", out var errorValue) ? errorValue.GetString() ?? "" : "";
        RcloneConfigOption? option = null;
        if (TryGet(response, "Option", out var optionValue) && optionValue.ValueKind == JsonValueKind.Object)
        {
            var optionName = TryGet(optionValue, "Name", out var optionNameValue) ? optionNameValue.GetString() ?? "" : "";
            var help = TryGet(optionValue, "Help", out var helpValue) ? helpValue.GetString() ?? "" : "";
            var required = TryGet(optionValue, "Required", out var requiredValue) && requiredValue.ValueKind == JsonValueKind.True;
            JsonElement? defaultValue = TryGet(optionValue, "Default", out var defaultElement) ? defaultElement.Clone() : null;
            option = new(optionName, help, defaultValue, required);
        }
        return new(state, option, error);
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }
}

public interface IRcloneConfigurationApi
{
    Task<bool> SupportsOAuthControlAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListRemoteNamesAsync(CancellationToken cancellationToken = default);
    Task<string?> GetRemoteTypeAsync(string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RcloneS3Provider>> GetS3ProvidersAsync(CancellationToken cancellationToken = default);
    Task<RcloneConfigState> CreateAsync(string name, string type, IReadOnlyDictionary<string, string> parameters, RcloneConfigOptions options, CancellationToken cancellationToken = default);
    Task<RcloneConfigState> UpdateAsync(string name, IReadOnlyDictionary<string, string> parameters, RcloneConfigOptions options, CancellationToken cancellationToken = default);
    Task DeleteAsync(string name, CancellationToken cancellationToken = default);
    Task VerifyAsync(string name, CancellationToken cancellationToken = default);
    Task<RcloneOAuthStatus> GetOAuthStatusAsync(CancellationToken cancellationToken = default);
    Task StopOAuthAsync(CancellationToken cancellationToken = default);
}

public sealed record RcloneConfigState(string State, RcloneConfigOption? Option, string Error);
public sealed record RcloneConfigOption(string Name, string Help, JsonElement? Default, bool Required);
public sealed record RcloneOAuthStatus(string Status, Uri? AuthorizationUri)
{
    public bool IsRunning => Status.Equals("running", StringComparison.OrdinalIgnoreCase);
}

public sealed record RcloneS3Provider(string Name, string Description, IReadOnlyList<string> RegionSuggestions);

public sealed record RcloneConfigOptions(
    bool Obscure = false,
    bool NoOutput = true,
    bool NonInteractive = false,
    bool Continue = false,
    string State = "",
    string Result = "")
{
    internal object ToPayload() => new
    {
        obscure = Obscure,
        noOutput = NoOutput,
        nonInteractive = NonInteractive,
        @continue = Continue,
        state = State,
        result = Result
    };
}
