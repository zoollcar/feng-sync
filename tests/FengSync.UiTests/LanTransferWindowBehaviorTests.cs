using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
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

                using var probe = new LanPeerClient(await IdentityAsync());
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
                Assert.Single(window.TrustedList.Items);
                Assert.Equal(Visibility.Collapsed, window.TrustedEmptyText.Visibility);
                Assert.True(window.Session!.AutoAcceptFromTrusted);

                // The row was in the list before this window opened, so its bytes are its own business.
                Assert.Equal(LanRefusal.None, await Task.Run(() => window.Session!.AdmitWriteAsync(Offer("第一个.txt", ReturningId, port))));
                Assert.Empty(window.PendingList.Items);

                window.AutoAcceptBox.IsChecked = false;
                await WaitUntil(() => !window.Session!.AutoAcceptFromTrusted, "关掉的开关没有落到正在跑的监听器上");

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
                using var probe = new LanPeerClient(await IdentityAsync());
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
                Realise(window);

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
    public Task The_basket_holds_only_what_was_dropped_and_nothing_at_all_after_the_window_closes()
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
                // A path that is gone and a path already on the list are both skipped, so four drops make two
                // rows rather than two rows and a lie about what is shareable.
                var added = window.Basket.Add([file, folder, Path.Combine(_root, "不存在的.txt"), file]);
                Assert.Equal(2, added);
                Assert.Equal(2, window.Basket.Rows.Count);

                // One collection, drawn twice: the rows on screen are exactly what a peer may list, in the same
                // order, because a second list would let 清空 and the peer's answer disagree.
                var snapshot = window.Session!.Basket.Snapshot();
                Assert.Equal(window.Basket.Rows.Select(row => row.Item.Id), snapshot.Select(item => item.Id));
                Assert.All(snapshot, item => Assert.True(item.AbsolutePath.StartsWith(_root, StringComparison.Ordinal), item.AbsolutePath));

                window.ClearBasketButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(window.Basket.IsEmpty);
                Assert.Empty(window.Session!.Basket.Snapshot());

                window.Basket.Add([file]);
                Assert.False(window.Basket.IsEmpty);
            }
            finally
            {
                await window.ShutdownAsync();
            }

            // Session memory by design: a row left over from this window would still be readable by a token its
            // peer keeps, and closing the window is supposed to close that door.
            Assert.True(window.Basket.IsEmpty);
            Assert.Empty(window.Session!.Basket.Snapshot());
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

                // 重新生成 replaces the spent-or-aging code, and the window repaints the picture for it.
                window.RegenerateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntil(() => window.PairCodeText.Text != code, "重新生成没有换掉配对码");
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
    /// Gives the transfer list the size a shown window would give it. A window that is never displayed never
    /// measures anything, and an ItemsControl that is never measured never generates a container — so without
    /// this its row template, and every binding in it, is never attached and never runs.
    /// </summary>
    private static void Realise(LanTransferWindow window)
    {
        window.TransferList.Measure(new Size(900, 300));
        window.TransferList.Arrange(new Rect(0, 0, 900, 300));
        window.TransferList.UpdateLayout();
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
