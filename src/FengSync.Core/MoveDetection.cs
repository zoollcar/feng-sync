namespace FengSync.Core;

public sealed record EndpointMoveFact(EndpointSide Side, string OldPath, string NewPath, EntryKind Kind,
    IdentityEvidenceKind Evidence, MoveConfidence Confidence, EntrySnapshot BaselineEntry, EntrySnapshot CurrentEntry);

/// <summary>Endpoint-local move detection. It only matches removed baseline paths to newly added paths on one endpoint.</summary>
public sealed class EndpointMoveDetector
{
    public IReadOnlyList<EndpointMoveFact> Detect(EndpointSide side, IEnumerable<BaselineEntry> baseline,
        IEnumerable<EntrySnapshot> current, MoveDetectionSettings? settings = null, EndpointPathSemantics? paths = null)
    {
        settings ??= new();
        if (!settings.Enabled) return [];
        var old = baseline.Select(x => side == EndpointSide.Left ? x.Left : x.Right).Where(x => x is not null).Cast<EntrySnapshot>().ToList();
        var now = current.ToList();
        // Path spelling is part of the scan evidence: on Windows Foo.txt -> foo.txt
        // is a real rename even though the endpoint lookup comparer is insensitive.
        var currentPaths = now.Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
        var oldPaths = old.Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
        var removed = old.Where(x => !currentPaths.Contains(x.Path)).ToList();
        var added = now.Where(x => !oldPaths.Contains(x.Path)).ToList();
        // Do not use a Windows-style comparer here.  A move detector is used for
        // S3/Drive too, where A.txt and a.txt are distinct objects.
        var canonical = (paths ?? new EndpointPathSemantics(true, System.Text.NormalizationForm.FormC)).Canonicalize;
        var result = new List<EndpointMoveFact>(); var consumed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in removed)
        {
            if (source.Kind == EntryKind.Directory || (!settings.DetectFiles && source.Kind == EntryKind.File)) continue;
            var candidates = added.Where(x => !consumed.Contains(canonical(x.Path)) && x.Kind == source.Kind).ToList();
            var matched = Match(source, candidates, settings);
            if (matched is null) continue;
            consumed.Add(canonical(matched.Value.Entry.Path));
            result.Add(new(side, source.Path, matched.Value.Entry.Path, source.Kind, matched.Value.Evidence, matched.Value.Confidence, source, matched.Value.Entry));
        }
        return result;
    }

    private static (EntrySnapshot Entry, IdentityEvidenceKind Evidence, MoveConfidence Confidence)? Match(EntrySnapshot old, List<EntrySnapshot> candidates, MoveDetectionSettings settings)
    {
        if (candidates.Count == 0) return null;
        var stable = Unique(candidates.Where(x => old.Identity?.StableObjectId is { } id && x.Identity?.StableObjectId == id).ToList());
        if (stable is not null) return (stable, IdentityEvidenceKind.StableObjectId, MoveConfidence.Certain);
        var digest = Unique(candidates.Where(x => old.Identity?.StrongDigest is { } d && x.Identity?.StrongDigest == d && SameSize(old, x)).ToList());
        if (digest is not null) return (digest, IdentityEvidenceKind.StrongDigest, MoveConfidence.High);
        var token = Unique(candidates.Where(x => old.Identity?.ProviderToken is { } t && x.Identity?.ProviderToken == t && SameSize(old, x)).ToList());
        if (token is not null) return (token, IdentityEvidenceKind.ProviderToken, MoveConfidence.High);
        if (!settings.AllowWeakFingerprint || old.Kind != EntryKind.File || old.Fingerprint is null) return null;
        var weak = candidates.Where(x => x.Fingerprint is not null && SameSize(old, x) &&
            Math.Abs((x.Fingerprint!.ModifiedUtc - old.Fingerprint.ModifiedUtc).TotalSeconds) <= 5).ToList();
        // The guard is intentionally only for weak evidence. Stable IDs, content
        // digests and provider tokens remain safe and deterministic in big trees.
        if (weak.Count > settings.MaxAmbiguousBucketSize) return null;
        var one = Unique(weak);
        return one is null ? null : (one, IdentityEvidenceKind.WeakFingerprint, MoveConfidence.Medium);
    }
    private static EntrySnapshot? Unique(List<EntrySnapshot> values) => values.Count == 1 ? values[0] : null;
    private static bool SameSize(EntrySnapshot a, EntrySnapshot b) => a.Fingerprint is not null && b.Fingerprint is not null && a.Fingerprint.Size == b.Fingerprint.Size;
}
