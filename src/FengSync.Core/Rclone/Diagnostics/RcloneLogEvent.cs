using System.Text.Json;

namespace FengSync.Core.Rclone.Diagnostics;

public sealed record RcloneLogEvent(DateTimeOffset Timestamp, string Level, string Message,
    string? Source, string Stream, JsonElement Data);

public interface IRcloneLogSink
{
    void Write(RcloneLogEvent entry);
}

public sealed class TraceRcloneLogSink : IRcloneLogSink
{
    public void Write(RcloneLogEvent entry) => System.Diagnostics.Trace.WriteLine(
        $"[{entry.Timestamp:O}] rclone {entry.Level} ({entry.Stream}/{entry.Source}): {entry.Message}");
}

public sealed class FileRcloneLogSink(string? directory = null) : IRcloneLogSink
{
    private static readonly object WriteGate = new();
    private readonly string _directory = directory ?? Path.Combine(AppDataPaths.Root, "logs");

    public void Write(RcloneLogEvent entry)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, $"rclone-{entry.Timestamp.UtcDateTime:yyyy-MM-dd}.jsonl");
            var line = JsonSerializer.Serialize(new
            {
                time = entry.Timestamp,
                level = entry.Level,
                message = entry.Message,
                entry.Source,
                entry.Stream,
                data = entry.Data
            });
            lock (WriteGate) File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // Diagnostics must never terminate the rclone pipe drain or a sync run.
        }
    }
}

public sealed class CompositeRcloneLogSink(params IRcloneLogSink[] sinks) : IRcloneLogSink
{
    public void Write(RcloneLogEvent entry)
    {
        foreach (var sink in sinks)
            try { sink.Write(entry); } catch { /* one sink must not suppress the others */ }
    }
}

public static class RcloneFailureLog
{
    private static readonly object WriteGate = new();

    public static void Write(RcloneFailure failure, string? context = null)
    {
        try
        {
            var directory = Path.Combine(AppDataPaths.Root, "logs");
            Directory.CreateDirectory(directory);
            var now = DateTimeOffset.UtcNow;
            var line = JsonSerializer.Serialize(new
            {
                time = now,
                kind = "rcloneFailure",
                context,
                failure.Category,
                failure.Retryable,
                failure.Operation,
                failure.HttpStatus,
                failure.RcStatus,
                failure.UserMessage,
                failure.SuggestedAction,
                failure.TechnicalMessage,
                failure.SanitizedInput,
                failure.CorrelationId
            });
            var path = Path.Combine(directory, $"rclone-{now.UtcDateTime:yyyy-MM-dd}.jsonl");
            lock (WriteGate) File.AppendAllText(path, line + Environment.NewLine);
        }
        catch { /* logging is best effort */ }
    }
}

public static class RcloneLogParser
{
    public static RcloneLogEvent Parse(string line, string stream)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var time = root.TryGetProperty("time", out var t) && DateTimeOffset.TryParse(t.GetString(), out var parsed)
                ? parsed : DateTimeOffset.UtcNow;
            using var sanitized = JsonDocument.Parse(RcloneFailureClassifier.Redact(root));
            return new(time, Get(root, "level") ?? "info", RcloneFailureClassifier.RedactText(Get(root, "msg") ?? line),
                Get(root, "source"), stream, sanitized.RootElement.Clone());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            using var document = JsonDocument.Parse("{}");
            return new(DateTimeOffset.UtcNow, stream == "stderr" ? "error" : "info", RcloneFailureClassifier.RedactText(line), null, stream,
                document.RootElement.Clone());
        }
    }
    private static string? Get(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) ? property.GetString() : null;
}
