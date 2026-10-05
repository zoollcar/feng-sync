using System.IO;
using System.Text.Json;
using FengSync.Core;

namespace FengSync.Services;

/// <summary>
/// The failure information a transfer row can hand over: the full diagnostic for one row, and the rclone
/// failure log for the day.
/// </summary>
/// <remarks>
/// The row already shows <see cref="CloudTransferTask.Error"/>, which is one sentence. What a bug report needs
/// is <see cref="CloudTransferTask.ErrorDetail"/> — operation, category, HTTP/RC status and correlation id —
/// which the journal has carried since the queue was written but nothing ever showed. The log is the file
/// <c>RcloneFailureLog</c> already writes (one JSON object per line, already redacted by the classifier), so
/// "查看日志" reads what is really there rather than re-deriving it.
/// </remarks>
public static class CloudTransferDiagnostics
{
    /// <summary>Whether this row has anything beyond its one-line status to show.</summary>
    public static bool OffersDetail(CloudTransferTask task) =>
        task.HasErrorDetail || !string.IsNullOrWhiteSpace(task.Error);

    /// <summary>
    /// The text 复制详情 hands over. It always starts with what the transfer was, because a diagnostic with no
    /// subject is not something the user can attach to anything.
    /// </summary>
    public static string DetailFor(CloudTransferTask task)
    {
        var where = task.Direction == CloudTransferDirection.Copy
            ? $"{task.RemoteUri}/{task.RemotePath} → {task.TargetUri}/{task.TargetPath}"
            : task.Direction == CloudTransferDirection.Upload
                ? $"{task.LocalPath} → {task.RemoteUri}/{task.RemotePath}"
                : $"{task.RemoteUri}/{task.RemotePath} → {task.LocalPath}";
        var lines = new List<string>
        {
            $"任务：{task.Display}",
            $"方向：{task.DirectionText}",
            $"路径：{where}",
            $"状态：{task.StatusText}",
            $"尝试次数：{task.Attempt}"
        };
        if (!string.IsNullOrWhiteSpace(task.Error)) lines.Add("错误：" + task.Error);
        if (task.HasErrorDetail) lines.Add("诊断详情：" + Environment.NewLine + task.ErrorDetail);
        else lines.Add("诊断详情：这个任务没有记录更详细的信息。");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>What the status line says after a copy attempt, so "copy" never looks like it worked when it did not.</summary>
    public static string DescribeCopy(bool copied) => copied
        ? "详情已复制到剪贴板，可直接粘贴到问题反馈里。"
        : "没能写入剪贴板，请从详情窗口里自行复制。";

    /// <summary>Where rclone failures for one day are written, matching <c>RcloneFailureLog</c> exactly.</summary>
    public static string LogPath(DateTimeOffset now) =>
        System.IO.Path.Combine(AppDataPaths.Root, "logs", $"rclone-{now.UtcDateTime:yyyy-MM-dd}.jsonl");

    /// <summary>
    /// The tail of the failure log, rendered one line per failure. A missing or unreadable file is reported as
    /// such rather than as an empty log, because "there is nothing here" and "I could not look" are different
    /// answers and the second one is actionable.
    /// </summary>
    public static string ReadLog(string path, int maxLines = 200)
    {
        try
        {
            if (!File.Exists(path)) return $"没有找到今天的失败日志：{path}" + Environment.NewLine + "（只有真正发生过 rclone 失败时才会生成这个文件。）";
            var lines = File.ReadLines(path).TakeLast(Math.Max(1, maxLines)).ToList();
            if (lines.Count == 0) return $"失败日志为空：{path}";
            return $"共 {lines.Count} 条（最多显示 {maxLines} 条）：{path}" + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine, lines.Select(Render));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "无法读取失败日志：" + RcloneUiError.Describe(ex, "cloud-transfer-log");
        }
    }

    /// <summary>
    /// Renders one JSONL record as a readable line. An unparseable line is passed through verbatim: showing the
    /// raw text is more useful than hiding a line the reader cannot explain.
    /// </summary>
    private static string Render(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : "";
            var time = Text("time");
            if (DateTimeOffset.TryParse(time, out var parsed)) time = parsed.ToLocalTime().ToString("HH:mm:ss");
            var parts = new[]
            {
                time,
                Text("context"),
                Text("Category"),
                Text("Operation"),
                Text("UserMessage"),
                Text("CorrelationId") is { Length: > 0 } id ? $"诊断 ID {id}" : ""
            };
            return string.Join("  ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }
        catch (JsonException) { return line; }
    }
}
