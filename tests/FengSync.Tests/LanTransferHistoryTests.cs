using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// Covers <see cref="LanTransferHistoryRepository"/>, the record of what the quick-transfer window already
/// stopped showing.
/// </summary>
/// <remarks>
/// The progress list on screen is live state and empties itself, so this file is the only answer to "which
/// phone sent me what, and did it finish". The properties worth pinning are the ones an append-only archive
/// gets wrong by accident: newest-first ordering, a cap that keeps the recent end rather than the first
/// written, and an unreadable file reading as "no history" instead of throwing at a window that only wanted
/// to show a list.
/// </remarks>
public sealed class LanTransferHistoryTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public LanTransferHistoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fengsync-lan-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "lan-transfer-history.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private static LanTransferHistoryEntry Entry(string name, DateTimeOffset at, LanTransferState state = LanTransferState.Completed) =>
        new(Guid.NewGuid(), LanTransferDirection.Received, "phone-a", "小明的手机", name, name.Length, state, at);

    [Fact]
    public async Task A_first_open_reads_as_no_history_rather_than_as_an_error()
    {
        Assert.Empty(Directory.GetFileSystemEntries(_root));

        Assert.Empty(await new LanTransferHistoryRepository(_path).QueryAsync());
    }

    [Fact]
    public async Task Entries_are_answered_newest_first_whatever_order_they_were_filed_in()
    {
        var now = DateTimeOffset.UtcNow;
        var repository = new LanTransferHistoryRepository(_path);

        await repository.AppendAsync([Entry("oldest.txt", now.AddMinutes(-30)), Entry("newest.txt", now)], CancellationToken.None);
        await repository.AppendAsync([Entry("middle.txt", now.AddMinutes(-10))], CancellationToken.None);

        Assert.Equal(["newest.txt", "middle.txt", "oldest.txt"], (await repository.QueryAsync()).Select(entry => entry.Path));
    }

    [Fact]
    public async Task The_cap_keeps_the_recent_end_and_says_so_by_number()
    {
        var now = DateTimeOffset.UtcNow;
        var repository = new LanTransferHistoryRepository(_path, maximumEntries: 3);
        await repository.AppendAsync(Enumerable.Range(0, 10)
            .Select(index => Entry($"file-{index:00}.txt", now.AddMinutes(index)))
            .ToList(), CancellationToken.None);

        var entries = await repository.QueryAsync();
        Assert.Equal(3, entries.Count);
        Assert.Equal(["file-09.txt", "file-08.txt", "file-07.txt"], entries.Select(entry => entry.Path));
    }

    [Fact]
    public async Task A_cap_of_one_is_still_a_cap_rather_than_an_unbounded_list()
    {
        var now = DateTimeOffset.UtcNow;
        var repository = new LanTransferHistoryRepository(_path, maximumEntries: 0);
        await repository.AppendAsync([Entry("a.txt", now), Entry("b.txt", now.AddSeconds(1))], CancellationToken.None);

        Assert.Equal(["b.txt"], (await repository.QueryAsync()).Select(entry => entry.Path));
    }

    [Fact]
    public async Task Appending_nothing_does_not_rewrite_the_file()
    {
        var repository = new LanTransferHistoryRepository(_path);
        await repository.AppendAsync([Entry("kept.txt", DateTimeOffset.UtcNow)], CancellationToken.None);
        var written = File.GetLastWriteTimeUtc(_path);

        await repository.AppendAsync([], CancellationToken.None);

        Assert.Equal(written, File.GetLastWriteTimeUtc(_path));
        Assert.Single(await repository.QueryAsync());
    }

    [Fact]
    public async Task Everything_a_row_knows_about_itself_survives_the_round_trip()
    {
        var at = new DateTimeOffset(2026, 10, 3, 14, 15, 16, TimeSpan.FromHours(8));
        var original = Entry("相册/a.txt", at, LanTransferState.Failed) with { Error = "对方在写入前断开了" };
        var repository = new LanTransferHistoryRepository(_path);

        await repository.AppendAsync([original], CancellationToken.None);
        var loaded = Assert.Single(await repository.QueryAsync());

        Assert.Equal(original, loaded);
        Assert.Equal(LanTransferDirection.Received, loaded.Direction);
        Assert.Equal(LanTransferState.Failed, loaded.State);
    }

    [Fact]
    public async Task A_send_and_a_receive_are_two_states_of_the_same_row_not_two_shapes()
    {
        var now = DateTimeOffset.UtcNow;
        var repository = new LanTransferHistoryRepository(_path);
        await repository.AppendAsync([
            new(Guid.NewGuid(), LanTransferDirection.Sent, "phone-a", "小明的手机", "docs/报告.docx", 4096, LanTransferState.Completed, now),
            Entry("inbound.txt", now.AddSeconds(1))], CancellationToken.None);

        var entries = await repository.QueryAsync();
        Assert.Equal(LanTransferDirection.Received, entries[0].Direction);
        Assert.Equal(LanTransferDirection.Sent, entries[1].Direction);
        Assert.Equal(4096, entries[1].Bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Path\":\"a.txt\"}")]
    public async Task A_file_this_class_cannot_read_reads_as_no_history(string content)
    {
        await File.WriteAllTextAsync(_path, content, CancellationToken.None);

        var repository = new LanTransferHistoryRepository(_path);
        Assert.Empty(await repository.QueryAsync());

        // And it keeps working: a damaged archive must not cost the user the transfers they are about to make.
        await repository.AppendAsync([Entry("fresh.txt", DateTimeOffset.UtcNow)], CancellationToken.None);
        Assert.Single(await repository.QueryAsync());
    }

    [Fact]
    public async Task A_declined_or_cancelled_transfer_is_archived_like_any_other_outcome()
    {
        // The window's confirm gate is a decision with consequences, so "the user said no" belongs in the
        // history beside "the bytes failed" rather than vanishing because nothing was written.
        var now = DateTimeOffset.UtcNow;
        var repository = new LanTransferHistoryRepository(_path);
        await repository.AppendAsync([
            Entry("no.txt", now, LanTransferState.Declined),
            Entry("gone.txt", now.AddSeconds(1), LanTransferState.Cancelled)], CancellationToken.None);

        Assert.Equal([LanTransferState.Cancelled, LanTransferState.Declined], (await repository.QueryAsync()).Select(entry => entry.State));
    }

    [Fact]
    public async Task Emptying_the_archive_is_a_real_answer_and_not_a_read_only_view()
    {
        var repository = new LanTransferHistoryRepository(_path);
        await repository.AppendAsync([Entry("a.txt", DateTimeOffset.UtcNow)], CancellationToken.None);

        await repository.ClearAsync(CancellationToken.None);

        Assert.False(File.Exists(_path));
        Assert.Empty(await repository.QueryAsync());
        // Clearing an archive that is already gone is not an error, and the window can offer the button anyway.
        await repository.ClearAsync(CancellationToken.None);
    }

    [Fact]
    public async Task No_write_ever_leaves_a_temporary_file_beside_the_archive()
    {
        var repository = new LanTransferHistoryRepository(_path);
        for (var batch = 0; batch < 5; batch++)
            await repository.AppendAsync([Entry($"file-{batch}.txt", DateTimeOffset.UtcNow.AddMinutes(batch))], CancellationToken.None);

        Assert.Equal(["lan-transfer-history.json"], Directory.GetFileSystemEntries(_root).Select(static path => Path.GetFileName(path)!).ToArray());
    }

    [Fact]
    public async Task Two_repositories_on_one_file_take_turns_and_lose_nothing()
    {
        // The window and the server can both be appending, and the archive is the one place where losing a row
        // means losing the only record that the transfer happened.
        var now = DateTimeOffset.UtcNow;
        var pair = Enumerable.Range(0, 2).Select(_ => new LanTransferHistoryRepository(_path)).ToList();
        await Task.WhenAll(pair.Select((repository, index) =>
            repository.AppendAsync([Entry($"file-{index}.txt", now.AddSeconds(index))], CancellationToken.None)));

        Assert.Equal(2, (await pair[0].QueryAsync()).Count);
    }
}
