using System.Diagnostics;
using FengSync.Core.Rclone.Diagnostics;
using FengSync.Core.SftpServer;

namespace FengSync.Services;

/// <summary>Converts structured rclone failures into actionable UI text and sanitized diagnostics.</summary>
public static class RcloneUiError
{
    public static string Describe(Exception exception, string? context = null)
    {
        switch (exception)
        {
            case RcloneException rclone:
                RcloneFailureLog.Write(rclone.Failure, context);
                Trace.TraceError(
                    "rclone failure context={0}; operation={1}; category={2}; retryable={3}; http={4}; rc={5}; correlationId={6}; detail={7}; input={8}",
                    context ?? "unspecified", rclone.Failure.Operation, rclone.Failure.Category,
                    rclone.Failure.Retryable, rclone.Failure.HttpStatus, rclone.Failure.RcStatus,
                    rclone.Failure.CorrelationId, rclone.Failure.TechnicalMessage,
                    rclone.Failure.SanitizedInput);
                return $"{rclone.Failure.UserMessage} {rclone.Failure.SuggestedAction}（诊断 ID：{rclone.Failure.CorrelationId}）";
            case SftpServerOperationException sftp:
                Trace.TraceError(
                    "SFTP failure context={0}; operation={1}; code={2}; correlationId={3}",
                    context ?? "unspecified", sftp.Operation, sftp.TechnicalCode, sftp.CorrelationId);
                return $"{sftp.Message} {sftp.SuggestedAction}（诊断 ID：{sftp.CorrelationId}）";
            default:
                Trace.TraceError("Operation failed context={0}; type={1}; detail={2}",
                    context ?? "unspecified", exception.GetType().Name,
                    RcloneFailureClassifier.RedactText(exception.Message));
                return RcloneFailureClassifier.RedactText(exception.Message);
        }
    }

    /// <summary>
    /// The copyable diagnostic behind <see cref="Describe"/>. It is kept separate because the short message is
    /// what a status column shows, while this is what a user pastes into a bug report: it carries the operation,
    /// category and correlation id that make the matching log line findable. Every field is already sanitized by
    /// the classifier, so nothing here can leak a credential.
    /// </summary>
    public static string DescribeDetail(Exception exception, string? context = null)
    {
        var heading = $"上下文：{context ?? "未指定"}";
        switch (exception)
        {
            case RcloneException rclone:
                return string.Join(Environment.NewLine,
                    heading,
                    $"异常类型：{rclone.GetType().Name}",
                    $"操作：{rclone.Failure.Operation}",
                    $"类别：{rclone.Failure.Category}（可重试：{(rclone.Failure.Retryable ? "是" : "否")}）",
                    $"HTTP 状态：{Describe(rclone.Failure.HttpStatus)}　RC 状态：{Describe(rclone.Failure.RcStatus)}",
                    $"诊断 ID：{rclone.Failure.CorrelationId}",
                    $"技术信息：{rclone.Failure.TechnicalMessage}",
                    $"请求参数：{rclone.Failure.SanitizedInput}");
            case SftpServerOperationException sftp:
                return string.Join(Environment.NewLine,
                    heading,
                    $"异常类型：{sftp.GetType().Name}",
                    $"操作：{sftp.Operation}",
                    $"技术代码：{sftp.TechnicalCode}",
                    $"诊断 ID：{sftp.CorrelationId}",
                    $"信息：{sftp.Message}");
            default:
                return string.Join(Environment.NewLine,
                    heading,
                    $"异常类型：{exception.GetType().Name}",
                    $"信息：{RcloneFailureClassifier.RedactText(exception.Message)}");
        }
    }

    private static string Describe(int? value) => value?.ToString() ?? "无";
}
