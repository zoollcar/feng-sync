namespace FengSync.Core.Updates;

/// <summary>Three-part semantic release version. Prerelease versions are deliberately not comparable as releases.</summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch, string? Prerelease = null) : IComparable<ReleaseVersion>
{
    public bool IsPrerelease => !string.IsNullOrWhiteSpace(Prerelease);
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim(); if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        var build = value.IndexOf('+'); if (build >= 0) value = value[..build];
        var dash = value.IndexOf('-'); var prerelease = dash >= 0 ? value[(dash + 1)..] : null;
        if (dash >= 0) value = value[..dash];
        var parts = value.Split('.');
        if (parts.Length != 3 || parts.Any(p => !int.TryParse(p, out var n) || n < 0) || (dash >= 0 && string.IsNullOrWhiteSpace(prerelease))) return false;
        version = new ReleaseVersion(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), prerelease); return true;
    }
    public static ReleaseVersion Parse(string text) => TryParse(text, out var v) ? v : throw new FormatException("版本必须是三段数字语义版本。");
    public int CompareTo(ReleaseVersion other)
    {
        var result = Major.CompareTo(other.Major); if (result != 0) return result;
        result = Minor.CompareTo(other.Minor); return result != 0 ? result : Patch.CompareTo(other.Patch);
    }
    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (IsPrerelease ? $"-{Prerelease}" : "");
}
