namespace FengSync.Core.Configuration;

/// <summary>
/// The one place that turns a pair of user-chosen limits into the string <c>core/bwlimit</c> accepts.
/// </summary>
/// <remarks>
/// Measured against the bundled rclone v1.75.0, because every rule here is a place a plausible-looking
/// value is silently read as something else:
/// <list type="bullet">
/// <item><c>core/bwlimit</c> is <b>global</b>. It has no <c>fs</c> parameter, per-remote specs such as
/// <c>nas:1M</c> are refused outright (<c>bad bwlimit: bad suffix 's'</c>), and the full <c>--bwlimit</c>
/// time-table form is refused too (<c>need exactly 1 bandwidth setting</c>). A per-endpoint limit is
/// therefore not expressible, and sending an extra <c>fs</c> field alongside <c>rate</c> sets the global
/// limit while looking like it did something else — the same shape as the ignored <c>remote</c> field on
/// <c>operations/size</c>.</item>
/// <item>The two values are <b>upload first, download second</b>: <c>1M:100k</c> answers
/// <c>bytesPerSecondTx = 1048576</c> and <c>bytesPerSecondRx = 102400</c>.</item>
/// <item>An unlimited direction has to be spelled <c>off</c>. <c>:100k</c> and <c>100k:</c> are both
/// rejected with <c>bad bwlimit: empty string</c>.</item>
/// <item>A <b>bare number is not bytes</b>: <c>512</c> is read as 512 KiB and <c>1048576</c> as 1 GiB. Only
/// an explicit <c>B</c> suffix means bytes (<c>1048576B</c> → 1048576). This formatter therefore always
/// writes the suffix, and a test pins it.</item>
/// </list>
/// </remarks>
public static class CloudTransferLimit
{
    /// <summary>The value meaning "no limit" in the settings, and the string rclone wants for it.</summary>
    public const long Unlimited = 0;
    public const string Off = "off";

    /// <summary>Whether either direction is limited, i.e. whether a rate has to be pushed to rclone at all.</summary>
    public static bool IsLimited(long uploadBytesPerSecond, long downloadBytesPerSecond)
        => uploadBytesPerSecond > 0 || downloadBytesPerSecond > 0;

    /// <summary>
    /// The <c>rate</c> string for <c>core/bwlimit</c>: <c>off</c> when nothing is limited, a single value
    /// when both directions match, and <c>upload:download</c> otherwise. Never a bare number.
    /// </summary>
    public static string Rate(long uploadBytesPerSecond, long downloadBytesPerSecond)
    {
        var upload = Part(uploadBytesPerSecond);
        var download = Part(downloadBytesPerSecond);
        if (upload == download) return upload;
        return upload + ":" + download;
    }

    private static string Part(long bytesPerSecond) =>
        bytesPerSecond > 0 ? bytesPerSecond.ToString() + "B" : Off;

    /// <summary>Human sentence for the settings page and the transfer window, in the direction names the UI uses.</summary>
    public static string Describe(long uploadBytesPerSecond, long downloadBytesPerSecond)
    {
        if (!IsLimited(uploadBytesPerSecond, downloadBytesPerSecond)) return "不限速";
        return $"上传 {Describe(uploadBytesPerSecond)} · 下载 {Describe(downloadBytesPerSecond)}";
    }

    private static string Describe(long bytesPerSecond) => bytesPerSecond > 0
        ? ByteSize.Format(bytesPerSecond) + "/s"
        : "不限";
}

/// <summary>
/// A daily window during which cloud transfers are allowed to run. Outside it the queue parks its pending
/// transfers rather than starting them.
/// </summary>
/// <remarks>
/// This is a Feng Sync rule, not an rclone one: <c>core/bwlimit</c> refuses the <c>--bwlimit</c> time-table
/// form (<c>need exactly 1 bandwidth setting</c>), and its table only ever chooses a <i>rate</i> — it has no
/// way to say "do not transfer at all". So "only transfer between these hours" has to be the queue holding
/// work back, which is also what makes it observable: a parked transfer is visible in the list instead of
/// looking like a transfer that silently crawls.
/// </remarks>
public sealed record CloudTransferTimeWindow(TimeOnly Start, TimeOnly End)
{
    /// <summary>
    /// Whether <paramref name="now"/> is inside the window. A window whose ends are equal covers the whole
    /// day rather than nothing, and one that wraps past midnight (22:00–06:00) is a single continuous
    /// window rather than an empty one — the second reading is the only one a user means by those numbers.
    /// </summary>
    public bool Contains(TimeOnly now)
    {
        if (Start == End) return true;
        return Start < End ? now >= Start && now < End : now >= Start || now < End;
    }

    /// <summary>How long until the window next opens. <see cref="TimeSpan.Zero"/> when it is already open.</summary>
    public TimeSpan DelayUntilOpen(TimeOnly now)
    {
        if (Contains(now)) return TimeSpan.Zero;
        var untilStart = Start.ToTimeSpan() - now.ToTimeSpan();
        return untilStart > TimeSpan.Zero ? untilStart : untilStart + TimeSpan.FromDays(1);
    }

    /// <summary>The window in the form the user typed it, e.g. <c>22:00–06:00</c>.</summary>
    public string Describe() => $"{Start:HH\\:mm}–{End:HH\\:mm}";

    /// <summary>
    /// Reads a <c>HH:mm</c> pair, or <see langword="null"/> when either half is unreadable.
    /// </summary>
    /// <remarks>
    /// Equal ends are deliberately <b>not</b> refused here even though <see cref="Contains"/> reads them as the
    /// whole day: "unreadable" and "self-cancelling" are different problems with different fixes, and the
    /// settings page can only say which one it is if this method keeps them apart. The self-cancelling case is
    /// refused by <c>ConfigurationValidator</c>, which can name it.
    /// </remarks>
    public static CloudTransferTimeWindow? Parse(string? start, string? end)
    {
        if (!TimeOnly.TryParseExact(start?.Trim(), ["HH:mm", "H:mm"], out var from)) return null;
        if (!TimeOnly.TryParseExact(end?.Trim(), ["HH:mm", "H:mm"], out var to)) return null;
        return new(from, to);
    }
}

/// <summary>
/// What the application does once a batch of cloud transfers has drained and everything succeeded. Appended
/// to, never renumbered: the value is persisted in the settings file as a number.
/// </summary>
public enum CloudAfterTransferAction
{
    /// <summary>Do nothing. The default, because the other three take the machine away from the user.</summary>
    None,
    /// <summary>Close Feng Sync.</summary>
    Exit,
    /// <summary>Suspend the machine.</summary>
    Sleep,
    /// <summary>Shut the machine down.</summary>
    Shutdown
}
