using System.Globalization;

namespace FengSync.Core.Configuration;

/// <summary>
/// Reads and writes the human-readable byte sizes the settings windows accept, e.g. <c>500M</c>,
/// <c>2 GB</c>, <c>1.5T</c>. A bare number means bytes. Multipliers are binary, matching the way the
/// rest of the application talks about file sizes. <see cref="Format"/> always emits a value
/// <see cref="TryParse"/> reads back identically.
/// </summary>
public static class ByteSize
{
    private const long Kilo = 1024;
    private static readonly (char Unit, long Multiplier)[] Units =
    [
        ('P', Kilo * Kilo * Kilo * Kilo * Kilo),
        ('T', Kilo * Kilo * Kilo * Kilo),
        ('G', Kilo * Kilo * Kilo),
        ('M', Kilo * Kilo),
        ('K', Kilo),
        ('B', 1)
    ];

    public static bool TryParse(string? text, out long bytes)
    {
        bytes = 0;
        var value = (text ?? "").Trim();
        if (value.Length == 0) return false;
        var multiplier = 1L;
        // "500M", "500MB" and "500MiB" all mean the same binary unit.
        if (value.EndsWith("iB", StringComparison.OrdinalIgnoreCase)) value = value[..^2];
        else if (value.Length > 1 && value.EndsWith("B", StringComparison.OrdinalIgnoreCase) && char.IsLetter(value[^2])) value = value[..^1];
        if (value.Length > 0 && char.IsLetter(value[^1]))
        {
            var unit = char.ToUpperInvariant(value[^1]);
            var index = Array.FindIndex(Units, x => x.Unit == unit);
            if (index < 0) return false;
            multiplier = Units[index].Multiplier;
            value = value[..^1];
        }
        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var scaled)
            && TryScale(scaled, multiplier, out bytes);
    }

    private static bool TryScale(double value, long multiplier, out long bytes)
    {
        bytes = 0;
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) return false;
        var scaled = value * multiplier;
        if (scaled > long.MaxValue) return false;
        bytes = (long)Math.Round(scaled);
        return true;
    }

    public static string Format(long bytes)
    {
        if (bytes <= 0) return "0";
        foreach (var (unit, multiplier) in Units)
            if (multiplier > 1 && bytes % multiplier == 0)
                return (bytes / multiplier).ToString(CultureInfo.InvariantCulture) + unit;
        return bytes.ToString(CultureInfo.InvariantCulture);
    }
}
