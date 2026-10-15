using System.Net;
using System.Net.Sockets;
using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// The calling half of a computer-to-computer transfer, driven against a real listener.
/// </summary>
/// <remarks>
/// <para>
/// These are the properties that only exist between the two halves: a token the peer issued lands in this
/// machine's own credential file keyed by the <b>peer's</b> id and nowhere else, so a second client reading the
/// same file is already paired and a <see cref="LanPeerClient.ForgetAsync"/> is a decision that survives closing
/// the window; and a folder a person dropped in the basket arrives as the same tree, empty directories included,
/// because the walk and the per-file writes are one sequence with no batch concept on either side.
/// </para>
/// <para>
/// A fake HTTP handler could assert the payloads, and <see cref="LanServerWireTests"/> already drives the same
/// endpoints from the other direction with a raw client. What is pinned here is the client's own behaviour over a
/// socket, including the two refusals it produces before and after a request.
/// </para>
/// </remarks>
public sealed class LanPeerClientTests
{
    [Fact]
    public async Task Hello_answers_null_for_a_port_where_nothing_is_listening()
    {
        var closed = FreePort();
        using var client = new LanPeerClient(await Identity(closed));

        Assert.Null(await client.HelloAsync("127.0.0.1", closed));
    }

    [Fact]
    public async Task A_code_that_is_not_a_code_is_refused_before_the_socket_is_opened()
    {
        var closed = FreePort();
        using var client = new LanPeerClient(await Identity(closed));
        var peer = new LanPeerEndpoint("a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6", "127.0.0.1", closed);

        // Nobody is listening, and the answer must still be about the code rather than about the network: a
        // window that reported 连不上 for a typo would send the user to check the cable.
        var failure = await Assert.ThrowsAsync<LanTransferException>(() => client.PairAsync(peer, "0O1IL0"));

        Assert.Equal(LanRefusal.BadCode, failure.Refusal);
    }

    [Fact]
    public async Task Pairing_stores_what_the_peer_issued_under_the_peers_id_and_nowhere_else()
    {
        await using var server = await Server.StartAsync();
        var self = await Identity(server.Port);
        using var client = new LanPeerClient(self, server.ClientTokens());
        var peer = PeerOf(server);

        Assert.False(await client.IsPairedAsync(peer));
        var reply = await client.PairAsync(peer, server.Pairing.Code);
        Assert.True(await client.IsPairedAsync(peer));

        // The direction is the whole point of the separate file: this machine looks a credential up by the id of
        // the machine that handed it over, and its own id keys nothing in that file.
        var received = server.ClientTokens();
        Assert.True(await received.VerifyAsync(server.Identity.DeviceId, reply.Token));
        Assert.False(await received.VerifyAsync(self.DeviceId, reply.Token));

        // The mirror of the same fact, on the far side: what the peer issued to this machine is in the peer's
        // file, under this machine's id. Two files, each keyed by the other end — which is why one of them being
        // read tells nobody how to call the other.
        Assert.True(await server.Secrets.VerifyAsync(self.DeviceId, reply.Token));
    }

    [Fact]
    public async Task A_credential_read_back_from_disk_pairs_a_second_client_with_no_round_trip()
    {
        await using var server = await Server.StartAsync();
        var self = await Identity(server.Port);
        var received = server.ClientTokens();
        using var client = new LanPeerClient(self, received);
        await client.PairAsync(PeerOf(server), server.Pairing.Code);

        // The window closes and another opens; the file is what makes the returning device not a stranger.
        using var reopened = new LanPeerClient(self, server.ClientTokens());
        Assert.True(await reopened.IsPairedAsync(PeerOf(server)));
    }

    [Fact]
    public async Task Forgetting_a_peer_stops_this_side_sending_but_leaves_the_peers_own_row_alone()
    {
        await using var server = await Server.StartAsync();
        var self = await Identity(server.Port);
        using var client = new LanPeerClient(self, server.ClientTokens());
        await client.PairAsync(PeerOf(server), server.Pairing.Code);
        var peer = PeerOf(server);

        Assert.True(await client.ForgetAsync(peer));
        Assert.False(await client.IsPairedAsync(peer));
        // 取消信任 on the other screen is the only thing that removes that row, and this side cannot reach it:
        // the peer still lists this machine, because its row records who was handed a credential, not who holds one.
        Assert.Contains(self.DeviceId, (await server.Peers.LoadAsync()).Select(record => record.DeviceId));
    }

    [Fact]
    public async Task Sending_without_a_credential_refuses_locally_and_writes_nothing_on_the_far_side()
    {
        await using var server = await Server.StartAsync();
        using var client = new LanPeerClient(await Identity(server.Port), server.ClientTokens());
        var source = Path.Combine(server.Root, "out.txt");
        File.WriteAllText(source, "bytes");

        var failure = await Assert.ThrowsAsync<LanTransferException>(() =>
            client.SendAsync(PeerOf(server), LanSendItem.Of(source)));

        Assert.Equal(LanRefusal.Unpaired, failure.Refusal);
        Assert.Empty(Directory.GetFiles(server.Receive));
    }

    [Fact]
    public async Task A_file_arrives_under_its_own_name_with_the_date_the_sender_described()
    {
        await using var server = await Server.StartAsync();
        using var client = await PairedAsync(server);
        var source = Path.Combine(server.Root, "季度 报告+1.txt");
        File.WriteAllText(source, "hello");
        var written = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, written);

        var outcome = await client.SendAsync(PeerOf(server), LanSendItem.Of(source), "sess");

        var landed = Path.Combine(server.Receive, "季度 报告+1.txt");
        Assert.Equal(1, outcome.Files);
        Assert.Equal(0, outcome.Directories);
        Assert.Equal(new FileInfo(source).Length, outcome.Bytes);
        Assert.Equal("hello", File.ReadAllText(landed));
        Assert.Equal(written, File.GetLastWriteTimeUtc(landed));
    }

    [Fact]
    public async Task A_folder_arrives_as_the_same_tree_including_the_empty_branch()
    {
        await using var server = await Server.StartAsync();
        using var client = await PairedAsync(server);
        var root = Path.Combine(server.Root, "tree");
        Directory.CreateDirectory(Path.Combine(root, "空目录"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        File.WriteAllText(Path.Combine(root, "readme.md"), "top");
        File.WriteAllText(Path.Combine(root, "nested", "a+b.txt"), "sub");

        var outcome = await client.SendAsync(PeerOf(server), LanSendItem.Of(root), "sess");

        Assert.Equal(2, outcome.Files);
        // The walked folder's own name, then its two subdirectories — the empty one only arrives because a
        // mkdir is sent for it, which is the whole reason that endpoint exists.
        Assert.Equal(3, outcome.Directories);
        // Six bytes, not ten: an empty directory costs a mkdir and no body, which is the whole point of counting
        // files and directories separately.
        Assert.Equal(6, outcome.Bytes);
        Assert.Equal("top", File.ReadAllText(Path.Combine(server.Receive, "tree", "readme.md")));
        Assert.Equal("sub", File.ReadAllText(Path.Combine(server.Receive, "tree", "nested", "a+b.txt")));
        Assert.True(Directory.Exists(Path.Combine(server.Receive, "tree", "空目录")));
    }

    [Fact]
    public async Task A_device_that_paired_in_this_window_is_asked_before_its_first_write()
    {
        await using var server = await Server.StartAsync();
        using var client = await PairedAsync(server);
        var source = Path.Combine(server.Root, "first.txt");
        File.WriteAllText(source, "x");

        await client.SendAsync(PeerOf(server), LanSendItem.Of(source), "sess");

        // Pairing is not consent to land bytes: the person agreed to talk, and the first write is the second
        // question. The snapshot of who was already trusted was taken before this device existed.
        var asked = await server.Asked.SingleAsync();
        Assert.Equal("first.txt", asked.Path);
        Assert.Contains(asked.Contact.DeviceId, (await server.Peers.LoadAsync()).Select(record => record.DeviceId));
    }

    [Fact]
    public async Task A_returning_device_lands_its_bytes_without_a_question_once_the_next_window_has_seen_its_row()
    {
        await using var server = await Server.StartAsync();
        using var client = await PairedAsync(server);
        var first = Path.Combine(server.Root, "one.txt");
        File.WriteAllText(first, "x");
        await client.SendAsync(PeerOf(server), LanSendItem.Of(first), "s1");
        var questions = server.Asked.Count;

        // A new window takes its snapshot after the row its own pairing wrote, so this device is a returning
        // one and the 自动同意 switch is what answers for it.
        await server.RestartAsync();
        var second = Path.Combine(server.Root, "two.txt");
        File.WriteAllText(second, "y");
        await client.SendAsync(PeerOf(server), LanSendItem.Of(second), "s2");

        Assert.Equal(questions, server.Asked.Count);
        Assert.Equal("y", File.ReadAllText(Path.Combine(server.Receive, "two.txt")));
    }

    [Fact]
    public void The_two_credential_files_never_share_a_path()
    {
        // One file holds what this machine handed out, the other what it was handed. A reader of either has to
        // be able to tell which direction the credential points, and a user opening lan-peers.json must find no
        // key in it.
        Assert.EndsWith("lan-received-tokens.dat", LanPeerClient.DefaultReceivedPath(), StringComparison.Ordinal);
        Assert.EndsWith("lan-peer-secrets.dat", new LanPeerSecretStore().Path, StringComparison.Ordinal);
        Assert.DoesNotContain(LanPeerClient.DefaultReceivedPath(), new LanPeerSecretStore().Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sentence_this_machine_says_lands_in_the_peers_copy_of_the_room_and_reads_back()
    {
        await using var server = await Server.StartAsync();
        using var client = await PairedAsync(server);
        var peer = PeerOf(server);

        var said = await client.SayAsync(peer, "先看报告");
        var log = await client.LogAsync(peer, since: 0);

        // The peer authors the entry on this machine's behalf, from the credential rather than from anything the
        // body claimed, so the room can show who spoke without trusting a name a stranger typed.
        Assert.Equal((await Identity(server.Port)).DeviceId, said.AuthorDeviceId);
        Assert.Equal(said.MessageId, Assert.Single(log.Entries).MessageId);
        Assert.Equal(log.Head, said.LocalSeq);
        Assert.Equal(LanApi.WireVersion, log.V);
    }

    [Fact]
    public async Task The_next_pull_of_the_room_asks_past_the_number_this_machine_remembered_for_that_peer()
    {
        await using var server = await Server.StartAsync();
        using var client = await PairedAsync(server);
        var peer = PeerOf(server);
        await client.SayAsync(peer, "一");
        await client.SayAsync(peer, "二");

        var first = await client.LogAsync(peer, since: 0);
        var second = await client.LogAsync(peer, since: first.Head);

        Assert.Equal(2, first.Entries.Count);
        // Nothing new is an ordinary answer, and the watermark still travels: a poller that read it off the
        // entries would re-ask for a gap it had already crossed.
        Assert.Empty(second.Entries);
        Assert.Equal(2, second.Head);
    }

    [Fact]
    public async Task A_sentence_the_peer_would_not_hold_comes_back_named_rather_than_as_a_number()
    {
        await using var server = await Server.StartAsync();
        using var client = await PairedAsync(server);

        var failure = await Assert.ThrowsAsync<LanTransferException>(() =>
            client.SayAsync(PeerOf(server), new string('字', LanGroupLog.MaximumTextLength + 1)));

        // 413 alone would tell the window "the request failed"; the reason token is what lets it say 发短一点.
        Assert.Equal(LanRefusal.MessageTooLong, failure.Refusal);
        Assert.Empty((await client.LogAsync(PeerOf(server), since: 0)).Entries);
    }

    [Fact]
    public async Task Reading_the_room_without_a_credential_refuses_locally_and_sends_nothing()
    {
        await using var server = await Server.StartAsync();
        using var client = new LanPeerClient(await Identity(server.Port), server.ClientTokens());

        var failure = await Assert.ThrowsAsync<LanTransferException>(() =>
            client.LogAsync(PeerOf(server), since: 0));

        Assert.Equal(LanRefusal.Unpaired, failure.Refusal);
        Assert.Equal(0, server.Group.Count);
    }

    [Fact]
    public async Task A_room_re_opened_starts_empty_while_the_devices_a_person_trusted_survive_in_their_file()
    {
        await using var server = await Server.StartAsync();
        // Read before the restart: this helper names its identity file after the port, and a new window binds a
        // new number, so asking afterwards would read a different device rather than the one that just spoke.
        var self = await Identity(server.Port);
        using var client = await PairedAsync(server);
        await client.SayAsync(PeerOf(server), "上一扇窗里说的话");
        Assert.NotEmpty(server.Group.Entries);

        await server.RestartAsync();

        // The history is memory and dies with the window, while the trust list is a file and does not: what a
        // person agreed to is a different promise from what the room said, and only one of them is kept.
        Assert.Empty(server.Group.Entries);
        Assert.Equal(0, server.Group.Head);
        Assert.Contains(self.DeviceId, (await server.Peers.LoadAsync()).Select(record => record.DeviceId));
    }

    private static async Task<LanPeerClient> PairedAsync(Server server)
    {
        var client = new LanPeerClient(await Identity(server.Port), server.ClientTokens());
        await client.PairAsync(PeerOf(server), server.Pairing.Code);
        return client;
    }

    private static LanPeerEndpoint PeerOf(Server server) => new(server.Identity.DeviceId, "127.0.0.1", server.Port);

    private static Task<LanDeviceIdentity> Identity(int port) =>
        LanDeviceIdentity.LoadOrCreateAsync(Path.Combine(Path.GetTempPath(), $"fengsync-lan-client-{port}-device.json"));

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// A listener of one's own, in a directory of one's own, that answers 同意 to everything and records who it
    /// was asked about.
    /// </summary>
    /// <remarks>
    /// The confirmation seam is where the first-write rule lives, and a client test needs both halves of it: the
    /// bytes have to be allowed to land, and a case has to be able to say that a question was actually asked.
    /// </remarks>
    private sealed class Server : IAsyncDisposable
    {
        private readonly string _root;
        private readonly LanReceiveSink _sink;
        private readonly LanPeerStore _peers;
        private readonly LanPeerSecretStore _secrets;
        private readonly LanPairingState _pairing;
        private readonly LanDeviceIdentity _identity;
        private LanSession _session = null!;
        private LanServerHost _host = null!;

        private Server(string root, LanReceiveSink sink, LanPeerStore peers, LanPeerSecretStore secrets,
            LanPairingState pairing, LanDeviceIdentity identity)
        {
            _root = root;
            _sink = sink;
            _peers = peers;
            _secrets = secrets;
            _pairing = pairing;
            _identity = identity;
            Asked = new Signal<LanInboundOffer>();
        }

        public string Root => _root;
        public string Receive => Path.Combine(_root, "in");
        public LanReceiveSink Sink => _sink;
        public LanPeerStore Peers => _peers;
        public LanPeerSecretStore Secrets => _secrets;
        public LanPairingState Pairing => _pairing;
        public LanDeviceIdentity Identity => _identity;
        public Signal<LanInboundOffer> Asked { get; }
        public int Port { get; private set; }

        /// <summary>The listener's copy of the room, which a new window replaces with an empty one.</summary>
        public LanGroupLog Group => _session.Group;

        public LanPeerSecretStore ClientTokens() => new(Path.Combine(_root, "client-tokens.dat"));

        public static async Task<Server> StartAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "fengsync-lan-client-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "in"));
            var identity = await LanDeviceIdentity.LoadOrCreateAsync(Path.Combine(root, "device.json"));
            var server = new Server(root,
                new LanReceiveSink(Path.Combine(root, "in"), FreeName),
                new LanPeerStore(Path.Combine(root, "peers.json")),
                new LanPeerSecretStore(Path.Combine(root, "secrets.dat")),
                new LanPairingState(),
                identity);
            await server.RestartAsync();
            return server;
        }

        /// <summary>Brings the listener up on a free port, so a case can watch what a second window decides.</summary>
        public async Task RestartAsync()
        {
            // A window closing really does give the port back, which is the same contract the shell depends on
            // when the user restarts the service after changing the number.
            if (_host is not null) await _host.DisposeAsync();
            Port = FreePort();
            // A new window means a new room: the listener's copy of the history is memory, so it is rebuilt here
            // rather than read back from the stores that survive on disk.
            _session = new LanSession(new LanOptions(Port, Receive, true), _peers, _secrets, _pairing,
                new EmptyBasket(), (offer, _) => Asked.PublishAsync(offer, accept: true));
            _host = await LanServerHost.StartAsync(new LanServerOptions(_session, _identity, _pairing, _sink), CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await _host.DisposeAsync();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private static string FreeName(string name, IReadOnlyList<string> taken) =>
            taken.Contains(name, StringComparer.OrdinalIgnoreCase)
                ? $"{Path.GetFileNameWithoutExtension(name)} (1){Path.GetExtension(name)}"
                : name;

        private sealed class EmptyBasket : ILanSendSource
        {
            public IReadOnlyList<LanSendItem> Snapshot() => [];
        }
    }

    /// <summary>Records every offer it is handed, so a case can read the one question a listener asked.</summary>
    private sealed class Signal<T>
    {
        private readonly List<T> _seen = [];
        private readonly object _gate = new();

        public int Count
        {
            get { lock (_gate) return _seen.Count; }
        }

        public Task<bool> PublishAsync(T value, bool accept)
        {
            lock (_gate) _seen.Add(value);
            return Task.FromResult(accept);
        }

        public async Task<T> SingleAsync()
        {
            for (var attempt = 0; ; attempt++)
            {
                lock (_gate) if (_seen.Count > 0) return _seen[0];
                Assert.True(attempt < 100, "nothing was ever asked about");
                await Task.Delay(20);
            }
        }
    }
}
