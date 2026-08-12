using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FengSync.Core;
using FengSync.Core.Execution;
using Microsoft.Data.Sqlite;

namespace FengSync.Tests;

public sealed class LocalIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-test-" + Guid.NewGuid().ToString("N"));
    private string Left => Path.Combine(_root, "left"); private string Right => Path.Combine(_root, "right"); private string Jobs => Path.Combine(_root, "jobs");
    public Task InitializeAsync() { Directory.CreateDirectory(Left); Directory.CreateDirectory(Right); return Task.CompletedTask; }
    public Task DisposeAsync() { if (Directory.Exists(_root)) Directory.Delete(_root, true); return Task.CompletedTask; }

    [Fact] public async Task Copy_executes_through_a_temporary_file_and_commits_journal()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), "hello"); var l = new LocalEndpoint(Left); var r = new LocalEndpoint(Right);
        var plan = new ThreeWayPlanner().Build(l.Scan(), r.Scan(), null); await ExecuteAsync(plan, l, r, journals: new TaskJournalStore(Jobs));
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(Right, "a.txt"))); Assert.Empty(Directory.EnumerateFiles(Right, "*.partial", SearchOption.AllDirectories)); Assert.Empty(await new TaskJournalStore(Jobs).LoadIncompleteAsync());
    }
    [Fact] public async Task Multiple_files_can_be_copied_with_configured_concurrency()
    {
        for (var i = 0; i < 8; i++) await File.WriteAllTextAsync(Path.Combine(Left, $"file-{i}.txt"), new string((char)('a' + i), 2048));
        var l = new LocalEndpoint(Left); var r = new LocalEndpoint(Right); var plan = new ThreeWayPlanner().Build(l.Scan(), r.Scan(), null);
        await ExecuteAsync(plan, l, r, maxConcurrentCopies: 3);
        Assert.Equal(8, Directory.EnumerateFiles(Right, "*.txt").Count());
    }
    [Fact] public async Task Ten_folders_with_one_file_each_are_compared_and_copied()
    {
        for (var i = 1; i <= 10; i++)
        {
            var folder = Path.Combine(Left, $"folder-{i:D3}"); Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "item.txt"), $"fixture {i}");
        }
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right);
        var plan = new ThreeWayPlanner().Build(left.Scan(), right.Scan(), null);
        await ExecuteAsync(plan, left, right, maxConcurrentCopies: 8);
        Assert.Equal(10, Directory.EnumerateFiles(Right, "item.txt", SearchOption.AllDirectories).Count());
        Assert.Equal("fixture 10", await File.ReadAllTextAsync(Path.Combine(Right, "folder-010", "item.txt")));
    }
    [Fact] public async Task Ten_flat_files_are_compared_and_copied()
    {
        for (var i = 1; i <= 10; i++) await File.WriteAllTextAsync(Path.Combine(Left, $"file-{i:D3}.txt"), $"fixture {i}");
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right);
        var plan = new ThreeWayPlanner().Build(left.Scan(), right.Scan(), null);
        await ExecuteAsync(plan, left, right, maxConcurrentCopies: 8);
        Assert.Equal(10, Directory.EnumerateFiles(Right, "*.txt").Count());
        Assert.Equal("fixture 10", await File.ReadAllTextAsync(Path.Combine(Right, "file-010.txt")));
    }
    [Fact] public void Scanner_excludes_internal_database_and_partial_files()
    {
        File.WriteAllText(Path.Combine(Left, "sync.fengdb"), "x"); File.WriteAllText(Path.Combine(Left, "a.fengsync-x.partial"), "x"); File.WriteAllText(Path.Combine(Left, "real.txt"), "x");
        Assert.Single(new LocalEndpoint(Left).Scan());
    }
    [Fact] public async Task Paired_endpoint_archive_uses_opposite_fragments_and_survives_side_swap()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(Right, "a.txt"), "same");
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right); var store = new EndpointBaselineStore();
        await store.CommitAsync(left, right);
        Assert.NotEqual(await File.ReadAllBytesAsync(Path.Combine(Left, "sync.fengdb")), await File.ReadAllBytesAsync(Path.Combine(Right, "sync.fengdb")));
        Assert.Single((await store.LoadAsync(left, right))!);
        Assert.Single((await store.LoadAsync(right, left))!);
    }
    [Fact] public async Task Lone_paired_archive_is_not_a_deletion_baseline()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(Right, "a.txt"), "same");
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right); var store = new EndpointBaselineStore();
        await store.CommitAsync(left, right); File.Delete(Path.Combine(Right, "sync.fengdb"));
        Assert.Null(await store.LoadAsync(left, right));
    }
    [Fact] public async Task Invalid_legacy_state_is_reported_as_untrusted_and_rebuilt_on_commit()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(Right, "a.txt"), "same");
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right);
        await File.WriteAllTextAsync(Path.Combine(Left, "sync.fengdb"), "legacy state");
        await File.WriteAllTextAsync(Path.Combine(Right, "sync.fengdb"), "legacy state");
        var store = new EndpointBaselineStore();
        Assert.Null(await store.LoadAsync(left, right));
        Assert.NotNull(store.LastLoadWarning);
        await store.CommitAsync(left, right);
        Assert.Single((await store.LoadAsync(left, right))!);
    }

    [Fact] public async Task Repeated_v3_commit_does_not_rewrite_state_files()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(Right, "a.txt"), "same");
        var store = new EndpointBaselineStore(); var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right);
        Assert.Equal(BaselineCommitStatus.Updated, (await store.CommitAsync(left, right)).Status);
        var leftBefore = await File.ReadAllBytesAsync(Path.Combine(Left, "sync.fengdb"));
        var rightBefore = await File.ReadAllBytesAsync(Path.Combine(Right, "sync.fengdb"));

        Assert.Equal(BaselineCommitStatus.Unchanged, (await store.CommitAsync(left, right)).Status);

        Assert.Equal(leftBefore, await File.ReadAllBytesAsync(Path.Combine(Left, "sync.fengdb")));
        Assert.Equal(rightBefore, await File.ReadAllBytesAsync(Path.Combine(Right, "sync.fengdb")));
    }

    [Fact] public async Task Stream_v2_is_loaded_and_upgraded_to_v3_on_successful_commit()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(Right, "a.txt"), "same");
        var entries = new[] { Entry("a.txt", 4) };
        await WriteLegacyPairAsync(entries, 2);
        var store = new EndpointBaselineStore(); var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right);

        var loaded = await store.LoadDetailedAsync(left, right);
        Assert.Equal(BaselineLoadStatus.Legacy, loaded.Status);
        Assert.True(loaded.CanPropagateDeletes);
        Assert.Single(loaded.Entries!);
        var changed = new EntrySnapshot("a.txt", EntryKind.File, new(6, DateTimeOffset.UnixEpoch.AddMinutes(1), null));
        var currentRight = loaded.Entries![0].Right!;
        var v2Plan = new ThreeWayPlanner().Build([changed], [currentRight], loaded.Entries);

        var migrationSnapshot = await SnapshotAsync(loaded.Entries.Select(x => x.Left!).ToList(), loaded.Entries.Select(x => x.Right!).ToList());
        Assert.Equal(BaselineCommitStatus.Updated, (await store.CommitFromSnapshotAsync(left, right, migrationSnapshot)).Status);
        var upgraded = await store.LoadDetailedAsync(left, right);
        Assert.Equal(BaselineLoadStatus.Available, upgraded.Status);
        Assert.Equal(3, upgraded.StreamVersion);
        var v3Plan = new ThreeWayPlanner().Build([changed], [currentRight], upgraded.Entries);
        Assert.Equal(v2Plan.Operations.Select(x => (x.Path, x.Kind)), v3Plan.Operations.Select(x => (x.Path, x.Kind)));
    }

    [Fact] public async Task Stream_v1_is_explicitly_unsupported_and_never_authorizes_deletes()
    {
        var entries = new[] { Entry("a.txt", 4) };
        await WriteLegacyPairAsync(entries, 1);
        var result = await new EndpointBaselineStore().LoadDetailedAsync(new LocalEndpoint(Left), new LocalEndpoint(Right));
        Assert.Equal(BaselineLoadStatus.UnsupportedVersion, result.Status);
        Assert.Equal(1, result.StreamVersion);
        Assert.False(result.CanPropagateDeletes);
        Assert.Null(result.Entries);
    }

    [Fact] public async Task V3_round_trips_hash_and_identity_fields()
    {
        var modified = new DateTimeOffset(2026, 8, 12, 3, 4, 5, TimeSpan.Zero);
        var snapshot = new EntrySnapshot("a.txt", EntryKind.File, new(7, modified, "A1B2"),
            new("stable", new(FengSync.Core.Scanning.HashAlgorithmId.Sha256, "C3D4"), "provider"));
        var comparison = await SnapshotAsync([snapshot], [snapshot]);
        var store = new EndpointBaselineStore();
        await store.CommitFromSnapshotAsync(new LocalEndpoint(Left), new LocalEndpoint(Right), comparison);

        var loaded = Assert.Single((await store.LoadAsync(new LocalEndpoint(Left), new LocalEndpoint(Right)))!);
        Assert.Equal(snapshot, loaded.Left);
        Assert.Equal(snapshot, loaded.Right);
    }

    [Fact] public async Task V3_payload_is_at_least_twenty_percent_smaller_than_v2_for_large_baseline()
    {
        var entries = Enumerable.Range(0, 5_000).Select(i =>
        {
            var path = $"folder-{i / 100:D3}/document-{i:D5}.txt";
            var snapshot = new EntrySnapshot(path, EntryKind.File,
                new(i * 17L, DateTimeOffset.UnixEpoch.AddSeconds(i), i % 3 == 0 ? $"HASH{i:X8}" : null));
            return new BaselineEntry(path, snapshot, snapshot);
        }).ToList();
        var v2Size = EncodeV2(entries).Length;
        var comparison = await SnapshotAsync(entries.Select(x => x.Left!).ToList(), entries.Select(x => x.Right!).ToList());
        await new EndpointBaselineStore().CommitFromSnapshotAsync(new LocalEndpoint(Left), new LocalEndpoint(Right), comparison);
        var v3Size = await ReadLatestPayloadSizeAsync(Path.Combine(Left, "sync.fengdb"));
        Assert.True(v3Size <= v2Size * 0.8, $"Expected v3 <= 80% of v2; v2={v2Size}, v3={v3Size}.");
    }

    [Fact] public async Task One_sided_publish_failure_keeps_previous_common_session_as_deletion_baseline()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), "old");
        await File.WriteAllTextAsync(Path.Combine(Right, "a.txt"), "old");
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right); var store = new EndpointBaselineStore();
        await store.CommitAsync(left, right);
        await File.WriteAllTextAsync(Path.Combine(Left, "b.txt"), "new");
        await File.WriteAllTextAsync(Path.Combine(Right, "b.txt"), "new");

        await Assert.ThrowsAsync<IOException>(() => store.CommitAsync(left, new FailingStateEndpoint(right)));

        var recovered = await store.LoadDetailedAsync(left, right);
        Assert.True(recovered.CanPropagateDeletes);
        Assert.Single(recovered.Entries!);
        Assert.Equal("a.txt", recovered.Entries![0].Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_file_and_directory_moves_are_persisted_in_v3_baseline(bool directoryMove)
    {
        var oldPath = directoryMove ? "old-dir/item.txt" : "old.txt";
        var newPath = directoryMove ? "new-dir/item.txt" : "new.txt";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Left, oldPath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Right, oldPath))!);
        await File.WriteAllTextAsync(Path.Combine(Left, oldPath), "move-data");
        await File.WriteAllTextAsync(Path.Combine(Right, oldPath), "move-data");
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right); var repository = new BaselineRepository();
        await new EndpointBaselineStore().CommitAsync(left, right);
        if (directoryMove) Directory.Move(Path.Combine(Left, "old-dir"), Path.Combine(Left, "new-dir"));
        else File.Move(Path.Combine(Left, oldPath), Path.Combine(Left, newPath));

        var baseline = await repository.LoadAsync(left, right);
        var comparison = await new FengSync.Core.Scanning.ComparisonSnapshotBuilder().CaptureAsync(left, right, baseline: baseline);
        var operations = new List<SyncOperation>();
        if (directoryMove)
        {
            operations.Add(new("new-dir", OperationKind.CreateRightDirectory, "directory move"));
            operations.Add(new("old-dir", OperationKind.DeleteRight, "directory move"));
        }
        operations.Add(new(newPath, OperationKind.Move, "move", move: new(
            EndpointSide.Left, EndpointSide.Right, oldPath, newPath, EntryKind.File,
            IdentityEvidenceKind.WeakFingerprint, MoveConfidence.High,
            EndpointMoveExecution.NativeRename, MoveFallback.CrossEndpointCopyDelete)));
        var plan = new SyncPlan(operations); comparison.Plan = plan;
        var run = await new SyncExecutorV2().ExecuteAsync(PlanSnapshot.FromComparison(plan, comparison), left, right);
        Assert.True(run.Succeeded, string.Join(Environment.NewLine, run.Operations.Select(x => x.Error)));

        var transaction = repository.Begin(left, right);
        await repository.CommitFromResultsAsync(left, right,
            new BaselineCommitInput(comparison, run.Operations.ToDictionary(x => x.OperationId), transaction));
        var persisted = (await repository.LoadDetailedAsync(left, right)).Entries!;
        Assert.DoesNotContain(persisted, x => x.Path == oldPath);
        Assert.Contains(persisted, x => x.Path == newPath && x.Left is not null && x.Right is not null);
        if (directoryMove) Assert.Contains(persisted, x => x.Path == "new-dir" && x.Left?.Kind == EntryKind.Directory && x.Right?.Kind == EntryKind.Directory);
    }

    private static BaselineEntry Entry(string path, long size)
    {
        var snapshot = new EntrySnapshot(path, EntryKind.File, new(size, DateTimeOffset.UnixEpoch, null));
        return new(path, snapshot, snapshot);
    }

    private async Task WriteLegacyPairAsync(IReadOnlyList<BaselineEntry> entries, int streamVersion)
    {
        var payload = EncodeV2(entries); var split = payload.Length / 2; var id = Guid.NewGuid(); var created = DateTimeOffset.UtcNow;
        await WriteLegacyArchiveAsync(Path.Combine(Left, "sync.fengdb"), id, SessionRole.Lead, streamVersion, payload, payload[..split], created);
        await WriteLegacyArchiveAsync(Path.Combine(Right, "sync.fengdb"), id, SessionRole.Follower, streamVersion, payload, payload[split..], created);
    }

    private static async Task WriteLegacyArchiveAsync(string path, Guid id, SessionRole role, int streamVersion, byte[] payload, byte[] fragment, DateTimeOffset created)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False"); await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE database_meta(format_magic TEXT NOT NULL, database_version INTEGER NOT NULL, stream_version INTEGER NOT NULL, created_utc TEXT NOT NULL); CREATE TABLE sessions(session_id TEXT NOT NULL, role INTEGER NOT NULL, stream_version INTEGER NOT NULL, payload_size INTEGER NOT NULL, payload_sha256 TEXT NOT NULL, fragment_sha256 TEXT NOT NULL, fragment_blob BLOB NOT NULL, created_utc TEXT NOT NULL, PRIMARY KEY(session_id, role)); INSERT INTO database_meta VALUES('FengSync',2,$stream,$created); INSERT INTO sessions VALUES($id,$role,$stream,$size,$payloadHash,$fragmentHash,$blob,$created);";
        command.Parameters.AddWithValue("$id", id.ToString("N")); command.Parameters.AddWithValue("$role", (int)role); command.Parameters.AddWithValue("$stream", streamVersion);
        command.Parameters.AddWithValue("$size", payload.Length); command.Parameters.AddWithValue("$payloadHash", Convert.ToHexString(SHA256.HashData(payload)));
        command.Parameters.AddWithValue("$fragmentHash", Convert.ToHexString(SHA256.HashData(fragment))); command.Parameters.AddWithValue("$blob", fragment);
        command.Parameters.AddWithValue("$created", created.ToString("O")); await command.ExecuteNonQueryAsync();
    }

    private static byte[] EncodeV2(IReadOnlyList<BaselineEntry> entries)
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(entries);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(raw);
        return output.ToArray();
    }

    private static async Task<long> ReadLatestPayloadSizeAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT payload_size FROM sessions WHERE stream_version=3 ORDER BY created_utc DESC LIMIT 1";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static Task<FengSync.Core.Scanning.ComparisonSnapshot> SnapshotAsync(IReadOnlyList<EntrySnapshot> left, IReadOnlyList<EntrySnapshot> right)
    {
        var paths = new EndpointPathSemantics(false, System.Text.NormalizationForm.FormC);
        var leftProfile = new EndpointProfile(Guid.NewGuid(), EndpointType.Local, "left");
        var rightProfile = new EndpointProfile(Guid.NewGuid(), EndpointType.Local, "right");
        return Task.FromResult(new FengSync.Core.Scanning.ComparisonSnapshot
        {
            SnapshotId = Guid.NewGuid(), Mode = FengSync.Core.Scanning.ComparisonMode.TimeAndSize, TimeTolerance = TimeSpan.Zero,
            Left = new() { Endpoint = leftProfile, Paths = paths, StartedUtc = DateTimeOffset.UtcNow, CompletedUtc = DateTimeOffset.UtcNow, Entries = left, ByPath = left.ToDictionary(x => x.Path) },
            Right = new() { Endpoint = rightProfile, Paths = paths, StartedUtc = DateTimeOffset.UtcNow, CompletedUtc = DateTimeOffset.UtcNow, Entries = right, ByPath = right.ToDictionary(x => x.Path) }
        });
    }

    private sealed class FailingStateEndpoint(LocalEndpoint inner) : IEndpoint, IEndpointStateStorage
    {
        public EndpointProfile Profile => inner.Profile;
        public EndpointCapabilities Capabilities => inner.Capabilities;
        public Task<IReadOnlyList<EntrySnapshot>> ScanAsync(CancellationToken cancellationToken = default) => inner.ScanAsync(cancellationToken);
        public IAsyncEnumerable<EntrySnapshot> ScanEntriesAsync(CancellationToken cancellationToken = default) => inner.ScanEntriesAsync(cancellationToken);
        public Task CopyToAsync(string relativePath, IEndpoint target, string temporaryPath, CancellationToken cancellationToken = default) => inner.CopyToAsync(relativePath, target, temporaryPath, cancellationToken);
        public Task MoveAsync(string from, string to, CancellationToken cancellationToken = default) => inner.MoveAsync(from, to, cancellationToken);
        public Task MoveDirectoryAsync(string from, string to, CancellationToken cancellationToken = default) => inner.MoveDirectoryAsync(from, to, cancellationToken);
        public Task DeleteAsync(string relativePath, bool directory, CancellationToken cancellationToken = default) => inner.DeleteAsync(relativePath, directory, cancellationToken);
        public Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default) => inner.CreateDirectoryAsync(relativePath, cancellationToken);
        public Task<EntrySnapshot?> StatAsync(string relativePath, CancellationToken cancellationToken = default) => inner.StatAsync(relativePath, cancellationToken);
        public Task<string?> DownloadStateAsync(string relativePath, string localDirectory, CancellationToken cancellationToken = default) => inner.DownloadStateAsync(relativePath, localDirectory, cancellationToken);
        public Task UploadAndPublishStateAsync(string localPath, string temporaryRelativePath, CancellationToken cancellationToken = default) => Task.FromException(new IOException("simulated state publish failure"));
    }

    private static async Task ExecuteAsync(SyncPlan plan, LocalEndpoint left, LocalEndpoint right,
        TaskJournalStore? journals = null, int maxConcurrentCopies = 3)
    {
        var result = await new SyncExecutorV2().ExecuteAsync(
            await PlanSnapshot.CaptureAsync(plan, left, right), left, right,
            journals: journals, maxConcurrentCopies: maxConcurrentCopies);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine,
            result.Operations.Where(x => x.Error is not null).Select(x => $"{x.Path}: {x.Error}")));
    }
}
