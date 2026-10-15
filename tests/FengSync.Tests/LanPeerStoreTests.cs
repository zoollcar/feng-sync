using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// Covers <see cref="LanPeerStore"/>, the list of devices allowed to talk to the listener again without a
/// fresh pairing code.
/// </summary>
/// <remarks>
/// The load-bearing rules are the two that a plain upsert gets wrong: a reconnect must not rewrite when the
/// device was first seen, and an unreadable file must read as "nobody is trusted" instead of either throwing
/// at the window or, worse, trusting everything. The record's own shape is pinned too, because a later field
/// that could hold a token would put a credential in a file the user is encouraged to screenshot.
/// </remarks>
public sealed class LanPeerStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public LanPeerStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fengsync-lan-peers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "lan-peers.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private static LanPeerRecord Peer(string id, string name = "手机", DateTimeOffset? seen = null) =>
        new(id, name, LanProtocol.KindPhone, seen ?? DateTimeOffset.UtcNow, seen ?? DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_first_run_reads_as_no_device_trusted_rather_than_as_an_error()
    {
        Assert.Empty(Directory.GetFileSystemEntries(_root));
        Assert.Empty(await new LanPeerStore(_path).LoadAsync());
    }

    [Fact]
    public async Task A_device_survives_a_second_store_pointed_at_the_same_file()
    {
        await new LanPeerStore(_path).RememberAsync(Peer("aaaaaaaa"), CancellationToken.None);

        var loaded = await new LanPeerStore(_path).LoadAsync();

        var record = Assert.Single(loaded);
        Assert.Equal("aaaaaaaa", record.DeviceId);
        Assert.Equal("手机", record.DeviceName);
        Assert.Equal(LanProtocol.KindPhone, record.Kind);
    }

    [Fact]
    public async Task A_reconnect_keeps_the_first_seen_moment_and_the_addresses_already_gathered()
    {
        var store = new LanPeerStore(_path);
        var first = DateTimeOffset.UtcNow.AddDays(-7);
        await store.RememberAsync(Peer("aaaaaaaa", seen: first) with { SeenAddresses = ["192.168.1.30"] }, CancellationToken.None);

        // A phone that re-paired today is still a device the user paired last week, and the address it used
        // before is what makes the manual-address fallback worth suggesting.
        await store.RememberAsync(Peer("aaaaaaaa", "Phone") with { SeenAddresses = ["192.168.9.9"] }, CancellationToken.None);

        var record = Assert.Single(await store.LoadAsync());
        Assert.Equal(first, record.FirstSeenUtc);
        Assert.Equal(["192.168.1.30"], record.SeenAddresses);
        Assert.Equal("Phone", record.DeviceName);
    }

    [Fact]
    public async Task A_blank_name_arriving_from_a_peer_does_not_erase_the_name_the_user_reads()
    {
        var store = new LanPeerStore(_path);
        await store.RememberAsync(Peer("aaaaaaaa", "小明的手机"), CancellationToken.None);
        await store.RememberAsync(Peer("aaaaaaaa", ""), CancellationToken.None);

        Assert.Equal("小明的手机", Assert.Single(await store.LoadAsync()).DeviceName);
    }

    [Fact]
    public async Task Two_devices_are_two_rows_and_a_repeat_of_one_is_still_one_row()
    {
        var store = new LanPeerStore(_path);
        await store.RememberAsync(Peer("aaaaaaaa"), CancellationToken.None);
        await store.RememberAsync(Peer("bbbbbbbb"), CancellationToken.None);
        await store.RememberAsync(Peer("AAAAAAAA"), CancellationToken.None);

        Assert.Equal(2, (await store.LoadAsync()).Count);
    }

    [Fact]
    public async Task A_full_list_drops_exactly_one_device_and_it_is_the_least_recently_seen()
    {
        var store = new LanPeerStore(_path, maximumPeers: 2);
        var now = DateTimeOffset.UtcNow;
        await store.RememberAsync(Peer("oldest", seen: now.AddMinutes(-30)), CancellationToken.None);
        await store.RememberAsync(Peer("middle", seen: now.AddMinutes(-20)), CancellationToken.None);
        await store.RememberAsync(Peer("newest", seen: now.AddMinutes(-10)), CancellationToken.None);

        var ids = (await store.LoadAsync()).Select(record => record.DeviceId).ToList();
        Assert.Equal(["middle", "newest"], ids);
    }

    [Fact]
    public async Task A_shared_oldest_timestamp_does_not_take_the_whole_tie_with_it()
    {
        // Two devices paired in the same second is ordinary, not a tie to break: dropping every row that
        // compares equal to the minimum would empty the list on a batch of reconnects.
        var stamp = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var store = new LanPeerStore(_path, maximumPeers: 2);
        await store.RememberAsync(Peer("one", seen: stamp), CancellationToken.None);
        await store.RememberAsync(Peer("two", seen: stamp), CancellationToken.None);
        await store.RememberAsync(Peer("three", seen: stamp.AddSeconds(1)), CancellationToken.None);

        Assert.Equal(2, (await store.LoadAsync()).Count);
    }

    [Fact]
    public async Task A_contact_is_recorded_only_for_a_device_the_list_already_holds()
    {
        var store = new LanPeerStore(_path);
        await store.RememberAsync(Peer("aaaaaaaa"), CancellationToken.None);

        await store.RememberContactAsync("zzzzzzzz", "192.168.1.44", 59871, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Single(await store.LoadAsync());

        await store.RememberContactAsync("aaaaaaaa", "192.168.1.44", 59880, DateTimeOffset.UtcNow, CancellationToken.None);
        var record = Assert.Single(await store.LoadAsync());
        Assert.Equal(["192.168.1.44"], record.SeenAddresses);
        Assert.Equal(59880, record.Port);
    }

    [Fact]
    public async Task Seen_addresses_keep_one_entry_per_place_and_the_newest_last()
    {
        var record = Peer("aaaaaaaa") with { SeenAddresses = ["10.0.0.2", "10.0.0.3"] };

        record = record.SeenFrom("10.0.0.4", 59871, DateTimeOffset.UtcNow);
        Assert.Equal(["10.0.0.2", "10.0.0.3", "10.0.0.4"], record.SeenAddresses);

        // A laptop that walked back to an address it used before is not a third place.
        record = record.SeenFrom("10.0.0.2", 59871, DateTimeOffset.UtcNow);
        Assert.Equal(["10.0.0.3", "10.0.0.4", "10.0.0.2"], record.SeenAddresses);
    }

    [Fact]
    public async Task Seen_addresses_are_bounded_by_the_record_s_own_limit()
    {
        var record = Peer("aaaaaaaa");
        for (var index = 0; index < LanPeerRecord.MaximumSeenAddresses + 5; index++)
            record = record.SeenFrom($"10.0.0.{index}", 59871, DateTimeOffset.UtcNow);

        Assert.Equal(LanPeerRecord.MaximumSeenAddresses, record.SeenAddresses.Count);
        Assert.Equal($"10.0.0.{LanPeerRecord.MaximumSeenAddresses + 4}", record.SeenAddresses.Last());
    }

    [Fact]
    public async Task Renaming_and_un_trusting_answer_for_the_device_they_named_and_nothing_else()
    {
        var store = new LanPeerStore(_path);
        await store.RememberAsync(Peer("aaaaaaaa"), CancellationToken.None);

        Assert.True(await store.RenameAsync("AAAAAAAA", "客厅的电脑", CancellationToken.None));
        Assert.False(await store.RenameAsync("missing", "any", CancellationToken.None));
        Assert.Equal("客厅的电脑", Assert.Single(await store.LoadAsync()).DeviceName);

        Assert.False(await store.ForgetAsync("missing", CancellationToken.None));
        Assert.True(await store.ForgetAsync("aaaaaaaa", CancellationToken.None));
        Assert.Empty(await store.LoadAsync());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"DeviceId\":\"a\"}")]
    [InlineData("[]]]")]
    public async Task A_file_this_class_cannot_read_reads_as_no_device_trusted(string content)
    {
        await File.WriteAllTextAsync(_path, content, CancellationToken.None);

        var store = new LanPeerStore(_path);
        Assert.Empty(await store.LoadAsync());

        // And it stays usable: a damaged list must not become a listener that cannot remember anything.
        await store.RememberAsync(Peer("aaaaaaaa"), CancellationToken.None);
        Assert.Single(await store.LoadAsync());
    }

    [Fact]
    public async Task An_empty_file_is_what_a_crash_between_create_and_write_leaves_and_reads_as_nothing()
    {
        await File.WriteAllTextAsync(_path, "", CancellationToken.None);

        Assert.Empty(await new LanPeerStore(_path).LoadAsync());
    }

    [Fact]
    public async Task No_write_ever_leaves_a_temporary_file_beside_the_list()
    {
        var store = new LanPeerStore(_path);
        for (var index = 0; index < 8; index++) await store.RememberAsync(Peer($"device{index:00}"), CancellationToken.None);

        Assert.Equal(["lan-peers.json"], Directory.GetFileSystemEntries(_root).Select(static path => Path.GetFileName(path)!).ToArray());
    }

    [Fact]
    public void The_record_has_no_field_that_could_carry_a_token()
    {
        // The secret for a peer lives in LanPeerSecretStore. This list is the file a user is most likely to
        // open, screenshot, or attach to a report, so the shape itself is the guarantee rather than a comment.
        var fields = typeof(LanPeerRecord)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [nameof(LanPeerRecord.DeviceId), nameof(LanPeerRecord.DeviceName), nameof(LanPeerRecord.FirstSeenUtc),
                nameof(LanPeerRecord.Kind), nameof(LanPeerRecord.LastSeenUtc), nameof(LanPeerRecord.Port),
                nameof(LanPeerRecord.SeenAddresses)],
            fields);
        foreach (var name in new[] { "token", "secret", "password", "key", "credential" })
            Assert.DoesNotContain(fields, field => field.Contains(name, StringComparison.OrdinalIgnoreCase));
    }
}
