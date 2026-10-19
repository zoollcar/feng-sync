using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// The board both surfaces read: how fast a transfer is going, and how long a finished row keeps answering.
/// </summary>
/// <remarks>
/// <para>
/// Every case drives the clock by hand. Speed is the one number in this feature that a real clock would make
/// untestable — a machine under load turns "ten megabytes a second" into whatever the test runner managed — so the
/// board takes a clock instead of reading one, and these cases assert arithmetic rather than throughput.
/// </para>
/// <para>
/// The two properties worth reading twice are that a transfer which stops reporting reads as zero rather than as
/// its last confident number, and that a finished row survives long enough for a page polling twice a second to
/// see it reach the end.
/// </para>
/// </remarks>
public sealed class LanTransferBoardTests
{
    private const string PeerId = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
    private const long Mb = 1024 * 1024;

    private static LanContact Phone() => new(PeerId, "小明的手机", LanProtocol.KindPhone, "192.168.1.44", 0);

    /// <summary>A board on a clock the test moves, and the handle that moves it.</summary>
    private static (LanTransferBoard Board, Action<int> Advance) Clock()
    {
        var now = 0L;
        var board = new LanTransferBoard(() => now);
        return (board, milliseconds => now += milliseconds);
    }

    [Fact]
    public void Speed_is_the_bytes_that_moved_over_the_window_rather_than_an_average_since_the_start()
    {
        var (board, advance) = Clock();
        var key = LanTransferBoard.InboundKey(PeerId, "movie.mkv");
        board.Begin(key, LanTransferDirection.Received, Phone(), "movie.mkv", totalBytes: 100 * Mb);

        advance(500);
        board.Report(key, 5 * Mb);
        advance(500);
        var tick = board.Report(key, 10 * Mb);

        // Ten megabytes over one second of wall clock. The first half-second moved half as much, and an average
        // would have said 10 MB/s from the first tick onwards — which is exactly the number a stalled transfer
        // would then keep reporting.
        Assert.NotNull(tick);
        Assert.Equal(10 * Mb, tick!.BytesPerSecond, 0);
        Assert.Equal(100 * Mb, tick.TotalBytes);
        Assert.Equal(10d, tick.Percentage, 0);
        Assert.Equal(9d, tick.RemainingSeconds!.Value, 0);
        Assert.False(tick.Done);
    }

    [Fact]
    public void A_transfer_that_stops_reporting_reads_as_zero_rather_than_as_its_last_number()
    {
        var (board, advance) = Clock();
        var key = LanTransferBoard.InboundKey(PeerId, "movie.mkv");
        board.Begin(key, LanTransferDirection.Received, Phone(), "movie.mkv", totalBytes: 100 * Mb);
        advance(500);
        board.Report(key, 5 * Mb);
        advance(500);
        board.Report(key, 10 * Mb);

        // A phone that walked out of range leaves the socket open. Nothing arrives to say so, which is why the
        // quiet itself has to be the signal.
        advance(1500);
        var stale = Assert.Single(board.Snapshot());

        Assert.Equal(0d, stale.BytesPerSecond);
        Assert.Null(stale.RemainingSeconds);
        Assert.Equal(10 * Mb, stale.Bytes);
        Assert.False(board.AnyRunning);
    }

    [Fact]
    public void A_folder_reports_a_rate_without_a_fraction_because_nobody_measured_its_size()
    {
        var (board, advance) = Clock();
        var key = LanTransferBoard.PullKey("2b3c4d5e6f708192a3b4c5d6e7f80910");
        board.Begin(key, LanTransferDirection.Received, Phone(), "photos", totalBytes: 0, session: null, messageId: key[5..]);

        advance(1000);
        var tick = board.Report(key, 4 * Mb);

        Assert.NotNull(tick);
        Assert.False(tick!.HasTotal);
        Assert.Equal(0d, tick.Percentage);
        Assert.Null(tick.RemainingSeconds);
        // The rate is still real: a bar cannot show a fraction, but the row can still say how fast it is going.
        Assert.Equal(4 * Mb, tick.BytesPerSecond, 0);
    }

    [Fact]
    public void Events_are_throttled_to_four_a_second_and_the_last_byte_always_passes()
    {
        var (board, advance) = Clock();
        var key = LanTransferBoard.InboundKey(PeerId, "big.bin");
        board.Begin(key, LanTransferDirection.Received, Phone(), "big.bin", totalBytes: 50 * Mb);
        var seen = new List<long>();
        board.Changed += tick => seen.Add(tick.Bytes);

        // Two hundred reads over two seconds — the shape a large pull actually takes — must not become two
        // hundred dispatcher posts on the window's thread.
        for (var i = 1; i <= 200; i++)
        {
            advance(10);
            board.Report(key, i * Mb / 4);
        }

        board.Complete(key, 50 * Mb);

        Assert.True(seen.Count <= 10, $"{seen.Count} 次事件对 200 次上报：节流没生效。");
        Assert.Equal(50 * Mb, seen[^1]);
    }

    [Fact]
    public void A_finished_row_keeps_answering_long_enough_for_a_poller_to_see_the_end()
    {
        var (board, advance) = Clock();
        var key = LanTransferBoard.InboundKey(PeerId, "report.pdf");
        board.Begin(key, LanTransferDirection.Received, Phone(), "report.pdf", totalBytes: 2 * Mb);
        board.Report(key, Mb);
        var final = board.Complete(key, 2 * Mb);

        Assert.NotNull(final);
        Assert.True(final!.Done);
        Assert.Equal(100d, final.Percentage);
        Assert.Equal(0d, final.BytesPerSecond);

        advance(7000);
        Assert.Single(board.Snapshot());

        advance(2000);
        Assert.Empty(board.Snapshot());
    }

    [Fact]
    public void Opening_the_same_transfer_again_starts_it_over_instead_of_adding_a_second_row()
    {
        var (board, advance) = Clock();
        var key = LanTransferBoard.InboundKey(PeerId, "retry.zip");
        board.Begin(key, LanTransferDirection.Received, Phone(), "retry.zip", totalBytes: 10 * Mb);
        advance(400);
        board.Report(key, 6 * Mb);

        // A retry is the same row with a fresh window. Two rows at half speed each would be a worse lie than a
        // bar that jumped back to zero.
        advance(400);
        board.Begin(key, LanTransferDirection.Received, Phone(), "retry.zip", totalBytes: 10 * Mb);
        advance(1000);
        var tick = board.Report(key, 2 * Mb);

        Assert.Single(board.Snapshot());
        Assert.Equal(2 * Mb, tick!.Bytes);
        Assert.Equal(2 * Mb, tick.BytesPerSecond, 0);
    }

    [Fact]
    public void Rows_come_back_oldest_first_with_the_direction_and_the_peer_that_owns_them()
    {
        var (board, advance) = Clock();
        board.Begin(LanTransferBoard.InboundKey(PeerId, "a.txt"), LanTransferDirection.Received, Phone(), "a.txt", 1 * Mb);
        advance(50);
        board.Begin(LanTransferBoard.OutboundKey("2b3c4d5e6f708192a3b4c5d6e7f80910"),
            LanTransferDirection.Sent, Phone(), "b.txt", 2 * Mb, messageId: "2b3c4d5e6f708192a3b4c5d6e7f80910");

        var rows = board.Snapshot();

        Assert.Equal(["a.txt", "b.txt"], rows.Select(row => row.Path));
        Assert.Equal(LanTransferDirection.Received, rows[0].Direction);
        Assert.Equal(LanTransferDirection.Sent, rows[1].Direction);
        Assert.Equal("小明的手机", rows[0].DeviceName);
        Assert.Equal(PeerId, rows[0].DeviceId);
    }

    [Fact]
    public void Reporting_against_a_key_nobody_opened_is_ignored_rather_than_creating_a_ghost()
    {
        var (board, _) = Clock();

        Assert.Null(board.Report("in|nobody|nothing", 10));
        Assert.Null(board.Complete("in|nobody|nothing", 10));
        Assert.Empty(board.Snapshot());
    }
}
