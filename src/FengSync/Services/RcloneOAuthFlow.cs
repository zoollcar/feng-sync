using System.Text.Json;
using FengSync.Core.Rclone.Configuration;
using FengSync.Core.Rclone.Diagnostics;
using FengSync.Localization;

namespace FengSync.Services;

/// <summary>
/// Drives rclone's supported RC configuration protocol. The authorization URL is obtained from
/// config/oauthstatus; rclone log text is deliberately never parsed.
/// <para>
/// The flow is the same for every OAuth backend rclone supports — Drive, OneDrive, Dropbox and Box all
/// answer the same <c>config/create</c> / <c>config/update</c> state machine and all expose their
/// authorization URI the same way — so the backend type and its label come from the caller's descriptor
/// rather than being baked in here. Only the wording the user sees differs.
/// </para>
/// </summary>
internal sealed class RcloneOAuthFlow(IRcloneConfigurationApi api, Action<Uri> openBrowser)
{
    private const int MaxSteps = 12;

    /// <summary>Starts authorization for a provider that does not exist in rclone.conf yet.</summary>
    public Task CreateAsync(
        string remoteName,
        CloudProviderDescriptor descriptor,
        IReadOnlyDictionary<string, string> fields,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => RunAsync(remoteName, descriptor, fields, create: true, progress, cancellationToken);

    /// <summary>Re-runs authorization for an endpoint that already exists, leaving its parameters alone.</summary>
    public Task ReconnectAsync(
        string remoteName,
        CloudProviderDescriptor descriptor,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => RunAsync(remoteName, descriptor, new Dictionary<string, string>(), create: false, progress, cancellationToken);

    private async Task RunAsync(
        string remoteName,
        CloudProviderDescriptor descriptor,
        IReadOnlyDictionary<string, string> fields,
        bool create,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!await api.SupportsOAuthControlAsync(cancellationToken))
            throw new NotSupportedException(Tr.F("OAuth.NoStructuredControl"));

        var parameters = descriptor.MergeParameters(fields)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        parameters["config_auth_no_browser"] = "true";
        var state = "";
        var result = "";
        var first = true;
        var browserOpened = false;

        try
        {
            for (var step = 0; step < MaxSteps; step++)
            {
                var options = new RcloneConfigOptions(
                    Obscure: true,
                    NoOutput: true,
                    NonInteractive: string.IsNullOrEmpty(state),
                    Continue: !string.IsNullOrEmpty(state),
                    State: state,
                    Result: result);
                var configure = create && first
                    ? api.CreateAsync(remoteName, descriptor.RcloneType, parameters, options, cancellationToken)
                    : api.UpdateAsync(remoteName, parameters, options, cancellationToken);

                while (!configure.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RcloneOAuthStatus? oauth = null;
                    try { oauth = await api.GetOAuthStatusAsync(cancellationToken); }
                    catch when (!cancellationToken.IsCancellationRequested)
                    {
                        // The server is created asynchronously inside config/create or config/update.
                        // A transient status error before it exists is expected; the configuration
                        // request remains the source of truth for its terminal failure.
                    }
                    if (!browserOpened && oauth is { IsRunning: true, AuthorizationUri: { } authorizationUri })
                    {
                        ValidateAuthorizationUri(authorizationUri);
                        openBrowser(authorizationUri);
                        browserOpened = true;
                        progress?.Report(Tr.Format("OAuth.BrowserOpened", descriptor.DisplayName));
                    }
                    await Task.WhenAny(configure, Task.Delay(100, cancellationToken));
                }

                var output = await configure;
                if (!string.IsNullOrWhiteSpace(output.Error))
                    throw new RcloneException(RcloneFailureClassifier.FromJob(
                        create && first ? "config/create" : "config/update", output.Error));
                if (string.IsNullOrEmpty(output.State)) return;
                if (output.Option is null)
                    throw new InvalidOperationException(Tr.Format("OAuth.StateMissingOption", output.State));

                state = output.State;
                result = Answer(output.Option);
                first = false;
            }

            throw new InvalidOperationException(Tr.F("OAuth.TooManySteps"));
        }
        catch
        {
            await StopOAuthIfRunningAsync();
            throw;
        }
    }

    private async Task StopOAuthIfRunningAsync()
    {
        try
        {
            var status = await api.GetOAuthStatusAsync(CancellationToken.None);
            if (status.IsRunning) await api.StopOAuthAsync(CancellationToken.None);
        }
        catch
        {
            // Preserve the original configuration/cancellation failure. The daemon is privately
            // owned and will also terminate any remaining OAuth listener when disposed.
        }
    }

    private static string Answer(RcloneConfigOption option)
    {
        if (option.Name.Equals("config_is_local", StringComparison.OrdinalIgnoreCase)) return "true";
        if (option.Default is { } defaultValue)
        {
            return defaultValue.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.String => defaultValue.GetString() ?? "",
                JsonValueKind.Number => defaultValue.GetRawText(),
                _ => throw UnsupportedQuestion(option)
            };
        }
        if (!option.Required) return "";
        throw UnsupportedQuestion(option);
    }

    private static InvalidOperationException UnsupportedQuestion(RcloneConfigOption option)
        => new(Tr.Format("OAuth.UnsupportedQuestion", option.Name, option.Help).Trim());

    private static void ValidateAuthorizationUri(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https") || !uri.IsLoopback || !uri.AbsolutePath.Equals("/auth", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Tr.F("OAuth.NonLoopbackAuthUri"));
    }
}
