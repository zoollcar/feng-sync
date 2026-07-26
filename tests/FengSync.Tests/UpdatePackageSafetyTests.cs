using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using FengSync.Core.Updates;

namespace FengSync.Tests;

public sealed class UpdatePackageSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FengSync-tests", Guid.NewGuid().ToString("N"));
    public UpdatePackageSafetyTests() => Directory.CreateDirectory(_root);
    [Fact]
    public void Manifest_rejects_unsafe_paths_hashes_and_unsorted_files()
    {
        var m = new ReleaseManifest("FengSync", "0.1.16", "win-x64", [new("z.exe", 1, new string('A', 64)), new("../x.exe", 1, new string('a', 64))]);
        Assert.NotEmpty(ReleaseManifestValidator.Validate(m));
    }
    [Fact]
    public async Task Downloader_uses_fake_http_and_removes_task_on_bad_checksum()
    {
        var bytes = new byte[] { 1, 2, 3 }; var h = new FakeHandler(bytes, new string('0', 64));
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdatePackageDownloader(new HttpClient(h)).DownloadAsync(new("https://example.test/a.zip"), new("https://example.test/a.sha256")));
    }
    [Fact]
    public async Task Downloader_downloads_bytes_reports_progress_and_accepts_matching_sha256()
    {
        var bytes = "portable update"u8.ToArray(); var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var progress = new List<UpdateDownloadProgress>();
        var package = await new UpdatePackageDownloader(new HttpClient(new DownloadHandler(bytes, hash))).DownloadAsync(new("https://example.test/a.zip"), new("https://example.test/a.sha256"), new InlineProgress(progress));
        try { Assert.Equal(bytes, await File.ReadAllBytesAsync(package.ZipPath)); Assert.Equal(hash, UpdatePackageDownloader.ParseChecksum(await File.ReadAllTextAsync(package.Sha256Path))); Assert.NotEmpty(progress); }
        finally { Directory.Delete(package.TaskDirectory, true); }
    }
    [Fact]
    public async Task Downloader_honors_cancellation_and_rejects_declared_or_streamed_size_over_limit()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UpdatePackageDownloader(new HttpClient(new DownloadHandler([1], new string('a', 64)))).DownloadAsync(new("https://example.test/a.zip"), new("https://example.test/a.sha256"), cancellationToken: cancelled.Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdatePackageDownloader(new HttpClient(new DownloadHandler([1], new string('a', 64), UpdatePackageDownloader.MaximumPackageBytes + 1))).DownloadAsync(new("https://example.test/a.zip"), new("https://example.test/a.sha256")));
    }
    [Fact]
    public async Task Extractor_rejects_zip_path_traversal()
    {
        var zip = Path.Combine(_root, "bad.zip"); using (var a = ZipFile.Open(zip, ZipArchiveMode.Create)) { await using var w = new StreamWriter(a.CreateEntry("../escape.txt").Open()); await w.WriteAsync("bad"); }
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdatePackageExtractor().ExtractAndValidateAsync(zip, _root, "v0.1.16"));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }
    [Theory]
    [InlineData("C:/escape.txt")]
    [InlineData("/escape.txt")]
    [InlineData("folder/../../escape.txt")]
    public async Task Extractor_rejects_absolute_and_nested_traversal_entries(string unsafeName)
    {
        var zip = Path.Combine(_root, Guid.NewGuid() + ".zip"); using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { await using var writer = new StreamWriter(archive.CreateEntry(unsafeName).Open()); await writer.WriteAsync("bad"); }
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdatePackageExtractor().ExtractAndValidateAsync(zip, _root, "v0.1.16"));
    }
    [Fact]
    public async Task Extractor_validates_manifest_and_payload_in_temp_directory()
    {
        var source = Path.Combine(_root, "source"); Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "FengSync.exe"), "app"); await File.WriteAllTextAsync(Path.Combine(source, "FengSync.Updater.exe"), "updater");
        var files = Directory.GetFiles(source).Select(p => new ReleaseManifestFile(Path.GetFileName(p), new FileInfo(p).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant())).OrderBy(x => x.Path, StringComparer.Ordinal).ToList(); await new ReleaseManifest("FengSync", "0.1.16", "win-x64", files).SaveAsync(Path.Combine(source, "release-manifest.json"));
        var zip = Path.Combine(_root, "good.zip"); ZipFile.CreateFromDirectory(source, zip); var result = await new UpdatePackageExtractor().ExtractAndValidateAsync(zip, _root, "v0.1.16"); Assert.True(File.Exists(Path.Combine(result, "FengSync.exe")));
    }
    [Fact]
    public async Task Extractor_rejects_missing_required_files_multiple_executables_and_unmanifested_program()
    {
        var source = Path.Combine(_root, "invalid-payload"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "FengSync.exe"), "app"); await File.WriteAllTextAsync(Path.Combine(source, "FengSync.Updater.exe"), "updater"); await File.WriteAllTextAsync(Path.Combine(source, "extra.dll"), "unmanifested");
        var manifestFiles = new[] { "FengSync.exe", "FengSync.Updater.exe" }.Select(p => new ReleaseManifestFile(p, new FileInfo(Path.Combine(source, p)).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, p)))).ToLowerInvariant())).OrderBy(x => x.Path, StringComparer.Ordinal).ToList();
        await new ReleaseManifest("FengSync", "0.1.16", "win-x64", manifestFiles).SaveAsync(Path.Combine(source, "release-manifest.json"));
        var zip = Path.Combine(_root, "invalid-program.zip"); ZipFile.CreateFromDirectory(source, zip);
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdatePackageExtractor().ExtractAndValidateAsync(zip, Path.Combine(_root, "task-one"), "v0.1.16"));
        var missing = Path.Combine(_root, "missing.zip"); using (var archive = ZipFile.Open(missing, ZipArchiveMode.Create)) { await using var writer = new StreamWriter(archive.CreateEntry("FengSync.exe").Open()); await writer.WriteAsync("app"); }
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdatePackageExtractor().ExtractAndValidateAsync(missing, Path.Combine(_root, "task-two"), "v0.1.16"));
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
    private sealed class FakeHandler(byte[] zip, string hash) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("sha256") ? hash + "  a.zip" : Convert.ToBase64String(zip)) }); }
    private sealed class DownloadHandler(byte[] bytes, string hash, long? declaredLength = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath.EndsWith("sha256")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(hash + "  a.zip") });
            var content = new ByteArrayContent(bytes); if (declaredLength is not null) content.Headers.ContentLength = declaredLength;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    private sealed class InlineProgress(List<UpdateDownloadProgress> values) : IProgress<UpdateDownloadProgress> { public void Report(UpdateDownloadProgress value) => values.Add(value); }
}
