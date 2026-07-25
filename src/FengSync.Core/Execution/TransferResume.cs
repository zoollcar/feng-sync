using System.Security.Cryptography;

namespace FengSync.Core.Execution;

/// <summary>Discovers Feng Sync staging files without using a baseline or local task journal.</summary>
internal static class TransferResume
{
    public static async Task<(string TemporaryPath, bool Resumed)> PrepareAsync(IEndpoint source, IEndpoint target, string path, CancellationToken ct)
    {
        var candidates = (await target.ListTransferTemporaryFilesAsync(ct))
            .Where(x => x.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Size)
            .ToList();

        if (source is LocalEndpoint localSource && target is LocalEndpoint localTarget)
        {
            var sourceLength = new FileInfo(localSource.PhysicalPath(path)).Length;
            foreach (var candidate in candidates.Where(x => x.Size <= sourceLength))
            {
                if (await PrefixMatchesAsync(localSource.PhysicalPath(path), localTarget.PhysicalPath(candidate.RelativePath), candidate.Size, ct))
                    return (candidate.RelativePath, candidate.Size > 0);
            }
        }

        // Any remote operation, or a failed local prefix verification, starts from a clean staging object.
        foreach (var candidate in candidates)
            await target.DeleteAsync(candidate.RelativePath, false, ct);
        return (path + ".fengsync-" + Guid.NewGuid().ToString("N") + ".partial", false);
    }

    public static async Task AppendLocalAsync(LocalEndpoint source, LocalEndpoint target, string path, string temporaryPath, CancellationToken ct)
    {
        var sourcePath = source.PhysicalPath(path);
        var destinationPath = target.PhysicalPath(temporaryPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var offset = File.Exists(destinationPath) ? new FileInfo(destinationPath).Length : 0;
        await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(destinationPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            input.Seek(offset, SeekOrigin.Begin); output.Seek(offset, SeekOrigin.Begin);
            await input.CopyToAsync(output, 128 * 1024, ct);
        }
        File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
    }

    public static async Task DiscardCandidatesAsync(IEndpoint target, string path, CancellationToken ct)
    {
        foreach (var temporary in (await target.ListTransferTemporaryFilesAsync(ct)).Where(x => x.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            await target.DeleteAsync(temporary.RelativePath, false, ct);
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
