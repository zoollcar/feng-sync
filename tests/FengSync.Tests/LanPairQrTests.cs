using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// The printed half of pairing: what a scanner turns into a URL, and what the picture costs on the way out.
/// </summary>
/// <remarks>
/// <para>
/// A QR is the one place this feature writes a secret somewhere a third party can see it — it gets photographed,
/// screenshotted and left on a screen. The cases below are about the two boundaries that keep that harmless: the
/// payload carries a code worth one match or two minutes and never a token, and it stays pure ASCII because a CJK
/// device name pushed into byte mode is a real compatibility problem on some scanners rather than a theoretical
/// one.
/// </para>
/// <para>
/// <see cref="LanPairQr.Png"/> is asserted by its header and its size rather than by pixels, because which
/// modules are black is QRCoder's business. What is ours is that the bytes come back and no file is left behind.
/// </para>
/// </remarks>
public sealed class LanPairQrTests
{
    private const string SelfId = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6";

    private static readonly DateTimeOffset Expires = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    private static LanPairTarget Target(string code = "QK7M2X", string address = "192.168.1.20", int port = 59871,
        string? name = "DESKTOP-AB1") => new(address, port, code, SelfId, name, Expires);

    [Fact]
    public void The_payload_carries_the_code_and_never_a_token()
    {
        var payload = LanPairQr.BuildPayload(Target());

        Assert.StartsWith($"http://192.168.1.20:59871{LanPairQr.PagePath}#", payload);
        Assert.Contains("k=QK7M2X", payload, StringComparison.Ordinal);
        Assert.Contains($"d={SelfId}", payload, StringComparison.Ordinal);
        Assert.Contains($"v={LanProtocol.Version}", payload, StringComparison.Ordinal);
        Assert.Contains($"e={Expires.ToUnixTimeSeconds()}", payload, StringComparison.Ordinal);
        // A token is 2 * TokenBytes hex characters. The longest run this payload can produce is the device id,
        // which is 32, so nothing in a printed code could be mistaken for one.
        Assert.True(SelfId.Length < 2 * LanPeerSecretStore.TokenBytes);
        Assert.DoesNotContain("token", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parameters_ride_in_the_fragment_so_the_page_is_the_same_whether_or_not_the_code_is_spent()
    {
        var payload = LanPairQr.BuildPayload(Target());

        // A query string would be logged by anything that answers, and GET /p has to serve the same page to a
        // person re-scanning a code that has already been used — the fragment is what keeps both true.
        Assert.DoesNotContain("?", payload, StringComparison.Ordinal);
        Assert.Equal(LanPairQr.PagePath, new Uri(payload).AbsolutePath);
    }

    [Fact]
    public void A_chinese_device_name_survives_as_ascii_and_still_arrives_whole()
    {
        var payload = LanPairQr.BuildPayload(Target(name: "小明的手机"));

        Assert.True(payload.All(character => character <= 127), $"not ASCII: {payload}");
        Assert.Contains($"n={Uri.EscapeDataString("小明的手机")}", payload, StringComparison.Ordinal);
        Assert.Equal("小明的手机", Parameter(payload, "n"));
    }

    [Fact]
    public void A_blank_name_is_omitted_rather_than_written_as_an_empty_parameter()
    {
        Assert.DoesNotContain("&n=", LanPairQr.BuildPayload(Target(name: "   ")), StringComparison.Ordinal);
        Assert.DoesNotContain("&n=", LanPairQr.BuildPayload(Target(name: null)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_longer_than_a_row_can_hold_is_shortened_before_it_is_printed()
    {
        var payload = LanPairQr.BuildPayload(Target(name: new string('甲', 200)));

        Assert.Equal(new string('甲', LanProtocol.MaximumNameLength), Parameter(payload, "n"));
    }

    [Fact]
    public void A_name_with_a_plus_is_not_the_space_a_form_encoding_would_have_made_it()
    {
        // The encoder has to escape '+' rather than leave it, because a page that reads its own parameter back
        // with form decoding would show a machine called "PC 1".
        var payload = LanPairQr.BuildPayload(Target(name: "PC+1"));

        Assert.Contains("n=PC%2B1", payload, StringComparison.Ordinal);
        Assert.Equal("PC+1", Parameter(payload, "n"));
    }

    [Fact]
    public void Anything_that_is_not_a_pairing_code_is_refused_before_it_becomes_a_picture()
    {
        Assert.Throws<ArgumentException>(() => LanPairQr.BuildPayload(Target(code: "0O1IL0")));
        Assert.Throws<ArgumentException>(() => LanPairQr.BuildPayload(Target(code: "ABC")));
        Assert.Throws<ArgumentException>(() => LanPairQr.For(Identity(), "192.168.1.20", 59871, "不是码", DateTimeOffset.UtcNow, 120));
    }

    [Fact]
    public void Only_an_ipv4_address_is_printable_because_a_camera_cannot_resolve_a_hostname()
    {
        Assert.Throws<ArgumentException>(() => LanPairQr.BuildPayload(Target(address: "localhost")));
        Assert.Throws<ArgumentException>(() => LanPairQr.BuildPayload(Target(address: "::1")));
        Assert.Throws<ArgumentException>(() => LanPairQr.BuildPayload(Target(address: "")));
    }

    [Fact]
    public void A_port_outside_the_one_the_firewall_rule_names_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LanPairQr.BuildPayload(Target(port: LanOptions.MinimumPort - 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => LanPairQr.BuildPayload(Target(port: LanOptions.MaximumPort + 1)));
    }

    [Fact]
    public void A_device_id_no_peer_of_ours_could_have_generated_is_not_printed_as_one()
    {
        Assert.Throws<ArgumentException>(() => LanPairQr.BuildPayload(
            new LanPairTarget("192.168.1.20", 59871, "QK7M2X", "..%2f..%2fetc", "本机", Expires)));
    }

    [Fact]
    public void For_snapshots_the_typed_code_and_the_countdown_from_the_same_clock_read()
    {
        var now = new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
        var target = LanPairQr.For(Identity(), "192.168.1.20", 59871, "  qk7m-2x ", now, remainingSeconds: 97);

        // Upper-cased and stripped on the way in, so the printed code is the one the state will accept.
        Assert.Equal("QK7M2X", target.Code);
        Assert.Equal(now.AddSeconds(97), target.ExpiresUtc);
    }

    [Fact]
    public void The_picture_comes_back_as_bytes_because_a_pairing_qr_leaves_nothing_on_disk()
    {
        var png = LanPairQr.Png(LanPairQr.BuildPayload(Target()));

        Assert.Equal([(byte)0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        Assert.True(png.Length > 200, $"a {png.Length}-byte QR is not a picture");
    }

    [Fact]
    public void A_bigger_module_grid_buys_a_bigger_picture_because_a_phone_at_arms_length_needs_it()
    {
        var payload = LanPairQr.BuildPayload(Target(name: null));

        Assert.True(LanPairQr.Png(payload, 3).Length < LanPairQr.Png(payload, 8).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => LanPairQr.Png(payload, 0));
    }

    /// <summary>Reads one fragment parameter back the way the page does.</summary>
    private static string? Parameter(string payload, string name)
    {
        var pairs = new Uri(payload).Fragment.TrimStart('#').Split('&');
        var pair = pairs.SingleOrDefault(entry => entry.StartsWith(name + "=", StringComparison.Ordinal));
        return pair is null ? null : Uri.UnescapeDataString(pair[(name.Length + 1)..]);
    }

    private static LanDeviceIdentity Identity() => new(SelfId, "本机", LanProtocol.KindWindows);
}
