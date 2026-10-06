using System.Diagnostics;
using FengSync.Core.Rclone.Diagnostics;
using FengSync.Core.SftpServer;
using FengSync.Localization;

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
                return Tr.Format("Rclone.DiagnosticId", rclone.Failure.UserMessage, rclone.Failure.SuggestedAction,
                    rclone.Failure.CorrelationId);
            case SftpServerOperationException sftp:
                Trace.TraceError(
                    "SFTP failure context={0}; operation={1}; code={2}; correlationId={3}",
                    context ?? "unspecified", sftp.Operation, sftp.TechnicalCode, sftp.CorrelationId);
                return Tr.Format("Rclone.DiagnosticId", sftp.Message, sftp.SuggestedAction, sftp.CorrelationId);
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
        var heading = Tr.Format("Rclone.Context", context ?? Tr.F("Rclone.Unspecified"));
        switch (exception)
        {
            case RcloneException rclone:
                return string.Join(Environment.NewLine,
                    heading,
                    Tr.Format("Rclone.ExceptionType", rclone.GetType().Name),
                    Tr.Format("Rclone.Operation", rclone.Failure.Operation),
                    Tr.Format("Rclone.Category", rclone.Failure.Category,
                        rclone.Failure.Retryable ? Tr.F("Common.Yes") : Tr.F("Common.No")),
                    Tr.Format("Rclone.Statuses", Describe(rclone.Failure.HttpStatus), Describe(rclone.Failure.RcStatus)),
                    Tr.Format("Rclone.DiagnosticIdOnly", rclone.Failure.CorrelationId),
                    Tr.Format("Rclone.TechnicalMessage", rclone.Failure.TechnicalMessage),
                    Tr.Format("Rclone.RequestInput", rclone.Failure.SanitizedInput));
            case SftpServerOperationException sftp:
                return string.Join(Environment.NewLine,
                    heading,
                    Tr.Format("Rclone.ExceptionType", sftp.GetType().Name),
                    Tr.Format("Rclone.Operation", sftp.Operation),
                    Tr.Format("Rclone.TechnicalCode", sftp.TechnicalCode),
                    Tr.Format("Rclone.DiagnosticIdOnly", sftp.CorrelationId),
                    Tr.Format("Rclone.Message", sftp.Message));
            default:
                return string.Join(Environment.NewLine,
                    heading,
                    Tr.Format("Rclone.ExceptionType", exception.GetType().Name),
                    Tr.Format("Rclone.Message", RcloneFailureClassifier.RedactText(exception.Message)));
        }
    }

    private static string Describe(int? value) => value?.ToString() ?? Tr.F("Rclone.NoStatus");
}
