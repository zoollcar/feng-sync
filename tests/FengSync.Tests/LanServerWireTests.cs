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

        var response = await server.GetAsync("/api/v1/basket");

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
        var stranger = await server.GetAsync("/api/v1/basket", OtherPeerId, forged);
        Assert.Equal(LanReason.PairRequired, await ReasonAsync(stranger));

        var known = await server.GetAsync("/api/v1/basket", PeerId, forged);
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
    public async Task The_basket_is_all_a_token_can_read_and_a_path_is_not_part_of_it()
    {
        await using var server = await Harness.StartAsync();
        await server.PairAsync(server.Pairing.Code);
        var shared = LanSendItem.Of(server.AddFile("可分享.txt", "可以读"));
        server.Basket.Add(shared);
        var outside = server.AddFile("不在篮里.txt", "不该被读到");

        var row = Assert.Single(await server.BasketAsync());
        Assert.Equal("可分享.txt", row.Name);
        // The number the phone sizes its download against is the file's own length on disk.
        Assert.Equal(new FileInfo(shared.AbsolutePath).Length, row.Bytes);
        Assert.False(row.IsDirectory);
        // The route takes an id and nothing else, so no request can name a file on this machine's disk.
        var missed = await server.GetAsync($"/api/v1/basket/{Uri.EscapeDataString(outside)}");
        Assert.Equal(HttpStatusCode.NotFound, missed.StatusCode);
    }

    [Fact]
    public async Task A_range_request_answers_206_with_the_tail_the_phone_asked_for()
    {
        await using var server = await Harness.StartAsync();
        var token = (await server.PairAsync(server.Pairing.Code)).Token;
        server.Basket.Add(LanSendItem.Of(server.AddFile("tail.txt", "0123456789")));
        var id = server.Basket.Single().Id.ToString("N");

        var response = await server.DownloadAsync(id, token, range: new RangeHeaderValue(3, null));

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
        await using var server = await Harness.StartAsync();
        var token = (await server.PairAsync(server.Pairing.Code)).Token;
        server.Basket.Add(LanSendItem.Of(server.AddFile("合同 终稿.txt", "x")));

        var response = await server.DownloadAsync(server.Basket.Single().Id.ToString("N"), token);

        var disposition = Header(response, "Content-Disposition");
        // Kestrel writes headers in ISO-8859-1, so the bare filename can only ever be a fallback; the real name
        // has to travel in the extended form a browser reads.
        Assert.Contains("filename=\"", disposition, StringComparison.Ordinal);
        Assert.Contains($"filename*=UTF-8''{Uri.EscapeDataString("合同 终稿.txt")}", disposition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_is_only_ever_served_as_an_archive_never_as_a_bare_item()
    {
        await using var server = await Harness.StartAsync();
        var token = (await server.PairAsync(server.Pairing.Code)).Token;
        var item = LanSendItem.Of(server.AddFolder("照片", ("a.txt", "1")));
        Assert.True(item.IsDirectory);
        server.Basket.Add(item);

        var response = await server.DownloadAsync(item.Id.ToString("N"), token);

        // A single file that is not the folder it was named for is the worst possible answer to this request.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_archive_streams_onto_the_response_and_never_builds_it_on_disk()
    {
        await using var server = await Harness.StartAsync();
        var token = (await server.PairAsync(server.Pairing.Code)).Token;
        var folder = server.AddFolder("发送 包", ("报告+总结.txt", "一"), (@"子目录/照片 1.png", "二"));
        Directory.CreateDirectory(Path.Combine(folder, "空目录"));
        server.Basket.Add(LanSendItem.Of(folder));
        var before = Snapshot(server.Root);

        var response = await server.DownloadAsync(server.Basket.Single().Id.ToString("N"), token, archive: true);
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

        Assert.Equal(HttpStatusCode.Unauthorized, (await server.GetAsync("/api/v1/basket", token: token)).StatusCode);
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
    /// The basket is a list the case adds rows to and the server reads through <see cref="BasketSource"/>, which
    /// is the same shape the real window uses. The clock is an object a case can advance, because a code that has
    /// aged out would otherwise cost two minutes of waiting.
    /// </remarks>
    private sealed class Harness : IAsyncDisposable
    {
        private string? _token;

        private Harness(string root, LanPairingState pairing, LanPeerStore peers, LanPeerSecretStore secrets,
            LanReceiveSink sink, TestClock clock, LanServerOptions serverOptions, List<LanSendItem> basket)
        {
            Root = root;
            Receive = Path.Combine(root, "in");
            Pairing = pairing;
            Peers = peers;
            Secrets = secrets;
            Sink = sink;
            Clock = clock;
            ServerOptions = serverOptions;
            Basket = basket;
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

        /// <summary>What was handed to <see cref="LanServerHost.StartAsync"/>, so a case can point a second listener at the same port.</summary>
        public LanServerOptions ServerOptions { get; }

        public LanServerHost Host { get; private set; } = null!;

        /// <summary>The rows the peer may list and read.</summary>
        public List<LanSendItem> Basket { get; }

        /// <summary>The same list, as the server sees it.</summary>
        private sealed class BasketSource(List<LanSendItem> items) : ILanSendSource
        {
            public IReadOnlyList<LanSendItem> Snapshot() => items;
        }

        public static string NewRoot() => Path.Combine(Path.GetTempPath(), "fengsync-lan-wire-" + Guid.NewGuid().ToString("N"));

        /// <summary>
        /// Files on disk are written without a preamble, because a byte order mark would silently shift every
        /// offset a range request names and make the file longer than the content the case wrote.
        /// </summary>
        private static readonly Encoding Plain = new UTF8Encoding(false);

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
            var basket = new List<LanSendItem>();
            var harness = new Harness(root, pairing, peers, secrets, sink, clock,
                new LanServerOptions(new LanSession(options, peers, secrets, pairing, new BasketSource(basket), confirm),
                    identity, pairing, sink),
                basket);
            harness.Host = await LanServerHost.StartAsync(harness.ServerOptions, CancellationToken.None);
            return harness;
        }

        /// <summary>Moves the injected clock forward, which is how a code is aged out without waiting two minutes.</summary>
        public void AdvanceClock(int seconds) => Clock.Now = Clock.Now.AddSeconds(seconds);

        public string AddFile(string relativePath, string content)
        {
            var path = Path.Combine(Root, "out", relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // No byte-order mark: a range request names offsets in the file, and a preamble the test never
            // counted would move every one of them by three.
            File.WriteAllText(path, content, Plain);
            return path;
        }

        public string AddFolder(string name, params (string RelativePath, string Content)[] files)
        {
            var root = Path.Combine(Root, "out", name);
            foreach (var (relative, content) in files)
            {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content, Plain);
            }

            return root;
        }

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

        public Task<HttpResponseMessage> InboxRawAsync(string wire, string content, long? declaredBytes = null, DateTimeOffset? mtime = null, string? token = null) =>
            InboxRawAsync(wire, Encoding.UTF8.GetBytes(content), declaredBytes, mtime, token);

        public Task<HttpResponseMessage> InboxRawAsync(string wire, byte[] content, long? declaredBytes = null, DateTimeOffset? mtime = null, string? token = null)
        {
            var request = Request(HttpMethod.Post, "/api/v1/inbox", PeerId, token);
            request.Content = new ByteArrayContent(content);
            if (declaredBytes is { } bytes) request.Headers.Add(LanProtocol.HeaderTotal, bytes.ToString());
            if (mtime is { } stamp) request.Headers.Add(LanProtocol.HeaderMtime, stamp.ToUnixTimeMilliseconds().ToString());
            request.Headers.Add(LanProtocol.HeaderRel, wire);
            return SendAsync(request);
        }

        public async Task<LanMkdirReply> MkdirAsync(string wire, string? token = null)
        {
            var request = Request(HttpMethod.Post, "/api/v1/mkdir", PeerId, token);
            request.Content = new StringContent("");
            request.Headers.Add(LanProtocol.HeaderRel, wire);
            var response = await SendAsync(request);
            response.EnsureSuccessStatusCode();
            return Deserialize<LanMkdirReply>(await response.Content.ReadAsStringAsync());
        }

        public async Task<IReadOnlyList<LanBasketReply>> BasketAsync(string? token = null)
        {
            var response = await GetAsync("/api/v1/basket", token: token);
            response.EnsureSuccessStatusCode();
            return Deserialize<List<LanBasketReply>>(await response.Content.ReadAsStringAsync());
        }

        public Task<HttpResponseMessage> GetAsync(string path, string deviceId = PeerId, string? token = null) =>
            SendAsync(Request(HttpMethod.Get, path, deviceId, token));

        /// <summary>
        /// A basket download, addressed only by the id the listing gave out.
        /// </summary>
        /// <remarks>
        /// The id is percent-encoded the way a browser would encode it, because a route value is something the
        /// framework decodes and a Chinese filename must survive that exactly as a header does.
        /// </remarks>
        public Task<HttpResponseMessage> DownloadAsync(string id, string token, bool archive = false, RangeHeaderValue? range = null) =>
            SendAsync(Request(HttpMethod.Get,
                $"/api/v1/basket/{Uri.EscapeDataString(id)}{(archive ? "/zip" : "")}", PeerId, token, range));

        /// <summary>A device removing its own credential, which is the one thing a peer may do to the trust list.</summary>
        public Task<HttpResponseMessage> ForgetSelfAsync(string token) =>
            SendAsync(Request(HttpMethod.Delete, "/api/v1/peers/mine", PeerId, token));

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
