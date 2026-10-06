using System.Reflection;
using FengSync.Core;
using FengSync.Core.Configuration;
using FengSync.Services;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// E3 诊断导出的规则。这份文件会被用户发给 somebody who can help，所以「它里面会不会出现凭据」是一个
/// 可以断言的事实，而不是一句承诺。
/// </summary>
/// <remarks>
/// 断言落在不随界面语言变化的事实上：哪一段被抹掉了、哪一段被留着、哪一句话是「未设置」、哪一句是
/// 「取不到」。措辞本身由 <c>LanguageCoverageTests</c> 和资源表管。
/// </remarks>
public sealed class CloudDiagnosticsReportTests
{
    [Fact]
    public void An_endpoint_row_has_nowhere_to_put_a_credential()
    {
        // 这是整份报告不泄漏凭据的根本原因，比任何一句「我们不导出」的承诺都硬：类型里根本没有放值的字段。
        // 有人日后加一个 Parameters 字段，这一条立刻失败。
        var fields = typeof(CloudDiagnosticsReport.EndpointRow)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Name", "Prefix", "Provider", "Type"], fields);
    }

    [Fact]
    public void An_endpoint_row_carries_only_what_is_not_a_credential()
    {
        var rows = CloudDiagnosticsReport.EndpointRows([Account("work", "drive", "Google Drive", "team/docs")]);

        var row = Assert.Single(rows);
        Assert.Equal("work", row.Name);
        Assert.Equal("drive", row.Type);
        Assert.Equal("Google Drive", row.Provider);
        Assert.Equal("/team/docs", row.Prefix);
    }

    [Fact]
    public void An_endpoint_with_no_prefix_says_so_rather_than_showing_a_stray_slash()
    {
        var rows = CloudDiagnosticsReport.EndpointRows([Account("work", "drive", "Google Drive", "")]);

        Assert.Equal(CloudDiagnosticsReport.NotSet, Assert.Single(rows).Prefix);
    }

    [Fact]
    public void The_proxy_password_is_removed_and_the_host_is_kept()
    {
        // 代理地址允许带 userinfo（http://user:pass@proxy:8080）。要抹的是 userinfo 而不是整个值 ——
        // 「这台机器的代理是 proxy.corp:8080」正是排障要的那那句话。
        var redacted = CloudDiagnosticsReport.RedactProxy("http://user:s3cret@proxy.corp:8080");

        Assert.Equal("http://proxy.corp:8080", redacted);
        Assert.DoesNotContain("s3cret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("user", redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_proxy_without_a_password_is_left_exactly_as_it_is()
    {
        Assert.Equal("proxy.corp:8080", CloudDiagnosticsReport.RedactProxy("proxy.corp:8080"));
        Assert.Equal("http://proxy.corp:8080", CloudDiagnosticsReport.RedactProxy("http://proxy.corp:8080"));
    }

    [Fact]
    public void An_at_sign_after_the_path_is_not_mistaken_for_a_password_separator()
    {
        // 密码不可能出现在斜杠之后，所以 authority 里最后一个 @ 才是分隔符；路径里的 @ 属于查询串的一部分。
        var redacted = CloudDiagnosticsReport.RedactProxy("http://user:pass@proxy:8080/path?to=a@b");

        Assert.Equal("http://proxy:8080/path?to=a@b", redacted);
        Assert.DoesNotContain("pass", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_proxy_says_it_is_not_set_rather_than_becoming_an_empty_row()
    {
        Assert.Equal(CloudDiagnosticsReport.NotSet, CloudDiagnosticsReport.RedactProxy(null));
        Assert.Equal(CloudDiagnosticsReport.NotSet, CloudDiagnosticsReport.RedactProxy("   "));
    }

    [Fact]
    public void The_settings_section_writes_out_what_the_user_configured_and_hides_only_the_password()
    {
        // 诊断报告把设置项全抹掉，就等于把要诊断的东西藏起来了。这些字段是行为不是秘密，逐条写出来。
        var settings = new ApplicationSettings
        {
            RcloneProxy = "http://user:s3cret@proxy.corp:8080",
            CloudMaxConcurrentTransfers = 7,
            CloudRetryCount = 3,
            CloudDownloadDirectory = @"D:\Downloads",
        };

        var rows = CloudDiagnosticsReport.SettingsRows(settings);
        var byName = rows.ToDictionary(row => row.Name, row => row.Value);
        var proxy = rows.Single(row => row.Value.Contains("proxy.corp:8080", StringComparison.Ordinal)).Value;
        var concurrency = rows.Single(row => row.Value == "7").Value;

        Assert.DoesNotContain("s3cret", string.Join('\n', byName.Values), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(proxy));
        Assert.False(string.IsNullOrWhiteSpace(concurrency));
        Assert.Contains(@"D:\Downloads", byName.Values);
        // 未设置与取不到是两句不同的话，不能混成一句。
        Assert.Contains(CloudDiagnosticsReport.NotSet, byName.Values);
    }

    [Fact]
    public void A_limit_that_is_off_says_not_set_rather_than_zero()
    {
        // 0 与「没设」在界面上是两件事：0 是显式关掉了限速，没设是沿用默认。写成 0 会让人以为前者。
        // 断言落在值上而不落在行名上：限速是唯一带 /s 后缀的那一类。
        var rows = CloudDiagnosticsReport.SettingsRows(new ApplicationSettings());

        Assert.DoesNotContain(rows, row => row.Value.EndsWith("/s", StringComparison.Ordinal));
        // 两条限速各自都要说「未设置」，而不是留空或者写 0。
        Assert.True(rows.Count(row => row.Value == CloudDiagnosticsReport.NotSet) >= 2);
    }

    [Fact]
    public void The_composed_report_contains_no_credential_even_when_the_settings_carry_one()
    {
        var report = CloudDiagnosticsReport.Compose(Snapshot(
            endpoints: [Account("work", "drive", "Google Drive", "team/docs")],
            settings: new ApplicationSettings { RcloneProxy = "http://user:s3cret@proxy.corp:8080" }));

        Assert.DoesNotContain("s3cret", report, StringComparison.Ordinal);
        Assert.DoesNotContain("user:", report, StringComparison.Ordinal);
        // 而读报告的人必须被明确告知这件事，否则他无法判断这份文件能不能发出去。
        Assert.Contains("rclone.conf", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Something_this_build_could_not_obtain_says_so_rather_than_leaving_a_blank()
    {
        // 问不到 rclone 版本、今天没有日志、没装 WinFsp —— 统统写成空值的话，就得到一份看上去很完整、
        // 其实少了三处的报告，而排障的人会照着不存在的那几行找下去。
        var report = CloudDiagnosticsReport.Compose(Snapshot(rcloneVersion: "", rclonePath: "", logTail: ""));

        Assert.Contains(CloudDiagnosticsReport.Unavailable, report, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_section_says_it_is_empty_rather_than_being_absent()
    {
        var report = CloudDiagnosticsReport.Compose(Snapshot(endpoints: [], history: []));

        Assert.Contains(CloudDiagnosticsReport.NotSet, report, StringComparison.Ordinal);
        // 「没有端点」和「端点取不到」不是同一句话。
        Assert.NotEqual(CloudDiagnosticsReport.NotSet, CloudDiagnosticsReport.Unavailable);
    }

    [Fact]
    public void The_sections_come_out_in_the_order_a_reader_wants_to_scan_them()
    {
        var report = CloudDiagnosticsReport.Compose(Snapshot());

        int At(string needle) => report.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(At("Feng Sync") >= 0, "the report does not say which program it came from");
        // 先说这台机器上有什么，再说这个程序看见了什么，最后是这个程序说过的话。三个 ASCII 锚点 ——
        // 版本、端点名、日志文件路径 —— 在两种语言里都一模一样，所以顺序是真的被测到了。
        Assert.True(At("1.2.3") < At("work"), "the environment should come before the endpoints");
        Assert.True(At("work") < At(@"C:\rclone\rclone.log"), "the endpoints should come before the log");
        Assert.True(At(@"C:\rclone\rclone.log") < At("NOTICE: transferred"),
            "the log file's path should be stated before its tail, so a reader knows what they are looking at");
    }

    [Fact]
    public void The_report_is_never_blank()
    {
        var report = CloudDiagnosticsReport.Compose(Snapshot());

        Assert.False(string.IsNullOrWhiteSpace(report));
        Assert.DoesNotContain("  \n", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_file_name_is_one_windows_accepts()
    {
        // 默认名带上了时间戳，而 Windows 的文件名里不能有冒号 —— 这正是那个格式串里没有冒号的原因。
        var name = CloudDiagnosticsReport.DefaultFileName(new DateTimeOffset(2026, 10, 5, 14, 30, 0, TimeSpan.Zero));

        Assert.StartsWith("fengsync-diagnostics-", name, StringComparison.Ordinal);
        Assert.EndsWith(".txt", name, StringComparison.Ordinal);
        Assert.DoesNotContain(name, Path.GetInvalidFileNameChars());
    }

    [Fact]
    public void Two_reports_a_minute_apart_do_not_share_a_file_name()
    {
        // 排障的人导出两次「就是现在」是有可能的；两份覆盖成一份，第二份就没了。
        var first = CloudDiagnosticsReport.DefaultFileName(new DateTimeOffset(2026, 10, 5, 14, 30, 0, TimeSpan.Zero));
        var second = CloudDiagnosticsReport.DefaultFileName(new DateTimeOffset(2026, 10, 5, 14, 31, 0, TimeSpan.Zero));

        Assert.NotEqual(first, second);
    }

    // ------------------------------------------------------------------ helpers

    private static CloudEndpointAccount Account(string name, string type, string provider, string prefix) =>
        new(new RcloneAccount(name, type), new CloudEndpointMetadata(name, type, provider, Bucket: "", Subdirectory: prefix));

    /// <summary>A snapshot with everything absent, so each test fills in only the part it is about.</summary>
    private static CloudDiagnosticsReport.Snapshot Snapshot(
        IReadOnlyList<CloudEndpointAccount>? endpoints = null,
        ApplicationSettings? settings = null,
        string rcloneVersion = "rclone v1.70.0",
        string rclonePath = @"C:\rclone\rclone.exe",
        string logTail = "NOTICE: transferred",
        IReadOnlyList<CloudDiagnosticsReport.Row>? history = null) =>
        new(
            AppVersion: "1.2.3",
            OsDescription: "Windows 11",
            Is64BitProcess: true,
            WinFspInstalled: true,
            WinFspSummary: "WinFsp 2.0",
            RcloneVersion: rcloneVersion,
            RclonePath: rclonePath,
            Endpoints: endpoints is null
                ? CloudDiagnosticsReport.EndpointRows([Account("work", "drive", "Google Drive", "team/docs")])
                : CloudDiagnosticsReport.EndpointRows(endpoints),
            Settings: CloudDiagnosticsReport.SettingsRows(settings ?? new ApplicationSettings()),
            LogFilePath: @"C:\rclone\rclone.log",
            LogTail: logTail,
            TransferHistory: history ?? [new CloudDiagnosticsReport.Row("2026-10-05 12:00", "3 files")],
            GeneratedUtc: new DateTimeOffset(2026, 10, 5, 14, 30, 0, TimeSpan.Zero));
}
