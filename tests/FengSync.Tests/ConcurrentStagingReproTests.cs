using FengSync.Core;
using FengSync.Core.Execution;

namespace FengSync.Tests;

/// <summary>
/// Reproduces concurrent staging into one flat directory, which is the case where several transfers
/// share a target root and can therefore observe each other's <c>.fengsync.partial</c> files.
/// </summary>
public sealed class ConcurrentStagingReproTests
{
    [Fact]
    public async Task Concurrent_staging_into_one_directory_never_touches_another_files_partial()
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var root = Path.Combine(Path.GetTempPath(), "fengsync-staging-" + Guid.NewGuid().ToString("N"));
            var left = Path.Combine(root, "left");
            var right = Path.Combine(root, "right");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            try
            {
                for (var i = 1; i <= 10; i++) await File.WriteAllTextAsync(Path.Combine(left, $"file-{i:D3}.txt"), $"fixture {i}");
                var source = new LocalEndpoint(left);
                var target = new LocalEndpoint(right);
                var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
                await Parallel.ForEachAsync(Enumerable.Range(1, 10), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (i, token) =>
                {
                    var path = $"file-{i:D3}.txt";
                    try
                    {
                        var staged = await EndpointTransfer.CopyToStagingAsync(source, target, path, null, Guid.NewGuid(),
                            new FileInfo(Path.Combine(left, path)).Length, 1, null, null, 0, token);
                        await EndpointTransfer.PublishAsync(target, staged, path, true, token);
                    }
                    catch (Exception ex) { failures.Add($"{path}: {ex}"); }
                });
                Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures));
                for (var i = 1; i <= 10; i++)
                    Assert.Equal($"fixture {i}", await File.ReadAllTextAsync(Path.Combine(right, $"file-{i:D3}.txt")));
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
