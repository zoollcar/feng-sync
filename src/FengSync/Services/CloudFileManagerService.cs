using FluentIcon = FluentIcons.Common.Icon;

namespace FengSync.Services;

/// <summary>
/// Directory listing for a saved rclone remote. Transfers deliberately do NOT live here: uploads and
/// downloads go through <see cref="CloudTransferQueue"/> so they reuse the sync engine's staging,
/// progress, cancellation, and journal behaviour instead of growing a second, divergent copy path
/// that silently lacks all of it.
/// </summary>
public sealed class CloudFileManagerService
{
    public async Task<IReadOnlyList<CloudFileEntry>> ListAsync(string remote, string path, CancellationToken ct = default)
    {
        var client = await AppServices.Current.RcloneHost.GetClientAsync(ct);
        var entries = await client.ListDirectoryAsync(FileSystem(remote), path.Trim('/'), ct);
        return entries.Select(x => new CloudFileEntry(NameOf(x.Path), x.Path, x.IsDirectory, x.Size, x.ModifiedUtc))
            .OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static string Join(string directory, string name) => string.IsNullOrWhiteSpace(directory) ? name : directory.Trim('/') + "/" + name;
    public static string Parent(string path) { var index = path.Trim('/').LastIndexOf('/'); return index < 0 ? "" : path.Trim('/')[..index]; }
    private static string FileSystem(string remote) => remote.EndsWith(':') ? remote : remote + ":";
    private static string NameOf(string path) => path.Trim('/').Split('/').Last();
}

public sealed record CloudFileEntry(string Name, string Path, bool IsDirectory, long Size, DateTimeOffset? ModifiedUtc)
{
    // Fully qualified: this record's own Path property shadows System.IO.Path here.
    public FluentIcon Icon => IsDirectory ? FluentIcon.Folder : ExtensionIcon(System.IO.Path.GetExtension(Name));
    public string Type => IsDirectory ? "文件夹" : "文件";
    public string SizeDisplay => IsDirectory ? "" : Size < 1024 ? Size + " B" : $"{Size / 1024d / 1024d:N1} MB";
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
