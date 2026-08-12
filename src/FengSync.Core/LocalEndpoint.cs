using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FengSync.Core.Diagnostics;
using FengSync.Core.Scanning;

namespace FengSync.Core;
public sealed class LocalEndpoint(string root) : IEndpoint, IEndpointStateStorage, IStagedPublishEndpoint
{
    public string Root { get; } = Path.GetFullPath(root);
    public EndpointProfile Profile { get; } = new(Guid.NewGuid(), EndpointType.Local, Path.GetFullPath(root), Identity: Path.GetFullPath(root));
    // Local scans currently do not capture a platform file ID. Do not advertise
    // stable IDs until that metadata is actually included in EntrySnapshot.
    public EndpointCapabilities Capabilities { get; } = new(false, true, true, TimeSpan.Zero,
        new(MoveEvidenceCapabilities.SizeAndTime, EndpointMoveExecution.NativeRename, EndpointMoveExecution.NativeRename),
        new(false, System.Text.NormalizationForm.FormC));
    public IEnumerable<EntrySnapshot> Scan()
    {
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
        SyncRunMetricsHub.Current.IncrementDirectoryScan();
        foreach (var path in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
            if (SyncInternalPaths.IsExcludedFromScan(relative)) continue;
            var attr = File.GetAttributes(path); if (attr.HasFlag(FileAttributes.System) || attr.HasFlag(FileAttributes.ReparsePoint)) continue;
            EntrySnapshot snapshot;
            if (attr.HasFlag(FileAttributes.Directory)) snapshot = new(relative, EntryKind.Directory, null);
            else { var f = new FileInfo(path); snapshot = new(relative, EntryKind.File, new(f.Length, f.LastWriteTimeUtc, null)); }
            SyncRunMetricsHub.Current.AddEntriesEnumerated(1);
            yield return snapshot;
        }
    }
    public string PhysicalPath(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    public Task<IReadOnlyList<EntrySnapshot>> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<EntrySnapshot>>(Scan().ToList());
    public async IAsyncEnumerable<EntrySnapshot> ScanEntriesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
        var channel = Channel.CreateBounded<EntrySnapshot>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        using var producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = Task.Run(async () =>
        {
            try
            {
                foreach (var entry in Scan())
                    await channel.Writer.WriteAsync(entry, producerCancellation.Token).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex) { channel.Writer.TryComplete(ex); }
        }, CancellationToken.None);
        try
        {
            await foreach (var entry in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return entry;
        }
        finally
        {
            producerCancellation.Cancel();
            try { await producer.ConfigureAwait(false); }
            catch (OperationCanceledException) when (producerCancellation.IsCancellationRequested) { }
        }
    }
    public Task<IReadOnlyList<EntrySnapshot>> ScanAsync(IProgress<ScanProgress> progress, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
        return Task.Run<IReadOnlyList<EntrySnapshot>>(() =>
        {
            var entries = new List<EntrySnapshot>();
            SyncRunMetricsHub.Current.IncrementDirectoryScan();
            foreach (var path in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
                if (SyncInternalPaths.IsExcludedFromScan(relative)) continue;
                var attr = File.GetAttributes(path); if (attr.HasFlag(FileAttributes.System) || attr.HasFlag(FileAttributes.ReparsePoint)) continue;
                entries.Add(attr.HasFlag(FileAttributes.Directory)
                    ? new(relative, EntryKind.Directory, null)
                    : new EntrySnapshot(relative, EntryKind.File, new Fingerprint(new FileInfo(path).Length, File.GetLastWriteTimeUtc(path), null)));
                SyncRunMetricsHub.Current.AddEntriesEnumerated(1);
                if (entries.Count == 1 || entries.Count % 25 == 0) progress.Report(new(entries.Count, relative));
            }
            progress.Report(new(entries.Count, entries.LastOrDefault()?.Path, true));
            return entries;
        }, cancellationToken);
    }
    public Task<EntrySnapshot?> StatAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        SyncRunMetricsHub.Current.IncrementStatCall();
        var physical = PhysicalPath(relativePath);
        if (File.Exists(physical))
        {
            var info = new FileInfo(physical);
            return Task.FromResult<EntrySnapshot?>(new(relativePath, EntryKind.File, new(info.Length, info.LastWriteTimeUtc, null)));
        }
        if (Directory.Exists(physical))
        {
            return Task.FromResult<EntrySnapshot?>(new(relativePath, EntryKind.Directory, null));
        }
        return Task.FromResult<EntrySnapshot?>(null);
    }
    public Task<IReadOnlyList<TransferTemporaryFile>> ListTransferTemporaryFilesAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Root)) return Task.FromResult<IReadOnlyList<TransferTemporaryFile>>([]);
        var files = new List<TransferTemporaryFile>();
        foreach (var file in Directory.EnumerateFiles(Root, "*.partial", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(Root, file).Replace('\\', '/');
            if (!SyncInternalPaths.TryGetTransferTemporaryOriginalPath(relative, out var original)) continue;
            var info = new FileInfo(file);
            files.Add(new(relative, original, info.Length, info.LastWriteTimeUtc));
        }
        return Task.FromResult<IReadOnlyList<TransferTemporaryFile>>(files);
    }
    public async Task CopyToAsync(string relativePath, IEndpoint target, string temporaryPath, CancellationToken cancellationToken = default)
    {
        if (target is not LocalEndpoint localTarget) throw new NotSupportedException("本地端点需要通过统一端点执行器传输到远程端点。");
        var source = PhysicalPath(relativePath); var destination = localTarget.PhysicalPath(temporaryPath); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = File.OpenRead(source); await using var output = File.Create(destination); await input.CopyToAsync(output, cancellationToken);
    }
    public Task MoveAsync(string from, string to, CancellationToken cancellationToken = default)
    { var target = PhysicalPath(to); if (File.Exists(target) || Directory.Exists(target)) throw new IOException($"移动目标已存在：{to}"); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Move(PhysicalPath(from), target, false); return Task.CompletedTask; }
    public Task MoveDirectoryAsync(string from, string to, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = PhysicalPath(from);
        var target = PhysicalPath(to);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException($"移动目标已存在：{to}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(source, target);
        return Task.CompletedTask;
    }
    public Task PublishStagedAsync(string temporaryPath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = PhysicalPath(temporaryPath);
        var target = PhysicalPath(destinationPath);
        if (Directory.Exists(target)) throw new IOException($"复制目标是目录：{destinationPath}");
        if (!overwrite && File.Exists(target)) throw new IOException($"复制目标在比较后出现：{destinationPath}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target, overwrite);
        return Task.CompletedTask;
    }
    public Task DeleteAsync(string relativePath, bool directory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = PhysicalPath(relativePath);
        // The comparison snapshot already established the target kind. Delete
        // directly without enumerating a directory first; absence is success.
        if (directory)
        {
            try { Directory.Delete(path); }
            catch (DirectoryNotFoundException) { }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 145)
            {
                // A deselected child intentionally keeps the directory alive.
                // Treat that as a no-op without listing the directory contents.
            }
        }
        else File.Delete(path);
        return Task.CompletedTask;
    }
    public Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default) { Directory.CreateDirectory(PhysicalPath(relativePath)); return Task.CompletedTask; }
    public Task<string?> DownloadStateAsync(string relativePath, string localDirectory, CancellationToken cancellationToken = default)
    {
        var source = PhysicalPath(relativePath); if (!File.Exists(source)) return Task.FromResult<string?>(null);
        Directory.CreateDirectory(localDirectory); var copy = Path.Combine(localDirectory, Guid.NewGuid().ToString("N") + ".db");
        File.Copy(source, copy); return Task.FromResult<string?>(copy);
    }
    public Task UploadAndPublishStateAsync(string localPath, string temporaryRelativePath, CancellationToken cancellationToken = default)
    {
        var temporary = PhysicalPath(temporaryRelativePath); Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        File.Copy(localPath, temporary, true); File.Move(temporary, PhysicalPath(SyncInternalPaths.StateDatabase), true); return Task.CompletedTask;
    }
}
