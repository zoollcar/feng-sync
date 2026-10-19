using System.Globalization;

namespace FengSync.Lan;

/// <summary>
/// A transfer rate, written the way a person reads one: <c>12.4 MB/s</c>.
/// </summary>
/// <remarks>
/// <para>
/// Not <see cref="FengSync.Core.Configuration.ByteSize"/>. That one writes exact multiples only, because it is
/// the round-trip form a settings box needs; a measured rate is never a round number, so it would print
/// <c>13002342</c> beside a progress bar. This is the display form instead: binary units, one decimal below a
/// hundred, and no decimal at all above it — the digit that changes ten times a second is noise in a sentence
/// somebody is reading out of the corner of their eye.
/// </para>
/// <para>
/// Invariant on purpose. The unit is a symbol rather than a word, so it reads the same in both languages this
/// application speaks, and a culture that writes <c>12,4</c> would put a comma where the number's own thousands
/// separator belongs.
/// </para>
/// </remarks>
public static class LanTransferSpeed
{
    private static readonly string[] Units = ["KB/s", "MB/s", "GB/s", "TB/s"];

    /// <summary>The rate as a sentence, or an empty string when there is no rate to state yet.</summary>
    public static string Format(double bytesPerSecond)
    {
        // Zero is not a slow transfer, it is a measurement that has not been taken: the board needs two samples
        // a moment apart before it can divide, and a row that says "0 B/s" for that moment reads as stuck.
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond <= 0) return "";
        if (bytesPerSecond < 1024) return Math.Round(bytesPerSecond).ToString("0", CultureInfo.InvariantCulture) + " B/s";

        var value = bytesPerSecond;
        var index = -1;
        do
        {
            value /= 1024;
            index++;
        }
        while (value >= 1024 && index < Units.Length - 1);

        return value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + Units[index];
    }
}
