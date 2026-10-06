using System.IO;
using System.Text.Json;
using FengSync.Core;
using FengSync.Localization;

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
            Tr.Format("Transfer.DetailTask", task.Display),
            Tr.Format("Transfer.DetailDirection", task.DirectionText),
            Tr.Format("Transfer.DetailPath", where),
            Tr.Format("Transfer.DetailStatus", task.StatusText),
            Tr.Format("Transfer.DetailAttempt", task.Attempt)
        };
        if (!string.IsNullOrWhiteSpace(task.Error)) lines.Add(Tr.Format("Transfer.DetailError", task.Error));
        if (task.HasErrorDetail) lines.Add(Tr.Format("Transfer.DetailDiagnostics", task.ErrorDetail));
        else lines.Add(Tr.F("Transfer.DetailNoDiagnostics"));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>What the status line says after a copy attempt, so "copy" never looks like it worked when it did not.</summary>
    public static string DescribeCopy(bool copied) => copied
        ? Tr.F("Transfer.CopiedDetail")
        : Tr.F("Transfer.CopyDetailFailed");

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
            if (!File.Exists(path)) return Tr.Format("Transfer.LogMissing", path);
            var lines = File.ReadLines(path).TakeLast(Math.Max(1, maxLines)).ToList();
            if (lines.Count == 0) return Tr.Format("Transfer.LogEmpty", path);
            return Tr.Format("Transfer.LogSummary", lines.Count, maxLines, path) + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine, lines.Select(Render));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Tr.Format("Transfer.LogReadFailed", RcloneUiError.Describe(ex, "cloud-transfer-log"));
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
                Text("CorrelationId") is { Length: > 0 } id ? Tr.Format("Transfer.DiagnosticId", id) : ""
            };
            return string.Join("  ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }
        catch (JsonException) { return line; }
    }
}
