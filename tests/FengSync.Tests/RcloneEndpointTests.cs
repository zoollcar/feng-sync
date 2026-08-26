using FengSync.Core;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FengSync.Core.Execution;

namespace FengSync.Tests;

public sealed class RcloneEndpointTests : IDisposable
{
    private readonly RecordingHandler _handler = new();
    private readonly HttpClient _http;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-rclone-" + Guid.NewGuid().ToString("N"));
    public RcloneEndpointTests() { _http = new HttpClient(_handler) { BaseAddress = new Uri("http://rc.test/") }; Directory.CreateDirectory(_root); }
    public void Dispose() { _http.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task Remote_scan_excludes_internal_files_and_reads_hash()
    {
        _handler.ListJson = "{\"list\":[{\"Path\":\"sync.fengdb\",\"IsDir\":false},{\"Path\":\"folder/a.txt\",\"IsDir\":false,\"Size\":4,\"ModTime\":\"2026-01-01T00:00:00Z\",\"Hashes\":{\"md5\":\"ABCD\"}},{\"Path\":\"empty\",\"IsDir\":true}]}";
        var endpoint = Remote(EndpointType.Sftp);
        var items = await endpoint.ScanAsync();
        Assert.Equal(2, items.Count); Assert.Equal("ABCD", items.Single(x => x.Path == "folder/a.txt").Fingerprint!.Hash); Assert.Contains(_handler.Requests, x => x.AbsolutePath.EndsWith("operations/list"));
    }

    [Fact]
    public async Task Remote_scan_normalizes_a_listing_that_includes_its_configured_root()
    {
        _handler.ListJson = "{\"list\":[{\"Path\":\"root/from-drive.txt\",\"IsDir\":false,\"Size\":5,\"ModTime\":\"2026-01-01T00:00:00Z\"}]}";

        var item = Assert.Single(await Remote(EndpointType.GoogleDrive).ScanAsync());

        Assert.Equal("from-drive.txt", item.Path);
    }

    [Fact]
    public async Task Directory_listing_includes_empty_and_implicit_parent_folders()
    {
        _handler.ListJson = "{\"list\":[{\"Path\":\"empty\",\"IsDir\":true},{\"Path\":\"one/two/file.txt\",\"IsDir\":false}]}";
        var client = new RcloneRcClient(_http, _http.BaseAddress!, "user", "pass");
        var directories = await client.ListDirectoriesAsync("remote", "", false);
        Assert.Equal(["empty", "one", "one/two"], directories);
        Assert.Contains(_handler.Bodies, x => x.Contains("\"recurse\":false", StringComparison.Ordinal));
        var tree = RemoteDirectoryTree.Build(directories);
        Assert.Equal(["empty", "one"], tree.Children.Select(x => x.Name)); Assert.Equal("one/two", Assert.Single(tree.Children.Single(x => x.Name == "one").Children).Path);
    }

    [Fact]
    public async Task File_manager_directory_listing_is_non_recursive_and_preserves_metadata()
    {
        _handler.ListJson = "{\"list\":[{\"Path\":\"report.xlsx\",\"IsDir\":false,\"Size\":42,\"ModTime\":\"2026-01-01T00:00:00Z\"},{\"Path\":\"archive\",\"IsDir\":true}]}";
        var client = new RcloneRcClient(_http, _http.BaseAddress!, "user", "pass");

        var entries = await client.ListDirectoryAsync("remote:", "documents");

        Assert.Equal(2, entries.Count);
        Assert.Equal("report.xlsx", entries[0].Path); Assert.Equal(42, entries[0].Size); Assert.False(entries[0].IsDirectory);
        Assert.True(entries[1].IsDirectory);
        Assert.Contains(_handler.Bodies, x => x.Contains("\"remote\":\"documents\"", StringComparison.Ordinal) && x.Contains("\"recurse\":false", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("cloudfile/入职", "cloudfile", "入职")]
    [InlineData("入职", "cloudfile", "入职")]
    [InlineData("cloudfile/contacts/a", "cloudfile", "contacts/a")]
    public void Directory_paths_are_not_prefixed_twice(string listed, string root, string expected) => Assert.Equal(expected, RemoteDirectoryTree.RelativeToListingRoot(listed, root));

    [Theory]
    [InlineData(EndpointType.Sftp)]
    [InlineData(EndpointType.GoogleDrive)]
    [InlineData(EndpointType.S3)]
    public async Task Local_to_remote_uses_the_same_safe_copy_then_move_protocol(EndpointType type)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "local.txt"), "content");
        var local = new LocalEndpoint(_root); var remote = Remote(type);
        var plan = new SyncPlan([new SyncOperation("local.txt", OperationKind.CopyLeftToRight, "test")]);
        var result = await new SyncExecutorV2().ExecuteAsync(await PlanSnapshot.CaptureAsync(plan, local, remote), local, remote);
        AssertSucceeded(result, _handler);
        Assert.Contains(_handler.Requests, x => x.AbsolutePath.EndsWith("operations/copyfile")); Assert.Contains(_handler.Requests, x => x.AbsolutePath.EndsWith("operations/movefile"));
        Assert.Contains(_handler.Bodies, x => x.Contains(".fengsync-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Local_to_remote_creates_an_unplanned_parent_directory_before_copying()
    {
        var directory = Path.Combine(_root, "new-parent");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "file.txt"), "content");
        var local = new LocalEndpoint(_root); var remote = Remote(EndpointType.GoogleDrive);
        // This intentionally models a move-derived copy, where a structural
        // directory operation was absent from the original plan.
        var plan = new SyncPlan([new SyncOperation("new-parent/file.txt", OperationKind.CopyLeftToRight, "test")]);

        var result = await new SyncExecutorV2().ExecuteAsync(await PlanSnapshot.CaptureAsync(plan, local, remote), local, remote);

        AssertSucceeded(result, _handler);
        var mkdir = _handler.Requests.FindIndex(x => x.AbsolutePath.EndsWith("operations/mkdir"));
        var copy = _handler.Requests.FindIndex(x => x.AbsolutePath.EndsWith("operations/copyfile"));
        Assert.True(mkdir >= 0 && mkdir < copy);
        Assert.Contains(_handler.Bodies, x => x.Contains("\"remote\":\"root/new-parent\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Local_to_remote_retries_a_parent_directory_after_the_cached_creation_fails()
    {
        var directory = Path.Combine(_root, "new-parent");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "first.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(directory, "second.txt"), "second");
        _handler.FailNextMkdir = true;
        var local = new LocalEndpoint(_root); var remote = Remote(EndpointType.Sftp);
        var first = new SyncOperation("new-parent/first.txt", OperationKind.CopyLeftToRight, "test");
        var second = new SyncOperation("new-parent/second.txt", OperationKind.CopyLeftToRight, "test");
        var plan = new SyncPlan([first, second]);

        var result = await new SyncExecutorV2().ExecuteAsync(
            await PlanSnapshot.CaptureAsync(plan, local, remote), local, remote, maxConcurrentCopies: 1);

        Assert.Equal(TransferStage.Failed, result.Operations.Single(x => x.OperationId == first.OperationId).Stage);
        Assert.Equal(TransferStage.Committed, result.Operations.Single(x => x.OperationId == second.OperationId).Stage);
        Assert.Equal(2, _handler.Requests.Count(x => x.AbsolutePath.EndsWith("operations/mkdir")));
    }

    [Fact]
    public async Task Local_to_remote_uses_rclones_logical_colon_for_a_windows_full_width_colon_filename()
    {
        const string physicalName = "文件：一.doc";
        await File.WriteAllTextAsync(Path.Combine(_root, physicalName), "content");
        var local = new LocalEndpoint(_root); var remote = Remote(EndpointType.GoogleDrive);
        var plan = new SyncPlan([new SyncOperation(physicalName, OperationKind.CopyLeftToRight, "test")]);

        var result = await new SyncExecutorV2().ExecuteAsync(await PlanSnapshot.CaptureAsync(plan, local, remote), local, remote);

        AssertSucceeded(result, _handler);
        Assert.Contains(_handler.Bodies, body =>
        {
            using var request = JsonDocument.Parse(body);
            return request.RootElement.TryGetProperty("srcRemote", out var source) && source.GetString() == "文件:一.doc";
        });
    }

    [Fact]
    public async Task Comparison_keeps_a_local_directory_as_a_visible_create_operation_before_its_file_copy()
    {
        var directory = Path.Combine(_root, "new-parent");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "file.txt"), "content");
        var local = new LocalEndpoint(_root); var remote = Remote(EndpointType.GoogleDrive);

        var plan = new ModePlanner().Build(SyncMode.Update, await local.ScanAsync(), await remote.ScanAsync(),
            leftCapabilities: local.Capabilities, rightCapabilities: remote.Capabilities);

        Assert.Contains(plan.Operations, operation => operation.Path == "new-parent" && operation.Kind == OperationKind.CreateRightDirectory);
        Assert.Contains(plan.Operations, operation => operation.Path == "new-parent/file.txt" && operation.Kind == OperationKind.CopyLeftToRight);
    }

    [Fact]
    public async Task Remote_calls_use_a_colon_qualified_rclone_filesystem()
    {
        _handler.ListJson = "{\"list\":[]}";
        await Remote(EndpointType.GoogleDrive).ScanAsync();
        Assert.Contains(_handler.Bodies, x => x.Contains("\"fs\":\"test:\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rclone_http_timeout_is_reported_as_a_connection_failure_not_cancellation()
    {
        using var http = new HttpClient(new TimeoutHandler()) { BaseAddress = new Uri("http://rc.test/") };
        var client = new RcloneRcClient(http, http.BaseAddress!, "user", "pass");

        var error = await Assert.ThrowsAsync<FengSync.Core.Rclone.Diagnostics.RcloneException>(() => client.CallAsync("operations/list", new { }));

        Assert.Equal(FengSync.Core.Rclone.Diagnostics.RcloneFailureCategory.Temporary, error.Failure.Category);
        Assert.True(error.Failure.Retryable);
        Assert.Equal("operations/list", error.Failure.Operation);
    }

    [Fact]
    public async Task Google_drive_directory_move_uses_one_sync_move_request_with_root_qualified_filesystems()
    {
        await Remote(EndpointType.GoogleDrive).MoveDirectoryAsync("old-dir", "new-dir");

        Assert.Single(_handler.Requests, x => x.AbsolutePath.EndsWith("sync/move"));
        Assert.Contains(_handler.Bodies, x => x.Contains("\"srcFs\":\"test:root/old-dir\"", StringComparison.Ordinal));
        Assert.Contains(_handler.Bodies, x => x.Contains("\"dstFs\":\"test:root/new-dir\"", StringComparison.Ordinal));
        Assert.Contains(_handler.Bodies, x => x.Contains("\"deleteEmptySrcDirs\":true", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(EndpointType.Sftp)]
    [InlineData(EndpointType.GoogleDrive)]
    public async Task Remote_file_move_uses_one_native_move_and_preserves_content_metadata(EndpointType type)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "new.txt"), "contents");
        _handler.Seed("old.txt", 8);
        var local = new LocalEndpoint(_root); var remote = Remote(type);
        var move = new SyncOperation("new.txt", OperationKind.Move, "remote file move", move: new(
            EndpointSide.Left, EndpointSide.Right, "old.txt", "new.txt", EntryKind.File,
            IdentityEvidenceKind.StrongDigest, MoveConfidence.High,
            EndpointMoveExecution.NativeRename, MoveFallback.CrossEndpointCopyDelete));

        var result = await new SyncExecutorV2().ExecuteAsync(await PlanSnapshot.CaptureAsync(new SyncPlan([move]), local, remote), local, remote);

        AssertSucceeded(result, _handler);
        Assert.Single(_handler.Requests, x => x.AbsolutePath.EndsWith("operations/movefile"));
        Assert.False(_handler.Contains("old.txt"));
        Assert.True(_handler.Contains("new.txt"));
    }

    [Theory]
    [InlineData(EndpointType.Sftp)]
    [InlineData(EndpointType.GoogleDrive)]
    public async Task Complete_remote_directory_move_is_aggregated_into_one_native_request(EndpointType type)
    {
        Directory.CreateDirectory(Path.Combine(_root, "new-dir", "sub"));
        await File.WriteAllTextAsync(Path.Combine(_root, "new-dir", "a.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(_root, "new-dir", "sub", "b.txt"), "bravo");
        _handler.Seed("old-dir/a.txt", 5); _handler.Seed("old-dir/sub/b.txt", 5);
        var local = new LocalEndpoint(_root); var remote = Remote(type);
        var plan = new SyncPlan([
            new("new-dir", OperationKind.CreateRightDirectory, "directory move"),
            new("new-dir/sub", OperationKind.CreateRightDirectory, "directory move"),
            new("old-dir/sub", OperationKind.DeleteRight, "directory move"),
            new("old-dir", OperationKind.DeleteRight, "directory move"),
            RemoteMove("old-dir/a.txt", "new-dir/a.txt"),
            RemoteMove("old-dir/sub/b.txt", "new-dir/sub/b.txt")]);
        var paths = remote.Capabilities.EffectivePaths.CreateComparer();
        var leftEntries = (await local.ScanAsync()).ToDictionary(x => x.Path, paths);
        var rightEntries = leftEntries.Values.Select(x => x with { Path = x.Path.Replace("new-dir", "old-dir", StringComparison.Ordinal) }).ToDictionary(x => x.Path, paths);
        // Reuse the comparison data directly: this is both faster and asserts
        // that execution does not need a second remote tree scan.
        var snapshot = new PlanSnapshot(plan, new Dictionary<Guid, Fingerprint?>(), leftEntries, rightEntries);

        var result = await new SyncExecutorV2().ExecuteAsync(snapshot, local, remote);

        AssertSucceeded(result, _handler);
        Assert.Single(_handler.Requests, x => x.AbsolutePath.EndsWith("sync/move"));
        Assert.DoesNotContain(_handler.Requests, x => x.AbsolutePath.EndsWith("operations/movefile"));
        Assert.False(_handler.Contains("old-dir/a.txt"));
        Assert.False(_handler.Contains("old-dir/sub/b.txt"));
        Assert.True(_handler.Contains("new-dir/a.txt"));
        Assert.True(_handler.Contains("new-dir/sub/b.txt"));
    }

    [Fact]
    public async Task Publishing_state_database_uses_rclone_overwrite_without_deleting_the_old_google_drive_file_first()
    {
        _handler.ListJson = "{\"list\":[{\"Path\":\"root/sync.fengdb\",\"IsDir\":false},{\"Path\":\"root/sync.fengdb\",\"IsDir\":false}]}";
        var local = Path.Combine(_root, "state.db");
        await File.WriteAllTextAsync(local, "state");

        await Remote(EndpointType.GoogleDrive).UploadAndPublishStateAsync(local, "sync.fengdb.fengsync-0123456789abcdef0123456789abcdef.tmp");

        Assert.Single(_handler.Requests, x => x.AbsolutePath.EndsWith("operations/copyfile"));
        Assert.DoesNotContain(_handler.Requests, x => x.AbsolutePath.EndsWith("operations/deletefile") || x.AbsolutePath.EndsWith("operations/movefile"));
        Assert.Contains(_handler.Bodies, body => body.Contains("\"dstRemote\":\"root/sync.fengdb\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Publishing_state_database_uses_the_same_safe_overwrite_for_sftp()
    {
        _handler.ListJson = "{\"list\":[{\"Path\":\"root/sync.fengdb\",\"IsDir\":false}]}";
        var local = Path.Combine(_root, "state.db");
        await File.WriteAllTextAsync(local, "state");

        await Remote(EndpointType.Sftp).UploadAndPublishStateAsync(local, "sync.fengdb.fengsync-0123456789abcdef0123456789abcdef.tmp");

        Assert.Single(_handler.Requests, x => x.AbsolutePath.EndsWith("operations/copyfile"));
        Assert.DoesNotContain(_handler.Requests, x => x.AbsolutePath.EndsWith("operations/deletefile") || x.AbsolutePath.EndsWith("operations/movefile"));
    }

    [Fact]
    public async Task Remote_executor_honors_the_configured_copy_concurrency()
    {
        _handler.DelayCopy = true;
        _handler.Seed("one.txt", 3); _handler.Seed("two.txt", 3);
        var source = Remote(EndpointType.Sftp); var target = Remote(EndpointType.Sftp);
        var plan = new SyncPlan([new SyncOperation("one.txt", OperationKind.CopyLeftToRight, "test"), new SyncOperation("two.txt", OperationKind.CopyLeftToRight, "test")]);
        var result = await new SyncExecutorV2().ExecuteAsync(await PlanSnapshot.CaptureAsync(plan, source, target), source, target, maxConcurrentCopies: 2);
        AssertSucceeded(result, _handler);
        Assert.True(_handler.MaximumConcurrentCopies >= 2);
    }

    [Fact]
    public async Task Google_drive_executor_reaches_ten_way_concurrency_for_small_local_files()
    {
        _handler.DelayCopy = true;
        var operations = new List<SyncOperation>();
        for (var i = 1; i <= 10; i++)
        {
            var path = $"small-{i:D2}.txt";
            await File.WriteAllTextAsync(Path.Combine(_root, path), "payload");
            operations.Add(new SyncOperation(path, OperationKind.CopyLeftToRight, "volume"));
        }
        var local = new LocalEndpoint(_root);
        var remote = Remote(EndpointType.GoogleDrive);
        var plan = new SyncPlan(operations);

        var result = await new SyncExecutorV2().ExecuteAsync(
            await PlanSnapshot.CaptureAsync(plan, local, remote),
            local, remote, maxConcurrentCopies: 10);

        AssertSucceeded(result, _handler);
        Assert.Equal(10, _handler.MaximumConcurrentCopies);
    }

    [Theory]
    [InlineData(EndpointType.Sftp, false)]
    [InlineData(EndpointType.Sftp, true)]
    [InlineData(EndpointType.GoogleDrive, false)]
    [InlineData(EndpointType.GoogleDrive, true)]
    public async Task Remote_delete_runs_directly_even_when_an_unrelated_copy_fails(EndpointType type, bool changeDeleteTarget)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "copy.txt"), "source");
        _handler.Seed("delete.txt", 3);
        var local = new LocalEndpoint(_root);
        var remote = Remote(type);
        var copy = new SyncOperation("copy.txt", OperationKind.CopyLeftToRight, "copy");
        var delete = new SyncOperation("delete.txt", OperationKind.DeleteRight, "delete");
        var snapshot = await PlanSnapshot.CaptureAsync(new SyncPlan([copy, delete]), local, remote);

        // The copy target appeared after comparison, so this copy fails without
        // changing the independently planned delete target.
        _handler.Seed("copy.txt", 1);
        if (changeDeleteTarget) _handler.Seed("delete.txt", 9);

        var result = await new SyncExecutorV2().ExecuteAsync(snapshot, local, remote);

        Assert.Equal(TransferStage.Failed, result.Operations.Single(x => x.OperationId == copy.OperationId).Stage);
        var deleteResult = result.Operations.Single(x => x.OperationId == delete.OperationId);
        Assert.Equal(TransferStage.Committed, deleteResult.Stage);
        Assert.True(_handler.HasDeleteFor("delete.txt"));
    }

    private static void AssertSucceeded(SyncRunResult result, RecordingHandler handler)
    {
        var operations = string.Join(Environment.NewLine, result.Operations.Select(x => $"path={x.Path}; kind={x.Kind}; stage={x.Stage}; published={x.Published}; error={x.Error ?? "<none>"}"));
        Assert.True(result.Succeeded, $"V2 synchronization failed.{Environment.NewLine}Operations:{Environment.NewLine}{operations}{Environment.NewLine}RC state:{Environment.NewLine}{handler.DiagnosticState}");
    }

    private static SyncOperation RemoteMove(string from, string to) => new(to, OperationKind.Move, "directory move", move: new(
        EndpointSide.Left, EndpointSide.Right, from, to, EntryKind.File,
        IdentityEvidenceKind.StrongDigest, MoveConfidence.High,
        EndpointMoveExecution.NativeRename, MoveFallback.CrossEndpointCopyDelete));

    private RcloneEndpoint Remote(EndpointType type) => new(new RcloneRcClient(_http, _http.BaseAddress!, "user", "pass"), new EndpointProfile(Guid.NewGuid(), type, "root", "test"), new(false, true, true, TimeSpan.FromSeconds(1)));
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string ListJson { get; set; } = "{\"list\":[]}";
        public List<Uri> Requests { get; } = []; public List<string> Bodies { get; } = [];
        public bool FailNextMkdir { get; set; }
        public bool DelayCopy { get; set; } public int MaximumConcurrentCopies { get; private set; } private int _concurrentCopies;
        private readonly object _gate = new();
        private readonly Dictionary<string, (long Size, DateTimeOffset Modified)> _objects = new(StringComparer.Ordinal);
        public string DiagnosticState
        {
            get { lock (_gate) return $"objects=[{string.Join(", ", _objects.Select(x => $"{x.Key}:{x.Value.Size}"))}]{Environment.NewLine}requests={string.Join(" | ", Requests.Select(x => x.AbsolutePath))}{Environment.NewLine}bodies={string.Join(" | ", Bodies)}"; }
        }
        public void Seed(string path, long size)
        {
            lock (_gate) _objects[path] = (size, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        }
        public bool HasDeleteFor(string path)
        {
            lock (_gate)
                return Requests.Zip(Bodies).Any(x => x.First.AbsolutePath.EndsWith("operations/deletefile") &&
                    x.Second.Contains($"\"remote\":\"root/{path}\"", StringComparison.Ordinal));
        }
        public bool Contains(string path) { lock (_gate) return _objects.ContainsKey(path); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_gate) { Requests.Add(request.RequestUri!); Bodies.Add(body); }
            var operation = request.RequestUri!.AbsolutePath;
            if (DelayCopy && operation.EndsWith("operations/copyfile"))
            {
                var current = Interlocked.Increment(ref _concurrentCopies);
                lock (_gate) MaximumConcurrentCopies = Math.Max(MaximumConcurrentCopies, current);
                await Task.Delay(80, cancellationToken);
                Interlocked.Decrement(ref _concurrentCopies);
            }
            if (FailNextMkdir && operation.EndsWith("operations/mkdir"))
            {
                FailNextMkdir = false;
                return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":\"temporary mkdir failure\"}", Encoding.UTF8, "application/json") };
            }
            var payload = operation.EndsWith("operations/list") ? List(body) : Apply(operation, body);
            return new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        }

        private string List(string body)
        {
            lock (_gate)
            {
                if (_objects.Count == 0) return ListJson;
                using var request = JsonDocument.Parse(body);
                var remote = request.RootElement.GetProperty("remote").GetString() ?? "";
                var prefix = remote.Trim('/');
                if (prefix == "root") prefix = "";
                else if (prefix.StartsWith("root/", StringComparison.Ordinal)) prefix = prefix[5..];
                var values = _objects.Where(x => string.IsNullOrEmpty(prefix) || x.Key.StartsWith(prefix + "/", StringComparison.Ordinal) || x.Key == prefix)
                    .Select(x => new { Path = x.Key, Name = x.Key[(x.Key.LastIndexOf('/') + 1)..], IsDir = false, Size = x.Value.Size, ModTime = x.Value.Modified.ToString("O") });
                return JsonSerializer.Serialize(new { list = values });
            }
        }

        private string Apply(string operation, string body)
        {
            if (operation.EndsWith("sync/move"))
            {
                using var moveRequest = JsonDocument.Parse(body);
                var sourceDirectory = RelativeFileSystem(moveRequest.RootElement.GetProperty("srcFs").GetString() ?? "");
                var destinationDirectory = RelativeFileSystem(moveRequest.RootElement.GetProperty("dstFs").GetString() ?? "");
                lock (_gate)
                {
                    var moved = _objects.Where(x => x.Key == sourceDirectory || x.Key.StartsWith(sourceDirectory + "/", StringComparison.Ordinal)).ToList();
                    foreach (var item in moved)
                    {
                        var suffix = item.Key[sourceDirectory.Length..];
                        _objects[destinationDirectory + suffix] = item.Value;
                        _objects.Remove(item.Key);
                    }
                }
                return "{}";
            }
            if (!operation.EndsWith("operations/copyfile") && !operation.EndsWith("operations/movefile")) return "{}";
            using var request = JsonDocument.Parse(body);
            var root = request.RootElement;
            var source = Relative(root.GetProperty("srcRemote").GetString() ?? "");
            var destination = Relative(root.GetProperty("dstRemote").GetString() ?? "");
            long size; DateTimeOffset modified;
            lock (_gate)
            {
                if (!_objects.TryGetValue(source, out var sourceEntry))
                {
                    var sourceFs = root.TryGetProperty("srcFs", out var fs) ? fs.GetString() : null;
                    var physical = string.IsNullOrEmpty(sourceFs) ? null : Path.Combine(sourceFs, source.Replace('/', Path.DirectorySeparatorChar));
                    // rclone's Windows local backend exposes a physical full-width
                    // colon under the logical ':' spelling used in RC requests.
                    var encodedPhysical = string.IsNullOrEmpty(sourceFs) ? null : Path.Combine(sourceFs, source.Replace(':', '：').Replace('/', Path.DirectorySeparatorChar));
                    var info = physical is not null && File.Exists(physical) ? new FileInfo(physical) :
                        encodedPhysical is not null && File.Exists(encodedPhysical) ? new FileInfo(encodedPhysical) : null;
                    size = info?.Length ?? 0;
                    modified = info?.LastWriteTimeUtc ?? DateTimeOffset.Parse("2026-01-01T00:00:00Z");
                }
                else { size = sourceEntry.Size; modified = sourceEntry.Modified; }
                _objects[destination] = (size, modified);
                if (operation.EndsWith("operations/movefile")) _objects.Remove(source);
            }
            return "{}";
        }

        private static string Relative(string remote) => remote.Trim('/').StartsWith("root/", StringComparison.Ordinal) ? remote.Trim('/')[5..] : remote.Trim('/');
        private static string RelativeFileSystem(string fileSystem)
        {
            var separator = fileSystem.IndexOf(':');
            return Relative(separator >= 0 ? fileSystem[(separator + 1)..] : fileSystem);
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new OperationCanceledException("simulated transport timeout");
    }
}
