using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// Covers <see cref="LanDeviceIdentity"/>, and in particular the one thing about this machine a person is
/// allowed to change: the name peers list it under.
/// </summary>
/// <remarks>
/// <para>
/// The load-bearing property is that a rename changes the name and nothing else. The device id is what every
/// peer's trust is keyed on, so a rename that minted a fresh identity would look to the other window like a
/// stranger arriving with a new pairing code and a first write to approve — the user would read 改名 as the
/// reason their phone stopped working.
/// </para>
/// <para>
/// The second property is that this is the only store for the name. The listener holds the instance it was
/// started with, so an identity that came back new on a rename would keep answering <c>/hello</c> with the name
/// the user just threw away, and a window that reopened would read the machine name again.
/// </para>
/// </remarks>
public sealed class LanDeviceIdentityTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public LanDeviceIdentityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fengsync-lan-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "device.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task A_name_chosen_once_is_the_name_the_next_window_reads_under_the_same_id()
    {
        var first = await LanDeviceIdentity.LoadOrCreateAsync(_path);
        var before = first.DeviceId;

        Assert.Equal("台式机上自己取的名字", await first.RenameAsync("台式机上自己取的名字"));

        var reopened = await LanDeviceIdentity.LoadOrCreateAsync(_path);
        Assert.Equal("台式机上自己取的名字", reopened.DeviceName);
        // The id is the whole cost of a rename: a peer's trust is keyed on it, so keeping it is what stops 改名
        // reading, on the other side, as a stranger that has to be paired and approved again.
        Assert.Equal(before, reopened.DeviceId);
        Assert.Equal(LanProtocol.KindWindows, reopened.Kind);
    }

    [Fact]
    public async Task A_rename_reaches_the_instance_the_listener_is_already_holding()
    {
        var identity = await LanDeviceIdentity.LoadOrCreateAsync(_path);

        await identity.RenameAsync("客厅的小电脑");

        // Not a new object: the hello reply, the QR, the discovery beacon and the room's own author rows all read
        // this instance, and a rename that produced a second identity would leave every one of them answering
        // with the name the user has just discarded.
        Assert.Equal("客厅的小电脑", identity.DeviceName);
        Assert.Equal("客厅的小电脑", (await LanDeviceIdentity.LoadOrCreateAsync(_path)).DeviceName);
    }

    [Fact]
    public async Task A_name_longer_than_a_row_can_hold_is_kept_shortened_and_answers_with_what_it_kept()
    {
        var identity = await LanDeviceIdentity.LoadOrCreateAsync(_path);

        var kept = await identity.RenameAsync(new string('名', 20) + "\n\t" + new string('字', 40));

        // One rule for both directions: a peer's claimed name and this machine's own choice are the same kind of
        // string, cleaned before being cut, and the caller is told what was stored rather than left showing a
        // name nobody else in the room reads.
        Assert.Equal(new string('名', 20) + new string('字', 28), kept);
        Assert.Equal(LanProtocol.MaximumNameLength, kept.Length);
        Assert.Equal(kept, identity.DeviceName);
        Assert.Equal(kept, (await LanDeviceIdentity.LoadOrCreateAsync(_path)).DeviceName);
    }

    [Fact]
    public async Task An_empty_choice_is_not_a_rename_and_costs_the_file_nothing()
    {
        var identity = await LanDeviceIdentity.LoadOrCreateAsync(_path);
        await identity.RenameAsync("已经取好的名字");
        var written = File.GetLastWriteTimeUtc(_path);

        var kept = await identity.RenameAsync("   ");

        // Which name to fall back to is the window's decision, since this library holds no table of those strings,
        // so the honest answer is the name still in effect — and nothing is written over the file at all, because
        // a half-written identity is the one state this file cannot be recovered from.
        Assert.Equal("已经取好的名字", kept);
        Assert.Equal("已经取好的名字", identity.DeviceName);
        Assert.Equal(written, File.GetLastWriteTimeUtc(_path));
    }
}
