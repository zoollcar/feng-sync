using System.Text;

namespace FengSync.Core;

public enum RunDisplayOutcome { Succeeded, PartialSuccess, Failed, Cancelled }

/// <summary>UI-independent classification, detail logging, and safe retry selection for a completed run.</summary>
public static class RunResultPresentation
{
    public static RunDisplayOutcome OutcomeOf(SyncRunResult result, bool cancelled = false)
    {
        if (cancelled || result.Operations.Any(x => x.Stage == TransferStage.Cancelled)) return RunDisplayOutcome.Cancelled;
        if (result.FailedOperations == 0 && result.Operations.All(x => x.Stage == TransferStage.Committed)) return RunDisplayOutcome.Succeeded;
        return result.SucceededOperations > 0 ? RunDisplayOutcome.PartialSuccess : RunDisplayOutcome.Failed;
    }

    public static string ToLog(SyncRunResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"运行 {result.RunId:N}");
        builder.AppendLine($"结果：{OutcomeOf(result)}；成功 {result.SucceededOperations}，失败 {result.FailedOperations}。");
        foreach (var item in result.Operations.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(item.Stage).Append("\t").Append(item.Kind).Append("\t").Append(item.Path);
            if (!string.IsNullOrWhiteSpace(item.Error)) builder.Append("\t").Append(item.Error);
            builder.AppendLine();
        }
        return builder.ToString();
    }

    public static SyncPlan BuildRetryPlan(SyncRunResult result, IEnumerable<SyncOperation> original)
    {
        var operations = original.ToDictionary(x => x.OperationId);
        var retry = result.Operations
            .Where(x => x.Stage == TransferStage.Failed && IsRetryable(x.Error))
            .Where(x => operations.ContainsKey(x.OperationId))
            .Select(x => Clone(operations[x.OperationId]))
            .ToList();
        return new SyncPlan(retry);
    }

    public static bool IsRetryable(string? error) => !string.IsNullOrWhiteSpace(error)
        && !error.Contains("不支持", StringComparison.OrdinalIgnoreCase)
        && !error.Contains("无效", StringComparison.OrdinalIgnoreCase)
        && !error.Contains("冲突", StringComparison.OrdinalIgnoreCase);

    private static SyncOperation Clone(SyncOperation operation)
        => new(operation.Path, operation.Kind, "失败项重试：" + operation.Reason, selected: true, keepLeft: operation.KeepLeft, keepRight: operation.KeepRight);
}
