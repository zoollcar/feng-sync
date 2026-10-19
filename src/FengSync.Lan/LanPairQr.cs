using QRCoder;

namespace FengSync.Lan;

/// <summary>
/// What a pairing QR has to carry: where the listener is, the one-time code, and who is answering.
/// </summary>
/// <param name="Address">
/// A literal IPv4 address, because a QR a phone camera has to read is not the place for a hostname nobody's
/// LAN resolver can answer.
/// </param>
public sealed record LanPairTarget(string Address, int Port, string Code, string DeviceId, string? DeviceName, DateTimeOffset ExpiresUtc);

/// <summary>
/// The printed half of pairing: the URL a phone camera turns into an open page, and the picture of it.
/// </summary>
/// <remarks>
/// <para>
/// The code goes into the QR and the token never does. That split is the whole security argument for printing
/// this: a QR is photographed, screenshotted and left on a screen, and what it hands away is a credential that
/// is worth nothing after one match or two minutes. A token is worth the receive directory forever, so it only
/// ever travels in a response body and a cookie.
/// </para>
/// <para>
/// Parameters ride in the fragment rather than the query because a fragment is never sent to a server and never
/// lands in an access log — and because <c>GET /p</c> has to answer the same page whether or not the code in
/// the URL is already spent, so a person can re-scan and type the replacement in.
/// </para>
/// <para>
/// The payload is ASCII by construction: a device name is percent-encoded on the way in, since QRCoder's
/// default byte mode has no agreed character set and a CJK name pushed straight into a payload is a real
/// compatibility problem on some scanners.
/// </para>
/// </remarks>
public static class LanPairQr
{
    /// <summary>The one page a stranger is pointed at, and the only one that reads a fragment.</summary>
    public const string PagePath = "/p";

    /// <summary>Screen pixels per QR module. Below this a phone camera at arm's length stops resolving it.</summary>
    public const int DefaultModulePixels = 5;

    /// <summary>
    /// The address alone: what a person reads aloud, and what the window falls back to when this machine cannot
    /// print its own identity. It reaches the page but does not pair by itself — <see cref="BuildPayload"/> is
    /// the URL that does.
    /// </summary>
    public static string BuildUrl(string address, int port) => $"http://{address}:{port}{PagePath}";

    /// <summary>
    /// Snapshots the live code into a printable target. The countdown comes from
    /// <see cref="LanPairingState.RemainingSeconds"/> and the moment from the same clock read, so the expiry the
    /// QR promises is the expiry the armed code will actually honour rather than a second copy of the lifetime
    /// constant. Truncating to whole seconds makes the page give up a moment early, which is the only direction
    /// that is safe to be wrong in.
    /// </summary>
    public static LanPairTarget For(LanDeviceIdentity self, string address, int port, string code, DateTimeOffset nowUtc, int remainingSeconds)
    {
        if (!LanPairCode.TryNormalise(code, out var normalised)) throw new ArgumentException("配对码不是一张二维码能印的东西。", nameof(code));
        return new LanPairTarget(address, port, normalised, self.DeviceId, self.DeviceName, nowUtc.AddSeconds(remainingSeconds));
    }

    /// <summary>
    /// The URL a scanner produces. Rejects anything it could not have read back, so a broken picture is a
    /// crash on this side rather than a phone that shows a page with nothing filled in.
    /// </summary>
    /// <remarks>
    /// This is the one URL this machine hands out: the picture carries it and the window's address box shows the
    /// very same string, because copying the line onto another computer is the act a scanner performs and a page
    /// that then asked for the six characters it had just been given would only be asking for a typo.
    /// </remarks>
    public static string BuildPayload(LanPairTarget target)
    {
        if (!LanPairCode.TryNormalise(target.Code, out var code)) throw new ArgumentException("配对码不是一张二维码能印的东西。", nameof(target));
        if (target.Port is < LanOptions.MinimumPort or > LanOptions.MaximumPort)
            throw new ArgumentOutOfRangeException(nameof(target), "端口不在可公布的范围里。");
        if (target.Address.Length == 0 || !System.Net.IPAddress.TryParse(target.Address, out var parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("二维码只能印一个 IPv4 地址。", nameof(target));
        if (!LanProtocol.IsPeerDeviceId(target.DeviceId)) throw new ArgumentException("本机设备 id 不是一台 Feng Sync。", nameof(target));

        var payload = $"http://{target.Address}:{target.Port}{PagePath}#k={code}&d={target.DeviceId}&v={LanProtocol.Version}&e={target.ExpiresUtc.ToUnixTimeSeconds()}";
        // The name is the one field a person writes in their own language, so it is the one field that cannot
        // be assumed to be ASCII. Omitting it is fine: the page reads the real name from /hello anyway.
        var name = LanProtocol.SanitizePeerName(target.DeviceName);
        if (name.Length > 0) payload += $"&n={Uri.EscapeDataString(name)}";
        if (payload.Any(character => character > 127)) throw new ArgumentException("二维码的内容必须是纯 ASCII。", nameof(target));
        return payload;
    }

    /// <summary>
    /// Renders a payload as PNG bytes. Nothing touches the disk: a pairing QR is a secret with a two-minute
    /// shelf life, and a stray <c>.png</c> in a temp folder is the one thing this feature should never leave
    /// behind.
    /// </summary>
    public static byte[] Png(string payload, int modulePixels = DefaultModulePixels)
    {
        if (modulePixels < 1) throw new ArgumentOutOfRangeException(nameof(modulePixels), "每个模块至少一个像素。");
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var code = new PngByteQRCode(data);
        return code.GetGraphic(modulePixels, drawQuietZones: true);
    }
}
