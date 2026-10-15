using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// The room as one machine experiences it: a window that keeps up with what the others said, and that goes and
/// fetches what they offered.
/// </summary>
/// <remarks>
/// <para>
/// Every case here runs two or three real listeners, because the properties under test only exist between them:
/// that a replica converges by being read rather than by being told, that a watermark belongs to the member it
/// tracks, and that an announcement of a file is a promise any holder may keep. A fake HTTP handler can assert a
/// payload; it cannot show a second machine's history arriving in this one's log.
/// </para>
/// <para>
/// Two computers in one room are paired in both directions — each holds a credential the other issued, which is
/// what lets each read the other — so the helpers do that paperwork and a case states only the fact it is about.
/// </para>
/// </remarks>
public sealed class LanGroupRoomTests
{
    [Fact]
    public async Task A_beat_takes_in_a_sentence_this_window_never_heard()
    {
        await using var a = await Node.StartAsync("书房");
        await using var b = await Node.StartAsync("客厅");
        await PairBothAsync(a, b);

        a.Say("楼下见");

        await b.Room.PollOnceAsync(CancellationToken.None);

        // One poll in each direction is enough to converge, and the entry arrives authored rather than echoed: the
        // sentence keeps the name of the machine that said it, because no member speaks for another.
        var entry = Assert.Single(b.Group.Entries);
        Assert.Equal(LanGroupKind.Text, entry.Kind);
        Assert.Equal("楼下见", entry.Text);
        Assert.Equal(a.DeviceId, entry.AuthorDeviceId);
        Assert.Equal("书房", entry.AuthorName);
        Assert.False(entry.HeldHere);
    }

    [Fact]
    public async Task An_offered_file_waits_for_a_person_and_then_lands_where_the_sink_puts_it()
    {
        await using var a = await Node.StartAsync("书房");
        await using var b = await Node.StartAsync("客厅");
        await PairBothAsync(a, b);
        var entry = a.Announce("报告.txt", "季度数字");

        await b.Room.PollOnceAsync(CancellationToken.None);

        // Hearing about a file is not having it: the room stores nothing on its own, so the row is on screen with
        // nothing in the receive directory until somebody presses 接收.
        Assert.Empty(Directory.GetFiles(b.Receive));
        var offer = Assert.Single(b.Offers.ToArray());
        Assert.Equal(entry.MessageId, offer.MessageId);
        Assert.False(offer.HeldHere);

        var result = await b.Room.ReceiveAsync(entry.MessageId);

        Assert.Equal(LanRefusal.None, result.Refusal);
        Assert.Equal(Path.Combine(b.Receive, "报告.txt"), result.LandedPath);
        Assert.Equal("季度数字", File.ReadAllText(result.LandedPath!));
        // The entry now speaks for this machine too, which is what makes that row read 已接收 and what lets a third
        // member ask this one for the bytes.
        Assert.True(b.Group.Entries.Single(item => item.MessageId == entry.MessageId).HeldHere);
        Assert.Single(b.Offers.ToArray());
    }

    [Fact]
    public async Task Auto_receive_lands_what_it_hears_without_anybody_pressing_receive()
    {
        await using var a = await Node.StartAsync("书房");
        await using var b = await Node.StartAsync("客厅");
        await PairBothAsync(a, b);
        b.Room.AutoReceive = true;

        a.Announce("通知.txt", "修好了");

        await b.Room.PollOnceAsync(CancellationToken.None);

        var landed = Path.Combine(b.Receive, "通知.txt");
        Assert.Equal("修好了", File.ReadAllText(landed));
        // The beat's own pull is the settled event the window reads as 已接收, so the switch costs no second copy.
        var fetched = Assert.Single(b.Fetches.ToArray());
        Assert.Equal(LanRefusal.None, fetched.Refusal);
        Assert.Equal(landed, fetched.LandedPath);
    }

    [Fact]
    public async Task A_folder_announced_by_a_member_unpacks_as_a_tree_and_not_as_an_archive_left_beside_it()
    {
        await using var a = await Node.StartAsync("书房");
        await using var b = await Node.StartAsync("客厅");
        await PairBothAsync(a, b);
        b.Room.AutoReceive = true;
        var entry = a.AnnounceTree("bundle", ("top.txt", "a"), ("deep/in.txt", "nested bytes"));
        Directory.CreateDirectory(Path.Combine(a.Shared, "bundle", "empty"));

        await b.Room.PollOnceAsync(CancellationToken.None);

        // The tree arrives with its directories, including the one holding nothing, and the entry that names the
        // folder points at the folder rather than at a .zip nobody asked to keep.
        Assert.Equal("nested bytes", File.ReadAllText(Path.Combine(b.Receive, "bundle", "deep", "in.txt")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(b.Receive, "bundle", "top.txt")));
        Assert.True(Directory.Exists(Path.Combine(b.Receive, "bundle", "empty")));
        // The archive is a temporary and not a delivery: neither a .zip beside the tree nor a half-written
        // staging file is what the room promised to hand over.
        Assert.Empty(Directory.GetFiles(b.Receive, "*.zip"));
        Assert.Empty(Directory.GetFiles(b.Receive, "*.fengsync.partial"));
        var held = b.Group.Entries.Single(item => item.MessageId == entry.MessageId);
        Assert.True(held.HeldHere);
        Assert.Equal(Path.Combine(b.Receive, "bundle"), held.LocalPath);
    }

    [Fact]
    public async Task An_author_that_no_longer_has_the_bytes_still_gets_the_file_asked_of_the_member_that_does()
    {
        await using var a = await Node.StartAsync("书房");
        await using var b = await Node.StartAsync("客厅");
        await using var c = await Node.StartAsync("走廊");
        await PairBothAsync(a, b);
        await PairBothAsync(b, c);
        await PairBothAsync(a, c);
        c.Room.AutoReceive = true;

        var entry = a.Announce("照片.jpg", "bytes of a photo");
        await b.Room.PollOnceAsync(CancellationToken.None);
        await b.Room.ReceiveAsync(entry.MessageId);
        // The author's own copy goes, which is the ordinary life of a file in a room: it was offered once and it
        // now lives wherever the members that wanted it put it.
        File.Delete(Path.Combine(a.Shared, "照片.jpg"));

        await c.Room.PollOnceAsync(CancellationToken.None);

        // C asks A first because A is who announced it, and A says it cannot. That is a member to move past, not a
        // reason to tell the user the file is gone.
        Assert.Equal("bytes of a photo", File.ReadAllText(Path.Combine(c.Receive, "照片.jpg")));
        Assert.Equal(LanRefusal.None, Assert.Single(c.Fetches.ToArray()).Refusal);
    }

    [Fact]
    public async Task A_member_that_answered_nothing_is_read_again_from_where_it_stopped()
    {
        await using var a = await Node.StartAsync("书房");
        await using var b = await Node.StartAsync("客厅");
        await PairBothAsync(a, b);

        a.Say("第一句");
        await b.Room.PollOnceAsync(CancellationToken.None);

        await a.PauseAsync();
        a.Say("第二句");
        await b.Room.PollOnceAsync(CancellationToken.None);

        // One sentence, not none: a listener that was down for a beat is a beat missed, and the watermark that did
        // not move is what makes the next read arrive rather than the room quietly agreeing to skip a sentence.
        Assert.Equal(["第一句"], b.Group.Entries.Select(item => item.Text));

        await a.ResumeAsync();
        await b.Room.PollOnceAsync(CancellationToken.None);

        Assert.Equal(["第一句", "第二句"], b.Group.Entries.Select(item => item.Text));
    }

    [Fact]
    public async Task A_path_this_machine_will_not_take_is_named_as_this_machines_own_answer()
    {
        await using var a = await Node.StartAsync("书房");
        await using var b = await Node.StartAsync("客厅");
        await PairBothAsync(a, b);
        b.Room.AutoReceive = true;

        // An announcement whose name walks out of the receive directory. It is refused whole, the same way an
        // inbound write is, rather than renamed into something the user would later trust as their own file.
        a.Group.AppendFile(a.SelfContact, "escape.txt", "../escape.txt", 3, isDirectory: false,
            DateTimeOffset.UtcNow, a.Write("innocent.txt", "abc"));

        await b.Room.PollOnceAsync(CancellationToken.None);

        var refusal = Assert.Single(b.Fetches.ToArray());
        Assert.Equal(LanRefusal.PathUnsafe, refusal.Refusal);
        Assert.Null(refusal.LandedPath);
        Assert.Empty(Directory.GetFiles(b.Receive));
        // The row keeps its sentence and no claim, so the person can read what was offered and press 接收 again
        // after moving the directory somewhere that can hold it.
        Assert.False(b.Group.Entries.Single(item => item.RelativePath == "../escape.txt").HeldHere);
    }

    [Fact]
    public async Task Pressing_receive_twice_asks_the_room_for_the_bytes_once()
    {
        await using var a = await Node.StartAsync("书房");
        await using var b = await Node.StartAsync("客厅");
        await PairBothAsync(a, b);
        var entry = a.Announce("清单.csv", "1,2,3");

        await b.Room.PollOnceAsync(CancellationToken.None);
        var first = await b.Room.ReceiveAsync(entry.MessageId);
        var second = await b.Room.ReceiveAsync(entry.MessageId);

        // The second answer is the file that is already here, not a copy of it under a name with a number in it:
        // a row the user clicked twice must not become two rows in their folder.
        Assert.Equal(first.LandedPath, second.LandedPath);
        Assert.Equal(["清单.csv"], Directory.GetFiles(b.Receive).Select(Path.GetFileName));
        // One settled event, because only one pull happened.
        Assert.Single(b.Fetches.ToArray());
    }

    [Fact]
    public async Task An_id_this_copy_never_heard_of_is_answered_instead_of_asked_of_the_room()
    {
        await using var a = await Node.StartAsync("书房");
        await using var b = await Node.StartAsync("客厅");
        await PairBothAsync(a, b);

        var result = await b.Room.ReceiveAsync(Guid.NewGuid().ToString("N"));

        // Nothing was merged, so there is no entry to name and nobody to ask. A pull of an id this copy has never
        // seen is not the room refusing, and the window has to be able to tell those two apart.
        Assert.Null(result.Entry);
        Assert.Equal(LanRefusal.NotFound, result.Refusal);
        Assert.Null(result.LandedPath);
        Assert.Empty(b.Fetches.ToArray());
    }

    [Fact]
    public async Task A_started_room_reads_the_history_it_missed_before_it_waits_for_the_next_beat()
    {
        await using var a = await Node.StartAsync("书房");
        await using var b = await Node.StartAsync("客厅");
        await PairBothAsync(a, b);
        a.Say("已经说过了");

        b.Room.Start();
        // The first beat happens as the room starts rather than one interval in, which is what makes a window that
        // opens into a conversation already talking show it straight away.
        await WaitUntilAsync(() => b.Group.Count == 1);

        Assert.Equal("已经说过了", b.Group.Entries[0].Text);
    }

    /// <summary>Two computers in one room: each pairs into the other, so each holds a credential it can spend.</summary>
    private static async Task PairBothAsync(Node left, Node right)
    {
        await left.PairWithAsync(right);
        await right.PairWithAsync(left);
    }

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!ready())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("房间里没有出现等待的内容。");
            await Task.Delay(50);
        }
    }

    private static string FreeName(string name, IReadOnlyList<string> taken) =>
        taken.Contains(name, StringComparer.OrdinalIgnoreCase)
            ? $"{Path.GetFileNameWithoutExtension(name)} (1){Path.GetExtension(name)}"
            : name;

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>One machine in the room: its own listener, its own copy of the history, and its own window's reader.</summary>
    private sealed class Node : IAsyncDisposable
    {
        private readonly string _root;
        private readonly LanDeviceIdentity _identity;
        private readonly LanPeerStore _peers;
        private readonly LanPairingState _pairing;
        private readonly LanReceiveSink _sink;
        private readonly LanSession _session;
        private LanServerHost? _host;

        private Node(string root, LanDeviceIdentity identity, LanPeerStore peers, LanPairingState pairing,
            LanReceiveSink sink, LanSession session, int port)
        {
            _root = root;
            _identity = identity;
            _peers = peers;
            _pairing = pairing;
            _sink = sink;
            _session = session;
            Port = port;
            Offers = [];
            Fetches = [];
            Client = new LanPeerClient(identity, peers, new LanPeerSecretStore(Path.Combine(root, "received.dat")));
            Room = new LanGroupRoom(identity, Client, peers, session.Group, sink);
            Room.Offered += message => Offers.Enqueue(message);
            Room.Fetched += args => Fetches.Enqueue(args);
        }

        public LanPeerClient Client { get; }
        public LanGroupRoom Room { get; }
        public ConcurrentQueue<LanGroupMessage> Offers { get; }
        public ConcurrentQueue<LanRoomFetchEventArgs> Fetches { get; }
        public LanGroupLog Group => _session.Group;
        public string DeviceId => _identity.DeviceId;
        public string Receive => Path.Combine(_root, "in");
        public string Shared => Path.Combine(_root, "shared");
        public int Port { get; private set; }
        public LanContact SelfContact => new(_identity.DeviceId, _identity.DeviceName, _identity.Kind, "127.0.0.1", Port);
        public LanPeerEndpoint Endpoint => new(_identity.DeviceId, "127.0.0.1", Port, _identity.DeviceName);

        public static async Task<Node> StartAsync(string label)
        {
            var root = Path.Combine(Path.GetTempPath(), $"fengsync-lan-room-{label}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(root, "in"));
            Directory.CreateDirectory(Path.Combine(root, "shared"));
            var receive = Path.Combine(root, "in");
            // The port is settled before the session, because the listener reads it out of the session's options and
            // a member that came back on another number would be dialled at the wrong place.
            var port = FreePort();
            var identity = new LanDeviceIdentity(Guid.NewGuid().ToString("N"), label, LanProtocol.KindWindows,
                Path.Combine(root, "device.json"));
            var peers = new LanPeerStore(Path.Combine(root, "peers.json"));
            var sink = new LanReceiveSink(receive, FreeName);
            var pairing = new LanPairingState();
            // One session for the life of the node: pausing a listener and bringing it back keeps the room this
            // machine holds, because a window that restarted its socket did not restart its conversation.
            var session = new LanSession(new LanOptions(port, receive, true), peers,
                new LanPeerSecretStore(Path.Combine(root, "secrets.dat")), pairing, new EmptyBasket(),
                (_, _) => Task.FromResult(true));
            var node = new Node(root, identity, peers, pairing, sink, session, port);
            await node.ResumeAsync();
            return node;
        }

        /// <summary>Trades this machine's credential for that one's, the way a person types the code on screen.</summary>
        public Task PairWithAsync(Node other) => Client.PairAsync(other.Endpoint, other._pairing.Arm());

        public LanGroupMessage Say(string text)
        {
            Group.TryAppendText(SelfContact, text, DateTimeOffset.UtcNow, out var entry);
            return entry!;
        }

        public string Write(string name, string content)
        {
            var path = Path.Combine(Shared, name);
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>Offers a file this machine has, exactly as the send path does when the bytes arrive.</summary>
        public LanGroupMessage Announce(string name, string content)
        {
            var path = Write(name, content);
            return Group.AppendFile(SelfContact, name, name, new FileInfo(path).Length, false, DateTimeOffset.UtcNow, path);
        }

        /// <summary>Offers a folder as one entry, with the whole tree on disk behind it.</summary>
        public LanGroupMessage AnnounceTree(string folder, params (string Relative, string Content)[] files)
        {
            var root = Path.Combine(Shared, folder);
            foreach (var (relative, content) in files)
            {
                var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }

            return Group.AppendFile(SelfContact, folder, folder, 0, true, DateTimeOffset.UtcNow, root);
        }

        /// <summary>Takes the socket away and leaves the room intact, which is what a member that went quiet looks like.</summary>
        public async Task PauseAsync()
        {
            if (_host is null) return;
            var host = _host;
            _host = null;
            await host.DisposeAsync();
        }

        public async Task ResumeAsync()
        {
            if (_host is not null) return;
            _host = await LanServerHost.StartAsync(new LanServerOptions(_session, _identity, _pairing, _sink),
                CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await PauseAsync();
            await Room.DisposeAsync();
            Client.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }

    private sealed class EmptyBasket : ILanSendSource
    {
        public IReadOnlyList<LanSendItem> Snapshot() => [];
    }
}
