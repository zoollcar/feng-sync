using System.Text.Json;

namespace FengSync.Core.Mount;

/// <summary>
/// Reads the size a mounted endpoint reports about itself, for <c>vfsOpt.diskSpaceTotalSize</c>.
/// </summary>
/// <remarks>
/// Measured against the bundled v1.75.0: <c>operations/about {fs}</c> answers
/// <c>{"free":…, "total":…, "used":…}</c> for a backend that knows its quota. Not every backend does — the
/// answer can come back empty, and the request itself can fail — so the rule is that an unknown size is
/// <see langword="null"/> and the key is then <b>omitted</b>. rclone's sentinel for "unspecified" is -1, so
/// writing 0 (or any number) would report a volume of that size and Windows would show a full disk.
/// </remarks>
public static class MountCapacity
{
    /// <summary>The reported total in bytes, or <see langword="null"/> when the answer does not carry one.</summary>
    public static long? TotalBytes(JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Object) return null;
        if (!answer.TryGetProperty("total", out var total)) return null;
        if (total.ValueKind != JsonValueKind.Number) return null;
        return total.TryGetInt64(out var value) && value > 0 ? value : null;
    }

    /// <summary>What the picker says about the size it found — including that the number is fixed at mount time.</summary>
    public static string Describe(long? totalBytes) => totalBytes is { } total
        ? $"已读取端点容量 {Configuration.ByteSize.Format(total)}，将作为挂载卷的容量。容量在挂载时确定，之后配额变化不会自动更新。"
        : "该端点没有报告容量，挂载卷将使用系统默认容量（不会写入 0，否则会显示为磁盘已满）。";

    /// <summary>
    /// What the picker promises <i>before</i> anything is mounted. Kept here rather than inline in XAML so the
    /// promise and the two outcomes above stay in one file: a UI that quietly stops mentioning the limitation
    /// is how "the number is stale after a quota change" turns into a bug report.
    /// </summary>
    public static string DescribeUpcoming() =>
        "挂载时会向端点查询容量并作为挂载卷的大小；该数值在挂载那一刻固定，之后云端配额变化不会自动更新。";
}
