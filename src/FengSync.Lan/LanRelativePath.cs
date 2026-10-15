namespace FengSync.Lan;

/// <summary>
/// The whole containment rule for a path that arrived over the network.
/// </summary>
/// <remarks>
/// <para>
/// Judgement only, never repair: an unsafe path is refused, not stripped, not flattened, and not silently
/// relocated to the receive root. This is the same stance <c>CloudFolderDrag</c> takes on the drag-out side,
/// and for the same reason — a name the user is about to trust in a folder they are about to open is worse
/// than no name at all. A peer that sends <c>../../Windows/x</c> gets a refusal, and rewriting it to
/// <c>Windows/x</c> would mean the application decided to write into that directory on its own behalf.
/// </para>
/// <para>
/// The output is the wire form joined with <c>/</c> and nothing else; the caller composes it against the
/// receive directory, which is the only absolute path this type never sees.
/// </para>
/// </remarks>
public static class LanRelativePath
{
    private static readonly string[] ReservedStems = ["CON", "PRN", "AUX", "NUL"];

    /// <summary>Validates a wire path and returns it normalised to <c>/</c>-separated relative form.</summary>
    public static bool TryResolve(string? relativeWirePath, out string safeRelative, out LanRefusal refusal)
    {
        safeRelative = "";
        refusal = LanRefusal.PathUnsafe;
        if (string.IsNullOrWhiteSpace(relativeWirePath)) return false;

        // A drive or a colon anywhere is either an absolute path or an alternate data stream.
        // A backslash is not a separator on the wire and is not a legal Windows name character.
        if (relativeWirePath.Contains(':', StringComparison.Ordinal)
            || relativeWirePath.Contains('\\', StringComparison.Ordinal))
            return false;
        if (relativeWirePath[0] == '/')
            return false;

        // No trimming: the wire form is exactly what the peer meant, so a trailing separator is an empty
        // segment and an empty segment is a path this application will not guess the end of.
        var segments = relativeWirePath.Split('/', StringSplitOptions.None);
        var kept = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (!TrySegment(segment, out var reason))
            {
                refusal = reason;
                return false;
            }

            kept.Add(segment);
        }

        if (kept.Count == 0) return false;
        safeRelative = string.Join('/', kept);
        refusal = LanRefusal.None;
        return true;
    }

    /// <summary>Whether one path segment could be created on a Windows filesystem, unescaped.</summary>
    public static bool TrySegment(string segment, out LanRefusal refusal)
    {
        refusal = LanRefusal.PathUnsafe;
        if (segment.Length == 0) return false;
        if (segment is "." or "..") return false;
        if (segment.Length > LanProtocol.MaximumSegmentLength)
        {
            refusal = LanRefusal.PathTooLong;
            return false;
        }

        // Trailing space and trailing dot are what Windows itself drops, so a name that survives them is a
        // different name than the one the peer will read back out of the listing.
        if (segment[^1] is ' ' or '.') return false;
        foreach (var character in segment)
        {
            if (character < ' ' || character == 0x7F) return false;
            if (character is '<' or '>' or '"' or '|' or '?' or '*' or ':') return false;
        }

        // CON, PRN, AUX and NUL are reserved with or without an extension, and COM1-9 / LPT1-9 are too. A
        // name with no dot at all is its own stem; IndexOf would answer -1 and cut the name to nothing.
        var dot = segment.IndexOf('.');
        var stem = dot < 0 ? segment : segment[..dot];
        if (stem.Length == 0) stem = segment;
        if (ReservedStems.Contains(stem, StringComparer.OrdinalIgnoreCase) || IsNumberedReserved(stem))
        {
            refusal = LanRefusal.NameReserved;
            return false;
        }

        refusal = LanRefusal.None;
        return true;
    }

    /// <summary>The last segment of a resolved path — the name a received file will be listed under.</summary>
    public static string NameOf(string safeRelative)
    {
        var index = safeRelative.LastIndexOf('/');
        return index < 0 ? safeRelative : safeRelative[(index + 1)..];
    }

    /// <summary>Every segment but the last, in wire form. Empty for a bare name.</summary>
    public static string ParentOf(string safeRelative)
    {
        var index = safeRelative.LastIndexOf('/');
        return index < 0 ? "" : safeRelative[..index];
    }

    private static bool IsNumberedReserved(string stem)
    {
        if (stem.Length is not (4 or 5)) return false;
        var prefix = stem[..3].ToUpperInvariant();
        if (prefix is not ("COM" or "LPT")) return false;
        var tail = stem[3..];
        // COM0 and COM10 are not reserved devices; only COM1-COM9 and LPT1-LPT9 are.
        return tail.Length == 1 && tail[0] is >= '1' and <= '9';
    }
}
