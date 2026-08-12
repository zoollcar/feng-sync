namespace FengSync.Core;

public enum TransferStage { Pending, Preparing, Transferring, Verifying, Committed, Deleting, Failed, Cancelled }

public sealed record TransferProgress(
    Guid OperationId,
    string Path,
    TransferStage Stage,
    long BytesCompleted,
    long TotalBytes,
    int ActiveTransfers = 0,
    string? Error = null);

/// <summary>
/// Result of a planned operation, including verified post-publish metadata used
/// to derive the next paired baseline.
/// </summary>
public sealed record OperationRunResult(
    Guid OperationId,
    string Path,
    OperationKind Kind,
    TransferStage Stage,
    long BytesTransferred = 0,
    string? Error = null,
    Fingerprint? SourceAfter = null,
    Fingerprint? TargetAfter = null,
    bool Published = false);

public sealed record SyncRunResult(
    Guid RunId,
    IReadOnlyList<OperationRunResult> Operations,
    bool BaselineCommitted = false,
    bool NeedsRecovery = false)
{
    public int SucceededOperations => Operations.Count(x => x.Stage == TransferStage.Committed);
    public int FailedOperations => Operations.Count(x => x.Stage == TransferStage.Failed);
    public bool Succeeded => FailedOperations == 0 && Operations.All(x => x.Stage == TransferStage.Committed);
}
