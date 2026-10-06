using System.Text;
using FengSync.Core;
using FengSync.Core.Configuration;
using FengSync.Localization;

namespace FengSync.Services;

/// <summary>
/// E3 诊断导出的**全部规则**：脱敏、拼装、措辞。纯函数，不读盘不联网，所以「这份文件里会不会出现凭据」
/// 是一个可以断言的事实，而不是一句承诺。
/// </summary>
/// <remarks>
/// <para>三件事按重要性排：</para>
/// <para>1. <b>永远不导出 <c>rclone.conf</c></b>，也永远不导出端点的参数<b>值</b>。端点那一节只写
/// 名字、类型、Provider 与前缀 —— 这正是 <see cref="CloudEndpointPortability"/> 早就在做的事（那份导出
/// 结构里连一个放值的字段都没有），所以这里复用同一条边界，而不是另立一套「看起来脱敏了」的规则。</para>
/// <para>2. <b>设置项要脱敏，但只有一项需要</b>：<see cref="ApplicationSettings.RcloneProxy"/>。它是明文存的，
/// 而代理地址允许带 userinfo（<c>http://user:pass@proxy:8080</c>），所以要脱敏的是 userinfo 而不是整个值 ——
/// 「这台机器的代理是 proxy.corp:8080」正是排障要的那句话，把它一起抹掉等于让这份文件少了一条线索。</para>
/// <para>3. <b>读不到的东西要说读不到</b>。WinFsp 探测失败、rclone 版本问不出来、今天的日志不存在，
/// 每一样都有自己的那一句；把它们统统写成空值，就会得到一份看上去很完整、其实少了三处的报告。</para>
/// </remarks>
public static class CloudDiagnosticsReport
{
    /// <summary>One rendered line under a section heading.</summary>
    public sealed record Row(string Name, string Value);

    /// <summary>One endpoint, described without any of its parameters' values.</summary>
    public sealed record EndpointRow(string Name, string Type, string Provider, string Prefix);

    /// <summary>Everything the report is made of, gathered by the caller.</summary>
    /// <param name="LogTail">
    /// The tail of today's rclone log, already rendered by <see cref="CloudTransferDiagnostics.ReadLog"/> —
    /// reused rather than re-implemented so 「文件不在」与「日志是空的」 stay two different sentences.
    /// </param>
    public sealed record Snapshot(
        string AppVersion,
        string OsDescription,
        bool Is64BitProcess,
        bool WinFspInstalled,
        string WinFspSummary,
        string RcloneVersion,
        string RclonePath,
        IReadOnlyList<EndpointRow> Endpoints,
        IReadOnlyList<Row> Settings,
        string LogFilePath,
        string LogTail,
        IReadOnlyList<Row> TransferHistory,
        DateTimeOffset GeneratedUtc);

    /// <summary>The file name a support report gets when the user has not typed one.</summary>
    public static string DefaultFileName(DateTimeOffset now) => $"fengsync-diagnostics-{now:yyyy-MM-dd-HHmm}.txt";

    /// <summary>What 「未设置」 says, so an empty setting and a failed read never look the same.</summary>
    public static string NotSet => Tr.F("Diagnostics.NotSet");

    /// <summary>What an answer this build could not obtain says.</summary>
    public static string Unavailable => Tr.F("Diagnostics.Unavailable");

    /// <summary>
    /// The settings section, with the one sensitive value redacted. Every other field is behaviour the user
    /// configured rather than a secret, and a diagnostic that hid them would be hiding the thing being
    /// diagnosed — so they are written out, spelled by name, in the order a reader wants to scan them.
    /// </summary>
    public static IReadOnlyList<Row> SettingsRows(ApplicationSettings settings) =>
    [
        new(Tr.F("Diagnostics.Setting.Concurrency"), settings.CloudMaxConcurrentTransfers.ToString()),
        new(Tr.F("Diagnostics.Setting.Retries"), settings.CloudRetryCount.ToString()),
        new(Tr.F("Diagnostics.Setting.ResumeThreshold"), ByteSize.Format(settings.DefaultResumeMinimumBytes)),
        new(Tr.F("Diagnostics.Setting.DefaultConcurrency"), settings.DefaultMaxConcurrentCopies.ToString()),
        new(Tr.F("Diagnostics.Setting.VerifyCopies"), settings.DefaultVerifyCopies ? Tr.F("Common.Yes") : Tr.F("Common.No")),
        new(Tr.F("Diagnostics.Setting.TimeTolerance"), settings.DefaultTimeToleranceSeconds.ToString()),
        new(Tr.F("Diagnostics.Setting.UploadLimit"), settings.CloudUploadLimitBytesPerSecond > 0 ? ByteSize.Format(settings.CloudUploadLimitBytesPerSecond) + "/s" : NotSet),
        new(Tr.F("Diagnostics.Setting.DownloadLimit"), settings.CloudDownloadLimitBytesPerSecond > 0 ? ByteSize.Format(settings.CloudDownloadLimitBytesPerSecond) + "/s" : NotSet),
        new(Tr.F("Diagnostics.Setting.TimeWindow"), settings.CloudTransferTimeWindow?.Describe() ?? NotSet),
        new(Tr.F("Diagnostics.Setting.AfterTransfer"), settings.CloudAfterTransfer.ToString()),
        new(Tr.F("Diagnostics.Setting.Conflict"), settings.CloudUploadConflict.ToString()),
        new(Tr.F("Diagnostics.Setting.FileView"), settings.CloudBrowserView.ToString()),
        new(Tr.F("Diagnostics.Setting.DownloadDirectory"), settings.CloudDownloadDirectory is { Length: > 0 } directory ? directory : NotSet),
        new(Tr.F("Diagnostics.Setting.MountCache"), settings.MountCacheMaxSizeBytes > 0 ? ByteSize.Format(settings.MountCacheMaxSizeBytes) : NotSet),
        new(Tr.F("Diagnostics.Setting.Proxy"), RedactProxy(settings.RcloneProxy)),
        new(Tr.F("Diagnostics.Setting.Language"), settings.Language.ToString())
    ];

    /// <summary>
    /// Drops the userinfo from a proxy address and keeps the rest. <paramref name="proxy"/> is
    /// <c>host:port</c>, a full URI, or empty; the redaction is keyed on the last <c>@</c> in the authority,
    /// because that is the one character a password cannot appear after. An empty proxy stays empty rather
    /// than becoming a sentence.
    /// </summary>
    public static string RedactProxy(string? proxy)
    {
        var text = (proxy ?? "").Trim();
        if (text.Length == 0) return NotSet;
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        var authorityStart = scheme < 0 ? 0 : scheme + 3;
        var slash = text.IndexOf('/', authorityStart);
        var at = text.LastIndexOf('@', (slash < 0 ? text.Length : slash) - 1);
        if (at < authorityStart) return text;
        // Drop the '@' with the userinfo: keeping it would leave an empty user in front of the host, which
        // reads as a different address than the one being diagnosed.
        return text[..authorityStart] + text[(at + 1)..];
    }

    /// <summary>The endpoints section. Parameters are not here at all — see the class remarks.</summary>
    public static IReadOnlyList<EndpointRow> EndpointRows(IEnumerable<CloudEndpointAccount> accounts) =>
        [.. accounts.Select(account => new EndpointRow(account.Name, account.Type, account.Provider,
            account.Prefix.Length == 0 ? NotSet : "/" + account.Prefix))];

    /// <summary>Renders the whole report. Sections are separated by a blank line, never by a rule of dashes.</summary>
    public static string Compose(Snapshot snapshot)
    {
        var report = new StringBuilder();
        report.AppendLine(Tr.F("Diagnostics.Title"));
        report.AppendLine(Tr.Format("Diagnostics.GeneratedUtc", snapshot.GeneratedUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)));
        report.AppendLine(Tr.F("Diagnostics.NoCredentials"));
        report.AppendLine();

        Section(report, Tr.F("Diagnostics.Section.Environment"));
        Add(report, Tr.F("Diagnostics.Row.AppVersion"), snapshot.AppVersion);
        Add(report, Tr.F("Diagnostics.Row.Os"), snapshot.OsDescription);
        Add(report, Tr.F("Diagnostics.Row.Bitness"), snapshot.Is64BitProcess ? Tr.F("Diagnostics.Bitness64") : Tr.F("Diagnostics.Bitness32"));
        Add(report, "WinFsp", snapshot.WinFspInstalled ? Tr.F("Diagnostics.Installed") : Tr.F("Diagnostics.NotInstalled"));
        if (snapshot.WinFspSummary.Length > 0) Add(report, Tr.F("Diagnostics.Row.WinFspDetail"), snapshot.WinFspSummary);
        Add(report, Tr.F("Diagnostics.Row.RcloneVersion"), snapshot.RcloneVersion.Length == 0 ? Unavailable : snapshot.RcloneVersion);
        Add(report, Tr.F("Diagnostics.Row.RclonePath"), snapshot.RclonePath.Length == 0 ? Unavailable : snapshot.RclonePath);

        Section(report, Tr.F("Diagnostics.Section.Endpoints"));
        if (snapshot.Endpoints.Count == 0) report.AppendLine("  " + NotSet);
        else foreach (var endpoint in snapshot.Endpoints)
            report.AppendLine($"  {endpoint.Name}  ·  {endpoint.Type}  ·  {endpoint.Provider}  ·  {endpoint.Prefix}");

        Section(report, Tr.F("Diagnostics.Section.Settings"));
        foreach (var row in snapshot.Settings) Add(report, row.Name, row.Value);

        Section(report, Tr.F("Diagnostics.Section.TransferHistory"));
        if (snapshot.TransferHistory.Count == 0) report.AppendLine("  " + NotSet);
        else foreach (var row in snapshot.TransferHistory) Add(report, row.Name, row.Value);

        Section(report, Tr.F("Diagnostics.Section.LogTail"));
        report.AppendLine(Tr.Format("Diagnostics.LogFile", snapshot.LogFilePath));
        report.AppendLine(snapshot.LogTail.Length == 0 ? "  " + Unavailable : snapshot.LogTail);
        return report.ToString();
    }

    private static void Section(StringBuilder report, string title)
    {
        report.AppendLine();
        report.AppendLine($"── {title} ──");
    }

    private static void Add(StringBuilder report, string name, string value) =>
        report.AppendLine($"  {name}：{(value.Length == 0 ? Unavailable : value)}");
}
