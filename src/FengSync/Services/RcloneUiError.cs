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
}
