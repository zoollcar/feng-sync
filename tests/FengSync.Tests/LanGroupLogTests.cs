using System.Text.Json;
using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// The room's history as one machine holds it: what may be said, what a copy adopts from a peer, and what it
/// throws away.
/// </summary>
/// <remarks>
/// <para>
/// None of these cases needs a socket, and that is the point — every one of them is a rule about the replica
/// rather than about the transport, and a rule that can only be observed through HTTP would be tested by whoever
/// happens to grow a wire case next.
/// </para>
/// <para>
/// The two properties worth reading twice are that identity is the message's own id and nothing else, and that
/// the cap binds merges as well as local appends. A copy that only shrank when this machine spoke would grow
/// forever on what it heard, and memory is the only place this history lives.
/// </para>
/// </remarks>
public sealed class LanGroupLogTests
{
    private const string SelfId = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6";
    private const string PeerId = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";

    private static readonly DateTimeOffset Stamp = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    private static LanContact Self() => new(SelfId, "台式机", LanProtocol.KindWindows, "192.168.1.20", 59871);
    private static LanContact Peer() => new(PeerId, "小明的手机", LanProtocol.KindPhone, "192.168.1.44", 0);

    [Fact]
    public void A_sentence_lands_numbered_and_reads_back_with_its_author()
    {
        var log = new LanGroupLog();

        var refusal = log.TryAppendText(Self(), "  先看报告  ", Stamp, out var entry);

        Assert.Equal(LanRefusal.None, refusal);
        Assert.Equal("先看报告", entry!.Text);
        Assert.Equal(SelfId, entry.AuthorDeviceId);
        Assert.Equal("台式机", entry.AuthorName);
        Assert.Equal(LanGroupKind.Text, entry.Kind);
        Assert.Equal(1, entry.LocalSeq);
        Assert.Equal(1, log.Head);
        Assert.False(entry.HeldHere);
    }

    [Fact]
    public void A_sentence_that_says_nothing_is_refused_rather_than_stored_as_blank()
    {
        var log = new LanGroupLog();

        var refusal = log.TryAppendText(Self(), "   ", Stamp, out var entry);

        Assert.Equal(LanRefusal.BadRequest, refusal);
        Assert.Null(entry);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void The_longest_allowed_sentence_lands_and_one_character_more_costs_nothing()
    {
        var log = new LanGroupLog();

        var fits = log.TryAppendText(Self(), new string('字', LanGroupLog.MaximumTextLength), Stamp, out var entry);
        var over = log.TryAppendText(Self(), new string('字', LanGroupLog.MaximumTextLength + 1), Stamp, out var refused);

        Assert.Equal(LanRefusal.None, fits);
        Assert.NotNull(entry);
        Assert.Equal(LanRefusal.MessageTooLong, over);
        Assert.Null(refused);
        // The refusal is the whole cost: a sentence too long to keep must not have taken a slot on the way out.
        Assert.Single(log.Entries);
    }

    [Fact]
    public void Each_copy_numbers_its_own_arrivals_and_answers_a_watermark_with_only_what_is_past_it()
    {
        var log = new LanGroupLog();
        log.TryAppendText(Self(), "一", Stamp, out _);
        log.TryAppendText(Peer(), "二", Stamp, out _);
        log.TryAppendText(Self(), "三", Stamp, out _);

        Assert.Equal(3, log.Head);
        Assert.Equal(["一", "二", "三"], log.Since(0).Select(entry => entry.Text));
        Assert.Equal(["三"], log.Since(2).Select(entry => entry.Text));
        // A watermark ahead of this copy answers empty rather than wrapping, because a peer that has read to the
        // end of a history has read all of it.
        Assert.Empty(log.Since(99));
    }

    [Fact]
    public void Saying_the_same_words_twice_is_two_messages_because_a_person_can_repeat_themselves()
    {
        var log = new LanGroupLog();
        log.TryAppendText(Self(), "在吗", Stamp, out var first);
        log.TryAppendText(Self(), "在吗", Stamp, out var second);

        Assert.NotEqual(first!.MessageId, second!.MessageId);
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void An_entry_heard_from_a_peer_is_adopted_but_never_believed_to_be_held_here()
    {
        var log = new LanGroupLog();
        // The peer's copy says it can hand the bytes over. This copy cannot.
        var wire = new LanMessageWire("deadbeef", PeerId, "小明的手机", 7, LanGroupKind.File,
            null, "报告.pdf", null, 1024, false, Stamp, true);

        Assert.True(log.TryMerge(wire));

        var adopted = Assert.Single(log.Entries);
        Assert.False(adopted.HeldHere);
        Assert.Null(adopted.LocalPath);
        // Its arrival number is this copy's own, not the number the peer's copy gave it — a peer's watermark
        // would otherwise mean something different in every copy it reached.
        Assert.Equal(1, adopted.LocalSeq);
        Assert.Equal("小明的手机", adopted.AuthorName);
    }

    [Fact]
    public void An_entry_delivered_twice_is_dropped_and_leaves_what_this_copy_already_knew()
    {
        var log = new LanGroupLog();
        var wire = new LanMessageWire("cafe0001", PeerId, "旧名字", 3, LanGroupKind.File,
            null, "照片.jpg", null, 2048, false, Stamp, false);
        Assert.True(log.TryMerge(wire));
        log.MarkHeld("cafe0001", Path.Combine(Path.GetTempPath(), "照片.jpg"));

        // The same id arrives again, from a peer whose copy has an older name for it and no bytes of its own.
        var again = new LanMessageWire("cafe0001", PeerId, "更新的称呼", 9, LanGroupKind.File,
            null, "照片.jpg", null, 2048, false, Stamp, false);

        Assert.False(log.TryMerge(again));
        var kept = Assert.Single(log.Entries);
        // Both halves of that matter: a re-delivery must not un-mark bytes this machine really holds, and it must
        // not rewrite a name the user already read.
        Assert.True(kept.HeldHere);
        Assert.Equal("旧名字", kept.AuthorName);
    }

    [Fact]
    public void An_entry_this_copy_has_never_heard_of_is_not_invented_to_be_marked()
    {
        var log = new LanGroupLog();

        log.MarkHeld("ffffffff", Path.Combine(Path.GetTempPath(), "没有这一项.txt"));

        Assert.Empty(log.Entries);
    }

    [Fact]
    public void A_second_entry_from_a_returning_device_is_kept_because_identity_is_the_message_not_a_counter()
    {
        var log = new LanGroupLog();
        // The device left, came back, and restarted its own numbering at the beginning. Two messages, both real.
        var before = new LanMessageWire("1111aaaa", PeerId, "手机", 1, LanGroupKind.Text, "上次说的", null, null, 0, false, Stamp, false);
        var after = new LanMessageWire("2222bbbb", PeerId, "手机", 1, LanGroupKind.Text, "这次说的", null, null, 0, false, Stamp, false);

        Assert.True(log.TryMerge(before));
        Assert.True(log.TryMerge(after));

        Assert.Equal(["上次说的", "这次说的"], log.Entries.Select(entry => entry.Text));
    }

    [Fact]
    public void A_wire_entry_that_names_no_kind_or_wears_an_unknown_one_is_refused_not_guessed_at()
    {
        var log = new LanGroupLog();

        Assert.False(log.TryMerge(new LanMessageWire("9999", PeerId, "手机", 1, "voice", null, null, null, 0, false, Stamp, false)));
        Assert.False(log.TryMerge(new LanMessageWire("", PeerId, "手机", 1, LanGroupKind.Text, "没有身份", null, null, 0, false, Stamp, false)));

        Assert.Empty(log.Entries);
    }

    [Fact]
    public void The_cap_pushes_out_the_oldest_line_and_a_heard_entry_counts_against_it_too()
    {
        var log = new LanGroupLog(maximumEntries: 2);
        var dropped = new List<LanGroupMessage>();
        log.Trimmed += dropped.Add;

        log.TryAppendText(Self(), "一", Stamp, out _);
        log.TryAppendText(Self(), "二", Stamp, out _);
        // The room's history is bounded by the one place it lives, so what arrives from a peer is no cheaper
        // than what this machine said itself.
        Assert.True(log.TryMerge(new LanMessageWire("peer-entry", PeerId, "手机", 1, LanGroupKind.Text, "三", null, null, 0, false, Stamp, false)));

        Assert.Equal(2, log.Count);
        Assert.Equal(["二", "三"], log.Entries.Select(entry => entry.Text));
        Assert.Equal("一", Assert.Single(dropped).Text);
        // The watermark never goes backwards, so a peer that had already read 一 is not asked for it again.
        Assert.Equal(3, log.Head);
    }

    [Fact]
    public void A_line_the_room_dropped_can_be_adopted_again_because_nobody_remembered_it_here()
    {
        var log = new LanGroupLog(maximumEntries: 1);
        var wire = new LanMessageWire("drop-me", PeerId, "手机", 1, LanGroupKind.Text, "一", null, null, 0, false, Stamp, false);
        Assert.True(log.TryMerge(wire));
        log.TryAppendText(Self(), "二", Stamp, out _);

        // This is the honest consequence of a bounded memory rather than a bug: a copy that forgot an entry has
        // no way to tell a first delivery from a second one, and pretending otherwise would need a graveyard.
        Assert.True(log.TryMerge(wire));
        Assert.Equal(["一"], log.Entries.Select(entry => entry.Text));
    }

    [Fact]
    public void Being_told_the_bytes_are_here_keeps_only_a_reference_to_where_they_already_are()
    {
        var log = new LanGroupLog();
        var entry = log.AppendFile(Self(), "报告.pdf", null, 4096, false, Stamp);
        var path = Path.Combine(Path.GetTempPath(), "fengsync-group-held", "报告.pdf");

        log.MarkHeld(entry.MessageId, path);

        Assert.True(entry.HeldHere);
        Assert.Equal(path, entry.LocalPath);
    }

    [Fact]
    public void Announcing_a_file_that_is_already_here_names_the_author_as_able_to_serve_it()
    {
        var log = new LanGroupLog();
        var path = Path.Combine(Path.GetTempPath(), "fengsync-group-local", "笔记.txt");

        var entry = log.AppendFile(Self(), "笔记.txt", null, 12, false, Stamp, path);

        Assert.True(entry.HeldHere);
        Assert.Equal(LanGroupKind.File, entry.Kind);
        Assert.Equal(path, entry.LocalPath);
    }

    [Fact]
    public void A_folder_is_announced_as_a_folder_and_a_row_with_no_bytes_still_reports_zero()
    {
        var log = new LanGroupLog();

        var folder = log.AppendFile(Self(), "照片", null, -1, true, Stamp);

        Assert.Equal(LanGroupKind.Directory, folder.Kind);
        Assert.True(folder.IsDirectory);
        // A negative length is a caller's mistake, not a wire state: the honest number is the one a listing can
        // show, and 0 is what an unmeasured folder already reports.
        Assert.Equal(0, folder.Bytes);
    }

    [Fact]
    public void The_wire_carries_what_a_member_needs_and_never_the_path_on_this_machine()
    {
        var log = new LanGroupLog();
        var path = Path.Combine(Path.GetTempPath(), "fengsync-group-priv", "合同 1+1.pdf");
        var entry = log.AppendFile(Self(), "合同 1+1.pdf", "子目录/合同 1+1.pdf", 1024, false, Stamp, path);

        var json = JsonSerializer.Serialize(entry.ToWire(), LanApi.Json);

        Assert.DoesNotContain(path, json, StringComparison.Ordinal);
        Assert.DoesNotContain("localPath", json, StringComparison.OrdinalIgnoreCase);
        // The name is ASCII-escaped on the wire, so the claim worth pinning is that it survives that and the
        // page on the other side reads the file it was named — not that a byte sequence appears in a body.
        Assert.Equal("合同 1+1.pdf", JsonSerializer.Deserialize<LanMessageWire>(json, LanApi.Json)!.FileName);
        Assert.True(JsonSerializer.Deserialize<LanMessageWire>(json, LanApi.Json)!.HeldHere);
    }

    [Fact]
    public void A_system_line_is_authored_by_nobody_so_no_device_can_be_told_it_said_this()
    {
        var log = new LanGroupLog();

        var entry = log.AppendSystem("防火墙规则已加上", Stamp);

        Assert.Equal("", entry.AuthorDeviceId);
        Assert.Equal("", entry.AuthorName);
        Assert.Equal(LanGroupKind.Text, entry.Kind);
        Assert.Contains("防火墙", entry.Text);
    }

    [Fact]
    public void A_push_that_names_an_entry_nobody_here_has_heard_of_announces_it_with_the_bytes_that_arrived()
    {
        var log = new LanGroupLog();
        var path = Path.Combine(Path.GetTempPath(), "fengsync-group-push", "report.txt");

        var entry = log.Hold(Peer(), "1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d", "report.txt", 12, isDirectory: false, path, Stamp);

        // The peer minted the id, so this copy files the entry under that id rather than minting its own: the
        // announcer and the announcer's host must converge on one message, not two.
        Assert.Equal("1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d", entry!.MessageId);
        Assert.Equal(PeerId, entry.AuthorDeviceId);
        Assert.Equal("小明的手机", entry.AuthorName);
        Assert.Equal("report.txt", entry.FileName);
        Assert.Equal(LanGroupKind.File, entry.Kind);
        Assert.True(entry.HeldHere);
        Assert.Equal(path, entry.LocalPath);
        Assert.Equal(1, log.Head);
    }

    [Fact]
    public void A_push_under_an_id_another_member_spoke_is_left_alone_because_bytes_cannot_retarget_a_sentence()
    {
        var log = new LanGroupLog();
        var theirs = "abcdef0123456789abcdef0123456789";
        log.TryMerge(new LanMessageWire(theirs, PeerId, "小明的手机", 2, LanGroupKind.File,
            null, "报告.pdf", null, 1024, false, Stamp, false));

        var entry = log.Hold(Self(), theirs, "冒名.txt", 8, isDirectory: false,
            Path.Combine(Path.GetTempPath(), "冒名.txt"), Stamp);

        // Nothing is returned, nothing is added, and the entry keeps the author and the absence it was heard with:
        // a device that tags its own bytes with somebody else's announcement must not be able to make this copy
        // offer those bytes under that name.
        Assert.Null(entry);
        Assert.Single(log.Entries);
        var kept = log.Entries[0];
        Assert.Equal(PeerId, kept.AuthorDeviceId);
        Assert.False(kept.HeldHere);
        Assert.Null(kept.LocalPath);
    }

    [Fact]
    public void A_retried_push_from_the_same_member_moves_the_held_path_and_adds_no_second_row()
    {
        var log = new LanGroupLog();
        var id = "00112233445566778899aabbccddeeff";
        var first = log.Hold(Peer(), id, "report.txt", 12, false, @"C:\in\report.txt", Stamp);
        // The name the sink settled on can differ from the one that was sent, and the newest copy is the one this
        // machine can hand over.
        var again = log.Hold(Peer(), id, "report (1).txt", 12, false, @"C:\in\report (1).txt", Stamp);

        Assert.NotNull(first);
        Assert.Same(first, again);
        Assert.Single(log.Entries);
        Assert.Equal(@"C:\in\report (1).txt", again!.LocalPath);
        // The watermark does not move either: no new line entered the room, so a poller that had read the first has
        // read everything there is.
        Assert.Equal(1, log.Head);
    }

    [Fact]
    public void A_push_of_a_tree_is_one_folder_entry_at_its_root_and_not_one_per_file()
    {
        var log = new LanGroupLog();
        var id = "ffeedccbbaa99887766554433221100";
        var root = Path.Combine(Path.GetTempPath(), "fengsync-group-tree");

        // The arrival order a sender really produces: the folder, then a nested file, each naming one id.
        log.Hold(Peer(), id, "照片", 0, isDirectory: true, root, Stamp);
        var entry = log.Hold(Peer(), id, "照片/假期的 IMG.jpg", 0, isDirectory: true, root, Stamp);

        Assert.Single(log.Entries);
        Assert.True(entry!.IsDirectory);
        // The entry names the root of what was sent rather than whichever file landed, because that is the thing a
        // member can ask for as one archive.
        Assert.Equal("照片", entry.FileName);
        Assert.Equal("照片", entry.RelativePath);
    }

    [Fact]
    public void An_id_that_could_not_have_been_minted_here_is_not_announced_by_a_push()
    {
        var log = new LanGroupLog();

        Assert.Null(log.Hold(Peer(), "一", "note.txt", 4, false, @"C:\in\note.txt", Stamp));
        Assert.Null(log.Hold(Peer(), "", "note.txt", 4, false, @"C:\in\note.txt", Stamp));
        Assert.Null(log.Hold(Peer(), "not a message id!", "note.txt", 4, false, @"C:\in\note.txt", Stamp));

        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Losing_the_bytes_takes_the_promise_and_leaves_the_sentence()
    {
        var log = new LanGroupLog();
        var entry = log.Hold(Peer(), "5566778899aabbccddeeff001122334455", "report.txt", 12, false,
            @"C:\in\report.txt", Stamp);

        log.DropHeld("5566778899aabbccddeeff001122334455");

        Assert.False(entry!.HeldHere);
        Assert.Null(entry.LocalPath);
        // What a member said is not this machine's to erase, so the room still shows the file — it just cannot be
        // served from here any more.
        Assert.Single(log.Entries);
        // An id this copy never heard of costs nothing, exactly as with the mark.
        log.DropHeld("ffffffff");
        Assert.Single(log.Entries);
    }

    [Fact]
    public void A_new_entry_is_announced_once_and_on_the_caller_that_spoke_it()
    {
        var log = new LanGroupLog();
        var seen = new List<LanGroupMessage>();
        log.Appended += seen.Add;

        log.TryAppendText(Self(), "一", Stamp, out var entry);
        log.TryMerge(new LanMessageWire("heard", PeerId, "手机", 1, LanGroupKind.Text, "二", null, null, 0, false, Stamp, false));
        // A duplicate costs the listener nothing but a false answer, and no event at all.
        Assert.False(log.TryMerge(new LanMessageWire("heard", PeerId, "手机", 1, LanGroupKind.Text, "二", null, null, 0, false, Stamp, false)));

        Assert.Equal(2, seen.Count);
        Assert.Same(entry, seen[0]);
    }
}
