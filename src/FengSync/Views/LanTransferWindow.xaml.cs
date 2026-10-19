using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FengSync.Core.Configuration;
using FengSync.Lan;
using FengSync.Localization;
using FengSync.Services;
using Microsoft.Win32;

namespace FengSync.Views;

/// <summary>
/// The window that owns a LAN listener and draws what the room of paired machines said: it pairs, it receives,
/// it speaks, and it hands out whatever it put in the room — for as long as this window is open, and not one
/// second longer.
/// </summary>
/// <remarks>
/// <para>
/// The service is a field of the window rather than of the shell on purpose. A listener that outlived its
/// window would answer a pairing code nobody is watching, and the one promise this feature makes about its
/// blast radius is that closing the window closes the door. The shell holds only a reference to the open
/// window, so application shutdown can await the teardown; the reference arrives through
/// <see cref="ShowOrActivate"/> rather than by this window reaching for <c>Application.Current</c>, which is
/// how a shared window stays hostable by any shell.
/// </para>
/// <para>
/// Everything with a decision in it lives in <see cref="LanSession"/>, <see cref="LanGroupLog"/>,
/// <see cref="LanGroupRoom"/> and <see cref="LanReceiveSink"/>, none of which needs a socket to be tested. What
/// is here is WPF state: bubbles on screen, a countdown, and the one place a person's 同意 or 拒绝 becomes an
/// answer the listener can act on.
/// </para>
/// <para>
/// The window owns the room log because closing this window is the only thing that is supposed to end this
/// machine's part in it. <see cref="LanSession"/> takes that same log on every listener restart, so changing
/// the port or the receive directory moves the door and not the conversation — while a window that never
/// reopened simply leaves, and its copy of the history goes with it.
/// </para>
/// </remarks>
public partial class LanTransferWindow : Window
{
    /// <summary>How often the countdown and the printed code are re-read off the pairing state.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long 已复制 stays under the code. Long enough to be noticed on a screen somebody is looking away
    /// from, short enough that it is gone before the countdown beside it needs the room.
    /// </summary>
    private static readonly TimeSpan CopyHintLinger = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a finished transfer row stays in the list before the tick takes it away.
    /// </summary>
    /// <remarks>
    /// Long enough to read the last number a transfer ever wrote, short enough that a folder of twenty files does
    /// not turn the strip into a wall of full bars. The room already carries the permanent record of what landed.
    /// </remarks>
    private const long FinishedLingerMs = 5000;

    private readonly LanTransferDependencies _deps;
    private readonly App? _shell;

    /// <summary>
    /// What this machine knows the room said — in memory, and gone when this window is.
    /// </summary>
    private readonly LanGroupLog _group = new();

    /// <summary>
    /// Who is in the room right now, which is a different question from who has ever been trusted.
    /// </summary>
    /// <remarks>
    /// One instance for the life of the window, handed to both the session and the room's reader so the two
    /// halves of this machine's own traffic renew the same lease. It survives a listener restart the way the log
    /// does: changing the port moves the door, and a device that is still announcing itself is still here.
    /// </remarks>
    private readonly LanRoomPresence _presence = new();

    private readonly ObservableCollection<LanChatRow> _chat = [];
    private readonly ObservableCollection<LanMemberRow> _members = [];
    private readonly ObservableCollection<LanRow> _pending = [];
    private readonly ObservableCollection<LanProgressRow> _transfers = [];
    private readonly ObservableCollection<LanRow> _history = [];
    private readonly Dictionary<string, LanChatRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// The computers this socket has heard, newest last, for the manual-address fallback to suggest.
    /// </summary>
    /// <remarks>
    /// Not the room's device list — that one is <see cref="_presence"/>, which knows who is here <i>now</i>. This
    /// is only "what address should the box already hold", and a stale entry in it costs nothing because the
    /// probe in front of pairing answers whether anything is there.
    /// </remarks>
    private readonly List<LanDiscoveredPeer> _found = [];
    private readonly List<LanPendingOffer> _awaiting = [];
    private readonly Dictionary<string, LanProgressRow> _running = [];
    private readonly object _gate = new();

    private ApplicationSettings _settings = new();
    private LanDeviceIdentity? _identity;
    private LanPeerStore? _peers;
    private LanPeerSecretStore? _secrets;
    private LanPairingState? _pairing;
    private LanSession? _session;
    private LanServerHost? _host;
    private LanGroupRoom? _room;
    private LanDiscoveryService? _discovery;
    private LanPeerClient? _client;
    private LanTransferHistoryRepository? _archive;
    private DispatcherTimer? _timer;
    private DispatcherTimer? _copyHintTimer;
    private CancellationTokenSource? _lifetime;
    private Task _teardown = Task.CompletedTask;
    private bool _teardownStarted;
    private string? _receiveDirectory;
    private string? _paintedCode;
    private string? _paintedAddress;
    private string _chosenAddress = "";
    // Assigning 自动接收 from the settings file raises the same Checked event a click does, and that handler
    // saves settings and writes the status line. So the read is marked, or every listener restart would answer
    // a choice nobody made and overwrite the sentence the user was reading.
    private bool _restoringToggle;

    /// <summary>Opens the one window this shell can have, or raises the one already open.</summary>
    internal static void ShowOrActivate(Window owner, App shell, LanTransferDependencies? dependencies = null)
    {
        // Non-modal and singleton by type: a second listener would compete for the port the firewall rule
        // names, and a modal window would freeze the shell for as long as a phone is deciding what to send.
        if (Application.Current.Windows.OfType<LanTransferWindow>().FirstOrDefault() is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        new LanTransferWindow(dependencies, shell) { Owner = owner }.Show();
    }

    public LanTransferWindow() : this(null, null) { }

    internal LanTransferWindow(LanTransferDependencies? dependencies, App? shell = null)
    {
        _deps = dependencies ?? new LanTransferDependencies();
        _shell = shell;
        InitializeComponent();
        ChatList.ItemsSource = _chat;
        MemberList.ItemsSource = _members;
        PendingList.ItemsSource = _pending;
        TransferList.ItemsSource = _transfers;
        HistoryList.ItemsSource = _history;
        AddressList.ItemsSource = Array.Empty<string>();
        // The log is written by a peer's poll, by a pushed arrival inside the listener, and by this window's own
        // composer, so its events arrive on whichever thread spoke. Everything they touch is on the dispatcher.
        _group.Appended += entry => OnUi(() => ShowEntry(entry));
        _group.Trimmed += entry => OnUi(() => ForgetEntry(entry));
        // Presence is fed from a request thread, a poll thread and the discovery socket, and every one of those
        // touches the device strip. A departure is also a line in the room, because a phone whose browser was
        // closed is exactly the thing a person would otherwise have to guess at.
        _presence.Joined += member => OnUi(() => ShowMember(member));
        _presence.Updated += member => OnUi(() => ShowMember(member));
        _presence.Left += member => OnUi(() => DropMember(member));
        // WPF has no Opened event on Window; ContentRendered is the one that means "the person can see it",
        // and it fires once rather than on every re-hive the way Loaded does.
        ContentRendered += async (_, _) => await OpenAsync();
        UpdateEmptyHints();
        // The line under the code has two states and only two: what a click does, and that it just happened.
        // Starting it empty would leave the copy affordance to a hover tooltip nobody has a reason to try.
        PairCopyHintText.Text = Tr.F("Lan.CopyCode");
    }

    /// <summary>The seams a test installs in place of a network, a firewall prompt, or a person.</summary>
    internal LanTransferDependencies Dependencies => _deps;

    /// <summary>What the listener is receiving into right now, for a test that needs to read the screen and the state together.</summary>
    internal LanSession? Session => _session;

    /// <summary>The reading half of the room, which is the half that pulls a file a member only announced.</summary>
    internal LanGroupRoom? Room => _room;

    internal LanGroupLog Group => _group;

    /// <summary>The room's device list, which is a lease rather than a file: it is what 退群 is measured against.</summary>
    internal LanRoomPresence Presence => _presence;

    internal IReadOnlyList<string> ListenAddresses => _host?.ListenAddresses ?? [];

    /// <summary>
    /// Where a copied pairing code goes. A seam for the same reason <c>CloudTransferWindow</c> has one: the
    /// clipboard belongs to the machine and to whoever is using it, so a test asserts what this window asked to
    /// be copied rather than reading back whatever the last test left there.
    /// </summary>
    internal Action<string>? CopyToClipboard { get; set; }

    /// <summary>
    /// Raises the listener. Internal rather than a private <c>Opened</c> handler because a window an in-process
    /// test never shows still has to be started — that is the only way the test can assert what a real open
    /// does rather than a re-creation of it.
    /// </summary>
    internal async Task OpenAsync()
    {
        // Installed before anything can fail: the shell stops this window on the way out, and it can only do
        // that if it knows which window is open.
        _shell?.LatchLanTransfer(this);
        await RestartListenerAsync();
        await LoadHistoryAsync();
        StartTick();
        await CheckFirewallAsync();
    }

    /// <summary>
    /// Starts the one-second tick, if it is not already running.
    /// </summary>
    /// <remarks>
    /// Deliberately not tied to the listener's lifetime, because it is not about the listener: it redraws the
    /// countdown and expires the room's presence leases, and both of those have to keep happening while the
    /// listener is being rebuilt under them. A tick that stopped with the listener would freeze the countdown on
    /// a port change and, worse, leave a phone that had gone away sitting in the room for the rest of the session.
    /// </remarks>
    private void StartTick()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer { Interval = Tick };
        _timer.Tick += (_, _) => { RefreshCode(); SweepPresence(); SweepFinished(); };
        _timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Not awaited: WPF is on the stack here and an await would resume after the window is gone. The shell's
        // shutdown path awaits the very same task, so the listener is still taken down properly on the way out
        // of the process, and a window closed by hand does not make the user wait for a socket.
        _ = ShutdownAsync();
        _shell?.ReleaseLanTransfer(this);
        base.OnClosed(e);
    }

    /// <summary>
    /// Stops the room, the advertiser and the listener, and forgets the transfers that were on screen. Idempotent,
    /// because the window's own close and the application's shutdown both call it and neither may make the other
    /// throw.
    /// </summary>
    internal Task ShutdownAsync()
    {
        lock (_gate)
        {
            if (_teardownStarted) return _teardown;
            _teardownStarted = true;
            _teardown = StopEverythingAsync();
            return _teardown;
        }
    }

    private async Task StopEverythingAsync()
    {
        // A pending question can never be answered once the window is gone, and the peer is owed an answer
        // rather than a hang — so it gets the one that writes nothing.
        foreach (var offer in _awaiting.ToList()) Resolve(offer, accepted: false);
        _awaiting.Clear();

        // The progress list is this window's own drawing of the transfers that were running. It is bound to the
        // screen, so it goes here rather than on the listener's thread, and it goes on the way out because a row
        // that survived would describe a delivery to a peer that has since left.
        _running.Clear();

        // The one place the tick actually stops: this is the window going away, not the listener being rebuilt.
        _timer?.Stop();
        _timer = null;
        // The copy hint's timer is a one-shot about a code that no longer exists once this window is gone.
        _copyHintTimer?.Stop();
        _copyHintTimer = null;

        await StopListenerAsync().ConfigureAwait(false);
        _client?.Dispose();
        _client = null;
    }

    // ------------------------------------------------------------------ listener

    /// <summary>
    /// (Re)builds the whole listening stack from the settings on disk. Called once on open and again whenever
    /// the port or the receive directory changes, because those two are the only choices that cannot be
    /// applied to a running listener.
    /// </summary>
    /// <param name="preferredPort">
    /// The number a person just applied through this window. It outranks the test seam, because a port box that
    /// only took effect on the next launch would leave the screen showing a listener on the number it says is
    /// busy.
    /// </param>
    private async Task RestartListenerAsync(int? preferredPort = null)
    {
        await StopListenerAsync();
        try { _settings = (await new SettingsStore().LoadAsync()).Settings; }
        catch (Exception ex) { Say(Tr.F("Lan.SettingsFailed") + Describe(ex)); }

        _receiveDirectory = _deps.ReceiveDirectory ?? ResolveReceiveDirectory(_settings.LanReceiveDirectory);
        if (_receiveDirectory is null)
        {
            // A listener that cannot receive is worse than no listener: a phone would pair, send, and be told
            // nothing at the other end. So the door stays shut until there is somewhere to put what arrives.
            Say(Tr.F("Lan.NoDirectory"));
            UpdateEmptyHints();
            return;
        }

        var port = preferredPort ?? _deps.Port ?? _settings.LanTransferPort;
        _lifetime = new CancellationTokenSource();
        LanReceiveSink sink;
        try
        {
            _identity ??= await LanDeviceIdentity.LoadOrCreateAsync(ct: _lifetime.Token);
            _peers ??= new LanPeerStore();
            _secrets ??= new LanPeerSecretStore();
            _archive ??= new LanTransferHistoryRepository();
            _pairing ??= new LanPairingState();
            sink = new LanReceiveSink(_receiveDirectory, CloudFileNames.KeepBothName);
            if (!sink.DirectoryIsUsable())
            {
                Say(Tr.F("Lan.DirectoryUnwritable"));
                await StopListenerAsync();
                return;
            }

            _session = new LanSession(new LanOptions(port, _receiveDirectory, _settings.LanAutoAcceptFromTrusted),
                _peers, _secrets, _pairing, AskInboundAsync, _group, _presence);
            _client ??= new LanPeerClient(_identity, _peers);
            // The room's reader is built beside the listener because it is the same pair of promises: this machine
            // answers its own <c>/log</c> here and reads everybody else's from here. It is built *before* the
            // listener because the listener answers on its behalf — a phone cannot dial anybody, so its 接收 is
            // this machine's own receive, and a route that reached back into the window for the room would be a
            // second, later copy of a promise the window already made.
            _room = new LanGroupRoom(_identity, _client, _peers, _group, sink, _presence)
            {
                AutoReceive = _settings.LanAutoAcceptFromTrusted,
            };
            _host = await LanServerHost.StartAsync(
                new LanServerOptions(_session, _identity, _pairing, sink) { Room = _room }, _lifetime.Token);
            AttachRoomEvents(_room);
        }
        catch (LanTransferException ex) when (ex.Refusal == LanRefusal.PortUnavailable)
        {
            // The port is fixed so the firewall rule can name it, which means a clash cannot be dodged by
            // quietly taking another one: the number on screen and the number in the rule have to stay the
            // same pair, so a clash is the user's to resolve.
            await StopListenerAsync();
            Say(Tr.F("Lan.PortBusy"));
            PortBox.Focus();
            PortBox.SelectAll();
            return;
        }
        catch (Exception ex)
        {
            await StopListenerAsync();
            Say(Tr.F("Lan.StartFailed") + Describe(ex));
            return;
        }

        AttachHostEvents(_host);
        PortBox.Text = port.ToString();
        DeviceNameBox.Text = _identity.DeviceName;
        _chosenAddress = _host.ListenAddresses.FirstOrDefault() ?? "";
        AddressList.ItemsSource = _host.ListenAddresses;
        AddressList.SelectedItem = _chosenAddress;
        _restoringToggle = true;
        AutoReceiveBox.IsChecked = _settings.LanAutoAcceptFromTrusted;
        _restoringToggle = false;
        ReceiveDirectoryText.Text = _receiveDirectory;
        await RefreshTrustedSummaryAsync();
        RefreshCode();
        StartDiscovery(port);
        // Started after the screen has its rows, so the first beat's arrivals are painted onto a window that is
        // already showing the room rather than onto one still reading its own settings file.
        _room.Start();
        Say(Tr.Format("Lan.Listening", port));
        UpdateEmptyHints();
    }

    /// <summary>Takes the room, the listener and the advertiser down, leaving the window's own state intact.</summary>
    private async Task StopListenerAsync()
    {
        if (_room is { } room)
        {
            _room = null;
            // Before the host: a poll that is still reading a peer belongs to this window, and the peer it is
            // waiting on has no business holding a listener that is already being torn down.
            try { await room.DisposeAsync(); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError("Unable to stop the LAN room reader: " + ex); }
        }

        if (_discovery is { } discovery)
        {
            _discovery = null;
            await discovery.DisposeAsync();
        }

        if (_host is { } host)
        {
            _host = null;
            // Disposing the listener invalidates the armed code on the way out, so a request already in flight
            // cannot pair into a window that is being torn down.
            try { await host.DisposeAsync(); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError("Unable to stop the LAN listener: " + ex); }
        }

        if (_lifetime is { } lifetime)
        {
            _lifetime = null;
            lifetime.Cancel();
            lifetime.Dispose();
        }
    }

    private void StartDiscovery(int port)
    {
        if (!_deps.DiscoverPeers || _identity is null) return;
        try
        {
            _discovery = LanDiscoveryService.Start(_identity.DeviceId, _identity.DeviceName, port);
            // Two events off one socket, and the difference is what each one is for. Every announcement renews
            // that computer's place in the room, because a machine repeating itself is still a machine that is
            // up; only a changed one is worth putting an address in the manual box for.
            _discovery.PeerSeen += peer => OnUi(() => OnPeerSeen(peer));
            _discovery.PeerFound += peer => OnUi(() => OnPeerFound(peer));
        }
        catch (LanTransferException)
        {
            // The UDP port is the same number as the TCP one and a busy UDP socket does not stop a phone from
            // reaching the listener over TCP, so discovery going quiet is a degraded feature and not a failed
            // start. The manual address box is the answer and it stays on screen either way.
            Say(Tr.F("Lan.DiscoveryUnavailable"));
        }
    }

    private void AttachHostEvents(LanServerHost host)
    {
        host.PairAttempted += args => Dispatcher.BeginInvoke(() => OnPairAttempted(args));
        host.PeerRenamed += args => Dispatcher.BeginInvoke(() => OnPeerRenamed(args));
        host.AdmissionDecided += args => Dispatcher.BeginInvoke(() => OnAdmission(args));
        host.InboxReceived += args => Dispatcher.BeginInvoke(() => OnInbox(args));
        host.DirectoryReceived += args => Dispatcher.BeginInvoke(() => OnDirectory(args));
        host.OutboundDelivered += args => Dispatcher.BeginInvoke(() => OnOutbound(args));
        host.TransferProgress += args => Dispatcher.BeginInvoke(() => OnProgress(args));
    }

    private void AttachRoomEvents(LanGroupRoom room)
    {
        // Both of these are raised from whichever thread was reading the peer's bytes, and the rows they touch
        // belong to the dispatcher.
        room.Progress += args => OnUi(() => OnRoomProgress(args));
        room.Fetched += args => OnUi(() => OnRoomFetched(args));
    }

    // ------------------------------------------------------------------ pairing

    /// <summary>
    /// Re-reads the armed code and repaints only what moved. The state replaces a spent or aged-out code on its
    /// own, so a window left open for ten minutes still pairs a phone without anybody hunting for a button.
    /// </summary>
    private void RefreshCode()
    {
        if (_pairing is null) return;
        var code = _pairing.Arm();
        var remaining = _pairing.RemainingSeconds;
        // A code that just changed makes the last 已复制 a lie: what is sitting in the clipboard is the one the
        // pairing state has already thrown away.
        if (!string.Equals(PairCodeText.Text, code, StringComparison.Ordinal)) HideCopyHint();
        PairCodeText.Text = code;
        PairRemainingText.Text = remaining > 0
            ? Tr.Format("Lan.CodeExpires", remaining)
            : Tr.F("Lan.CodeRenewing");
        var address = _chosenAddress.Length > 0 ? _chosenAddress : _host?.ListenAddresses.FirstOrDefault() ?? "";
        if (address.Length == 0)
        {
            if (PairUrlBox.Text.Length > 0) PairUrlBox.Text = "";
            return;
        }
        // Repainted only when the code or the address moved, and then the box and the picture are written from
        // one string: the typed form of the same thing the picture carries. Scanning is one way in and copying is
        // the other, and whoever is copying needs the URL that pairs — the code rides in the fragment, so opening
        // it is the pairing rather than a page asking for the six characters that were copied along with it.
        if (_paintedCode == code && _paintedAddress == address) return;
        var url = PairingUrl(code, address, remaining);
        if (!string.Equals(PairUrlBox.Text, url, StringComparison.Ordinal)) PairUrlBox.Text = url;
        _paintedCode = code;
        _paintedAddress = address;
        PaintQr(url);
    }

    /// <summary>
    /// The URL this window hands out, which is the same one the picture carries rather than a bare address.
    /// </summary>
    /// <remarks>
    /// The code belongs in it: copying this line onto another computer is the act a scanner performs, and a page
    /// that then asked for six characters it had just been handed would only be asking for a typo. The fragment
    /// is also what keeps the code out of any server's access log, which is the reason the picture already put it
    /// there. Falls back to the plain address when this machine's identity cannot be printed, because a URL that
    /// still reaches the page — and can have the code typed into it — is worth more than an empty box.
    /// </remarks>
    private string PairingUrl(string code, string address, int remainingSeconds)
    {
        var plain = LanPairQr.BuildUrl(IpOf(address), PortOf(address));
        if (_identity is null) return plain;
        try
        {
            return LanPairQr.BuildPayload(LanPairQr.For(_identity, IpOf(address), PortOf(address), code,
                DateTimeOffset.UtcNow, remainingSeconds));
        }
        catch (Exception)
        {
            return plain;
        }
    }

    private void PaintQr(string payload)
    {
        if (_identity is null) return;
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            // The bytes go straight into the bitmap and are never written out: a pairing QR is a secret with a
            // two-minute shelf life, and a stray .png in a temp folder is the one thing this must not leave.
            using (var stream = new MemoryStream(LanPairQr.Png(payload)))
            {
                // BitmapImage is an ISupportInitialize: the decoder only runs at EndInit, and it refuses the
                // call outright when no BeginInit opened the batch.
                image.BeginInit();
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
            QrImage.Source = image;
            // The picture and the box say the same thing, down to the code: what a person reads aloud, and what
            // a browser does when it opens the line, is the one URL this window is handing out.
            QrImage.ToolTip = payload;
        }
        catch (Exception ex)
        {
            // A QR this machine cannot draw must not take the window with it: the six characters below the
            // picture are the same pairing path, typed.
            QrImage.Source = null;
            Say(Tr.F("Lan.QrFailed") + Describe(ex));
        }
    }

    /// <summary>
    /// The six characters, straight to the clipboard. A pairing code is short enough to retype and exactly the
    /// kind of short string a person mistypes, so the screen offers the copy instead of the transcription.
    /// </summary>
    private void CopyPairCode_Click(object sender, RoutedEventArgs e)
    {
        var code = PairCodeText.Text;
        if (string.IsNullOrEmpty(code)) return;
        try { (CopyToClipboard ?? Clipboard.SetText)(code); }
        catch (Exception ex)
        {
            // The clipboard is a machine-wide resource somebody else can be holding, and failing to take it is
            // not a reason to lose the code — it is still on screen, which is how it was being read out anyway.
            Say(Tr.F("Lan.CopyCodeFailed") + Describe(ex));
            return;
        }
        ShowCopyHint();
    }

    private void ShowCopyHint()
    {
        if (_copyHintTimer is null)
        {
            _copyHintTimer = new DispatcherTimer { Interval = CopyHintLinger };
            _copyHintTimer.Tick += (_, _) => HideCopyHint();
        }
        PairCopyHintText.Text = Tr.F("Lan.CodeCopied");
        // Restarted rather than merely started: a second click while the first hint is up buys the full two
        // seconds again instead of the remainder of the first one.
        _copyHintTimer.Stop();
        _copyHintTimer.Start();
    }

    private void HideCopyHint()
    {
        _copyHintTimer?.Stop();
        PairCopyHintText.Text = Tr.F("Lan.CopyCode");
    }

    private static string IpOf(string addressPort)
    {
        var split = addressPort.LastIndexOf(':');
        return split > 0 ? addressPort[..split] : addressPort;
    }

    private int PortOf(string addressPort)
    {
        var split = addressPort.LastIndexOf(':');
        return split > 0 && int.TryParse(addressPort[(split + 1)..], out var port) ? port : CurrentPort();
    }

    private int CurrentPort() => _host?.Port ?? _settings.LanTransferPort;

    private void AddressList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // With several adapters — a VPN, a virtual switch — the address the QR prints is a choice and not a
        // fact, so the list is selectable and the picture follows it.
        if (AddressList.SelectedItem is not string address) return;
        _chosenAddress = address;
        _paintedCode = null;
        RefreshCode();
    }

    private void Regenerate_Click(object sender, RoutedEventArgs e)
    {
        if (_pairing is null) return;
        _pairing.Invalidate();
        var code = _pairing.Arm();
        _paintedCode = null;
        RefreshCode();
        Say(Tr.Format("Lan.CodeRegenerated", code));
    }

    private async void Firewall_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            var outcome = await LanFirewall.EnsureAsync(CurrentPort(), _deps.Netsh);
            FirewallStatusText.Text = LanPeerPresentation.Firewall(outcome);
            Say(FirewallStatusText.Text);
        }
        catch (Exception ex)
        {
            FirewallStatusText.Text = Tr.F("Lan.Firewall.FailedUnknown") + Describe(ex);
        }
        finally { button.IsEnabled = true; }
    }

    private async Task CheckFirewallAsync()
    {
        try
        {
            var open = await LanFirewall.InspectAsync(CurrentPort(), _deps.Netsh);
            // The read-only check never elevates, so this runs on every open; a machine that already has the
            // rule is told so and is never asked for anything.
            FirewallStatusText.Text = open ? Tr.F("Lan.Firewall.Allowed") : Tr.F("Lan.Firewall.Missing");
        }
        catch (Exception ex) { FirewallStatusText.Text = Tr.F("Lan.Firewall.FailedUnknown") + Describe(ex); }
    }

    private void OnPairAttempted(LanPairAttemptEventArgs args)
    {
        var who = Who(args.Contact);
        // A device that just proved it could read the code is in the room from now on, and the room is where a
        // person is already looking — so the joining is a line on the stream rather than a status sentence the
        // next tick overwrites.
        if (args.Accepted) _group.AppendSystem(Tr.Format("Lan.JoinedRoom", who), DateTimeOffset.UtcNow);
        else Say(Tr.Format("Lan.PairRefused", who, LanPeerPresentation.Refusal(args.Refusal)));
        _ = RefreshTrustedSummaryAsync();
    }

    private void OnPeerRenamed(LanPeerRenamedEventArgs args)
    {
        var was = string.IsNullOrWhiteSpace(args.PreviousName) ? Tr.F("Lan.KindUnknown") : args.PreviousName;
        _group.AppendSystem(Tr.Format("Lan.RenamedPeer", was, args.Contact.DeviceName), DateTimeOffset.UtcNow);
        _ = RefreshTrustedSummaryAsync();
    }

    // ------------------------------------------------------------------ who is in the room

    /// <summary>
    /// Adds or refreshes one chip in the room's device strip.
    /// </summary>
    /// <remarks>
    /// Keyed on the device id rather than appended, because a device that moved between access points is the
    /// same device at a different address and a list that grew a second chip for it would read as two phones.
    /// </remarks>
    private void ShowMember(LanRoomMember member)
    {
        var row = new LanMemberRow(MemberName(member), MemberDetail(member), member);
        var index = _members.ToList().FindIndex(candidate =>
            string.Equals(candidate.Member.DeviceId, member.DeviceId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) _members.Add(row);
        else _members[index] = row;
        UpdateEmptyHints();
    }

    /// <summary>
    /// Takes a chip away because that device is no longer here.
    /// </summary>
    /// <remarks>
    /// A device that was only ever heard on the subnet leaves silently: it never joined anything, so saying it
    /// left would be narrating a membership it does not have. A paired one gets a line, because its departure is
    /// the whole of what 退群 means and a chip that vanished with no explanation is a list that lied.
    /// </remarks>
    private void DropMember(LanRoomMember member)
    {
        var index = _members.ToList().FindIndex(candidate =>
            string.Equals(candidate.Member.DeviceId, member.DeviceId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _members.RemoveAt(index);
        if (!member.Discovered)
            _group.AppendSystem(Tr.Format("Lan.LeftRoom", MemberName(member)), DateTimeOffset.UtcNow);
        UpdateEmptyHints();
    }

    /// <summary>Expires the leases that ran out, which is how a phone whose browser was closed actually leaves.</summary>
    private void SweepPresence() => _presence.Sweep(DateTimeOffset.UtcNow);

    private string MemberName(LanRoomMember member) => string.IsNullOrWhiteSpace(member.DeviceName)
        ? Tr.F("Lan.KindUnknown")
        : member.DeviceName;

    /// <summary>What the chip says under the name: what kind of thing it is, and where it is.</summary>
    private static string MemberDetail(LanRoomMember member) => Tr.Format("Lan.MemberChip",
        member.Discovered ? Tr.F("Lan.MemberDiscovered") : LanPeerPresentation.Kind(member.Kind), member.Where);

    // ------------------------------------------------------------------ the room on screen

    /// <summary>
    /// Paints one entry of this machine's copy of the room.
    /// </summary>
    /// <remarks>
    /// Keyed on the message id rather than appended blindly, because one sentence can genuinely be heard twice
    /// in one window — the composer wrote it into this copy and a member's poll then carried the same id back —
    /// and a room that shows a person their own words twice reads as damage.
    /// </remarks>
    private void ShowEntry(LanGroupMessage entry)
    {
        if (_rows.ContainsKey(entry.MessageId)) return;
        var row = new LanChatRow(entry, IsMine(entry));
        if (entry.Kind != LanGroupKind.Text) row.SetDetail(OfferLine(entry));
        _rows[entry.MessageId] = row;
        _chat.Add(row);
        FollowNewest();
        UpdateEmptyHints();
    }

    /// <summary>
    /// Drops the row of an entry that aged out of this copy. The file it pointed at is left exactly where it is:
    /// a published copy belongs to the user, and this log never had a claim on it.
    /// </summary>
    private void ForgetEntry(LanGroupMessage entry)
    {
        if (!_rows.Remove(entry.MessageId, out var row)) return;
        _chat.Remove(row);
        UpdateEmptyHints();
    }

    private bool IsMine(LanGroupMessage entry) => _identity is { } self
        && string.Equals(entry.AuthorDeviceId, self.DeviceId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The line under a file's bubble: where its bytes are, or what the room promised about them.</summary>
    private static string OfferLine(LanGroupMessage entry) => entry.HeldHere ? HeldLine(entry) : SizeLine(entry);

    private static string HeldLine(LanGroupMessage entry) =>
        Tr.Format("Lan.ChatHeld", Path.GetFileName(entry.LocalPath ?? entry.FileName ?? ""));

    private static string SizeLine(LanGroupMessage entry) => entry.IsDirectory
        ? Tr.F("Lan.ChatFolder")
        : ByteSize.Format(entry.Bytes);

    private void Receive_Click(object sender, RoutedEventArgs e)
    {
        if (_room is not { } room || sender is not Button { Tag: LanChatRow row }) return;
        _ = PullAsync(row, room);
    }

    /// <summary>
    /// Asks the room for one entry's bytes. Everything the answer decides is painted by the room's own
    /// <c>Fetched</c> event, because an auto-received file goes through this same call from the poll thread and
    /// the two must not grow two versions of the same sentence.
    /// </summary>
    private async Task PullAsync(LanChatRow row, LanGroupRoom room)
    {
        try { await room.ReceiveAsync(row.MessageId, _lifetime?.Token ?? CancellationToken.None); }
        // A pull cut short by this window closing has nothing left to report to.
        catch (Exception ex) when (ex is LanTransferException or HttpRequestException or JsonException
                or IOException or OperationCanceledException)
        {
            row.SetDetail(Tr.F("Lan.Refuse.IncompleteBody"));
        }
    }

    private void OnRoomProgress(LanRoomProgressEventArgs args)
    {
        // A pull reports against the length the entry announced; a folder's archive announces no length at all, so
        // its row draws a bar in flight rather than a percentage nobody measured.
        if (!_rows.TryGetValue(args.Entry.MessageId, out var row)) return;
        // The last tick of a pull is the board saying it is over, and the bubble's own line about where the bytes
        // landed arrives on a different event — so the bar is taken down here rather than waiting for it.
        if (args.IsDone)
        {
            row.SetProgress(args.Bytes, args.TotalBytes);
            row.ClearProgress();
            return;
        }

        row.SetProgress(args.Bytes, args.TotalBytes, SpeedLine(args.SpeedBytesPerSecond, args.Bytes, args.TotalBytes));
    }

    private void OnRoomFetched(LanRoomFetchEventArgs args)
    {
        if (args.Entry is not { } entry) return;
        var row = _rows.TryGetValue(entry.MessageId, out var found) ? found : null;
        if (args.Refusal == LanRefusal.None && args.LandedPath is { Length: > 0 } path)
        {
            row?.SetDetail(HeldLine(entry));
            Say(Tr.Format("Lan.RoomReceived", Path.GetFileName(path),
                entry.IsDirectory ? Tr.F("Lan.ChatFolder") : ByteSize.Format(entry.Bytes)));
            _ = ArchiveAsync(new LanTransferHistoryEntry(Guid.NewGuid(), LanTransferDirection.Received,
                entry.AuthorDeviceId, entry.AuthorName, path, entry.Bytes,
                LanTransferState.Completed, DateTimeOffset.UtcNow));
            return;
        }

        var reason = LanPeerPresentation.Refusal(args.Refusal);
        // The 接收 button stays, because the entry is still the room's promise and a member that is asleep tonight
        // may answer it tomorrow; only the reason goes under it.
        row?.SetDetail(reason);
        Say(Tr.Format("Lan.RoomRefused", entry.FileName ?? "", reason));
    }

    private void FollowNewest()
    {
        // A chat is read from the bottom, so the newest line is the one worth showing. Asked of a list that has
        // never been laid out there is no scroll host to answer, which is why this waits for the window to be
        // loaded rather than running on every entry a background test ever paints.
        if (ChatList.IsLoaded && ChatList.Items.Count > 0) ChatList.ScrollIntoView(ChatList.Items[^1]);
    }

    // ------------------------------------------------------------------ speaking

    private void Send_Click(object sender, RoutedEventArgs e) => _ = SendFromComposerAsync();

    private void Message_KeyDown(object sender, KeyEventArgs e)
    {
        // 回车发送、Shift+回车换行. The box accepts both because a sentence with a line break in it is a normal
        // thing to say, and which of the two a person means when they press Enter is not in doubt.
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) return;
        e.Handled = true;
        _ = SendFromComposerAsync();
    }

    private async Task SendFromComposerAsync()
    {
        var text = ChatInputBox.Text;
        var refusal = await SayAsync(text);
        if (refusal == LanRefusal.None) ChatInputBox.Clear();
        else Say(LanPeerPresentation.Refusal(refusal));
    }

    /// <summary>
    /// Says one sentence in the room: it goes into this machine's copy at once, and every member that can be
    /// dialled is told about it.
    /// </summary>
    /// <returns>
    /// What the local append decided, which is the only answer worth showing: <see cref="LanRefusal.BadRequest"/>
    /// for an empty box and <see cref="LanRefusal.MessageTooLong"/> for a sentence longer than the room holds.
    /// </returns>
    /// <remarks>
    /// The id is minted here, before anything is sent, because the same sentence reaches a member by two paths —
    /// this push and that member's own poll of somebody else who heard it — and two ids for one sentence would
    /// converge as two rows. The push is not the propagation mechanism: every member is polled every few
    /// seconds anyway. It is there for the machine the firewall still has shut, which can dial out but cannot be
    /// read, and would otherwise never get its own sentences any further than its own copy.
    /// </remarks>
    internal async Task<LanRefusal> SayAsync(string? text)
    {
        if (_identity is null) return LanRefusal.BadRequest;
        var id = Guid.NewGuid().ToString("N");
        var said = (text ?? "").Trim();
        var refusal = _group.TryAppendText(SelfContact(), text, DateTimeOffset.UtcNow, out _, id);
        if (refusal != LanRefusal.None) return refusal;

        await FanOutAsync(async member =>
        {
            if (_client is null) return;
            await _client.SayAsync(member, said, id, _lifetime?.Token ?? CancellationToken.None);
        });
        return LanRefusal.None;
    }

    /// <summary>
    /// Puts a file or a folder the user chose into the room: this machine announces it and holds its bytes, and
    /// every other member learns of it by reading this copy.
    /// </summary>
    /// <returns>How many rows the room gained, so a pick of five vanished files can be told apart from a pick of five.</returns>
    /// <remarks>
    /// <para>
    /// No byte leaves here. The entry lands in this machine's log already marked as held, a member's poll carries
    /// it outward, and whoever wants it asks — which is why sending into a room of five costs one row rather than
    /// five uploads, and why a machine that was switched off when the file was announced can still take it
    /// tomorrow.
    /// </para>
    /// </remarks>
    internal int Announce(IEnumerable<string?> paths)
    {
        if (_identity is null) return 0;
        var self = SelfContact();
        var now = DateTimeOffset.UtcNow;
        var announced = 0;
        foreach (var candidate in paths)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            LanSendItem item;
            try { item = LanSendItem.Of(candidate); }
            // A path that is gone, or that this machine may not read, is skipped rather than announced: an entry
            // with nobody holding it is a 接收 button that can only ever fail.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { continue; }

            _group.Hold(self, Guid.NewGuid().ToString("N"), item.Name, item.Bytes, item.IsDirectory,
                item.AbsolutePath, now);
            announced++;
        }

        Say(announced > 0 ? Tr.Format("Lan.Announced", announced) : Tr.F("Lan.AnnouncedNothing"));
        return announced;
    }

    private LanContact SelfContact() => new(_identity!.DeviceId, _identity.DeviceName, _identity.Kind, "", CurrentPort());

    /// <summary>
    /// Hands one finished sentence to every member this machine can dial, at the same time.
    /// </summary>
    /// <remarks>
    /// Concurrently because the wait is a dead member's own timeout, and a room of five must not pay it five
    /// times before the composer's box clears.
    /// </remarks>
    private async Task FanOutAsync(Func<LanPeerEndpoint, Task> tell)
    {
        if (_client is null || _room is null) return;
        IReadOnlyList<LanPeerEndpoint> members;
        try { members = await _room.ReachableMembersAsync(_lifetime?.Token ?? CancellationToken.None); }
        catch (Exception ex) when (ConvergesAnyway(ex)) { return; }
        catch (Exception ex) { Say(Tr.F("Lan.PeerListFailed") + Describe(ex)); return; }

        try
        {
            await Task.WhenAll([.. members.Select(async member =>
            {
                try { await tell(member); }
                catch (Exception ex) when (ConvergesAnyway(ex)) { }
            })]);
        }
        catch (Exception ex) when (ConvergesAnyway(ex)) { }
    }

    /// <summary>
    /// Whether a member that did not answer is the room's problem rather than this window's. It is: the sentence
    /// is already in this machine's copy of the log, and that member's own poll picks it up from whoever else in
    /// the room heard it. A cancellation belongs in the same list, because a window that is closing has nobody
    /// left to report to.
    /// </summary>
    private static bool ConvergesAnyway(Exception ex) =>
        ex is LanTransferException or HttpRequestException or JsonException or OperationCanceledException;

    // ------------------------------------------------------------------ receiving

    /// <summary>
    /// The one gate a stranger's first write passes through: a row on screen and a person's click. It never
    /// times out into a yes, because bytes landing in the user's Downloads folder is the one effect here that
    /// closing a window cannot undo.
    /// </summary>
    /// <remarks>
    /// Called on a request thread, so only the row goes to the dispatcher and the caller waits on the task
    /// rather than on the screen. A window that is gone before anybody reads the question answers no: the safe
    /// direction for a mistake here is a file that did not arrive, not one that did.
    /// </remarks>
    private Task<bool> AskInboundAsync(LanInboundOffer offer, CancellationToken ct)
    {
        if (_deps.ConfirmInbound is { } seam) return seam(offer, ct);
        if (ct.IsCancellationRequested || _teardownStarted) return Task.FromResult(false);

        var pending = new LanPendingOffer(offer);
        // Registered before the row is even queued, because this is the list the teardown answers from: a
        // question the window raised and then lost is one the peer is still waiting on, and it deserves the
        // answer that writes nothing rather than a request hanging until the other side gives up.
        lock (_gate) _awaiting.Add(pending);
        Dispatcher.BeginInvoke(() =>
        {
            // Re-checked here: the peer can give up between the question being raised and this window painting
            // it, and a row nobody can any longer answer would sit on screen until the window closed.
            if (ct.IsCancellationRequested || pending.Answer.Task.IsCompleted) return;
            pending.GiveUp = ct.Register(() => Dispatcher.BeginInvoke(() => Withdraw(pending)));
            pending.Row = new LanRow(Tr.Format("Lan.PendingRow", Who(offer.Contact)), DescribeOffer(offer), pending);
            _pending.Add(pending.Row);
            PendingList.SelectedItem = pending.Row;
            UpdateEmptyHints();
            Say(Tr.Format("Lan.AskingFirstWrite", Who(offer.Contact)));
        });
        return pending.Answer.Task;
    }

    private void Withdraw(LanPendingOffer pending) => Resolve(pending, accepted: false);

    private void AcceptPending_Click(object sender, RoutedEventArgs e) => AnswerPending(true);

    private void DeclinePending_Click(object sender, RoutedEventArgs e) => AnswerPending(false);

    /// <summary>
    /// Answers the selected question, or the oldest one when nothing is selected — the common case is exactly
    /// one row waiting, and a phone should not have to be pointed at before a person can say yes to it.
    /// </summary>
    private void AnswerPending(bool accepted)
    {
        var pending = (PendingList.SelectedItem as LanRow)?.Tag as LanPendingOffer
            ?? _pending.FirstOrDefault()?.Tag as LanPendingOffer;
        if (pending is null)
        {
            Say(Tr.F("Lan.NothingToAnswer"));
            return;
        }

        Resolve(pending, accepted);
        if (!accepted) Say(Tr.F("Lan.DeclinedThatFile"));
    }

    private void Resolve(LanPendingOffer pending, bool accepted)
    {
        lock (_gate) _awaiting.Remove(pending);
        if (pending.Row is { } row && _pending.Contains(row)) _pending.Remove(row);
        pending.GiveUp.Dispose();
        // TrySetResult rather than SetResult: the peer giving up and a person clicking are two threads that
        // can genuinely land in the same moment, and the first answer is the one that was already honoured.
        pending.Answer.TrySetResult(accepted);
        UpdateEmptyHints();
    }

    private void OnAdmission(LanAdmissionEventArgs args)
    {
        if (args.Accepted) return;
        var who = Who(args.Offer.Contact);
        var reason = LanPeerPresentation.Refusal(args.Decision);
        Say(Tr.Format("Lan.RefusedWrite", who, reason));
        var state = args.Decision == LanRefusal.Declined ? LanTransferState.Declined : LanTransferState.Failed;
        _ = ArchiveAsync(new LanTransferHistoryEntry(Guid.NewGuid(), LanTransferDirection.Received,
            args.Offer.Contact.DeviceId, args.Offer.Contact.DeviceName, args.Offer.Path, args.Offer.DeclaredBytes,
            state, DateTimeOffset.UtcNow, reason));
        // The request was refused before a byte moved, so no progress event ever reached a row: closing here is
        // what turns a refused offer from a bar stuck at zero into a line that says it was refused.
        CloseInbound(args.Offer.Contact.DeviceId, args.Offer.Path, state, reason);
    }

    private void OnInbox(LanInboxEventArgs args)
    {
        var stored = Path.GetFileName(args.Received.RelativePath);
        var line = args.Received.Renamed
            ? Tr.Format("Lan.ReceivedRenamed", stored, ByteSize.Format(args.Received.Bytes), Who(args.Contact))
            : Tr.Format("Lan.Received", stored, ByteSize.Format(args.Received.Bytes), Who(args.Contact));
        Say(line);
        _ = ArchiveAsync(new LanTransferHistoryEntry(Guid.NewGuid(), LanTransferDirection.Received,
            args.Contact.DeviceId, args.Contact.DeviceName, args.Received.RelativePath, args.Received.Bytes,
            LanTransferState.Completed, DateTimeOffset.UtcNow));
        CloseInbound(args.Contact.DeviceId, args.Received.RelativePath, LanTransferState.Completed, "");
    }

    private void OnDirectory(LanDirectoryEventArgs args) =>
        Say(Tr.Format("Lan.ReceivedFolder", args.RelativePath, Who(args.Contact)));

    private void OnOutbound(LanOutboundEventArgs args)
    {
        Say(args.IsArchive
            ? Tr.Format("Lan.SentArchive", args.Item.Name, Who(args.Contact))
            : Tr.Format("Lan.Sent", args.Item.Name, ByteSize.Format(args.Item.Bytes), Who(args.Contact)));
        _ = ArchiveAsync(new LanTransferHistoryEntry(Guid.NewGuid(), LanTransferDirection.Sent,
            args.Contact.DeviceId, args.Contact.DeviceName, args.Item.Name, args.Item.Bytes,
            LanTransferState.Completed, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// One tick of a transfer, from whichever of the four paths is moving the bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row is keyed by the board's own id when there is one. A path alone cannot tell two files of the same
    /// name apart, and a room can hold both at once — the id is what the board measured, so it is what this list
    /// groups by. Inbound keeps the older <see cref="InKey"/> shape because <see cref="CloseInbound"/> finds the
    /// row a landed file belongs to through it, and the board's inbound id is that same pair with a prefix.
    /// </para>
    /// <para>
    /// A finished tick ends the row rather than leaving it: the sentence 「已收到 X」 is already in the room, and a
    /// list that only ever grows would turn every session into a scroll of dead bars.
    /// </para>
    /// </remarks>
    private void OnProgress(LanProgressEventArgs args)
    {
        var key = args.IsInbound
            ? InKey(args.Contact.DeviceId, args.Path)
            : string.IsNullOrEmpty(args.TransferId) ? InKey(args.Contact.DeviceId, args.Path) : args.TransferId;
        var hasTotal = args.TotalBytes > 0;
        var line = ProgressLine(args.Path, args.Contact, args.Bytes, args.TotalBytes, args.IsInbound);

        if (args.IsDone)
        {
            // The row stays in the map until the sweep takes it: the sentence about where the bytes actually
            // landed arrives on a different event, and it is that sentence which turns a finished row into a
            // useful one. Removing it here would leave only the size the sender claimed.
            if (_running.TryGetValue(key, out var finished))
                finished.Finish(args.Error is null ? LanTransferState.Completed : LanTransferState.Failed,
                    args.Error ?? "");
            return;
        }

        if (!_running.TryGetValue(key, out var row) || row.FinishedAt is not null)
        {
            // A key already holding a finished row is a second transfer of the same name, not a continuation of
            // the one that is over: the old row keeps its own place in the list and this one starts from zero.
            row = new LanProgressRow(line, hasTotal);
            _running[key] = row;
            _transfers.Add(row);
            UpdateEmptyHints();
        }

        row.Update(line, hasTotal ? 100d * args.Bytes / args.TotalBytes : -1,
            SpeedLine(args.SpeedBytesPerSecond, args.Bytes, args.TotalBytes));
    }

    private string ProgressLine(string path, LanContact contact, long bytes, long totalBytes, bool inbound) =>
        inbound
            ? Tr.Format("Lan.ProgressIn", Path.GetFileName(path), Who(contact), ByteSize.Format(bytes),
                totalBytes > 0 ? ByteSize.Format(totalBytes) : Tr.F("Lan.SizeUnknown"))
            : Tr.Format("Lan.ProgressOut", Path.GetFileName(path), Who(contact), ByteSize.Format(bytes),
                totalBytes > 0 ? ByteSize.Format(totalBytes) : Tr.F("Lan.SizeUnknown"));

    /// <summary>
    /// The speed and the time left, as one sentence, or nothing at all.
    /// </summary>
    /// <remarks>
    /// Silence is the honest answer for the first quarter second of a transfer, when the board has one sample and
    /// nothing to divide — a row reading "0 B/s" at that moment looks stuck rather than new. The same silence
    /// covers a folder, whose size nobody declared: a rate without a total is a speed with no arrival in it, and
    /// the line above already says what is moving.
    /// </remarks>
    internal static string SpeedLine(double bytesPerSecond, long bytes, long totalBytes)
    {
        var rate = LanTransferSpeed.Format(bytesPerSecond);
        if (rate.Length == 0) return "";

        var seconds = totalBytes > bytes && bytesPerSecond > 0
            ? (long)Math.Ceiling((totalBytes - bytes) / bytesPerSecond)
            : 0;
        return seconds <= 0 ? rate : Tr.Format("Lan.SpeedEta", rate, EtaText(seconds));
    }

    private static string EtaText(long seconds) => seconds switch
    {
        < 60 => Tr.Format("Lan.EtaSeconds", seconds),
        < 3600 => Tr.Format("Lan.EtaMinutes", seconds / 60, seconds % 60),
        _ => Tr.Format("Lan.EtaHours", seconds / 3600, seconds % 3600 / 60),
    };

    /// <summary>
    /// Takes finished rows off the list once they have been readable for a moment.
    /// </summary>
    /// <remarks>
    /// Runs on the tick the window already has. The pause is the same idea as the board's own: a bar that vanishes
    /// on the same frame it fills gives nobody the chance to see that it finished, and the row is the only place
    /// the last speed of a transfer was ever written.
    /// </remarks>
    private void SweepFinished()
    {
        if (_transfers.Count == 0) return;
        var now = Environment.TickCount64;
        var gone = _transfers.Where(row => row.FinishedAt is { } at && now - at >= FinishedLingerMs).ToList();
        if (gone.Count == 0) return;

        foreach (var row in gone) _transfers.Remove(row);
        foreach (var key in _running.Where(pair => pair.Value.FinishedAt is not null).Select(pair => pair.Key).ToList())
            _running.Remove(key);
        UpdateEmptyHints();
    }

    /// <summary>
    /// Marks an inbound row done. The exact wire path is the key, and a landed file is stored under a name this
    /// application may have chosen — so when that lookup misses, the oldest still-running row for that device is
    /// the one that just landed. Only a rename can make it ambiguous, and the history rows come from the events
    /// themselves rather than from this list, so a display that finishes one file early never mis-archives one.
    /// </summary>
    private void CloseInbound(string deviceId, string path, LanTransferState state, string detail)
    {
        if (_running.Remove(InKey(deviceId, path), out var exact))
        {
            exact.Finish(state, detail);
            return;
        }

        var prefix = deviceId + "|";
        var oldest = _running.FirstOrDefault(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (oldest.Key is null) return;
        _running.Remove(oldest.Key);
        oldest.Value.Finish(state, detail);
    }

    private static string InKey(string deviceId, string path) => $"{deviceId}|{path}";

    // ------------------------------------------------------------------ this computer

    private void Window_Drop(object sender, DragEventArgs e) => AcceptDrop(e);

    private void AcceptDrop(DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;
        Announce(dropped);
    }

    private void Window_DragOver(object sender, DragEventArgs e) =>
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = Tr.F("Lan.SendFiles") };
        if (dialog.ShowDialog(this) == true) Announce(dialog.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Tr.F("Lan.SendFolder"), Multiselect = true };
        if (dialog.ShowDialog(this) == true) Announce(dialog.FolderNames);
    }

    private async void SaveDeviceName_Click(object sender, RoutedEventArgs e) => await RenameSelfAsync(DeviceNameBox.Text);

    /// <summary>
    /// Renames this machine, then tells the room.
    /// </summary>
    /// <returns>The name the identity file really holds, which is what every other member now lists.</returns>
    /// <remarks>
    /// The order is the whole point of returning a name. A typed one can be longer than a row holds or blank, and
    /// a window that kept showing what the person typed while the rest of the room listed what it stored would
    /// be describing a rename nobody made. The room is told after the file is written for the same reason: a peer
    /// that answers the new name must be able to find it on disk afterwards.
    /// </remarks>
    internal async Task<string> RenameSelfAsync(string? name)
    {
        if (_identity is null) return "";
        var was = _identity.DeviceName;
        string kept;
        try { kept = await _identity.RenameAsync(name, _lifetime?.Token ?? CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say(Tr.F("Lan.RenameFailed") + Describe(ex));
            return "";
        }
        catch (OperationCanceledException) { return ""; }

        DeviceNameBox.Text = kept;
        // A name that did not move is not a rename, and the room must not be told it was. This is also what a
        // blank box does: the identity answers with the name it already holds, and the person gets their name
        // back on the screen without a second line saying so.
        if (string.Equals(kept, was, StringComparison.Ordinal)) return kept;

        await FanOutAsync(async member =>
        {
            if (_client is null) return;
            await _client.TellNameAsync(member, kept, _lifetime?.Token ?? CancellationToken.None);
        });
        // The advertiser carries it too, so a computer that has not paired yet sees this one under the name it
        // chose rather than the one Windows gave it.
        _discovery?.AnnounceAs(kept);
        // The room is the one place this is said. A status line would only repeat it, and the status line is the
        // window's only space for reporting a failure.
        _group.AppendSystem(Tr.Format("Lan.RenamedSelf", kept), DateTimeOffset.UtcNow);
        return kept;
    }

    private async void CheckPeer_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPeerTarget(out var address, out var port)) return;
        var hello = await HelloAsync(address, port);
        PeerStatusText.Text = hello is null
            ? Tr.F("Lan.PeerNoAnswer")
            : Tr.Format("Lan.PeerAnswers", hello.DeviceName, $"{address}:{port}", LanPeerPresentation.Kind(hello.Kind));
        if (hello is not null) NoteDiscovered(new LanDiscoveredPeer(hello.DeviceId, hello.DeviceName, address, port));
    }

    private async void PairPeer_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || !TryPeerTarget(out var address, out var port)) return;
        var hello = await HelloAsync(address, port);
        if (hello is null)
        {
            PeerStatusText.Text = Tr.F("Lan.PeerNoAnswer");
            return;
        }

        try
        {
            await _client.PairAsync(new LanPeerEndpoint(hello.DeviceId, address, port), PeerCodeBox.Text);
            PeerStatusText.Text = Tr.Format("Lan.PeerPaired", hello.DeviceName);
            PeerCodeBox.Clear();
            NoteDiscovered(new LanDiscoveredPeer(hello.DeviceId, hello.DeviceName, address, port));
        }
        catch (LanTransferException ex) { PeerStatusText.Text = LanPeerPresentation.Refusal(ex.Refusal); }
        catch (Exception ex) { PeerStatusText.Text = Tr.F("Lan.PeerNoAnswer") + Describe(ex); }
    }

    /// <summary>
    /// A peer address typed, or the last computer that announced itself. Kept as a parse rather than a guess: the
    /// manual box is the one path that works when discovery cannot, so an unparseable address has to be named
    /// rather than sent.
    /// </summary>
    private bool TryPeerTarget(out string address, out int port)
    {
        port = CurrentPort();
        var typed = (PeerAddressBox.Text ?? "").Trim();
        if (typed.Length == 0 && _found.Count > 0)
        {
            var found = _found[^1];
            address = found.Address;
            port = found.Port;
            return true;
        }

        var candidate = typed.Length == 0 ? "" : typed.Contains(':') ? typed : $"{typed}:{port}";
        var parts = candidate.Split(':');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var parsed)
            || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out port) || port is < LanOptions.MinimumPort or > LanOptions.MaximumPort)
        {
            address = "";
            port = CurrentPort();
            PeerStatusText.Text = Tr.F("Lan.PeerAddressInvalid");
            return false;
        }

        address = parts[0];
        return true;
    }

    private Task<LanHelloReply?> HelloAsync(string address, int port) =>
        _client?.HelloAsync(address, port) ?? Task.FromResult<LanHelloReply?>(null);

    /// <summary>
    /// Every announcement, whether or not it says anything new.
    /// </summary>
    /// <remarks>
    /// A computer repeating its own name every two seconds is the only heartbeat this side ever gets from a
    /// machine it has not paired with, and it is what keeps a discovered computer in the room's device list
    /// instead of letting it flicker out between two beats.
    /// </remarks>
    private void OnPeerSeen(LanDiscoveredPeer peer) => _presence.Seen(new LanRoomMember(peer.DeviceId, peer.DeviceName,
        LanProtocol.KindUnknown, peer.Address, peer.Port, DateTimeOffset.UtcNow, Discovered: true));

    /// <summary>A computer that just said something new — a first sighting, a rename, or a new address.</summary>
    private void OnPeerFound(LanDiscoveredPeer peer) => NoteDiscovered(peer);

    /// <summary>
    /// Keeps the newest address of a computer this window has heard of, and offers it in the manual box.
    /// </summary>
    private void NoteDiscovered(LanDiscoveredPeer peer)
    {
        var index = _found.FindIndex(candidate => string.Equals(candidate.DeviceId, peer.DeviceId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) _found.Add(peer);
        else _found[index] = peer;
        // The manual box follows the announcement rather than the other way round: a person who typed an
        // address and then saw the same machine announce itself should not lose what they wrote.
        if (string.IsNullOrWhiteSpace(PeerAddressBox.Text)) PeerAddressBox.Text = $"{peer.Address}:{peer.Port}";
    }

    // ------------------------------------------------------------------ device list & directory

    /// <summary>
    /// How many devices this machine would let back in without a fresh code.
    /// </summary>
    /// <remarks>
    /// A count and not a list, because the list is a file on disk that outlives this window and the room's own
    /// device strip is about who is here now. The two are different questions and showing both in one column
    /// made them read as one — so the list has its own window and this is only the way in.
    /// </remarks>
    private async Task RefreshTrustedSummaryAsync()
    {
        if (_peers is null) return;
        try
        {
            var records = await _peers.LoadAsync();
            TrustedCountText.Text = records.Count == 0
                ? Tr.F("Lan.NoTrustedDevices")
                : Tr.Format("Lan.TrustedCount", records.Count);
        }
        catch (Exception ex) { Say(Tr.F("Lan.PeerListFailed") + Describe(ex)); }
    }

    private void ManageTrusted_Click(object sender, RoutedEventArgs e)
    {
        if (_peers is null) return;
        // Modal, and owned by this window: forgetting a device is a decision about the file this listener reads
        // on every request, so it belongs in front of the window that is answering them rather than beside it.
        new LanTrustedDevicesWindow(_peers, ForgetPeerAsync) { Owner = this }.ShowDialog();
        _ = RefreshTrustedSummaryAsync();
    }

    /// <summary>
    /// Un-trusts one device: its row, and the credential this machine issued to it.
    /// </summary>
    /// <remarks>
    /// Both halves, always. The session does that in one call, and it is the one used whenever a listener
    /// exists. When none does — the window is open with an unwritable receive directory, so nothing is
    /// listening — the row still has to come off and the credential still has to go, because a leftover secret
    /// would authenticate a device whose row is gone the moment a listener starts again.
    /// </remarks>
    private async Task<bool> ForgetPeerAsync(string deviceId)
    {
        if (_session is { } session) return await session.ForgetAsync(deviceId);
        var forgotten = await _peers!.ForgetAsync(deviceId);
        if (_secrets is not null) await _secrets.ForgetAsync(deviceId);
        return forgotten;
    }

    private string? ResolveReceiveDirectory(string? saved)
    {
        if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved)) return saved;
        var downloads = LanKnownFolders.Downloads();
        return !string.IsNullOrWhiteSpace(downloads) && Directory.Exists(downloads) ? downloads : null;
    }

    private async void PickDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Tr.F("Lan.PickReceiveDirectory") };
        if (dialog.ShowDialog(this) != true) return;
        await SaveSettingsAsync(settings => settings with { LanReceiveDirectory = dialog.FolderName });
        // The listener's whole write path is rooted at that directory, so changing it means starting a new
        // listener rather than telling the running one to move. The room's log is this window's, so the
        // conversation is still here when it comes back.
        await RestartListenerAsync();
    }

    private async void ApplyPort_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < LanOptions.MinimumPort or > LanOptions.MaximumPort)
        {
            Say(Tr.Format("Lan.PortRange", LanOptions.MinimumPort, LanOptions.MaximumPort));
            return;
        }

        await SaveSettingsAsync(settings => settings with { LanTransferPort = port });
        await RestartListenerAsync(port);
    }

    private async void AutoReceive_Changed(object sender, RoutedEventArgs e)
    {
        if (_restoringToggle) return;
        var chosen = AutoReceiveBox.IsChecked == true;
        await SaveSettingsAsync(settings => settings with { LanAutoAcceptFromTrusted = chosen });
        // One switch, two directions: the session reads it per write and the room reads it per offer, so a
        // running listener and a running poll both take the change without a restart, and a transfer already in
        // the air is not cancelled by a change of mind.
        if (_session is { } session) session.AutoAcceptFromTrusted = chosen;
        if (_room is { } room) room.AutoReceive = chosen;
        Say(chosen ? Tr.F("Lan.AutoReceiveOn") : Tr.F("Lan.AutoReceiveOff"));
    }

    /// <summary>
    /// Writes one choice into the settings file through a fresh read of it. A whole snapshot saved from this
    /// window's own copy would quietly undo whatever another window decided since, which is why the read is
    /// redone here instead of being carried.
    /// </summary>
    private async Task SaveSettingsAsync(Func<ApplicationSettings, ApplicationSettings> change)
    {
        try
        {
            var store = new SettingsStore();
            var loaded = await store.LoadAsync();
            _settings = change(loaded.Settings);
            await store.SaveAsync(_settings);
        }
        catch (Exception ex) { Say(Tr.F("Lan.SettingsSaveFailed") + Describe(ex)); }
    }

    // ------------------------------------------------------------------ history & presentation

    private async Task ArchiveAsync(LanTransferHistoryEntry entry)
    {
        if (_archive is null) return;
        try { await _archive.AppendAsync([entry]); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Unable to archive a LAN transfer: " + ex); }
    }

    internal async Task LoadHistoryAsync()
    {
        if (_archive is null) return;
        try
        {
            var entries = await _archive.QueryAsync();
            _history.Clear();
            foreach (var entry in entries)
                _history.Add(new LanRow(LanPeerPresentation.Summary(entry.Direction, entry.DeviceName, entry.Path,
                    entry.Bytes, entry.State, entry.Error), entry.ArchivedUtc.ToLocalTime().ToString("g"), entry));
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Unable to read the LAN history: " + ex); }
        UpdateEmptyHints();
    }

    private void UpdateEmptyHints()
    {
        MemberEmptyText.Visibility = _members.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        HistoryEmptyText.Visibility = _history.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        TransferBorder.Visibility = _transfers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PendingBorder.Visibility = _pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChatEmptyText.Visibility = _chat.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Runs a paint on the dispatcher, or straight away when this thread already is it.
    /// </summary>
    /// <remarks>
    /// The straight-away half matters: a sentence the user just typed would otherwise wait for the next beat to
    /// appear in its own room, and a window that is closing has nobody left to paint for, which is why the
    /// teardown is the first test rather than an exception to catch.
    /// </remarks>
    private void OnUi(Action paint)
    {
        if (_teardownStarted) return;
        if (Dispatcher.CheckAccess())
        {
            paint();
            return;
        }

        try { Dispatcher.BeginInvoke(paint); }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException) { }
    }

    private string Who(LanContact contact) => string.IsNullOrWhiteSpace(contact.DeviceName)
        ? Tr.Format("Lan.UnnamedDevice", contact.Address)
        : contact.DeviceName;

    private string DescribeOffer(LanInboundOffer offer) => offer.IsDirectory
        ? Tr.Format("Lan.OfferFolder", offer.Path)
        : Tr.Format("Lan.OfferFile", offer.Path, ByteSize.Format(offer.DeclaredBytes));

    private void Say(string text) => StatusText.Text = text;

    private static string Describe(Exception ex) => " " + ex.Message;
}

/// <summary>
/// A row that shows two lines of text and carries whatever the button in that row needs to act on.
/// </summary>
/// <remarks>
/// One type for the two lists that carry a payload: a question waiting to be answered and a history line are
/// both a name, a detail, and the thing the row is about — and a per-list row class would have made the
/// payload's type a compile-time accident rather than the thing the click handler checks.
/// </remarks>
public sealed record LanRow(string Name, string Detail, object? Tag);

/// <summary>
/// One device in the room's strip: what it is called, what kind of thing it is and where, and the sighting it
/// was drawn from.
/// </summary>
/// <remarks>
/// A type of its own rather than another <see cref="LanRow"/>, because nothing here is clickable and the thing
/// it carries is a live sighting rather than a payload — a strip whose chips all looked like the rows that have
/// buttons would invite somebody to click one.
/// </remarks>
public sealed record LanMemberRow(string Name, string Detail, LanRoomMember Member);

/// <summary>
/// One line of the room as the screen draws it.
/// </summary>
/// <remarks>
/// <para>
/// The row holds the entry rather than a copy of what it says, because a room line changes under the person
/// reading it: a file that was announced becomes a file that landed, and the bubble must move from 接收 to the
/// name it was stored under instead of being replaced by a second row. Which side it sits on and whether it is
/// narration are the entry's own answers, so nobody has to keep a flag in step with the log.
/// </para>
/// <para>
/// <see cref="Percentage"/> is read-only on purpose and both progress bars bind it with
/// <c>Mode=OneWay</c>: <c>RangeBase.Value</c> binds TwoWay by default, and a TwoWay read of a property with no
/// setter throws out of the dispatcher on the first tick — in a window the user is already looking at.
/// </para>
/// </remarks>
public sealed class LanChatRow(LanGroupMessage entry, bool isMine) : INotifyPropertyChanged
{
    private string _detail = "";
    private string _rate = "";
    private bool _showsProgress;
    private double _percentage;
    private bool _isUnknown;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The entry this row draws, live: <see cref="LanGroupMessage.HeldHere"/> is the room's own answer to 「接收」.</summary>
    public LanGroupMessage Entry { get; } = entry;

    public string MessageId => Entry.MessageId;

    public bool IsMine { get; } = isMine;

    public HorizontalAlignment Side => IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    /// <summary>A line this machine wrote about the room, which belongs to neither side of it.</summary>
    public bool IsSystem => Entry.AuthorDeviceId.Length == 0;

    public Visibility SystemVisibility => IsSystem ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BubbleVisibility => IsSystem ? Visibility.Collapsed : Visibility.Visible;

    public string SystemText => Entry.Text ?? "";

    /// <summary>What the bubble is about: the sentence, or the name the sender gave the bytes.</summary>
    public string Body => Entry.Kind == LanGroupKind.Text ? Entry.Text ?? "" : Entry.FileName ?? "";

    public string AuthorLine => $"{Author} · {Entry.CreatedUtc.ToLocalTime().ToString("g")}";

    /// <summary>Whose name to read above a bubble — never on this machine's own, whose side already says it.</summary>
    public Visibility AuthorVisibility => IsMine ? Visibility.Collapsed : Visibility.Visible;

    public string Detail => _detail;

    public Visibility DetailVisibility => _detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// 接收 is offered on a file or folder this copy cannot hand over, and on nothing else: not on a sentence, not
    /// on a line this machine wrote itself, and not on bytes that are already in the receive directory.
    /// </summary>
    public Visibility ActionVisibility =>
        !IsSystem && Entry.Kind != LanGroupKind.Text && !Entry.HeldHere
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ProgressVisibility => _showsProgress ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The speed of the pull this bubble is drawing, or nothing before the board has measured one.</summary>
    public string Rate => _rate;

    public Visibility RateVisibility => _rate.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public double Percentage => _percentage;

    public bool IsUnknown => _isUnknown;

    /// <summary>What a screen reader says for the whole row, so a row is reachable by name without being selectable.</summary>
    public string AutomationLine =>
        string.Join(" · ", new[] { Body, _detail, _rate }.Where(part => part.Length > 0));

    /// <summary>Says what this machine now knows about the bytes behind the entry, and takes the in-flight bar with it.</summary>
    public void SetDetail(string detail)
    {
        _detail = detail;
        _showsProgress = false;
        SetRate("");
        Raise(nameof(Detail), nameof(DetailVisibility), nameof(ProgressVisibility), nameof(AutomationLine));
    }

    /// <summary>
    /// Takes the in-flight bar away without touching what the row says.
    /// </summary>
    /// <remarks>
    /// The last tick of a pull arrives on the progress event, while the sentence about where the bytes landed
    /// arrives on the fetch event. Clearing here is what keeps a bubble that has finished from drawing a bar that
    /// will never move again during the moment between the two.
    /// </remarks>
    public void ClearProgress()
    {
        if (!_showsProgress && _rate.Length == 0) return;
        _showsProgress = false;
        SetRate("");
        Raise(nameof(ProgressVisibility), nameof(AutomationLine));
    }

    /// <summary>One tick of a pull this machine is doing right now.</summary>
    /// <param name="totalBytes">The length the entry announced, or 0 when nothing measured it.</param>
    /// <param name="rate">The measured speed and the time left, already written as one sentence.</param>
    public void SetProgress(long bytes, long totalBytes, string rate = "")
    {
        _showsProgress = true;
        _isUnknown = totalBytes <= 0;
        _percentage = totalBytes > 0 ? Math.Min(100d, 100d * bytes / totalBytes) : 0d;
        SetRate(rate);
        Raise(nameof(ProgressVisibility), nameof(Percentage), nameof(IsUnknown));
    }

    private void SetRate(string rate)
    {
        if (_rate == rate) return;
        _rate = rate;
        Raise(nameof(Rate), nameof(RateVisibility), nameof(AutomationLine));
    }

    private string Author => string.IsNullOrWhiteSpace(Entry.AuthorName) ? Tr.F("Lan.KindUnknown") : Entry.AuthorName;

    private void Raise(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// One transfer in flight, as the window shows it.
/// </summary>
public sealed class LanProgressRow : INotifyPropertyChanged
{
    private string _line;
    private string _rate = "";
    private double _percentage;
    private bool _isUnknown;

    public LanProgressRow(string line, bool hasTotal)
    {
        _line = line;
        _isUnknown = !hasTotal;
        _percentage = 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Line => _line;

    /// <summary>The measured speed and the time left, or nothing before the board has two samples.</summary>
    public string Rate => _rate;

    public Visibility RateVisibility => _rate.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public double Percentage => _percentage;

    public bool IsUnknown => _isUnknown;

    /// <summary>When this row finished, so the tick can take it away after a moment — null while it is running.</summary>
    public long? FinishedAt { get; private set; }

    public void Update(string line, double percentage, string rate = "")
    {
        _line = line;
        // A negative percentage is "no total was declared", which the bar already draws as indeterminate; the
        // value stays at zero so the fill never sits at a number nobody measured.
        _percentage = percentage < 0 ? 0 : Math.Min(100, percentage);
        SetRate(rate);
        Raise(nameof(Line));
        Raise(nameof(Percentage));
    }

    public void Finish(LanTransferState state, string detail)
    {
        _isUnknown = false;
        _percentage = 100;
        FinishedAt = Environment.TickCount64;
        _line = string.IsNullOrWhiteSpace(detail) ? _line : $"{_line} · {detail}";
        // A rate belongs to a transfer that is still going. Left on a finished row it reads as a promise the row
        // has already stopped keeping.
        SetRate("");
        Raise(nameof(Line));
        Raise(nameof(Percentage));
        Raise(nameof(IsUnknown));
    }

    private void SetRate(string rate)
    {
        if (_rate == rate) return;
        _rate = rate;
        Raise(nameof(Rate));
        Raise(nameof(RateVisibility));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// What a peer asked and how this side answered it. Held apart from the on-screen row because the listener
/// waits on this task while a person reads the screen, and the two must not be the same object.
/// </summary>
internal sealed class LanPendingOffer(LanInboundOffer offer)
{
    public LanInboundOffer Offer { get; } = offer;

    public TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The row this question drew, so answering it can take the row with it.</summary>
    public LanRow? Row { get; set; }

    /// <summary>The peer-gave-up hook, released as soon as one side has answered.</summary>
    public CancellationTokenRegistration GiveUp { get; set; }
}

/// <summary>
/// What the window needs from the shell to do any of its work, and the seams that let a test drive it without
/// touching a network, a firewall, or a person.
/// </summary>
/// <param name="Port">
/// The number the first listener takes, so a test never depends on what the developer's own settings hold. A
/// port applied through the window's box outranks it, because that path has to be drivable too.
/// </param>
/// <param name="ConfirmInbound">
/// Replaces the on-screen question. A modal dialog cannot be answered from an in-process test and this gate is
/// asked by a request thread rather than by the UI, so the rule it enforces is reached through here — the same
/// shape as the cloud window's conflict seam.
/// </param>
/// <param name="Netsh">
/// The process call behind the firewall button. Its absence is what lets a test prove that a machine with the
/// rule already open is never asked to elevate.
/// </param>
/// <param name="DiscoverPeers">Off in a test, where binding the UDP port would compete with the listener the
/// test itself is starting.</param>
internal sealed record LanTransferDependencies(
    int? Port = null,
    string? ReceiveDirectory = null,
    Func<LanInboundOffer, CancellationToken, Task<bool>>? ConfirmInbound = null,
    LanProcessRunner? Netsh = null,
    bool DiscoverPeers = true);
