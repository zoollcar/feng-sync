using System.IO;
using FengSync.Core;
using FengSync.Core.Rclone.Configuration;
using FengSync.Localization;

namespace FengSync.Services;

/// <summary>What a rename did, so the caller can report it and select the endpoint under its new name.</summary>
public sealed record CloudEndpointRenameResult(string OldName, string NewName, int RewrittenProfiles);

/// <summary>
/// Everything a rename has to check before it is allowed to start, gathered once so the prompt can reject a
/// bad name without another round trip per keystroke.
/// </summary>
/// <param name="RemoteNames">Every remote in rclone.conf, hidden backing stores included: a new name must
/// not collide with one, or the endpoint list would silently hide the other endpoint's storage.</param>
public sealed record CloudEndpointRenameContext(
    IReadOnlyList<string> RemoteNames,
    IReadOnlyList<string> MetadataKeys,
    IReadOnlyList<string> MountedRemotes);

/// <summary>
/// Renames a cloud endpoint. rclone has no rename operation, so the work is a rewrite of the section in
/// rclone.conf plus a migration of the three files that remember the endpoint by name — its metadata, the
/// profiles that sync through it, and the browser's 收藏 and 最近访问 — and all four are backed up first,
/// because a half-applied rename would leave one of them pointing at an endpoint that no longer exists.
/// </summary>
/// <remarks>
/// The conf rewrite is deliberately not a <c>config/create</c> of the new name followed by a
/// <c>config/delete</c> of the old one. Measured against the bundled rclone v1.75.0, <c>config/create</c>
/// for an OAuth backend runs the backend's configuration state machine — <c>oauth-confirm</c>,
/// <c>oauth-islocal</c>, <c>oauth-authorize</c> — so cloning a Drive/OneDrive/Dropbox/Box endpoint would
/// demand a fresh browser authorization instead of copying the token. Editing the file copies every value
/// byte-for-byte, and a running daemon reads the change back immediately (see <see cref="RcloneConfigFile"/>).
/// </remarks>
public static class CloudEndpointRename
{
    /// <summary>
    /// Serialises renames inside this process. rclone.conf has no store-level gate of its own, so two
    /// concurrent renames could read the same content and the later write would drop the earlier one.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Gathers the state <see cref="Validate"/> needs. Split out so a prompt can validate offline.</summary>
    public static async Task<CloudEndpointRenameContext> InspectAsync(
        IReadOnlyCollection<string>? mountedRemotes = null,
        CancellationToken ct = default)
    {
        var metadata = await new CloudEndpointMetadataStore().LoadAsync(ct);
        var remotes = await ConfigurationApi.ListRemoteNamesAsync(ct);
        return new(remotes, [.. metadata.Keys], [.. mountedRemotes ?? []]);
    }

    /// <summary>
    /// The reason a rename cannot start, or <see langword="null"/> when it can. Kept separate from the
    /// rename itself so the name prompt can refuse a bad name inline instead of failing after the fact.
    /// </summary>
    public static string? Validate(string? requested, string oldName, CloudEndpointRenameContext context)
    {
        if (string.IsNullOrWhiteSpace(requested)) return Tr.F("Endpoint.RenameNeedName");
        var newName = CloudEndpointService.SanitizeRemoteName(requested);
        if (newName.Equals(oldName, StringComparison.OrdinalIgnoreCase))
            return Tr.F("Endpoint.RenameSameName");

        // A live mount holds the filesystem string "<old>:", which the daemon resolved when it mounted. It
        // would keep working until the next remount and then point at nothing, so the rename waits instead.
        if (context.MountedRemotes.Contains(oldName, StringComparer.OrdinalIgnoreCase))
            return Tr.Format("Endpoint.RenameStillMounted", oldName);

        if (context.RemoteNames.Contains(newName, StringComparer.OrdinalIgnoreCase) ||
            context.MetadataKeys.Contains(newName, StringComparer.OrdinalIgnoreCase))
            return Tr.Format("Endpoint.RenameNameTaken", newName);

        var backing = CloudEndpointService.BackingRemoteName(newName);
        if (context.RemoteNames.Contains(backing, StringComparer.OrdinalIgnoreCase) ||
            context.MetadataKeys.Contains(backing, StringComparer.OrdinalIgnoreCase))
            return Tr.Format("Endpoint.RenameBackingTaken", backing);

        return null;
    }

    /// <summary>
    /// Repoints every profile that references the endpoint. A profile stores an endpoint URI, so the rename
    /// is a prefix rewrite of the remote name only — the scheme and the path inside the endpoint are kept,
    /// and a profile on a different endpoint, or on a local folder, is returned unchanged.
    /// </summary>
    internal static IReadOnlyList<SyncProfile> RewriteProfiles(
        IReadOnlyList<SyncProfile> profiles,
        string oldName,
        string newName) =>
        profiles.Select(profile =>
        {
            var left = EndpointSchemes.RenameRemote(profile.LeftPath, oldName, newName);
            var right = EndpointSchemes.RenameRemote(profile.RightPath, oldName, newName);
            return left == profile.LeftPath && right == profile.RightPath
                ? profile
                : profile with { LeftPath = left, RightPath = right };
        }).ToList();

    public static async Task<CloudEndpointRenameResult> RenameAsync(
        string oldName,
        string requestedName,
        IReadOnlyCollection<string>? mountedRemotes = null,
        CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var api = ConfigurationApi;
            var metadataStore = new CloudEndpointMetadataStore();
            var metadata = await metadataStore.LoadAsync(ct);
            var context = new CloudEndpointRenameContext(
                await api.ListRemoteNamesAsync(ct), [.. metadata.Keys], [.. mountedRemotes ?? []]);
            if (Validate(requestedName, oldName, context) is { } error) throw new InvalidOperationException(error);
            var newName = CloudEndpointService.SanitizeRemoteName(requestedName);

            // An enforced-prefix endpoint is two remotes and only the alias is the endpoint. Its credentials
            // live in the backing store, so that is what has to be copied — and what has to be verified.
            metadata.TryGetValue(oldName, out var entry);
            var backing = entry?.AliasOf is { Length: > 0 } value ? value : null;
            var source = backing ?? oldName;
            var parameters = await api.GetRemoteParametersAsync(source, ct);
            if (parameters.Count == 0)
                throw new InvalidOperationException(Tr.Format("Endpoint.RenameNotInRclone", oldName));

            var configPath = BundledRclone.ConfigPath;
            var profiles = new ProfileStore();
            var bookmarks = new CloudBrowserBookmarkStore();
            using var backups = new ArtifactBackups();
            backups.Capture(configPath);
            backups.Capture(metadataStore.FilePath);
            backups.Capture(profiles.FilePath);
            backups.Capture(bookmarks.FilePath);
            try
            {
                await RenameInConfigAsync(configPath, api, oldName, newName, backing, entry, parameters, ct);

                if (entry is not null)
                    await metadataStore.RenameAsync(oldName,
                        entry with { RemoteName = newName, AliasOf = backing is null ? null : CloudEndpointService.BackingRemoteName(newName) }, ct);

                // A bookmark is a pointer at an endpoint name, so it has to move with the endpoint: left behind it
                // addresses a section rclone no longer has, and the 收藏 list hides it — the user's favourites
                // emptied by a rename, with no word about why.
                //
                // This comes before the profiles on purpose. Rewriting profiles reads a file this build did not
                // just write, so it is the step that can fail on input nobody controls, and a rename that failed
                // there would otherwise leave the bookmarks moved while everything else stayed where it was.
                await bookmarks.RenameRemoteAsync(oldName, newName, ct);

                var loaded = await profiles.LoadAsync(ct);
                var rewritten = RewriteProfiles(loaded, oldName, newName);
                var changed = rewritten.Where((profile, index) => profile != loaded[index]).Count();
                if (changed > 0) await profiles.SaveAsync(rewritten, ct);

                return new(oldName, newName, changed);
            }
            catch
            {
                // Every artifact goes back together. Restoring only the config would leave a profile pointing
                // at a remote that exists again, but a metadata record under a name rclone no longer has.
                backups.Restore();
                throw;
            }
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Rewrites the section(s) in rclone.conf, then proves through the daemon that the new name is really
    /// there with the same settings. The verification is what makes editing the file safe to rely on: if a
    /// future rclone stops picking the change up, this fails loudly instead of leaving a broken endpoint.
    /// </summary>
    private static async Task RenameInConfigAsync(
        string configPath,
        RcloneConfigurationClient api,
        string oldName,
        string newName,
        string? backing,
        CloudEndpointMetadata? entry,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct)
    {
        var content = File.Exists(configPath) ? await File.ReadAllTextAsync(configPath, ct) : "";
        var newBacking = CloudEndpointService.BackingRemoteName(newName);
        // The alias's own target embeds the backing remote's name, so the alias is repointed in the same
        // pass that renames it. Rewriting only the backing store would leave the endpoint reading the store
        // it was renamed away from.
        var updated = backing is null
            ? RcloneConfigFile.RenameSection(content, oldName, newName)
            : RcloneConfigFile.RenameSection(content, oldName, newName,
                new Dictionary<string, string> { ["remote"] = CloudEndpointService.AliasTarget(newBacking, entry!.Prefix) });
        if (backing is not null) updated = RcloneConfigFile.RenameSection(updated, backing, newBacking);
        await WriteAtomicAsync(configPath, updated, ct);

        var copied = await api.GetRemoteParametersAsync(backing is null ? newName : newBacking, ct);
        if (!SameParameters(parameters, copied))
            throw new InvalidOperationException(Tr.Format("Endpoint.RenameConfigMismatch", newName));

        var names = await api.ListRemoteNamesAsync(ct);
        if (!names.Contains(newName, StringComparer.OrdinalIgnoreCase) ||
            names.Contains(oldName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(Tr.Format("Endpoint.RenameNotReadable", newName));

        if (backing is not null)
        {
            var expected = CloudEndpointService.AliasTarget(newBacking, entry!.Prefix);
            var alias = await api.GetRemoteParametersAsync(newName, ct);
            if (!alias.TryGetValue("remote", out var target) || !target.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    Tr.Format("Endpoint.RenameAliasTargetWrong", newName, expected, target ?? Tr.F("Common.None")));
        }
    }

    private static bool SameParameters(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual) =>
        expected.Count == actual.Count &&
        expected.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, ct);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static RcloneConfigurationClient ConfigurationApi => new(AppServices.Current.RcloneLifecycle);

    /// <summary>
    /// Copies the files a rename touches, so any failure can put all of them back. A file that did not exist
    /// before is recorded too: restoring then means deleting whatever the failed attempt created.
    /// </summary>
    private sealed class ArtifactBackups : IDisposable
    {
        private readonly List<(string Path, string? Backup)> _entries = [];

        public void Capture(string path)
        {
            if (!File.Exists(path)) { _entries.Add((path, null)); return; }
            var backup = path + "." + Guid.NewGuid().ToString("N") + ".bak";
            File.Copy(path, backup, true);
            _entries.Add((path, backup));
        }

        public void Restore()
        {
            foreach (var (path, backup) in _entries)
            {
                if (backup is not null) File.Copy(backup, path, true);
                else if (File.Exists(path)) File.Delete(path);
            }
        }

        public void Dispose()
        {
            foreach (var (_, backup) in _entries)
                if (backup is not null && File.Exists(backup)) File.Delete(backup);
            _entries.Clear();
        }
    }
}
