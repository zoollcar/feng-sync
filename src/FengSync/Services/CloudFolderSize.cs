using FengSync.Core;
using FengSync.Core.Rclone.Diagnostics;

namespace FengSync.Services;

/// <summary>What the browser can say about a directory's size, and how much of it the cloud actually answered.</summary>
public enum CloudSizeOutcome
{
    /// <summary>The walk finished and gave numbers.</summary>
    Counted,

    /// <summary>The walk finished and found nothing to count: an empty directory is an answer, not a failure.</summary>
    Empty,

    /// <summary>The row asked about is not a directory — rclone refuses to size a file.</summary>
    NotADirectory,

    /// <summary>The directory the row named is no longer on the endpoint.</summary>
    Gone,

    /// <summary>The backend refused or answered with nothing readable.</summary>
    Failed,
}

/// <summary>
/// One answer to 统计大小. <paramref name="Count"/> is objects, and a directory is never one of them;
/// <paramref name="Sizeless"/> is how many objects the backend would not size, which rclone counts as zero bytes
/// — so a non-zero value means <paramref name="Bytes"/> is a floor, not a total, and saying it as a total would be
/// the kind of number a user budgets storage against.
/// </summary>
public sealed record CloudSizeResult(CloudSizeOutcome Outcome, long Bytes, long Count, long Sizeless, string? Reason = null)
{
    public static CloudSizeResult Of(RcloneFolderSize size) =>
        new(size.Count == 0 ? CloudSizeOutcome.Empty : CloudSizeOutcome.Counted, size.Bytes, size.Count, size.Sizeless);

    /// <summary>An answer with no numbers in it: the directory may exist, but nothing here can be reported.</summary>
    public static readonly CloudSizeResult NoAnswer = new(CloudSizeOutcome.Failed, 0, 0, 0, "云端没有给出可读取的大小。");

    public static CloudSizeResult Refused(string? message) =>
        new(OutcomeFor(message), 0, 0, 0, message);

    /// <summary>
    /// Which refusal this is, read from rclone's own words. The status code is not enough and the shared
    /// classifier is worse: v1.75.0 answers a missing directory as 404 <c>directory not found</c> and a file as
    /// HTTP 500 <c>is a file not a directory</c>, and a 500 reads as a temporary connection problem to anything
    /// that looks at the category first. Same rule as the trash and the link reads — the sentence is the fact.
    /// </summary>
    public static CloudSizeOutcome OutcomeFor(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return CloudSizeOutcome.Failed;
        if (message.Contains("directory not found", StringComparison.OrdinalIgnoreCase)) return CloudSizeOutcome.Gone;
        if (message.Contains("not a directory", StringComparison.OrdinalIgnoreCase)) return CloudSizeOutcome.NotADirectory;
        return CloudSizeOutcome.Failed;
    }
}

/// <summary>
/// The rules behind 文件夹大小, apart from the read that produces the numbers: what a counted answer is allowed to
/// claim, which of the cloud's refusals it can be, and what the button is called and warned about. Whether a row
/// may be counted at all is <see cref="CloudItemDetails.OffersSize"/>; this file is only about the answer.
/// <para>
/// The property this whole batch rests on is that a directory's size is <em>not</em> in the listing. A row's own
/// <c>Size</c> for a folder is whatever the backend chose to put in that field — zero for most — and showing it
/// would read as "this folder is empty". Counting costs a walk over the subtree, so it happens only when the
/// user asks, and the number is not written back onto the row: the listing, the counted answer and the open
/// panel are three places the same fact could live, and only the panel is here long enough to be trusted.
/// </para>
/// </summary>
public static class CloudFolderSize
{
    /// <summary>
    /// The button that asks for the walk. It sits in 属性 rather than in the row menu because the row it changes
    /// is in that panel, and a menu item whose answer lands somewhere else is a command the user has to hunt for.
    /// </summary>
    public const string ActionLabel = "统计大小";

    /// <summary>
    /// The hint shown beside the button, before anything is read. It says both halves of what the user is about
    /// to start: the read walks the whole subtree, and it cannot be recalled — measured against the bundled
    /// v1.75.0, <c>job/stop</c> answers and the recursion keeps going. Closing the panel stops the waiting,
    /// not the work.
    /// </summary>
    public const string CostHint = "统计会遍历整个子目录树，对象越多越久；这一步发出去就收不回，关掉面板只是不再等它。";

    /// <summary>The 大小 row for one directory before anybody has counted it.</summary>
    public const string NotCounted = "未统计";

    /// <summary>
    /// The row the panel shows for one counted directory. The byte total keeps the panel's existing shape
    /// (human size with the raw number beside it) and adds the object count, because "46.3 GB in 205,034
    /// objects" is what tells the user the walk really went down a tree.
    /// </summary>
    public static string TextFor(CloudSizeResult result) => result.Outcome switch
    {
        CloudSizeOutcome.Empty => "0 个对象，空目录",
        CloudSizeOutcome.Counted => $"{CloudItemDetails.HumanSize(result.Bytes)}（{result.Bytes:N0} 字节）· {result.Count:N0} 个对象"
            + SizelessClause(result),
        CloudSizeOutcome.Gone => "云端已没有这个文件夹",
        CloudSizeOutcome.NotADirectory => "这是一个文件，不是文件夹",
        _ => "未能统计" + (result.Reason is { Length: > 0 } why ? "：" + why : ""),
    };

    /// <summary>
    /// Why a walk ended without numbers, told apart because only some of the endings are worth asking again. The
    /// refusal is read from rclone's own sentence rather than its category, for the reason <see
    /// cref="CloudSizeResult.OutcomeFor"/> gives; anything else becomes the diagnosis the user can quote.
    /// </summary>
    public static CloudSizeResult FromFailure(Exception failure) => failure switch
    {
        RcloneException { Failure: { } detail } => CloudSizeResult.Refused(detail.TechnicalMessage),
        _ => new CloudSizeResult(CloudSizeOutcome.Failed, 0, 0, 0, RcloneUiError.Describe(failure, "cloud-size")),
    };

    /// <summary>
    /// Why a byte total with unsized objects in it is not a total. Naming the count is the whole point: a
    /// Google Drive folder full of Docs answers 0 bytes for each one, and 0 字节 is a number a user would
    /// believe.
    /// </summary>
    private static string SizelessClause(CloudSizeResult result) => result.Sizeless == 0
        ? ""
        : $"（其中 {result.Sizeless:N0} 个云端不报大小，按 0 计入）";
}
