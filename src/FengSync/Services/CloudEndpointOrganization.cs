namespace FengSync.Services;

/// <summary>
/// Grouping and tagging for cloud endpoints. rclone has nowhere to keep either, so both live in this
/// application's own metadata file and are pure functions here rather than logic buried in the window.
/// </summary>
public static class CloudEndpointOrganization
{
    /// <summary>The heading ungrouped endpoints are filed under, so every row has one.</summary>
    public const string UngroupedLabel = "未分组";

    /// <summary>Trimmed group name, or null when the endpoint is ungrouped.</summary>
    public static string? NormalizeGroup(string? group) => string.IsNullOrWhiteSpace(group) ? null : group.Trim();

    /// <summary>
    /// Trims, drops blanks and removes case-insensitive duplicates. Returns null rather than an empty list
    /// when nothing is left, so "has no tags" has exactly one representation on disk.
    /// </summary>
    public static IReadOnlyList<string>? NormalizeTags(IEnumerable<string>? tags)
    {
        List<string> kept = [];
        foreach (var tag in tags ?? [])
        {
            var value = tag.Trim();
            if (value.Length == 0) continue;
            if (kept.Contains(value, StringComparer.OrdinalIgnoreCase)) continue;
            kept.Add(value);
        }
        return kept.Count == 0 ? null : kept;
    }

    /// <summary>
    /// Reads what the user typed into the tag box. Every separator the box can be given is accepted: a Chinese
    /// input method produces the full-width ones without the user noticing, and <c>、</c> is included because
    /// that is what <see cref="FormatTags"/> writes, so a value shown in the box has to survive being saved
    /// again unchanged.
    /// </summary>
    public static IReadOnlyList<string>? ParseTags(string? text) =>
        NormalizeTags((text ?? "").Split([',', '，', ';', '；', '、', '\r', '\n'], StringSplitOptions.TrimEntries));

    /// <summary>What the tag box shows for a stored list.</summary>
    public static string FormatTags(IReadOnlyList<string>? tags) => tags is null ? "" : string.Join("、", tags);

    /// <summary>The heading a row is filed under. Every row has one, including the ungrouped ones.</summary>
    public static string GroupLabel(string? group) => NormalizeGroup(group) ?? UngroupedLabel;

    /// <summary>
    /// The record to persist after the user edits one endpoint's organisation. The stored record is preferred
    /// over the one the list is holding, and a record is created when neither exists — an endpoint added to
    /// <c>rclone.conf</c> by hand has no metadata — so that saving a group can never silently do nothing.
    /// </summary>
    public static CloudEndpointMetadata Merge(CloudEndpointMetadata? existing, CloudEndpointAccount account,
        string? group, IReadOnlyList<string>? tags) =>
        (existing ?? account.Metadata ?? new CloudEndpointMetadata(account.Name, account.Remote.Type, account.Provider, "", ""))
            with { Group = NormalizeGroup(group), Tags = NormalizeTags(tags) };

    /// <summary>
    /// The record to persist after a create-or-edit path rebuilds one from scratch. Those paths own the
    /// provider, bucket and prefix, but know nothing about the group, the tags or the hidden backing remote,
    /// and rebuilding the record without carrying them over erases the user's filing every time they change
    /// an endpoint's root directory.
    /// </summary>
    public static CloudEndpointMetadata Preserve(CloudEndpointMetadata? existing, CloudEndpointMetadata rebuilt) =>
        rebuilt with { AliasOf = rebuilt.AliasOf ?? existing?.AliasOf, Group = existing?.Group, Tags = existing?.Tags };

    /// <summary>The status line after an organisation change, kept separate so the wording is testable.</summary>
    public static string Describe(string remoteName, string? group, IReadOnlyList<string>? tags)
    {
        if (group is null && tags is null) return $"已清除端点“{remoteName}”的分组与标签。";
        if (group is null) return $"已为端点“{remoteName}”设置 {tags!.Count} 个标签。";
        if (tags is null) return $"已将端点“{remoteName}”归入分组“{group}”。";
        return $"已更新端点“{remoteName}”的分组与标签。";
    }
}
