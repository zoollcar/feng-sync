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
using FengSync.Core.Mount;
using FengSync.Core.Rclone.Lifecycle;
using FengSync.Services;
using FengSync.Views;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// Looking at one file without downloading it. The capability is a refusal by default — this program paints a
/// picture when WPF can decode it, paints text when the bytes are text it can name the encoding of, and otherwise
/// says which of those two things went wrong — so the tests are mostly about the refusals, and each distinct state
/// is asserted as itself rather than as "no preview".
/// <para>
/// The cloud side is the same stand-in the grid view uses, answering a <c>--rc-serve</c> GET in the shapes
/// <c>RcloneServeReadFactTests</c> measured against the bundled v1.75.0: a missing object is a 404 saying
/// <c>object not found</c>, and a held object is a 206 whose <c>Content-Range</c> carries the total — which is how
/// "the object turned out bigger than the listing said" is expressed without holding 40 MB in memory.
/// </para>
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudBrowserPreviewTests
{
    private const string Remote = "team";
    private const string Photo = "team/photo.png";
    private const string Note = "team/note.txt";
    private const string Webp = "team/shot.webp";
    private const string Report = "team/report.pdf";
    private const string Mystery = "team/mystery.qqq";
    private const string Nameless = "team/LICENSE";
    private const string Inbox = "team/inbox";

    private static readonly DateTimeOffset Stamp = new(2026, 5, 4, 6, 30, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ the rules

    [Fact]
    public void Only_a_picture_or_a_text_row_is_offered_the_preview_command()
    {
        Assert.Equal(CloudPreviewOutcome.Picture, CloudBrowserPreview.DecideFor(Row(Photo, 40 * 1024)));
        Assert.Equal(CloudPreviewOutcome.Text, CloudBrowserPreview.DecideFor(Row(Note, 1024)));
        Assert.True(CloudBrowserPreview.OffersPreview(Row(Photo, 40 * 1024)));
        Assert.True(CloudBrowserPreview.OffersPreview(Row(Note, 1024)));

        Assert.Equal(CloudPreviewOutcome.Folder, CloudBrowserPreview.DecideFor(Row(Inbox, 0, isDirectory: true)));
        Assert.Equal(CloudPreviewOutcome.PictureWithoutDecoder, CloudBrowserPreview.DecideFor(Row(Webp, 40 * 1024)));
        Assert.Equal(CloudPreviewOutcome.NoRenderer, CloudBrowserPreview.DecideFor(Row(Report, 40 * 1024)));
        Assert.Equal(CloudPreviewOutcome.Unrecognized, CloudBrowserPreview.DecideFor(Row(Mystery, 40 * 1024)));

        foreach (var refused in new[] { Inbox, Webp, Report, Mystery })
            Assert.False(CloudBrowserPreview.OffersPreview(Row(refused, 40 * 1024)), $"{refused} 不该拿到预览命令。");
    }

    [Fact]
    public void Each_kind_has_its_own_read_ceiling_and_a_row_refused_up_front_costs_no_read()
    {
        Assert.Equal(CloudBrowserPreview.PictureMaxBytes, CloudBrowserPreview.ReadCapFor(CloudPreviewOutcome.Picture));
        Assert.Equal(CloudBrowserPreview.TextMaxBytes, CloudBrowserPreview.ReadCapFor(CloudPreviewOutcome.Text));
        foreach (var never in new[]
                     {
                         CloudPreviewOutcome.Folder, CloudPreviewOutcome.NoRenderer, CloudPreviewOutcome.Unrecognized,
                         CloudPreviewOutcome.PictureWithoutDecoder, CloudPreviewOutcome.TooLarge
                     })
            Assert.Equal(0L, CloudBrowserPreview.ReadCapFor(never));

        // The name is the only thing left once a row has been decided TooLarge, and the sentence still has to say
        // which ceiling it crossed.
        Assert.Equal(CloudBrowserPreview.PictureMaxBytes, CloudBrowserPreview.CapForName("a.png"));
        Assert.Equal(CloudBrowserPreview.TextMaxBytes, CloudBrowserPreview.CapForName("a.md"));
        Assert.Equal(0L, CloudBrowserPreview.CapForName("a.pdf"));
    }

    [Fact]
    public void A_row_the_listing_already_says_is_over_the_ceiling_is_refused_before_anything_is_read()
    {
        Assert.Equal(CloudPreviewOutcome.TooLarge,
            CloudBrowserPreview.DecideFor(Row(Photo, CloudBrowserPreview.PictureMaxBytes + 1)));
        Assert.Equal(CloudPreviewOutcome.TooLarge,
            CloudBrowserPreview.DecideFor(Row(Note, CloudBrowserPreview.TextMaxBytes + 1)));
        Assert.False(CloudBrowserPreview.OffersPreview(Row(Photo, CloudBrowserPreview.PictureMaxBytes + 1)));

        var sentence = CloudBrowserPreview.ReasonFor(CloudPreviewOutcome.TooLarge,
            Row(Note, CloudBrowserPreview.TextMaxBytes + 1));
        Assert.Contains(CloudItemDetails.HumanSize(CloudBrowserPreview.TextMaxBytes), sentence);
        Assert.Contains("note.txt", sentence);
    }

    [Fact]
    public void A_row_with_no_bytes_is_empty_rather_than_a_file_waiting_to_be_read()
    {
        Assert.Equal(CloudPreviewOutcome.Empty, CloudBrowserPreview.DecideFor(Row(Photo, 0)));
        Assert.Equal(CloudPreviewOutcome.Empty, CloudBrowserPreview.DecideFor(Row(Note, -1)));
        Assert.False(CloudBrowserPreview.OffersPreview(Row(Note, 0)));
    }

    [Fact]
    public void Every_refusal_names_the_thing_that_made_it_a_refusal()
    {
        Assert.Contains("文件夹", CloudBrowserPreview.ReasonFor(CloudPreviewOutcome.Folder, Row(Inbox, 0)));
        Assert.Contains(".webp", CloudBrowserPreview.ReasonFor(CloudPreviewOutcome.PictureWithoutDecoder, Row(Webp, 40 * 1024)));
        Assert.Contains("pdf", CloudBrowserPreview.ReasonFor(CloudPreviewOutcome.NoRenderer, Row(Report, 40 * 1024)));
        Assert.Contains(".qqq", CloudBrowserPreview.ReasonFor(CloudPreviewOutcome.Unrecognized, Row(Mystery, 16)));
        Assert.Contains("没有后缀", CloudBrowserPreview.ReasonFor(CloudPreviewOutcome.Unrecognized, Row(Nameless, 16)));
        Assert.Contains("云端已没有", CloudBrowserPreview.ReasonFor(CloudPreviewOutcome.Missing, Row(Photo, 40 * 1024)));
        Assert.Contains("0 字节", CloudBrowserPreview.ReasonFor(CloudPreviewOutcome.Binary, Row(Note, 1024)));
        Assert.Contains("UTF-8", CloudBrowserPreview.ReasonFor(CloudPreviewOutcome.NotUtf8, Row(Note, 1024)));
        Assert.Contains("损坏", CloudBrowserPreview.ReasonFor(CloudPreviewOutcome.UndecodableBytes, Row(Photo, 1024)));
        // rclone's own sentence, not a paraphrase of it.
        Assert.Contains("server gave HTTP response",
            CloudBrowserPreview.ReasonFor(CloudPreviewOutcome.Failed, Row(Photo, 1024), -1, "server gave HTTP response to HTTPS client"));
    }

    [Fact]
    public void A_byte_order_mark_a_NUL_and_an_undecodable_byte_sequence_are_three_different_answers()
    {
        var (utf8State, utf8Text, utf8Label) = CloudBrowserPreview.ExtractText(
            [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("配置")]);
        Assert.Equal(CloudPreviewOutcome.Text, utf8State);
        Assert.Equal("配置", utf8Text);
        Assert.Equal("UTF-8", utf8Label);

        var (plainState, plainText, plainLabel) = CloudBrowserPreview.ExtractText(Encoding.UTF8.GetBytes("hello\n"));
        Assert.Equal(CloudPreviewOutcome.Text, plainState);
        Assert.Equal("hello\n", plainText);
        Assert.Equal("UTF-8", plainLabel);

        var (leState, leText, leLabel) = CloudBrowserPreview.ExtractText(
            [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("日志")]);
        Assert.Equal(CloudPreviewOutcome.Text, leState);
        Assert.Equal("日志", leText);
        Assert.Equal("UTF-16 LE", leLabel);

        var (beState, beText, beLabel) = CloudBrowserPreview.ExtractText(
            [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("log")]);
        Assert.Equal(CloudPreviewOutcome.Text, beState);
        Assert.Equal("log", beText);
        Assert.Equal("UTF-16 BE", beLabel);

        // A binary file wearing a text extension: the NUL is the finding, and it is not shown as text.
        Assert.Equal(CloudPreviewOutcome.Binary,
            CloudBrowserPreview.ExtractText([0x00, 0x01, 0x02, 0x00]).Outcome);
        // The first byte of a two-byte UTF-8 sequence followed by something that cannot continue it. Guessing a
        // code page here would produce question marks that look like file damage.
        Assert.Equal(CloudPreviewOutcome.NotUtf8, CloudBrowserPreview.ExtractText([0xC3, 0x28, 0xC3]).Outcome);
    }

    [Fact]
    public void The_cloud_s_own_answer_outranks_the_listing_snapshot()
    {
        var photo = CloudBrowserPreview.DecideFor(Row(Photo, 40 * 1024));
        Assert.Equal(CloudPreviewOutcome.TooLarge,
            CloudBrowserPreview.OutcomeOf(photo, new RcloneObjectRead(RcloneReadStatus.TooLarge, [], 40L * 1024 * 1024, null)));
        Assert.Equal(CloudPreviewOutcome.Missing,
            CloudBrowserPreview.OutcomeOf(photo, new RcloneObjectRead(RcloneReadStatus.Missing, [], -1, null)));
        Assert.Equal(CloudPreviewOutcome.Failed,
            CloudBrowserPreview.OutcomeOf(photo, new RcloneObjectRead(RcloneReadStatus.Failed, [], -1, "refused")));
        // An object the listing said had bytes and the cloud answered with none is still empty, not a painted file.
        Assert.Equal(CloudPreviewOutcome.Empty,
            CloudBrowserPreview.OutcomeOf(photo, new RcloneObjectRead(RcloneReadStatus.Read, [], 0, null)));
        Assert.Equal(CloudPreviewOutcome.Picture,
            CloudBrowserPreview.OutcomeOf(photo, new RcloneObjectRead(RcloneReadStatus.Read, [1, 2, 3], 3, null)));

        // The size the cloud reported is the one the sentence quotes, because the row's own number is stale.
        var sentence = CloudBrowserPreview.ReasonFor(CloudPreviewOutcome.TooLarge, Row(Photo, 40 * 1024), 40L * 1024 * 1024);
        Assert.Contains(CloudItemDetails.HumanSize(40L * 1024 * 1024), sentence);
    }

    [Fact]
    public async Task Bytes_no_decoder_claims_are_answered_as_damaged_rather_than_as_an_absent_picture()
    {
        await RunSta(() =>
        {
            var png = Png();
            Assert.NotNull(CloudBrowserPreview.DecodePicture(png));
            // Decoding is what costs, so the state that follows has to come from the same bytes the panel would
            // have painted — a fake that only looks like a picture is exactly the case the user reports as
            // "双击打不开".
            Assert.Null(CloudBrowserPreview.DecodePicture(Encoding.UTF8.GetBytes("this is not a picture")));
            Assert.Null(CloudBrowserPreview.DecodePicture([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
            return Task.CompletedTask;
        });
    }

    // ------------------------------------------------------------------ the window

    [Fact]
    public async Task A_picture_row_is_read_through_the_bounded_path_and_painted()
    {
        var png = Png();
        await WithWindow(async (window, cloud) =>
        {
            var panel = await window.PreviewAsync(window.VisibleEntries.Single(entry => entry.Path == Photo));

            Assert.Equal(CloudPreviewOutcome.Picture, panel.Outcome);
            Assert.NotNull(panel.Image);
            Assert.Null(panel.Message);
            Assert.Equal([Photo], cloud.Reads);
            // One bounded read for the object the user pointed at, sent while the browser is still in its default
            // list view: a preview does not need the grid, and does not re-list the directory either.
            Assert.Empty(cloud.Posts);
        }, new FakeServe().Holds(Photo, png), Listing());
    }

    [Fact]
    public async Task A_second_preview_asks_the_cloud_again_because_the_panel_keeps_nothing()
    {
        var png = Png();
        await WithWindow(async (window, cloud) =>
        {
            var entry = window.VisibleEntries.Single(e => e.Path == Photo);
            Assert.NotNull((await window.PreviewAsync(entry)).Image);
            Assert.NotNull((await window.PreviewAsync(entry)).Image);

            // Deliberate: the tile cache holds decoration the app chose, and a preview is the object itself, which
            // may have changed. Keeping a full-size copy beside the tiles would make this window a second content
            // cache nobody designed.
            Assert.Equal(2, cloud.Reads.Count);
        }, new FakeServe().Holds(Photo, png), Listing());
    }

    [Fact]
    public async Task A_text_row_is_painted_with_the_encoding_it_was_read_as()
    {
        await WithWindow(async (window, cloud) =>
        {
            var panel = await window.PreviewAsync(window.VisibleEntries.Single(entry => entry.Path == Note));

            Assert.Equal(CloudPreviewOutcome.Text, panel.Outcome);
            Assert.Equal("第一行\n第二行\n", panel.Content);
            Assert.Equal("UTF-8", panel.TextEncoding);
            Assert.Null(panel.Message);
            Assert.Equal([Note], cloud.Reads);
        }, new FakeServe().Holds(Note, Encoding.UTF8.GetBytes("第一行\n第二行\n")), Listing());
    }

    [Fact]
    public async Task A_row_that_cannot_be_previewed_costs_the_user_no_bandwidth()
    {
        await WithWindow(async (window, cloud) =>
        {
            foreach (var (path, outcome) in new[]
                     {
                         (Report, CloudPreviewOutcome.NoRenderer),
                         (Webp, CloudPreviewOutcome.PictureWithoutDecoder),
                         (Mystery, CloudPreviewOutcome.Unrecognized),
                         (Inbox, CloudPreviewOutcome.Folder)
                     })
            {
                var panel = await window.PreviewAsync(window.VisibleEntries.Single(entry => entry.Path == path));
                Assert.Equal(outcome, panel.Outcome);
                Assert.NotNull(panel.Message);
                Assert.True(panel.Message!.Length > 0, $"{path} 的拒绝要说清原因，不能是空话。");
            }

            // The refusal is decided from the row's own name, so a format that cannot be rendered never reaches
            // the daemon at all — that is the difference between "not supported" and "not yet downloaded".
            Assert.Empty(cloud.Reads);
        }, new FakeServe(), Listing());
    }

    [Fact]
    public async Task A_previewed_picture_leaves_the_thumbnail_cache_alone()
    {
        var png = Png();
        await WithWindow(async (window, cloud) =>
        {
            Assert.NotNull((await window.PreviewAsync(window.VisibleEntries.Single(entry => entry.Path == Photo))).Image);

            // The two paths may look alike — both ask the same bounded read for the same object — and mixing them
            // would let a throwaway decoration cache serve the file itself later.
            Assert.False(Directory.Exists(AppDataPaths.CloudThumbnails),
                "预览不该把整份文件写进缩略图缓存。");
        }, new FakeServe().Holds(Photo, png), Listing());
    }

    [Fact]
    public async Task An_object_that_turned_out_bigger_than_the_ceiling_is_reported_with_the_size_the_cloud_gave()
    {
        var png = Png();
        await WithWindow(async (window, cloud) =>
        {
            var panel = await window.PreviewAsync(window.VisibleEntries.Single(entry => entry.Path == Photo));

            Assert.Equal(CloudPreviewOutcome.TooLarge, panel.Outcome);
            Assert.Contains(CloudItemDetails.HumanSize(CloudBrowserPreview.PictureMaxBytes + 1), panel.Message);
        }, new FakeServe().Holds(Photo, png, reportedTotal: CloudBrowserPreview.PictureMaxBytes + 1), Listing());
    }

    [Fact]
    public async Task An_object_the_cloud_has_dropped_is_called_gone_and_a_refusal_is_quoted()
    {
        await WithWindow(async (window, cloud) =>
        {
            var panel = await window.PreviewAsync(window.VisibleEntries.Single(entry => entry.Path == Photo));

            Assert.Equal(CloudPreviewOutcome.Missing, panel.Outcome);
            Assert.Contains("云端已没有", panel.Message);
            // The row stays a row: a stale listing is an explanation, not a crash and not an empty panel.
            Assert.Equal([Photo], cloud.Reads);
        }, new FakeServe(), Listing());
    }

    [Fact]
    public async Task Picture_bytes_that_are_not_a_picture_are_named_instead_of_painted_as_nothing()
    {
        await WithWindow(async (window, cloud) =>
        {
            var panel = await window.PreviewAsync(window.VisibleEntries.Single(entry => entry.Path == Photo));

            Assert.Equal(CloudPreviewOutcome.UndecodableBytes, panel.Outcome);
            Assert.Null(panel.Image);
            Assert.Contains("损坏", panel.Message);
            Assert.Equal([Photo], cloud.Reads);
        }, new FakeServe().Holds(Photo, Encoding.UTF8.GetBytes("this is not a picture")), Listing());
    }

    [Fact]
    public async Task A_binary_file_wearing_a_text_extension_is_refused_by_what_was_found_in_it()
    {
        await WithWindow(async (window, cloud) =>
        {
            var panel = await window.PreviewAsync(window.VisibleEntries.Single(entry => entry.Path == Note));

            Assert.Equal(CloudPreviewOutcome.Binary, panel.Outcome);
            Assert.Null(panel.Content);
            Assert.Contains("0 字节", panel.Message);
        }, new FakeServe().Holds(Note, [0x00, 0x01, 0x02, 0x03]), Listing());
    }

    [Fact]
    public async Task Text_in_a_code_page_the_program_will_not_guess_is_refused_rather_than_shown_as_question_marks()
    {
        await WithWindow(async (window, cloud) =>
        {
            var panel = await window.PreviewAsync(window.VisibleEntries.Single(entry => entry.Path == Note));

            Assert.Equal(CloudPreviewOutcome.NotUtf8, panel.Outcome);
            Assert.Null(panel.Content);
            Assert.Contains("UTF-8", panel.Message);
        }, new FakeServe().Holds(Note, [0xC3, 0x28, 0xC3, 0x28]), Listing());
    }

    [Fact]
    public async Task The_preview_command_is_offered_only_for_a_row_it_can_paint()
    {
        await WithWindow(async (window, cloud) =>
        {
            Select(window, Photo);
            Assert.Contains("预览", Headers(window.BuildRowMenu()));
            // The two routes stay reachable: 打开 is the external one, and a row that can be previewed can still
            // be downloaded.
            Assert.Contains("打开", Headers(window.BuildRowMenu()));

            Select(window, Report);
            Assert.DoesNotContain("预览", Headers(window.BuildRowMenu()));
            Assert.Contains("打开", Headers(window.BuildRowMenu()));

            Select(window, Inbox);
            // A folder's menu opens it; 预览 for a folder would only ever say "this is a folder".
            Assert.DoesNotContain("预览", Headers(window.BuildRowMenu()));
            Assert.Empty(cloud.Reads);

            // The command and the double-click run the same method, so the row this menu offered is the row the
            // read proves was real.
            Assert.NotNull((await window.PreviewAsync(window.VisibleEntries.Single(entry => entry.Path == Photo))).Image);
            Assert.Equal([Photo], cloud.Reads);
        }, new FakeServe().Holds(Photo, Png()), Listing());
    }

    [Fact]
    public async Task A_preview_of_a_row_the_listing_calls_huge_never_reaches_the_cloud()
    {
        await WithWindow(async (window, cloud) =>
        {
            // The grid's own 20 MB photo, and the 300 KB text row: both are decided from the listing's size, and
            // a read spent on a file that will be refused anyway is bandwidth the user was never going to see.
            var huge = window.VisibleEntries.Single(entry => entry.Path == Huge);
            var panel = await window.PreviewAsync(huge);
            Assert.Equal(CloudPreviewOutcome.TooLarge, panel.Outcome);
            Select(window, Huge);
            Assert.DoesNotContain("预览", Headers(window.BuildRowMenu()));

            var big = window.VisibleEntries.Single(entry => entry.Path == LongNote);
            Assert.Equal(CloudPreviewOutcome.TooLarge, (await window.PreviewAsync(big)).Outcome);
            Assert.Empty(cloud.Reads);
        }, new FakeServe().Holds(Huge, new byte[1024]).Holds(LongNote, new byte[1024]),
            Listing(CloudBrowserPreview.PictureMaxBytes + 1, CloudBrowserPreview.TextMaxBytes + 1));
    }

    // ------------------------------------------------------------------ the harnesses

    private const string Huge = "team/huge.jpg";
    private const string LongNote = "team/long.txt";

    /// <summary>The rows the preview cases start with: a picture, a text file, an undecodable picture format, a
    /// document, an unknown extension, a nameless file and a folder.</summary>
    private static IReadOnlyList<CloudFileEntry> Listing(long hugeSize = 0, long longNoteSize = 0) =>
    [
        Row(Photo, 40 * 1024), Row(Note, 2 * 1024), Row(Webp, 40 * 1024), Row(Report, 90 * 1024),
        Row(Mystery, 16), Row(Inbox, 0, isDirectory: true),
        hugeSize > 0 ? Row(Huge, hugeSize) : Row(Huge, 20L * 1024 * 1024),
        longNoteSize > 0 ? Row(LongNote, longNoteSize) : Row(LongNote, 4 * 1024)
    ];

    /// <summary>
    /// Runs one action against the real browser window over a stand-in that serves both the RC calls and the
    /// <c>--rc-serve</c> object reads. The data root is redirected because the refusal cases leave the window with
    /// a settings file to write, and one case checks that the tile cache stays absent.
    /// </summary>
    private static Task WithWindow(Func<RemoteEndpointManagerWindow, FakeServe, Task> action, FakeServe serve,
        IReadOnlyList<CloudFileEntry> listing) => RunSta(async () =>
    {
        var previousDataRoot = Environment.GetEnvironmentVariable("FENGSYNC_DATA_DIR");
        var dataRoot = Path.Combine(Path.GetTempPath(), "fengsync-preview-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        var previous = AppServices.TryCurrent;
        Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", dataRoot);
        var client = ServeClient(serve);
        var queue = new CloudTransferQueue(new CloudTransferJournalStore(Path.Combine(dataRoot, "jobs")),
            _ => Task.FromResult(client));
        RemoteEndpointManagerWindow? window = null;
        try
        {
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

    private static void Select(RemoteEndpointManagerWindow window, params string[] paths)
    {
        window.FileList.SelectedItems.Clear();
        foreach (var path in paths)
            window.FileList.SelectedItems.Add(window.VisibleEntries.Single(entry => entry.Path == path));
    }

    private static IReadOnlyList<string> Headers(ContextMenu? menu) =>
        menu?.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? "").ToArray() ?? [];

    private static CloudFileEntry Row(string path, long size, bool isDirectory = false) =>
        new(path.Split('/').Last(), path, isDirectory, size, Stamp);

    private static CloudEndpointRow EndpointRow(string name, string root) =>
        new(new CloudEndpointAccount(new RcloneAccount(name, "sftp"),
            new CloudEndpointMetadata(name, "sftp", "SFTP", root, "")));

    /// <summary>A real PNG, encoded by the decoder's own library rather than committed as a fixture.</summary>
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
    /// the fact tests measured against v1.75.0: a missing object is a 404 whose body says <c>object not found</c>,
    /// and a held object is a 206 carrying its total in <c>Content-Range</c> — including a total larger than the
    /// bytes held here, which is how an object bigger than the preview ceiling is expressed without keeping 32 MB
    /// in memory.
    /// </summary>
    private sealed class FakeServe : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Serving> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _reads = [];
        private readonly List<string> _posts = [];

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
            lock (_gate) _posts.Add(request.RequestUri!.AbsolutePath.TrimStart('/'));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"list":[]}""", Encoding.UTF8, "application/json")
            };
        }
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

    /// <summary>
    /// Runs one action against a window on its own STA thread, with the dispatcher pumped while the action is in
    /// flight, and rethrows whatever it threw. The dispatcher is shut down explicitly because the process allows
    /// only one WPF <see cref="Application"/>.
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
                failure = new TimeoutException("预览测试没有在规定时间内完成（窗口线程被卡住了）。");
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

    /// <summary>Windows can hold a directory a few milliseconds after its last handle closes.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
