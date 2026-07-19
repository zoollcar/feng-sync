using FengSync.Core;
using FengSync.Core.Automation;
using FengSync.Core.SftpServer;

namespace FengSync.Tests;

public sealed class AutomationAndSftpTests
{
    [Fact]
    public void Automation_runner_allows_remote_two_way_profile_when_a_durable_baseline_store_is_available()
    {
        var profile = SyncProfile.Create("unsafe", "sftp://remote/root", Path.GetTempPath());
        Assert.True(new FengSync.Core.Capabilities.FeatureCapabilityService().Evaluate(profile).CanRun);
    }

    [Fact]
    public async Task Compare_rejects_custom_mode_through_the_shared_gate()
    {
        var profile = SyncProfile.Create("custom", Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "fengsync-custom-" + Guid.NewGuid().ToString("N"))) with { Mode = SyncMode.Custom };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ProfileRunner().CompareAsync(profile));

        Assert.Contains("自定义同步", error.Message);
    }

    [Fact]
    public async Task Task_scheduler_service_uses_profile_id_and_never_puts_secrets_in_command()
    {
        var calls = new List<string>();
        var scheduler = new WindowsTaskSchedulerService((file, arguments, _) => { calls.Add(file + " " + arguments); return Task.FromResult(new ScheduledProcessResult(0, "", "")); });
        await scheduler.CreateOrReplaceAsync(new ScheduledProfileTask("FengSync-test", "profile-42", "C:\\Program Files\\FengSync\\FengSync.Cli.exe", "daily", "secret=must-not-appear"));
        var call = Assert.Single(calls);
        Assert.Contains("/Create", call); Assert.Contains("--profile profile-42", call);
        Assert.DoesNotContain("secret=must-not-appear", call);
    }

    [Fact]
    public async Task Task_scheduler_service_can_start_a_scheduled_profile_for_a_test_run()
    {
        var calls = new List<string>();
        var scheduler = new WindowsTaskSchedulerService((file, arguments, _) =>
        {
            calls.Add(file + " " + arguments);
            return Task.FromResult(new ScheduledProcessResult(0, "", ""));
        });

        await scheduler.TestRunAsync("FengSync-test");

        Assert.Equal("schtasks.exe /Run /TN \"FengSync-test\"", Assert.Single(calls));
    }

    [Theory]
    [InlineData("daily")]
    [InlineData("WEEKLY")]
    public void Scheduled_profile_task_accepts_supported_schtasks_schedules(string schedule)
    {
        new ScheduledProfileTask("FengSync-test", "profile-42", "FengSync.Cli.exe", schedule).Validate();
    }

    [Theory]
    [InlineData("daily & calc")]
    [InlineData("not-a-schedule")]
    public void Scheduled_profile_task_rejects_unsupported_schtasks_schedules(string schedule)
    {
        Assert.Throws<InvalidOperationException>(() => new ScheduledProfileTask("FengSync-test", "profile-42", "FengSync.Cli.exe", schedule).Validate());
    }

    [Fact]
    public async Task Batch_scheduler_never_exceeds_configured_parallelism_and_isolates_failures()
    {
        var running = 0;
        var peak = 0;
        var scheduler = new BatchScheduler(2);
        var results = await scheduler.RunAsync(new Func<CancellationToken, Task<int>>[]
        {
            async _ => { var now = Interlocked.Increment(ref running); peak = Math.Max(peak, now); await Task.Delay(40); Interlocked.Decrement(ref running); return 1; },
            async _ => { var now = Interlocked.Increment(ref running); peak = Math.Max(peak, now); await Task.Delay(40); Interlocked.Decrement(ref running); throw new InvalidOperationException("isolated"); },
            async _ => { var now = Interlocked.Increment(ref running); peak = Math.Max(peak, now); await Task.Delay(40); Interlocked.Decrement(ref running); return 3; }
        });

        Assert.Equal(2, peak);
        Assert.Equal([1, 3], results.Where(x => x.Succeeded).Select(x => x.Value));
        Assert.Single(results, x => !x.Succeeded);
    }

    [Theory]
    [InlineData(AutomationExitCode.Success, 0)]
    [InlineData(AutomationExitCode.Warning, 1)]
    [InlineData(AutomationExitCode.Failure, 2)]
    [InlineData(AutomationExitCode.Conflict, 3)]
    [InlineData(AutomationExitCode.ConfigurationError, 4)]
    public void Automation_exit_codes_are_stable(AutomationExitCode code, int expected) => Assert.Equal(expected, (int)code);

    [Fact]
    public void Sftp_virtual_file_system_cannot_escape_a_share_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "fengsync-sftp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var vfs = new SftpVirtualFileSystem([new SftpShare("docs", root, SftpPermission.ReadWrite)]);
            Assert.Equal(Path.Combine(root, "folder", "report.txt"), vfs.Resolve("/docs/folder/report.txt", SftpFileAccess.Write));
            Assert.Throws<UnauthorizedAccessException>(() => vfs.Resolve("/docs/../../Windows/win.ini", SftpFileAccess.Read));
            Assert.Throws<UnauthorizedAccessException>(() => vfs.Resolve("/other/file.txt", SftpFileAccess.Read));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Sftp_read_only_share_rejects_every_write_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "fengsync-sftp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var vfs = new SftpVirtualFileSystem([new SftpShare("readonly", root, SftpPermission.ReadOnly)]);
            Assert.Throws<UnauthorizedAccessException>(() => vfs.Resolve("/readonly/file.txt", SftpFileAccess.Write));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Passwords_are_salted_hashes_and_never_recoverable()
    {
        var account = SftpAccount.CreatePasswordAccount("alice", "correct horse battery staple");
        Assert.True(account.VerifyPassword("correct horse battery staple"));
        Assert.False(account.VerifyPassword("wrong"));
        Assert.DoesNotContain("correct horse battery staple", account.PasswordHash, StringComparison.Ordinal);
        Assert.NotEmpty(account.PasswordSalt);
    }

    [Fact]
    public void Enabled_sftp_requires_accounts_and_shares_before_it_can_listen()
    {
        Assert.Throws<InvalidOperationException>(() => new SftpServerOptions(Enabled: true).Validate());
        var account = SftpAccount.CreatePasswordAccount("alice", "a real password");
        Assert.Throws<InvalidOperationException>(() => new SftpServerOptions(Enabled: true, Accounts: [account]).Validate());
    }

    [Fact]
    public void Sftp_configuration_rejects_duplicate_accounts_and_overlapping_shares()
    {
        var root = Path.Combine(Path.GetTempPath(), "fengsync-sftp-shares-" + Guid.NewGuid().ToString("N"));
        var child = Path.Combine(root, "child");
        Directory.CreateDirectory(child);
        try
        {
            var alice = SftpAccount.CreatePasswordAccount("alice", "a secure password");
            var duplicate = SftpAccount.CreatePasswordAccount("ALICE", "another secure password");
            Assert.Throws<InvalidOperationException>(() => new SftpServerOptions(true, Accounts: [alice, duplicate], Shares: [new("docs", root, SftpPermission.ReadWrite)]).Validate());
            Assert.Throws<InvalidOperationException>(() => new SftpServerOptions(true, Accounts: [alice], Shares: [new("docs", root, SftpPermission.ReadWrite), new("nested", child, SftpPermission.ReadOnly)]).Validate());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Disabled_sftp_never_starts_a_protocol_host()
    {
        await using var service = new SftpServerHostedService();
        await service.StartAsync(new SftpServerOptions());
        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task Sftp_settings_store_round_trips_non_secret_service_configuration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fengsync-sftp-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "sftp-server.json");
        try
        {
            Directory.CreateDirectory(directory);
            var account = SftpAccount.CreatePasswordAccount("alice", "this password is not persisted as plaintext");
            var options = new SftpServerOptions(true, true, "127.0.0.1", 2223, 4, TimeSpan.FromMinutes(6), NodeExecutablePath: "C:\\FengSync\\node.exe", NodeModulePath: "C:\\FengSync\\node_modules", Accounts: [account], Shares: [new SftpShare("docs", directory, SftpPermission.ReadWrite)]);
            var store = new SftpServerSettingsStore(path);
            await store.SaveAsync(options);
            var restored = await store.LoadAsync();

            Assert.Equal(2223, restored.Port);
            Assert.True(restored.StartWithApplication);
            Assert.Equal("C:\\FengSync\\node.exe", restored.NodeExecutablePath);
            Assert.Equal("C:\\FengSync\\node_modules", restored.NodeModulePath);
            Assert.Equal("alice", Assert.Single(restored.Accounts!).UserName);
            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("this password is not persisted as plaintext", json, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Host_key_store_uses_a_stable_private_path_and_fingerprint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fengsync-host-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SftpHostKeyStore(directory);
            var first = store.GetKeyReference();
            Assert.True(File.Exists(first.Path));
            Assert.NotEqual("SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU", first.Fingerprint);
            File.WriteAllText(first.Path, "test-private-key");
            var second = store.GetKeyReference();

            Assert.Equal(first.Path, second.Path);
            Assert.StartsWith("SHA256:", second.Fingerprint);
            Assert.Equal(second.Fingerprint, store.GetKeyReference().Fingerprint);
            Assert.True(Path.IsPathFullyQualified(second.Path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Sftp_runtime_diagnostics_explain_missing_node_and_pinned_module_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "fengsync-sftp-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = new SftpRuntimeDiagnostics(root).Inspect(new SftpServerOptions(
                NodeExecutablePath: Path.Combine(root, "missing-node.exe"),
                NodeModulePath: Path.Combine(root, "missing-modules")));

            Assert.False(result.CanStart);
            Assert.Contains("Node", result.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ssh2", result.Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Authentication_rate_limiter_blocks_after_configured_failures_then_expires()
    {
        var now = DateTimeOffset.UtcNow;
        var limiter = new SftpAuthenticationRateLimiter(2, TimeSpan.FromMinutes(3), () => now);
        Assert.True(limiter.TryAllow("127.0.0.1", "alice"));
        limiter.RecordFailure("127.0.0.1", "alice");
        Assert.True(limiter.TryAllow("127.0.0.1", "alice"));
        limiter.RecordFailure("127.0.0.1", "alice");
        Assert.False(limiter.TryAllow("127.0.0.1", "alice"));
        now = now.AddMinutes(4);
        Assert.True(limiter.TryAllow("127.0.0.1", "alice"));
    }

    [Fact]
    public async Task Sftp_audit_log_never_persists_password_material()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fengsync-sftp-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "audit.jsonl");
            await new SftpAuditLog(path).AppendAsync(new("alice", "127.0.0.1", "authentication", "/docs/report.txt", "password=not-a-secret", DateTimeOffset.UtcNow));
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("alice", text);
            Assert.DoesNotContain("not-a-secret", text);
            Assert.DoesNotContain("password=", text, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Sftp_configuration_rejects_account_share_grants_that_do_not_exist()
    {
        var root = Path.Combine(Path.GetTempPath(), "fengsync-sftp-grants-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var account = SftpAccount.CreatePasswordAccount("alice", "a secure password") with { AllowedShares = ["missing"] };
            Assert.Throws<InvalidOperationException>(() => new SftpServerOptions(true, Accounts: [account], Shares: [new("docs", root, SftpPermission.ReadWrite)]).Validate());
        }
        finally { Directory.Delete(root, true); }
    }
}
