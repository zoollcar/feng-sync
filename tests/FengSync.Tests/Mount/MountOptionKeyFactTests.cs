using System.Text.Json;
using FengSync.Core;
using FengSync.Core.Mount;
using FengSync.Core.Rclone.Diagnostics;

namespace FengSync.Tests.Mount;

/// <summary>
/// What the bundled rclone v1.75.0 really does with a mount option, measured against the binary itself.
/// </summary>
/// <remarks>
/// <para>
/// This is the half of the option contract that <see cref="MountOptionsPayloadTests"/> cannot prove. That file
/// pins the names this application <i>intends</i> to send; this one pins that those names are the ones rclone
/// actually reads — and it can, because the two failure modes are distinguishable:
/// </para>
/// <list type="bullet">
/// <item>a real option name given a value rclone cannot parse is <b>refused</b> during input decoding, with
/// <c>key "vfsOpt": Reshape failed to Unmarshal: ...</c>;</item>
/// <item>a typo'd name given the very same value is <b>accepted</b>, and the request goes on to mount. No
/// error, no warning, nothing in the log.</item>
/// </list>
/// <para>
/// The second half is the reason the whole feature needs a single source for option names. The decode happens
/// before the mount is attempted, so the refusal half needs no mount point and no WinFsp driver — which is
/// also why these facts can run unconditionally instead of behind the <c>FENGSYNC_TEST_REAL_MOUNT</c> gate.
/// </para>
/// </remarks>
public sealed class MountOptionKeyFactTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-mount-keys-" + Guid.NewGuid().ToString("N"));

    /// <summary>The rclone fs string every call here is addressed with: an alias to a local directory.</summary>
    private const string Fs = "endpoint:";

    /// <summary>A path rclone is willing to create for itself, so the typo'd-name probes get as far as mounting.</summary>
    private string MountPoint => Path.Combine(_root, "mount");

    private RcloneDaemon? _daemon;

    private RcloneRcClient Client => _daemon!.Client;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "endpoint"));
        var config = Path.Combine(_root, "rclone.conf");
        await File.WriteAllTextAsync(config,
            $"[endpoint]\ntype = alias\nremote = {Path.Combine(_root, "endpoint").Replace('\\', '/')}\n");
        _daemon = await RcloneDaemon.StartAsync(RclonePath(), config);
    }

    public async Task DisposeAsync()
    {
        if (_daemon is not null)
        {
            await UnmountAllAsync();
            await _daemon.DisposeAsync();
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    /// <summary>Every option name this application sends, paired with a value rclone cannot read for its type.</summary>
    public static TheoryData<string, string, object> UnreadableValues => new()
    {
        { "vfsOpt", "CacheMode", "bogus" },
        { "vfsOpt", "CacheMaxSize", "abc" },
        { "vfsOpt", "CacheMinFreeSpace", "abc" },
        { "vfsOpt", "DiskSpaceTotalSize", "abc" },
        { "vfsOpt", "CacheMaxAge", "abc" },
        { "vfsOpt", "WriteBack", "abc" },
        { "vfsOpt", "DirCacheTime", "abc" },
        { "vfsOpt", "PollInterval", "abc" },
        { "vfsOpt", "NoChecksum", "notabool" },
        { "vfsOpt", "NoModTime", "notabool" },
        { "vfsOpt", "NoSeek", "notabool" },
        { "vfsOpt", "ReadOnly", "notabool" },
        { "mountOpt", "VolumeName", 123 },
        { "mountOpt", "NetworkMode", "notabool" }
    };

    [Fact]
    public async Task Every_name_Feng_Sync_sends_is_a_field_rclone_reports_for_that_block()
    {
        // The single assertion that catches a misspelling, a rename in a future rclone, and an option put in
        // the wrong block — options/info is rclone's own inventory, grouped by the block each name belongs to.
        var info = await Client.CallAsync("options/info", new { });
        var vfsFields = FieldNames(info, "vfs");
        var mountFields = FieldNames(info, "mount");

        Assert.NotEmpty(vfsFields);
        Assert.NotEmpty(mountFields);

        var sentVfs = MountOptions.CreateVfsOptions(EveryOption).Keys;
        var sentMount = MountOptions.CreateMountOptions(EveryOption).Keys;

        Assert.NotEmpty(sentVfs);
        Assert.NotEmpty(sentMount);
        Assert.Empty(sentVfs.Except(vfsFields, StringComparer.Ordinal));
        Assert.Empty(sentMount.Except(mountFields, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(UnreadableValues))]
    public async Task A_value_rclone_cannot_read_is_refused_which_proves_it_read_the_name(string block, string key, object value)
    {
        // The refusal names the block it came from, which is also how the "readOnly belongs in vfsOpt" rule
        // stays visible: the same key in the other block would not be refused at all.
        var error = await Assert.ThrowsAsync<RcloneException>(
            () => Client.CallAsync("mount/mount", Payload(block, key, value)));

        Assert.Contains("Reshape failed to Unmarshal", error.Failure.TechnicalMessage, StringComparison.Ordinal);
        Assert.Contains(block, error.Failure.TechnicalMessage, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UnreadableValues))]
    public async Task The_same_value_under_a_typo_d_name_is_accepted_which_is_why_the_names_are_pinned(string block, string key, object value)
    {
        // The measured failure mode in one assertion: rclone reads "CacheMaxSizee" as an option it does not know
        // and drops it on the floor. Whatever happens next — the mount succeeding, or failing for a reason that
        // has nothing to do with the option — the point is that the *value* was never looked at.
        var exception = await Record.ExceptionAsync(() => Client.CallAsync("mount/mount", Payload(block, key + "e", value)));
        var message = (exception as RcloneException)?.Failure.TechnicalMessage ?? "";

        Assert.DoesNotContain("Reshape failed to Unmarshal", message, StringComparison.Ordinal);
        await UnmountAllAsync();
    }

    [Fact]
    public async Task The_options_this_application_refuses_to_offer_are_the_ones_rclone_documents_as_unsupported_here()
    {
        // These four are deliberately absent from MountOptionsSettings. The reason is not taste: options/info
        // documents all four as "not supported on Windows", so offering a switch for them would produce exactly
        // the silent no-op the rest of this feature is built to prevent. The inventory is taken from the help
        // text rather than hard-coded, so this fails if a future rclone drops the disclaimer.
        var info = await Client.CallAsync("options/info", new { });
        var unsupported = UnsupportedOnWindows(info);

        foreach (var name in new[] { "Umask", "AllowNonEmpty", "AllowOther", "DefaultPermissions" })
            Assert.Contains(name, unsupported);
    }

    /// <summary>Every option name whose own help text says Windows cannot do it.</summary>
    private static HashSet<string> UnsupportedOnWindows(JsonElement info)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (info.ValueKind != JsonValueKind.Object) return names;
        foreach (var group in info.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var entry in group.Value.EnumerateArray())
            {
                var name = entry.TryGetProperty("FieldName", out var field) ? field.GetString() : null;
                var help = entry.TryGetProperty("Help", out var text) ? text.GetString() : null;
                if (name is not null && help is not null
                    && help.Contains("not supported on Windows", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
        }
        return names;
    }

    /// <summary>A fully populated record, so a name missing from a block shows up as a failed subset check.</summary>
    private static MountOptionsSettings EveryOption => new()
    {
        VolumeName = "nas",
        NetworkMode = true,
        CacheMode = MountCacheMode.Full,
        CacheMaxSizeBytes = 1024L * 1024 * 1024,
        CacheMinFreeSpaceBytes = 1024L * 1024 * 1024,
        CacheMaxAge = TimeSpan.FromHours(1),
        WriteBack = TimeSpan.FromSeconds(5),
        DirCacheTime = TimeSpan.FromMinutes(5),
        PollInterval = TimeSpan.FromMinutes(1),
        DiskSpaceTotalSize = 1024L * 1024 * 1024,
        ReadOnly = true,
        NoSeek = true,
        NoChecksum = false,
        NoModTime = false
    };

    private Dictionary<string, object?> Payload(string block, string key, object? value) => new()
    {
        ["fs"] = Fs,
        ["mountPoint"] = MountPoint,
        ["mountType"] = "cmount",
        [block] = new Dictionary<string, object?> { [key] = value }
    };

    private static HashSet<string> FieldNames(JsonElement info, string group)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (!info.TryGetProperty(group, out var entries) || entries.ValueKind != JsonValueKind.Array) return names;
        foreach (var entry in entries.EnumerateArray())
            if (entry.TryGetProperty("FieldName", out var field) && field.GetString() is { } name) names.Add(name);
        return names;
    }

    private async Task UnmountAllAsync()
    {
        JsonElement mounts;
        try { mounts = await Client.CallAsync("mount/listmounts", new { }, CancellationToken.None); }
        catch (Exception) { return; }
        if (!mounts.TryGetProperty("mountPoints", out var points) || points.ValueKind != JsonValueKind.Array) return;
        foreach (var point in points.EnumerateArray())
        {
            var value = point.ValueKind == JsonValueKind.Object && point.TryGetProperty("MountPoint", out var named)
                ? named.GetString()
                : point.GetString();
            if (string.IsNullOrWhiteSpace(value)) continue;
            try { await Client.CallAsync("mount/unmount", new { mountPoint = value }, CancellationToken.None); }
            catch (Exception) { /* best effort: the daemon is about to be disposed */ }
        }
    }

    private static string RclonePath()
    {
        try { return BundledRclone.ExecutablePath; }
        catch (FileNotFoundException) { }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "FengSync", "Assets", "rclone", "rclone.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("找不到内置 rclone.exe，无法运行挂载选项实证测试。");
    }
}
