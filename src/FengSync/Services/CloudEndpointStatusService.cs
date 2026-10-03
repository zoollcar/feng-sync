using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using FengSync.Core;
using FengSync.Core.Rclone.Diagnostics;

namespace FengSync.Services;

/// <summary>How an endpoint last answered. <see cref="Unknown"/> is the state before anything was tried.</summary>
public enum CloudEndpointHealth { Unknown, Checking, Connected, NeedsLogin, Unreachable }

/// <summary>
/// The result of one endpoint probe: whether it answered, and how much space it reports. Every quota field
/// is optional because most backends answer nothing at all — an absent quota is "unknown", never "0 of 0".
/// </summary>
public sealed record CloudEndpointStatus(
    CloudEndpointHealth Health,
    string Detail,
    long? TotalBytes = null,
    long? UsedBytes = null,
    long? FreeBytes = null)
{
    public static CloudEndpointStatus Unknown { get; } = new(CloudEndpointHealth.Unknown, "");

    public string HealthText => Health switch
    {
        CloudEndpointHealth.Checking => "检查中…",
        CloudEndpointHealth.Connected => "已连接",
        CloudEndpointHealth.NeedsLogin => "需要重新登录",
        CloudEndpointHealth.Unreachable => "无法连接",
        _ => "未检查"
    };

    /// <summary>Empty when the backend reports no quota, so the row simply shows nothing extra.</summary>
    public string QuotaText => TotalBytes is > 0
        ? $"已用 {CloudEndpointStatusService.FormatBytes(UsedBytes ?? Math.Max(0, TotalBytes.Value - (FreeBytes ?? 0)))} / {CloudEndpointStatusService.FormatBytes(TotalBytes.Value)}"
        : "";
}

/// <summary>
/// Answers the two questions a cloud drive list is expected to answer next to an account: is it still
/// signed in, and how full is it. Both come from calls rclone already exposes; nothing here stores or logs
/// a credential, and the probe deliberately reuses the endpoint's own root so it tests what the user will
/// actually browse rather than the account in the abstract.
/// </summary>
public static class CloudEndpointStatusService
{
    /// <summary>
    /// How many endpoints are probed at once by "测试全部". Bounded because every probe opens its own request
    /// through the same loopback daemon to the same remote service: a long list would otherwise fire them all
    /// at once and turn a status check into a burst that some backends throttle.
    /// </summary>
    public const int ProbeConcurrency = 3;

    /// <summary>
    /// Runs <paramref name="action"/> over every item with at most <paramref name="concurrency"/> in flight and
    /// returns the failures instead of throwing the first one. One endpoint that cannot be reached must not
    /// hide the state of the others — which is the whole point of checking them together. A cancelled token
    /// stops the run and is not reported as a failure: cancelling is the caller's decision, not an endpoint's
    /// fault, and the caller already knows it asked.
    /// </summary>
    public static async Task<IReadOnlyList<Exception>> RunBoundedAsync<T>(
        IReadOnlyList<T> items,
        int concurrency,
        Func<T, Task> action,
        CancellationToken ct = default)
    {
        var failures = new List<Exception>();
        if (items.Count == 0) return failures;
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var running = items.Select(async item =>
        {
            // Whether the slot was taken is tracked rather than assumed: a cancelled token makes the wait
            // throw, and releasing a slot that was never acquired would corrupt the count for every other item.
            var acquired = false;
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                acquired = true;
                await action(item).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { lock (failures) failures.Add(ex); }
            finally { if (acquired) gate.Release(); }
        });
        await Task.WhenAll(running).ConfigureAwait(false);
        return failures;
    }

    /// <summary>
    /// Whether a failure means "sign in again" rather than "the network is down". Read from rclone's own
    /// classification first, because that is parsed from the backend's error rather than guessed from
    /// wording; the keyword pass only exists for errors that reached us unclassified.
    /// </summary>
    public static CloudEndpointHealth Classify(Exception exception) => exception switch
    {
        RcloneException { Failure.Category: RcloneFailureCategory.Authentication } => CloudEndpointHealth.NeedsLogin,
        RcloneException { Failure.Category: RcloneFailureCategory.Permission } => CloudEndpointHealth.NeedsLogin,
        _ => ClassifyMessage(exception.Message)
    };

    /// <summary>
    /// Keyword fallback for an unclassified error. Kept narrow on purpose: calling a plain network outage
    /// "needs re-login" would send the user through a browser authorization that cannot fix anything.
    /// </summary>
    internal static CloudEndpointHealth ClassifyMessage(string? message)
    {
        var text = (message ?? "").ToLowerInvariant();
        if (text.Length == 0) return CloudEndpointHealth.Unreachable;
        string[] loginSignals =
        [
            "invalid_grant", "invalid grant", "token expired", "token has been expired", "expired token",
            "oauth2", "401", "unauthorized", "authentication", "credential", "not signed in", "重新登录", "重新授权", "登录已过期"
        ];
        return loginSignals.Any(text.Contains) ? CloudEndpointHealth.NeedsLogin : CloudEndpointHealth.Unreachable;
    }

    /// <summary>
    /// Lists the endpoint's own root — the cheapest call that proves both the credentials and the configured
    /// path at once — then asks for quota separately, because a backend that cannot report space is still a
    /// perfectly usable endpoint.
    /// </summary>
    public static async Task<CloudEndpointStatus> ProbeAsync(CloudEndpointAccount account, CancellationToken ct = default)
    {
        var client = await AppServices.Current.RcloneHost.GetClientAsync(ct);
        var filesystem = account.Name.EndsWith(':') ? account.Name : account.Name + ":";
        try
        {
            await client.ListDirectoriesAsync(filesystem, account.RootPath, false, ct);
        }
        catch (Exception ex)
        {
            return new(Classify(ex), RcloneUiError.Describe(ex, "cloud-endpoint-probe"));
        }

        var quota = await TryQuotaAsync(client, filesystem, ct);
        return new(CloudEndpointHealth.Connected, "", quota?.Total, quota?.Used, quota?.Free);
    }

    /// <summary>
    /// Space, or <see langword="null"/> when the backend does not report it. rclone answers
    /// <c>about not supported</c> for SFTP and for most S3 configurations, so that failure is the expected
    /// answer rather than an error worth surfacing.
    /// </summary>
    private static async Task<RcloneAbout?> TryQuotaAsync(RcloneRcClient client, string filesystem, CancellationToken ct)
    {
        try { return await client.AboutAsync(filesystem, ct); }
        catch (Exception ex) when (ex is RcloneException or HttpRequestException or OperationCanceledException) { return null; }
    }

    /// <summary>
    /// A human-readable size for a quota. Deliberately not <see cref="FengSync.Core.Configuration.ByteSize"/>:
    /// that one writes exact multiples only, which suits a configured threshold but turns a real quota into
    /// a nine-digit byte count.
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        double value = bytes;
        var unit = "B";
        if (bytes >= 1L << 40) { value = bytes / (double)(1L << 40); unit = "TB"; }
        else if (bytes >= 1L << 30) { value = bytes / (double)(1L << 30); unit = "GB"; }
        else if (bytes >= 1L << 20) { value = bytes / (double)(1L << 20); unit = "MB"; }
        else if (bytes >= 1L << 10) { value = bytes / (double)(1L << 10); unit = "KB"; }
        return value.ToString(value >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + unit;
    }
}

/// <summary>
/// One row of the endpoint list. The account itself is immutable, so the probe result needs somewhere to
/// live; wrapping it here keeps the window's bindings declarative instead of rebuilt by hand on every check.
/// </summary>
public sealed class CloudEndpointRow(CloudEndpointAccount account) : INotifyPropertyChanged
{
    private CloudEndpointStatus _status = CloudEndpointStatus.Unknown;

    public CloudEndpointAccount Account { get; } = account;
    public string Display => Account.Display;
    public string StatusText => Status.HealthText;
    public string QuotaText => Status.QuotaText;
    /// <summary>The failure reason, shown as a tooltip rather than in the row, which stays one line tall.</summary>
    public string Detail => Status.Detail;
    public CloudEndpointHealth Health => Status.Health;

    public CloudEndpointStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Raise(nameof(Status));
            Raise(nameof(StatusText));
            Raise(nameof(QuotaText));
            Raise(nameof(Detail));
            Raise(nameof(Health));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
