namespace FengSync.Core.Execution;

/// <summary>
/// The single staging-aware transfer primitive. The sync executor and the cloud file manager both
/// route through here so staging names, resume policy, publish contract, and progress reporting stay
/// identical — a second implementation would silently drift from <see cref="TransferResume"/>.
/// </summary>
public static class EndpointTransfer
{
    /// <summary>
    /// Moves <paramref name="path"/> into a Feng Sync staging object on <paramref name="target"/> and
    /// returns that staging path; the caller owns publishing it. <paramref name="ensureDestinationAsync"/>
    /// runs after staging is chosen and before bytes move, matching the executor's original ordering.
    /// <para>
    /// <paramref name="resumeMinimumBytes"/> is the size at or above which an interrupted transfer is
    /// continued instead of restarted. Below it the transfer uses rclone's multi-stream copy, whose
    /// partially written files are not a trustworthy prefix and are therefore never continued.
    /// </para>
    /// </summary>
    public static async Task<string> CopyToStagingAsync(
        IEndpoint source,
        IEndpoint target,
        string path,
        IReadOnlyList<TransferTemporaryFile>? resumeCandidates,
        Guid operationId,
        long totalBytes,
        int activeTransfers = 0,
        IProgress<TransferProgress>? progress = null,
        Func<CancellationToken, Task>? ensureDestinationAsync = null,
        long resumeMinimumBytes = 0,
        CancellationToken ct = default)
    {
        var decision = await TransferResume.PrepareAsync(source, target, path, resumeCandidates, totalBytes, resumeMinimumBytes, ct);
        if (ensureDestinationAsync is not null) await ensureDestinationAsync(ct);
        void Report(long completed) => progress?.Report(new(operationId, path, TransferStage.Transferring, completed, totalBytes, activeTransfers));
        if (source is LocalEndpoint localSource && target is LocalEndpoint localTarget)
            await TransferResume.AppendLocalAsync(localSource, localTarget, path, decision.TemporaryPath, Report, ct);
        else if (source is RcloneEndpoint remoteSource && target is LocalEndpoint remoteTarget)
            // Deliberately sequential: this is the leg that can be continued, and a parallel write
            // could leave holes in a staging file that a later run would wrongly trust.
            await TransferResume.AppendRemoteAsync(remoteSource, remoteTarget, path, decision.TemporaryPath, decision.Offset, Report, ct);
        else
            await CopyRemoteWithProgressAsync(source, target, path, decision.TemporaryPath, operationId, totalBytes, activeTransfers, progress, ct);
        return decision.TemporaryPath;
    }

    /// <summary>Drops staging objects for a path. Remote backends are retried from zero by policy.</summary>
    public static Task DiscardStagingAsync(IEndpoint target, string path) =>
        TransferResume.DiscardCandidatesAsync(target, path, CancellationToken.None);

    /// <summary>
    /// Publishes a verified staging object to its user-visible path. Endpoints that own an atomic
    /// publish implement <see cref="IStagedPublishEndpoint"/>; everything else is a plain move.
    /// </summary>
    public static async Task PublishAsync(IEndpoint target, string temporary, string destination, bool overwrite, CancellationToken ct = default)
    {
        if (target is IStagedPublishEndpoint publisher) await publisher.PublishStagedAsync(temporary, destination, overwrite, ct);
        else await target.MoveAsync(temporary, destination, ct);
        // The published file can no longer be continued, so its resume state is dead weight.
        await new TransferResumeStateStore().RemoveAsync(target, destination, ct);
    }

    private static async Task CopyRemoteWithProgressAsync(IEndpoint source, IEndpoint target, string path, string temporary, Guid operationId, long totalBytes, int activeTransfers, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var remote = source as RcloneEndpoint ?? target as RcloneEndpoint ?? throw new NotSupportedException("不支持的端点组合。");
        var statsGroup = "fengsync-" + operationId.ToString("N");
        var copy = CopyAsync(source, target, path, temporary, statsGroup, ct);
        while (!copy.IsCompleted)
        {
            await Task.WhenAny(copy, Task.Delay(TimeSpan.FromMilliseconds(350), ct));
            if (copy.IsCompleted) break;
            try
            {
                var stats = await remote.Client.GetTransferStatsAsync(statsGroup, ct);
                if (stats is not null)
                    progress?.Report(new(operationId, path, TransferStage.Transferring, stats.BytesTransferred, totalBytes > 0 ? totalBytes : stats.TotalBytes, activeTransfers));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                // Progress telemetry must never interrupt a healthy transfer.
            }
        }
        await copy;
    }

    /// <summary>
    /// Copies one path onto an explicit staging path with no resume negotiation and no progress.
    /// Used for the cross-endpoint move fallback, which already chose its own staging name.
    /// </summary>
    public static async Task CopyAsync(IEndpoint source, IEndpoint target, string path, string temporary, string? statsGroup, CancellationToken ct)
    {
        if (source is LocalEndpoint localSource && target is LocalEndpoint localTarget)
        {
            await localSource.CopyToAsync(path, localTarget, temporary, ct);
            return;
        }
        var remote = source as RcloneEndpoint ?? target as RcloneEndpoint ?? throw new NotSupportedException("不支持的端点组合。");
        await remote.Client.CopyFileAsync(
            source is LocalEndpoint sl ? sl.Root : ((RcloneEndpoint)source).FileSystem,
            source is LocalEndpoint ? RcloneLocalPathEncoding.ToRclonePath(path) : ((RcloneEndpoint)source).RemotePath(path),
            target is LocalEndpoint tl ? tl.Root : ((RcloneEndpoint)target).FileSystem,
            target is LocalEndpoint ? temporary : ((RcloneEndpoint)target).RemotePath(temporary),
            ct, statsGroup);
    }
}
