using FengSync.Core;
using FengSync.Core.Rclone.Diagnostics;
using FluentIcon = FluentIcons.Common.Icon;

namespace FengSync.Services;

/// <summary>
/// Directory listing and the file operations the browser performs on a saved rclone remote. Transfers
/// deliberately do NOT live here: uploads and downloads go through <see cref="CloudTransferQueue"/> so they
/// reuse the sync engine's staging, progress, cancellation, and journal behaviour instead of growing a
/// second, divergent copy path that silently lacks all of it.
/// </summary>
public sealed class CloudFileManagerService
{
    /// <summary>How many recursive search results are returned before the caller is told to narrow the query.</summary>
    public const int SearchResultLimit = 500;

    private readonly Func<CancellationToken, Task<RcloneRcClient>> _client;

    /// <summary>
    /// Resolves the RC client lazily so a service can be constructed before a shell installed its daemon —
    /// which is what lets the browser's logic be exercised without starting rclone.
    /// </summary>
    public CloudFileManagerService(Func<CancellationToken, Task<RcloneRcClient>>? client = null) =>
        _client = client ?? (ct => AppServices.Current.RcloneHost.GetClientAsync(ct));

    public async Task<IReadOnlyList<CloudFileEntry>> ListAsync(string remote, string path, CancellationToken ct = default)
    {
        var client = await _client(ct);
        var directory = path.Trim('/');
        var entries = await client.ListDirectoryAsync(FileSystem(remote), directory, ct);
        return entries.Select(x => Entry(x, directory))
            .OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Every object at or below <paramref name="path"/> whose name or path contains <paramref name="query"/>.
    /// The listing is recursive and therefore reads the whole subtree before filtering, which is the price of
    /// matching on the path rather than on a file name: a user searching "report" under a large tree means
    /// the folder as much as the file. Results are capped at <see cref="SearchResultLimit"/>.
    /// </summary>
    public async Task<IReadOnlyList<CloudFileEntry>> SearchAsync(string remote, string path, string query, CancellationToken ct = default)
    {
        var client = await _client(ct);
        var directory = path.Trim('/');
        var entries = await client.ListRecursiveAsync(FileSystem(remote), directory, ct);
        return entries
            .Select(x => Entry(x, directory))
            // The searched directory itself comes back as an empty-path entry; it is not a result.
            .Where(x => x.Path.Length > 0 && CloudBrowse.MatchesQuery(x, query))
            .Take(SearchResultLimit)
            .ToList();
    }

    /// <summary>
    /// Creates one directory. rclone backends without real directories (S3) accept this as a marker and
    /// simply report an empty listing, which is why the browser never depends on the folder appearing.
    /// </summary>
    public async Task CreateDirectoryAsync(string remote, string path, CancellationToken ct = default)
    {
        var client = await _client(ct);
        await client.MakeDirectoryAsync(FileSystem(remote), path.Trim('/'), ct);
    }

    /// <summary>
    /// Renames an entry inside its own directory — a move to the same directory under a new name, so it shares
    /// <see cref="MoveIntoAsync"/> and inherits its measured hazard: a name that is already taken there is
    /// overwritten or merged into, not refused.
    /// </summary>
    public Task RenameAsync(string remote, string path, string newName, bool isDirectory, CancellationToken ct = default) =>
        MoveIntoAsync(remote, path, CloudFileNames.RenameTarget(path.Trim('/'), newName), isDirectory, ct);

    /// <summary>
    /// Deletes an entry. A directory is purged rather than removed-empty: the browser's "delete this folder"
    /// means the folder and what is inside it, and on S3 — which has no directories at all — purging the key
    /// prefix is the only operation that can express that. The caller is responsible for confirming first;
    /// see <see cref="CloudDeleteSafety"/>.
    /// </summary>
    public async Task DeleteAsync(string remote, string path, bool isDirectory, CancellationToken ct = default)
    {
        var client = await _client(ct);
        var filesystem = FileSystem(remote);
        var target = path.Trim('/');
        if (isDirectory) await client.PurgeAsync(filesystem, target, ct);
        else await client.DeleteFileAsync(filesystem, target, ct);
    }

    /// <summary>
    /// Moves one file to another path inside the same remote. rclone answers this without moving bytes
    /// through this process, so it is what a 剪切 inside one endpoint runs. A move across two endpoints is
    /// deliberately not here: that is a copy with a delete after it, and it belongs to
    /// <see cref="CloudTransferQueue"/> so it gets progress, cancellation and history like every other
    /// transfer — see <see cref="CloudPastePlanner"/>.
    /// </summary>
    public Task MoveAsync(string remote, string sourcePath, string destinationPath, CancellationToken ct = default) =>
        MoveIntoAsync(remote, sourcePath, destinationPath, isDirectory: false, ct);

    /// <summary>
    /// Moves one object to another path inside the same remote. A file goes through <c>operations/movefile</c>,
    /// and a directory through the sync engine's directory move, because v1.75.0 refuses to movefile a directory
    /// at all (500 "is a directory not a file") and a plain file move would leave a folder's contents behind.
    /// Both legs were measured to <em>overwrite</em> a destination that already exists, so the caller has to
    /// choose a free destination name first — see <see cref="CloudRecycleBin.PlacementsFor"/> — and a directory
    /// caller that wants its source gone has to follow up with <see cref="RemoveIfEmptyAsync"/>.
    /// </summary>
    public async Task MoveIntoAsync(string remote, string sourcePath, string destinationPath, bool isDirectory, CancellationToken ct = default)
    {
        var client = await _client(ct);
        var filesystem = FileSystem(remote);
        var source = sourcePath.Trim('/');
        var target = destinationPath.Trim('/');
        if (isDirectory) await client.MoveDirectoryAsync(filesystem + source, filesystem + target, ct);
        else await client.MoveFileAsync(filesystem, source, target, ct);
    }

    /// <summary>
    /// Removes a directory that is empty, and does nothing when it still holds something. This exists for the
    /// husk a directory move leaves behind: measured on v1.75.0, moving a directory onto a destination that
    /// already exists merges into it and leaves the source sitting there empty — <c>--delete-empty-src-dirs</c>
    /// clears the source's subdirectories but never the source root — while moving onto a fresh path renames it
    /// and the source is gone. Existence is checked with <c>operations/stat</c> rather than inferred from
    /// <c>rmdir</c>'s answer, because rmdir reports a path that is not there as an HTTP 500 with the operating
    /// system's own wording, which the shared classifier reads as a temporary failure.
    /// </summary>
    public async Task<bool> RemoveIfEmptyAsync(string remote, string path, CancellationToken ct = default)
    {
        var client = await _client(ct);
        var filesystem = FileSystem(remote);
        var directory = path.Trim('/');
        if (directory.Length == 0 || await client.StatAsync(filesystem, directory, null, ct) is null) return false;
        await client.RemoveDirectoryAsync(filesystem, directory, ct);
        return true;
    }

    /// <summary>
    /// The names one directory holds, for choosing a landing name that is free. Built on
    /// <see cref="ListOrEmptyAsync"/>, so it inherits that reading of a missing directory. Names cover both
    /// files and directories because a landing name that belongs to an existing <em>directory</em> is refused by
    /// the move itself (500 "is a directory not a file").
    /// </summary>
    public async Task<IReadOnlyList<string>> NamesAsync(string remote, string path, CancellationToken ct = default)
    {
        var entries = await ListOrEmptyAsync(remote, path, ct);
        return entries.Select(entry => entry.Name).ToList();
    }

    /// <summary>
    /// Lists one directory and reads a directory that is not there as an empty listing rather than as an error:
    /// the trash folder does not exist until the first object moves into it, and v1.75.0 answers a missing
    /// directory with 404 "directory not found" while an existing empty one answers <code>{"list": []}</code>
    /// (both measured) — so "not found" means genuinely absent, and the two shapes have to be told apart.
    /// </summary>
    public async Task<IReadOnlyList<CloudFileEntry>> ListOrEmptyAsync(string remote, string path, CancellationToken ct = default)
    {
        try
        {
            return await ListAsync(remote, path, ct);
        }
        catch (RcloneException ex) when (ex.Failure.Category == RcloneFailureCategory.NotFound)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads one object for the details panel without listing its directory.
    /// <paramref name="hashTypes"/> stays null unless the user asked for a checksum: on a backend that stores
    /// none, answering one means rclone reading the whole object, and a details panel that fetched it on
    /// opening would spend that cost on every double-click.
    /// </summary>
    public async Task<CloudItemStat> StatAsync(string remote, string path, IReadOnlyList<string>? hashTypes = null, CancellationToken ct = default)
    {
        var client = await _client(ct);
        var stat = await client.StatAsync(FileSystem(remote), path.Trim('/'), hashTypes, ct);
        return stat is null ? CloudItemStat.Missing : CloudItemStat.Found(stat);
    }

    /// <summary>
    /// Asks the cloud for a public link to one object. A link is not a read: on the backends that do support it,
    /// rclone's default is a grant that anyone holding the URL can use without logging in, which is why the
    /// browser only ever reaches this from an explicit, confirmed action on one file.
    /// </summary>
    public async Task<string?> PublicLinkAsync(string remote, string path, CancellationToken ct = default)
    {
        var client = await _client(ct);
        return await client.PublicLinkAsync(FileSystem(remote), path.Trim('/'), ct);
    }

    /// <summary>
    /// Reads up to <paramref name="maxBytes"/> of one object's bytes — the only content read the browser does
    /// for itself, and the reason the cap is a parameter rather than a constant: a grid tile, a preview and a
    /// checksum each owe the user a different amount of bandwidth for the same file. The status must be read,
    /// never assumed — <see cref="RcloneReadStatus.TooLarge"/> reports the object's real size and no bytes,
    /// <c>Missing</c> and <c>Failed</c> each need their own sentence, and only <c>Read</c> means the bytes in
    /// hand are the whole object.
    /// </summary>
    public async Task<RcloneObjectRead> ReadAsync(string remote, string path, long maxBytes, CancellationToken ct = default)
    {
        var client = await _client(ct);
        return await client.ReadAsync(FileSystem(remote), path.Trim('/'), maxBytes, ct);
    }

    public static string Join(string directory, string name) => string.IsNullOrWhiteSpace(directory) ? name : directory.Trim('/') + "/" + name;
    public static string Parent(string path) { var index = path.Trim('/').LastIndexOf('/'); return index < 0 ? "" : path.Trim('/')[..index]; }

    /// <summary>The last segment of a cloud path — the part worth naming to a user.</summary>
    public static string NameOf(string path) => path.Trim('/').Split('/').Last();

    /// <summary>
    /// The browser addresses paths relative to the rclone remote root, while <see cref="EndpointFactory"/>
    /// addresses an endpoint relative to its configured root (the account's default root folder). Anything
    /// that goes through an endpoint — every transfer, including a paste — has to be told in the second
    /// shape, or the root would be addressed twice.
    /// </summary>
    public static string RelativeToEndpoint(CloudEndpointAccount account, string remotePath)
    {
        var root = account.RootPath.Trim('/');
        var path = (remotePath ?? "").Trim('/');
        if (root.Length == 0) return path;
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return "";
        return path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ? path[(root.Length + 1)..] : path;
    }

    private static string FileSystem(string remote) => remote.EndsWith(':') ? remote : remote + ":";

    /// <summary>
    /// Turns one RC entry into a browser entry whose <see cref="CloudFileEntry.Path"/> is always relative to
    /// the remote root, whatever shape the backend used. rclone reports <c>Path</c> with or without the
    /// browsed prefix depending on the backend, and a listing whose paths were read the wrong way would send
    /// a double-click to the wrong directory instead of failing loudly.
    /// </summary>
    private static CloudFileEntry Entry(RcloneDirectoryEntry entry, string directory)
    {
        var path = entry.Path.Trim('/');
        if (directory.Length > 0
            && !path.Equals(directory, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase))
            path = Join(directory, path);
        return new CloudFileEntry(NameOf(path), path, entry.IsDirectory, entry.Size, entry.ModifiedUtc);
    }
}

public sealed record CloudFileEntry(string Name, string Path, bool IsDirectory, long Size, DateTimeOffset? ModifiedUtc)
{
    /// <summary>
    /// Set by a recursive search, whose results come from many directories and are therefore only
    /// identifiable by their full path. Left off for a plain listing, where every row is in the directory
    /// already named by the address bar.
    /// </summary>
    public bool ShowFullPath { get; init; }

    /// <summary>What the name column shows: the full path for a search result, the bare name otherwise.</summary>
    public string NameDisplay => ShowFullPath ? Path : Name;

    // Fully qualified: this record's own Path property shadows System.IO.Path here.
    public FluentIcon Icon => IsDirectory ? FluentIcon.Folder : ExtensionIcon(System.IO.Path.GetExtension(Name));
    public string Type => IsDirectory ? "文件夹" : "文件";
    public string SizeDisplay => IsDirectory ? "" : CloudItemDetails.HumanSize(Size);
    public string ModifiedDisplay => ModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";

    private static FluentIcon ExtensionIcon(string extension) => extension.ToLowerInvariant() switch
    {
        ".txt" or ".log" or ".md" or ".rtf" => FluentIcon.DocumentText,
        ".doc" or ".docx" or ".odt" => FluentIcon.DocumentWord,
        ".xls" or ".xlsx" or ".csv" or ".ods" => FluentIcon.DocumentTable,
        ".ppt" or ".pptx" or ".odp" => FluentIcon.SlideText,
        ".pdf" => FluentIcon.DocumentPdf,
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" => FluentIcon.Image,
        ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" => FluentIcon.MusicNote2,
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" => FluentIcon.Video,
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => FluentIcon.Archive,
        ".cs" or ".js" or ".ts" or ".py" or ".java" or ".json" or ".xml" or ".html" or ".css" => FluentIcon.Code,
        _ => FluentIcon.Document
    };
}
