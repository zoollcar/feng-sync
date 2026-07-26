using System.Security.Cryptography;

namespace FengSync.Core.Updates;

public sealed record UpdateDownloadProgress(long ReceivedBytes, long? TotalBytes) { public double? Percentage => TotalBytes is > 0 ? ReceivedBytes * 100d / TotalBytes : null; }
public sealed record DownloadedUpdatePackage(string TaskDirectory, string ZipPath, string Sha256Path);

public sealed class UpdatePackageDownloader
{
    public const long MaximumPackageBytes = 512L * 1024 * 1024;
    private readonly HttpClient _http;
    public UpdatePackageDownloader(HttpClient http) => _http = http;
    public async Task<DownloadedUpdatePackage> DownloadAsync(Uri zipUri, Uri shaUri, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var task = Path.Combine(Path.GetTempPath(), "FengSync", "updates", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(task);
        var partial = Path.Combine(task, "package.zip.download"); var zip = Path.Combine(task, "package.zip"); var sha = Path.Combine(task, "package.zip.sha256");
        try
        {
            using var response = await _http.GetAsync(zipUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken); response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength; if (total > MaximumPackageBytes) throw new InvalidDataException("更新包超过 512 MB 上限。");
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920]; long received = 0; int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0) { received += read; if (received > MaximumPackageBytes) throw new InvalidDataException("更新包超过 512 MB 上限。"); await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken); progress?.Report(new(received, total)); }
                await output.FlushAsync(cancellationToken);
            }
            File.Move(partial, zip);
            using var shaResponse = await _http.GetAsync(shaUri, cancellationToken); shaResponse.EnsureSuccessStatusCode();
            await File.WriteAllTextAsync(sha, await shaResponse.Content.ReadAsStringAsync(cancellationToken), cancellationToken);
            var expected = ParseChecksum(await File.ReadAllTextAsync(sha, cancellationToken));
            await using var zipStream = File.OpenRead(zip);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(zipStream, cancellationToken)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual))) throw new InvalidDataException("更新包 SHA-256 校验失败。");
            return new(task, zip, sha);
        }
        catch { try { Directory.Delete(task, true); } catch { } throw; }
    }
    public static string ParseChecksum(string text) { var hash = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ""; return hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F') ? hash.ToLowerInvariant() : throw new InvalidDataException("SHA-256 文件格式无效。"); }
}
