using System.Text.Json;
using FengSync.Core.Rclone.Configuration;
using FengSync.Core.Rclone.Diagnostics;

namespace FengSync.Services;

/// <summary>
/// Drives rclone's supported RC configuration protocol. The authorization URL is obtained from
/// config/oauthstatus; rclone log text is deliberately never parsed.
/// </summary>
internal sealed class RcloneOAuthFlow(IRcloneConfigurationApi api, Action<Uri> openBrowser)
{
    private const int MaxSteps = 12;

    public Task CreateGoogleDriveAsync(
        string remoteName,
        IReadOnlyDictionary<string, string> fields,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => RunAsync(remoteName, fields, create: true, progress, cancellationToken);

    public Task ReconnectGoogleDriveAsync(
        string remoteName,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => RunAsync(remoteName, new Dictionary<string, string>(), create: false, progress, cancellationToken);

    private async Task RunAsync(
        string remoteName,
        IReadOnlyDictionary<string, string> fields,
        bool create,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!await api.SupportsOAuthControlAsync(cancellationToken))
            throw new NotSupportedException("内置 rclone 不支持结构化 OAuth 状态接口（config/oauthstatus、config/oauthstop）。请升级 rclone 后重试；Feng Sync 不会回退到解析命令行文本。");

        var parameters = fields
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
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
                    ? api.CreateAsync(remoteName, "drive", parameters, options, cancellationToken)
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
                        progress?.Report("浏览器已打开，请完成 Google 授权…");
                    }
                    await Task.WhenAny(configure, Task.Delay(100, cancellationToken));
                }

                var output = await configure;
                if (!string.IsNullOrWhiteSpace(output.Error))
                    throw new RcloneException(RcloneFailureClassifier.FromJob(
                        create && first ? "config/create" : "config/update", output.Error));
                if (string.IsNullOrEmpty(output.State)) return;
                if (output.Option is null)
                    throw new InvalidOperationException($"rclone OAuth 状态“{output.State}”缺少待回答选项。");

                state = output.State;
                result = Answer(output.Option);
                first = false;
            }

            throw new InvalidOperationException("rclone OAuth 配置步骤过多，已停止以避免无限循环。");
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
        => new($"rclone OAuth 要求应用无法自动回答的选项：{option.Name}。{Environment.NewLine}{option.Help}".Trim());

    private static void ValidateAuthorizationUri(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https") || !uri.IsLoopback || !uri.AbsolutePath.Equals("/auth", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("rclone 返回了非本机 OAuth 授权地址，已拒绝打开。请检查内置 rclone 的完整性。");
    }
}
