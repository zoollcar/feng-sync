using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// Covers what a credential buys on its own — the question every protected route asks through
/// <see cref="LanSession.AuthenticateAsync"/>.
/// </summary>
/// <remarks>
/// The roster and the token are two files, and only the roster is what the user can see and edit. These tests
/// pin the consequence: a token whose row is gone is not a member, however well it verifies.
/// </remarks>
public sealed class LanSessionAuthTests : IDisposable
{
    private readonly string _root;
    private readonly LanPeerStore _peers;
    private readonly LanPeerSecretStore _secrets;
    private readonly LanSession _session;

    public LanSessionAuthTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fengsync-lan-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "in"));
        _peers = new LanPeerStore(Path.Combine(_root, "peers.json"));
        _secrets = new LanPeerSecretStore(Path.Combine(_root, "secrets.dat"));
        _session = new LanSession(
            new LanOptions(59871, Path.Combine(_root, "in"), true),
            _peers, _secrets, new LanPairingState());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private static LanPeerRecord Peer(string id, string name) =>
        new(id, name, LanProtocol.KindPhone, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_known_device_presenting_its_own_token_is_allowed_and_named_by_its_row()
    {
        await _peers.RememberAsync(Peer("aaaaaaaa", "小明的手机"), CancellationToken.None);
        var token = await _secrets.IssueAsync("aaaaaaaa", CancellationToken.None);

        // The peer claims a different name; the row is what the person agreed to, so the row wins.
        var result = await _session.AuthenticateAsync($"aaaaaaaa:{token}", "别人的手机", LanProtocol.KindWindows,
            "192.168.1.20", 59871, CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal("小明的手机", result.Contact!.DeviceName);
        Assert.Equal(LanProtocol.KindPhone, result.Contact.Kind);
    }

    [Fact]
    public async Task A_token_whose_row_is_gone_is_refused_rather_than_treated_as_a_member()
    {
        // This is what an evicted or forgotten device looks like: the credential is still on disk and still
        // verifies, because the two files are separate.
        var token = await _secrets.IssueAsync("aaaaaaaa", CancellationToken.None);
        Assert.True(await _secrets.VerifyAsync("aaaaaaaa", token, CancellationToken.None));

        var result = await _session.AuthenticateAsync($"aaaaaaaa:{token}", "小明的手机", LanProtocol.KindPhone,
            "192.168.1.20", 59871, CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(LanRefusal.Unpaired, result.Refusal);
        Assert.Null(result.Contact);
    }

    [Fact]
    public async Task A_known_device_with_somebody_else_s_token_is_reported_as_a_changed_identity()
    {
        await _peers.RememberAsync(Peer("aaaaaaaa", "小明的手机"), CancellationToken.None);
        await _secrets.IssueAsync("aaaaaaaa", CancellationToken.None);

        var result = await _session.AuthenticateAsync("aaaaaaaa:" + new string('0', LanPeerSecretStore.TokenBytes * 2),
            "小明的手机", LanProtocol.KindPhone, "192.168.1.20", 59871, CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal(LanRefusal.DeviceIdentityChanged, result.Refusal);
    }
}
