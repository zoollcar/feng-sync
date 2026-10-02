using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FengSync.Core.Execution;

/// <summary>
/// Durable record of the source a staging file was built from. It is what lets a later attempt prove
/// the staged bytes are still a prefix of that same source before continuing them, without having to
/// re-read (or re-download) the bytes it already holds.
/// </summary>
internal sealed record TransferResumeState(string StagingPath, string SourceKey, long SourceSize, DateTimeOffset SourceModifiedUtc);

/// <summary>
/// Per-destination resume state under the application data directory. It deliberately mirrors
/// <c>CloudTransferJournalStore</c>: one small JSON file per destination, written atomically, never
/// holding credentials.
/// <para>
/// It lives in application data rather than beside the staging file so a sync root never gains a
/// second program-owned file next to the user's data. Losing this state costs one restart and can
/// never cost correctness, so every failure here is swallowed.
/// </para>
/// </summary>
internal sealed class TransferResumeStateStore(string? root = null)
{
    /// <summary>How long a state entry may describe a staging file that does not exist yet.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);
    private readonly string _root = root ?? Path.Combine(AppDataPaths.Root, "transfer-resume");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task SaveAsync(IEndpoint target, string path, TransferResumeState state, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_root);
            // Prune before writing, never after: the entry being added describes a staging file that
            // does not exist yet, so judging it in the same pass would delete what was just written.
            PruneOrphans();
            await File.WriteAllTextAsync(FileFor(target, path), JsonSerializer.Serialize(state), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        finally { _gate.Release(); }
    }

    public async Task<TransferResumeState?> LoadAsync(IEndpoint target, string path, CancellationToken ct = default)
    {
        var file = FileFor(target, path);
        if (!File.Exists(file)) return null;
        try { return JsonSerializer.Deserialize<TransferResumeState>(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    public Task RemoveAsync(IEndpoint target, string path, CancellationToken ct = default)
    {
        try { File.Delete(FileFor(target, path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    private string FileFor(IEndpoint target, string path) => Path.Combine(_root, Key(target, path) + ".json");

    /// <summary>Identifies the destination file itself rather than the profile that happens to reference it.</summary>
    internal static string Key(IEndpoint target, string path)
    {
        var identity = $"{target.Profile.Type}|{target.Profile.Root}|{path}".ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
    }

    /// <summary>
    /// A staging file that no longer exists cannot be continued, so its state is dead weight. The
    /// grace period is what makes this safe: state is written before the first byte moves, so for a
    /// moment it legitimately describes a staging file that does not exist yet, and a second transfer
    /// saving at that instant must not treat it as garbage.
    /// </summary>
    private void PruneOrphans()
    {
        var cutoff = DateTime.UtcNow - Grace;
        foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) > cutoff) continue;
                var state = JsonSerializer.Deserialize<TransferResumeState>(File.ReadAllText(file));
                if (state is not null && File.Exists(state.StagingPath)) continue;
                File.Delete(file);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        }
    }
}
