using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FengSync.Core.Configuration;
using FengSync.Lan;
using FengSync.Localization;
using FengSync.Services;
using Microsoft.Win32;

namespace FengSync.Views;

/// <summary>
/// The window that owns a LAN listener: it pairs, it receives, and it hands out whatever the user put in the
/// basket — for as long as this window is open, and not one second longer.
/// </summary>
/// <remarks>
/// <para>
/// The service is a field of the window rather than of the shell on purpose. A listener that outlives its
/// window would answer a pairing code nobody is watching, and the one promise this feature makes about its
/// blast radius is that closing the window closes the door. The shell holds only a reference to the open
/// window, so application shutdown can await the teardown; the reference arrives through
/// <see cref="ShowOrActivate"/> rather than by this window reaching for <c>Application.Current</c>, which is
/// how a shared window stays hostable by any shell.
/// </para>
/// <para>
/// Everything with a decision in it lives in <see cref="LanSession"/> and <see cref="LanReceiveSink"/>, both
/// of which are reachable without a socket. What is here is WPF state: rows on screen, a countdown, and the
/// one place a person's 同意 or 拒绝 becomes an answer the listener can act on.
/// </para>
/// </remarks>
public partial class LanTransferWindow : Window
{
    /// <summary>How often the countdown and the printed code are re-read off the pairing state.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly LanTransferDependencies _deps;
    private readonly App? _shell;
    private readonly LanSendBasket _basket = new();
    private readonly ObservableCollection<LanRow> _trusted = [];
    private readonly ObservableCollection<LanRow> _discovered = [];
    private readonly ObservableCollection<LanRow> _pending = [];
    private readonly ObservableCollection<LanProgressRow> _transfers = [];
    private readonly ObservableCollection<LanRow> _history = [];
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
    private LanDiscoveryService? _discovery;
    private LanPeerClient? _client;
    private LanTransferHistoryRepository? _archive;
    private DispatcherTimer? _timer;
    private CancellationTokenSource? _lifetime;
    private Task _teardown = Task.CompletedTask;
    private bool _teardownStarted;
    private string? _receiveDirectory;
    private string? _paintedCode;
    private string? _paintedAddress;
    private string _chosenAddress = "";

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
        BasketList.ItemsSource = _basket.Rows;
        TrustedList.ItemsSource = _trusted;
        DiscoveredList.ItemsSource = _discovered;
        PendingList.ItemsSource = _pending;
        TransferList.ItemsSource = _transfers;
        HistoryList.ItemsSource = _history;
        AddressList.ItemsSource = Array.Empty<string>();
        BasketList.AllowDrop = true;
        BasketList.Drop += Basket_Drop;
        // WPF has no Opened event on Window; ContentRendered is the one that means "the person can see it",
        // and it fires once rather than on every re-hive the way Loaded does.
        ContentRendered += async (_, _) => await OpenAsync();
        _basket.PropertyChanged += (_, _) => UpdateEmptyHints();
        UpdateEmptyHints();
    }

    /// <summary>The seams a test installs in place of a network, a firewall prompt, or a person.</summary>
    internal LanTransferDependencies Dependencies => _deps;

    /// <summary>What the listener is receiving into right now, for a test that needs to read the screen and the state together.</summary>
    internal LanSession? Session => _session;

    internal LanSendBasket Basket => _basket;

    internal IReadOnlyList<string> ListenAddresses => _host?.ListenAddresses ?? [];

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
        _timer = new DispatcherTimer { Interval = Tick };
        _timer.Tick += (_, _) => RefreshCode();
        _timer.Start();
        await CheckFirewallAsync();
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
    /// Stops the discovery advertiser and the listener and empties the basket. Idempotent, because the window's
    /// own close and the application's shutdown both call it and neither may make the other throw.
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

        await StopListenerAsync().ConfigureAwait(false);
        _client?.Dispose();
        _client = null;
        // Session memory, by design: a row that outlived the window would still be readable by a token issued
        // to a peer that has since left.
        _basket.Clear();
        _running.Clear();
    }

    // ------------------------------------------------------------------ listener

    /// <summary>
    /// (Re)builds the whole listening stack from the settings on disk. Called once on open and again whenever
    /// the port or the receive directory changes, because those two are the only choices that cannot be
    /// applied to a running listener.
    /// </summary>
    private async Task RestartListenerAsync()
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

        var port = _deps.Port ?? _settings.LanTransferPort;
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
                _peers, _secrets, _pairing, _basket, AskInboundAsync);
            _client ??= new LanPeerClient(_identity);
            _host = await LanServerHost.StartAsync(new LanServerOptions(_session, _identity, _pairing, sink), _lifetime.Token);
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
        _chosenAddress = _host.ListenAddresses.FirstOrDefault() ?? "";
        AddressList.ItemsSource = _host.ListenAddresses;
        AddressList.SelectedItem = _chosenAddress;
        AutoAcceptBox.IsChecked = _settings.LanAutoAcceptFromTrusted;
        ReceiveDirectoryText.Text = _receiveDirectory;
        await RefreshTrustedAsync();
        RefreshCode();
        StartDiscovery(port);
        Say(Tr.Format("Lan.Listening", port));
        UpdateEmptyHints();
    }

    /// <summary>Takes the listener and the advertiser down, leaving the window's own state intact.</summary>
    private async Task StopListenerAsync()
    {
        _timer?.Stop();
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
            // A datagram arrives on the socket's thread, so only the row crosses onto the dispatcher.
            _discovery.PeerFound += peer => Dispatcher.BeginInvoke(() => RememberDiscovered(peer));
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
        host.AdmissionDecided += args => Dispatcher.BeginInvoke(() => OnAdmission(args));
        host.InboxReceived += args => Dispatcher.BeginInvoke(() => OnInbox(args));
        host.DirectoryReceived += args => Dispatcher.BeginInvoke(() => OnDirectory(args));
        host.OutboundDelivered += args => Dispatcher.BeginInvoke(() => OnOutbound(args));
        host.TransferProgress += args => Dispatcher.BeginInvoke(() => OnProgress(args));
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
        PairCodeText.Text = code;
        PairRemainingText.Text = remaining > 0
            ? Tr.Format("Lan.CodeExpires", remaining)
            : Tr.F("Lan.CodeRenewing");
        var address = _chosenAddress.Length > 0 ? _chosenAddress : _host?.ListenAddresses.FirstOrDefault() ?? "";
        if (address.Length == 0 || (_paintedCode == code && _paintedAddress == address)) return;
        PaintQr(code, address, remaining);
    }

    private void PaintQr(string code, string address, int remainingSeconds)
    {
        if (_identity is null) return;
        try
        {
            var payload = LanPairQr.BuildPayload(LanPairQr.For(_identity, IpOf(address), PortOf(address), code,
                DateTimeOffset.UtcNow, remainingSeconds));
            var image = new System.Windows.Media.Imaging.BitmapImage();
            // The bytes go straight into the bitmap and are never written out: a pairing QR is a secret with a
            // two-minute shelf life, and a stray .png in a temp folder is the one thing this must not leave.
            using (var stream = new MemoryStream(LanPairQr.Png(payload)))
            {
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
            QrImage.Source = image;
            // The address is what a person reads aloud to a colleague; the code in it is spent within two
            // minutes, which is why the whole line is safe to show and a token never would be.
            QrImage.ToolTip = LanPairQr.BuildUrl(IpOf(address), PortOf(address));
            _paintedCode = code;
            _paintedAddress = address;
        }
        catch (Exception ex)
        {
            // A QR this machine cannot draw must not take the window with it: the six characters below the
            // picture are the same pairing path, typed.
            QrImage.Source = null;
            Say(Tr.F("Lan.QrFailed") + Describe(ex));
        }
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
        Say(args.Accepted ? Tr.Format("Lan.Paired", who) : Tr.Format("Lan.PairRefused", who, LanPeerPresentation.Refusal(args.Refusal)));
        _ = RefreshTrustedAsync();
    }

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

    private void OnProgress(LanProgressEventArgs args)
    {
        var key = InKey(args.Contact.DeviceId, args.Path);
        var line = ProgressLine(args.Path, args.Contact, args.Bytes, args.TotalBytes, args.IsInbound);
        if (!_running.TryGetValue(key, out var row))
        {
            row = new LanProgressRow(line, args.TotalBytes > 0);
            _running[key] = row;
            _transfers.Add(row);
            UpdateEmptyHints();
        }
        row.Update(line, args.TotalBytes > 0 ? 100d * args.Bytes / args.TotalBytes : -1);
    }

    /// <summary>
    /// A row for the bytes this side is pushing to a peer. The host raises nothing for those — they leave
    /// through <see cref="LanPeerClient"/>, which has no listener to report to — so the window draws its own
    /// upload progress from the client's callback.
    /// </summary>
    internal void ReportOutboundProgress(LanSendItem item, long bytes)
    {
        // Report lands on whichever thread drained the request stream, and every collection here belongs to the
        // dispatcher, so the crossing happens before the lookup rather than after it.
        Dispatcher.BeginInvoke(() =>
        {
            var key = OutKey(item);
            // Its own key rather than Lan.ProgressOut, because this line has no peer to name: the bytes leave
            // through the client, which knows the address it dialed but reports progress without a contact.
            var line = Tr.Format("Lan.ProgressUp", item.Name, ByteSize.Format(bytes),
                item.IsDirectory ? Tr.F("Lan.SizeUnknown") : ByteSize.Format(item.Bytes));
            if (!_running.TryGetValue(key, out var row))
            {
                row = new LanProgressRow(line, !item.IsDirectory && item.Bytes > 0);
                _running[key] = row;
                _transfers.Add(row);
                UpdateEmptyHints();
            }
            row.Update(line, item.IsDirectory || item.Bytes <= 0 ? -1 : 100d * bytes / item.Bytes);
        });
    }

    private string ProgressLine(string path, LanContact contact, long bytes, long totalBytes, bool inbound) =>
        inbound
            ? Tr.Format("Lan.ProgressIn", Path.GetFileName(path), Who(contact), ByteSize.Format(bytes),
                totalBytes > 0 ? ByteSize.Format(totalBytes) : Tr.F("Lan.SizeUnknown"))
            : Tr.Format("Lan.ProgressOut", Path.GetFileName(path), Who(contact), ByteSize.Format(bytes),
                totalBytes > 0 ? ByteSize.Format(totalBytes) : Tr.F("Lan.SizeUnknown"));

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

    /// <summary>Closes the row for a send that finished, was refused, or never got as far as a row.</summary>
    private void CloseOutbound(LanSendItem? item, LanTransferState state, string detail)
    {
        if (item is not null && _running.Remove(OutKey(item), out var row)) row.Finish(state, detail);
    }

    private static string InKey(string deviceId, string path) => $"{deviceId}|{path}";

    /// <summary>
    /// Outbound rows are keyed on the basket item rather than on a device, because this side names its own
    /// uploads and a peer's identifier has nothing to do with which row is being read out of the basket.
    /// </summary>
    private static string OutKey(LanSendItem item) => $"out|{item.Id:N}";

    // ------------------------------------------------------------------ sending

    private void Basket_Drop(object sender, DragEventArgs e) => AcceptDrop(e);

    private void Window_Drop(object sender, DragEventArgs e) => AcceptDrop(e);

    private void AcceptDrop(DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var added = _basket.Add(e.Data.GetData(DataFormats.FileDrop) as string[]);
        Say(added > 0 ? Tr.Format("Lan.BasketAdded", added) : Tr.F("Lan.BasketNothingAdded"));
        UpdateEmptyHints();
    }

    private void Window_DragOver(object sender, DragEventArgs e) =>
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = Tr.F("Lan.AddFiles") };
        if (dialog.ShowDialog(this) != true) return;
        Say(Tr.Format("Lan.BasketAdded", _basket.Add(dialog.FileNames)));
        UpdateEmptyHints();
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Tr.F("Lan.AddFolder"), Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        Say(Tr.Format("Lan.BasketAdded", _basket.Add(dialog.FolderNames)));
        UpdateEmptyHints();
    }

    private void RemoveFromBasket_Click(object sender, RoutedEventArgs e)
    {
        var picked = BasketList.SelectedItems.OfType<LanSendRow>().ToList();
        foreach (var row in picked) _basket.Remove(row);
        Say(Tr.Format("Lan.BasketRemoved", picked.Count));
        UpdateEmptyHints();
    }

    private void ClearBasket_Click(object sender, RoutedEventArgs e)
    {
        _basket.Clear();
        Say(Tr.F("Lan.BasketCleared"));
        UpdateEmptyHints();
    }

    private async void CheckPeer_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPeerTarget(out var address, out var port)) return;
        var hello = await HelloAsync(address, port);
        PeerStatusText.Text = hello is null
            ? Tr.F("Lan.PeerNoAnswer")
            : Tr.Format("Lan.PeerAnswers", hello.DeviceName, $"{address}:{port}", LanPeerPresentation.Kind(hello.Kind));
        if (hello is not null) RememberDiscovered(new LanDiscoveredPeer(hello.DeviceId, hello.DeviceName, address, port));
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
            RememberDiscovered(new LanDiscoveredPeer(hello.DeviceId, hello.DeviceName, address, port));
        }
        catch (LanTransferException ex) { PeerStatusText.Text = LanPeerPresentation.Refusal(ex.Refusal); }
        catch (Exception ex) { PeerStatusText.Text = Tr.F("Lan.PeerNoAnswer") + Describe(ex); }
    }

    private async void SendToPeer_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || !TryPeerTarget(out var address, out var port)) return;
        var rows = _basket.Snapshot();
        if (rows.Count == 0)
        {
            PeerStatusText.Text = Tr.F("Lan.BasketEmptySend");
            return;
        }

        var hello = await HelloAsync(address, port);
        if (hello is null)
        {
            PeerStatusText.Text = Tr.F("Lan.PeerNoAnswer");
            return;
        }

        var peer = new LanPeerEndpoint(hello.DeviceId, address, port);
        if (!await _client.IsPairedAsync(peer))
        {
            // A device that has to be told the code again is not a failure of the send; the pairing is a
            // separate decision a person makes on the other screen.
            PeerStatusText.Text = LanPeerPresentation.Refusal(LanRefusal.Unpaired);
            return;
        }

        var session = Guid.NewGuid().ToString("N")[..8];
        var button = (Button)sender;
        button.IsEnabled = false;
        var sent = 0;
        long bytes = 0;
        LanSendItem? current = null;
        try
        {
            foreach (var item in rows)
            {
                current = item;
                var outcome = await _client.SendAsync(peer, item, session, new ProgressRowReporter(this, item), _lifetime?.Token ?? CancellationToken.None);
                CloseOutbound(item, LanTransferState.Completed, "");
                sent++;
                bytes += outcome.Bytes;
                current = null;
            }
            PeerStatusText.Text = Tr.Format("Lan.SendDone", sent, ByteSize.Format(bytes), hello.DeviceName);
        }
        catch (LanTransferException ex)
        {
            var reason = LanPeerPresentation.Refusal(ex.Refusal);
            PeerStatusText.Text = current is null ? reason : Tr.Format("Lan.SendFailed", current.Name, reason);
            CloseOutbound(current, LanTransferState.Failed, reason);
        }
        catch (OperationCanceledException)
        {
            PeerStatusText.Text = Tr.F("Lan.SendCancelled");
            CloseOutbound(current, LanTransferState.Cancelled, Tr.F("Lan.State.Cancelled"));
        }
        catch (Exception ex)
        {
            PeerStatusText.Text = current is null ? Describe(ex) : Tr.Format("Lan.SendFailed", current.Name, Describe(ex));
            CloseOutbound(current, LanTransferState.Failed, Describe(ex));
        }
        finally { if (IsLoaded) button.IsEnabled = true; }
    }

    /// <summary>
    /// A peer address typed or selected. Kept as a parse rather than a guess: the manual box is the one path
    /// that works when discovery cannot, so an unparseable address has to be named rather than sent.
    /// </summary>
    private bool TryPeerTarget(out string address, out int port)
    {
        port = CurrentPort();
        var typed = (PeerAddressBox.Text ?? "").Trim();
        if (typed.Length == 0 && DiscoveredList.SelectedItem is LanRow { Tag: LanPeerEndpoint found })
        {
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

    private void RememberDiscovered(LanDiscoveredPeer peer)
    {
        var existing = _discovered.FirstOrDefault(row => row.Tag is LanPeerEndpoint endpoint
            && string.Equals(endpoint.DeviceId, peer.DeviceId, StringComparison.OrdinalIgnoreCase));
        var row = new LanRow(peer.DeviceName,
            Tr.Format("Lan.PeerRow", peer.Address, peer.Port, LanPeerPresentation.Kind(LanProtocol.KindWindows)),
            new LanPeerEndpoint(peer.DeviceId, peer.Address, peer.Port));
        if (existing is null) _discovered.Add(row);
        else _discovered[_discovered.IndexOf(existing)] = row;
        // The manual box follows the announcement rather than the other way round: a person who typed an
        // address and then saw the same machine announce itself should not lose what they wrote.
        if (string.IsNullOrWhiteSpace(PeerAddressBox.Text)) PeerAddressBox.Text = $"{peer.Address}:{peer.Port}";
        UpdateEmptyHints();
    }

    // ------------------------------------------------------------------ device list & directory

    private async Task RefreshTrustedAsync()
    {
        if (_peers is null) return;
        try
        {
            var records = await _peers.LoadAsync();
            _trusted.Clear();
            foreach (var record in records.OrderByDescending(candidate => candidate.LastSeenUtc))
                _trusted.Add(new LanRow(
                    string.IsNullOrWhiteSpace(record.DeviceName) ? Tr.F("Lan.KindUnknown") : record.DeviceName,
                    Tr.Format("Lan.TrustedRow", LanPeerPresentation.Kind(record.Kind),
                        record.SeenAddresses.Count > 0 ? record.SeenAddresses[^1] : record.DeviceId,
                        record.LastSeenUtc.ToLocalTime().ToString("g")), record));
        }
        catch (Exception ex) { Say(Tr.F("Lan.PeerListFailed") + Describe(ex)); }
        UpdateEmptyHints();
    }

    private async void ForgetPeer_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || (sender as Button)?.Tag is not LanRow { Tag: LanPeerRecord record }) return;
        // Two things go, not one: the row the user is looking at and the credential behind it. A forgotten
        // device that still holds a live token would keep writing files into the receive directory.
        var forgotten = await _session.ForgetAsync(record.DeviceId);
        Say(forgotten ? Tr.Format("Lan.Forgotten", record.DeviceName) : Tr.F("Lan.AlreadyGone"));
        await RefreshTrustedAsync();
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
        // listener rather than telling the running one to move.
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
        await RestartListenerAsync();
    }

    private async void AutoAccept_Changed(object sender, RoutedEventArgs e)
    {
        var chosen = AutoAcceptBox.IsChecked == true;
        await SaveSettingsAsync(settings => settings with { LanAutoAcceptFromTrusted = chosen });
        // The session reads the switch per write, so a running listener takes it without a restart and a
        // transfer already in the air is not cancelled by a change of mind.
        if (_session is { } session) session.AutoAcceptFromTrusted = chosen;
        Say(chosen ? Tr.F("Lan.AutoAcceptOn") : Tr.F("Lan.AutoAcceptOff"));
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
        TrustedEmptyText.Visibility = _trusted.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        DiscoveredEmptyText.Visibility = _discovered.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        HistoryEmptyText.Visibility = _history.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        TransfersEmptyText.Visibility = _transfers.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        PendingBorder.Visibility = _pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
/// One type for four lists on purpose: a trusted device, a discovered computer, a question waiting to be
/// answered, and a history line are all a name, a detail, and a payload — and a per-list row class would have
/// made the payload's type a compile-time accident rather than the thing the click handler checks.
/// </remarks>
public sealed record LanRow(string Name, string Detail, object? Tag);

/// <summary>
/// One transfer in flight, as the window shows it.
/// </summary>
public sealed class LanProgressRow : INotifyPropertyChanged
{
    private string _line;
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
    public double Percentage => _percentage;
    public bool IsUnknown => _isUnknown;

    public void Update(string line, double percentage)
    {
        _line = line;
        // A negative percentage is "no total was declared", which the bar already draws as indeterminate; the
        // value stays at zero so the fill never sits at a number nobody measured.
        _percentage = percentage < 0 ? 0 : Math.Min(100, percentage);
        Raise(nameof(Line));
        Raise(nameof(Percentage));
    }

    public void Finish(LanTransferState state, string detail)
    {
        _isUnknown = false;
        _percentage = 100;
        _line = string.IsNullOrWhiteSpace(detail) ? _line : $"{_line} · {detail}";
        Raise(nameof(Line));
        Raise(nameof(Percentage));
        Raise(nameof(IsUnknown));
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

/// <summary>Reports a peer's own upload progress into the window's list.</summary>
internal sealed class ProgressRowReporter(LanTransferWindow window, LanSendItem item) : IProgress<long>
{
    public void Report(long value) => window.ReportOutboundProgress(item, value);
}
