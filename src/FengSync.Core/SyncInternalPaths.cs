using System.Text.RegularExpressions;

namespace FengSync.Core;

/// <summary>Names owned by Feng Sync's control plane.  This deliberately does not hide
/// generic .partial files (or arbitrary names containing "fengsync").</summary>
public static partial class SyncInternalPaths
{
    public const string StateDatabase = "sync.fengdb";
    /// <summary>Suffix of the deterministic staging name, e.g. <c>report.pdf.fengsync.partial</c>.</summary>
    public const string TransferTemporarySuffix = ".fengsync.partial";
    [GeneratedRegex("^sync\\.fengdb\\.fengsync-[0-9a-f]{32}\\.(?:tmp|bak)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StateTemporaryName();
    // Two forms are recognized: the deterministic "<name>.fengsync.partial" used for resumable
    // transfers, and the "<name>.fengsync-<token>.partial" random name older builds wrote. The
    // legacy form stays recognizable so a leftover file from a previous version is still hidden
    // from scans and still eligible for the cleanup pass.
    [GeneratedRegex("^(.+)\\.fengsync(?:-[a-z0-9]+)?\\.partial$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TransferTemporaryName();

    public static bool IsInternal(string relativePath)
    {
        var name = relativePath.Replace('\\', '/').Trim('/');
        return !name.Contains('/') && (name.Equals(StateDatabase, StringComparison.OrdinalIgnoreCase) || StateTemporaryName().IsMatch(name));
    }

    /// <summary>
    /// The staging name for one user-visible path. Deliberately deterministic rather than random:
    /// an interrupted transfer must be able to find the bytes it already moved, which is what makes
    /// <see cref="Execution.TransferResume"/> able to continue instead of starting over.
    /// </summary>
    public static string TransferTemporary(string relativePath) => relativePath + TransferTemporarySuffix;

    // Transfer staging is also program-owned, but only the exact Feng Sync form is hidden.
    public static bool IsTransferTemporary(string relativePath) => TransferTemporaryName().IsMatch(relativePath.Replace('\\', '/'));
    /// <summary>Returns the user-visible path represented by a Feng Sync transfer staging file.</summary>
    public static bool TryGetTransferTemporaryOriginalPath(string relativePath, out string originalPath)
    {
        var match = TransferTemporaryName().Match(relativePath.Replace('\\', '/'));
        originalPath = match.Success ? match.Groups[1].Value : "";
        return match.Success;
    }
    public static bool IsExcludedFromScan(string relativePath) => IsInternal(relativePath) || IsTransferTemporary(relativePath);
    public static string StateTemporary(Guid transaction) => $"{StateDatabase}.fengsync-{transaction:N}.tmp";
}
