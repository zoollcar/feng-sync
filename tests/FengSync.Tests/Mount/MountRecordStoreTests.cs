using System.Text.Json;
using FengSync.Core.Mount;

namespace FengSync.Tests.Mount;

/// <summary>
/// What is remembered about a mount across restarts, and what a restore does with it.
/// </summary>
/// <remarks>
/// The interesting decisions here are all about what a restore must <b>not</b> do: it must not mount anything
/// the user did not mark, it must not claim success when one endpoint stayed down, and it must not forget a
/// mount's auto-mount choice just because the mount was re-created. Each of those is a test below.
/// </remarks>
public sealed class MountRecordStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-mounts-" + Guid.NewGuid().ToString("N"));

    private MountRecordStore NewStore()
    {
        Directory.CreateDirectory(_root);
        return new MountRecordStore(Path.Combine(_root, "mounts.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static MountRecord Record(string mountPoint = "X:", string remote = "nas", bool autoMount = false) =>
        new(remote, "SFTP", mountPoint, MountTargetKind.DriveLetter, "photos", null, autoMount);

    [Fact]
    public async Task A_record_round_trips_including_its_options()
    {
        var store = NewStore();
        var options = new MountOptionsSettings
        {
            VolumeName = "nas",
            CacheMode = MountCacheMode.Full,
            CacheMaxSizeBytes = 3L * 1024 * 1024 * 1024,
            ReadOnly = true
        };

        await store.UpsertAsync(Record() with { Options = options });
        var loaded = Assert.Single(await store.LoadAsync());

        Assert.Equal("X:", loaded.MountPoint);
        Assert.Equal("nas", loaded.RemoteName);
        Assert.Equal("SFTP", loaded.Provider);
        Assert.Equal(MountTargetKind.DriveLetter, loaded.Kind);
        Assert.Equal("photos", loaded.RootPath);
        Assert.NotNull(loaded.CreatedUtc);
        Assert.False(loaded.AutoMount);
        Assert.Equal("nas", loaded.Options!.VolumeName);
        Assert.Equal(MountCacheMode.Full, loaded.Options.CacheMode);
        Assert.Equal(3L * 1024 * 1024 * 1024, loaded.Options.CacheMaxSizeBytes);
        Assert.True(loaded.Options.ReadOnly);
    }

    [Fact]
    public async Task A_record_never_carries_a_credential()
    {
        // The record names the remote and stops there: rclone resolves the credentials from rclone.conf at
        // mount time, which is the same rule the endpoint export follows. This test is the guard on that — a
        // field added here that held a secret would be written to a plain JSON file in the user's app data.
        var properties = typeof(MountRecord).GetProperties().Select(property => property.Name).OrderBy(x => x, StringComparer.Ordinal);

        Assert.Equal(
            ["AutoMount", "CreatedUtc", "Kind", "MountPoint", "Options", "Provider", "RemoteName", "RootPath"],
            properties);
        // Belt and braces: the serialised form must not contain any of the words a secret would be named with.
        var store = NewStore();
        await store.UpsertAsync(Record() with { Options = new MountOptionsSettings { VolumeName = "nas" } });
        var text = await File.ReadAllTextAsync(store.Path);
        foreach (var forbidden in new[] { "password", "pass", "secret", "token", "key" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upserting_the_same_mount_point_replaces_it_whatever_the_letter_case()
    {
        var store = NewStore();
        await store.UpsertAsync(Record("X:"));
        await store.UpsertAsync(Record("x:", remote: "other"));

        var loaded = Assert.Single(await store.LoadAsync());
        Assert.Equal("x:", loaded.MountPoint);
        Assert.Equal("other", loaded.RemoteName);
    }

    [Fact]
    public async Task Remove_forgets_one_mount_point_and_says_whether_it_found_it()
    {
        var store = NewStore();
        await store.UpsertAsync(Record("X:"));
        await store.UpsertAsync(Record("Y:"));

        Assert.True(await store.RemoveAsync("x:"));
        Assert.False(await store.RemoveAsync("Q:"));
        Assert.Equal("Y:", Assert.Single(await store.LoadAsync()).MountPoint);
    }

    [Fact]
    public async Task Remembering_a_mount_that_is_already_recorded_keeps_its_auto_mount_choice()
    {
        // The rule that makes 重新挂载 safe: re-creating a mount is not the user changing their mind about
        // auto-mounting it, so the flag and the original creation time have to survive.
        var store = NewStore();
        await store.UpsertAsync(Record("X:"));
        await store.SetAutoMountAsync("X:", true);
        var created = Assert.Single(await store.LoadAsync()).CreatedUtc;

        await store.RememberAsync(Record("X:", remote: "nas-renamed"));

        var loaded = Assert.Single(await store.LoadAsync());
        Assert.True(loaded.AutoMount);
        Assert.Equal(created, loaded.CreatedUtc);
        Assert.Equal("nas-renamed", loaded.RemoteName);
    }

    [Fact]
    public async Task Remembering_a_brand_new_mount_records_it_with_auto_mount_off()
    {
        // Auto-mount is opt-in: a mount nobody asked to make automatic must not become automatic by existing.
        var store = NewStore();

        await store.RememberAsync(Record("X:"));

        var loaded = Assert.Single(await store.LoadAsync());
        Assert.False(loaded.AutoMount);
        Assert.NotNull(loaded.CreatedUtc);
    }

    [Fact]
    public async Task SetAutoMount_reports_when_there_is_no_such_record()
    {
        var store = NewStore();

        Assert.False(await store.SetAutoMountAsync("X:", true));

        await store.UpsertAsync(Record("X:"));
        Assert.True(await store.SetAutoMountAsync("x:", true));
        Assert.True(Assert.Single(await store.LoadAsync()).AutoMount);
        Assert.True(await store.SetAutoMountAsync("X:", false));
        Assert.False(Assert.Single(await store.LoadAsync()).AutoMount);
    }

    [Fact]
    public async Task A_corrupt_or_absent_file_reads_as_nothing_remembered()
    {
        // Losing the memory of a mount is a convenience failure. Blocking the mount list from opening because
        // of it would be a real one.
        var store = NewStore();
        Assert.Empty(await store.LoadAsync());

        await File.WriteAllTextAsync(store.Path, "{ this is not json");
        Assert.Empty(await store.LoadAsync());

        await File.WriteAllTextAsync(store.Path, "");
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public void Plan_offers_every_remembered_mount_that_is_not_already_live()
    {
        var records = new[] { Record("X:"), Record("Y:", remote: "other") };

        var plan = MountRecovery.Plan(records, ["Y:"], winFspInstalled: true);

        var skipped = Assert.Single(plan, item => !item.CanRestore);
        Assert.Equal("Y:", skipped.Record.MountPoint);
        Assert.Contains("已经处于挂载状态", skipped.SkipReason);
        Assert.True(plan.Single(item => item.Record.MountPoint == "X:").CanRestore);
    }

    [Fact]
    public void Plan_refuses_everything_when_the_mount_driver_is_missing()
    {
        // The "WinFsp 未装" branch. rclone cannot mount without the driver, so offering a restore would be
        // offering something that is guaranteed to fail.
        var plan = MountRecovery.Plan([Record("X:"), Record("Y:")], [], winFspInstalled: false);

        Assert.All(plan, item =>
        {
            Assert.False(item.CanRestore);
            Assert.False(item.Automatic);
            Assert.Contains("WinFsp", item.SkipReason);
        });
    }

    [Fact]
    public void Plan_marks_only_the_records_the_user_made_automatic()
    {
        var records = new[] { Record("X:", autoMount: true), Record("Y:", remote: "other") };

        var plan = MountRecovery.Plan(records, [], winFspInstalled: true);

        Assert.True(plan.Single(item => item.Record.MountPoint == "X:").Automatic);
        Assert.False(plan.Single(item => item.Record.MountPoint == "Y:").Automatic);
    }

    [Fact]
    public void Plan_does_not_call_a_live_mount_automatic_even_when_it_is_marked()
    {
        // Otherwise the startup hook would try to mount a point that is already taken and report a failure for
        // something that is in fact already there.
        var plan = MountRecovery.Plan([Record("X:", autoMount: true)], ["x:"], winFspInstalled: true);

        Assert.False(Assert.Single(plan).Automatic);
    }

    [Fact]
    public void The_pending_and_outcome_lines_never_claim_more_than_happened()
    {
        Assert.Equal("", MountRecovery.DescribePending(0));
        Assert.Contains("1 个", MountRecovery.DescribePending(1));
        Assert.Contains("3 个", MountRecovery.DescribePending(3));

        Assert.Contains("没有需要恢复", MountRecovery.DescribeOutcome(0, 0));
        Assert.Contains("已恢复 2 个", MountRecovery.DescribeOutcome(2, 0));
        Assert.DoesNotContain("失败", MountRecovery.DescribeOutcome(2, 0));
        // Partial success is partial: saying "已恢复" while some endpoints stayed down would hide the failure.
        var partial = MountRecovery.DescribeOutcome(2, 1);
        Assert.Contains("已恢复 2 个", partial);
        Assert.Contains("1 个失败", partial);
        Assert.Contains("恢复失败", MountRecovery.DescribeOutcome(0, 2));
    }

    [Fact]
    public async Task Restore_mounts_every_restorable_record()
    {
        var starter = new RecordingMountStarter();
        var plan = MountRecovery.Plan([Record("X:"), Record("Y:")], [], winFspInstalled: true);

        var outcome = await MountRestore.RunAsync(starter, plan);

        Assert.Equal(2, outcome.Restored);
        Assert.Equal(0, outcome.Failed);
        Assert.Equal(["X:", "Y:"], starter.Requested.Select(request => request.Target.MountPoint));
        Assert.Contains("已恢复 2 个", outcome.Describe());
    }

    [Fact]
    public async Task Restore_carries_each_record_s_own_options_into_the_mount()
    {
        // A restored mount that came back with the defaults instead of the settings it was created with would
        // be a different mount wearing the same drive letter.
        var starter = new RecordingMountStarter();
        var options = new MountOptionsSettings { CacheMode = MountCacheMode.Off, ReadOnly = true };
        var plan = MountRecovery.Plan([Record("X:") with { Options = options }], [], winFspInstalled: true);

        await MountRestore.RunAsync(starter, plan);

        var request = Assert.Single(starter.Requested);
        Assert.Same(options, request.Options);
        Assert.Equal("photos", request.Target.RootPath);
        Assert.Equal("nas", request.Target.RemoteName);
    }

    [Fact]
    public async Task Restore_keeps_going_when_one_endpoint_fails_and_reports_it_as_partial()
    {
        // "网络不通" on one endpoint must not stop the others, and the count has to say so.
        var starter = new RecordingMountStarter { Fail = "Y:" };
        var plan = MountRecovery.Plan([Record("X:"), Record("Y:"), Record("Z:")], [], winFspInstalled: true);

        var outcome = await MountRestore.RunAsync(starter, plan);

        Assert.Equal(2, outcome.Restored);
        Assert.Equal(1, outcome.Failed);
        Assert.Contains("Z:", starter.Requested.Select(request => request.Target.MountPoint));
        var failure = Assert.Single(outcome.Failures);
        Assert.Contains("Y:", failure);
        Assert.Contains("网络不通", failure);
    }

    [Fact]
    public async Task Restore_in_automatic_mode_mounts_only_what_was_marked()
    {
        // The startup hook's contract: the rest is offered by the mount list, never mounted behind the user.
        var starter = new RecordingMountStarter();
        var plan = MountRecovery.Plan([Record("X:", autoMount: true), Record("Y:")], [], winFspInstalled: true);

        var outcome = await MountRestore.RunAsync(starter, plan, automaticOnly: true);

        Assert.Equal(1, outcome.Restored);
        Assert.Equal("X:", Assert.Single(starter.Requested).Target.MountPoint);
    }

    [Fact]
    public async Task Restore_never_attempts_a_record_it_cannot_restore()
    {
        var starter = new RecordingMountStarter();
        var plan = MountRecovery.Plan([Record("X:"), Record("Y:")], ["Y:"], winFspInstalled: true);

        var outcome = await MountRestore.RunAsync(starter, plan);

        Assert.Equal(1, outcome.Restored);
        Assert.Equal("X:", Assert.Single(starter.Requested).Target.MountPoint);
    }

    [Fact]
    public async Task Restore_of_nothing_at_all_is_reported_as_nothing_to_do()
    {
        var outcome = await MountRestore.RunAsync(new RecordingMountStarter(), []);

        Assert.Equal(0, outcome.Restored);
        Assert.Contains("没有需要恢复", outcome.Describe());
    }

    /// <summary>
    /// Stands in for <see cref="RcloneMountService"/>, which will not mount anything at all unless the WinFsp
    /// driver is installed. The loop being tested has no business needing a driver.
    /// </summary>
    private sealed class RecordingMountStarter : IMountStarter
    {
        public List<MountRequest> Requested { get; } = [];
        /// <summary>The mount point whose mount should fail, standing in for an unreachable endpoint.</summary>
        public string? Fail { get; init; }

        public Task<MountResult> MountAsync(MountRequest request, CancellationToken ct = default)
        {
            Requested.Add(request);
            if (string.Equals(request.Target.MountPoint, Fail, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("网络不通，无法连接端点。");
            return Task.FromResult(new MountResult(request.Target.MountPoint, null));
        }
    }
}
