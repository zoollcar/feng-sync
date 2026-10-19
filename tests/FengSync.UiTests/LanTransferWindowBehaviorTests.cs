using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FengSync.Core.Configuration;
using FengSync.Lan;
using FengSync.Localization;
using FengSync.Services;
using FengSync.Views;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// The window that owns a LAN listener, built on an STA thread and driven without ever being shown.
/// </summary>
/// <remarks>
/// <para>
/// What is pinned here is the seam between WPF state and the listener's decisions — the parts that only exist
/// once a window is holding both. A socket answers a pairing code and a sink writes a file, and
/// <see cref="FengSync.Tests"/>"s wire tests already prove those; what no pure test can see is that opening this
/// window really binds and closing it really gives the port back, that a stranger's first write becomes a row a
/// person can click, that the click is the answer the waiting request receives, and that a switch on screen
/// reaches a running listener and the settings file without undoing another window's choice.
/// </para>
/// <para>
/// The listener binds <c>IPAddress.Any</c> on a port the test hands it, so nothing here waits on which network
/// card this machine has; the local address is always in the list, which is what makes the hello probe
/// deterministic. Discovery stays off — the UDP advertiser takes the same number as the listener and two tests
/// would compete for it.
/// </para>
/// </remarks>
[Collection(ProcessWideStateCollection.Name)]
public sealed class LanTransferWindowBehaviorTests : IDisposable
{
    private const string StrangerId = "9e8d7c6b5a4938271605f4e3d2c1b0a9";
    private const string ReturningId = "1a2b3c4d5e6f708192a3b4c5d6e7f809";

    private readonly string _root;
    private readonly string _dataRoot;
    private readonly string _receive;
    private readonly string? _previousDataDir;

    public LanTransferWindowBehaviorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fengsync-lan-window-" + Guid.NewGuid().ToString("N"));
        _dataRoot = Path.Combine(_root, "data");
        _receive = Path.Combine(_root, "in");
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_receive);

        // The device identity, the peer list, the tokens and the history all live under the app-data root, so
        // without this the window would write a real pairing into whoever is running the suite.
        _previousDataDir = Environment.GetEnvironmentVariable("FENGSYNC_DATA_DIR");
        Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", _dataRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", _previousDataDir);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public Task Opening_binds_a_listener_a_peer_can_reach_and_closing_gives_the_port_straight_back()
    {
        return RunSta(async () =>
        {
            var port = FreePort();
            var window = NewWindow(port);
            await window.OpenAsync();
            try
            {
                // The address list is what a person reads aloud, and the local one is always there — a machine
                // with no network card still has something to show rather than an empty box.
                Assert.Contains($"127.0.0.1:{port}", window.ListenAddresses);
                Assert.Equal(port.ToString(), window.PortBox.Text);
                Assert.Equal(Tr.Format("Lan.Listening", port), window.StatusText.Text);

                using var probe = new LanPeerClient(await IdentityAsync(), Members());
                var hello = await probe.HelloAsync("127.0.0.1", port);
                Assert.NotNull(hello);
                Assert.True(hello!.PairingArmed);
            }
            finally
            {
                await window.ShutdownAsync();
            }

            // The promise the port-change path depends on: a window that closed lets the next one bind the same
            // number at once, rather than the user waiting for a socket to age out.
            var reborn = new TcpListener(IPAddress.Any, port);
            reborn.Start();
            reborn.Stop();
        });
    }

    [Fact]
    public Task A_strangers_first_write_is_a_row_on_screen_and_the_click_is_the_answer_the_request_waits_for()
    {
        return RunSta(async () =>
        {
            var port = FreePort();
            var window = NewWindow(port);
            await window.OpenAsync();
            try
            {
                var decision = Task.Run(() => window.Session!.AdmitWriteAsync(Offer("报告.txt", StrangerId, port)));
                await WaitUntil(() => window.PendingList.Items.Count == 1, "等待确认的行没有画出来");
                Assert.Equal(Visibility.Visible, window.PendingBorder.Visibility);
                var row = (LanRow)window.PendingList.Items[0];
                Assert.Contains("小明的手机", row.Name);
                Assert.Contains("报告.txt", row.Detail);
                Assert.Equal(Tr.Format("Lan.AskingFirstWrite", "小明的手机"), window.StatusText.Text);

                // Nothing is selected, which is the common case: one row is waiting and 同意 means that one.
                window.AcceptPendingButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(LanRefusal.None, await decision);
                Assert.Empty(window.PendingList.Items);
                Assert.Equal(Visibility.Collapsed, window.PendingBorder.Visibility);

                // And the agreement covers the rest of this window, so the second file does not ask again —
                // a person who said yes to a phone should not answer for every file it sends.
                Assert.Equal(LanRefusal.None, await Task.Run(() => window.Session!.AdmitWriteAsync(Offer("第二个.txt", StrangerId, port))));
                Assert.Empty(window.PendingList.Items);
            }
            finally
            {
                await window.ShutdownAsync();
            }
        });
    }

    [Fact]
    public Task 拒绝_a_row_answers_the_waiting_request_and_leaves_the_thing_asked_about_on_screen()
    {
        return RunSta(async () =>
        {
            var port = FreePort();
            var window = NewWindow(port);
            await window.OpenAsync();
            try
            {
                var decision = Task.Run(() => window.Session!.AdmitWriteAsync(Offer("不要的文件.txt", StrangerId, port)));
                await WaitUntil(() => window.PendingList.Items.Count == 1, "等待确认的行没有画出来");
                window.DeclinePendingButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                // Declined rather than nothing: bytes landing in the user's Downloads folder is the one effect
                // here that closing a window cannot undo, so a question nobody agreed to writes nothing.
                Assert.Equal(LanRefusal.Declined, await decision);
                Assert.Empty(window.PendingList.Items);
                Assert.Equal(Tr.F("Lan.DeclinedThatFile"), window.StatusText.Text);
            }
            finally
            {
                await window.ShutdownAsync();
            }
        });
    }

    [Fact]
    public Task 一个问题被取消时_this_side_answers_no_and_the_screen_never_keeps_the_row()
    {
        return RunSta(async () =>
        {
            var port = FreePort();
            var window = NewWindow(port);
            await window.OpenAsync();
            try
            {
                var decision = Task.Run(() => window.Session!.AdmitWriteAsync(Offer("half.txt", StrangerId, port),
                    CancellationToken.None));
                await WaitUntil(() => window.PendingList.Items.Count == 1, "等待确认的行没有画出来");

                // Closing the window is the peer's question going unanswered: it gets the answer that writes
                // nothing rather than a request hanging until the client times out.
                await window.ShutdownAsync();
                Assert.Equal(LanRefusal.Declined, await decision);
                Assert.Empty(window.PendingList.Items);
            }
            finally
            {
                await window.ShutdownAsync();
            }
        });
    }

    [Fact]
    public Task A_question_a_test_must_answer_without_a_dialog_arrives_through_the_seam_instead_of_the_screen()
    {
        return RunSta(async () =>
        {
            var port = FreePort();
            var asked = new ConcurrentBag<LanInboundOffer>();
            var window = NewWindow(port, confirmInbound: (offer, _) =>
            {
                asked.Add(offer);
                return Task.FromResult(false);
            });
            await window.OpenAsync();
            try
            {
                Assert.Equal(LanRefusal.Declined, await Task.Run(() => window.Session!.AdmitWriteAsync(Offer("篮外的.txt", StrangerId, port))));

                // A modal dialog cannot be answered from an in-process test, so the rule it enforces is reached
                // this way — and the screen stays clean, which is the proof the seam replaced it rather than
                // running beside it.
                var offer = Assert.Single(asked);
                Assert.Equal("篮外的.txt", offer.Path);
                Assert.Equal(StrangerId, offer.Contact.DeviceId);
                Assert.Empty(window.PendingList.Items);
                Assert.Equal(Visibility.Collapsed, window.PendingBorder.Visibility);
            }
            finally
            {
                await window.ShutdownAsync();
            }
        });
    }

    [Fact]
    public Task A_machine_whose_rules_are_already_open_is_never_asked_to_elevate_for_it()
    {
        return RunSta(async () =>
        {
            var port = FreePort();
            var netsh = new Netsh(ruleIsPresent: true);
            var window = NewWindow(port, netsh: netsh.RunAsync);
            await window.OpenAsync();
            try
            {
                Assert.Equal(2, netsh.Calls.Count);
                // One read per rule and not one elevation, on every open. A prompt the user learns to click
                // through without reading is worse for them than a phone that cannot connect.
                Assert.All(netsh.Calls, call => Assert.False(call.Elevated));
                Assert.All(netsh.Calls, call => Assert.True(call.Arguments.StartsWith("advfirewall firewall show rule", StringComparison.Ordinal), call.Arguments));
                Assert.Equal(Tr.F("Lan.Firewall.Allowed"), window.FirewallStatusText.Text);
            }
            finally
            {
                await window.ShutdownAsync();
            }
        });
    }

    [Fact]
    public Task The_button_reads_both_rules_first_and_elevates_only_for_the_ones_that_are_missing()
    {
        return RunSta(async () =>
        {
            var port = FreePort();
            var netsh = new Netsh(ruleIsPresent: false);
            var window = NewWindow(port, netsh: netsh.RunAsync);
            await window.OpenAsync();
            try
            {
                Assert.Equal(Tr.F("Lan.Firewall.Missing"), window.FirewallStatusText.Text);
                window.FirewallButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntil(() => netsh.Calls.Count(call => call.Elevated) == 2, "两条缺的规则都该提权补上");

                // The order is the design: a rule is read before it is written, every time, so a second click
                // cannot add a second rule beside the one that is already there — and an elevated call never
                // names anything but an add.
                var calls = netsh.Calls;
                Assert.False(calls[0].Elevated, "第一次调用必须是只读的检查");
                Assert.Equal(2, calls.Count(call => call.Elevated));
                Assert.All(calls, call => Assert.Equal(call.Arguments.Contains("add rule", StringComparison.Ordinal), call.Elevated));
                for (var i = 1; i < calls.Count; i++)
                    if (calls[i].Elevated) Assert.False(calls[i - 1].Elevated, "提权之前先读同一条规则");
                Assert.Equal(2, calls.Count(call => call.Arguments.Contains("localport=" + port, StringComparison.Ordinal)));
                // Two rules, one protocol each: the transfer and the discovery are different protocols on one
                // number, and an unlisted UDP half shows up only as "the phones never find each other".
                Assert.Equal(1, calls.Count(call => call.Elevated && call.Arguments.Contains("protocol=TCP", StringComparison.Ordinal)));
                Assert.Equal(1, calls.Count(call => call.Elevated && call.Arguments.Contains("protocol=UDP", StringComparison.Ordinal)));
                Assert.Equal(Tr.F("Lan.Firewall.Added"), window.FirewallStatusText.Text);
            }
            finally
            {
                await window.ShutdownAsync();
            }
        });
    }

    [Fact]
    public Task A_busy_port_does_not_take_the_window_down_and_the_number_typed_next_is_the_one_that_listens()
    {
        return RunSta(async () =>
        {
            var occupied = FreePort();
            var blocker = new TcpListener(IPAddress.Any, occupied);
            blocker.Start();
            var window = NewWindow(occupied);
            await window.OpenAsync();
            try
            {
                // The port is fixed so the firewall rule can name it, so a clash is the user's to resolve rather
                // than something to dodge by quietly taking another number.
                Assert.Empty(window.ListenAddresses);
                Assert.Equal(Tr.F("Lan.PortBusy"), window.StatusText.Text);

                blocker.Stop();
                var replacement = FreePort();
                window.PortBox.Text = replacement.ToString();
                window.ApplyPortButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntil(() => window.ListenAddresses.Count > 0, "换过端口之后没有重新监听");

                Assert.Equal(Tr.Format("Lan.Listening", replacement), window.StatusText.Text);
                Assert.Equal(replacement, (await new SettingsStore().LoadAsync()).Settings.LanTransferPort);
            }
            finally
            {
                blocker.Stop();
                await window.ShutdownAsync();
            }
        });
    }

    [Fact]
    public Task A_returning_device_lands_unasked_until_the_switch_is_off_and_the_choice_written_is_this_windows_own()
    {
        return RunSta(async () =>
        {
            var port = FreePort();
            var now = DateTimeOffset.UtcNow;
            await new LanPeerStore().RememberAsync(new LanPeerRecord(ReturningId, "旧手机", LanProtocol.KindPhone,
                now, now, port, ["192.168.1.30"]));
            // Someone else's choice, written before this window existed: a whole snapshot saved back from the
            // window's own copy would undo it with no trace.
            await new SettingsStore().SaveAsync(new ApplicationSettings { MainWindowSidebarWidth = 300, LanAutoAcceptFromTrusted = true });

            var window = NewWindow(port);
            await window.OpenAsync();
            try
            {
                // The list itself is a file on disk and has its own window now; what this column carries is the
                // way in and a count, because "who has ever been trusted" and "who is in the room" are different
                // questions and showing them together made them read as one.
                Assert.Equal(Tr.Format("Lan.TrustedCount", 1), window.TrustedCountText.Text);
                Assert.True(window.Session!.AutoAcceptFromTrusted);
                Assert.True(window.Room!.AutoReceive);

                // The row was in the list before this window opened, so its bytes are its own business.
                Assert.Equal(LanRefusal.None, await Task.Run(() => window.Session!.AdmitWriteAsync(Offer("第一个.txt", ReturningId, port))));
                Assert.Empty(window.PendingList.Items);

                window.AutoReceiveBox.IsChecked = false;
                // One switch, two directions: the session reads it per write and the room reads it per offer, so
                // a running listener and a running poll both take the change without a restart. The status line is
                // the last thing the handler writes, so waiting for it waits for both.
                await WaitUntil(() => window.StatusText.Text == Tr.F("Lan.AutoReceiveOff"), "开关没有落到状态行");
                Assert.False(window.Session!.AutoAcceptFromTrusted);
                Assert.False(window.Room!.AutoReceive);

                var saved = await new SettingsStore().LoadAsync();
                Assert.False(saved.Settings.LanAutoAcceptFromTrusted);
                Assert.Equal(300d, saved.Settings.MainWindowSidebarWidth);

                // 自动同意 off means ask me again, not ask me per file: the same returning device is a question
                // now, on a listener that was never restarted.
                var decision = Task.Run(() => window.Session!.AdmitWriteAsync(Offer("第二个.txt", ReturningId, port)));
                await WaitUntil(() => window.PendingList.Items.Count == 1, "关掉自动同意后没有重新提问");
                window.AcceptPendingButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(LanRefusal.None, await decision);
            }
            finally
            {
                await window.ShutdownAsync();
            }
        });
    }

    [Fact]
    public Task A_file_a_phone_sends_draws_its_progress_row_instead_of_taking_the_process_with_it()
    {
        return RunSta(async () =>
        {
            var port = FreePort();
            var window = NewWindow(port, (_, _) => Task.FromResult(true));
            await window.OpenAsync();
            try
            {
                using var probe = new LanPeerClient(await IdentityAsync(), Members());
                var hello = await probe.HelloAsync("127.0.0.1", port);
                Assert.NotNull(hello);
                var peer = new LanPeerEndpoint(hello!.DeviceId, "127.0.0.1", port);
                await probe.PairAsync(peer, window.PairCodeText.Text);

                var body = new byte[1024];
                using var stream = new MemoryStream(body);
                await probe.PushFileAsync(peer, "报告 1+1.txt", stream, body.Length, DateTimeOffset.UtcNow);

                Assert.True(File.Exists(Path.Combine(_receive, "报告 1+1.txt")),
                    "文件没有落到接收目录，那这里测的就不是画进度行，而是收文件。");
                await WaitUntil(() => window.TransferList.Items.Count == 1, "手机发来的文件没有在传输栏里留下进度行");

                // Laying the list out is what a shown window does, and it is the only thing that instantiates the
                // row template — which is where its bindings are attached. RangeBase.Value binds TwoWay by default,
                // so a ProgressBar pointed at the read-only LanProgressRow.Percentage without an explicit
                // Mode=OneWay throws XamlParseException right here, and in the app that throw comes out of a
                // Dispatcher callback on the first progress tick of the first inbound file, which kills the
                // process mid-transfer. A window this suite never shows never lays anything out, so building it
                // and reading its collections could stay green over a row that cannot be drawn at all — which is
                // exactly how the cloud transfer window died before, for the same binding.
                Realise(window.TransferList);

                var bar = Descendants<ProgressBar>(window.TransferList).Single();
                Assert.Equal(100d, bar.Maximum);
                Assert.True(bar.Value is >= 0d and <= 100d, bar.Value.ToString());
            }
            finally
            {
                await window.ShutdownAsync();
            }
        });
    }

    [Fact]
    public Task A_file_the_user_puts_in_the_room_is_held_here_and_shown_without_a_receive_button()
    {
        return RunSta(async () =>
        {
            var port = FreePort();
            var file = Path.Combine(_root, "季度 报告+1.txt");
            File.WriteAllText(file, "hello");
            var folder = Path.Combine(_root, "一个 目录");
            Directory.CreateDirectory(folder);
            var window = NewWindow(port);
            await window.OpenAsync();
            try
            {
                // A path that is gone is skipped rather than announced: an entry whose bytes nobody holds is a
                // 接收 button that can only ever fail, and a room full of those reads as damage.
                Assert.Equal(Visibility.Visible, window.ChatEmptyText.Visibility);
                var announced = window.Announce([file, folder, Path.Combine(_root, "不存在的.txt")]);
                Assert.Equal(2, announced);

                var entries = window.Group.Entries;
                Assert.Equal(2, entries.Count);
                Assert.All(entries, entry => Assert.True(entry.HeldHere, entry.MessageId));
                Assert.All(entries, entry => Assert.True(entry.LocalPath!.StartsWith(_root, StringComparison.Ordinal),
                    entry.LocalPath));
                Assert.Single(entries, entry => entry.IsDirectory);

                // No 接收 on a file this copy can hand over, and the line under it says where it is.
                Assert.Equal(2, window.ChatList.Items.Count);
                Assert.Equal(Visibility.Collapsed, window.ChatEmptyText.Visibility);
                var row = (LanChatRow)window.ChatList.Items[0];
                Assert.Equal(Visibility.Collapsed, row.ActionVisibility);
                Assert.Equal(Tr.Format("Lan.ChatHeld", Path.GetFileName(file)), row.Detail);
            }
            finally
            {
                await window.ShutdownAsync();
            }

            // Session memory by design: the reading half of the room belongs to this window, so closing it takes
            // away the one thing that could pull these bytes toward a peer.
            Assert.Null(window.Room);
        });
    }

    [Fact]
    public Task 一句打出来的话在房间里只出现一次_and_a_refused_one_stays_in_the_box()
    {
        return RunSta(async () =>
        {
            var port = FreePort();
            var window = NewWindow(port);
            await window.OpenAsync();
            try
            {
                window.ChatInputBox.Text = "下午三点之前把报告发我";
                window.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntil(() => window.ChatList.Items.Count == 1, "typed 的话没有出现");
                Assert.Equal("", window.ChatInputBox.Text);

                var row = (LanChatRow)window.ChatList.Items[0];
                Assert.Equal("下午三点之前把报告发我", row.Body);
                Assert.True(row.IsMine);
                // One id for one sentence, minted before anything left this machine: the same words arriving later
                // from a member's poll have to land on this row rather than open a second one.
                var entry = Assert.Single(window.Group.Entries);
                Assert.Equal(row.MessageId, entry.MessageId);
                Assert.Equal(LanGroupKind.Text, entry.Kind);

                // 空 is not a sentence, and a sentence longer than the room holds is refused before a request is
                // spent on it.
                Assert.Equal(LanRefusal.BadRequest, await window.SayAsync("   "));
                var tooLong = new string('字', LanGroupLog.MaximumTextLength + 1);
                Assert.Equal(LanRefusal.MessageTooLong, await window.SayAsync(tooLong));
                Assert.Equal(1, window.Group.Count);

                // The box keeps what it was refused: what a person typed must not be lost to their own typing limit.
                window.ChatInputBox.Text = tooLong;
                window.SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntil(() => window.StatusText.Text == Tr.F("Lan.Refuse.MessageTooLong"), "长句没有被拒绝");
                Assert.Equal(tooLong, window.ChatInputBox.Text);
            }
            finally
            {
                await window.ShutdownAsync();
            }
        });
    }

    [Fact]
    public Task 房间里一个本机没有的文件给了一个接收按钮_and_the_answer_lands_on_that_same_row()
    {
        return RunSta(async () =>
        {
            var port = FreePort();
            var window = NewWindow(port);
            await window.OpenAsync();
            try
            {
                // A member's announcement, in exactly the shape the poll merges it. The wire claims the bytes are
                // held where it came from, and this copy must not inherit that claim.
                var id = Guid.NewGuid().ToString("N");
                Assert.True(window.Group.TryMerge(new LanMessageWire(id, StrangerId, "小明的手机", 7,
                    LanGroupKind.File, null, "季度 报告.txt", "季度 报告.txt", 2048, false,
                    DateTimeOffset.UtcNow, true)));
                await WaitUntil(() => window.ChatList.Items.Count == 1, "别人报的文件没有画出气泡");

                var row = (LanChatRow)window.ChatList.Items[0];
                Assert.False(row.IsMine);
                Assert.Contains("小明的手机", row.AuthorLine);
                Assert.Equal("季度 报告.txt", row.Body);
                Assert.Contains(ByteSize.Format(2048), row.Detail);
                Assert.Equal(Visibility.Visible, row.ActionVisibility);

                // A pull in flight draws its bar on the bubble, and laying the row out is the only way to attach
                // that binding at all — see the note on the transfer list's own case above.
                row.SetProgress(1024, 2048);
                Realise(window.ChatList, 900, 900);
                var bar = Descendants<ProgressBar>(window.ChatList).Single();
                Assert.Equal(100d, bar.Maximum);
                Assert.Equal(50d, bar.Value, 3);
                var button = Descendants<Button>(window.ChatList).Single();
                Assert.Equal(Tr.F("Lan.Receive"), button.Content);

                // The click reaches the room, and the room's answer paints on the row it came from rather than
                // opening a new one. Nobody here can be dialled, so the entry stays as the room's promise: a
                // machine that is switched off tonight may still answer it tomorrow.
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntil(() => row.Detail.Contains(Tr.F("Lan.Refuse.NotFound")), "点了接收，房间的回答没有落回这一行");
                Assert.Single(window.ChatList.Items);
                Assert.Equal(Visibility.Visible, row.ActionVisibility);
                Assert.Equal(Visibility.Collapsed, row.ProgressVisibility);
                Assert.Equal(Tr.Format("Lan.RoomRefused", "季度 报告.txt", Tr.F("Lan.Refuse.NotFound")),
                    window.StatusText.Text);
                // And the room keeps what it was told: a refused pull does not un-say an announcement.
                var entry = Assert.Single(window.Group.Entries);
                Assert.False(entry.HeldHere);
            }
            finally
            {
                await window.ShutdownAsync();
            }
        });
    }

    [Fact]
    public Task 改名之后屏幕上是名字表里存着的那个_and_房间里多说一句而不是两句()
    {
        return RunSta(async () =>
        {
            var port = FreePort();
            var window = NewWindow(port);
            await window.OpenAsync();
            try
            {
                var before = (await LanDeviceIdentity.LoadOrCreateAsync()).DeviceName;
                Assert.Equal(before, window.DeviceNameBox.Text);

                const string chosen = "小风的笔记本";
                Assert.Equal(chosen, await window.RenameSelfAsync(chosen));
                Assert.Equal(chosen, window.DeviceNameBox.Text);
                // The identity file is the only store a device name has, so this is what the next window reads.
                Assert.Equal(chosen, (await LanDeviceIdentity.LoadOrCreateAsync()).DeviceName);

                // Said once, as narration rather than as something this machine "said": no author line, and the
                // status line is left free for the failures that need it.
                var line = Assert.Single(window.Group.Entries);
                Assert.Equal("", line.AuthorDeviceId);
                Assert.Equal(Tr.Format("Lan.RenamedSelf", chosen), line.Text);
                var row = (LanChatRow)window.ChatList.Items[0];
                Assert.True(row.IsSystem);
                Assert.Equal(Visibility.Visible, row.SystemVisibility);
                Assert.Equal(Visibility.Collapsed, row.BubbleVisibility);

                // A blank is not a rename. The box stops showing what was typed, and the room does not hear that
                // the machine is still called the same thing.
                Assert.Equal(chosen, await window.RenameSelfAsync("   "));
                Assert.Equal(chosen, window.DeviceNameBox.Text);
                Assert.Equal(1, window.Group.Count);
            }
            finally
            {
                await window.ShutdownAsync();
            }
        });
    }

    [Fact]
    public Task The_six_characters_on_screen_are_the_ones_the_picture_carries_and_the_picture_is_not_a_file()
    {
        return RunSta(async () =>
        {
            var port = FreePort();
            var window = NewWindow(port);
            await window.OpenAsync();
            try
            {
                var code = window.PairCodeText.Text;
                Assert.Equal(LanPairCode.Length, code.Length);
                Assert.All([.. code], character => Assert.True(LanPairCode.Alphabet.Contains(character, StringComparison.Ordinal), character.ToString()));

                // The bitmap comes out of memory and is never written out: a pairing QR is a secret with a
                // two-minute shelf life, and a stray .png in a temp folder is the one thing this must not leave.
                Assert.True(window.QrImage.Source is BitmapImage, window.StatusText.Text);
                var picture = (BitmapImage)window.QrImage.Source;
                Assert.True(picture.IsFrozen);
                var url = Assert.IsType<string>(window.QrImage.ToolTip);
                // The address is whichever adapter the picture was drawn for, so the port and the page are what
                // a case can name in advance — and no credential is anywhere in it.
                Assert.True(url.StartsWith("http://", StringComparison.Ordinal), url);
                Assert.Contains($":{port}/p", url);
                Assert.False(url.Contains("token", StringComparison.OrdinalIgnoreCase), url);
                // The same URL in a box beside the code, and it is the self-pairing one: the code rides in the
                // fragment, so a person who copies this line onto another computer has paired it by opening it
                // rather than being asked to retype the six characters they just copied alongside. Read-only, so
                // the box cannot become a place a person edits the address in.
                Assert.Equal(url, window.PairUrlBox.Text);
                Assert.Contains($"#k={code}", window.PairUrlBox.Text);
                Assert.Contains("&d=", window.PairUrlBox.Text);
                Assert.Contains("&v=1", window.PairUrlBox.Text);
                Assert.Contains("&e=", window.PairUrlBox.Text);
                Assert.True(window.PairUrlBox.IsReadOnly);

                // 重新生成 replaces the spent-or-aging code, and the window repaints the picture for it — the box
                // and the picture move together, because the code is in both of them.
                window.RegenerateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntil(() => window.PairCodeText.Text != code, "重新生成没有换掉配对码");
                Assert.Contains($"#k={window.PairCodeText.Text}", window.PairUrlBox.Text);
            }
            finally
            {
                await window.ShutdownAsync();
            }
        });
    }

    [Fact]
    public Task The_six_characters_can_be_clicked_away_instead_of_transcribed()
    {
        return RunSta(async () =>
        {
            var window = NewWindow(FreePort());
            var copied = new List<string>();
            window.CopyToClipboard = copied.Add;
            await window.OpenAsync();
            try
            {
                var code = window.PairCodeText.Text;
                // Nothing is claimed before the click: the line under the code says what the click would do, and
                // only the click turns that into 已复制.
                Assert.Equal(Tr.F("Lan.CopyCode"), window.PairCopyHintText.Text);

                window.CopyPairCodeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(code, Assert.Single(copied));
                Assert.Equal(Tr.F("Lan.CodeCopied"), window.PairCopyHintText.Text);

                // A new code makes that claim false — what sits in the clipboard is the one the pairing state has
                // already thrown away — so the line goes back to offering the click.
                window.RegenerateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntil(() => window.PairCodeText.Text != code, "重新生成没有换掉配对码");
                Assert.Equal(Tr.F("Lan.CopyCode"), window.PairCopyHintText.Text);
            }
            finally
            {
                await window.ShutdownAsync();
            }
        });
    }

    [Fact]
    public Task A_phone_that_cannot_be_dialled_is_received_for_by_the_window_s_own_listener()
    {
        return RunSta(async () =>
        {
            var port = FreePort();
            var file = Path.Combine(_root, "quarter-report.txt");
            File.WriteAllText(file, "第一季度的数字");
            var window = NewWindow(port);
            await window.OpenAsync();
            try
            {
                Assert.Equal(1, window.Announce([file]));
                var messageId = Assert.Single(window.Group.Entries,
                    entry => entry.Kind == LanGroupKind.File).MessageId;

                using var phone = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                var paired = await phone.PostAsync("/api/v1/pair", Body(new LanPairRequest(
                    window.PairCodeText.Text!, StrangerId, "小明的手机", LanProtocol.KindPhone)));
                paired.EnsureSuccessStatusCode();
                var token = JsonSerializer.Deserialize<LanPairReply>(
                    await paired.Content.ReadAsStringAsync(), LanApi.Json)!.Token;

                // The 接收 a phone presses is a POST to this machine's own listener, because a browser holds no
                // port nobody can dial. Only the window can make that call answer: it builds the room's reader
                // before the listener and hands it over, so a listener started without one replies 这里没有 here.
                // Nothing in FengSync.Tests can see that composition — it is the window's, not the server's.
                var taken = await phone.SendAsync(
                    Authenticated(HttpMethod.Post, $"/api/v1/attachments/{messageId}/receive", token));
                Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
                var body = await taken.Content.ReadAsStringAsync();
                var row = JsonSerializer.Deserialize<LanMessageWire>(body, LanApi.Json)!;
                Assert.Equal(messageId, row.MessageId);
                Assert.True(row.HeldHere, row.MessageId);

                // No bytes moved, because the file was already here: this is the window's own announcement, so the
                // room answers without asking anybody and the receive directory stays untouched.
                Assert.DoesNotContain("localPath", body, StringComparison.OrdinalIgnoreCase);
                Assert.Empty(Directory.EnumerateFileSystemEntries(_receive));

                // And the row it was given is the one it can then download, by the id its author minted rather than
                // by the id its author minted, and by nothing a caller can build out of a path.
                var bytes = await phone.SendAsync(
                    Authenticated(HttpMethod.Get, $"/api/v1/attachments/{messageId}", token));
                bytes.EnsureSuccessStatusCode();
                Assert.Equal("第一季度的数字", await bytes.Content.ReadAsStringAsync());
            }
            finally
            {
                await window.ShutdownAsync();
            }
        });
    }

    [Fact]
    public Task A_device_that_stops_saying_it_is_here_leaves_the_room_and_the_room_says_so()
    {
        return RunSta(async () =>
        {
            var port = FreePort();
            var window = NewWindow(port);
            await window.OpenAsync();
            try
            {
                Assert.Equal(Visibility.Visible, window.MemberEmptyText.Visibility);

                window.Presence.Seen(new LanRoomMember(StrangerId, "小明的手机", LanProtocol.KindPhone, "192.168.1.5", 0,
                    DateTimeOffset.UtcNow, Discovered: false));

                var chip = Assert.IsType<LanMemberRow>(Assert.Single(window.MemberList.Items));
                Assert.Equal("小明的手机", chip.Name);
                Assert.Contains(Tr.F("Lan.KindPhone"), chip.Detail);
                Assert.Equal(Visibility.Collapsed, window.MemberEmptyText.Visibility);

                // A phone whose browser was closed cannot send anything, so this absence — not an event — is what
                // 退群 means. The sweep is the same clock the window's own tick runs on, moved past the lease.
                window.Presence.Sweep(DateTimeOffset.UtcNow + LanRoomPresence.DefaultTimeToLive + TimeSpan.FromSeconds(1));
                await WaitUntil(() => window.MemberList.Items.Count == 0, "断连的设备没有退群");
                Assert.Equal(Visibility.Visible, window.MemberEmptyText.Visibility);
                // …and the room says it, because a chip that vanished with no explanation is a list that lied.
                Assert.Equal(Tr.Format("Lan.LeftRoom", "小明的手机"), window.Group.Entries[^1].Text);
            }
            finally
            {
                await window.ShutdownAsync();
            }
        });
    }

    [Fact]
    public Task A_computer_heard_on_the_subnet_is_in_the_room_too_and_leaves_without_a_word()
    {
        return RunSta(async () =>
        {
            var port = FreePort();
            var window = NewWindow(port);
            await window.OpenAsync();
            try
            {
                var now = DateTimeOffset.UtcNow;
                window.Presence.Seen(new LanRoomMember(ReturningId, "客厅的台式机", LanProtocol.KindUnknown, "192.168.1.9",
                    port, now, Discovered: true));

                // 发现的电脑 belongs in the room's device list and not only in a corner of the pairing panel: it is
                // the machine a person is most likely to pair with next, and a device list that left it out made
                // them hunt for it somewhere else.
                var chip = Assert.IsType<LanMemberRow>(Assert.Single(window.MemberList.Items));
                Assert.Equal("客厅的台式机", chip.Name);
                Assert.Contains(Tr.F("Lan.MemberDiscovered"), chip.Detail);
                Assert.Contains($"192.168.1.9:{port}", chip.Detail);

                window.Presence.Sweep(now + LanRoomPresence.DefaultTimeToLive + TimeSpan.FromSeconds(1));

                // It leaves silently: it never joined anything, so a line announcing its departure would narrate
                // a membership it does not have. The room therefore stays empty.
                Assert.Empty(window.MemberList.Items);
                Assert.Empty(window.Group.Entries);
            }
            finally
            {
                await window.ShutdownAsync();
            }
        });
    }

    [Fact]
    public Task The_trusted_list_has_its_own_window_and_forgetting_there_takes_the_credential_with_it()
    {
        return RunSta(async () =>
        {
            var port = FreePort();
            var now = DateTimeOffset.UtcNow;
            await new LanPeerStore().RememberAsync(new LanPeerRecord(ReturningId, "旧手机", LanProtocol.KindPhone,
                now, now, port, ["192.168.1.30"]));
            var window = NewWindow(port);
            await window.OpenAsync();
            try
            {
                var dialog = new LanTrustedDevicesWindow(new LanPeerStore(), deviceId => window.Session!.ForgetAsync(deviceId));
                await dialog.RefreshAsync();
                try
                {
                    var row = Assert.IsType<LanRow>(Assert.Single(dialog.Rows));
                    Assert.Equal("旧手机", row.Name);
                    Assert.Contains(Tr.F("Lan.KindPhone"), row.Detail);
                    Assert.Contains("192.168.1.30", row.Detail);
                    Assert.Equal(Visibility.Collapsed, dialog.EmptyText.Visibility);

                    // The button is the whole interface of this window, so the click is what a case has to make —
                    // and laying the list out is the only thing that instantiates the row template the button
                    // lives in.
                    Realise(dialog.TrustedList, 560, 360);
                    Descendants<Button>(dialog.TrustedList).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitUntil(() => dialog.Rows.Count == 0, "取消信任没有把行去掉");

                    // Both halves, which is the point of the dialog owning a verb rather than a store: a row left
                    // behind in the file would let the device back in, and a live token without a row would too.
                    Assert.Empty(await new LanPeerStore().LoadAsync());
                    Assert.Equal(Tr.Format("Lan.Forgotten", "旧手机"), dialog.StatusText.Text);
                    Assert.Equal(Visibility.Visible, dialog.EmptyText.Visibility);
                }
                finally { dialog.Close(); }
            }
            finally
            {
                await window.ShutdownAsync();
            }
        });
    }

    // ------------------------------------------------------------------ harness
    /// <summary>
    /// A window pointed at its own port and its own receive directory, with discovery off. Every case gets one,
    /// because a listener left running would answer the next test's pairing code.
    /// </summary>
    private LanTransferWindow NewWindow(int port, Func<LanInboundOffer, CancellationToken, Task<bool>>? confirmInbound = null,
        LanProcessRunner? netsh = null) =>
        new(new LanTransferDependencies(port, _receive, confirmInbound, netsh ?? new Netsh(ruleIsPresent: true).RunAsync, DiscoverPeers: false));

    private static LanInboundOffer Offer(string path, string deviceId, int port) =>
        new(new LanContact(deviceId, "小明的手机", LanProtocol.KindPhone, "127.0.0.1", port), path, 12, false, "sess");

    /// <summary>The identity of the machine doing the calling, in this test's own scratch folder.</summary>
    private string IdentityPath() => Path.Combine(_root, "probe-device.json");

    private Task<LanDeviceIdentity> IdentityAsync() => LanDeviceIdentity.LoadOrCreateAsync(IdentityPath());

    /// <summary>The list a probing caller keeps, so pairing it records an address without touching the window's own trust file.</summary>
    private LanPeerStore Members() => new(Path.Combine(_root, "probe-peers.json"));

    /// <summary>A request body in the shape this protocol reads, so a case sends what the shipped page sends.</summary>
    private static StringContent Body<T>(T payload) =>
        new(JsonSerializer.Serialize(payload, LanApi.Json), Encoding.UTF8, "application/json");

    /// <summary>
    /// The sentence a transfer row puts beside the name, and the silence it keeps when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// The three cases that are not the happy one matter as much as it does. A rate of zero is the board's first
    /// quarter second, and "0 B/s" there reads as a transfer that has stalled rather than one that just started. A
    /// folder declares no length at all, so there is no arrival to count down to — the rate is still worth saying,
    /// the time is not. And a transfer already at its total has nothing left to wait for.
    /// </remarks>
    [Fact]
    public void A_transfer_row_says_its_speed_and_the_time_left_in_the_same_sentence()
    {
        const long megabyte = 1024 * 1024;

        Assert.Equal("", LanTransferWindow.SpeedLine(0, 0, 3 * megabyte));
        Assert.Equal("", LanTransferWindow.SpeedLine(0, 0, 0));

        var counted = LanTransferWindow.SpeedLine(megabyte, megabyte, 3 * megabyte);
        Assert.StartsWith("1.0 MB/s", counted, StringComparison.Ordinal);
        Assert.Contains(Tr.Format("Lan.EtaSeconds", 2), counted, StringComparison.Ordinal);

        // No total: the rate alone, and no countdown invented out of a number nobody sent.
        Assert.Equal("1.0 MB/s", LanTransferWindow.SpeedLine(megabyte, megabyte, 0));

        // Nothing left: the same, because a "还剩 0 秒" is a sentence about a wait that is already over.
        Assert.Equal("1.0 MB/s", LanTransferWindow.SpeedLine(megabyte, 3 * megabyte, 3 * megabyte));

        // A long wait is written in the units a person would say it in, not in five hundred seconds.
        var slow = LanTransferWindow.SpeedLine(1024, 0, 500 * 1024L);
        Assert.Contains(Tr.Format("Lan.EtaMinutes", 8, 20), slow, StringComparison.Ordinal);
    }

    [Fact]
    public void A_finished_row_keeps_its_last_number_but_drops_the_rate()
    {
        var row = new LanProgressRow("接收 a.bin ← 手机（1M / 2M）", true);
        Assert.Equal(Visibility.Collapsed, row.RateVisibility);

        row.Update("接收 a.bin ← 手机（1M / 2M）", 50, "1.0 MB/s");
        Assert.Equal(Visibility.Visible, row.RateVisibility);
        Assert.Equal(50d, row.Percentage, 3);
        Assert.Null(row.FinishedAt);

        row.Finish(LanTransferState.Completed, "");
        Assert.Equal(Visibility.Collapsed, row.RateVisibility);
        Assert.Equal(100d, row.Percentage);
        // The stamp is what the one-second tick sweeps on; a row that never sets it would sit in the strip for the
        // rest of the session.
        Assert.NotNull(row.FinishedAt);
    }

    [Fact]
    public void A_bubble_takes_its_bar_away_when_the_pull_is_over_without_forgetting_what_it_says()
    {
        var entry = new LanGroupMessage
        {
            MessageId = Guid.NewGuid().ToString("N"),
            AuthorDeviceId = StrangerId,
            AuthorName = "小明的手机",
            Kind = LanGroupKind.File,
            FileName = "季度 报告.txt",
            RelativePath = "季度 报告.txt",
            Bytes = 2048,
            CreatedUtc = DateTimeOffset.UtcNow,
            HeldHere = true,
        };
        var row = new LanChatRow(entry, isMine: false);

        row.SetProgress(1024, 2048, "1.0 MB/s");
        Assert.Equal(Visibility.Visible, row.ProgressVisibility);
        Assert.Equal(Visibility.Visible, row.RateVisibility);
        Assert.Contains("1.0 MB/s", row.AutomationLine, StringComparison.Ordinal);

        // The sentence about where the bytes landed arrives on a different event than the last progress tick, so
        // the bar has to be takeable down on its own — otherwise a finished pull keeps a full bar until the fetch
        // event happens to arrive.
        row.ClearProgress();
        Assert.Equal(Visibility.Collapsed, row.ProgressVisibility);
        Assert.Equal(Visibility.Collapsed, row.RateVisibility);
        Assert.DoesNotContain("1.0 MB/s", row.AutomationLine, StringComparison.Ordinal);
        Assert.Equal("季度 报告.txt", row.Body);
    }

    /// <summary>
    /// One call carrying the credential a pairing just handed out, in the header form rather than the cookie form —
    /// which is the form a test can name a device id in, and the one a phone's own <c>fetch</c> uses.
    /// </summary>
    private static HttpRequestMessage Authenticated(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(LanProtocol.AuthScheme, $"{StrangerId}:{token}");
        return request;
    }

    /// <summary>A port nobody is holding, so two tests on one machine never bind the same number.</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Any, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// <c>netsh</c> as this suite will have it: the same answer for both rules, either present or absent, and
    /// every call recorded so "never elevated" and "elevated twice, after reading" are both assertable.
    /// </summary>
    private sealed class Netsh(bool ruleIsPresent)
    {
        private readonly List<LanFirewallCall> _calls = [];
        private readonly object _gate = new();

        public IReadOnlyList<LanFirewallCall> Calls
        {
            get { lock (_gate) return [.. _calls]; }
        }

        public Task<LanProcessOutcome> RunAsync(LanFirewallCall call, CancellationToken ct)
        {
            lock (_gate) _calls.Add(call);
            if (call.Elevated) return Task.FromResult(new LanProcessOutcome(0, ""));
            // A present rule is answered by echoing the name out of the arguments, which is the only thing
            // ReportsRule looks at; a missing one is the sentence netsh really writes on a Chinese machine.
            return Task.FromResult(new LanProcessOutcome(0, ruleIsPresent ? call.Arguments : "没有与指定标准相匹配的规则。"));
        }
    }

    /// <summary>
    /// Gives a list the size a shown window would give it. A window that is never displayed never measures
    /// anything, and an ItemsControl that is never measured never generates a container — so without this its row
    /// template, and every binding in it, is never attached and never runs.
    /// </summary>
    private static void Realise(ItemsControl list, double width = 900, double height = 300)
    {
        list.Measure(new Size(width, height));
        list.Arrange(new Rect(0, 0, width, height));
        list.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    /// <summary>Waits for a state the window reaches on its own, pumping the dispatcher so its continuations run.</summary>
    private static async Task WaitUntil(Func<bool> condition, string message)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail(message);
    }

    /// <summary>
    /// Runs one action on its own STA thread with the dispatcher pumped while it is in flight, and rethrows
    /// whatever it threw. The dispatcher is shut down explicitly because the process allows only one WPF
    /// <see cref="Application"/>, and the watchdog is there so a listener that never stops cannot hang the suite.
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
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
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
                failure = new TimeoutException("局域网快传的窗口测试没有在规定时间内完成（监听器没有停下来）。");
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

    /// <summary>Gives this thread an Application to resolve the window's theme dictionaries through, and says whether
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
}
