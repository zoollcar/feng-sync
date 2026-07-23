using System.Text.RegularExpressions;

namespace FengSync.Core;

/// <summary>Names owned by Feng Sync's control plane.  This deliberately does not hide
/// generic .partial files (or arbitrary names containing "fengsync").</summary>
public static partial class SyncInternalPaths
{
    public const string StateDatabase = "sync.fengdb";
    [GeneratedRegex("^sync\\.fengdb\\.fengsync-[0-9a-f]{32}\\.(?:tmp|bak)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StateTemporaryName();
    [GeneratedRegex("^.+\\.fengsync-[a-z0-9]+\\.partial$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TransferTemporaryName();

    public static bool IsInternal(string relativePath)
    {
        var name = relativePath.Replace('\\', '/').Trim('/');
        return !name.Contains('/') && (name.Equals(StateDatabase, StringComparison.OrdinalIgnoreCase) || StateTemporaryName().IsMatch(name));
    }

    // Transfer staging is also program-owned, but only the exact GUID form is hidden.
    public static bool IsTransferTemporary(string relativePath) => TransferTemporaryName().IsMatch(relativePath.Replace('\\', '/'));
    public static bool IsExcludedFromScan(string relativePath) => IsInternal(relativePath) || IsTransferTemporary(relativePath);
    public static string StateTemporary(Guid transaction) => $"{StateDatabase}.fengsync-{transaction:N}.tmp";
}
