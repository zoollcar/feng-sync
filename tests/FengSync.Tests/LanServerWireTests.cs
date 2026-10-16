using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// Drives <see cref="LanServerHost"/> over a real socket with a real <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// These cases exist because the pure tests cannot see the wire. Whether a header survives a percent-encoding
/// round trip with a Chinese name and a <c>+</c>, whether a range request really answers 206 with a correct
/// <c>Content-Range</c>, whether a refusal carries the reason body the phone page reads, and whether a zip is
/// written while it streams rather than after being built on disk — each is a property of the transport and of
/// the framework, not of any method in this repository. A fake client would assert the intent and miss the fact.
/// </para>
/// <para>
/// Every case binds its own port and its own directory tree, because the wrong-code budget is keyed on the
/// source address and all of these requests arrive from loopback: one shared listener would have the rate-limit
/// case lock its neighbours out.
/// </para>
/// </remarks>
public sealed class LanServerWireTests
{
    private const string PeerId = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6";
    private const string OtherPeerId = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";

    private static string WrongCode(string armed) => armed == "AAAAAA" ? "BBBBBB" : "AAAAAA";

    private static DateTimeOffset OldStamp => new(2021, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [Fact]
    public async Task Hello_answers_a_stranger_with_no_credential_and_says_a_code_is_armed()
    {
        await using var server = await Harness.StartAsync();
        var hello = await server.HelloAsync();

        Assert.Equal(LanApi.WireVersion, hello.V);
        Assert.False(string.IsNullOrWhiteSpace(hello.DeviceId));
        Assert.Equal(LanProtocol.KindWindows, hello.Kind);
        Assert.True(hello.PairingArmed);
    }

    [Fact]
    public async Task Pairing_the_armed_code_answers_a_token_and_a_row_that_carries_no_token()
    {
        await using var server = await Harness.StartAsync();
        var reply = await server.PairAsync(server.Pairing.Code);

        Assert.Equal(2 * LanPeerSecretStore.TokenBytes, reply.Token.Length);
        Assert.Equal(LanApi.WireVersion, reply.V);
        Assert.Equal(PeerId, reply.Peer.DeviceId);
        // The name came from the body, not from anything this machine guessed about a phone.
        Assert.Equal("小明的手机", reply.Peer.DeviceName);
        Assert.DoesNotContain("token", JsonSerializer.Serialize(reply.Peer, LanApi.Json), StringComparison.OrdinalIgnoreCase);
        var records = await server.Peers.LoadAsync();
        Assert.Equal(PeerId, Assert.Single(records).DeviceId);
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_costs_the_typer_nothing_but_a_turn()
    {
        await using var server = await Harness.StartAsync();
        var armed = server.Pairing.Code;

        var mistyped = await server.PairRawAsync(WrongCode(armed));
        Assert.Equal(HttpStatusCode.Unauthorized, mistyped.StatusCode);
        Assert.Equal(LanReason.BadCode, await ReasonAsync(mistyped));

        // A hand typing six characters on a phone mis-types, and burning the pairing over that would make the
        // user regenerate the code every attempt. Guessing is bounded by the budget below instead.
        Assert.Equal(2 * LanPeerSecretStore.TokenBytes, (await server.PairAsync(armed)).Token.Length);
    }

    [Fact]
    public async Task An_unparsable_pair_body_answers_bad_request_instead_of_failing_the_route()
    {
        await using var server = await Harness.StartAsync();

        var response = await server.Http.PostAsync("/api/v1/pair",
            new StringContent("{ this is not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(LanReason.BadRequest, await ReasonAsync(response));
    }

    [Fact]
    public async Task A_device_id_nobody_could_have_generated_is_refused_before_the_code_is_spent()
    {
        await using var server = await Harness.StartAsync();
        var armed = server.Pairing.Code;

        var response = await server.PairRawAsync(armed, "not a device id!!");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(LanReason.BadRequest, await ReasonAsync(response));
        // The code is what a person read off the screen; a malformed envelope must not cost them it.
        Assert.True(server.Pairing.IsAccepting);
        Assert.Equal(2 * LanPeerSecretStore.TokenBytes, (await server.PairAsync(armed)).Token.Length);
    }

    [Fact]
    public async Task Five_wrong_codes_from_one_source_lock_it_out_even_when_it_then_guesses_right()
    {
        await using var server = await Harness.StartAsync();
        var armed = server.Pairing.Code;

        for (var attempt = 0; attempt < LanProtocol.MaximumFailedAttempts; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await server.PairRawAsync(WrongCode(armed))).StatusCode);

        // The honest consequence of a per-source budget: the next request from that address is refused before
        // the code is compared, so the right answer included. What is being priced is guessing, and a source
        // that has spent its window waits for it to age out rather than trying again.
        var locked = await server.PairRawAsync(armed);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal(LanReason.RateLimited, await ReasonAsync(locked));
    }

    [Fact]
    public async Task A_code_past_its_lifetime_answers_code_expired_and_the_hello_stops_advertising_it()
    {
        await using var server = await Harness.StartAsync();
        var armed = server.Pairing.Code;

        server.AdvanceClock(LanProtocol.PairCodeLifetimeSeconds + 1);

        Assert.False((await server.HelloAsync()).PairingArmed);
        var response = await server.PairRawAsync(armed);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(LanReason.CodeExpired, await ReasonAsync(response));
    }

    [Fact]
    public async Task Every_authorized_route_refuses_a_request_that_carries_nothing()
    {
        await using var server = await Harness.StartAsync();

        var response = await server.GetAsync("/api/v1/log");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(LanReason.PairRequired, await ReasonAsync(response));
    }

    [Fact]
    public async Task A_known_device_showing_somebody_elses_token_is_named_as_an_identity_change()
    {
        await using var server = await Harness.StartAsync();
        await server.PairAsync(server.Pairing.Code, PeerId);
        var forged = new string('f', 2 * LanPeerSecretStore.TokenBytes);

        // A device id this list already holds, presenting a credential that was never issued to it, is a device
        // re-paired from elsewhere — not a stranger asking to pair, and the two need different sentences.
        var stranger = await server.GetAsync("/api/v1/log", OtherPeerId, forged);
        Assert.Equal(LanReason.PairRequired, await ReasonAsync(stranger));

        var known = await server.GetAsync("/api/v1/log", PeerId, forged);
        Assert.Equal(LanReason.IdentityChanged, await ReasonAsync(known));
    }

    [Fact]
    public async Task A_file_arrives_under_its_percent_encoded_Chinese_name_with_the_plus_sign_intact()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        await server.PairAsync(server.Pairing.Code);
        // A form decoder would turn this '+' into a space and rename the user's file on the way in.
        var wire = Uri.EscapeDataString("报告 2026+最终/账单+明细.txt");

        var reply = await server.SendInboxAsync(wire, "内容", 6, OldStamp);

        Assert.Equal("账单+明细.txt", reply.SavedAs);
        Assert.False(reply.Renamed);
        Assert.Equal("报告 2026+最终/账单+明细.txt", reply.RelativePath);
        var stored = Path.Combine(server.Receive, "报告 2026+最终", "账单+明细.txt");
        Assert.True(File.Exists(stored), stored);
        Assert.Equal("内容", File.ReadAllText(stored, Encoding.UTF8));
    }

    [Fact]
    public async Task The_reported_modification_time_survives_the_publish()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        await server.PairAsync(server.Pairing.Code);

        await server.SendInboxAsync("stamp.txt", "abc", 3, OldStamp);

        // The publish is a move, which keeps the staging file's timestamp, so this is the only moment it can be
        // set — and a receive that lost it would make every folder sent look freshly written.
        var written = new FileInfo(Path.Combine(server.Receive, "stamp.txt")).LastWriteTimeUtc;
        Assert.True((written - OldStamp.UtcDateTime).Duration() <= LanReceiveSink.TimestampTolerance,
            $"落地时间 {written:O} 对声明时间 {OldStamp.UtcDateTime:O}");
    }

    [Fact]
    public async Task A_name_already_taken_is_kept_both_of_instead_of_overwritten()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        await server.PairAsync(server.Pairing.Code);
        File.WriteAllText(Path.Combine(server.Receive, "a.txt"), "原来的", Encoding.UTF8);

        var reply = await server.SendInboxAsync("a.txt", "新的", 6, mtime: null);

        Assert.True(reply.Renamed);
        Assert.Equal("a (1).txt", reply.SavedAs);
        Assert.Equal("原来的", File.ReadAllText(Path.Combine(server.Receive, "a.txt"), Encoding.UTF8));
    }

    [Fact]
    public async Task A_traversal_in_the_wire_path_is_refused_and_nothing_is_created()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        await server.PairAsync(server.Pairing.Code);

        var response = await server.InboxRawAsync(Uri.EscapeDataString("../escape.txt"), "x");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(LanReason.PathUnsafe, await ReasonAsync(response));
        Assert.False(File.Exists(Path.Combine(server.Root, "escape.txt")));
    }

    [Fact]
    public async Task A_body_shorter_than_it_promised_is_refused_as_incomplete()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        await server.PairAsync(server.Pairing.Code);

        var response = await server.InboxRawAsync("short.txt", "abc", declaredBytes: 100);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(LanReason.IncompleteBody, await ReasonAsync(response));
        // Half a file published under its real name is worse than no file, so neither the file nor its partial
        // sibling is left behind.
        Assert.Empty(Directory.GetFileSystemEntries(server.Receive));
    }

    [Fact]
    public async Task A_stranger_is_asked_once_and_then_lands_its_remaining_files_without_asking_again()
    {
        var asked = 0;
        await using var server = await Harness.StartAsync(confirm: (_, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult(true);
        });
        await server.PairAsync(server.Pairing.Code);

        await server.SendInboxAsync("one.txt", "1", 1, mtime: null);
        await server.SendInboxAsync("two.txt", "2", 1, mtime: null);

        // One agreement covers the session, not one click per file — a folder of a hundred photos is not a
        // hundred dialogs.
        Assert.Equal(1, asked);
        Assert.True(File.Exists(Path.Combine(server.Receive, "one.txt")));
        Assert.True(File.Exists(Path.Combine(server.Receive, "two.txt")));
    }

    [Fact]
    public async Task A_write_a_person_refused_is_answered_with_a_reason_and_nothing_on_disk()
    {
        await using var server = await Harness.StartAsync(confirm: (_, _) => Task.FromResult(false));
        await server.PairAsync(server.Pairing.Code);

        var response = await server.InboxRawAsync("nope.txt", "x");

        // A refusal with no body would leave the phone page showing a spinner for a file nobody will ever accept.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(LanReason.Declined, await ReasonAsync(response));
        Assert.Empty(Directory.GetFileSystemEntries(server.Receive));
    }

    [Fact]
    public async Task Nobody_to_ask_is_answered_the_same_as_a_person_who_said_no()
    {
        // The window is the only thing that can confirm a write, so a session built without that seam refuses
        // rather than assuming yes.
        await using var server = await Harness.StartAsync();
        await server.PairAsync(server.Pairing.Code);

        Assert.Equal(HttpStatusCode.Forbidden, (await server.InboxRawAsync("silent.txt", "x")).StatusCode);
    }

    [Fact]
    public async Task A_device_that_pairs_now_is_still_a_stranger_because_trust_was_read_before_it_arrived()
    {
        await using var server = await Harness.StartAsync(autoAccept: true);
        await server.PairAsync(server.Pairing.Code);

        // Auto-accept means "a device this window already knew", not "a device holding any valid credential":
        // pairing proves a peer read the code off the screen, not that a person wants its files.
        var response = await server.InboxRawAsync("fresh.txt", "x");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_device_already_trusted_when_the_window_opened_lands_without_being_asked()
    {
        var root = Harness.NewRoot();
        try
        {
            string token;
            await using (var first = await Harness.StartAsync(root))
            {
                first.KeepRoot = true;
                token = (await first.PairAsync(first.Pairing.Code)).Token;
                // This window has nobody to ask, so the write is refused even though the device is now trusted.
                Assert.Equal(HttpStatusCode.Forbidden, (await first.InboxRawAsync("kept.txt", "x", token: token)).StatusCode);
            }

            // A second window on the same stores: the row predates it, so this time the bytes land unasked.
            await using var second = await Harness.StartAsync(root, autoAccept: true);
            var reply = await second.SendInboxAsync("returning.txt", "内容", 6, mtime: null, token: token);

            Assert.Equal("returning.txt", reply.SavedAs);
        }
        finally
        {
            Harness.Delete(root);
        }
    }

    [Fact]
    public async Task Mkdir_keeps_an_empty_folder_that_no_file_would_ever_create()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        await server.PairAsync(server.Pairing.Code);

        var reply = await server.MkdirAsync(Uri.EscapeDataString("空文件夹"));

        Assert.Equal("空文件夹", reply.RelativePath);
        Assert.True(Directory.Exists(Path.Combine(server.Receive, "空文件夹")));
    }

    [Fact]
    public async Task The_bytes_a_room_entry_hands_over_are_the_disks_own_number_and_not_the_announcement_s()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        await server.PairAsync(server.Pairing.Code);
        var id = "1a2b3c4d5e6f708192a3b4c5d6e7f809";
        await server.InboxRawAsync("report.txt", "1", messageId: id);
        // The member that announced this file said one byte, and then the bytes here grew.
        File.WriteAllText(Path.Combine(server.Receive, "report.txt"), "0123456789");

        var response = await server.AttachmentAsync(id);

        // A peer sizes its download — and the range it resumes into that download — against what it is about to be
        // handed. Answering with the length announced when the file landed would cut the client off mid-file and
        // never ask for the tail, so the name, the length and the modification time come off the disk.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0123456789", await response.Content.ReadAsStringAsync());
        Assert.Equal(10, response.Content.Headers.ContentLength);
        // The room still reports the sentence that was said at the time; a file changing underneath an entry is
        // not this machine's licence to rewrite what a member announced.
        Assert.Equal(1, Assert.Single((await server.LogAsync()).Entries).Bytes);
        // And a caller that names a path rather than an id is refused, because the id is a key in this copy of the
        // log and never a way to point at a file on this disk.
        Assert.Equal(HttpStatusCode.NotFound,
            (await server.AttachmentAsync(Path.Combine(server.Receive, "report.txt"))).StatusCode);
    }

    [Fact]
    public async Task A_range_request_answers_206_with_the_tail_the_phone_asked_for()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        var token = (await server.PairAsync(server.Pairing.Code)).Token;
        var id = "2b3c4d5e6f708192a3b4c5d6e7f80910";
        await server.InboxRawAsync("tail.txt", "0123456789", messageId: id);

        var response = await server.AttachmentAsync(id, token: token, range: new RangeHeaderValue(3, null));

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes", Header(response, "Accept-Ranges"));
        Assert.Equal("3456789", await response.Content.ReadAsStringAsync());
        // A resumed download is the whole reason this route exists, and it only works if the answer names the
        // window it served and the total it is a window into.
        Assert.Equal("bytes 3-9/10", Header(response, "Content-Range"));
    }

    [Fact]
    public async Task A_download_names_itself_in_both_forms_so_a_Chinese_file_keeps_its_name()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        var token = (await server.PairAsync(server.Pairing.Code)).Token;
        var id = "3c4d5e6f708192a3b4c5d6e7f8091011";
        await server.InboxRawAsync(Uri.EscapeDataString("合同 终稿.txt"), "x", messageId: id);

        var response = await server.AttachmentAsync(id, token: token);

        var disposition = Header(response, "Content-Disposition");
        // Kestrel writes headers in ISO-8859-1, so the bare filename can only ever be a fallback; the real name
        // has to travel in the extended form a browser reads.
        Assert.Contains("filename=\"", disposition, StringComparison.Ordinal);
        Assert.Contains($"filename*=UTF-8''{Uri.EscapeDataString("合同 终稿.txt")}", disposition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_archive_streams_onto_the_response_and_never_builds_it_on_disk()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        var token = (await server.PairAsync(server.Pairing.Code)).Token;
        var id = "4d5e6f708192a3b4c5d6e7f809101112";
        // The tree arrives the way a folder really arrives: one directory at a time, then the files under it.
        await server.MkdirAsync(Uri.EscapeDataString("发送 包"), messageId: id);
        await server.MkdirAsync(Uri.EscapeDataString("发送 包/空目录"), messageId: id);
        await server.InboxRawAsync(Uri.EscapeDataString("发送 包/报告+总结.txt"), "一", messageId: id, announcedDirectory: true);
        await server.InboxRawAsync(Uri.EscapeDataString("发送 包/子目录/照片 1.png"), "二", messageId: id, announcedDirectory: true);
        var before = Snapshot(server.Root);

        var response = await server.AttachmentAsync(id, archive: true, token: token);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("filename*=UTF-8''", Header(response, "Content-Disposition"), StringComparison.Ordinal);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        var names = archive.Entries.Select(entry => entry.FullName).ToList();
        // Everything hangs off the folder's own name, so unzipping makes one directory instead of scattering the
        // tree into whatever the phone happened to choose.
        Assert.Contains("发送 包/", names);
        Assert.Contains("发送 包/报告+总结.txt", names);
        Assert.Contains("发送 包/子目录/照片 1.png", names);
        // An empty directory names itself or vanishes, and a peer that sent one expects it back.
        Assert.Contains("发送 包/空目录/", names);
        Assert.Equal("一", ReadEntry(archive, "发送 包/报告+总结.txt"));
        // The archive exists only as the bytes the peer took: no temporary zip beside the source, none in the
        // receive directory, none anywhere under this case's own root.
        Assert.Equal(before, Snapshot(server.Root));
    }

    [Fact]
    public async Task Peers_shows_the_caller_its_own_row_and_nothing_about_anybody_else()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        var mine = (await server.PairAsync(server.Pairing.Code, PeerId)).Token;
        // A spent code is replaced by arming again, which is the same thing the window's 重新生成 button does.
        await server.PairAsync(server.Pairing.Arm(), OtherPeerId);

        var raw = await (await server.GetAsync("/api/v1/peers", token: mine)).Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(raw);
        Assert.Equal(PeerId, document.RootElement.GetProperty("deviceId").GetString());
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
        // A phone has no business learning which other devices this machine trusts.
        Assert.DoesNotContain(OtherPeerId, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_device_forgetting_itself_makes_its_own_token_stop_working()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        var token = (await server.PairAsync(server.Pairing.Code)).Token;
        await server.SendInboxAsync("before.txt", "x", 1, mtime: null, token: token);

        var forgotten = await server.ForgetSelfAsync(token);
        Assert.Equal(HttpStatusCode.NoContent, forgotten.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await server.GetAsync("/api/v1/log", token: token)).StatusCode);
        Assert.Empty(await server.Peers.LoadAsync());
        Assert.Empty(await server.Secrets.SecretDeviceIdsAsync());
    }

    [Fact]
    public async Task An_unknown_route_still_answers_this_protocol_instead_of_a_plain_404_page()
    {
        await using var server = await Harness.StartAsync();

        var response = await server.GetAsync("/api/v1/whatever");

        // A peer that mistyped a path must still learn that it reached a Feng Sync, which is the question the
        // reason body answers.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(LanReason.NotFound, await ReasonAsync(response));
    }

    [Fact]
    public async Task Progress_reaches_the_window_and_ends_at_exactly_the_bytes_that_landed()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        var token = (await server.PairAsync(server.Pairing.Code)).Token;
        var content = new byte[300_000];
        new Random(7).NextBytes(content);
        var seen = new ConcurrentBag<long>();
        server.Host.TransferProgress += progress => seen.Add(progress.Bytes);

        var reply = await server.SendInboxAsync("big.bin", content, content.Length, mtime: null, token: token);

        Assert.Equal(content.Length, reply.Bytes);
        // The last tick has to be the whole file, or a bar would sit one step short of finishing, and the count
        // has to be throttled — a tick per read is thousands of dispatcher posts for one file.
        Assert.Contains((long)content.Length, seen);
        Assert.True(seen.Count < content.Length / 1024, $"{seen.Count} 次进度事件对 {content.Length} 字节：节流没生效。");
    }

    [Fact]
    public async Task The_page_opens_for_a_stranger_and_refuses_to_be_kept()
    {
        await using var server = await Harness.StartAsync();

        // A phone has no credential when it asks for the page — that is the whole point of the page — and a
        // cached copy would show it a pairing code that has already been spent.
        var response = await server.LinkAsync("/p");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("charset=utf-8", Header(response, "Content-Type"), StringComparison.Ordinal);
        Assert.Equal("no-store", Header(response, "Cache-Control"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.StartsWith("<!doctype html>", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_is_one_document_that_asks_nothing_off_this_machine()
    {
        var html = LanPhonePage.Html;

        // The budget is the promise that this stays a page rather than becoming an application, and the two
        // absence checks are the promises that keep it honest on a phone: nothing leaves the local network, and
        // the credential lives only as long as the tab that was handed the code.
        Assert.True(Encoding.UTF8.GetByteCount(html) <= LanPhonePage.MaximumBytes,
            $"{Encoding.UTF8.GetByteCount(html)} 字节，上限 {LanPhonePage.MaximumBytes}");
        Assert.DoesNotContain("http://", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", html, StringComparison.Ordinal);
        Assert.Contains("sessionStorage", html, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", html, StringComparison.Ordinal);
        // Signing a request is not available over plain HTTP, so the page must not reach for an API it has never
        // got — crypto.subtle is undefined here, and a page that assumes it would break at the first upload.
        Assert.DoesNotContain("crypto.subtle", html, StringComparison.Ordinal);
        Assert.Contains("crypto.getRandomValues", html, StringComparison.Ordinal);
        // Every route it names is one this listener maps, in the shape the mapping answers in. There is no listing
        // of shareable rows any more: a phone takes a file by the id its author minted, which is the same address a
        // computer's own 接收 uses, so the page has no second way to point at a byte.
        foreach (var route in new[]
                 {
                     "/api/v1", "/pair", "/hello", "/peers", "/name", "/messages", "/inbox", "/log?since=",
                     "/attachments/", "/receive",
                 })
            Assert.Contains(route, html, StringComparison.Ordinal);
        foreach (var header in new[]
                 {
                     LanProtocol.HeaderRel, LanProtocol.HeaderTotal, LanProtocol.HeaderMtime, LanProtocol.HeaderSession,
                     LanProtocol.HeaderMessage, LanProtocol.HeaderKind,
                 })
            Assert.Contains(header, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pairing_reply_leaves_the_credential_where_a_link_can_reach_it()
    {
        await using var server = await Harness.StartAsync();

        var response = await server.PairRawAsync(server.Pairing.Code);
        var cookie = Header(response, "Set-Cookie");

        // A download is a link, and a link cannot carry a header, so the credential has to travel somewhere the
        // browser will attach by itself. HttpOnly is what keeps it off the page's own reach, and Path=/ because a
        // cookie scoped to /p would not go with a request to /api/v1.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains($"{LanProtocol.CookieName}=", cookie, StringComparison.Ordinal);
        Assert.Contains("HttpOnly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_device_that_removes_itself_leaves_the_browser_with_nothing_to_send()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        var token = (await server.PairAsync(server.Pairing.Code)).Token;
        var id = "5e6f708192a3b4c5d6e7f80910111213";
        await server.InboxRawAsync("a.txt", "x", messageId: id);
        Assert.Equal(HttpStatusCode.OK, (await server.LinkAsync($"/api/v1/attachments/{id}")).StatusCode);

        await server.ForgetSelfAsync(token);

        // Revoking a device has to reach the copy the browser is holding, or deleting the row would leave the
        // phone working off a credential this machine says it no longer has.
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.LinkAsync($"/api/v1/attachments/{id}")).StatusCode);
    }

    [Fact]
    public async Task Closing_the_listener_hands_the_armed_code_over_to_nobody()
    {
        await using var server = await Harness.StartAsync();

        await server.Host.DisposeAsync();

        // A stopped service must not stay answerable, or a code photographed off an old screen would still
        // trade for a token.
        Assert.False(server.Pairing.IsAccepting);
        Assert.Empty(server.Pairing.Code);
    }

    [Fact]
    public async Task A_port_already_held_is_reported_as_the_one_failure_the_window_can_answer()
    {
        await using var taken = await Harness.StartAsync();

        var failure = await Assert.ThrowsAsync<LanTransferException>(async () =>
            await LanServerHost.StartAsync(taken.ServerOptions, CancellationToken.None));

        Assert.Equal(LanRefusal.PortUnavailable, failure.Refusal);
    }

    [Fact]
    public async Task Closing_the_listener_frees_the_port_for_the_next_window()
    {
        var root = Harness.NewRoot();
        var port = Harness.FreePort();
        try
        {
            await using (var first = await Harness.StartAsync(root, port: port))
                Assert.NotEmpty(first.Host.ListenAddresses);

            // The window can be closed and re-opened, so the port has to come back with it rather than wait for
            // the process to exit.
            await using var again = await Harness.StartAsync(root, port: port);
            Assert.Equal(LanApi.WireVersion, (await again.HelloAsync()).V);
        }
        finally
        {
            Harness.Delete(root);
        }
    }

    [Fact]
    public async Task The_room_reads_back_only_for_a_device_that_has_traded_a_code_for_a_credential()
    {
        await using var server = await Harness.StartAsync();

        // Nobody has paired in this case, so the request carries no credential at all — the same shape a stranger
        // gets from every other route, and the reason a page can show 请先配对 instead of a blank list.
        var response = await server.LogRawAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(LanReason.PairRequired, await ReasonAsync(response));
    }

    [Fact]
    public async Task A_watermark_that_is_not_a_number_is_refused_instead_of_bringing_the_whole_history()
    {
        await using var server = await Harness.StartAsync();
        await server.PairAsync(server.Pairing.Code);
        await server.SayAsync("先看报告");

        // A poller that mistyped its watermark would otherwise ask for everything, every few seconds, and look
        // only a little different from one copying the room out.
        Assert.Equal(HttpStatusCode.BadRequest, (await server.LogRawQueryAsync("abc")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.LogRawQueryAsync("-1")).StatusCode);

        Assert.Single((await server.LogAsync()).Entries);
    }

    [Fact]
    public async Task A_sentence_a_peer_says_lands_in_the_room_with_the_author_the_credential_names()
    {
        await using var server = await Harness.StartAsync();
        await server.PairAsync(server.Pairing.Code);

        var said = await server.SayAsync("  先看报告  ");
        var log = await server.LogAsync();

        // The body carried no author, so the only name the entry can wear is the one this machine verified.
        Assert.Equal(PeerId, said.AuthorDeviceId);
        Assert.Equal(LanGroupKind.Text, said.Kind);
        Assert.False(said.HeldHere);
        Assert.Equal(1, said.LocalSeq);
        Assert.Equal(1, log.Head);
        // What the sender got back is what a reader gets, because a page that redraws from its own reply must
        // not show a row the room would describe differently.
        Assert.Equal(said.MessageId, Assert.Single(log.Entries).MessageId);
        Assert.Equal("先看报告", Assert.Single(log.Entries).Text);
    }

    [Fact]
    public async Task An_incremental_read_carries_what_is_past_the_watermark_and_still_names_where_the_room_ends()
    {
        await using var server = await Harness.StartAsync();
        await server.PairAsync(server.Pairing.Code);
        await server.SayAsync("一");
        await server.SayAsync("二");
        await server.SayAsync("三");

        var first = await server.LogAsync();
        var rest = await server.LogAsync(2);

        Assert.Equal(3, first.Entries.Count);
        Assert.Equal(["三"], rest.Entries.Select(entry => entry.Text));
        // The head is the room's, not the page's: a caller that was told nothing new still learns where the end
        // is, which is what lets it stop asking for a gap it already crossed.
        Assert.Equal(3, rest.Head);
    }

    [Fact]
    public async Task A_device_that_was_refused_its_bytes_is_still_allowed_to_speak()
    {
        await using var server = await Harness.StartAsync(
            confirm: (offer, _) => Task.FromResult(false));
        await server.PairAsync(server.Pairing.Code);

        var declined = await server.InboxRawAsync("report.txt", "内容");
        var said = await server.SayRawAsync("文件你拒绝了，我先说一句");

        Assert.Equal(HttpStatusCode.Forbidden, declined.StatusCode);
        Assert.Equal(LanReason.Declined, await ReasonAsync(declined));
        Assert.Equal(HttpStatusCode.Created, said.StatusCode);
        // The gate exists for bytes landing on the user's disk. A sentence costs a slot in a capped in-memory
        // copy, which pairing alone already bought, and folding the two into one refusal would tell the phone
        // that its whole window is closed.
        Assert.Equal("文件你拒绝了，我先说一句", Assert.Single((await server.LogAsync()).Entries).Text);
    }

    [Fact]
    public async Task A_sentence_too_long_for_the_history_answers_with_the_one_thing_the_sender_can_do_about_it()
    {
        await using var server = await Harness.StartAsync();
        await server.PairAsync(server.Pairing.Code);

        var response = await server.SayRawAsync(new string('字', LanGroupLog.MaximumTextLength + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(LanReason.MessageTooLong, await ReasonAsync(response));
        // The refusal costs no slot: a sentence too long to keep has not taken a row from the newest one.
        Assert.Empty((await server.LogAsync()).Entries);

        Assert.Equal(HttpStatusCode.Created, (await server.SayRawAsync(new string('字', LanGroupLog.MaximumTextLength))).StatusCode);
    }

    [Fact]
    public async Task A_sentence_that_says_nothing_is_refused_as_a_request_rather_than_stored_as_blank()
    {
        await using var server = await Harness.StartAsync();
        await server.PairAsync(server.Pairing.Code);

        Assert.Equal(HttpStatusCode.BadRequest, (await server.SayRawAsync("   ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.SayRawAsync(null)).StatusCode);
        Assert.Empty((await server.LogAsync()).Entries);
    }

    [Fact]
    public async Task An_unparsable_message_body_answers_bad_request_instead_of_failing_the_route()
    {
        await using var server = await Harness.StartAsync();
        await server.PairAsync(server.Pairing.Code);

        var response = await server.SayRawBodyAsync("{ this is not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(LanReason.BadRequest, await ReasonAsync(response));
    }

    [Fact]
    public async Task An_entry_adopted_from_elsewhere_is_served_to_a_poller_as_this_machines_copy_of_it()
    {
        await using var server = await Harness.StartAsync();
        await server.PairAsync(server.Pairing.Code);
        // What a relay does: an entry pulled from a peer lands in this copy, and the next page asking this
        // machine for the room must see it — that is how a phone reaches a sentence another computer spoke.
        server.ServerOptions.Session.Group.TryMerge(new LanMessageWire("heard-from-a-peer", OtherPeerId,
            "另一台电脑", 4, LanGroupKind.File, null, "合同.pdf", null, 4096, false, OldStamp, true));

        var entry = Assert.Single((await server.LogAsync()).Entries);

        Assert.Equal("heard-from-a-peer", entry.MessageId);
        Assert.Equal(OtherPeerId, entry.AuthorDeviceId);
        Assert.Equal(4096, entry.Bytes);
        // HeldHere comes back false because this machine does not hold the bytes, whatever the machine it heard
        // the entry from claimed about itself.
        Assert.False(entry.HeldHere);
        // A peer's arrival number is not adopted, so the watermark this page tracks stays one this machine owns.
        Assert.Equal(1, entry.LocalSeq);
    }

    [Fact]
    public async Task A_line_about_this_window_is_shown_to_its_own_machine_and_to_nobody_else()
    {
        await using var server = await Harness.StartAsync();
        await server.PairAsync(server.Pairing.Code);
        await server.SayAsync("先看报告");
        server.ServerOptions.Session.Group.AppendSystem("防火墙规则已加上", OldStamp);

        var log = await server.LogAsync();

        // Narration belongs to the window that wrote it: a peer told about another machine's firewall question
        // would show a sentence nobody there agreed to.
        Assert.Equal(["先看报告"], log.Entries.Select(entry => entry.Text));
        Assert.Equal(2, server.ServerOptions.Session.Group.Count);
        // The watermark still counts the line it hid, or a poller would be asked to re-read past it.
        Assert.Equal(2, log.Head);
    }

    [Fact]
    public async Task A_push_that_names_the_entry_it_belongs_to_lands_and_becomes_askable_by_that_id()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        await server.PairAsync(server.Pairing.Code);

        await server.InboxRawAsync("report.txt", "合同 1+1", messageId: "9a8b7c6d5e4f1a0b3c2d1e0f9a8b7c6d");

        var response = await server.AttachmentAsync("9a8b7c6d5e4f1a0b3c2d1e0f9a8b7c6d");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The bytes come back as the file itself, not as a JSON description of it, and under the name the sink
        // really stored rather than the one the sender hoped for.
        Assert.Equal("合同 1+1", await response.Content.ReadAsStringAsync());
        Assert.Contains("attachment", Header(response, "Content-Disposition"), StringComparison.Ordinal);

        var entry = Assert.Single((await server.LogAsync()).Entries);
        Assert.Equal("9a8b7c6d5e4f1a0b3c2d1e0f9a8b7c6d", entry.MessageId);
        Assert.Equal(PeerId, entry.AuthorDeviceId);
        Assert.Equal(LanGroupKind.File, entry.Kind);
        Assert.Equal("report.txt", entry.FileName);
        Assert.Equal(Encoding.UTF8.GetByteCount("合同 1+1"), entry.Bytes);
        Assert.True(entry.HeldHere);
    }

    [Fact]
    public async Task A_tree_that_arrives_file_by_file_under_one_id_is_announced_as_one_folder_and_not_as_many_files()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        await server.PairAsync(server.Pairing.Code);
        var folder = "7d6c5b4a39281706f5e4d3c2b1a09876";

        // The order a sender really sends in: the folder's own path first, then every file under it.
        await server.MkdirAsync("tree", messageId: folder);
        await server.InboxRawAsync("tree/a.txt", "A", messageId: folder, announcedDirectory: true);
        await server.InboxRawAsync("tree/deep/b.txt", "B", messageId: folder, announcedDirectory: true);

        var entry = Assert.Single((await server.LogAsync()).Entries);
        Assert.Equal(LanGroupKind.Directory, entry.Kind);
        Assert.True(entry.IsDirectory);
        // The entry names the tree's root rather than whichever file landed beside it, because that is the thing a
        // member fetching it later can ask for as one archive.
        Assert.Equal("tree", entry.FileName);
        Assert.Equal("tree", entry.RelativePath);
        // A folder reports no size, which is the same choice the serving description makes: totalling a subtree is a walk
        // nobody asked for at the moment the entry is made.
        Assert.Equal(0, entry.Bytes);

        var archive = await server.AttachmentAsync(folder, archive: true);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        Assert.Equal("application/zip", archive.Content.Headers.ContentType?.MediaType);
        using var zip = new System.IO.Compression.ZipArchive(await archive.Content.ReadAsStreamAsync(), System.IO.Compression.ZipArchiveMode.Read);
        Assert.Equal(["tree/", "tree/a.txt", "tree/deep/b.txt"], zip.Entries.Select(each => each.FullName).Order(StringComparer.Ordinal));

        // A folder has no single-length answer, so the bare route refuses it rather than handing out a zip under a
        // name that would read as one file.
        Assert.Equal(HttpStatusCode.BadRequest, (await server.AttachmentAsync(folder)).StatusCode);
    }

    [Fact]
    public async Task A_push_that_names_another_member_s_announcement_lands_as_a_file_and_moves_nobody_s_entry()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        await server.PairAsync(server.Pairing.Code);
        var theirs = "1234567890abcdef1234567890abcdef";
        server.ServerOptions.Session.Group.TryMerge(new LanMessageWire(theirs, OtherPeerId, "另一台电脑", 1,
            LanGroupKind.File, null, "contract.pdf", null, 4096, false, OldStamp, false));

        var push = await server.InboxRawAsync("other.txt", "不是那份", messageId: theirs);

        // The bytes are what the user agreed to receive, so a tag that cannot be honoured is no reason to refuse them.
        Assert.Equal(HttpStatusCode.Created, push.StatusCode);
        var entry = Assert.Single((await server.LogAsync()).Entries);
        Assert.Equal(OtherPeerId, entry.AuthorDeviceId);
        Assert.False(entry.HeldHere);
        // And the read still refuses: an entry this copy does not hold is not made servable by a stranger's push,
        // which is the difference between a room and a place where any device can put any file's bytes behind any
        // other device's name.
        Assert.Equal(HttpStatusCode.NotFound, (await server.AttachmentAsync(theirs)).StatusCode);
    }

    [Fact]
    public async Task An_entry_adopted_from_elsewhere_is_refused_here_rather_than_served_on_the_announcer_s_word()
    {
        await using var server = await Harness.StartAsync();
        await server.PairAsync(server.Pairing.Code);
        server.ServerOptions.Session.Group.TryMerge(new LanMessageWire("heard-from-a-peer", OtherPeerId,
            "另一台电脑", 4, LanGroupKind.File, null, "合同.pdf", null, 4096, false, OldStamp, true));

        var response = await server.AttachmentAsync("heard-from-a-peer");

        // The peer's copy said it held the file; this copy never saw a byte of it, so the honest answer to a poller
        // choosing where to fetch is 这里没有 rather than the empty 200 a trusting implementation would hand back.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(LanReason.NotFound, await ReasonAsync(response));
    }

    [Fact]
    public async Task A_member_that_cannot_be_dialled_asks_its_relay_and_the_answer_names_no_path()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        await server.PairAsync(server.Pairing.Code);
        var id = "5e4d3c2b1a09876f5e4d3c2b1a098765";
        await server.InboxRawAsync("quarter-report.txt", "内容", messageId: id);

        var response = await server.TakeAsync(id);

        // A browser cannot dial anybody, so this route is the only way it can want a file. When the bytes are
        // already here the answer is the row and nothing else: no second pull, and no duplicate landed under a
        // renamed name, because already-here is a success rather than a retry.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var row = JsonSerializer.Deserialize<LanMessageWire>(body, LanApi.Json)!;
        Assert.Equal(id, row.MessageId);
        Assert.True(row.HeldHere);
        // And the answer names no path. LocalPath is a statement about this machine's disk, and a phone that read
        // it would learn where the user keeps their files while gaining nothing it could press.
        Assert.DoesNotContain("localPath", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_file_the_relay_holds_is_downloadable_by_a_link_that_carries_nothing_but_the_cookie()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        await server.PairAsync(server.Pairing.Code);
        var id = "7a6b5c4d3e2f10987a6b5c4d3e2f1098";
        await server.InboxRawAsync("tail.txt", "0123456789", messageId: id);

        // The page's 接收 button answers with a row, and the row is then handed to the browser as a plain link —
        // so the read route has to accept a request that carries nothing but that cookie, or a tap on it would
        // come back as a pairing refusal for a device that paired two requests ago.
        var link = $"/api/v1/attachments/{id}";
        var whole = await server.LinkAsync(link);
        var tail = await server.LinkAsync(link, new RangeHeaderValue(5, null));

        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        Assert.Equal("0123456789", await whole.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.PartialContent, tail.StatusCode);
        Assert.Equal("bytes 5-9/10", Header(tail, "Content-Range"));
        Assert.Equal("56789", await tail.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Asking_a_relay_for_a_file_it_does_not_hold_refuses_rather_than_answering_empty()
    {
        await using var server = await Harness.StartAsync();
        await server.PairAsync(server.Pairing.Code);
        server.ServerOptions.Session.Group.TryMerge(new LanMessageWire("heard-from-a-peer", OtherPeerId,
            "另一台电脑", 4, LanGroupKind.File, null, "合同.pdf", null, 4096, false, OldStamp, true));

        var response = await server.TakeAsync("heard-from-a-peer");

        // The peer's copy claimed the bytes and this copy has none, and there is nobody here to ask: the room
        // answers the same sentence a computer's own 接收 would have got, rather than publishing an empty file to
        // the receive directory because some device once said it had something.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(LanReason.NotFound, await ReasonAsync(response));
        Assert.Empty(Directory.EnumerateFileSystemEntries(server.Receive));
    }

    [Fact]
    public async Task A_relay_request_is_refused_before_the_room_is_asked_by_anybody_without_a_credential()
    {
        await using var server = await Harness.StartAsync();

        var response = await server.TakeAsync("5e4d3c2b1a09876f5e4d3c2b1a098765");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(LanReason.PairRequired, await ReasonAsync(response));
    }

    [Fact]
    public async Task An_id_this_copy_has_never_heard_of_is_refused_at_the_relay_without_anybody_being_asked()
    {
        await using var server = await Harness.StartAsync();
        await server.PairAsync(server.Pairing.Code);

        var response = await server.TakeAsync("not-an-id");

        // Not a bad request: a member keeping its own copy of the room cannot tell a caller whether the id was
        // never an id, never heard, or heard and not held, and the read route already answers all three as 这里没有.
        // What matters is that nothing was dialed for it — an id is a key in this machine's own log and never a
        // path, so a caller that invents keys spends a refusal rather than a round trip per member.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(LanReason.NotFound, await ReasonAsync(response));
    }

    [Fact]
    public async Task A_held_entry_whose_file_went_away_stops_being_offered_and_keeps_what_the_room_said()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        await server.PairAsync(server.Pairing.Code);
        var id = "5e4d3c2b1a09876f5e4d3c2b1a098765";
        await server.InboxRawAsync("gone.txt", "内容", messageId: id);
        File.Delete(Path.Combine(server.Receive, "gone.txt"));

        Assert.Equal(HttpStatusCode.NotFound, (await server.AttachmentAsync(id)).StatusCode);

        var entry = Assert.Single((await server.LogAsync()).Entries);
        // The sentence stays — what a member said is not this machine's to erase — while the promise goes with it,
        // so no later poller is invited to ask a member that can only refuse.
        Assert.False(entry.HeldHere);
        Assert.Equal("gone.txt", entry.FileName);
    }

    [Fact]
    public async Task An_id_that_could_not_be_a_message_is_answered_as_absent_without_reaching_the_room()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        await server.PairAsync(server.Pairing.Code);
        var id = "3c2b1a09f8e7d6c5b4a39281706f5e4d";
        await server.InboxRawAsync("one.txt", "1", messageId: id);

        // A value of the wrong shape is the same answer as a well-formed id nobody holds: a caller learns that this
        // machine cannot serve it and nothing about which of the two it typed.
        Assert.Equal(HttpStatusCode.NotFound, (await server.AttachmentAsync("not a message id!")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.AttachmentAsync("3c2b")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.AttachmentAsync(id)).StatusCode);
    }

    [Fact]
    public async Task A_stranger_cannot_read_an_attachment_because_the_room_is_not_a_public_board()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        var paired = await server.PairAsync(server.Pairing.Code);
        var id = "0123456789abcdef0123456789abcdef";
        await server.InboxRawAsync("one.txt", "1", messageId: id);

        // A device this listener has never issued to, holding a token that belongs to somebody else: the file is in
        // the room, and that room is not a public board.
        var response = await server.AttachmentAsync(id, token: paired.Token, deviceId: OtherPeerId);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(LanReason.PairRequired, await ReasonAsync(response));
    }

    [Fact]
    public async Task A_write_that_named_no_entry_stays_a_file_and_says_nothing_to_the_room()
    {
        await using var server = await Harness.StartAsync(confirm: Accepted);
        await server.PairAsync(server.Pairing.Code);

        await server.SendInboxAsync("plain.txt", "内容", 6, OldStamp);

        // A peer that knows nothing of the room — or is sending to a window that has no room in it — gets the same
        // landing as always, and no entry appears that nobody announced.
        Assert.Empty((await server.LogAsync()).Entries);
        Assert.True(File.Exists(Path.Combine(server.Receive, "plain.txt")));
    }

    [Fact]
    public async Task A_device_that_renames_itself_is_listed_under_the_new_name_from_then_on()
    {
        await using var server = await Harness.StartAsync();
        await server.PairAsync(server.Pairing.Code);

        var kept = await server.NameAsync("小红的平板");

        Assert.Equal("小红的平板", kept);
        Assert.Equal("小红的平板", (await server.ServerOptions.Session.Peers.LoadAsync())
            .Single(record => record.DeviceId == PeerId).DeviceName);
        // The record is what a person read and agreed to, so a later sentence is authored under the new name even
        // though the write carries no name header at all — which is the whole reason a rename has to reach the row
        // rather than be a claim re-read on every request.
        Assert.Equal("小红的平板", (await server.SayAsync("二")).AuthorName);
    }

    [Fact]
    public async Task A_name_too_long_for_a_row_comes_back_as_the_name_that_was_actually_stored()
    {
        await using var server = await Harness.StartAsync();
        await server.PairAsync(server.Pairing.Code);

        var kept = await server.NameAsync(new string('板', LanProtocol.MaximumNameLength + 12));

        // The caller is told what it got, because a device that kept showing its own sixty characters would be
        // listing a name no member of the room can read.
        Assert.Equal(new string('板', LanProtocol.MaximumNameLength), kept);
        Assert.Equal(kept, (await server.ServerOptions.Session.Peers.LoadAsync())
            .Single(record => record.DeviceId == PeerId).DeviceName);
    }

    [Fact]
    public async Task An_empty_name_is_refused_and_leaves_the_row_the_person_already_agreed_to()
    {
        await using var server = await Harness.StartAsync();
        await server.PairAsync(server.Pairing.Code);

        var response = await server.NameRawAsync("   ");

        // Which name to fall back to is the user's decision, so this route guesses nothing and writes nothing: an
        // empty row is the failure the fallback exists to avoid.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("小明的手机", (await server.ServerOptions.Session.Peers.LoadAsync())
            .Single(record => record.DeviceId == PeerId).DeviceName);
        // And a device this listener has never issued a token to cannot rename anybody at all, which is the same
        // answer every other verb gives: the room is not a public board.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await server.NameRawAsync("路人", token: "never-issued", deviceId: OtherPeerId)).StatusCode);
    }

    [Fact]
    public async Task A_rename_reaches_the_row_the_credential_proves_and_no_other_member_s()
    {
        await using var server = await Harness.StartAsync();
        var mine = await server.PairAsync(server.Pairing.Code);
        var theirs = await server.PairAsync(server.Pairing.Arm(), OtherPeerId);
        var seen = new List<LanPeerRenamedEventArgs>();
        server.Host.PeerRenamed += seen.Add;

        // A body that invents a deviceId field is inert: LanNameRequest has no such property, and the row written
        // is the one the credential names — so no device can relabel another member.
        var response = await server.NameRawBodyAsync(
            $"{{\"deviceId\":\"{PeerId}\",\"deviceName\":\"改过的名字\"}}", token: theirs.Token, deviceId: OtherPeerId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var records = await server.ServerOptions.Session.Peers.LoadAsync();
        Assert.Equal("改过的名字", records.Single(record => record.DeviceId == OtherPeerId).DeviceName);
        Assert.Equal("小明的手机", records.Single(record => record.DeviceId == PeerId).DeviceName);
        // The window needs the old name to say 改名 at all, and the room's earlier rows keep the name they were
        // said under, so the event is the only place the two meet.
        var notice = Assert.Single(seen);
        Assert.Equal("小明的手机", notice.PreviousName);
        Assert.Equal(OtherPeerId, notice.Contact.DeviceId);
        Assert.Equal("改过的名字", notice.Contact.DeviceName);
        Assert.NotEqual(mine.Token, theirs.Token);
    }

    private static Task<bool> Accepted(LanInboundOffer offer, CancellationToken ct) => Task.FromResult(true);

    private static string ReadEntry(System.IO.Compression.ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task<string> ReasonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("reason").GetString()!;
    }

    /// <summary>
    /// One header, whichever side of the response it belongs to.
    /// </summary>
    /// <remarks>
    /// <c>Content-Disposition</c> and <c>Content-Range</c> are content-classified by <see cref="HttpResponseMessage"/>,
    /// so reading them off <c>response.Headers</c> would answer empty and a test would assert against a mistake of
    /// its own rather than against the wire.
    /// </remarks>
    private static string Header(HttpResponseMessage response, string name)
    {
        if (response.Content.Headers.TryGetValues(name, out var content)) return string.Join(",", content);
        return response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";
    }

    private static IReadOnlyList<string> Snapshot(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// One listener, one port, one directory tree, one set of stores — everything a case needs to be alone.
    /// </summary>
    /// <remarks>
    /// The room is this machine's own copy of itself, so a case that wants a peer to read a file puts a held entry
    /// into it rather than into a list the listener serves. The clock is an object a case can advance, because a
    /// code that has aged out would otherwise cost two minutes of waiting.
    /// </remarks>
    private sealed class Harness : IAsyncDisposable
    {
        private string? _token;

        private Harness(string root, LanPairingState pairing, LanPeerStore peers, LanPeerSecretStore secrets,
            LanReceiveSink sink, TestClock clock, LanGroupRoom reader, LanPeerClient dialer,
            LanServerOptions serverOptions)
        {
            Root = root;
            Receive = Path.Combine(root, "in");
            Pairing = pairing;
            Peers = peers;
            Secrets = secrets;
            Sink = sink;
            Clock = clock;
            Reader = reader;
            Dialer = dialer;
            ServerOptions = serverOptions;
            Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{serverOptions.Session.Options.Port}") };
        }

        public string Root { get; }
        public string Receive { get; }
        public LanPairingState Pairing { get; }
        public LanPeerStore Peers { get; }
        public LanPeerSecretStore Secrets { get; }
        public LanReceiveSink Sink { get; }
        public TestClock Clock { get; }
        public HttpClient Http { get; }

        /// <summary>
        /// This machine's room reader, never started. The listener answers on its behalf for a member that cannot
        /// be dialled, so a case that asks for a file has to be asking a listener that has one.
        /// </summary>
        public LanGroupRoom Reader { get; }

        /// <summary>The reader's own dialer, which the room deliberately does not own and this harness therefore does.</summary>
        public LanPeerClient Dialer { get; }

        /// <summary>What was handed to <see cref="LanServerHost.StartAsync"/>, so a case can point a second listener at the same port.</summary>
        public LanServerOptions ServerOptions { get; }

        public LanServerHost Host { get; private set; } = null!;

        public static string NewRoot() => Path.Combine(Path.GetTempPath(), "fengsync-lan-wire-" + Guid.NewGuid().ToString("N"));

        /// <summary>
        /// A port to bind, taken from the ephemeral range and handed straight back.
        /// </summary>
        /// <remarks>
        /// The listener needs a stable port for a firewall rule to name it, so a case asks the OS which number is
        /// free rather than binding 0 and reading back what it got.
        /// </remarks>
        public static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public static async Task<Harness> StartAsync(
            string? root = null,
            int? port = null,
            bool autoAccept = false,
            Func<LanInboundOffer, CancellationToken, Task<bool>>? confirm = null)
        {
            root ??= NewRoot();
            var receive = Path.Combine(root, "in");
            Directory.CreateDirectory(receive);
            var clock = new TestClock();
            var pairing = new LanPairingState(() => clock.Now);
            var peers = new LanPeerStore(Path.Combine(root, "peers.json"));
            var secrets = new LanPeerSecretStore(Path.Combine(root, "secrets.dat"));
            var sink = new LanReceiveSink(receive, AppendCounter);
            var options = new LanOptions(port ?? FreePort(), receive, autoAccept);
            var identity = await LanDeviceIdentity.LoadOrCreateAsync(Path.Combine(root, "device.json"));
            var session = new LanSession(options, peers, secrets, pairing, confirm);
            // A reader that is never started holds no beat: a case that presses 接收 wants one pull, decided now,
            // with nothing else in the room dialing behind it.
            var dialer = new LanPeerClient(identity, peers, secrets);
            var reader = new LanGroupRoom(identity, dialer, peers, session.Group, sink);
            var harness = new Harness(root, pairing, peers, secrets, sink, clock, reader, dialer,
                new LanServerOptions(session, identity, pairing, sink) { Room = reader });
            harness.Host = await LanServerHost.StartAsync(harness.ServerOptions, CancellationToken.None);
            return harness;
        }

        /// <summary>Moves the injected clock forward, which is how a code is aged out without waiting two minutes.</summary>
        public void AdvanceClock(int seconds) => Clock.Now = Clock.Now.AddSeconds(seconds);

        public async Task<LanHelloReply> HelloAsync() =>
            Deserialize<LanHelloReply>(await Http.GetStringAsync("/api/v1/hello"));

        public async Task<LanPairReply> PairAsync(string code, string deviceId = PeerId)
        {
            var response = await PairRawAsync(code, deviceId);
            response.EnsureSuccessStatusCode();
            var reply = Deserialize<LanPairReply>(await response.Content.ReadAsStringAsync());
            // The credential this harness presents from now on, which is what a paired peer does.
            _token = reply.Token;
            return reply;
        }

        public Task<HttpResponseMessage> PairRawAsync(string code, string deviceId = PeerId) =>
            Http.PostAsync("/api/v1/pair", Json(new LanPairRequest(code, deviceId, "小明的手机", LanProtocol.KindPhone)));

        public async Task<LanInboxReply> SendInboxAsync(string wire, string content, long declaredBytes, DateTimeOffset? mtime, string? token = null)
        {
            var response = await InboxRawAsync(wire, content, declaredBytes, mtime, token);
            response.EnsureSuccessStatusCode();
            return Deserialize<LanInboxReply>(await response.Content.ReadAsStringAsync());
        }

        public async Task<LanInboxReply> SendInboxAsync(string wire, byte[] content, long declaredBytes, DateTimeOffset? mtime, string? token = null)
        {
            var response = await InboxRawAsync(wire, content, declaredBytes, mtime, token);
            response.EnsureSuccessStatusCode();
            return Deserialize<LanInboxReply>(await response.Content.ReadAsStringAsync());
        }

        public Task<HttpResponseMessage> InboxRawAsync(string wire, string content, long? declaredBytes = null, DateTimeOffset? mtime = null, string? token = null,
            string? messageId = null, bool announcedDirectory = false) =>
            InboxRawAsync(wire, Encoding.UTF8.GetBytes(content), declaredBytes, mtime, token, messageId, announcedDirectory);

        public Task<HttpResponseMessage> InboxRawAsync(string wire, byte[] content, long? declaredBytes = null, DateTimeOffset? mtime = null, string? token = null,
            string? messageId = null, bool announcedDirectory = false)
        {
            var request = Request(HttpMethod.Post, "/api/v1/inbox", PeerId, token);
            request.Content = new ByteArrayContent(content);
            if (declaredBytes is { } bytes) request.Headers.Add(LanProtocol.HeaderTotal, bytes.ToString());
            if (mtime is { } stamp) request.Headers.Add(LanProtocol.HeaderMtime, stamp.ToUnixTimeMilliseconds().ToString());
            request.Headers.Add(LanProtocol.HeaderRel, wire);
            AddAnnouncement(request, messageId, announcedDirectory);
            return SendAsync(request);
        }

        public async Task<LanMkdirReply> MkdirAsync(string wire, string? token = null, string? messageId = null)
        {
            var request = Request(HttpMethod.Post, "/api/v1/mkdir", PeerId, token);
            request.Content = new StringContent("");
            request.Headers.Add(LanProtocol.HeaderRel, wire);
            AddAnnouncement(request, messageId, isDirectory: true);
            var response = await SendAsync(request);
            response.EnsureSuccessStatusCode();
            return Deserialize<LanMkdirReply>(await response.Content.ReadAsStringAsync());
        }

        /// <summary>
        /// Tags a write with the room entry it belongs to, which is the whole of what a sender may say about a file
        /// it is pushing. The kind is a separate header because a folder arrives as many writes and the entry that
        /// stands for them has to be a folder whichever write happened to land first.
        /// </summary>
        private static void AddAnnouncement(HttpRequestMessage request, string? messageId, bool isDirectory)
        {
            if (messageId is null) return;
            request.Headers.Add(LanProtocol.HeaderMessage, messageId);
            if (isDirectory) request.Headers.Add(LanProtocol.HeaderKind, LanGroupKind.Directory);
        }

        /// <summary>
        /// This copy of the room, past a watermark. A peer reads it on a timer and merges whatever is new, so the
        /// reply's shape is the whole of what a second machine can learn from this one.
        /// </summary>
        public async Task<LanLogReply> LogAsync(long? since = null, string? token = null)
        {
            var response = await LogRawAsync(since, token);
            response.EnsureSuccessStatusCode();
            return Deserialize<LanLogReply>(await response.Content.ReadAsStringAsync());
        }

        public Task<HttpResponseMessage> LogRawAsync(long? since = null, string? token = null) =>
            GetAsync(since is { } watermark ? $"/api/v1/log?since={watermark}" : "/api/v1/log", token: token);

        /// <summary>A <c>since</c> as a mistyped query would carry it, because the route reads a number and not a word.</summary>
        public Task<HttpResponseMessage> LogRawQueryAsync(string since, string? token = null) =>
            GetAsync($"/api/v1/log?since={Uri.EscapeDataString(since)}", token: token);

        /// <summary>Says something in the room, from the device its credential names.</summary>
        public async Task<LanMessageWire> SayAsync(string text, string? token = null)
        {
            var response = await SayRawAsync(text, token);
            response.EnsureSuccessStatusCode();
            return Deserialize<LanMessageWire>(await response.Content.ReadAsStringAsync());
        }

        public Task<HttpResponseMessage> SayRawAsync(string? text, string? token = null)
        {
            var request = Request(HttpMethod.Post, "/api/v1/messages", PeerId, token);
            request.Content = Json(new LanSayRequest(text));
            return SendAsync(request);
        }

        public Task<HttpResponseMessage> SayRawBodyAsync(string json, string? token = null)
        {
            var request = Request(HttpMethod.Post, "/api/v1/messages", PeerId, token);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return SendAsync(request);
        }

        public Task<HttpResponseMessage> GetAsync(string path, string deviceId = PeerId, string? token = null) =>
            SendAsync(Request(HttpMethod.Get, path, deviceId, token));

        /// <summary>
        /// Asks for the bytes behind one room entry, the way a member that wants a file it was only told about does.
        /// </summary>
        /// <remarks>
        /// The id is escaped because a case that names a value this protocol would never mint has to reach the route
        /// to be refused by it, rather than mangled into some other address by the client.
        /// </remarks>
        public Task<HttpResponseMessage> AttachmentAsync(string id, bool archive = false, string? token = null,
            string? deviceId = null, RangeHeaderValue? range = null) =>
            SendAsync(Request(HttpMethod.Get,
                $"/api/v1/attachments/{Uri.EscapeDataString(id)}{(archive ? "/zip" : "")}", deviceId ?? PeerId, token, range));

        /// <summary>A device removing its own credential, which is the one thing a peer may do to the trust list.</summary>
        public Task<HttpResponseMessage> ForgetSelfAsync(string token) =>
            SendAsync(Request(HttpMethod.Delete, "/api/v1/peers/mine", PeerId, token));

        /// <summary>
        /// Asks this machine to become the holder of one room entry: the move a member makes when it cannot dial
        /// anybody and the browser's only reachable machine is the one it paired with.
        /// </summary>
        public Task<HttpResponseMessage> TakeAsync(string id, string? token = null, string? deviceId = null) =>
            SendAsync(Request(HttpMethod.Post,
                $"/api/v1/attachments/{Uri.EscapeDataString(id)}/receive", deviceId ?? PeerId, token));

        /// <summary>Says what this device wants to be called, and reads back the name that was kept.</summary>
        public async Task<string> NameAsync(string deviceName, string? token = null, string? deviceId = null)
        {
            var response = await NameRawAsync(deviceName, token, deviceId);
            response.EnsureSuccessStatusCode();
            return Deserialize<LanNameReply>(await response.Content.ReadAsStringAsync()).DeviceName;
        }

        public Task<HttpResponseMessage> NameRawAsync(string? deviceName, string? token = null, string? deviceId = null)
        {
            var request = Request(HttpMethod.Post, "/api/v1/name", deviceId ?? PeerId, token);
            request.Content = Json(new LanNameRequest(deviceName));
            return SendAsync(request);
        }

        /// <summary>
        /// A rename claim as raw JSON, because the shape worth pinning is a body that invents a field: a peer
        /// naming somebody else's device id must be answered with its own row moving and nobody else's.
        /// </summary>
        public Task<HttpResponseMessage> NameRawBodyAsync(string json, string? token = null, string? deviceId = null)
        {
            var request = Request(HttpMethod.Post, "/api/v1/name", deviceId ?? PeerId, token);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return SendAsync(request);
        }

        /// <summary>
        /// A request as a browser makes it when a person taps a link: no header of ours, only what the browser
        /// already stores for this origin.
        /// </summary>
        /// <remarks>
        /// This is the shape a download has to work in. A page can put a header on its own <c>fetch</c>, but a
        /// link the browser streams — which is the only way a phone gets a big file with a progress bar it can
        /// resume — carries nothing but its address and its cookie.
        /// </remarks>
        public Task<HttpResponseMessage> LinkAsync(string path, RangeHeaderValue? range = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            if (range is not null) request.Headers.Range = range;
            return Http.SendAsync(request);
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => Http.SendAsync(request);

        /// <summary>
        /// A request, carrying the credential this harness holds unless the case names another one.
        /// </summary>
        /// <remarks>
        /// The path is percent-encoded here rather than by the caller: route values are what the framework
        /// decodes, and a Chinese filename in one of them must survive exactly as it does in a header.
        /// </remarks>
        private HttpRequestMessage Request(HttpMethod method, string path, string deviceId, string? token, RangeHeaderValue? range = null)
        {
            var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            var credential = token ?? _token;
            if (credential is not null)
                request.Headers.Add("Authorization", $"{LanProtocol.AuthScheme} {deviceId}:{credential}");
            if (range is not null) request.Headers.Range = range;
            return request;
        }

        private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, LanApi.Json)!;

        private static StringContent Json(object body) =>
            new(JsonSerializer.Serialize(body, LanApi.Json), Encoding.UTF8, "application/json");

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

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await Reader.DisposeAsync();
            Dialer.Dispose();
            Http.Dispose();
            // A case that hands its stores to a second window keeps the directory and deletes it itself.
            if (!KeepRoot) Delete(Root);
        }

        /// <summary>Leave the directory tree in place, because another window is about to read these stores.</summary>
        public bool KeepRoot { get; set; }

        public static void Delete(string root)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A clock a case can move, so a two-minute code lifetime costs a method call rather than two minutes.</summary>
    private sealed class TestClock
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    }
}
