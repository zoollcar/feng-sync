using FengSync.Core;
using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// Covers <see cref="LanReceiveSink"/>, the only code in the feature that writes a peer's bytes onto the
/// user's disk.
/// </summary>
/// <remarks>
/// The tests are built around the two promises the sink makes: an interrupted receive leaves nothing that
/// looks like a file, and a name the user already has is never replaced. Both are asserted by looking at the
/// directory afterwards rather than at a return value, because the whole point is what survives on disk.
/// The collision rule itself belongs to <c>CloudFileNames.KeepBothName</c> in the WPF project and is injected
/// here as a stand-in — these tests prove the sink asks, hands over the right listing, and honours the answer.
/// </remarks>
public sealed class LanReceiveSinkTests : IDisposable
{
    private readonly string _root;
    private readonly string _receive;

    public LanReceiveSinkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fengsync-lan-sink-" + Guid.NewGuid().ToString("N"));
        _receive = Path.Combine(_root, "in");
        Directory.CreateDirectory(_receive);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    /// <summary>The stand-in for the application's one naming rule: append (1), (2), … until free.</summary>
    private static string AppendCounter(string name, IReadOnlyList<string> taken)
    {
        var dot = name.LastIndexOf('.');
        var stem = dot < 0 ? name : name[..dot];
        var extension = dot < 0 ? "" : name[dot..];
        for (var attempt = 1; attempt <= 99; attempt++)
        {
            var candidate = $"{stem} ({attempt}){extension}";
            if (!taken.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return candidate;
        }
        throw new InvalidOperationException("测试内的改名规则用尽了。");
    }

    private LanReceiveSink Sink(Func<string, IReadOnlyList<string>, string>? chooseFreeName = null, bool verify = true) =>
        new(_receive, chooseFreeName ?? AppendCounter, verify);

    private static Stream Body(string content) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
    private static string ContentOf(string path) => System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path));

    private static readonly DateTimeOffset OldStamp = new(2021, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [Fact]
    public async Task A_file_lands_under_the_name_it_was_sent_with_and_leaves_no_staging_behind()
    {
        var received = await Sink().ReceiveAsync(Body("hello"), "a.txt", 5, OldStamp, null, CancellationToken.None);

        Assert.Equal("a.txt", received.Name);
        Assert.False(received.Renamed);
        Assert.Equal("a.txt", received.RelativePath);
        Assert.Equal(5, received.Bytes);
        Assert.Equal(Path.Combine(_receive, "a.txt"), received.AbsolutePath);
        Assert.Equal("hello", ContentOf(received.AbsolutePath));
        // The partial name is the one thing that must not outlive the transfer: a scan of this directory would
        // hide it, and a user would never find what deleted their half-written file.
        Assert.Empty(Directory.GetFiles(_receive, "*" + SyncInternalPaths.TransferTemporarySuffix));
    }

    [Fact]
    public async Task Two_senders_of_one_path_take_turns_and_the_second_renames_instead_of_sharing_a_staging_file()
    {
        // A deterministic staging name is what makes an interrupted receive recognisable, so the price of it is
        // that nobody else may hold that name at the same time. Without the gate the two bodies interleave into
        // one partial file and whichever publishes last wins with no error for either sender.
        var sink = Sink();
        // Started on pool threads because an uncontended MemoryStream read completes synchronously, and a test
        // that never actually overlaps two transfers proves nothing about the gate.
        var first = Task.Run(() => sink.ReceiveAsync(new ChunkedStream("first!", 2), "shared.txt", 6, OldStamp, null, CancellationToken.None));
        var second = Task.Run(() => sink.ReceiveAsync(new ChunkedStream("second!", 2), "shared.txt", 7, OldStamp, null, CancellationToken.None));

        var pair = await Task.WhenAll(first, second);
        var names = pair.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();

        Assert.Equal(["shared (1).txt", "shared.txt"], names);
        Assert.Equal(2, Directory.GetFiles(_receive).Length);
        Assert.Empty(Directory.GetFiles(_receive, "*" + SyncInternalPaths.TransferTemporarySuffix));
    }

    [Fact]
    public async Task A_nested_path_creates_the_parents_it_needs_inside_the_receive_directory()
    {
        var received = await Sink().ReceiveAsync(Body("x"), "docs/deep/nest/a.txt", 1, OldStamp, null, CancellationToken.None);

        Assert.Equal("docs/deep/nest/a.txt", received.RelativePath);
        Assert.True(File.Exists(Path.Combine(_receive, "docs", "deep", "nest", "a.txt")));
        // Forward slashes are the wire form; the platform separator only ever appears in the absolute path.
        Assert.Equal(Path.Combine(_receive, "docs", "deep", "nest", "a.txt"), received.AbsolutePath);
    }

    [Fact]
    public async Task The_reported_mtime_survives_the_publish_because_it_is_set_before_the_move()
    {
        // The ordering is the entire test: a move keeps the staging file's timestamp, so a receive that set
        // the stamp after publishing would have to re-touch a file the user can already open, and one that
        // never set it would show every incoming file as having arrived just now.
        var received = await Sink().ReceiveAsync(Body("payload"), "stamp.txt", 7, OldStamp, null, CancellationToken.None);

        var actual = new FileInfo(received.AbsolutePath).LastWriteTimeUtc;
        Assert.True((actual - OldStamp.UtcDateTime).Duration() <= LanReceiveSink.TimestampTolerance,
            $"落地时间为 {actual:O}，发送方声明为 {OldStamp.UtcDateTime:O}");
    }

    [Fact]
    public async Task An_empty_mtime_is_accepted_and_the_landed_time_is_left_as_the_filesystem_wrote_it()
    {
        var received = await Sink().ReceiveAsync(Body("now"), "plain.txt", 3, null, null, CancellationToken.None);

        Assert.True(File.Exists(received.AbsolutePath));
        Assert.NotEqual(default, new FileInfo(received.AbsolutePath).LastWriteTimeUtc);
    }

    [Fact]
    public async Task An_interrupted_body_leaves_neither_a_file_nor_a_staging_object()
    {
        var sink = Sink();
        // Returns some bytes and then fails, which is what a phone dropping off the network looks like from
        // the server side — not an exception the reader can distinguish from a client that never sent anything.
        var body = new TruncatedStream("parti");

        var error = await Assert.ThrowsAsync<IOException>(async () =>
            await sink.ReceiveAsync(body, "half.txt", 100, OldStamp, null, CancellationToken.None));

        Assert.Contains("连接中断", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_receive, "half.txt")));
        Assert.False(File.Exists(Path.Combine(_receive, "half.txt" + SyncInternalPaths.TransferTemporarySuffix)));
        Assert.Empty(Directory.GetFileSystemEntries(_receive));
    }

    [Fact]
    public async Task A_cancelled_body_leaves_neither_a_file_nor_a_staging_object()
    {
        var sink = Sink();
        using var cancellation = new CancellationTokenSource();
        var body = new SelfCancellingStream("half", cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sink.ReceiveAsync(body, "cancelled.txt", 100, OldStamp, null, cancellation.Token));

        Assert.False(File.Exists(Path.Combine(_receive, "cancelled.txt")));
        Assert.False(File.Exists(Path.Combine(_receive, "cancelled.txt" + SyncInternalPaths.TransferTemporarySuffix)));
        Assert.Empty(Directory.GetFileSystemEntries(_receive));
    }

    [Fact]
    public async Task A_body_shorter_than_declared_is_refused_and_nothing_is_published()
    {
        var sink = Sink();
        var error = await Assert.ThrowsAsync<LanTransferException>(async () =>
            await sink.ReceiveAsync(Body("short"), "short.txt", 999, null, null, CancellationToken.None));

        Assert.Equal(LanRefusal.IncompleteBody, error.Refusal);
        Assert.False(File.Exists(Path.Combine(_receive, "short.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(_receive));
    }

    [Fact]
    public async Task A_body_longer_than_declared_stops_instead_of_filling_the_disk()
    {
        var sink = Sink();
        var error = await Assert.ThrowsAsync<LanTransferException>(async () =>
            await sink.ReceiveAsync(Body("far too many bytes for the promise"), "lying.txt", 4, null, null, CancellationToken.None));

        Assert.Equal(LanRefusal.IncompleteBody, error.Refusal);
        Assert.Empty(Directory.GetFileSystemEntries(_receive));
    }

    [Fact]
    public async Task A_taken_name_goes_through_the_injected_rule_with_the_destination_directory_s_own_listing()
    {
        // Two same-named files under two different parents are two different problems: a name free at the
        // receive root is not free inside docs, and a sink that consulted one listing for both would drop the
        // second file onto the first.
        await Sink().ReceiveAsync(Body("root"), "report.txt", 4, OldStamp, null, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(_receive, "docs"));
        await Sink().ReceiveAsync(Body("docs"), "docs/report.txt", 4, OldStamp, null, CancellationToken.None);

        var renamed = await Sink().ReceiveAsync(Body("again"), "docs/report.txt", 5, OldStamp, null, CancellationToken.None);

        Assert.True(renamed.Renamed);
        Assert.Equal("report (1).txt", renamed.Name);
        Assert.Equal("docs/report (1).txt", renamed.RelativePath);
        Assert.Equal("again", ContentOf(renamed.AbsolutePath));
        // The two originals are untouched, so the rename was driven by docs' own listing.
        Assert.Equal("docs", ContentOf(Path.Combine(_receive, "docs", "report.txt")));
        Assert.Equal("root", ContentOf(Path.Combine(_receive, "report.txt")));
    }

    [Fact]
    public async Task A_directory_holding_the_wanted_name_counts_as_taken_and_never_merges()
    {
        Directory.CreateDirectory(Path.Combine(_receive, "notes"));

        var renamed = await Sink().ReceiveAsync(Body("x"), "notes", 1, OldStamp, null, CancellationToken.None);

        Assert.True(renamed.Renamed);
        Assert.Equal("notes (1)", renamed.Name);
        Assert.True(File.Exists(Path.Combine(_receive, "notes (1)")));
        Assert.True(Directory.Exists(Path.Combine(_receive, "notes")));
    }

    [Fact]
    public async Task A_staging_file_left_by_a_crash_does_not_hold_the_name()
    {
        // The sink's own partial object is not the user's file. Counting it as taken would rename a correct
        // receive because of a dead transfer's litter, and the counter would then name a file nobody sent.
        await File.WriteAllTextAsync(Path.Combine(_receive, "a.txt"), "real", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_receive, "a.txt" + SyncInternalPaths.TransferTemporarySuffix), "orphan", CancellationToken.None);
        IReadOnlyList<string>? handedOver = null;
        Func<string, IReadOnlyList<string>, string> rule = (name, taken) =>
        {
            handedOver = taken;
            return AppendCounter(name, taken);
        };

        var received = await Sink(rule).ReceiveAsync(Body("again"), "a.txt", 5, OldStamp, null, CancellationToken.None);

        // The rule only runs for a name that is genuinely taken, so its being called is itself part of the claim.
        Assert.NotNull(handedOver);
        Assert.Contains("a.txt", handedOver!);
        Assert.False(handedOver!.Any(name => name.EndsWith(SyncInternalPaths.TransferTemporarySuffix, StringComparison.OrdinalIgnoreCase)),
            $"改名规则看到了暂存文件：{string.Join(", ", handedOver)}");
        Assert.True(received.Renamed);
        Assert.Equal("a (1).txt", received.Name);
        Assert.Equal("again", ContentOf(received.AbsolutePath));
        Assert.Equal("real", ContentOf(Path.Combine(_receive, "a.txt")));
    }

    [Fact]
    public async Task A_name_the_rule_cannot_free_is_a_conflict_and_leaves_the_existing_file_alone()
    {
        await Sink().ReceiveAsync(Body("mine"), "keep.txt", 4, OldStamp, null, CancellationToken.None);

        // The rule answering with the same name means "I have no room here", which is the sender's problem to
        // solve — not a licence to overwrite a file the user already has.
        var sink = Sink((name, _) => name);
        var error = await Assert.ThrowsAsync<LanTransferException>(async () =>
            await sink.ReceiveAsync(Body("other"), "keep.txt", 5, OldStamp, null, CancellationToken.None));

        Assert.Equal(LanRefusal.Conflict, error.Refusal);
        Assert.Equal("mine", ContentOf(Path.Combine(_receive, "keep.txt")));
        Assert.Empty(Directory.GetFiles(_receive, "*" + SyncInternalPaths.TransferTemporarySuffix));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("docs/../../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/windows/not-here.txt")]
    [InlineData("")]
    [InlineData("docs/")]
    [InlineData("./a.txt")]
    public async Task An_unsafe_path_is_refused_before_anything_is_created(string wire)
    {
        var sink = Sink();
        var error = await Assert.ThrowsAsync<LanTransferException>(async () =>
            await sink.ReceiveAsync(Body("x"), wire, 1, OldStamp, null, CancellationToken.None));

        Assert.Equal(LanRefusal.PathUnsafe, error.Refusal);
        Assert.Empty(Directory.GetFileSystemEntries(_receive));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task A_missing_path_is_refused_the_same_way_rather_than_landing_at_the_root()
    {
        // "No name given" published as a file named after nothing, at the top of the user's Downloads folder,
        // is the failure mode a silent fallback produces.
        var sink = Sink();
        var error = await Assert.ThrowsAsync<LanTransferException>(async () =>
            await sink.ReceiveAsync(Body("x"), null, 1, OldStamp, null, CancellationToken.None));

        Assert.Equal(LanRefusal.PathUnsafe, error.Refusal);
        Assert.Empty(Directory.GetFileSystemEntries(_receive));
    }

    [Fact]
    public async Task A_progress_report_sees_every_byte_once_and_the_final_count_last()
    {
        var seen = new List<long>();
        var sink = Sink();

        await sink.ReceiveAsync(new ChunkedStream("abcdefghij", 4), "counted.txt", 10, null,
            new ProgressReporting(seen), CancellationToken.None);

        Assert.Equal([4, 8, 10], seen);
    }

    [Fact]
    public async Task An_empty_file_is_a_real_answer_and_not_a_refusal()
    {
        var received = await Sink().ReceiveAsync(Body(""), "empty.txt", 0, OldStamp, null, CancellationToken.None);

        Assert.Equal(0, received.Bytes);
        Assert.True(File.Exists(received.AbsolutePath));
    }

    [Fact]
    public async Task Ensure_directory_lands_an_empty_folder_inside_the_receive_root()
    {
        // A recursive listing names directories that hold nothing and no file transfer would ever create one,
        // so a folder sent as an empty tree has to arrive as an empty tree.
        var created = await Sink().EnsureDirectoryAsync("相册/2024 旅行", CancellationToken.None);

        Assert.Equal("相册/2024 旅行", created);
        Assert.True(Directory.Exists(Path.Combine(_receive, "相册", "2024 旅行")));
    }

    [Theory]
    [InlineData("../out")]
    [InlineData("C:/out")]
    public async Task Ensure_directory_refuses_the_same_paths_a_file_would(string wire)
    {
        var error = await Assert.ThrowsAsync<LanTransferException>(async () =>
            await Sink().EnsureDirectoryAsync(wire, CancellationToken.None));

        Assert.Equal(LanRefusal.PathUnsafe, error.Refusal);
        Assert.Empty(Directory.GetFileSystemEntries(_receive));
    }

    [Fact]
    public async Task A_receive_with_the_length_guard_tripped_creates_nothing()
    {
        // The staging suffix has to fit too: MAX_PATH is what the destination accepts without a long-path
        // manifest, and the partial name is part of the path for the whole duration of the write.
        var deep = string.Join('/', Enumerable.Repeat(new string('d', 120), 2));
        var sink = Sink();

        var error = await Assert.ThrowsAsync<LanTransferException>(async () =>
            await sink.ReceiveAsync(Body("x"), $"{deep}/a.txt", 1, OldStamp, null, CancellationToken.None));

        Assert.Equal(LanRefusal.PathTooLong, error.Refusal);
        Assert.Empty(Directory.GetFileSystemEntries(_receive));
    }

    [Fact]
    public async Task Directory_is_usable_names_the_state_the_window_has_to_answer_for()
    {
        Assert.True(Sink().DirectoryIsUsable());

        var missing = Path.Combine(_root, "gone");
        Assert.True(new LanReceiveSink(missing, AppendCounter).DirectoryIsUsable());

        // A file occupying the chosen directory is not a directory the sink can create, and the window must be
        // able to say so instead of accepting a transfer that can only fail.
        var blocked = Path.Combine(_root, "blocker");
        await File.WriteAllTextAsync(blocked, "not a folder", CancellationToken.None);
        Assert.False(new LanReceiveSink(blocked, AppendCounter).DirectoryIsUsable());
    }

    private sealed class ProgressReporting(List<long> seen) : IProgress<long>
    {
        public void Report(long value) => seen.Add(value);
    }

    /// <summary>Hands over a few bytes and then fails the way a dropped connection does.</summary>
    private sealed class TruncatedStream : MemoryStream
    {
        private readonly int _limit;

        public TruncatedStream(string content) : base(System.Text.Encoding.UTF8.GetBytes(content)) => _limit = checked(content.Length / 2);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= _limit) throw new IOException("连接中断（模拟）。");
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class SelfCancellingStream : MemoryStream
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly int _limit;

        public SelfCancellingStream(string content, CancellationTokenSource cancellation) : base(System.Text.Encoding.UTF8.GetBytes(content))
        {
            _cancellation = cancellation;
            _limit = checked(content.Length / 2);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= _limit)
            {
                _cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    /// <summary>Answers a fixed chunk size so a progress sequence is deterministic.</summary>
    private sealed class ChunkedStream : MemoryStream
    {
        private readonly int _chunk;

        public ChunkedStream(string content, int chunk) : base(System.Text.Encoding.UTF8.GetBytes(content)) => _chunk = chunk;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var available = (int)(Length - Position);
            if (available <= 0) return ValueTask.FromResult(0);
            // Only ever hand back the chunk, never the whole buffer: the sink's read loop has to cross several
            // boundaries for a progress sequence or a byte count to mean anything.
            var take = Math.Min(_chunk, Math.Min(available, buffer.Length));
            return ValueTask.FromResult(base.Read(buffer[..take].Span));
        }
    }
}
