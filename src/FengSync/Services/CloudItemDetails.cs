using FengSync.Core;

namespace FengSync.Services;

/// <summary>One line of the details panel: what the user reads as the label, and the value beside it.</summary>
public sealed record CloudDetailRow(string Label, string Value);

/// <summary>
/// The one expensive read a panel may still offer, with the words that go around it. The label and the warning
/// travel with the action because they are the same decision: a checksum walks one object and a size walks a
/// whole subtree, so the panel says which one it is about to do and what it costs.
/// </summary>
public sealed record CloudDetailsAction(string Label, string Hint, Func<Task<CloudDetailsPanel>> Run);

/// <summary>
/// A panel's rows plus the one thing about them the dialog cannot see: whether a read is still worth offering,
/// and which one. Saying so here rather than letting the view look for a row that reads 未计算 keeps the wording
/// and the button owned by the same file. A panel carries <em>at most one</em> action, because the two candidates
/// are mutually exclusive by what they can be asked about — only a file still has a checksum to read, only a
/// directory still has a subtree to walk.
/// </summary>
public sealed record CloudDetailsPanel(IReadOnlyList<CloudDetailRow> Rows, CloudDetailsAction? Action = null);

/// <summary>
/// What <c>operations/stat</c> answered about one object, already reduced to what the browser shows.
/// <paramref name="Exists"/> false is the answer for an object that is gone: v1.75.0 reports a missing object
/// as an empty reply rather than as an error, so a stale row in the listing is a value here and not an
/// exception. <paramref name="Hashes"/> is keyed by rclone's own hash names.
/// </summary>
public sealed record CloudItemStat(bool Exists, bool IsDirectory, long Size, DateTimeOffset? ModifiedUtc,
    string? MimeType, IReadOnlyDictionary<string, string> Hashes)
{
    /// <summary>The answer for an object the backend no longer has, and the only value with no hashes at all.</summary>
    public static readonly CloudItemStat Missing = new(false, false, 0, null, null,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public static CloudItemStat Found(RcloneStatEntry entry) =>
        new(true, entry.IsDirectory, entry.Size, entry.ModifiedUtc, entry.MimeType, entry.Hashes);
}

/// <summary>
/// The rules behind 属性 / 详情, kept apart from the dialog that draws them so the wording is checkable
/// without a window: which facts a listing already carries, which ones only a stat does, and what is said
/// out loud when a backend cannot answer. The two things a details panel must not do are claim a size it
/// never counted and show a checksum it never read.
/// </summary>
public static class CloudItemDetails
{
    /// <summary>
    /// The checksums this application trusts, in the order it prefers them — the same chain the sync engine
    /// reads a fingerprint with, so the browser and the engine never disagree about what a file's hash is.
    /// Asking for one is not free on a backend that stores none: rclone has to read the object to answer.
    /// </summary>
    public static readonly IReadOnlyList<string> HashPreference = ["md5", "sha1", "sha256"];

    private static readonly Dictionary<string, string> HashLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["md5"] = "MD5",
        ["sha1"] = "SHA-1",
        ["sha256"] = "SHA-256",
    };

    /// <summary>Names the hash type rclone reports under <paramref name="type"/>, or the type itself.</summary>
    public static string HashLabel(string type) => HashLabels.TryGetValue(type, out var label) ? label : type.ToUpperInvariant();

    /// <summary>
    /// The rows for one browsed entry. <paramref name="stat"/> is what the backend answered just now; when it
    /// is null the caller read nothing, so only the listing's own facts are shown.
    /// <paramref name="hashRequested"/> separates "the user asked and the backend has none" from "nobody asked
    /// yet" — the first is a fact about the endpoint, the second is about this dialog.
    /// <paramref name="size"/> is a directory's counted answer, and it is null until somebody asks for a walk:
    /// the listing's own size field for a folder is whatever the backend chose to put there, so an uncounted
    /// directory says 未统计 rather than quoting a number it never counted.
    /// </summary>
    public static IReadOnlyList<CloudDetailRow> For(CloudFileEntry entry, CloudEndpointAccount? account,
        CloudItemStat? stat, bool hashRequested = false, CloudSizeResult? size = null)
    {
        var rows = new List<CloudDetailRow>
        {
            new("名称", entry.Name),
            new("类型", entry.IsDirectory ? "文件夹" : "文件"),
        };
        if (stat is { Exists: false })
        {
            // The row on screen is a listing the backend has since stopped answering; saying so is the whole
            // point, because the alternative is a panel of values for a file that is not there.
            rows.Add(new CloudDetailRow("状态", "云端已没有这个" + (entry.IsDirectory ? "文件夹" : "文件") + "，请刷新目录。"));
            AddLocation(rows, entry, account);
            return rows;
        }

        var read = stat;
        var reported = read?.Size ?? entry.Size;
        var modified = read?.ModifiedUtc ?? entry.ModifiedUtc;
        rows.Add(new CloudDetailRow("大小", entry.IsDirectory
            ? size is null ? CloudFolderSize.NotCounted : CloudFolderSize.TextFor(size)
            : HumanSize(reported) + "（" + reported.ToString("N0") + " 字节）"));
        rows.Add(new CloudDetailRow("修改时间", ModifiedText(modified)));
        // rclone answers a MimeType for every object, including inode/directory, so this row is about the
        // bytes rather than the extension the listing shows.
        if (read?.MimeType is { Length: > 0 } mime) rows.Add(new CloudDetailRow("MIME 类型", mime));

        if (read?.Hashes.Count > 0)
        {
            foreach (var type in HashPreference)
                if (read.Hashes.TryGetValue(type, out var hash)) rows.Add(new CloudDetailRow("校验值（" + HashLabel(type) + "）", hash));
        }
        else if (entry.IsDirectory) rows.Add(new CloudDetailRow("校验值", "文件夹没有校验值"));
        else if (hashRequested) rows.Add(new CloudDetailRow("校验值", "此后端未提供校验值"));
        else rows.Add(new CloudDetailRow("校验值", "未计算"));

        AddLocation(rows, entry, account);
        return rows;
    }

    /// <summary>The button that asks for a checksum, and the warning that goes with it before anything is read.</summary>
    public const string HashActionLabel = "计算校验值";

    public const string HashActionHint = "校验值需要读取整个文件；云端对象会因此走完一遍下载，大文件请耐心等待。";

    /// <summary>
    /// Whether the panel may still offer to read a checksum: only an object that is there, is a file, reported
    /// nothing, and has not already been asked in this panel. The first three are because the read costs a pass
    /// over the object and a folder or a gone object has nothing to pay for; the last is because an endpoint that
    /// was just asked and answered with none has now been asked — offering again spends the same pass for the same
    /// 此后端未提供校验值.
    /// </summary>
    public static bool OffersHash(CloudFileEntry entry, CloudItemStat? stat, bool alreadyAsked = false) =>
        !alreadyAsked && !entry.IsDirectory && stat is { Exists: true, Hashes: { Count: 0 } };

    /// <summary>
    /// Whether the panel may still offer to walk a directory's subtree: a row that is a directory, is still
    /// there, and has not been counted in this panel yet. The first two are because counting a file is refused
    /// outright and a gone directory answers 404, so the offer would be an error with extra steps; the third is
    /// because a counted number belongs to the panel it was read for — keeping it would make the row on screen
    /// and the listing behind it two more places claiming to know a size they never re-read.
    /// </summary>
    public static bool OffersSize(CloudFileEntry entry, CloudItemStat? stat, CloudSizeResult? counted) =>
        counted is null && entry.IsDirectory && stat is { Exists: true };

    /// <summary>
    /// The one read a panel may still start, or null for a panel that has nothing left to ask. A file with no
    /// checksum earns the hash pass and a directory with no count earns the subtree walk, and the two conditions
    /// cannot both hold — a row is one or the other — which is why a panel carries one action rather than two
    /// buttons. The words come from the rule that owns each read, so a button and its warning can never disagree.
    /// </summary>
    public static CloudDetailsAction? ActionFor(CloudFileEntry entry, CloudItemStat? stat, bool hashAsked,
        CloudSizeResult? counted, Func<Task<CloudDetailsPanel>> computeHash, Func<Task<CloudDetailsPanel>> computeSize)
    {
        if (OffersHash(entry, stat, hashAsked)) return new CloudDetailsAction(HashActionLabel, HashActionHint, computeHash);
        if (OffersSize(entry, stat, counted)) return new CloudDetailsAction(CloudFolderSize.ActionLabel, CloudFolderSize.CostHint, computeSize);
        return null;
    }

    /// <summary>
    /// The rows for a multi-selection, which has no single object to stat. Only files contribute bytes: a
    /// folder's size would have to be counted by reading its subtree, and saying a folder is 0 bytes is the
    /// kind of number a user reads as "this is empty".
    /// </summary>
    public static IReadOnlyList<CloudDetailRow> ForSelection(IReadOnlyList<CloudFileEntry> selection, CloudEndpointAccount? account)
    {
        var files = selection.Where(entry => !entry.IsDirectory).ToList();
        var folders = selection.Count - files.Count;
        var bytes = files.Sum(entry => entry.Size);
        var rows = new List<CloudDetailRow>
        {
            new("选择", selection.Count + " 项"),
            new("文件", files.Count + " 个"),
            new("大小", files.Count == 0
                ? "没有文件"
                : HumanSize(bytes) + "（" + bytes.ToString("N0") + " 字节）"),
        };
        if (folders > 0)
            rows.Add(new CloudDetailRow("文件夹", folders + " 个（大小未统计）"));
        DateTimeOffset? newest = null;
        foreach (var entry in selection)
            if (entry.ModifiedUtc is { } modified && (newest is null || modified > newest)) newest = modified;
        rows.Add(new CloudDetailRow("最近修改", ModifiedText(newest)));
        if (account is not null) rows.Add(new CloudDetailRow("所在端点", EndpointText(account)));
        return rows;
    }

    /// <summary>
    /// A size in the units the browser's 大小 column uses. The column stops at MB, so a 500 KB file used to
    /// read "0.0 MB" there; both the column and this panel go through here and say 500.0 KB.
    /// </summary>
    public static string HumanSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        string[] units = ["KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var index = -1;
        do
        {
            value /= 1024;
            index++;
        }
        while (value >= 1024 && index < units.Length - 1);
        return $"{value:N1} {units[index]}";
    }

    private static void AddLocation(List<CloudDetailRow> rows, CloudFileEntry entry, CloudEndpointAccount? account)
    {
        // The address rclone itself would take, because that is the path a repair or a CLI check needs; the
        // endpoint line below is the one a user recognises.
        rows.Add(new CloudDetailRow("路径", (account?.Name ?? "") + ":" + entry.Path));
        if (account is not null) rows.Add(new CloudDetailRow("所在端点", EndpointText(account)));
        if (account is { Prefix.Length: > 0 } withPrefix)
            rows.Add(new CloudDetailRow("端点根目录", "/" + withPrefix.Prefix
                + (withPrefix.PrefixIsEnforced ? "（由 rclone 强制，无法越出）" : "")));
    }

    private static string EndpointText(CloudEndpointAccount account) =>
        string.Join(" · ", new[] { account.Name, account.Provider, account.Type }.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string ModifiedText(DateTimeOffset? modified) => modified is { } value
        ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : "未知";
}
