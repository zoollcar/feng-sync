using System.Security.Cryptography;
using FengSync.Core;
using FengSync.Core.Execution;

namespace FengSync.Tests;

/// <summary>
/// Remote→remote copies against the bundled rclone. Such a copy has no Feng Sync staging object: both legs
/// belong to rclone, so the destination name written is the user-visible one and there is no partial file of
/// ours to continue later. These tests pin the properties only real rclone can prove — the object lands at
/// exactly the path asked for, the source survives, and a copy that never started leaves nothing behind.
/// </summary>
public sealed class ServerSideCopyTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-serverside-copy-" + Guid.NewGuid().ToString("N"));
    private string SourceDirectory => Path.Combine(_root, "source");
    private string TargetDirectory => Path.Combine(_root, "target");
    private string LocalDirectory => Path.Combine(_root, "local");
    private RcloneDaemon? _daemon;
    private byte[] _content = [];

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(SourceDirectory);
        Directory.CreateDirectory(TargetDirectory);
        Directory.CreateDirectory(LocalDirectory);
        _content = RandomNumberGenerator.GetBytes(4 * 1024 * 1024);
        await File.WriteAllBytesAsync(SourceObject, _content);
        var config = Path.Combine(_root, "rclone.conf");
        // Two local backends, each behind an alias remote: the bytes genuinely travel from one remote to
        // another across the daemon's RC endpoint, which is the only way to observe what a server-side copy
        // actually writes. Nothing here touches the network or a real provider's credentials.
        await File.WriteAllTextAsync(config,
            $"[source]\ntype = alias\nremote = {SourceDirectory.Replace('\\', '/')}\n" +
            $"[target]\ntype = alias\nremote = {TargetDirectory.Replace('\\', '/')}\n");
        _daemon = await RcloneDaemon.StartAsync(RclonePath(), config);
    }

    public async Task DisposeAsync()
    {
        if (_daemon is not null) await _daemon.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string SourceObject => Path.Combine(SourceDirectory, "big.bin");

    [Fact]
    public async Task A_server_side_copy_lands_at_the_destination_path_it_was_given()
    {
        // The destination of a copy is the user-visible path, not a staging name, and it is composed against
        // the target endpoint's own root exactly once. Both halves are easy to get wrong in a queue that has
        // already computed a destination: a staging name here would leave a file nobody asked for, and a
        // repeated root would bury it under target/team/team/.
        var created = false;
        await EndpointTransfer.CopyServerSideAsync(Source(), Target(), "big.bin", "team/big.bin", Guid.NewGuid(), _content.Length,
            ensureDestinationAsync: async ct =>
            {
                Assert.Empty(Directory.GetFiles(TargetDirectory, "*", SearchOption.AllDirectories));
                await Target().CreateDirectoryAsync("team", ct);
                created = true;
            });

        Assert.True(created);
        Assert.Equal(["target/team/big.bin"], TargetFiles());
        Assert.Equal(Hash(_content), Hash(await File.ReadAllBytesAsync(Path.Combine(TargetDirectory, "team", "big.bin"))));
        // A copy is not a move: the source object is what the user still sees in the other cloud.
        Assert.True(File.Exists(SourceObject));
        Assert.Equal(Hash(_content), Hash(await File.ReadAllBytesAsync(SourceObject)));
        // rclone stages under its own name, never Feng Sync's, so the browser's staging filter stays empty.
        Assert.Empty(await Target().ListTransferTemporaryFilesAsync());
    }

    [Fact]
    public async Task A_retried_server_side_copy_replaces_the_object_at_the_destination()
    {
        // The queue retries a transfer, and a copy stages nothing that a retry would clear first, so a second
        // attempt has to be able to write over the first one's result rather than be refused by the backend.
        await File.WriteAllBytesAsync(Path.Combine(TargetDirectory, "existing.bin"), new byte[16]);

        await EndpointTransfer.CopyServerSideAsync(Source(), Target(), "big.bin", "existing.bin", Guid.NewGuid(), _content.Length);

        Assert.Equal(Hash(_content), Hash(await File.ReadAllBytesAsync(Path.Combine(TargetDirectory, "existing.bin"))));
    }

    [Fact]
    public async Task A_server_side_copy_that_never_started_leaves_no_object_at_the_destination()
    {
        // The reason a copy may skip staging: rclone's own partial object is not a name Feng Sync can
        // continue, so a copy that fails must not leave a truncated file under the destination name.
        await Assert.ThrowsAnyAsync<Exception>(() => EndpointTransfer.CopyServerSideAsync(
            Source(), Target(), "absent.bin", "absent.bin", Guid.NewGuid(), 1024));

        Assert.Empty(TargetFiles());
    }

    [Fact]
    public async Task A_copy_that_reaches_a_local_endpoint_is_refused_before_any_byte_moves()
    {
        // A local endpoint has no rclone side to copy into; Feng Sync's own temp file and atomic publish are
        // what make a local write safe. Accepting a local target here would write the destination name
        // directly, and a cancelled copy would leave a truncated file the user cannot tell from a complete one.
        var local = new LocalEndpoint(LocalDirectory);

        await Assert.ThrowsAnyAsync<NotSupportedException>(() => EndpointTransfer.CopyServerSideAsync(
            Source(), local, "big.bin", "big.bin", Guid.NewGuid(), _content.Length));
        await Assert.ThrowsAnyAsync<NotSupportedException>(() => EndpointTransfer.CopyServerSideAsync(
            local, Target(), "big.bin", "big.bin", Guid.NewGuid(), _content.Length));

        Assert.Empty(Directory.GetFiles(LocalDirectory, "*", SearchOption.AllDirectories));
        Assert.Empty(TargetFiles());
    }

    /// <summary>Every file under the target directory, named relative to it with forward slashes.</summary>
    private string[] TargetFiles() => Directory
        .GetFiles(TargetDirectory, "*", SearchOption.AllDirectories)
        .Select(file => "target/" + Path.GetRelativePath(TargetDirectory, file).Replace('\\', '/'))
        .OrderBy(file => file, StringComparer.Ordinal)
        .ToArray();

    private RcloneEndpoint Source() => Assert.IsType<RcloneEndpoint>(EndpointFactory.CreateWithClient("sftp://source", _daemon!.Client));
    private RcloneEndpoint Target() => Assert.IsType<RcloneEndpoint>(EndpointFactory.CreateWithClient("sftp://target", _daemon!.Client));

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    private static string RclonePath()
    {
        try { return BundledRclone.ExecutablePath; }
        catch (FileNotFoundException) { }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "FengSync", "Assets", "rclone", "rclone.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("找不到内置 rclone.exe，无法运行云端复制测试。");
    }
}
