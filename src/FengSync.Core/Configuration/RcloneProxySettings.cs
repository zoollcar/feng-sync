using FengSync.Core.Rclone.Transport;

namespace FengSync.Core.Configuration;

/// <summary>
/// Turns the persisted proxy setting into what <see cref="RcloneProxyOptions"/> expects.
/// </summary>
/// <remarks>
/// Lives in Core rather than in either shell because the value has to reach the rclone child the same way
/// from every entry point: FengSync.exe and DriveHub.exe each own a daemon, and a future CLI would too.
/// </remarks>
public static class RcloneProxySettings
{
    /// <summary>
    /// The proxy to apply, or null to let <see cref="RcloneProxyResolver"/> fall back to the environment
    /// and then the Windows Internet Settings.
    /// </summary>
    public static RcloneProxyOptions? From(ApplicationSettings settings)
    {
        var value = settings.RcloneProxy;
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        // One address covers every protocol the user might reach, so it goes into all three slots. rclone
        // only reads HTTP_PROXY/HTTPS_PROXY (see RcloneProxyResolver.Build); ALL_PROXY is kept so the value
        // survives for anything else that inspects the child's environment.
        return new RcloneProxyOptions(value, value, value);
    }

    /// <summary>Reads the current settings from disk and resolves the proxy from them.</summary>
    public static async Task<RcloneProxyOptions?> LoadAsync(CancellationToken ct = default) =>
        From((await new SettingsStore().LoadAsync(ct).ConfigureAwait(false)).Settings);
}
