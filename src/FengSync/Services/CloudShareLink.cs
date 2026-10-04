using FengSync.Core.Rclone.Diagnostics;

namespace FengSync.Services;

/// <summary>How asking the cloud for a share link ended. Each one has its own sentence because they are different facts.</summary>
public enum CloudShareLinkOutcome
{
    Copied,
    /// <summary>The cloud answered with a link the application could not put anywhere — a different problem from having no link.</summary>
    CopiedNotStored,
    Declined,
    Unsupported,
    Missing,
    NotAFile,
    Failed,
}

/// <summary>
/// The answer to one share request: what happened, and the link itself when it happened. <see cref="Detail"/>
/// is the diagnosis the user may need to quote, never a substitute for <see cref="Outcome"/> — an outcome the
/// panel cannot name is an outcome that will be worded as a network problem it is not.
/// </summary>
public sealed record CloudShareLinkResult(CloudShareLinkOutcome Outcome, string? Url = null, string? Detail = null);

/// <summary>
/// The rules behind 复制分享链接, kept apart from the window that asks so that what a link costs is checkable
/// without a cloud: a link is a <em>grant</em>, rclone's own default is the least-restricted one it offers
/// (readable without an account, no expiry the app sets), and what it does afterwards — how long it lives,
/// whether it can be taken back — is the backend's answer and not something this application may promise.
/// The measured shapes this rests on: a supporting backend replies <c>{"url": …}</c>, a backend without the
/// feature replies 500 with “doesn't support public links”, and a directory replies 404 “object not found”.
/// </summary>
public static class CloudShareLinks
{
    /// <summary>
    /// Only a file earns the command. A folder is not a linkable object — v1.75.0 answers 404 for an S3 prefix —
    /// and a link that did cover one would grant a whole subtree the user never pointed at.
    /// </summary>
    public static bool OffersLink(CloudFileEntry entry) => !entry.IsDirectory;

    public const string ConfirmationTitle = "生成分享链接";

    /// <summary>The question asked before anything is created, naming exactly what is being handed over.</summary>
    public static string ConfirmationFor(CloudFileEntry entry) =>
        $"“{entry.Name}”会获得一个公开链接：任何拿到这个链接的人都能读取该文件，无需登录你的账号。\n\n"
        + "链接的有效期和它能否撤回都由云端决定（内置 rclone 实测：S3 给的是约一周后自动失效的签名地址），"
        + "本应用只负责把结果复制到剪贴板。\n\n确定生成？";

    public static string Explain(CloudShareLinkResult result) => result.Outcome switch
    {
        CloudShareLinkOutcome.Copied => "分享链接已复制到剪贴板：拿到链接的人无需登录即可读取该文件，有效期由云端决定。",
        CloudShareLinkOutcome.CopiedNotStored => "云端已经生成链接，但没能放进剪贴板，请从下方内容里自己复制。"
            + (result.Detail is { Length: > 0 } why ? " " + why : ""),
        CloudShareLinkOutcome.Declined => "已取消，云端没有生成链接。",
        CloudShareLinkOutcome.Unsupported => "此端点不支持分享链接，云端没有生成任何东西。",
        CloudShareLinkOutcome.Missing => "云端已没有这个文件，请刷新目录。",
        CloudShareLinkOutcome.NotAFile => "文件夹没有可分享的链接，只能分享单个文件。",
        CloudShareLinkOutcome.Failed => "没有取到分享链接。" + (result.Detail is { Length: > 0 } why ? " " + why : ""),
        _ => "没有取到分享链接。",
    };

    /// <summary>An answer that carries no link is a failure, not an empty success the status line would call a copy.</summary>
    public static CloudShareLinkResult FromAnswer(string? url) =>
        string.IsNullOrWhiteSpace(url)
            ? new CloudShareLinkResult(CloudShareLinkOutcome.Failed, Detail: "云端没有返回链接内容。")
            : new CloudShareLinkResult(CloudShareLinkOutcome.Copied, url);

    /// <summary>The three ways a share request fails, told apart because only one of them is worth retrying.</summary>
    public static CloudShareLinkResult FromFailure(Exception failure)
    {
        if (failure is RcloneException { Failure: { } detail })
        {
            // Checked before the category on purpose: a backend with no links answers HTTP 500, which the shared
            // classifier reads as a temporarily broken connection — a retryable-sounding sentence for something
            // that will never succeed on this endpoint.
            if (IsUnsupported(detail.TechnicalMessage)) return new CloudShareLinkResult(CloudShareLinkOutcome.Unsupported);
            if (detail.Category == RcloneFailureCategory.NotFound) return new CloudShareLinkResult(CloudShareLinkOutcome.Missing);
        }
        return new CloudShareLinkResult(CloudShareLinkOutcome.Failed, Detail: RcloneUiError.Describe(failure, "cloud-share-link"));
    }

    /// <summary>rclone's own wording for a backend that has no links, matching both “doesn't” and “does not”.</summary>
    public static bool IsUnsupported(string message) => message.Contains("support public link", StringComparison.OrdinalIgnoreCase);
}
