using System.Collections.Concurrent;
using FengSync.Core;

namespace FengSync.Lan;

/// <summary>What landed.</summary>
/// <param name="Name">The name it was stored under, which is not always the name it was sent as.</param>
/// <param name="RelativePath">The whole stored path, in wire form, relative to the receive directory.</param>
/// <param name="AbsolutePath">The stored file, so the window can reveal it.</param>
/// <param name="Bytes">Bytes actually written — the number the sender's progress bar must agree with.</param>
/// <param name="Renamed">Whether this application picked a different name than the one it was handed.</param>
public sealed record LanReceived(string Name, string RelativePath, string AbsolutePath, long Bytes, bool Renamed);

/// <summary>
/// The one place inbound LAN bytes reach the disk.
/// </summary>
/// <remarks>
/// <para>
/// It reuses Feng Sync's staging contract rather than inventing a smaller one: the bytes go to
/// <see cref="SyncInternalPaths.TransferTemporary"/> — <c>&lt;name&gt;.fengsync.partial</c> — inside the
/// directory the file will live in, and only then move into place through
/// <see cref="IStagedPublishEndpoint.PublishStagedAsync"/>. That is what makes an interrupted receive leave
/// a half-written file the scan already knows to hide, instead of a plausible-looking full one. The staging
/// name is the bare name and not the remote-relative path for the same reason the cloud download stages
/// that way: the publish is a rename inside one directory, so the two halves have to share a parent.
/// </para>
/// <para>
/// The receive directory is the sink's whole world. There is no parameter that lets a caller name a
/// destination outside it, and a path that traverses out is refused by <see cref="LanRelativePath"/> before
/// anything is created. The containment check below is therefore the second of two independent guards, not
/// the only one — the root itself can be a junction, which is a fact no string rule can see.
/// </para>
/// </remarks>
public sealed class LanReceiveSink
{
    /// <summary>
    /// The room a composed staging path is allowed to take. <c>MAX_PATH</c> without a long-path manifest is
    /// what the destination filesystem will actually accept here, and the staging suffix is part of the path
    /// while the bytes are being written.
    /// </summary>
    public const int MaximumPathLength = 260;

    /// <summary>
    /// How far a modification time may disagree and still be the same file. Windows filesystems differ in
    /// timestamp granularity, and an exact comparison would reject a correct receive on some of them.
    /// </summary>
    public static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> DestinationGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly LocalEndpoint _endpoint;
    private readonly Func<string, IReadOnlyList<string>, string> _chooseFreeName;
    private readonly bool _verifyAfterPublish;

    /// <param name="receiveDirectory">The root every received path is composed against.</param>
    /// <param name="chooseFreeName">
    /// The collision rule, supplied rather than re-derived here because <c>CloudFileNames.KeepBothName</c>
    /// is the application's one naming rule and a second copy of it would drift the first time either side
    /// changed. It is handed the wanted name and the destination directory's own listing.
    /// </param>
    /// <param name="verifyAfterPublish">
    /// Compare size and modification time after the move, the same pair <c>StatVerifier</c> compares for
    /// copies. Not a hash: a LAN receive has no second copy to hash against at that moment, and re-reading a
    /// gigabyte to prove the bytes we just wrote is a cost the user never asked for.
    /// </param>
    public LanReceiveSink(
        string receiveDirectory,
        Func<string, IReadOnlyList<string>, string> chooseFreeName,
        bool verifyAfterPublish = true)
    {
        _root = Path.GetFullPath(receiveDirectory);
        _endpoint = new LocalEndpoint(_root);
        _chooseFreeName = chooseFreeName;
        _verifyAfterPublish = verifyAfterPublish;
    }

    /// <summary>The directory this sink will write into, normalised once.</summary>
    public string ReceiveDirectory => _root;

    /// <summary>
    /// Takes a request body down to the receive directory and publishes it under a free name.
    /// </summary>
    /// <param name="declaredBytes">The sender's own size, or 0 when it declined to say.</param>
    /// <param name="sourceWriteUtc">
    /// The modification time the sender reported. Set on the staging file <b>before</b> the publish, because
    /// a move keeps the source's timestamp and there is no second chance after it.
    /// </param>
    /// <exception cref="LanTransferException">
    /// Carries <see cref="LanRefusal"/> for everything this method can decide — an unsafe path, a name the
    /// collision rule could not free, a short body, a destination that disagreed with the sender afterwards.
    /// </exception>
    public async Task<LanReceived> ReceiveAsync(
        Stream body,
        string? relativeWirePath,
        long declaredBytes,
        DateTimeOffset? sourceWriteUtc,
        IProgress<long>? progress,
        CancellationToken ct)
    {
        var safe = Resolve(relativeWirePath);
        // The staging name is derived from the stored name, so two peers sending the same path would otherwise
        // write into one partial file and publish whichever finished last. Holding one destination at a time is
        // what makes that deterministic name safe: the second transfer waits, then reads a listing that already
        // has the first file in it and renames itself. It also means a leftover partial from a process that
        // crashed is garbage rather than somebody's in-flight work, so overwriting it is correct.
        var gate = DestinationGates.GetOrAdd(Compose(safe), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReceiveLockedAsync(body, safe, declaredBytes, sourceWriteUtc, progress, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<LanReceived> ReceiveLockedAsync(
        Stream body,
        string safe,
        long declaredBytes,
        DateTimeOffset? sourceWriteUtc,
        IProgress<long>? progress,
        CancellationToken ct)
    {
        var name = LanRelativePath.NameOf(safe);
        var parent = LanRelativePath.ParentOf(safe);

        var (storedName, renamed) = PickName(parent, name);
        var storedRelative = Combine(parent, storedName);
        var stagingRelative = Combine(parent, SyncInternalPaths.TransferTemporary(storedName));
        var storedFull = Compose(storedRelative);
        var stagingFull = Compose(stagingRelative);
        GuardLength(stagingFull);

        ct.ThrowIfCancellationRequested();
        var parentFull = Path.GetDirectoryName(stagingFull)!;
        Directory.CreateDirectory(parentFull);

        long written;
        try
        {
            written = await StageAsync(body, stagingFull, declaredBytes, sourceWriteUtc, progress, ct).ConfigureAwait(false);
        }
        catch
        {
            // A staging file that never became a file is this application's own litter, and leaving it
            // beside the user's files would make the next receive look at it as a candidate.
            TryDelete(stagingFull);
            throw;
        }

        if (declaredBytes > 0 && written != declaredBytes)
        {
            TryDelete(stagingFull);
            throw LanTransferException.For(LanRefusal.IncompleteBody,
                $"期望 {declaredBytes} 字节，实际收到 {written} 字节。");
        }

        try
        {
            await _endpoint.PublishStagedAsync(stagingRelative, storedRelative, overwrite: false, ct)
                .ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            TryDelete(stagingFull);
            // Either the name appeared between choosing it and moving onto it, or something already there is
            // a directory. Both are the sender's "the cloud has that name now", not this application's fault.
            throw new LanTransferException(LanRefusal.Conflict, $"落地时名字已被占用：{storedName}", ex);
        }

        if (_verifyAfterPublish) Verify(storedFull, written, sourceWriteUtc);
        return new LanReceived(storedName, storedRelative, storedFull, written, renamed);
    }

    /// <summary>
    /// Creates a directory inside the receive root so an empty folder a peer sent still arrives.
    /// </summary>
    /// <remarks>
    /// A recursive listing names directories that hold nothing, and no file transfer would ever create one —
    /// the same reason the drag-out path creates every directory before transferring anything.
    /// </remarks>
    public Task<string> EnsureDirectoryAsync(string? relativeWirePath, CancellationToken ct = default)
    {
        var safe = Resolve(relativeWirePath);
        var full = Compose(safe);
        GuardLength(full);
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(full);
        return Task.FromResult(safe);
    }

    /// <summary>
    /// The absolute path a stored relative path names inside the receive directory, through the same composition
    /// and containment check every path here goes through.
    /// </summary>
    /// <remarks>
    /// Exists for the room, which has to be able to point one entry at the <b>folder</b> a tree landed in: a
    /// per-file receive only ever names its own file, while a folder is announced once and arrives many times.
    /// This application has no caller-built read path, and a public method that turns a name into a path is where
    /// one could begin, so the only thing it accepts is a path the caller has just watched this class accept.
    /// </remarks>
    public string AbsolutePathOf(string storedRelative) => Compose(storedRelative);

    /// <summary>
    /// Whether the receive directory can be written into at all. Asked before the window says it is
    /// listening: a directory deleted since it was chosen is a different situation from one never chosen, and
    /// only the window knows which sentence each deserves.
    /// </summary>
    public bool DirectoryIsUsable()
    {
        if (File.Exists(_root)) return false;
        try
        {
            Directory.CreateDirectory(_root);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string Resolve(string? relativeWirePath)
    {
        if (!LanRelativePath.TryResolve(relativeWirePath, out var safe, out var refusal))
            throw LanTransferException.For(refusal, $"不接收这条路径：{relativeWirePath}");
        return safe;
    }

    /// <summary>
    /// The name this file will be stored under, taken from the destination directory's own listing.
    /// </summary>
    /// <remarks>
    /// A name that is free in one directory is not free in another, which is why the listing is read here
    /// rather than from anything the peer claimed about its own tree. A directory holding the wanted name
    /// counts as taken: <c>a.txt</c> arriving where <c>a.txt</c> is a folder becomes <c>a (1).txt</c>, not a
    /// merge nobody asked for.
    /// </remarks>
    private (string Name, bool Renamed) PickName(string parent, string name)
    {
        var taken = Listing(parent);
        if (!taken.Contains(name, StringComparer.OrdinalIgnoreCase)) return (name, false);

        var chosen = _chooseFreeName(name, taken);
        if (string.Equals(chosen, name, StringComparison.OrdinalIgnoreCase))
            throw LanTransferException.For(LanRefusal.Conflict, $"名字已被占用且无法改名：{name}");
        return (chosen, true);
    }

    private IReadOnlyList<string> Listing(string parent)
    {
        var directory = parent.Length == 0 ? _root : Compose(parent);
        if (!Directory.Exists(directory)) return [];
        try
        {
            return Directory.EnumerateFileSystemEntries(directory)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Cast<string>()
                // The application's own staging name is not the user's file, and letting it hold a name
                // would make the next receive rename itself over a transfer that is still in flight.
                .Where(name => !SyncInternalPaths.IsTransferTemporary(name))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LanTransferException(LanRefusal.PathUnsafe, $"无法列出接收目录：{directory}", ex);
        }
    }

    private async Task<long> StageAsync(
        Stream body,
        string stagingFull,
        long declaredBytes,
        DateTimeOffset? sourceWriteUtc,
        IProgress<long>? progress,
        CancellationToken ct)
    {
        var written = 0L;
        // A larger buffer than a local copy uses, because this loop is the network side of the same file and
        // a 4 KiB read here would spend the receive in syscalls rather than in bytes.
        var buffer = new byte[81920];
        await using (var staging = File.Create(stagingFull, buffer.Length))
        {
            while (true)
            {
                var read = await body.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) break;
                written += read;
                if (declaredBytes > 0 && written > declaredBytes)
                    // More than the sender promised means the length is not the fact we thought it was; stop
                    // before filling a disk on somebody else's mistake.
                    throw LanTransferException.For(LanRefusal.IncompleteBody,
                        $"收到的字节超过声明的 {declaredBytes} 字节。");
                await staging.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                progress?.Report(written);
            }
        }

        if (sourceWriteUtc is { } timestamp)
            File.SetLastWriteTimeUtc(stagingFull, timestamp.UtcDateTime);
        return written;
    }

    private void Verify(string storedFull, long written, DateTimeOffset? sourceWriteUtc)
    {
        var info = new FileInfo(storedFull);
        if (!info.Exists)
            throw LanTransferException.For(LanRefusal.VerificationFailed, $"发布后读不到文件：{storedFull}");
        if (info.Length != written)
            throw LanTransferException.For(LanRefusal.VerificationFailed,
                $"发布后大小为 {info.Length} 字节，写入 {written} 字节。");
        if (sourceWriteUtc is { } expected && (info.LastWriteTimeUtc - expected.UtcDateTime).Duration() > TimestampTolerance)
            throw LanTransferException.For(LanRefusal.VerificationFailed,
                $"发布后修改时间与发送方声明的不一致：{info.LastWriteTimeUtc:O} 对 {expected.UtcDateTime:O}");
    }

    /// <summary>Composes a wire path against the root and proves it stayed inside.</summary>
    private string Compose(string safeRelative)
    {
        var full = Path.GetFullPath(Path.Combine(_root, safeRelative.Replace('/', Path.DirectorySeparatorChar)));
        var boundary = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) && !string.Equals(full, _root, StringComparison.OrdinalIgnoreCase))
            throw LanTransferException.For(LanRefusal.PathUnsafe, $"路径落到了接收目录之外：{safeRelative}");
        return full;
    }

    private static void GuardLength(string fullPath)
    {
        if (fullPath.Length >= MaximumPathLength)
            throw LanTransferException.For(LanRefusal.PathTooLong,
                $"目标路径长 {fullPath.Length} 字符，超过 {MaximumPathLength} 字符上限。");
    }

    private static string Combine(string parent, string name) => parent.Length == 0 ? name : $"{parent}/{name}";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A staging file we could not remove is hidden from every scan by its own name, so a failure here
            // costs a little disk and nothing else. The next receive into that directory reports it.
        }
    }
}
