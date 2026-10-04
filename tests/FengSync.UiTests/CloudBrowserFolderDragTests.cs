using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using FengSync.Core;
using FengSync.Core.Mount;
using FengSync.Core.Rclone.Lifecycle;
using FengSync.Services;
using FengSync.Views;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// The rules a folder drag-out is made of, away from any window. The one that decides everything else: Explorer
/// copies whatever is on disk at drop time and reports success either way, so a folder may only be handed over when
/// its local mirror is genuinely complete — a stricter gate than a single file drag needs. Paths are composed in the
/// shape measured in <c>RcloneRecursiveListFactTests</c>: every entry of a recursive listing is already relative to
/// the remote root, directories are reported (empty ones included), and a directory's size never stands for its
/// contents.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudFolderDragRulesTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "fengsync-folder-drag-rules-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousDataDir;

    public CloudFolderDragRulesTests()
    {
        Directory.CreateDirectory(_dataRoot);
        // The mirror lives under the application data root, so without this a rules test would write into the
        // developer's own cache folder and read it back on the next run.
        _previousDataDir = Environment.GetEnvironmentVariable("FENGSYNC_DATA_DIR");
        Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", _dataRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", _previousDataDir);
        TryDelete(_dataRoot);
    }

    /// <summary>The local mirror path a drag of the endpoint named this would write into.</summary>
    private string Mirror(string endpointName, params string[] segments) =>
        Path.Combine(_dataRoot, "cloud-cache", endpointName, Path.Combine(segments));

    [Fact]
    public void A_folder_drag_mirrors_every_path_the_recursive_listing_names()
    {
        var plan = CloudFolderDrag.Build(Nas, Folder(Docs),
            [Leaf($"{Docs}/a.txt", 1024), Dir($"{Docs}/sub"), Leaf($"{Docs}/sub/b.txt", 2048)]);

        Assert.Null(plan.Refusal);
        Assert.Equal(Mirror("nas", "docs"), plan.FolderLocalPath);
        // The queue speaks the endpoint's coordinates — the configured root is already implied by the endpoint uri —
        // while Explorer needs the local mirror path. One row has to be read in both, or a file lands beside the
        // folder the user is dragging.
        Assert.Equal(["docs/a.txt", "docs/sub/b.txt"], plan.Files.Select(x => x.RemotePath));
        Assert.Equal([Mirror("nas", "docs", "a.txt"), Mirror("nas", "docs", "sub", "b.txt")],
            plan.Files.Select(x => x.LocalPath));
        Assert.Equal([Mirror("nas", "docs", "sub")], plan.DirectoryLocalPaths);
        Assert.Equal(3072, plan.TotalBytes);
    }

    [Fact]
    public void An_empty_directory_survives_the_drag_as_a_directory()
    {
        var plan = CloudFolderDrag.Build(Nas, Folder(Docs),
            [Leaf($"{Docs}/a.txt", 1024), Dir($"{Docs}/empty"), Dir($"{Docs}/sub"), Leaf($"{Docs}/sub/b.txt", 2048)]);

        // Measured: a recursive listing does report an empty directory. Dropping it from the plan would hand the
        // user a folder that is missing something they can see on screen right now.
        Assert.Contains(Mirror("nas", "docs", "empty"), plan.DirectoryLocalPaths);
        Assert.Equal(2, plan.FileCount);
    }

    [Fact]
    public void A_directory_never_counts_as_a_file_of_its_own_size()
    {
        var plan = CloudFolderDrag.Build(Nas, Folder(Docs), [Dir($"{Docs}/sub")]);

        // A directory's reported size is always 0, so it can never stand for its contents. A folder that names only
        // directories is therefore either genuinely empty — honest to drag — or the caller listed the wrong thing.
        Assert.Empty(plan.Files);
        Assert.Equal([Mirror("nas", "docs", "sub")], plan.DirectoryLocalPaths);
        Assert.Equal(0, plan.TotalBytes);
        Assert.Null(plan.Refusal);
    }

    [Fact]
    public void An_endpoint_whose_prefix_rclone_enforces_stages_without_repeating_a_root()
    {
        // An enforced-prefix S3 endpoint has an empty RootPath because the alias root *is* the prefix, and the
        // browser therefore addresses it with bare remote-root-relative paths. A drag that re-added a root here
        // would address the prefix twice and stage a folder that does not exist on the cloud.
        var aliasBacked = new CloudEndpointAccount(new RcloneAccount("photos", "s3"),
            new CloudEndpointMetadata("photos", "s3", "S3", "", "bucket/album"));

        var plan = CloudFolderDrag.Build(aliasBacked, Folder("2026"), [Leaf("2026/a.txt", 10)]);

        Assert.Equal("2026/a.txt", Assert.Single(plan.Files).RemotePath);
        Assert.Equal(Mirror("photos", "2026"), plan.FolderLocalPath);
    }

    [Fact]
    public void A_name_windows_cannot_hold_refuses_the_whole_folder()
    {
        var plan = CloudFolderDrag.Build(Nas, Folder(Docs), [Leaf($"{Docs}/a:b.txt", 1024)]);

        // A renamed file inside a folder the user is about to trust is worse than no drag, so this refuses the
        // whole folder rather than quietly dropping the row.
        Assert.NotNull(plan.Refusal);
        Assert.Contains("a:b.txt", plan.Refusal);
        Assert.Empty(plan.Files);
        Assert.Empty(plan.DirectoryLocalPaths);
    }

    [Fact]
    public void A_path_that_would_climb_out_of_the_folder_is_refused()
    {
        var plan = CloudFolderDrag.Build(Nas, Folder(Docs), [Leaf($"{Docs}/../escape.txt", 1024)]);

        Assert.NotNull(plan.Refusal);
        Assert.Contains("..", plan.Refusal);
        Assert.Empty(plan.Files);
    }

    [Fact]
    public void A_cloud_name_that_is_both_a_file_and_a_directory_is_refused()
    {
        // S3 allows the keys "x" and "x/y" in one bucket, and rclone then reports a directory named x beside a file
        // named x. Windows cannot hold both, so the folder is refused rather than drawn half one way and half the
        // other.
        var plan = CloudFolderDrag.Build(Nas, Folder(Docs), [Leaf($"{Docs}/x", 10), Dir($"{Docs}/x")]);

        Assert.NotNull(plan.Refusal);
        Assert.Contains("同名的文件与文件夹", plan.Refusal);
    }

    [Fact]
    public void One_folder_drag_never_asks_for_more_files_than_the_browser_can_read()
    {
        var atTheBound = Enumerable.Range(0, CloudFolderDrag.MaxFilesPerDrag)
            .Select(index => Leaf($"{Docs}/f{index}.txt", 1)).ToArray();
        Assert.Null(CloudFolderDrag.Build(Nas, Folder(Docs), atTheBound).Refusal);

        var oneOver = atTheBound.Concat([Leaf($"{Docs}/over.txt", 1)]).ToArray();
        var refused = CloudFolderDrag.Build(Nas, Folder(Docs), oneOver);

        // The bound is a count of transfers one gesture may start, so the message has to say what was counted and
        // what the ceiling is rather than just refusing.
        Assert.Contains($"{CloudFolderDrag.MaxFilesPerDrag + 1}", refused.Refusal);
        Assert.Contains(CloudFolderDrag.MaxFilesPerDrag.ToString(), refused.Refusal);
    }

    [Fact]
    public void A_listing_that_is_not_the_folders_own_is_a_caller_error()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            CloudFolderDrag.Build(Nas, Folder(Docs), [Leaf("team/other/a.txt", 10)]));

        // Measured: a recursive listing of a directory never names anything outside it. A caller that handed over a
        // parent's listing would drag a folder's worth of its neighbours, so this fails loudly instead.
        Assert.Contains("team/other/a.txt", failure.Message);
    }

    [Fact]
    public void A_folder_is_only_ready_when_every_file_has_the_length_the_cloud_reported()
    {
        var plan = CloudFolderDrag.Build(Nas, Folder(Docs), [Leaf($"{Docs}/a.txt", 5)]);
        CloudFolderDrag.CreateDirectories(plan);

        Assert.False(CloudFolderDrag.IsMirrorReady(plan));
        File.WriteAllText(Mirror("nas", "docs", "a.txt"), "12345");
        Assert.True(CloudFolderDrag.IsMirrorReady(plan));

        // A truncated mirror is not the folder: Explorer would drop half a file and report success.
        File.WriteAllText(Mirror("nas", "docs", "a.txt"), "123");
        Assert.False(CloudFolderDrag.IsMirrorReady(plan));
    }

    [Fact]
    public void An_empty_cloud_file_is_ready_once_it_exists()
    {
        var plan = CloudFolderDrag.Build(Nas, Folder(Docs), [Leaf($"{Docs}/zero.bin", 0)]);
        CloudFolderDrag.CreateDirectories(plan);
        var local = Mirror("nas", "docs", "zero.bin");

        // Measured against the bundled binary: an empty object reads back as empty, so 0 is a size to trust here.
        // The single-file cache gate treats an unknown size as nothing to trust, and reusing that gate for a folder
        // would leave any folder holding one empty file permanently undraggable.
        Assert.False(CloudDragCache.IsUsable(local, 0));
        Assert.False(CloudFolderDrag.IsMirrorReady(plan));

        File.WriteAllBytes(local, []);
        Assert.True(CloudFolderDrag.IsReady(Assert.Single(plan.Files)));
        Assert.True(CloudFolderDrag.IsMirrorReady(plan));
    }

    [Fact]
    public void A_missing_directory_keeps_the_folder_from_being_ready()
    {
        var plan = CloudFolderDrag.Build(Nas, Folder(Docs), [Leaf($"{Docs}/a.txt", 5), Dir($"{Docs}/empty")]);
        CloudFolderDrag.CreateDirectories(plan);
        File.WriteAllText(Mirror("nas", "docs", "a.txt"), "12345");
        Assert.True(CloudFolderDrag.IsMirrorReady(plan));

        Directory.Delete(Mirror("nas", "docs", "empty"));

        // Every file being present is not enough: the structure the user can see is part of what they are dragging.
        Assert.False(CloudFolderDrag.IsMirrorReady(plan));
    }

    [Fact]
    public void A_refused_plan_is_never_ready_and_names_nothing_to_create()
    {
        var plan = CloudFolderDrag.Build(Nas, Folder(Docs), [Leaf($"{Docs}/a:b.txt", 5)]);

        Assert.True(plan.IsRefused);
        Assert.False(CloudFolderDrag.IsMirrorReady(plan));
        Assert.Empty(plan.Files);
        Assert.Empty(plan.DirectoryLocalPaths);
    }

    private const string Docs = "team/docs";

    private static CloudEndpointAccount Nas =>
        new(new RcloneAccount("nas", "sftp"), new CloudEndpointMetadata("nas", "sftp", "SFTP", "team", ""));

    private static CloudFileEntry Folder(string path) => new(path.Split('/').Last(), path, true, 0, null);

    private static CloudFileEntry Dir(string path) => new(path.Split('/').Last(), path, true, 0, null);

    private static CloudFileEntry Leaf(string path, long size) =>
        new(path.Split('/').Last(), path, false, size, new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero));

    private static void TryDelete(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Directory.Delete(path, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}

/// <summary>
/// The browser's own part of a folder drag: which files it asks the queue for, what it hands Explorer, and what it
/// says when it must not drag. Every case runs the real window on its own dispatcher thread over one stand-in
/// rclone, so no daemon starts and no credential exists. Where a claim is about bytes a stand-in cannot move, the
/// test reads the payload the drag handed the queue instead — that payload is exactly what decides where the bytes
/// land.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudBrowserFolderDragTests
{
    /// <summary>The root folder of the endpoint being browsed.</summary>
    private const string Root = "team";

    private const string Docs = "team/docs";

    [Fact]
    public async Task A_folder_whose_mirror_is_already_there_hands_explorer_the_folder_and_nothing_else() =>
        await WithWindow(async harness =>
        {
            Write(harness.Mirror("docs", "a.txt"), 1024);
            Write(harness.Mirror("docs", "sub", "b.txt"), 2048);

            var handed = await harness.Drag(harness.FolderRow(Docs));

            // One path, the mirror folder itself — never the files inside it, and never a row of the listing.
            Assert.Equal([harness.Mirror("docs")], handed);
            // Nothing was missing, so nothing went to the queue: a drag that re-downloaded what it already holds
            // would show the user a transfer list for a folder they have.
            Assert.Empty(harness.Queue.Tasks);
            Assert.Empty(harness.Rclone.Copies);
            Assert.Contains("已拖出整个文件夹：docs", harness.Window.StatusText.Text, StringComparison.Ordinal);
            // A drag that had nothing to fetch must not open a transfer list — the user would see a progress
            // window for a folder that was already on disk.
            Assert.Equal(0, harness.Surfaced);
        });

    [Fact]
    public async Task A_folder_drag_asks_for_exactly_the_files_it_does_not_have_and_never_for_a_directory() =>
        await WithWindow(async harness =>
        {
            Write(harness.Mirror("docs", "a.txt"), 1024);

            var handed = await harness.Drag(harness.FolderRow(Docs));

            // The listing names four rows — two files and two directories — and only the missing file is asked for.
            Assert.Empty(handed);
            var task = Assert.Single(harness.Queue.Tasks);
            Assert.Equal("sftp://nas/team", task.RemoteUri);
            Assert.Equal("docs/sub/b.txt", task.RemotePath);
            Assert.Equal(harness.Mirror("docs", "sub", "b.txt"), task.LocalPath);
            // The bytes are not there yet, so this gesture must not pretend the folder is ready to drop.
            Assert.Contains("正在下载", harness.Window.StatusText.Text, StringComparison.Ordinal);
            // Bytes the user did not have are now moving, so the queue's window is where they can see it — the same
            // promise a single-file drag makes.
            Assert.Equal(1, harness.Surfaced);
            // And a mirror holding a file whose bytes never arrived is not put in front of the user as if it were
            // the folder they asked for.
            Assert.Empty(harness.Revealed);
        });

    [Fact]
    public async Task A_folder_drag_creates_the_empty_directories_a_download_would_never_create() =>
        await WithWindow(async harness =>
        {
            await harness.Drag(harness.FolderRow(Docs));

            // Empty directories are part of what the user sees, and a download only creates the folders that hold a
            // file, so the structure has to come from the listing rather than from the bytes.
            Assert.True(Directory.Exists(harness.Mirror("docs", "empty")));
            Assert.True(Directory.Exists(harness.Mirror("docs", "sub")));
            Assert.Empty(harness.Revealed);
        });

    [Fact]
    public async Task A_folder_whose_bytes_arrive_after_the_gesture_is_revealed_once_it_is_whole() =>
        await WithWindow(async harness =>
        {
            // No budget at all means the gesture always ends before the bytes do, which is the case a large
            // folder hits: the drop cannot happen now, so the folder has to be revealed when it is whole.
            harness.Window.DragBudget = TimeSpan.Zero;
            harness.Rclone.Serves("[nas:/]/team/docs/a.txt", 1024);
            harness.Rclone.Serves("[nas:/]/team/docs/sub/b.txt", 2048);

            var handed = await harness.Drag(harness.FolderRow(Docs));

            // Nothing was dropped: at drop time the mirror was still filling in, and Explorer would have copied
            // whatever was on disk and reported success either way.
            Assert.Empty(handed);
            await WaitUntil(() => harness.Revealed.Count > 0, "文件夹下载完成后没有被交到用户面前");
            Assert.Equal([harness.Mirror("docs")], harness.Revealed);
            Assert.Contains("已下载完成", harness.Window.StatusText.Text, StringComparison.Ordinal);
        });

    [Fact]
    public async Task A_folder_with_a_file_that_failed_is_reported_instead_of_revealed() =>
        await WithWindow(async harness =>
        {
            harness.Window.DragBudget = TimeSpan.Zero;
            // a.txt's bytes are refused, so its download fails; b.txt's arrive.
            harness.Rclone.Refuses("[nas:/]/team/docs/a.txt");
            harness.Rclone.Serves("[nas:/]/team/docs/sub/b.txt", 2048);

            var handed = await harness.Drag(harness.FolderRow(Docs));

            Assert.Empty(handed);
            await WaitUntil(() => harness.Window.StatusText.Text.Contains("没能下载", StringComparison.Ordinal),
                "没能下载的文件没有被报告");
            // A folder with a hole in it must never be put in front of the user as if it were the one they chose.
            Assert.Empty(harness.Revealed);
        });

    [Fact]
    public async Task A_folder_drag_reads_each_file_from_the_directory_it_lives_in() =>
        await WithWindow(async harness =>
        {
            await harness.Drag(harness.FolderRow(Docs));

            // A download stages under the file's own name, so the endpoint it reads from is rooted at the file's
            // own directory. Addressing that name against the endpoint root instead would fetch a same-named
            // object one directory up: a different file, and one the user never asked to drag.
            Assert.Contains("[nas:/]/team/docs/sub/b.txt", harness.Rclone.Operations);
            Assert.DoesNotContain("[nas:/]/team/b.txt", harness.Rclone.Operations);
        });

    [Fact]
    public async Task A_folder_the_cloud_no_longer_has_is_reported_before_anything_is_staged() =>
        await WithWindow(async harness =>
        {
            harness.Rclone.Forgets(Docs);

            var handed = await harness.Drag(harness.FolderRow(Docs));

            Assert.Empty(handed);
            Assert.Empty(harness.Rclone.Copies);
            Assert.Empty(harness.Queue.Tasks);
            Assert.Contains("无法读取这个文件夹的内容", harness.Window.StatusText.Text, StringComparison.Ordinal);
            // Measured: a missing directory is an error at the wire, not an empty listing — so "gone" and "empty"
            // are two different answers, and neither may leave a folder behind in the cache.
            Assert.False(Directory.Exists(harness.Mirror("docs")));
            Assert.Equal(0, harness.Surfaced);
        });

    [Fact]
    public async Task An_empty_folder_is_dragged_rather_than_refused() =>
        await WithWindow(async harness =>
        {
            // Measured: an existing-but-empty directory answers an empty list, and a folder whose listing holds
            // only directories is genuinely empty. The user sees an empty folder, so the drag gives them one.
            harness.Rclone.Lists(Docs, [($"{Docs}/empty", true, 0)]);

            var handed = await harness.Drag(harness.FolderRow(Docs));

            Assert.Equal([harness.Mirror("docs")], handed);
            Assert.Empty(harness.Queue.Tasks);
            Assert.True(Directory.Exists(harness.Mirror("docs", "empty")));
            Assert.Contains("空文件夹", harness.Window.StatusText.Text, StringComparison.Ordinal);
            Assert.Equal(0, harness.Surfaced);
        });

    [Fact]
    public async Task A_refused_folder_drag_creates_nothing_and_asks_for_nothing() =>
        await WithWindow(async harness =>
        {
            harness.Rclone.Lists(Docs, [($"{Docs}/a:b.txt", false, 10)]);

            var handed = await harness.Drag(harness.FolderRow(Docs));

            Assert.Empty(handed);
            Assert.Empty(harness.Queue.Tasks);
            Assert.Contains("无法拖出“docs”", harness.Window.StatusText.Text, StringComparison.Ordinal);
            Assert.Contains("a:b.txt", harness.Window.StatusText.Text, StringComparison.Ordinal);
            Assert.False(Directory.Exists(harness.Mirror("docs")));
            Assert.Equal(0, harness.Surfaced);
        });

    [Fact]
    public async Task Dragging_a_cached_file_still_hands_over_that_one_file() =>
        await WithWindow(async harness =>
        {
            Write(harness.Mirror("docs", "a.txt"), 512);

            var handed = await harness.Drag(harness.FileRow($"{Root}/docs/a.txt", 512));

            Assert.Equal([harness.Mirror("docs", "a.txt")], handed);
            Assert.Contains("已拖出：a.txt", harness.Window.StatusText.Text, StringComparison.Ordinal);
            Assert.Equal(0, harness.Surfaced);
        });

    [Fact]
    public async Task Dragging_a_file_the_cache_does_not_have_goes_to_the_queue_instead_of_to_explorer() =>
        await WithWindow(async harness =>
        {
            var handed = await harness.Drag(harness.FileRow($"{Root}/docs/a.txt", 1024));

            Assert.Empty(handed);
            var task = Assert.Single(harness.Queue.Tasks);
            Assert.Equal("sftp://nas/team", task.RemoteUri);
            Assert.Equal("docs/a.txt", task.RemotePath);
            Assert.Equal(harness.Mirror("docs", "a.txt"), task.LocalPath);
            Assert.Equal(1, harness.Surfaced);
        });

    /// <summary>
    /// Runs one drag against the real browser window: a stand-in rclone answers the recursive listing and records
    /// what the drag asked the queue to copy, the application data root points at a throwaway directory so the
    /// mirror is written where the test can read it, <see cref="RemoteEndpointManagerWindow.DragOutHandoff"/>
    /// stands in for the pointer (WPF hit-tests a drag against the real cursor, so an in-process test cannot start
    /// the modal loop and records the paths it would have handed Explorer instead), and
    /// <see cref="RemoteEndpointManagerWindow.SurfaceTransfers"/> stands in for raising the queue's window — which
    /// an in-process test cannot reach because <c>Application.Current</c> may belong to another test's thread.
    /// </summary>
    private static Task WithWindow(Func<TestHarness, Task> test) => RunSta(async () =>
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "fengsync-folder-drag-window-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        var previous = AppServices.TryCurrent;
        var previousDataDir = Environment.GetEnvironmentVariable("FENGSYNC_DATA_DIR");
        Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", dataRoot);
        var standIn = new FakeRclone();
        // The shape a recursive listing really answers: every path is relative to the fs root, so a row under
        // the browsed folder still carries that folder's own name. A fake that answered bare `docs/...` here
        // would have the browser compose the browsed prefix a second time and mirror the tree one level deep.
        standIn.Lists(Docs, [($"{Docs}/a.txt", false, 1024), ($"{Docs}/empty", true, 0), ($"{Docs}/sub", true, 0),
            ($"{Docs}/sub/b.txt", false, 2048)]);
        var http = new HttpClient(standIn) { BaseAddress = new Uri("http://rc.test/") };
        var client = new RcloneRcClient(http, new Uri("http://rc.test/"), "user", "pass");
        var queue = new CloudTransferQueue(new CloudTransferJournalStore(dataRoot), _ => Task.FromResult(client));
        // A refused download must reach its verdict now rather than after the real backoff: the drag's report of
        // a folder with a hole in it is the thing under test, not the queue's patience.
        queue.RetryDelay = _ => TimeSpan.Zero;
        AppServices.Install(new StubShell(queue));
        RemoteEndpointManagerWindow? window = null;
        try
        {
            window = new RemoteEndpointManagerWindow(new CloudFileManagerService(_ => Task.FromResult(client)), queue);
            window.LoadEndpointsForTest([Row("nas", Root)], selectName: "nas");
            window.ShowListing(Root, []);
            var handed = new List<string>();
            window.DragOutHandoff = paths => handed.AddRange(paths);
            var surfaced = new List<int>();
            window.SurfaceTransfers = () => surfaced.Add(1);
            var revealed = new List<string>();
            // Opening Explorer is a machine-level effect the suite must not cause, and the reveal is the only
            // thing a user gets when the bytes did not arrive inside the gesture.
            window.Reveal = revealed.Add;
            await test(new TestHarness(window, standIn, queue, dataRoot, handed, surfaced, revealed));
        }
        finally
        {
            AppServices.Install(previous);
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", previousDataDir);
            try { window?.Close(); } catch (InvalidOperationException) { }
            TryDelete(dataRoot);
        }
    });

    private sealed class TestHarness(
        RemoteEndpointManagerWindow window,
        FakeRclone standIn,
        CloudTransferQueue queue,
        string dataRoot,
        List<string> handed,
        List<int> surfaced,
        List<string> revealed)
    {
        public RemoteEndpointManagerWindow Window => window;
        public FakeRclone Rclone => standIn;
        public CloudTransferQueue Queue => queue;

        /// <summary>How many times the drag raised the transfer window — the user's view of the bytes it started.</summary>
        public int Surfaced => surfaced.Count;

        /// <summary>The paths a finished download put in front of the user instead of dropping.</summary>
        public IReadOnlyList<string> Revealed => revealed.ToArray();

        /// <summary>The local mirror path this endpoint's drag writes into.</summary>
        public string Mirror(params string[] segments) =>
            Path.Combine(new[] { dataRoot, "cloud-cache", "nas" }
                .Concat(segments.Select(x => x.Replace('/', Path.DirectorySeparatorChar))).ToArray());

        public CloudFileEntry FolderRow(string path) => new(path.Split('/').Last(), path, true, 0, null);

        public CloudFileEntry FileRow(string path, long size) =>
            new(path.Split('/').Last(), path, false, size, new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero));

        /// <summary>
        /// Starts the drag the pointer would have started and returns the paths it handed Explorer. The gesture waits
        /// at most the drag budget for bytes and then finishes on its own, which is why this can be awaited rather
        /// than blocked on: the window's own continuations need this dispatcher to keep pumping.
        /// </summary>
        public async Task<IReadOnlyList<string>> Drag(CloudFileEntry entry)
        {
            handed.Clear();
            await window.DragOutAsync(entry);
            return handed.ToArray();
        }
    }

    private static void Write(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    /// <summary>Waits for a state the drag reaches on its own, pumping the dispatcher so its continuations run.</summary>
    private static async Task WaitUntil(Func<bool> condition, string message)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail(message);
    }

    private static CloudEndpointRow Row(string name, string root) =>
        new(new CloudEndpointAccount(new RcloneAccount(name, "sftp"), new CloudEndpointMetadata(name, "sftp", "SFTP", root, "")));

    /// <summary>
    /// A stand-in for rclone's RC endpoint: <c>operations/list</c> answers from a modelled tree in the shape measured
    /// against v1.75.0 (every path relative to the remote root, directories marked, a directory of size 0), a
    /// directory it was not told to hold is the 404 "directory not found" the real backend says, and every
    /// <c>operations/copyfile</c> is recorded as the four coordinates a transfer is made of — which is how a test can
    /// read where the bytes would land without any bytes moving.
    /// </summary>
    private sealed class FakeRclone : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, List<(string Path, bool IsDir, long Size)>> _lists = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _bodies = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _refusals = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Call> _copies = [];
        private readonly List<string> _operations = [];

        public IReadOnlyList<Call> Copies
        {
            get { lock (_gate) return _copies.ToArray(); }
        }

        public void Lists(string directory, IEnumerable<(string Path, bool IsDir, long Size)> entries) =>
            _lists[directory] = [.. entries];

        /// <summary>
        /// The bytes a <c>--rc-serve</c> read of one object answers with. A download stages by reading the
        /// object's bytes and comparing the result's length with the size the listing promised, so a stand-in
        /// that answered an empty body would leave every mirror incomplete for the wrong reason.
        /// </summary>
        public void Serves(string servePath, int bytes) => _bodies[servePath] = new byte[bytes];

        /// <summary>The way a backend answers a read of an object it does not have: a refusal, not an empty body.</summary>
        public void Refuses(string servePath) => _refusals.Add(servePath);

        /// <summary>The way a backend answers a directory that is not there: it refuses, it does not list nothing.</summary>
        public void Forgets(string directory)
        {
            _lists.Remove(directory);
        }

        public IReadOnlyList<string> Operations
        {
            get { lock (_gate) return _operations.ToArray(); }
        }

        public sealed record Call(string SourceFs, string SourceRemote, string TargetFs, string TargetRemote);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var operation = request.RequestUri!.AbsolutePath.TrimStart('/');
            lock (_gate) _operations.Add(operation + (operation == "operations/list" ? "<" + body + ">" : ""));
            if (operation != "operations/list")
            {
                if (operation == "operations/copyfile") RecordCopy(body);
                if (_refusals.Contains(operation))
                    return Answer("""{"error":"object not found","status":404}""", HttpStatusCode.NotFound);
                return _bodies.TryGetValue(operation, out var served)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served) }
                    : Answer("{}", HttpStatusCode.OK);
            }

            using var document = JsonDocument.Parse(body);
            var remote = Read(document.RootElement, "remote");
            if (!_lists.TryGetValue(remote, out var entries))
                return Answer("""{"error":"sftp: directory not found","input":{},"path":"","status":404}""", HttpStatusCode.NotFound);
            return Answer("{\"list\":[" + string.Join(",", entries.Select(Item)) + "]}", HttpStatusCode.OK);
        }

        private void RecordCopy(string body)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            lock (_gate)
                _copies.Add(new Call(Read(root, "srcFs"), Read(root, "srcRemote"), Read(root, "dstFs"), Read(root, "dstRemote")));
        }

        private static string Item((string Path, bool IsDir, long Size) entry) =>
            "{\"Path\":\"" + entry.Path + "\",\"Name\":\"" + entry.Path.Split('/').Last()
            + "\",\"IsDir\":" + (entry.IsDir ? "true" : "false")
            + ",\"Size\":" + entry.Size + ",\"ModTime\":\"2026-03-01T08:00:00Z\"}";

        private static HttpResponseMessage Answer(string json, HttpStatusCode status) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static string Read(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
    }

    /// <summary>Just enough of a shell to build the window; the rclone behind it is unreachable on purpose.</summary>
    private sealed class StubShell : IAppServiceHost
    {
        private readonly RcloneLifecycleHost _rclone = new();

        public StubShell(CloudTransferQueue transfers)
        {
            CloudTransfers = transfers;
            MountService = new RcloneMountService(new UnreachableRclone());
        }

        public RcloneLifecycleHost RcloneHost => _rclone;
        public IRcloneLifecycleClient RcloneLifecycle => _rclone;
        public RcloneMountService MountService { get; }
        public CloudTransferQueue CloudTransfers { get; }

        private sealed class UnreachableRclone : IRcloneLifecycleClient
        {
            public Task<JsonElement> CallAsync(string operation, object payload, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException($"这个测试不调用 rclone（{operation}）。");
        }
    }

    private static Task RunSta(Func<Task> action)
    {
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            Exception? failure = null;
            Task? run = null;
            var ownsApplication = MergeApplicationThemes();
            try
            {
                run = action();
                _ = run.ContinueWith(completed =>
                {
                    failure = completed.IsFaulted ? completed.Exception!.GetBaseException() : null;
                    frame.Continue = false;
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
            }
            catch (Exception exception)
            {
                failure = exception;
                frame.Continue = false;
            }
            var watchdog = new System.Threading.Timer(
                _ => dispatcher.BeginInvoke(() => frame.Continue = false),
                null, TimeSpan.FromSeconds(90), System.Threading.Timeout.InfiniteTimeSpan);
            Dispatcher.PushFrame(frame);
            watchdog.Dispose();
            if (failure is null && run is { IsCompleted: false })
                failure = new TimeoutException("云端拖出测试没有在规定时间内完成（窗口线程被卡住了）。");
            if (ownsApplication)
                try { Application.Current?.Shutdown(); }
                catch (InvalidOperationException) { }
            dispatcher.InvokeShutdown();
            finished.TrySetResult(failure);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return Task.Run(async () =>
        {
            var exception = await finished.Task;
            thread.Join(TimeSpan.FromSeconds(30));
            if (exception is not null) ExceptionDispatchInfo.Capture(exception).Throw();
        });
    }

    private static bool MergeApplicationThemes()
    {
        var ownsApplication = Application.Current is null;
        if (ownsApplication) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var application = Application.Current!;
        if (application.Resources.MergedDictionaries.Count == 0)
            foreach (var resource in new[] { "DesignTokens.xaml", "Typography.xaml", "Icons.xaml", "Controls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/FengSync;component/Themes/{resource}")
                });
        return ownsApplication;
    }

    private static void TryDelete(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Directory.Delete(path, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}
