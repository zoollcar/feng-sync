using System.Security.Cryptography;
using FengSync.Core.Rclone.Diagnostics;

namespace FengSync.Core.Execution;

/// <summary>What a transfer should do with its staging file: where to stage, and how much is already there.</summary>
internal readonly record struct ResumeDecision(string TemporaryPath, long Offset)
{
    public bool Resumed => Offset > 0;
}

/// <summary>
/// Decides whether an interrupted transfer can continue, and proves that the bytes already staged
/// are still a prefix of the current source before it does.
/// <para>
/// rclone cannot resume anything itself: its copy path always restarts a partial transfer from zero,
/// and the RC API has no offset read or offset write. Continuing a transfer is therefore Feng Sync's
/// job, and it works by keeping a deterministic staging file
/// (<c>&lt;name&gt;.fengsync.partial</c>) and appending the missing bytes to it.
/// </para>
/// <para>
/// Resume is only attempted at or above the configured threshold. Below it the transfer uses
/// rclone's multi-stream copy, which writes chunks concurrently and can leave holes in a file that
/// was killed mid-flight — such a file is not a trustworthy prefix, so it is never continued.
/// </para>
/// </summary>
internal static class TransferResume
{
    /// <summary>Bytes compared on both sides of the append point before a resumed transfer is trusted.</summary>
    private const int SeamBytes = 64 * 1024;
    /// <summary>The staging write granularity, and therefore the most a single interrupted write can lose.</summary>
    private const int WriteBufferBytes = 128 * 1024;
    /// <summary>How far back a torn tail is searched for, in write buffers.</summary>
    private const int MaxSeamRetreats = 3;

    public static async Task<ResumeDecision> PrepareAsync(IEndpoint source, IEndpoint target, string path,
        IReadOnlyList<TransferTemporaryFile>? knownCandidates, long sourceSize, long resumeMinimumBytes, CancellationToken ct)
    {
        var temporary = SyncInternalPaths.TransferTemporary(path);
        var candidates = (knownCandidates ?? await target.ListTransferTemporaryFilesAsync(ct))
            .Where(x => x.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Size)
            .ToList();

        // A remote target cannot continue a partially written object: rclone has no append or offset
        // write, so the staging object is discarded and the file is written again in one pass.
        if (target is not LocalEndpoint localTarget)
        {
            foreach (var candidate in candidates) await target.DeleteAsync(candidate.RelativePath, false, ct);
            return new(temporary, 0);
        }

        var destination = localTarget.PhysicalPath(temporary);
        var staged = File.Exists(destination) ? new FileInfo(destination).Length : 0;
        var eligible = sourceSize > 0 && sourceSize >= resumeMinimumBytes;
        var resumed = eligible
            ? await TryResumeAsync(source, target, localTarget, path, temporary, destination, staged, candidates, sourceSize, ct)
            : null;
        if (resumed is { } decision) return new(decision.Path, decision.Offset);

        // Not resumable: drop every staging file for this path so the next attempt starts clean.
        // The resume state is deliberately left alone here — a remote source records its identity
        // below precisely so that an interruption later in this run can be continued by the next one,
        // and removing it here would delete the record the same call just wrote.
        foreach (var candidate in candidates.Where(x => !x.RelativePath.Equals(temporary, StringComparison.OrdinalIgnoreCase)))
            await target.DeleteAsync(candidate.RelativePath, false, ct);
        if (staged > 0) File.Delete(destination);
        return new(temporary, 0);
    }

    /// <summary>
    /// Returns the staging file and the offset to continue from, or <c>null</c> to restart from zero.
    /// </summary>
    private static async Task<(string Path, long Offset)?> TryResumeAsync(IEndpoint source, IEndpoint target, LocalEndpoint localTarget,
        string path, string temporary, string destination, long staged, IReadOnlyList<TransferTemporaryFile> candidates, long sourceSize, CancellationToken ct)
    {
        if (source is LocalEndpoint localSource)
        {
            // Both sides are local, so the staged bytes can be verified against the source outright by
            // comparing the SHA-256 of the prefix. A mismatch means the source changed underneath us.
            var sourcePath = localSource.PhysicalPath(path);
            var sourceLength = new FileInfo(sourcePath).Length;
            foreach (var candidate in StagedCandidates(candidates, temporary, destination, path, staged)
                         .Where(x => x.Size > 0 && x.Size <= sourceLength))
                if (await PrefixMatchesAsync(sourcePath, localTarget.PhysicalPath(candidate.RelativePath), candidate.Size, ct))
                    return (candidate.RelativePath, candidate.Size);
            return null;
        }

        if (source is not RcloneEndpoint remoteSource) return null;

        // A remote source cannot be hashed for free, so the staged file is trusted only when the
        // source still reports the size and modification time it had when the staging file was
        // created, and the bytes either side of the append point still match.
        var probe = await remoteSource.Client.ProbeAsync(remoteSource.FileSystem, remoteSource.RemotePath(path), ct);
        var store = new TransferResumeStateStore();
        if (probe is null || probe.Length != sourceSize || probe.ModifiedUtc is not { } modified)
        {
            // The source cannot be described well enough to prove the staged bytes belong to it, so
            // any record claiming otherwise is stale and must not be used by a later run either.
            await store.RemoveAsync(target, path, ct);
            return null;
        }
        var identity = new TransferResumeState(destination, SourceKey(remoteSource, path), probe.Length, modified);
        if (staged > 0 && staged <= sourceSize)
        {
            var recorded = await store.LoadAsync(target, path, ct);
            if (recorded == identity)
            {
                var verified = await VerifiedPrefixAsync(remoteSource, path, destination, staged, ct);
                if (verified > 0)
                {
                    // Refresh the entry so pruning cannot drop a record whose staging file is still live.
                    await store.SaveAsync(target, path, identity, ct);
                    return (temporary, verified);
                }
            }
        }
        // Record this source so an interruption later in this run can be continued by the next one.
        await store.SaveAsync(target, path, identity, ct);
        return null;
    }

    /// <summary>
    /// The longest prefix of the staging file that still matches the source, truncating the file to it.
    /// <para>
    /// The tail is the part that can be wrong: an interruption during a write can leave bytes that were
    /// counted but never fully stored, and a resumed transfer must never append to those. Discarding a
    /// long transfer over a few bad bytes would be almost as bad as not resuming at all, so the
    /// verified prefix is kept and the unverified tail is cut away instead.
    /// </para>
    /// </summary>
    private static async Task<long> VerifiedPrefixAsync(RcloneEndpoint source, string path, string staged, long stagedLength, CancellationToken ct)
    {
        // A single interrupted write can only lose one buffer, so a couple of steps back is enough, and
        // the bound keeps a corrupt file from turning into an unbounded scan.
        for (var step = 0; step <= MaxSeamRetreats; step++)
        {
            var candidate = stagedLength - (step * WriteBufferBytes);
            if (candidate <= 0) break;
            if (!await SeamMatchesAsync(source, path, staged, candidate, ct)) continue;
            if (candidate == stagedLength) return candidate;
            await using var file = new FileStream(staged, FileMode.Open, FileAccess.Write, FileShare.None, WriteBufferBytes, FileOptions.Asynchronous);
            file.SetLength(candidate);
            return candidate;
        }
        return 0;
    }

    /// <summary>Appends the missing tail of a remote object to its local staging file.</summary>
    /// <param name="offset">
    /// The verified prefix length from <see cref="PrepareAsync"/>. The staging file is cut back to it
    /// first, so an append can never extend bytes that were not verified against the source.
    /// </param>
    public static async Task AppendRemoteAsync(RcloneEndpoint source, LocalEndpoint target, string path, string temporary,
        long offset, Action<long>? progress, CancellationToken ct)
    {
        var destination = target.PhysicalPath(temporary);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination) && new FileInfo(destination).Length != offset)
        {
            await using var staging = new FileStream(destination, FileMode.Open, FileAccess.Write, FileShare.None, WriteBufferBytes, FileOptions.Asynchronous);
            staging.SetLength(offset);
        }
        var content = await source.Client.OpenRangeAsync(source.FileSystem, source.RemotePath(path), offset, ct).ConfigureAwait(false);
        if (content is null)
        {
            // The daemon did not honour the range, so the staging file cannot be continued. Discard
            // the part that is there and read the object from the beginning instead.
            offset = 0;
            content = await source.Client.OpenRangeAsync(source.FileSystem, source.RemotePath(path), 0, ct).ConfigureAwait(false)
                ?? throw new RcloneException(RcloneFailureClassifier.FromJob("operations/cat", "rclone 拒绝提供文件内容。"));
        }
        var modified = content.ModifiedUtc;
        using (content)
        await using (var output = new FileStream(destination, offset == 0 ? FileMode.Create : FileMode.OpenOrCreate,
            FileAccess.Write, FileShare.None, WriteBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            output.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[WriteBufferBytes];
            var copied = offset;
            progress?.Invoke(copied);
            int read;
            while ((read = await content.Content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                copied += read;
                progress?.Invoke(copied);
            }
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        // The staging file has to carry the source object's modification time. Publishing is a rename,
        // so whatever the staging file holds is what the user sees, and the post-copy verification
        // compares fingerprints — size plus modification time — rather than hashes. rclone's own copy
        // path preserves the time as well, so leaving "now" here fails every download as
        // "传输验证失败". AppendLocalAsync already does the same for a local source.
        if (modified is { } stamp) File.SetLastWriteTimeUtc(destination, stamp.UtcDateTime);
    }

    public static async Task AppendLocalAsync(LocalEndpoint source, LocalEndpoint target, string path, string temporaryPath, Action<long>? progress, CancellationToken ct)
    {
        var sourcePath = source.PhysicalPath(path);
        var destinationPath = target.PhysicalPath(temporaryPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var offset = File.Exists(destinationPath) ? new FileInfo(destinationPath).Length : 0;
        await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(destinationPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            input.Seek(offset, SeekOrigin.Begin); output.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[128 * 1024];
            var copied = offset;
            progress?.Invoke(copied);
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                copied += read;
                progress?.Invoke(copied);
            }
        }
        File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
    }

    public static async Task DiscardCandidatesAsync(IEndpoint target, string path, CancellationToken ct)
    {
        foreach (var temporary in (await target.ListTransferTemporaryFilesAsync(ct)).Where(x => x.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            await target.DeleteAsync(temporary.RelativePath, false, ct);
    }

    /// <summary>
    /// The staging names to consider for a path: everything the endpoint reported plus the
    /// deterministic name, which an endpoint listing may not have reached yet.
    /// </summary>
    private static IEnumerable<TransferTemporaryFile> StagedCandidates(IReadOnlyList<TransferTemporaryFile> candidates,
        string temporary, string destination, string path, long staged)
    {
        foreach (var candidate in candidates) yield return candidate;
        if (staged <= 0) yield break;
        if (candidates.Any(x => x.RelativePath.Equals(temporary, StringComparison.OrdinalIgnoreCase))) yield break;
        yield return new(temporary, path, staged, File.GetLastWriteTimeUtc(destination));
    }

    /// <summary>Identifies the source object a staging file belongs to, so two sources never share one.</summary>
    private static string SourceKey(IEndpoint source, string path) =>
        $"{source.Profile.Type}|{source.Profile.Remote}|{source.Profile.Root}|{path}";

    /// <summary>
    /// Compares the bytes immediately before the append point on both sides. This is what makes the
    /// junction safe: a staging file that was truncated, torn by a crash, or built from different
    /// content fails here and is restarted rather than appended to.
    /// </summary>
    private static async Task<bool> SeamMatchesAsync(RcloneEndpoint source, string path, string staged, long stagedLength, CancellationToken ct)
    {
        var window = (int)Math.Min(SeamBytes, stagedLength);
        if (window == 0) return true;
        var start = stagedLength - window;
        using var content = await source.Client.OpenRangeAsync(source.FileSystem, source.RemotePath(path), start, ct);
        if (content is null) return false;
        var fromSource = new byte[window];
        var fromStaged = new byte[window];
        if (!await ReadExactlyAsync(content.Content, fromSource, ct)) return false;
        await using var file = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        file.Seek(start, SeekOrigin.Begin);
        if (!await ReadExactlyAsync(file, fromStaged, ct)) return false;
        return CryptographicOperations.FixedTimeEquals(fromSource, fromStaged);
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
            if (read == 0) return false;
            filled += read;
        }
        return true;
    }

    private static async Task<bool> PrefixMatchesAsync(string source, string temporary, long length, CancellationToken ct)
    {
        if (length == 0) return true;
        var sourceHash = await HashPrefixAsync(source, length, ct);
        var temporaryHash = await HashPrefixAsync(temporary, length, ct);
        return CryptographicOperations.FixedTimeEquals(sourceHash, temporaryHash);
    }

    private static async Task<byte[]> HashPrefixAsync(string path, long length, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (read == 0) throw new EndOfStreamException("临时文件长度超过可验证内容。");
            hash.AppendData(buffer, 0, read); remaining -= read;
        }
        return hash.GetHashAndReset();
    }
}
