using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FengSync.Core;
using FengSync.Core.Configuration;
using FengSync.Core.Mount;
using FengSync.Core.Rclone.Lifecycle;
using FengSync.Services;
using FengSync.Views;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// The browser's second drawing: the same rows as tiles, with a picture pulled for the rows that are pictures.
/// Three things are pinned here and each needs a different harness. Which row may show a picture, and what the
/// identity of that picture is, are plain functions. The store that pulls, caches and decodes is driven against a
/// stand-in that answers <c>--rc-serve</c> GETs in the shapes the fact tests measured against the bundled v1.75.0
/// (<c>RcloneServeReadFactTests</c>) — a fake that invented its own status shapes would prove nothing about the
/// binary the application actually talks to. And the window is built for real, because what a toggle must not do is
/// rebuild the rows: the keyboard, the clipboard and the delete threshold all read the one row collection, so the
/// second drawing has to be a different paint of the same list rather than a second list.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudBrowserGridViewTests
{
    private const string Remote = "team";
    private const string Photo = "team/photo.png";
    private const string Other = "team/other.png";
    private const string Note = "team/note.txt";
    private const string Webp = "team/shot.webp";
    private const string Huge = "team/huge.jpg";
    private const string Broken = "team/broken.png";
    private const string Gone = "team/gone.png";
    private const string Inbox = "team/inbox";

    private static readonly DateTimeOffset Stamp = new(2026, 5, 4, 6, 30, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ the rules

    [Fact]
    public void Only_the_formats_wpf_can_decode_are_offered_as_a_picture()
    {
        Assert.True(CloudBrowserTiles.CanDecode("a.PNG"));
        Assert.True(CloudBrowserTiles.CanDecode("a.jpeg"));
        Assert.True(CloudBrowserTiles.CanDecode("a.tif"));

        Assert.Equal(CloudTileDecision.Show, CloudBrowserTiles.DecideFor(Row(Photo, 40 * 1024)));
        Assert.True(CloudBrowserTiles.OffersThumbnail(Row(Photo, 40 * 1024)));
        Assert.Equal(CloudTileDecision.NotAPicture, CloudBrowserTiles.DecideFor(Row(Note, 512)));
        Assert.Equal(CloudTileDecision.NotAPicture, CloudBrowserTiles.DecideFor(Row("team/report.pdf", 512)));
    }

    [Fact]
    public void A_picture_wpf_cannot_decode_is_named_as_the_reason_rather_than_left_unexplained()
    {
        // The two sets must never overlap, or one row would be both offered and refused — and WebP in particular is
        // a photo a user expects to see, so saying nothing about it would read as "this is not an image".
        Assert.Empty(CloudBrowserTiles.DecodableExtensions
            .Intersect(CloudBrowserTiles.UndecodableImageExtensions, StringComparer.OrdinalIgnoreCase));

        var entry = Row(Webp, 30 * 1024);
        var refusal = CloudBrowserTiles.RefusalFor(entry);

        Assert.Equal(CloudTileDecision.Undecodable, CloudBrowserTiles.DecideFor(entry));
        Assert.False(CloudBrowserTiles.OffersThumbnail(entry));
        Assert.NotNull(refusal);
        Assert.Contains(".webp", refusal!, StringComparison.OrdinalIgnoreCase);
        // A document is not a picture and does not need telling.
        Assert.Null(CloudBrowserTiles.RefusalFor(Row(Note, 512)));
    }

    [Fact]
    public void A_picture_bigger_than_the_tile_cap_keeps_its_icon_and_says_its_size()
    {
        var entry = Row(Huge, CloudBrowserTiles.ThumbnailMaxBytes + 1);
        var refusal = CloudBrowserTiles.RefusalFor(entry);

        Assert.Equal(CloudTileDecision.TooLarge, CloudBrowserTiles.DecideFor(entry));
        Assert.False(CloudBrowserTiles.OffersThumbnail(entry));
        Assert.NotNull(refusal);
        Assert.Contains(CloudItemDetails.HumanSize(entry.Size), refusal!);
        // Exactly at the cap is still a tile: the cap is a cost, not a cliff.
        Assert.Equal(CloudTileDecision.Show, CloudBrowserTiles.DecideFor(Row(Huge, CloudBrowserTiles.ThumbnailMaxBytes)));
    }

    [Fact]
    public void An_empty_picture_says_it_has_nothing_to_read_and_a_folder_says_nothing_at_all()
    {
        Assert.Equal(CloudTileDecision.Empty, CloudBrowserTiles.DecideFor(Row(Photo, 0)));
        Assert.Contains("没有可读取的内容", CloudBrowserTiles.RefusalFor(Row(Photo, 0)));

        var folder = new CloudFileEntry("inbox", Inbox, true, 0, Stamp);
        Assert.Equal(CloudTileDecision.NotAPicture, CloudBrowserTiles.DecideFor(folder));
        Assert.Null(CloudBrowserTiles.RefusalFor(folder));
    }

    [Fact]
    public void The_tile_key_follows_the_bytes_it_would_paint()
    {
        var baseKey = CloudBrowserTiles.KeyFor(Remote, Row(Photo, 1024));

        // Name and path alone are not enough: an overwritten picture keeps its name, and a cache keyed by name
        // would go on painting the old bytes forever.
        Assert.Equal(baseKey, CloudBrowserTiles.KeyFor(Remote, Row(Photo, 1024)));
        Assert.NotEqual(baseKey, CloudBrowserTiles.KeyFor(Remote, Row(Photo, 1025)));
        Assert.NotEqual(baseKey, CloudBrowserTiles.KeyFor(Remote,
            new CloudFileEntry("photo.png", Photo, false, 1024, Stamp.AddMinutes(1))));
        Assert.NotEqual(baseKey, CloudBrowserTiles.KeyFor("other", Row(Photo, 1024)));
        Assert.NotEqual(baseKey, CloudBrowserTiles.KeyFor(Remote, Row(Other, 1024)));
        // A backend that reports no modification time still gets one stable key rather than one that changes on
        // every repaint — that would re-read the object for nothing.
        var timeless = new CloudFileEntry("photo.png", Photo, false, 1024, null);
        Assert.Equal(CloudBrowserTiles.KeyFor(Remote, timeless), CloudBrowserTiles.KeyFor(Remote, timeless));
    }

    [Fact]
    public void The_cached_name_keeps_the_extension_so_the_decoder_sees_the_format()
    {
        var entry = Row(Photo, 1024);
        var path = CloudBrowserTiles.CachedFileFor("cache", Remote, entry);

        Assert.StartsWith("cache", path);
        Assert.EndsWith(".png", path, StringComparison.Ordinal);
        // The key is the whole file name: nothing the user called the file survives into the cache directory.
        Assert.Equal(CloudBrowserTiles.KeyFor(Remote, entry) + ".png", Path.GetFileName(path));
        Assert.DoesNotContain("photo", path, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the store

    [Fact]
    public async Task A_pulled_picture_is_read_once_and_a_repaint_reads_nothing()
    {
        var png = Png();
        await WithStore(new FakeServe().Holds(Photo, png), async (store, cloud, cache) =>
        {
            var entry = Row(Photo, png.Length);
            Assert.Null(store.Peek(Remote, entry));

            Assert.True(await store.LoadAsync(Remote, entry));
            Assert.NotNull(store.Peek(Remote, entry));
            Assert.True(await store.LoadAsync(Remote, entry));

            // Waiting is the state a tile is in most of its life: the grid repaints whenever a picture arrives,
            // and without a remembered answer each repaint would be another read of the same object.
            Assert.Equal([Photo], cloud.Reads);
            Assert.Single(CacheFiles(cache));
        });
    }

    [Fact]
    public async Task A_picture_already_in_the_cache_is_painted_without_touching_the_cloud()
    {
        var png = Png();
        await WithStore(new FakeServe().Holds(Photo, png), async (store, cloud, cache) =>
        {
            var entry = Row(Photo, png.Length);
            Directory.CreateDirectory(cache);
            File.WriteAllBytes(CloudBrowserTiles.CachedFileFor(cache, Remote, entry), png);

            Assert.True(await store.LoadAsync(Remote, entry));
            Assert.NotNull(store.Peek(Remote, entry));

            // This is the whole point of the disk cache: reopening the folder costs the user nothing.
            Assert.Empty(cloud.Reads);
        });
    }

    [Fact]
    public async Task A_row_that_offers_no_picture_is_never_read()
    {
        await WithStore(new FakeServe().Holds(Photo, Png()), async (store, cloud, cache) =>
        {
            Assert.False(await store.LoadAsync(Remote, Row(Note, 512)));
            Assert.False(await store.LoadAsync(Remote, Row(Webp, 30 * 1024)));
            Assert.False(await store.LoadAsync(Remote, Row(Huge, CloudBrowserTiles.ThumbnailMaxBytes + 1)));
            Assert.False(await store.LoadAsync(Remote, new CloudFileEntry("inbox", Inbox, true, 0, null)));

            Assert.Empty(cloud.Reads);
            Assert.Empty(CacheFiles(cache));
        });
    }

    [Fact]
    public async Task An_object_the_cloud_has_dropped_is_refused_once_and_not_asked_again()
    {
        // The listing still shows the row, so the grid asks for it and the cloud answers 404 the way the fact tests
        // measured. Refusing every repaint would turn one vanished file into a permanent stream of reads.
        await WithStore(new FakeServe(), async (store, cloud, cache) =>
        {
            var entry = Row(Gone, 2048);

            Assert.False(await store.LoadAsync(Remote, entry));
            Assert.Null(store.Peek(Remote, entry));
            Assert.False(await store.LoadAsync(Remote, entry));

            Assert.Equal([Gone], cloud.Reads);
            Assert.Empty(CacheFiles(cache));
        });
    }

    [Fact]
    public async Task Bytes_that_are_not_a_picture_leave_no_cache_entry_behind()
    {
        await WithStore(new FakeServe().Holds(Broken, Encoding.UTF8.GetBytes("this is not a picture")),
            async (store, cloud, cache) =>
            {
                var entry = Row(Broken, 21);

                Assert.False(await store.LoadAsync(Remote, entry));
                Assert.Null(store.Peek(Remote, entry));

                // The row was read and its bytes refused decoding, so the entry is dropped: keeping it would make
                // every later session re-decode the same broken bytes instead of giving the object a fresh read.
                Assert.Empty(CacheFiles(cache));
                Assert.Equal([Broken], cloud.Reads);
            });
    }

    [Fact]
    public async Task A_container_the_decoder_does_not_recognise_leaves_no_tile_either()
    {
        // A real PNG signature with nothing after it. The bundled WPF answers this shape with a
        // FileFormatException, which derives from SystemException rather than IOException, so a decode filter that
        // names only the obvious exceptions lets it escape through the tile round and crash the UI thread — the
        // failure this case exists to keep impossible.
        await WithStore(new FakeServe().Holds(Broken, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            async (store, cloud, cache) =>
            {
                var entry = Row(Broken, 8);

                Assert.False(await store.LoadAsync(Remote, entry));
                Assert.Null(store.Peek(Remote, entry));
                Assert.Empty(CacheFiles(cache));
                Assert.Equal([Broken], cloud.Reads);
            });
    }

    [Fact]
    public async Task A_row_whose_object_is_bigger_than_the_listing_said_is_not_painted()
    {
        // The listing's size is what makes a row an offer; the object's own answer is what stops the read when that
        // listing was stale. Both are needed and neither is a substitute for the other.
        var png = Png();
        await WithStore(new FakeServe().Holds(Huge, png, reportedTotal: 20L * 1024 * 1024),
            async (store, cloud, cache) =>
            {
                var entry = Row(Huge, png.Length);

                Assert.False(await store.LoadAsync(Remote, entry));
                Assert.Null(store.Peek(Remote, entry));

                Assert.Equal([Huge], cloud.Reads);
                Assert.Empty(CacheFiles(cache));
            });
    }

    [Fact]
    public async Task The_cache_is_trimmed_when_it_passes_its_ceiling()
    {
        // A 66 MB file whose bytes are never written: the ceiling rule counts what the directory reports, and a
        // sparse file reports the same length for a fraction of the cost. The point of the case is that the trim
        // fires at all — a cap nobody has watched being reached is a cap that does not exist.
        var png = Png();
        await WithStore(new FakeServe().Holds(Photo, png), async (store, cloud, cache) =>
        {
            Directory.CreateDirectory(cache);
            var filler = Path.Combine(cache, "an-older-tile.png");
            using (var created = File.Create(filler)) created.SetLength(CloudBrowserTiles.CacheMaxBytes + 1);
            File.SetLastWriteTimeUtc(filler, DateTime.UtcNow.AddDays(-1));

            Assert.True(await store.LoadAsync(Remote, Row(Photo, png.Length)));

            var remaining = CacheFiles(cache).Select(name => new FileInfo(name).Length).ToList();
            Assert.True(remaining.Sum() <= CloudBrowserTiles.CacheMaxBytes,
                $"缩略图缓存没有收回到上限内：{remaining.Sum()} 字节。");
            Assert.False(File.Exists(filler));
            Assert.Equal([Photo], cloud.Reads);
        });
    }

    // ------------------------------------------------------------------ the window

    [Fact]
    public async Task The_toggle_redraws_the_same_rows_and_keeps_which_ones_were_selected()
    {
        var png = Png();
        await WithWindow(async (window, cloud) =>
        {
            var before = window.VisibleEntries;
            Select(window, Photo);
            Assert.NotNull(window.FileList.View);

            await window.SetBrowserViewAsync(CloudBrowserViewMode.Grid);

            Assert.Equal(CloudBrowserViewMode.Grid, window.BrowserView);
            Assert.Null(window.FileList.View);
            Assert.NotNull(window.FileList.ItemTemplate);
            Assert.NotNull(window.FileList.ItemsPanel);
            var after = window.VisibleEntries;
            Assert.Equal(before.Count, after.Count);
            for (var index = 0; index < before.Count; index++)
                Assert.True(ReferenceEquals(before[index], after[index]),
                    $"网格视图重做了第 {index} 行：一份行集合变成两份，全选与行菜单就会各说一套。");
            Assert.Equal([Photo], window.Selection.Select(entry => entry.Path));

            await window.PendingThumbnails;

            // The repaint that paints an arrived picture is the one that could silently drop the selection, and a
            // user who highlighted three files before clicking 删除 must still be highlighting those three.
            Assert.Equal([Photo], window.Selection.Select(entry => entry.Path));
            Assert.NotNull(window.TileFor(window.VisibleEntries.Single(entry => entry.Path == Photo)));

            await window.SetBrowserViewAsync(CloudBrowserViewMode.List);
            Assert.Equal(CloudBrowserViewMode.List, window.BrowserView);
            Assert.NotNull(window.FileList.View);
            // Unset, not null: `ItemTemplate = null` and `ItemsPanel = null` read back as null here and pass,
            // while a shown control presents nothing at all. Measured against a real window: a bound GridView
            // writes no local template, panel or container style (all <c>unset</c>) and draws its rows through
            // the ListView's own style, so a local null ItemsPanel took the cell count from 2 to 0 while the
            // collection kept every row. The list view is the state the XAML built, so the toggle removes its own
            // overrides instead of writing empty ones.
            Assert.Equal(DependencyProperty.UnsetValue,
                window.FileList.ReadLocalValue(ItemsControl.ItemTemplateProperty));
            Assert.Equal(DependencyProperty.UnsetValue,
                window.FileList.ReadLocalValue(ItemsControl.ItemsPanelProperty));
            Assert.Equal([Photo], window.Selection.Select(entry => entry.Path));
        }, new FakeServe().Holds(Photo, png), Listing(png.Length, 21, 2048));
    }

    [Fact]
    public async Task Grid_view_reads_only_the_rows_that_offer_a_picture()
    {
        var png = Png();
        await WithWindow(async (window, cloud) =>
        {
            await window.SetBrowserViewAsync(CloudBrowserViewMode.Grid);
            await window.PendingThumbnails;

            // photo.png is offered and painted, broken.png is offered and refused by the decoder, gone.png is
            // offered and answered 404. note.txt, shot.webp, the 20 MB huge.jpg and the folder are never read.
            Assert.Equal(new[] { Broken, Gone, Photo }, cloud.Reads.OrderBy(path => path, StringComparer.Ordinal).ToArray());
            Assert.NotNull(window.TileFor(window.VisibleEntries.Single(entry => entry.Path == Photo)));
            Assert.Null(window.TileFor(window.VisibleEntries.Single(entry => entry.Path == Note)));
            Assert.Null(window.TileFor(window.VisibleEntries.Single(entry => entry.Path == Webp)));
        }, new FakeServe().Holds(Photo, png).Holds(Broken, Encoding.UTF8.GetBytes("this is not a picture")),
            Listing(png.Length, 21, 2048));
    }

    [Fact]
    public async Task List_view_pulls_nothing_even_when_the_rows_are_pictures()
    {
        var png = Png();
        await WithWindow(async (window, cloud) =>
        {
            await window.PendingThumbnails;

            // The default view costs the user no bandwidth: a picture nobody asked to look at is not pulled because
            // a cache directory happens to exist.
            Assert.Empty(cloud.Reads);
            Assert.Null(window.TileFor(window.VisibleEntries.Single(entry => entry.Path == Photo)));
        }, new FakeServe().Holds(Photo, png), Listing(png.Length, 21, 2048));
    }

    [Fact]
    public async Task Switching_the_view_does_not_re_list_the_directory()
    {
        var png = Png();
        await WithWindow(async (window, cloud) =>
        {
            await window.SetBrowserViewAsync(CloudBrowserViewMode.Grid);
            await window.PendingThumbnails;
            await window.SetBrowserViewAsync(CloudBrowserViewMode.List);

            // A redraw of rows the window already holds is not a reason to ask the cloud what folder it is in: one
            // round trip per toggle is how a view choice turns into a rate limit.
            Assert.Empty(cloud.Posts);
        }, new FakeServe().Holds(Photo, png), Listing(png.Length, 21, 2048));
    }

    [Fact]
    public async Task A_new_listing_aims_the_reads_at_the_rows_now_on_screen()
    {
        var png = Png();
        await WithWindow(async (window, cloud) =>
        {
            await window.SetBrowserViewAsync(CloudBrowserViewMode.Grid);
            await window.PendingThumbnails;
            // The first round reads the three rows the listing offers and nothing else.
            Assert.Equal(new[] { Broken, Gone, Photo },
                cloud.Reads.OrderBy(path => path, StringComparer.Ordinal).ToArray());

            // The next directory holds one picture nobody has looked at, the one this user already saw, and a
            // document. Only the new picture is worth a read: the row still on screen is already decoded in this
            // window, and the two rows that scrolled away are decoration nobody is looking at any more.
            window.ShowListing("team", [Row(Other, png.Length), Row(Photo, png.Length), Row(Note, 512)]);
            await window.PendingThumbnails;

            Assert.Equal(new[] { Broken, Gone, Other, Photo },
                cloud.Reads.OrderBy(path => path, StringComparer.Ordinal).ToArray());
            Assert.NotNull(window.TileFor(window.VisibleEntries.Single(entry => entry.Path == Other)));
            Assert.NotNull(window.TileFor(window.VisibleEntries.Single(entry => entry.Path == Photo)));
        }, new FakeServe().Holds(Photo, png).Holds(Other, png), Listing(png.Length, 21, 2048));
    }

    [Fact]
    public async Task A_view_choice_is_remembered_without_overwriting_anybody_else_s_choice()
    {
        var png = Png();
        await WithWindow(async (window, cloud) =>
        {
            // Written from a different shell's point of view: the main window owns the sidebar width, and this
            // window never loaded the settings file (its Loaded handler is not reached), so a toggle that saved its
            // own snapshot would repaint that width back to the default.
            await new SettingsStore().SaveAsync(new ApplicationSettings { MainWindowSidebarWidth = 300 });

            await window.SetBrowserViewAsync(CloudBrowserViewMode.Grid);

            var saved = (await new SettingsStore().LoadAsync()).Settings;
            Assert.Equal(CloudBrowserViewMode.Grid, saved.CloudBrowserView);
            Assert.Equal(300, saved.MainWindowSidebarWidth);

            await window.SetBrowserViewAsync(CloudBrowserViewMode.List);
            Assert.Equal(CloudBrowserViewMode.List, (await new SettingsStore().LoadAsync()).Settings.CloudBrowserView);
        }, new FakeServe().Holds(Photo, png), Listing(png.Length, 21, 2048));
    }

    // ------------------------------------------------------------------ the harnesses

    /// <summary>The seven rows the window cases start with: one picture, one that fails to decode, one the cloud
    /// has dropped, a document, a format with no decoder, a picture over the cap, and a folder.</summary>
    private static IReadOnlyList<CloudFileEntry> Listing(long photoSize, long brokenSize, long goneSize) =>
    [
        Row(Photo, photoSize), Row(Note, 512), Row(Webp, 30 * 1024),
        Row(Huge, CloudBrowserTiles.ThumbnailMaxBytes + 1), Row(Broken, brokenSize), Row(Gone, goneSize),
        new("inbox", Inbox, true, 0, null),
    ];

    /// <summary>
    /// Runs one action against a store aimed at a temporary cache directory over a stand-in rclone. The cache root
    /// is handed in rather than taken from app data so a case can inspect and delete exactly the files the store
    /// wrote.
    /// </summary>
    private static Task WithStore(FakeServe serve, Func<CloudThumbnailStore, FakeServe, string, Task> action) =>
        RunSta(async () =>
        {
            var cache = Path.Combine(Path.GetTempPath(), "fengsync-tile-cache-" + Guid.NewGuid().ToString("N"));
            var client = ServeClient(serve);
            var store = new CloudThumbnailStore(new CloudFileManagerService(_ => Task.FromResult(client)), cache);
            try
            {
                await action(store, serve, cache);
            }
            finally
            {
                TryDelete(cache);
            }
        });

    /// <summary>
    /// Runs one action against the real browser window over a stand-in rclone that serves both the RC calls and the
    /// <c>--rc-serve</c> object reads. Two pieces of process-wide state are isolated for the duration: the window
    /// remembers its view in the settings file, and the tile cache lives under the application's data root, so both
    /// are pointed at a temporary directory instead of the developer's own.
    /// </summary>
    private static Task WithWindow(Func<RemoteEndpointManagerWindow, FakeServe, Task> action, FakeServe serve,
        IReadOnlyList<CloudFileEntry> listing) => RunSta(async () =>
    {
        var previousDataRoot = Environment.GetEnvironmentVariable("FENGSYNC_DATA_DIR");
        var dataRoot = Path.Combine(Path.GetTempPath(), "fengsync-grid-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        var previous = AppServices.TryCurrent;
        Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", dataRoot);
        var client = ServeClient(serve);
        var queue = new CloudTransferQueue(new CloudTransferJournalStore(Path.Combine(dataRoot, "jobs")),
            _ => Task.FromResult(client));
        RemoteEndpointManagerWindow? window = null;
        try
        {
            // The window resolves the mount service from the installed shell, and the rclone behind that shell is
            // unreachable on purpose: a test that reaches it fails loudly instead of starting a daemon.
            AppServices.Install(new StubShell(queue));
            window = new RemoteEndpointManagerWindow(new CloudFileManagerService(_ => Task.FromResult(client)), queue);
            window.LoadEndpointsForTest([EndpointRow(Remote, "team")], selectName: Remote);
            window.ShowListing("team", listing);
            await action(window, serve);
        }
        finally
        {
            AppServices.Install(previous);
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", previousDataRoot);
            try { window?.Close(); }
            catch (InvalidOperationException) { }
            TryDelete(dataRoot);
        }
    });

    private static RcloneRcClient ServeClient(FakeServe serve) =>
        new(new HttpClient(serve) { BaseAddress = new Uri("http://rc.test/") },
            new Uri("http://rc.test/"), "user", "pass");

    /// <summary>The cache files that exist. A refused row never creates the directory at all, and an empty cache
    /// and an absent one are the same answer.</summary>
    private static IReadOnlyList<string> CacheFiles(string cache) =>
        Directory.Exists(cache) ? Directory.EnumerateFiles(cache).ToList() : [];

    private static void Select(RemoteEndpointManagerWindow window, params string[] paths)
    {
        window.FileList.SelectedItems.Clear();
        foreach (var path in paths)
            window.FileList.SelectedItems.Add(window.VisibleEntries.Single(entry => entry.Path == path));
    }

    private static CloudFileEntry Row(string path, long size) =>
        new(path.Split('/').Last(), path, false, size, Stamp);

    private static CloudEndpointRow EndpointRow(string name, string root) =>
        new(new CloudEndpointAccount(new RcloneAccount(name, "sftp"),
            new CloudEndpointMetadata(name, "sftp", "SFTP", root, "")));

    /// <summary>A real PNG, encoded here rather than committed as a fixture: the decoder's own library made these
    /// bytes, which is the property a tile depends on.</summary>
    private static byte[] Png(int edge = 12)
    {
        var pixels = new byte[edge * edge * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 8;
            pixels[i + 1] = 120;
            pixels[i + 2] = 220;
            pixels[i + 3] = 255;
        }
        var source = BitmapSource.Create(edge, edge, 96, 96, PixelFormats.Bgra32, null, pixels, edge * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// A stand-in for the bundled rclone's <c>--rc-serve</c> endpoint, answering a GET for one object in the shapes
    /// the fact tests measured: a missing object is a 404 whose JSON body says <c>object not found</c>, and a held
    /// object answers 206 with its total in <c>Content-Range</c> — including a total larger than the bytes this fake
    /// holds, which is how an object that outgrew its listing is expressed without keeping 20 MB in memory. POSTs
    /// are recorded and answered as an empty listing, so a case can say a repaint did not re-list anything.
    /// </summary>
    private sealed class FakeServe : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Serving> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _reads = [];
        private readonly List<string> _posts = [];

        /// <summary>The bytes the cloud holds for one path, and the size it reports for it.</summary>
        private sealed record Serving(byte[] Bytes, long? ReportedTotal);

        public FakeServe Holds(string path, byte[] bytes, long? reportedTotal = null)
        {
            _files[path] = new Serving(bytes, reportedTotal);
            return this;
        }

        public IReadOnlyList<string> Reads
        {
            get { lock (_gate) return _reads.ToArray(); }
        }

        public IReadOnlyList<string> Posts
        {
            get { lock (_gate) return _posts.ToArray(); }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(request.Method == HttpMethod.Get ? Serve(request) : Post(request));

        private HttpResponseMessage Serve(HttpRequestMessage request)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.TrimStart('/'));
            var key = path[(path.IndexOf(']') + 1)..].TrimStart('/');
            lock (_gate) _reads.Add(key);
            if (!_files.TryGetValue(key, out var serving))
                // v1.75.0's own sentence for a missing object, not a stand-in's.
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"error":"failed to find object: object not found"}""",
                        Encoding.UTF8, "application/json")
                };

            var bytes = serving.Bytes;
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            var from = (int)(range?.From ?? 0);
            var to = (int)Math.Min(range?.To ?? bytes.Length - 1, bytes.Length - 1);
            var slice = bytes[from..(to + 1)];
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(slice)
            };
            response.Content.Headers.ContentLength = slice.Length;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, serving.ReportedTotal ?? bytes.Length);
            return response;
        }

        private HttpResponseMessage Post(HttpRequestMessage request)
        {
            var operation = request.RequestUri!.AbsolutePath.TrimStart('/');
            lock (_gate) _posts.Add(operation);
            // The rows under test are installed directly, so this only has to be a valid answer; what matters is
            // that a toggle sends nothing at all.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"list":[]}""", Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>
    /// Runs one action against a window on its own STA thread, with the dispatcher pumped while the action is in
    /// flight. The dispatcher is shut down explicitly because the process allows only one WPF
    /// <see cref="Application"/>, and a run that stops making progress is cut off by a watchdog.
    /// </summary>
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
                }, CancellationToken.None, TaskContinuationOptions.None,
                    TaskScheduler.FromCurrentSynchronizationContext());
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
                failure = new TimeoutException("网格视图测试没有在规定时间内完成（窗口线程被卡住了）。");
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

    /// <summary>Gives this thread an Application to resolve a window's theme dictionaries through, and says whether
    /// the thread also owns it.</summary>
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

    /// <summary>Just enough of a shell to build the window; nothing here owns a daemon or a journal.</summary>
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

    /// <summary>Windows can hold a directory a few milliseconds after its last handle closes.</summary>
    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
