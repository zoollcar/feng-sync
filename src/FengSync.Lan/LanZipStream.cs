using System.IO.Compression;

namespace FengSync.Lan;

/// <summary>
/// Packs a directory onto a writable stream without ever holding the archive.
/// </summary>
/// <remarks>
/// <para>
/// <c>ZipFile.CreateFromDirectory</c> is the obvious call and the wrong one: it writes a temporary file first,
/// which means a folder sent to a phone costs double the disk in the user's own volume, leaves a <c>.zip</c>
/// nobody asked for when the peer disconnects halfway, and makes this process responsible for cleaning that up.
/// Writing the archive straight onto the response stream means the only artifact is the bytes the peer took.
/// </para>
/// <para>
/// The price of that choice is stated in the UI rather than hidden: a stream written once cannot be resumed, so
/// a phone that drops the connection asks for the whole folder again. A single file, by contrast, is served with
/// range support and can pick up where it stopped.
/// </para>
/// </remarks>
public static class LanZipStream
{
    /// <summary>
    /// Streams <paramref name="directory"/> as a zip whose entries all start with the folder's own name, so
    /// unzipping on the receiving side creates one directory rather than scattering the tree into the current one.
    /// </summary>
    public static async Task WriteAsync(Stream output, string directory, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);

        var folder = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        // A drive root has no name to carry, and an archive of "C:/" with no top directory would unzip the
        // user's whole volume into wherever the phone happened to put it.
        if (folder.Length == 0) throw new ArgumentException("没有名字可打包的目录。", nameof(directory));

        // Create mode writes the central directory when the archive is disposed, so the archive must be disposed
        // before the response ends — and it must not close the response stream on the way out. There is no
        // asynchronous way to be rid of it: <c>ZipArchive</c> has no <c>IAsyncDisposable</c>, and measured here,
        // disposing it synchronously is what wrote through Kestrel's response body and got refused. The route
        // that calls this therefore lifts that gate for its own response, and nothing else does.
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        // An entry that is never opened lands as a zero-length name, which is exactly what a directory is.
        archive.CreateEntry($"{folder}/", CompressionLevel.NoCompression);

        foreach (var entry in LanFolderTree.Entries(root))
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var name = $"{folder}/{entry.RelativePath}";

            if (entry.IsDirectory)
            {
                // Named only when it holds nothing: a directory with children arrives through them, and an
                // empty one would otherwise vanish — the same reason the inbound path has its own mkdir.
                if (Directory.EnumerateFileSystemEntries(path).Any()) continue;
                archive.CreateEntry($"{name}/", CompressionLevel.NoCompression);
                continue;
            }

            // Fastest rather than Optimal: this pass runs while a phone waits, and these bytes are already on
            // local disk, where deflating a photo or a video costs time and buys nothing.
            var archiveEntry = archive.CreateEntry(name, CompressionLevel.Fastest);
            await using var source = File.OpenRead(path);
            await using var target = archiveEntry.Open();
            await source.CopyToAsync(target, ct).ConfigureAwait(false);
        }
    }
}
