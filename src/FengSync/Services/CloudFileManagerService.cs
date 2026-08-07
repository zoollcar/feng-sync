using System.Diagnostics;
using System.IO;
using System.Net.Http;
using FengSync.Core;
using FengSync.Core.Rclone.Diagnostics;
using FluentIcon = FluentIcons.Common.Icon;

namespace FengSync.Services;

/// <summary>File-manager operations for a saved rclone remote.  This deliberately stays outside the sync planner.</summary>
public sealed class CloudFileManagerService
{
    public async Task<IReadOnlyList<CloudFileEntry>> ListAsync(string remote, string path, CancellationToken ct = default)
    {
        var client = await App.CurrentApp.RcloneHost.GetClientAsync(ct);
        var entries = await client.ListDirectoryAsync(FileSystem(remote), path.Trim('/'), ct);
        return entries.Select(x => new CloudFileEntry(NameOf(x.Path), x.Path, x.IsDirectory, x.Size, x.ModifiedUtc))
            .OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task UploadAsync(string remote, string directory, string localPath, IProgress<CloudTransferProgress>? progress, CancellationToken ct = default)
    {
        var destination = Join(directory, Path.GetFileName(localPath));
        await TransferAsync(remote, Path.GetDirectoryName(localPath)!, Path.GetFileName(localPath), FileSystem(remote), destination,
            new FileInfo(localPath).Length, progress, ct);
    }

    public async Task DownloadAsync(string remote, string remotePath, string localPath, IProgress<CloudTransferProgress>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        var length = (await ListAsync(remote, Parent(remotePath), ct)).FirstOrDefault(x => x.Name.Equals(NameOf(remotePath), StringComparison.OrdinalIgnoreCase))?.Size ?? 0;
        await TransferAsync(remote, FileSystem(remote), remotePath, Path.GetDirectoryName(localPath)!, Path.GetFileName(localPath), length, progress, ct);
    }

    private static async Task TransferAsync(string remote, string sourceFs, string sourcePath, string destinationFs, string destinationPath, long total, IProgress<CloudTransferProgress>? progress, CancellationToken ct)
    {
        var client = await App.CurrentApp.RcloneHost.GetClientAsync(ct);
        var group = "cloud-manager-" + Guid.NewGuid().ToString("N");
        var copy = client.CopyFileAsync(sourceFs, sourcePath, destinationFs, destinationPath, ct, group);
        while (!copy.IsCompleted)
        {
            await Task.WhenAny(copy, Task.Delay(350, ct));
            if (!copy.IsCompleted)
            {
                try
                {
                    var stats = await client.GetTransferStatsAsync(group, ct);
                    if (stats is not null) progress?.Report(new(sourcePath, stats.BytesTransferred, total > 0 ? total : stats.TotalBytes, stats.BytesPerSecond));
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or RcloneException) { }
            }
        }
        await copy;
        progress?.Report(new(sourcePath, total, total, 0));
    }

    public static string Join(string directory, string name) => string.IsNullOrWhiteSpace(directory) ? name : directory.Trim('/') + "/" + name;
    public static string Parent(string path) { var index = path.Trim('/').LastIndexOf('/'); return index < 0 ? "" : path.Trim('/')[..index]; }
    private static string FileSystem(string remote) => remote.EndsWith(':') ? remote : remote + ":";
    private static string NameOf(string path) => path.Trim('/').Split('/').Last();
}

public sealed record CloudFileEntry(string Name, string Path, bool IsDirectory, long Size, DateTimeOffset? ModifiedUtc)
{
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
public sealed record CloudTransferProgress(string Path, long CompletedBytes, long TotalBytes, double BytesPerSecond)
{
    public double Percentage => TotalBytes <= 0 ? 0 : Math.Min(100, CompletedBytes * 100d / TotalBytes);
}
