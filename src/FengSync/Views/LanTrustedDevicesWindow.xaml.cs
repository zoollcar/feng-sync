using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using FengSync.Lan;
using FengSync.Localization;
using FengSync.Services;

namespace FengSync.Views;

/// <summary>
/// The list of devices this machine would let back in without a fresh code, and the one thing a person does to
/// it: take a device off.
/// </summary>
/// <remarks>
/// <para>
/// A window of its own rather than a column in the LAN window, because it is the only part of that window that
/// outlives it. Everything else there is about this session — who is in the room, what was said, what is in
/// flight — and this is a file on disk that will still be there tomorrow, so putting the two side by side made
/// a permanent decision look like a piece of live status.
/// </para>
/// <para>
/// The forgetting itself is not done here. Removing a device means removing the row <i>and</i> the credential
/// issued to it, and the credential lives with the session that issued it — so the caller hands in the verb
/// rather than this window reaching for a store it does not own. That is also what keeps the rule in one place
/// when the listener is rebuilt underneath an open dialog.
/// </para>
/// </remarks>
public partial class LanTrustedDevicesWindow : Window
{
    private readonly LanPeerStore _peers;
    private readonly Func<string, Task<bool>> _forget;
    private readonly ObservableCollection<LanRow> _rows = [];
    private bool _forgetting;

    public LanTrustedDevicesWindow() : this(new LanPeerStore(), _ => Task.FromResult(false)) { }

    internal LanTrustedDevicesWindow(LanPeerStore peers, Func<string, Task<bool>> forget)
    {
        _peers = peers;
        _forget = forget;
        InitializeComponent();
        TrustedList.ItemsSource = _rows;
        // Read on Loaded rather than in the constructor: the store is a file, and a window that blocked its own
        // construction on disk I/O would show nothing at all if that read was slow.
        Loaded += async (_, _) => await RefreshAsync();
    }

    /// <summary>The rows on screen, for a test that drives this window without showing it.</summary>
    internal ObservableCollection<LanRow> Rows => _rows;

    /// <summary>Re-reads the list, which is the only way the screen and the file are known to agree.</summary>
    internal async Task RefreshAsync()
    {
        try
        {
            var records = await _peers.LoadAsync();
            _rows.Clear();
            // Newest first: the device somebody is looking for is almost always the one they just paired.
            foreach (var record in records.OrderByDescending(candidate => candidate.LastSeenUtc))
                _rows.Add(new LanRow(
                    string.IsNullOrWhiteSpace(record.DeviceName) ? Tr.F("Lan.KindUnknown") : record.DeviceName,
                    Tr.Format("Lan.TrustedRow", LanPeerPresentation.Kind(record.Kind),
                        record.SeenAddresses.Count > 0 ? record.SeenAddresses[^1] : record.DeviceId,
                        record.LastSeenUtc.ToLocalTime().ToString("g")), record));
        }
        catch (Exception ex)
        {
            StatusText.Text = Tr.F("Lan.PeerListFailed") + " " + ex.Message;
        }

        EmptyText.Visibility = _rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ForgetPeer_Click(object sender, RoutedEventArgs e)
    {
        if (_forgetting) return;
        if ((sender as Button)?.Tag is not LanRow { Tag: LanPeerRecord record }) return;
        _forgetting = true;
        try
        {
            // Two things go, not one: the row the user is looking at and the credential behind it. A forgotten
            // device that still held a live token would keep writing files into the receive directory.
            var forgotten = await _forget(record.DeviceId);
            StatusText.Text = forgotten
                ? Tr.Format("Lan.Forgotten", record.DeviceName)
                : Tr.F("Lan.AlreadyGone");
            await RefreshAsync();
        }
        finally { _forgetting = false; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
