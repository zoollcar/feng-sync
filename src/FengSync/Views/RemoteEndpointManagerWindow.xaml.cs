using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FengSync.Core;
using FengSync.Core.Configuration;
using FengSync.Core.Mount;
using FengSync.Services;
using Microsoft.Win32;

namespace FengSync.Views;

/// <summary>
/// "远程端点管理" — opens from the Tools menu and consolidates two admin-style concerns:
/// (1) managing the cloud endpoints stored in rclone.conf (refresh / new / edit / re-check / re-login /
/// delete), and (2) managing live <c>rclone mount</c> processes on the host. It also hosts the file browser,
/// which is the one place a cloud endpoint's contents can be listed, searched, renamed, deleted and moved
/// in and out of. The browse-and-pick flow used when adding an endpoint to a sync side lives in
/// <see cref="RemoteEndpointPickerWindow"/> instead.
/// </summary>
public partial class RemoteEndpointManagerWindow : Window
{
    /// <summary>How long a drag gesture waits for a not-yet-cached file before falling back to reveal.</summary>
    private static readonly TimeSpan DragHydrationTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The ordering choices, in the order the picker offers them.</summary>
    private static readonly (CloudSortColumn Column, string Label)[] SortOptions =
    [
        (CloudSortColumn.Name, "名称"),
        (CloudSortColumn.Size, "大小"),
        (CloudSortColumn.Modified, "修改时间")
    ];

    /// <summary>The endpoint list's ordering choices. Health is first because a list ordered by it is a to-do
    /// list, and a broken endpoint is what the user came here to find.</summary>
    private static readonly (CloudEndpointSortColumn Column, string Label)[] EndpointSortOptions =
    [
        (CloudEndpointSortColumn.Health, "状态"),
        (CloudEndpointSortColumn.Name, "名称"),
        (CloudEndpointSortColumn.Provider, "类型")
    ];

    private readonly ObservableCollection<CloudEndpointRow> _accounts = [];
    /// <summary>Every endpoint as loaded, before the search box and the sort choice are applied.</summary>
    private readonly List<CloudEndpointRow> _loaded = [];
    private readonly ObservableCollection<MountInfo> _mounts = [];
    private readonly ObservableCollection<CloudFileEntry> _entries = [];
    /// <summary>The last listing before filtering and ordering, so typing in the search box re-lists nothing.</summary>
    private readonly List<CloudFileEntry> _listed = [];
    /// <summary>Visited directories, and where in them the browser currently is, for 后退 and 前进.</summary>
    private readonly List<string> _history = [];
    private readonly RcloneMountService _mountService = AppServices.Current.MountService;
    private readonly CloudFileManagerService _files;
    /// <summary>
    /// Shared so uploads and downloads keep running after this window closes; falls back to a private
    /// queue only when no shell installed its services (a preview or a test).
    /// </summary>
    private readonly CloudTransferQueue _transfers;
    private ApplicationSettings _settings = new();
    /// <summary>
    /// The pictures the grid view paints. Its own object because a tile is not a transfer: it has no journal,
    /// no progress the user asked to see, and no reason to wait behind either.
    /// </summary>
    private readonly CloudThumbnailStore _thumbnails;
    /// <summary>The window's own tile lookup, so a repaint of a row can ask the store without the row knowing it exists.</summary>
    private readonly CloudTileSourceConverter _tileConverter;
    /// <summary>The GridView the XAML declares, kept so 列表视图 restores exactly the columns it was built with.</summary>
    private ViewBase? _listView;
    private CloudBrowserViewMode _browserView = CloudBrowserViewMode.List;
    /// <summary>Cancels the tile reads started by the previous listing or the previous view.</summary>
    private CancellationTokenSource? _tileRound;
    private Task _tileWait = Task.CompletedTask;
    private bool _tileRefreshQueued;
    private Point _dragStart;
    private bool _dragArmed;
    private string _currentPath = "";
    /// <summary>
    /// The rows 复制 or 剪切 put on hold, and how they got there. Held here rather than in the system
    /// clipboard because a cloud listing row is not something another application could paste, and because
    /// the mode is the only thing that decides whether a paste ends in a delete.
    /// </summary>
    private readonly List<CloudClipboardItem> _clipboard = [];
    private CloudClipboardMode _clipboardMode;
    /// <summary>
    /// True while the listing came from a recursive search. The search box then no longer filters it: the
    /// results were matched on the whole path, so re-filtering them by name would hide the folder hits the
    /// search deliberately included.
    /// </summary>
    private bool _listingIsSearchResult;
    /// <summary>
    /// The places the browser remembers, as the file holds them — including a favourite whose endpoint the list
    /// cannot offer right now, which is hidden from the jump lists rather than deleted.
    /// </summary>
    private readonly CloudBrowserBookmarkStore _bookmarks = new();
    private List<CloudBookmark> _favorites = [];
    private List<CloudBookmark> _recent = [];
    /// <summary>
    /// Set while the window itself writes a jump list or the endpoint selection. Those writes raise selection
    /// events, and this is what keeps the window's own bookkeeping from being read as a gesture that starts a
    /// second listing.
    /// </summary>
    private bool _applyingJump;
    /// <summary>Why the remembered places are not there, held until the startup status line can carry it.</summary>
    private string? _bookmarkNotice;
    private int _historyIndex = -1;
    private CloudSortColumn _sortColumn = CloudSortColumn.Name;
    private bool _sortDescending;
    private CloudEndpointSortColumn _endpointSortColumn = CloudEndpointSortColumn.Health;
    private bool _endpointSortDescending;
    /// <summary>
    /// The endpoint whose contents the browser is showing. The endpoint list is re-populated whenever the
    /// search box or the sort choice changes, and every re-population raises selection events — including a
    /// transient one with nothing selected. Comparing against this is what keeps a keystroke from re-listing
    /// the remote directory over the network, which is one round trip to the cloud service per key.
    /// </summary>
    private string? _browsedEndpoint;
    /// <summary>
    /// True while the endpoint list is being rebuilt. Rebuilding clears the selection and sets it again, so
    /// the selection events raised on the way are not gestures the browser should follow; the re-arrange
    /// decides for itself, once, when it is done.
    /// </summary>
    private bool _rearranging;
    private bool _busy;
    private bool _mountBusy;

    public RemoteEndpointManagerWindow() : this(null, null) { }

    /// <summary>
    /// Builds the browser over the services it should talk to. The parameterless form takes the
    /// application-owned ones, which is what both shells use; this form lets a test hand in a file service and
    /// a queue wired to a stand-in rclone, so the browser's own decisions — what a 粘贴 means for each selected
    /// row, and which call each case makes — are checkable without starting rclone.
    /// </summary>
    internal RemoteEndpointManagerWindow(CloudFileManagerService? files, CloudTransferQueue? transfers)
    {
        _files = files ?? new CloudFileManagerService();
        _transfers = transfers ?? AppServices.TryCurrent?.CloudTransfers ?? new CloudTransferQueue();
        _thumbnails = new CloudThumbnailStore(_files);
        InitializeComponent();
        EndpointList.ItemsSource = _accounts;
        MountsList.ItemsSource = _mounts;
        FileList.ItemsSource = _entries;
        _listView = FileList.View;
        _tileConverter = (CloudTileSourceConverter)Resources["CloudTileConverter"];
        // The tile template binds a row to the picture it currently has. Both the thumbnail store and which
        // endpoint is being browsed belong to the window, not to the row, so the lookup is handed to the
        // converter here rather than being reached from inside the template.
        _tileConverter.Peek = entry => _thumbnails.Peek(Selected?.Name ?? "", entry);
        BuildSortOptions();
        BuildEndpointSortOptions();
        // A GridView header click is routed, not bubbled as a per-column event, so it is attached here.
        FileList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(ColumnHeader_Click));
        _transfers.PropertyChanged += Transfers_PropertyChanged;
        Closed += (_, _) => _transfers.PropertyChanged -= Transfers_PropertyChanged;
        Loaded += async (_, _) => await InitializeAsync();
    }

    private CloudEndpointAccount? Selected => (EndpointList.SelectedItem as CloudEndpointRow)?.Account;
    private MountInfo? SelectedMount => MountsList.SelectedItem as MountInfo;

    // ---- Test seams: the parts of the browser's state a test can inspect or drive without rclone ----

    /// <summary>The path the browser is showing.</summary>
    internal string CurrentPath => _currentPath;
    /// <summary>The rows the browser is showing, already filtered and ordered.</summary>
    internal IReadOnlyList<CloudFileEntry> VisibleEntries => [.. _entries];
    /// <summary>The address trail the browser is showing.</summary>
    internal IReadOnlyList<CloudBreadcrumb> Trail => Breadcrumbs.ItemsSource as IReadOnlyList<CloudBreadcrumb> ?? [];
    /// <summary>The rows currently selected in the list.</summary>
    internal IReadOnlyList<CloudFileEntry> Selection => SelectedEntries();

    /// <summary>The rows the browser holds for a 粘贴, and whether they were copied or cut.</summary>
    internal IReadOnlyList<CloudClipboardItem> ClipboardItems => [.. _clipboard];
    internal CloudClipboardMode ClipboardMode => _clipboardMode;

    /// <summary>The endpoints the list loaded, before the search box and the sort choice are applied.</summary>
    internal IReadOnlyList<CloudEndpointRow> LoadedEndpoints => [.. _loaded];
    /// <summary>The endpoint rows the list is showing, already filtered and ordered.</summary>
    internal IReadOnlyList<CloudEndpointRow> VisibleEndpoints => [.. _accounts];

    /// <summary>The places remembered as favourites, in the order the user put them in, stale endpoint included.</summary>
    internal IReadOnlyList<CloudBookmark> FavoritePlaces => [.. _favorites];
    internal IReadOnlyList<CloudBookmark> RecentPlaces => [.. _recent];
    /// <summary>The rows the two jump lists offer right now.</summary>
    internal IReadOnlyList<CloudJumpItem> FavoriteJumps => FavoriteJumpBox.ItemsSource as IReadOnlyList<CloudJumpItem> ?? [];
    internal IReadOnlyList<CloudJumpItem> RecentJumps => RecentJumpBox.ItemsSource as IReadOnlyList<CloudJumpItem> ?? [];
    /// <summary>What the favourite button says, which is the state it reports rather than a label it was given.</summary>
    internal string FavoriteToggleLabel => FavoriteToggleButton.Content as string ?? "";
    /// <summary>Where the remembered places are persisted, so a test can read the file it wrote.</summary>
    internal CloudBrowserBookmarkStore Bookmarks => _bookmarks;
    /// <summary>
    /// The sentence the window owes the status line when the remembered places could not be read. It is held
    /// rather than shown straight away because the endpoint refresh that follows overwrites the status line, and
    /// a lost file has to be said after the count that would otherwise bury it.
    /// </summary>
    internal string? BookmarkNotice => _bookmarkNotice;

    /// <summary>
    /// Installs the loaded rows without touching rclone, so the search and ordering rules can be driven
    /// through the real controls rather than re-tested as pure functions. The selection is recorded as
    /// already shown, so a later keystroke that leaves it alone does not start a listing either.
    /// </summary>
    internal void LoadEndpointsForTest(IEnumerable<CloudEndpointRow> rows, string? selectName = null)
    {
        _loaded.Clear();
        _loaded.AddRange(rows);
        ApplyEndpointView(selectName, browse: false);
        _browsedEndpoint = Selected?.Name;
    }

    private async Task InitializeAsync()
    {
        try { _settings = (await new SettingsStore().LoadAsync()).Settings; }
        catch (Exception ex) { StatusText.Text = "无法读取设置：" + RcloneUiError.Describe(ex, "cloud-settings"); }
        // Applied before the first listing so the directory the window opens on is drawn in the view the user
        // chose, rather than being drawn as a list and then re-drawn as tiles.
        _browserView = Enum.IsDefined(_settings.CloudBrowserView) ? _settings.CloudBrowserView : CloudBrowserViewMode.List;
        ApplyBrowserView();
        await LoadBookmarksAsync();
        await RefreshAsync();
        // Said last, because a window that opened on an empty 收藏 for a reason the user cannot see has to say so
        // over the endpoint count: the count is what any listing repeats, the lost file is not.
        if (_bookmarkNotice is { } notice) StatusText.Text = notice;
    }

    /// <summary>
    /// Reads the remembered places into the window's two lists and rebuilds the jump lists. Split out from
    /// <see cref="InitializeAsync"/> because a window that is never shown — every in-process case — does not reach
    /// the <c>Loaded</c> handler, and a test that drives 收藏 has to start from the same file the shell does.
    /// </summary>
    internal async Task LoadBookmarksAsync()
    {
        _bookmarkNotice = null;
        try
        {
            var loaded = await _bookmarks.LoadAsync();
            _favorites = [.. loaded.Favorites];
            _recent = [.. loaded.Recent];
            if (loaded.RecoveredFromCorruption)
                _bookmarkNotice = $"收藏文件读不出来，已备份为 {Path.GetFileName(loaded.BackupPath)}，"
                    + "收藏与最近访问从空列表开始。";
        }
        catch (Exception ex) { _bookmarkNotice = "无法读取收藏与最近访问：" + RcloneUiError.Describe(ex, "cloud-bookmarks-load"); }
        RefreshBookmarkLists();
    }

    private async Task RefreshAsync()
    {
        try
        {
            StatusText.Text = "正在读取云端端点列表…";
            var previous = Selected?.Name;
            // A refresh must not throw away what a check already learned about an endpoint.
            var known = _loaded.ToDictionary(row => row.Account.Name, row => row.Status, StringComparer.OrdinalIgnoreCase);
            var accounts = await CloudEndpointService.LoadEndpointAccountsAsync();
            _loaded.Clear();
            foreach (var account in accounts)
            {
                var row = new CloudEndpointRow(account);
                if (known.TryGetValue(account.Name, out var status)) row.Status = status;
                _loaded.Add(row);
            }
            ApplyEndpointView(previous);
            StatusText.Text = _loaded.Count == 0
                ? "尚无云端端点。点击“新建端点”创建一个。"
                : $"共 {_loaded.Count} 个云端端点。";
        }
        catch (Exception ex) { StatusText.Text = "无法读取云端端点：" + RcloneUiError.Describe(ex, "endpoint-manager-list"); }
        UpdateEndpointButtons();
        await RefreshMountsAsync();
    }

    /// <summary>
    /// Rebuilds the visible rows from <see cref="_loaded"/> using the current search text and sort choice, and
    /// keeps the selection on <paramref name="selectName"/> when it survived the filter.
    /// </summary>
    /// <param name="browse">
    /// Whether the browser may follow the resulting selection. False only for a caller that installs a
    /// selection nobody chose — the test seam, which exists to drive the list without starting rclone.
    /// </param>
    private void ApplyEndpointView(string? selectName = null, bool browse = true)
    {
        selectName ??= Selected?.Name;
        var rows = CloudEndpointList.Arrange(_loaded, EndpointSearchBox.Text, _endpointSortColumn, _endpointSortDescending);
        _rearranging = true;
        try
        {
            _accounts.Clear();
            foreach (var row in rows) _accounts.Add(row);
            // By item rather than by index, so the selection cannot drift when the order changes.
            // The first survivor is selected when the preferred name is gone — including when there was no
            // preferred name at all: an empty file pane with no visible cause reads as a failure.
            EndpointList.SelectedItem = (selectName is null ? null : rows.FirstOrDefault(row => row.Account.Name == selectName))
                ?? rows.FirstOrDefault();
        }
        finally { _rearranging = false; }

        EndpointSearchBox.ToolTip = rows.Count == _loaded.Count
            ? null
            : $"已按“{EndpointSearchBox.Text.Trim()}”过滤，显示 {rows.Count} / {_loaded.Count} 个端点。";
        // A filter that matches nothing empties the browser too: the list and the file pane are two views of
        // one selection, and the previous endpoint's files beside an empty list read as a stale pane.
        if (rows.Count == 0) { _browsedEndpoint = null; _currentPath = ""; ClearListing(); }
        // The jump lists offer places by endpoint, so they are rebuilt whenever the endpoint list is: a bookmark
        // whose endpoint the search box just hid has to leave the list, and come back when it does.
        RefreshBookmarkLists();
        UpdateEndpointButtons();
        UpdateMountButtons();
        // Decided here rather than left to the selection event: re-arranging may have moved the selection to
        // another endpoint, and the events it raised along the way were about states nobody asked for.
        if (browse) _ = FollowSelectionAsync();
    }

    /// <summary>
    /// Points the browser at whatever the list now has selected. Re-listing is a network round trip to the
    /// remote service, so it happens only when the endpoint actually changed.
    /// </summary>
    private async Task FollowSelectionAsync()
    {
        if (!SwitchEndpoint(Selected)) return;
        await BrowseAsync();
    }

    /// <summary>
    /// Adopts the endpoint the caller names and says whether that was a change: its root becomes the current
    /// directory and the per-endpoint history starts again. It lists nothing, which is what lets a jump that
    /// already knows the directory it wants adopt the endpoint and ask for that one directory rather than paying
    /// for the root first.
    /// </summary>
    private bool SwitchEndpoint(CloudEndpointAccount? account)
    {
        if (account?.Name == _browsedEndpoint) return false;
        _browsedEndpoint = account?.Name;
        _currentPath = account?.RootPath ?? "";
        // History is per endpoint: "the directory before this one" means nothing across two accounts.
        _history.Clear();
        _historyIndex = -1;
        if (account is not null) PushHistory(_currentPath);
        return true;
    }

    private async Task RefreshMountsAsync()
    {
        try
        {
            MountStatus.Text = "正在扫描系统全部 rclone 挂载…";
            var mounts = await _mountService.ScanAsync();
            _mounts.Clear();
            foreach (var mount in mounts) _mounts.Add(mount);
            MountStatus.Text = _mounts.Count == 0 ? "当前系统没有 rclone 挂载。" : $"共发现 {_mounts.Count} 个挂载，其中 {_mounts.Count(m => m.Origin == MountOrigin.FengSyncManaged)} 个由本应用启动。";
        }
        catch (Exception ex) { MountStatus.Text = "扫描挂载失败：" + RcloneUiError.Describe(ex, "mount-scan"); }
        UpdateMountButtons();
    }

    private void UpdateMountButtons()
    {
        MountSelectedEndpointButton.IsEnabled = Selected is not null && !_mountBusy;
        UnmountMountButton.IsEnabled = SelectedMount?.CanUnmount == true && !_mountBusy;
        RefreshMountsButton.IsEnabled = !_mountBusy;
    }

    private async void EndpointList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateEndpointButtons();
        UpdateMountButtons();
        // A re-arrange raises this event several times over, with a selection that is momentarily nothing at
        // all. Those are not gestures, and the re-arrange follows up on its own once it is finished.
        if (_rearranging || _applyingJump) return;
        await FollowSelectionAsync();
    }

    private void MountsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMountButtons();

    /// <summary>
    /// Keeps the endpoint actions in step with the selection. Only a provider whose descriptor requires a
    /// browser authorization can be re-authorized, so the button is hidden for the rest rather than failing
    /// after the click.
    /// </summary>
    private void UpdateEndpointButtons()
    {
        EditButton.IsEnabled = Selected is not null;
        CheckButton.IsEnabled = Selected is not null;
        RenameEndpointButton.IsEnabled = Selected is not null;
        OrganizeEndpointButton.IsEnabled = Selected is not null;
        // Follows the rows on screen rather than everything that was loaded: the button sits beside the
        // search box, and its status line reports how many it probed, so a filter that is not applied would
        // make the two disagree.
        CheckAllButton.IsEnabled = _accounts.Count > 0;
        ReconnectButton.Visibility = NeedsReconnect(Selected) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Split out from the button update so the rule is testable without a window: an endpoint whose provider
    /// is fully described by its stored parameters has nothing to re-authorize, so re-login would be a dead
    /// end for it. The answer comes from the provider descriptor, which is what makes OneDrive, Dropbox and
    /// Box behave like Drive here without a second list to keep in step.
    /// </summary>
    internal static bool NeedsReconnect(CloudEndpointAccount? account) =>
        account is not null && CloudProviderCatalog.RequiresOAuth(KindOf(account));

    /// <summary>The flavour of an endpoint, which decides whether a delete can be undone.</summary>
    internal static EndpointType KindOf(CloudEndpointAccount? account) =>
        account is null ? EndpointType.Sftp : CloudEndpointService.KindFromRcloneType(account.Type);

    /// <summary>What the status line says after a check, kept separate so the wording is testable.</summary>
    internal static string DescribeStatus(CloudEndpointRow row) => row.Status.Health switch
    {
        CloudEndpointHealth.Connected when row.Status.QuotaText.Length > 0 => $"“{row.Account.Name}”连接正常，{row.Status.QuotaText}。",
        CloudEndpointHealth.Connected => $"“{row.Account.Name}”连接正常；该服务不提供容量信息。",
        _ => $"“{row.Account.Name}”{row.Status.HealthText}：{row.Status.Detail}"
    };

    /// <summary>The label under the file list, which names the enforced prefix when there is one.</summary>
    internal static string PathLabel(CloudEndpointAccount account, string path) => account.PrefixIsEnforced
        ? $"{account.Name}:/{path}    （前缀 /{account.Prefix}，由 rclone 强制，无法越界）"
        : $"{account.Name}:/{path}";

    private async void Refresh_Click(object sender, RoutedEventArgs e)
        => await RunBusyAsync(RefreshButton, "正在刷新…", "正在读取云端端点列表…", RefreshAsync);

    /// <summary>
    /// Fills the endpoint ordering choices. Setting <c>SelectedIndex</c> here raises
    /// <see cref="EndpointSort_Changed"/>, which is harmless: the view is re-arranged from state that is
    /// already consistent, and it happens before any endpoint has been loaded.
    /// </summary>
    private void BuildEndpointSortOptions()
    {
        foreach (var (_, label) in EndpointSortOptions) EndpointSortBox.Items.Add(label);
        EndpointSortBox.SelectedIndex = 0;
    }

    private void EndpointSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyEndpointView();

    private void EndpointSort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EndpointSortBox.SelectedIndex < 0 || EndpointSortBox.SelectedIndex >= EndpointSortOptions.Length) return;
        _endpointSortColumn = EndpointSortOptions[EndpointSortBox.SelectedIndex].Column;
        ApplyEndpointView();
    }

    private void EndpointSortDirection_Click(object sender, RoutedEventArgs e)
    {
        _endpointSortDescending = !_endpointSortDescending;
        EndpointSortDirectionButton.Content = _endpointSortDescending ? "降序" : "升序";
        ApplyEndpointView();
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(NewButton, "正在新建…", "正在打开新建端点窗口…", async () =>
        {
            var editor = new CloudEndpointEditorWindow { Owner = this };
            if (editor.ShowDialog() != true || editor.SavedRemoteName is null) return;
            await RefreshAsync();
            SelectEndpoint(editor.SavedRemoteName);
            StatusText.Text = $"已创建端点：{editor.SavedRemoteName}。";
        });
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not CloudEndpointAccount account) { StatusText.Text = "请先选择要编辑的端点。"; return; }
        await RunBusyAsync(EditButton, "正在打开…", "正在读取端点参数…", async () =>
        {
            // Read the parameters before opening the form: an edit form must show the settings rclone
            // actually holds, not the subset the endpoint list displays.
            var target = await CloudEndpointService.LoadEditTargetAsync(account);
            var editor = new CloudEndpointEditorWindow(target) { Owner = this };
            if (editor.ShowDialog() != true || editor.SavedRemoteName is null) return;
            await RefreshAsync();
            SelectEndpoint(editor.SavedRemoteName);
            StatusText.Text = $"已更新端点：{editor.SavedRemoteName}。";
        });
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not CloudEndpointAccount account) { StatusText.Text = "请先选择一个端点。"; return; }
        var kind = KindOf(account);
        if (!CloudProviderCatalog.RequiresOAuth(kind))
        { StatusText.Text = $"只有需要浏览器授权的端点可以重新登录；{CloudEndpointService.DisplayName(kind)} 请直接编辑端点参数。"; return; }
        await RunBusyAsync(ReconnectButton, "正在登录…", "正在重新登录，请在浏览器完成授权…", async () =>
        {
            var progress = new Progress<string>(message => StatusText.Text = message);
            await CloudEndpointService.ReconnectAsync(kind, account.Name, progress);
            await RefreshAsync();
            StatusText.Text = $"“{account.Name}”已重新登录。";
        });
    }

    /// <summary>
    /// Asks the endpoint to list its own root and to report its quota, then records the answer on the row so
    /// the list keeps showing it. This is the only place a stale Google Drive token becomes visible before a
    /// transfer fails on it.
    /// </summary>
    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (EndpointList.SelectedItem is not CloudEndpointRow row) { StatusText.Text = "请先选择要检查的端点。"; return; }
        await RunBusyAsync(CheckButton, "正在检查…", $"正在检查“{row.Account.Name}”…", async () =>
        {
            row.Status = new CloudEndpointStatus(CloudEndpointHealth.Checking, "");
            row.Status = await CloudEndpointStatusService.ProbeAsync(row.Account);
            StatusText.Text = DescribeStatus(row);
        });
    }

    /// <summary>
    /// Renames an endpoint. The prompt validates against a snapshot taken before it opens, so a colliding
    /// name is refused inline rather than after a rename attempt has already touched rclone.conf.
    /// </summary>
    private async void RenameEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not CloudEndpointAccount account) { StatusText.Text = "请先选择要重命名的端点。"; return; }
        await RunBusyAsync(RenameEndpointButton, "正在重命名…", "正在读取挂载状态…", async () =>
        {
            // Scanned fresh rather than reused from the list: a mount that appeared since the last refresh
            // would be stranded by the rename, and the check is the only thing standing in the way.
            var mounted = (await _mountService.ScanAsync()).Select(mount => mount.RemoteName).ToList();
            var context = await CloudEndpointRename.InspectAsync(mounted);
            var requested = TextPrompt.Ask(this, "重命名端点",
                $"端点“{account.Name}”的新名称（只能包含字母、数字、下划线或连字符）：",
                account.Name,
                text => CloudEndpointRename.Validate(text, account.Name, context));
            if (requested is null) return;
            var result = await CloudEndpointRename.RenameAsync(account.Name, requested, mounted);
            // Re-read before anything can write. The rename moved the remembered places on disk, but the lists in
            // memory still name a remote rclone no longer has, and the refresh below starts a listing that records
            // a visit — a visit written from the stale lists would quietly undo the migration the rename just made.
            await LoadBookmarksAsync();
            await RefreshAsync();
            SelectEndpoint(result.NewName);
            StatusText.Text = DescribeRename(result);
        });
    }

    /// <summary>
    /// Files the selected endpoint under a group and gives it tags. Both live in this application's metadata
    /// file, so the row is updated in place rather than reloaded: a reload would throw away the connection
    /// status and quota a check had already paid for.
    /// </summary>
    private async void OrganizeEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not CloudEndpointAccount account) { StatusText.Text = "请先选择要设置分组与标签的端点。"; return; }
        var row = _accounts.FirstOrDefault(candidate => candidate.Account.Name == account.Name);
        if (row is null) return;
        await RunBusyAsync(OrganizeEndpointButton, "正在保存…", "正在保存分组与标签…", async () =>
        {
            var dialog = new CloudEndpointOrganizationWindow(account.Name, account.Metadata?.Group,
                account.Metadata?.Tags, _loaded.Select(other => other.Group).Where(group => group.Length > 0)) { Owner = this };
            if (dialog.ShowDialog() != true) return;

            var store = new CloudEndpointMetadataStore();
            var metadata = CloudEndpointOrganization.Merge(await store.FindAsync(account.Name), account, dialog.Group, dialog.Tags);
            await store.UpsertAsync(metadata);
            row.AdoptMetadata(metadata);
            // Re-arranged because the group decides where the row belongs; without it the list would keep
            // showing the endpoint under its previous heading.
            ApplyEndpointView(account.Name, browse: false);
            StatusText.Text = CloudEndpointOrganization.Describe(account.Name, metadata.Group, metadata.Tags);
        });
    }

    /// <summary>
    /// Points the list at one endpoint by name. By item rather than by index, because grouping makes the
    /// selector's own items the group headings.
    /// </summary>
    private void SelectEndpoint(string name)
    {
        if (_accounts.FirstOrDefault(row => row.Account.Name == name) is { } row) EndpointList.SelectedItem = row;
    }

    /// <summary>What the status line says after a rename, kept separate so the wording is testable.</summary>
    internal static string DescribeRename(CloudEndpointRenameResult result) => result.RewrittenProfiles == 0
        ? $"已将端点“{result.OldName}”重命名为“{result.NewName}”。"
        : $"已将端点“{result.OldName}”重命名为“{result.NewName}”，并更新了 {result.RewrittenProfiles} 个同步任务的路径。";

    /// <summary>
    /// Probes every endpoint the list is showing, bounded so a long list does not fire one request per
    /// endpoint at once. Results land on the rows as they arrive, which is what makes the run useful on a
    /// slow list instead of only at the end.
    /// </summary>
    private async void CheckAll_Click(object sender, RoutedEventArgs e)
    {
        // A snapshot: the search box stays live during the run, and a filter that changes mid-flight must not
        // make the progress readout or the summary count a different set than the one that was probed.
        var rows = _accounts.ToList();
        if (rows.Count == 0) { StatusText.Text = DescribeCheckAll(0, 0, 0); return; }
        await RunBusyAsync(CheckAllButton, "正在检查…", $"正在检查 {rows.Count} 个端点…", async () =>
        {
            foreach (var row in rows) row.Status = new CloudEndpointStatus(CloudEndpointHealth.Checking, "");
            var done = 0;
            var failures = await CloudEndpointStatusService.RunBoundedAsync(
                rows, CloudEndpointStatusService.ProbeConcurrency, async row =>
                {
                    var status = await CloudEndpointStatusService.ProbeAsync(row.Account);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        row.Status = status;
                        done++;
                        StatusText.Text = $"正在检查… 已完成 {done}/{rows.Count}";
                    });
                });
            var connected = rows.Count(row => row.Health == CloudEndpointHealth.Connected);
            StatusText.Text = DescribeCheckAll(rows.Count, connected, failures.Count);
        });
    }

    /// <summary>
    /// What the status line says after checking every endpoint. The three outcomes are worded apart on
    /// purpose: "not connected" and "could not be checked at all" need different next steps, and a run where
    /// nothing answered must not read like a success.
    /// </summary>
    internal static string DescribeCheckAll(int total, int connected, int failed) => (total, connected, failed) switch
    {
        (0, _, _) => "没有可检查的端点。",
        (_, _, > 0) => $"检查结束：{connected}/{total} 个端点连接正常，{failed} 个未能完成检查。",
        var (count, all, _) when all == count => $"检查结束：{count} 个端点全部连接正常。",
        var (count, none, _) when none == 0 => $"检查结束：{count} 个端点都无法连接，请检查网络或端点参数。",
        var (count, some, _) => $"检查结束：{some}/{count} 个端点连接正常，其余需要重新登录或无法连接。"
    };

    /// <summary>
    /// Writes every endpoint's non-secret configuration to a file. Parameter values are never read — only
    /// their names — so no credential can leave <c>rclone.conf</c>.
    /// </summary>
    private async void ExportEndpoints_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(ExportEndpointsButton, "正在导出…", "正在读取端点配置…", async () =>
        {
            var accounts = await CloudEndpointService.LoadEndpointAccountsAsync();
            if (accounts.Count == 0) { StatusText.Text = "没有可导出的端点。"; return; }
            var dialog = new SaveFileDialog
            {
                Title = "导出云盘端点",
                FileName = "fengsync-endpoints.json",
                DefaultExt = ".json",
                Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) != true) return;
            var bundle = await CloudEndpointService.BuildExportBundleAsync(accounts, DateTimeOffset.Now);
            await File.WriteAllTextAsync(dialog.FileName, CloudEndpointPortability.Serialize(bundle));
            StatusText.Text = $"已导出 {bundle.Endpoints.Count} 个端点的非敏感配置；凭据未写入文件。";
        });
    }

    /// <summary>
    /// Restores endpoints from an export file. Each one opens the normal editor with its non-secret values
    /// pre-filled, so the user supplies the credentials and the same validation and create path run as for a
    /// hand-entered endpoint. A name already in use is never overwritten: the newcomer is renamed.
    /// </summary>
    private async void ImportEndpoints_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入云盘端点",
            DefaultExt = ".json",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        string json;
        try { json = await File.ReadAllTextAsync(dialog.FileName); }
        catch (Exception ex) { StatusText.Text = "无法读取文件：" + ex.Message; return; }

        var import = CloudEndpointPortability.Parse(json);
        if (import.Errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", import.Errors), "导入文件有问题", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (import.Endpoints.Count == 0) { StatusText.Text = "导出文件里没有端点。"; return; }
        if (MessageBox.Show(this,
                $"将导入 {import.Endpoints.Count} 个端点。每个端点都需要补填凭据，填写后取消的会被跳过。\n\n继续吗？",
                "导入云盘端点", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        await RunBusyAsync(ImportEndpointsButton, "正在导入…", "正在打开导入向导…", async () =>
        {
            var taken = (await CloudEndpointService.LoadEndpointAccountsAsync())
                .Select(account => account.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var imported = 0;
            var skipped = 0;
            foreach (var draft in import.Endpoints)
            {
                var name = CloudEndpointPortability.UniqueName(draft.RemoteName, taken);
                var editor = new CloudEndpointEditorWindow(draft with { RemoteName = name }) { Owner = this };
                if (editor.ShowDialog() != true || editor.SavedRemoteName is null) { skipped++; continue; }
                await CloudEndpointService.SaveOrganizationAsync(editor.SavedRemoteName, draft.Group, draft.Tags);
                taken.Add(editor.SavedRemoteName);
                imported++;
            }
            await RefreshAsync();
            StatusText.Text = $"导入完成：{imported} 个成功，{skipped} 个跳过。";
        });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not CloudEndpointAccount account) { StatusText.Text = "请先选择要删除的端点。"; return; }
        var relatedMounts = _mounts.Where(m => string.Equals(m.RemoteName, account.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (relatedMounts.Count > 0)
        {
            var detail = string.Join("\n", relatedMounts.Select(m => " · " + m.Display));
            if (!TextPrompt.Confirm(this, "删除端点", $"端点“{account.Name}”仍有 {relatedMounts.Count} 个挂载：\n{detail}\n\n继续删除端点？", danger: true)) return;
        }
        else if (!TextPrompt.Confirm(this, "删除端点", $"删除云端端点“{account.Name}”？本地 Profile 不会被删除。", danger: true)) return;
        await RunBusyAsync(DeleteButton, "正在删除…", $"正在删除端点“{account.Name}”…", async () => { await CloudEndpointService.DeleteAsync(account.Name); await RefreshAsync(); StatusText.Text = $"已删除端点：{account.Name}。"; });
    }

    private async Task RunBusyAsync(Button button, string busyText, string status, Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        var original = button.Content;
        try { button.IsEnabled = false; button.Content = busyText; StatusText.Text = status; await action(); }
        catch (Exception ex)
        {
            var message = RcloneUiError.Describe(ex, "endpoint-manager-action");
            StatusText.Text = message;
            MessageBox.Show(message, "远程端点", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { button.IsEnabled = true; button.Content = original; _busy = false; }
    }

    private async void RefreshMounts_Click(object sender, RoutedEventArgs e)
    {
        if (_mountBusy) return;
        _mountBusy = true;
        UpdateMountButtons();
        try { await RefreshMountsAsync(); }
        catch (Exception ex) { MountStatus.Text = "刷新挂载失败：" + RcloneUiError.Describe(ex, "mount-refresh"); }
        finally { _mountBusy = false; UpdateMountButtons(); }
    }

    private async void MountSelectedEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not CloudEndpointAccount account) { MountStatus.Text = "请先选择要挂载的端点。"; return; }
        if (_mountBusy) return;
        var provider = CloudEndpointService.DisplayName(CloudEndpointService.KindFromRcloneType(account.Type));
        var dialog = new MountPickerDialog(account.Name, provider, _mounts.Select(m => m.MountPoint).ToList(), account.RootPath, account.Prefix) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var target = dialog.SelectedTarget!;
        _mountBusy = true;
        UpdateMountButtons();
        MountStatus.Text = $"正在挂载 {account.Name} → {target.MountPoint}…";
        try
        {
            await _mountService.MountAsync(target);
            await RefreshMountsAsync();
            MountStatus.Text = $"已启动挂载：{target.RemoteName} → {target.MountPoint}。";
        }
        catch (Exception ex)
        {
            var message = RcloneUiError.Describe(ex, "mount-create");
            MountStatus.Text = "挂载失败：" + message;
            MessageBox.Show(message, "挂载", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _mountBusy = false; UpdateMountButtons(); }
    }

    private async void UnmountMount_Click(object sender, RoutedEventArgs e)
    {
        var mount = SelectedMount;
        if (mount is null) { MountStatus.Text = "请先选择要取消的挂载。"; return; }
        if (_mountBusy) return;
        if (!mount.CanUnmount)
        {
            MessageBox.Show("该挂载由外部程序管理。Feng Sync 只读显示外部挂载，不会结束其进程或代为卸载。", "取消挂载", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TextPrompt.Confirm(this, "取消挂载", $"确定取消挂载？\n\n{mount.Display}", danger: true)) return;
        _mountBusy = true;
        UpdateMountButtons();
        MountStatus.Text = $"正在取消挂载：{mount.MountPoint}…";
        try
        {
            var result = await _mountService.UnmountAsync(mount);
            if (result.AllStopped) { MountStatus.Text = $"已取消挂载：{mount.MountPoint}。"; }
            else
            {
                var detail = string.Join("\n", result.Failures.Select(f => $" · {f.MountPoint}（PID {f.Pid}）：{f.Reason}"));
                MountStatus.Text = "部分挂载未完成：\n" + detail;
                MessageBox.Show("部分挂载未完成：\n" + detail, "取消挂载", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            await RefreshMountsAsync();
        }
        catch (Exception ex)
        {
            var message = RcloneUiError.Describe(ex, "mount-remove");
            MountStatus.Text = "取消挂载失败：" + message;
            MessageBox.Show(message, "取消挂载", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _mountBusy = false; UpdateMountButtons(); }
    }

    // ---- Browsing ----

    private async Task BrowseAsync()
    {
        if (Selected is not CloudEndpointAccount account) { ClearListing(); return; }
        try
        {
            StatusText.Text = "正在读取目录…";
            var items = await _files.ListAsync(account.Name, _currentPath);
            BackButton.IsEnabled = !_currentPath.Equals(account.RootPath, StringComparison.OrdinalIgnoreCase);
            ShowListing(_currentPath, items);
            // Only a listing that arrived is a visit. A directory that failed to read is remembered by the error
            // in the status line, not as somewhere the user can jump back to.
            await RecordVisitAsync(account.Name, _currentPath);
        }
        catch (Exception ex) { StatusText.Text = "无法读取目录：" + RcloneUiError.Describe(ex, "cloud-file-list"); }
    }

    private void ClearListing()
    {
        _listed.Clear();
        _entries.Clear();
        _listingIsSearchResult = false;
        PathText.Text = "请选择一个云盘";
        AddressBox.Text = "";
        Breadcrumbs.ItemsSource = null;
        ViewSummary.Text = "";
        BackButton.IsEnabled = false;
        UpdateHistoryButtons();
        UpdateFileButtons();
        UpdateFavoriteToggle();
    }

    /// <summary>
    /// Shows a listing the caller already has, and is the single place the browser's view is assembled:
    /// <see cref="BrowseAsync"/> calls it after listing a directory and the search calls it with results.
    /// Keeping the assembly here is what makes filtering, ordering, the address trail and the label
    /// checkable without a live rclone or a shown window.
    /// </summary>
    internal void ShowListing(string path, IEnumerable<CloudFileEntry> entries, bool isSearchResult = false)
    {
        _currentPath = path;
        _listingIsSearchResult = isSearchResult;
        _listed.Clear();
        _listed.AddRange(entries);
        PathText.Text = Selected is { } account ? PathLabel(account, path) : "";
        UpdateBreadcrumbs();
        AddressBox.Text = path;
        ApplyView();
        UpdateFileButtons();
    }

    /// <summary>
    /// Rebuilds the visible rows from the last listing. Filtering and ordering go through
    /// <see cref="CloudBrowse"/> rather than a collection view, so both stay plain functions that can be
    /// tested on their own.
    /// </summary>
    private void ApplyView()
    {
        var filtered = _listingIsSearchResult ? _listed : CloudBrowse.Filter(_listed, SearchBox.Text);
        var visible = CloudBrowse.Sort(filtered, _sortColumn, _sortDescending);
        _entries.Clear();
        foreach (var entry in visible) _entries.Add(entry);
        ViewSummary.Text = _listed.Count == 0
            ? ""
            : visible.Count == _listed.Count ? $"共 {_listed.Count} 项" : $"显示 {visible.Count} / {_listed.Count} 项";
        if (ViewSummary.Text.Length > 0) StatusText.Text = ViewSummary.Text + "。";
        UpdateFileButtons();
        // The rows a search or a sort change left on screen are the only ones worth pulling pictures for.
        RestartTileRound();
    }

    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.ItemsSource = Selected is { } account
            ? CloudBrowse.Breadcrumbs(account.RootPath, _currentPath, account.Name)
            : null;
    }

    private void UpdateFileButtons()
    {
        var selected = SelectedEntries();
        NewFolderButton.IsEnabled = Selected is not null;
        RenameButton.IsEnabled = selected.Count == 1;
        DeleteEntryButton.IsEnabled = selected.Count > 0;
        RecycleButton.IsEnabled = selected.Count > 0;
        // 还原 and 清空回收站 appear only in a trash view. Outside that folder the two commands would be aimed at
        // objects that were never recycled, and a permanent third pair of buttons would crowd the row that
        // already holds every other command.
        var inTrash = Selected is { } account && CloudRecycleBin.IsTrashView(account.RootPath, _currentPath);
        RestoreButton.Visibility = inTrash ? Visibility.Visible : Visibility.Collapsed;
        RestoreButton.IsEnabled = inTrash && selected.Count > 0;
        EmptyTrashButton.Visibility = inTrash ? Visibility.Visible : Visibility.Collapsed;
        EmptyTrashButton.IsEnabled = Selected is not null;
        OpenTrashButton.IsEnabled = Selected is not null;
        // A folder is downloaded by entering it and taking its files; the queue's unit is a file.
        DownloadButton.IsEnabled = selected.Count > 0 && selected.All(entry => !entry.IsDirectory);
        // The clipboard survives leaving the directory it came from, which is the point: pasting into another
        // endpoint is what makes this a cloud-to-cloud copy rather than a rename.
        PasteButton.IsEnabled = Selected is not null && _clipboard.Count > 0;
    }

    private IReadOnlyList<CloudFileEntry> SelectedEntries() => [.. FileList.SelectedItems.Cast<CloudFileEntry>()];

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFileButtons();

    // ---- 收藏与最近访问 ----

    /// <summary>
    /// Rebuilds both jump lists from what is remembered and what the endpoint list can offer, and puts the
    /// favourite button in step with the directory on screen. Nothing here changes what is remembered: an endpoint
    /// the search box has hidden takes its favourites out of sight and brings them back, because a place the user
    /// chose is not lost because rclone.conf could not be read once.
    /// </summary>
    private void RefreshBookmarkLists()
    {
        // The rows on screen, not everything loaded, because that is the set <see cref="JumpToAsync"/> can select
        // from: an endpoint the search box has hidden has no row to click, so offering its favourites would be a
        // list of rows that refuse to jump.
        var names = _accounts.Select(row => row.Account.Name).ToList();
        FavoriteJumpBox.ItemsSource = CloudBrowserBookmarks.JumpItems(_favorites, names);
        RecentJumpBox.ItemsSource = CloudBrowserBookmarks.JumpItems(_recent, names);
        DescribeHidden(FavoriteJumpBox, _favorites, names, "收藏");
        DescribeHidden(RecentJumpBox, _recent, names, "最近访问");
        UpdateFavoriteToggle();
    }

    /// <summary>
    /// Says how many remembered places the list had to leave out. A row count that fell from six to two is
    /// otherwise indistinguishable from a list that lost four of them for good.
    /// </summary>
    private static void DescribeHidden(ComboBox box, IReadOnlyList<CloudBookmark> places, IReadOnlyCollection<string> names, string what)
    {
        var hidden = CloudBrowserBookmarks.HiddenCount(places, names);
        box.ToolTip = hidden == 0 ? null : $"已隐藏 {hidden} 条{what}，因为对应端点当前不在列表里。";
    }

    /// <summary>
    /// The button says what clicking it does now. That is why it has no static accessible name in the XAML: a
    /// fixed label would replace the state it reports, for a screen reader and for the desktop scenario alike.
    /// </summary>
    private void UpdateFavoriteToggle()
    {
        var place = CurrentPlace();
        FavoriteToggleButton.IsEnabled = place is not null;
        FavoriteToggleButton.Content = place is not null && CloudBrowserBookmarks.IsFavorite(_favorites, place)
            ? "取消收藏"
            : "收藏此目录";
    }

    /// <summary>The directory on screen as a place worth remembering, or nothing when there is nothing to remember.</summary>
    private CloudBookmark? CurrentPlace() => Selected is { } account
        ? CloudBrowserBookmarks.Normalize(account.Name, _currentPath)
        : null;

    private async void FavoriteToggle_Click(object sender, RoutedEventArgs e) => await ToggleFavoriteAsync();

    /// <summary>Keeps or drops the directory on screen, and writes the file both lists share.</summary>
    internal async Task ToggleFavoriteAsync()
    {
        if (CurrentPlace() is not { } place)
        {
            StatusText.Text = "请先选择一个云盘的目录。";
            return;
        }

        _favorites = CloudBrowserBookmarks.IsFavorite(_favorites, place)
            ? [.. CloudBrowserBookmarks.RemoveFavorite(_favorites, place)]
            : [.. CloudBrowserBookmarks.AddFavorite(_favorites, place)];
        RefreshBookmarkLists();
        await SaveBookmarksAsync();
    }

    /// <summary>
    /// Remembers a directory the browser just listed. Re-listing the place the window already shows — a 刷新, a
    /// 后退, a search that fell back to a plain listing — is not a new visit, and without that gate each of them
    /// would rewrite the file for the same answer.
    /// </summary>
    private async Task RecordVisitAsync(string remote, string path)
    {
        if (CloudBrowserBookmarks.Normalize(remote, path) is not { } place) return;
        if (CloudBrowserBookmarks.IsNewestVisit(_recent, place)) return;
        _recent = [.. CloudBrowserBookmarks.RecordVisit(_recent, place)];
        RefreshBookmarkLists();
        await SaveBookmarksAsync();
    }

    private async Task SaveBookmarksAsync()
    {
        try { await _bookmarks.SaveAsync(_favorites, _recent); }
        catch (Exception ex) { StatusText.Text = "无法保存收藏与最近访问：" + RcloneUiError.Describe(ex, "cloud-bookmarks-save"); }
    }

    private async void BookmarkJump_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingJump || sender is not ComboBox { SelectedItem: CloudJumpItem item } box) return;
        await JumpToAsync(item);
        // The pick named a destination, not a filter, so it is put back: leaving a row selected would read as
        // though the browser were showing only that place, and would leave nothing to pick the second time.
        _applyingJump = true;
        try { box.SelectedItem = null; }
        finally { _applyingJump = false; }
    }

    /// <summary>
    /// Jumps to a remembered place: the endpoint first, then the directory inside it. Going through the endpoint
    /// list's own selection is what makes the browser's rules apply — its root, its history, its address trail —
    /// and the guard around it is why the selection event does not list anything: two listings of one endpoint
    /// would otherwise race for the file pane, and whichever answered last would win.
    /// </summary>
    internal async Task<bool> JumpToAsync(CloudJumpItem item)
    {
        var row = _accounts.FirstOrDefault(candidate =>
            string.Equals(candidate.Account.Name, item.Bookmark.Remote, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            // The jump list only offers places whose endpoint this window loaded, so arriving here means it went
            // away between the pick and this line — a refresh, or a keystroke in the search box.
            StatusText.Text = $"端点“{item.Bookmark.Remote}”已不在列表里，无法跳转。";
            return false;
        }

        _applyingJump = true;
        try { EndpointList.SelectedItem = row; }
        finally { _applyingJump = false; }
        SwitchEndpoint(row.Account);
        await NavigateToAsync(item.Bookmark.Path);
        return true;
    }

    // ---- 视图与缩略图 ----

    /// <summary>Which drawing the browser is currently using for its rows.</summary>
    internal CloudBrowserViewMode BrowserView => _browserView;

    /// <summary>
    /// Completes once every tile the current listing asked for has been pulled or refused, and the pictures that
    /// arrived have been painted. An await on this is how a test knows the grid is finished rather than guessing
    /// how long two bounded reads take.
    /// </summary>
    internal Task PendingThumbnails => _tileWait;

    /// <summary>
    /// The picture the grid would paint for this row right now, which is exactly what a realised tile asks the
    /// converter for. A window that was never shown has no realised rows, so this is how a test checks the bytes
    /// reached the drawing without measuring pixels.
    /// </summary>
    internal ImageSource? TileFor(CloudFileEntry entry) => _tileConverter.Peek?.Invoke(entry);

    private void ViewToggle_Click(object sender, RoutedEventArgs e) => _ = SetBrowserViewAsync(
        _browserView == CloudBrowserViewMode.Grid ? CloudBrowserViewMode.List : CloudBrowserViewMode.Grid);

    /// <summary>
    /// Switches between 列表视图 and 网格视图 and remembers the choice. The rows are not rebuilt: both drawings
    /// read the same <see cref="_entries"/>, so 全选, the row menu and the clipboard keep aiming at exactly the
    /// rows that were on screen before the switch.
    /// </summary>
    internal async Task SetBrowserViewAsync(CloudBrowserViewMode view)
    {
        // Normalised because the value reaches this window from a settings file as well as from the button, and a
        // number that names no view must not be painted — or written back — as though it did.
        var chosen = Enum.IsDefined(view) ? view : CloudBrowserViewMode.List;
        if (chosen == _browserView) return;
        _browserView = chosen;
        ApplyBrowserView();
        try
        {
            // Read-modify-write instead of saving this window's own snapshot: the settings file also holds the main
            // window's sidebar width and every other shell's choices, so writing back what was loaded when this
            // window opened would quietly undo whatever changed since.
            var store = new SettingsStore();
            var loaded = await store.LoadAsync();
            _settings = loaded.Settings with { CloudBrowserView = chosen };
            await store.SaveAsync(_settings);
        }
        catch (Exception ex) { StatusText.Text = "无法记住视图选择：" + RcloneUiError.Describe(ex, "cloud-view-choice"); }
    }

    /// <summary>
    /// Points the one list control at the drawing the current view asks for. Nothing else changes: the items
    /// source, the selection, the search filter and the sort all stay exactly where they were, which is the whole
    /// reason the toggle goes through <see cref="ListView.View"/> rather than a second control or a second
    /// collection.
    /// </summary>
    private void ApplyBrowserView()
    {
        var grid = _browserView == CloudBrowserViewMode.Grid;
        FileList.View = grid ? null : _listView;
        if (grid)
        {
            FileList.ItemTemplate = (DataTemplate)Resources["CloudTileTemplate"];
            FileList.ItemsPanel = (ItemsPanelTemplate)Resources["CloudTilePanel"];
        }
        else
        {
            // Cleared, not set to null. A bound GridView writes no local row template or panel of its own (it
            // renders through the ListView's own style), so the list view is restored by removing this window's
            // overrides — and `ItemsPanel = null` is a local null rather than "unset", which measured against a
            // shown window stops every row being presented at all: the list is empty, the rows are still there,
            // and only the real shell can see it.
            FileList.ClearValue(ItemsControl.ItemTemplateProperty);
            FileList.ClearValue(ItemsControl.ItemsPanelProperty);
        }
        ViewToggleButton.Content = grid ? "列表视图" : "网格视图";
        RestartTileRound();
    }

    /// <summary>
    /// Starts the tile reads the current rows and view call for, and stops the ones the previous listing or the
    /// previous view started. Called from <see cref="ApplyView"/>, so a search or a sort change re-aims the reads
    /// at the rows that are actually visible instead of going on feeding pictures to rows that scrolled away.
    /// </summary>
    private void RestartTileRound()
    {
        // Not disposed: a cancelled source can still be registered by a read on its way out, and disposing it
        // there would throw inside that read rather than cancel it.
        _tileRound?.Cancel();
        if (_browserView != CloudBrowserViewMode.Grid || Selected is not { } account)
        {
            _tileWait = Task.CompletedTask;
            return;
        }
        var round = new CancellationTokenSource();
        _tileRound = round;
        _tileWait = PaintTilesAsync(account.Name, _entries.Where(CloudBrowserTiles.OffersThumbnail).ToList(), round);
    }

    private async Task PaintTilesAsync(string remote, IReadOnlyList<CloudFileEntry> rows, CancellationTokenSource round)
    {
        try
        {
            await Task.WhenAll(rows.Select(entry => PaintOneTileAsync(remote, entry, round.Token))).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Net.Http.HttpRequestException or OperationCanceledException)
        {
            // A tile round that fell over leaves the rows drawn with their file icons, which is the fallback every
            // refusal in CloudBrowserTiles already describes. The listing itself is not at fault and stays up.
        }
        // One final repaint when the round is over, so an await on PendingThumbnails guarantees the arrived
        // pictures are on screen; the coalesced repaints along the way are only what makes them appear one by one.
        if (round.IsCancellationRequested) return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (_browserView == CloudBrowserViewMode.Grid) FileList.Items.Refresh();
        }).Task.ConfigureAwait(false);
    }

    private async Task PaintOneTileAsync(string remote, CloudFileEntry entry, CancellationToken ct)
    {
        try
        {
            if (await _thumbnails.LoadAsync(remote, entry, ct).ConfigureAwait(false)) QueueTileRefresh();
        }
        catch (OperationCanceledException) { }
    }

    private void QueueTileRefresh()
    {
        if (_tileRefreshQueued) return;
        _tileRefreshQueued = true;
        // Coalesced rather than one repaint per picture: in a folder of photos the reads land a few milliseconds
        // apart, and re-drawing every realised row for each of them is work nobody gets to see.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _tileRefreshQueued = false;
            if (_browserView == CloudBrowserViewMode.Grid) FileList.Items.Refresh();
        }), DispatcherPriority.Background);
    }

    // ---- 键盘快捷键 ----

    /// <summary>
    /// The rename question, behind a seam because a test cannot answer a modal. It hands back the new name, or
    /// <see langword="null"/> for "cancelled" — the same contract every other seam here uses, so a test that
    /// means "the user typed report.txt" cannot be read as one that means "the user closed the dialog".
    /// </summary>
    internal Func<CloudFileEntry, string?>? PromptRename { get; set; }

    /// <summary>
    /// Decides what a keystroke in the browser pane means and runs the command it names. The four arguments are
    /// the facts the XAML handler reads off the event and off <see cref="Keyboard"/>: a test cannot put Ctrl on
    /// the physical keyboard that <see cref="Keyboard.Modifiers"/> reports, so it hands the modifiers in and
    /// still exercises the real table and the real dispatch. Returns true when the keystroke was the browser's
    /// own, which is the only case its handler may claim.
    /// </summary>
    internal async Task<bool> HandleKeyAsync(Key key, bool ctrl, bool shift, bool typing)
    {
        if (CloudBrowserShortcuts.Resolve(key, ctrl, shift, typing) is not { } command) return false;
        switch (command)
        {
            case CloudBrowserShortcut.Refresh: await BrowseAsync(); break;
            case CloudBrowserShortcut.Recycle: await RecycleSelectedAsync(); break;
            case CloudBrowserShortcut.DeleteForever: await DeleteSelectedAsync(); break;
            case CloudBrowserShortcut.Rename: await RenameSelectedAsync(); break;
            // SelectAll goes through the list itself, so the rows it picks are the ones the search box and the
            // sort left on screen — Ctrl+A that ignored an active filter would select what the user cannot see.
            case CloudBrowserShortcut.SelectAll: FileList.SelectAll(); break;
            case CloudBrowserShortcut.Copy: SetClipboard(CloudClipboardMode.Copy, SelectedEntries()); break;
            case CloudBrowserShortcut.Cut: SetClipboard(CloudClipboardMode.Cut, SelectedEntries()); break;
            case CloudBrowserShortcut.Paste: await PasteAsync(); break;
        }
        return true;
    }

    private async void FileBrowser_KeyDown(object sender, KeyEventArgs e)
    {
        if (await HandleKeyAsync(e.Key, Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), IsTyping(e))) e.Handled = true;
    }

    /// <summary>
    /// True when the keystroke belongs to a text field rather than to the list. Real input and a test's raised
    /// event both report the field as <see cref="RoutedEventArgs.OriginalSource"/>, which is the one fact the
    /// gate needs: a TextBox consumes Del and Ctrl+A before they ever bubble here, but not F2, so without this
    /// check a rename question would open while the user is mid-address.
    /// </summary>
    private static bool IsTyping(KeyEventArgs e) => e.OriginalSource is TextBox;

    private void UpdateHistoryButtons()
    {
        HistoryBackButton.IsEnabled = _historyIndex > 0;
        HistoryForwardButton.IsEnabled = _historyIndex >= 0 && _historyIndex + 1 < _history.Count;
    }

    /// <summary>
    /// Moves to a directory and records it. <paramref name="record"/> is false when the move came from
    /// 后退 or 前进 itself, which must not push a new entry or the two would ping-pong forever.
    /// </summary>
    internal async Task NavigateToAsync(string path, bool record = true)
    {
        if (Selected is not CloudEndpointAccount account) { StatusText.Text = "请先选择云盘。"; return; }
        var target = CloudBrowse.Normalize(path, account.RootPath);
        if (record) PushHistory(target);
        _currentPath = target;
        await BrowseAsync();
    }

    private void PushHistory(string path)
    {
        if (_historyIndex >= 0 && _historyIndex < _history.Count && _history[_historyIndex] == path) return;
        // A new move made after going back discards the forward entries, exactly as a browser does.
        if (_historyIndex + 1 < _history.Count) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(path);
        _historyIndex = _history.Count - 1;
        UpdateHistoryButtons();
    }

    private async void HistoryBack_Click(object sender, RoutedEventArgs e)
    {
        if (_historyIndex <= 0) return;
        _historyIndex--;
        _currentPath = _history[_historyIndex];
        UpdateHistoryButtons();
        await BrowseAsync();
    }

    private async void HistoryForward_Click(object sender, RoutedEventArgs e)
    {
        if (_historyIndex < 0 || _historyIndex + 1 >= _history.Count) return;
        _historyIndex++;
        _currentPath = _history[_historyIndex];
        UpdateHistoryButtons();
        await BrowseAsync();
    }

    private async void Breadcrumb_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path) return;
        await NavigateToAsync(path);
    }

    private async void Go_Click(object sender, RoutedEventArgs e) => await NavigateToAsync(AddressBox.Text);

    private async void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await NavigateToAsync(AddressBox.Text);
    }

    private async void BrowseRefresh_Click(object sender, RoutedEventArgs e) => await BrowseAsync();

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not CloudEndpointAccount account || _currentPath.Equals(account.RootPath, StringComparison.OrdinalIgnoreCase)) return;
        await NavigateToAsync(CloudFileManagerService.Parent(_currentPath));
    }

    private async void FileList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is not CloudFileEntry entry) return;
        if (entry.IsDirectory) { await NavigateToAsync(entry.Path); return; }
        // A row the browser can paint is shown in place; everything else keeps the route it always had — pull it
        // into a temporary folder and hand it to the program the system associates with the format.
        if (CloudBrowserPreview.OffersPreview(entry)) await ShowPreviewAsync(entry);
        else await OpenAsync(entry);
    }

    // ---- Ordering and search ----

    private void BuildSortOptions()
    {
        foreach (var (_, label) in SortOptions) SortBox.Items.Add(label);
        SortBox.SelectedIndex = 0;
    }

    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedIndex < 0 || SortBox.SelectedIndex >= SortOptions.Length) return;
        _sortColumn = SortOptions[SortBox.SelectedIndex].Column;
        ApplyView();
    }

    private void SortDirection_Click(object sender, RoutedEventArgs e)
    {
        _sortDescending = !_sortDescending;
        SortDirectionButton.Content = _sortDescending ? "降序" : "升序";
        ApplyView();
    }

    /// <summary>
    /// Orders by the clicked column. Clicking the column already in use flips the direction, which is what
    /// every file manager does and the only way a single click can express both.
    /// </summary>
    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Role == GridViewColumnHeaderRole.Padding) return;
        // The header's own text lives on its column; GridViewColumnHeader itself only exposes Column/Role.
        var index = (header.Column?.Header as string) switch { "名称" => 0, "大小" => 1, "修改时间" => 2, _ => -1 };
        if (index < 0) return;
        if (SortOptions[index].Column == _sortColumn) _sortDescending = !_sortDescending;
        else { _sortColumn = SortOptions[index].Column; _sortDescending = false; }
        SortBox.SelectedIndex = index;
        SortDirectionButton.Content = _sortDescending ? "降序" : "升序";
        ApplyView();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyView();

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunSearchAsync();
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await RunSearchAsync();

    /// <summary>
    /// Runs a recursive search when the user asked for one, and otherwise re-lists: with no query there is
    /// nothing to search for, and without the recursive switch the plain listing is already filtered live
    /// by the search box, so doing nothing would look like a broken button.
    /// </summary>
    private async Task RunSearchAsync()
    {
        if (Selected is not CloudEndpointAccount account) { StatusText.Text = "请先选择云盘。"; return; }
        var query = SearchBox.Text.Trim();
        if (!SearchRecursiveBox.IsChecked.GetValueOrDefault() || query.Length == 0) { await BrowseAsync(); return; }
        try
        {
            StatusText.Text = $"正在“{account.Name}”内搜索“{query}”…";
            var results = await _files.SearchAsync(account.Name, _currentPath, query);
            // Search results come from many directories, so each row has to carry its own path.
            ShowListing(_currentPath, results.Select(entry => entry with { ShowFullPath = true }), isSearchResult: true);
            StatusText.Text = results.Count == 0
                ? $"没有匹配“{query}”的内容。"
                : $"找到 {results.Count} 项"
                  + (results.Count >= CloudFileManagerService.SearchResultLimit ? $"（已达上限 {CloudFileManagerService.SearchResultLimit} 项，请缩小范围）" : "")
                  + "。";
        }
        catch (Exception ex) { ReportFailure("搜索失败", ex, "cloud-file-search"); }
    }

    private async void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchRecursiveBox.IsChecked = false;
        await BrowseAsync();
    }

    // ---- File operations ----

    private async void NewFolder_Click(object sender, RoutedEventArgs e) => await PromptNewFolderAsync();

    private async Task PromptNewFolderAsync()
    {
        if (Selected is null) { StatusText.Text = "请先选择云盘。"; return; }
        var name = TextPrompt.Ask(this, "新建文件夹", $"在当前目录 {_currentPath} 下新建文件夹，名称：", "", CloudFileNames.Validate);
        if (name is null) return;
        await CreateFolderAsync(name);
    }

    /// <summary>Creates one folder under the current directory. Returns false when it did not happen.</summary>
    internal async Task<bool> CreateFolderAsync(string name)
    {
        if (Selected is not CloudEndpointAccount account) return false;
        var reason = CloudFileNames.Validate(name);
        if (reason is not null) { StatusText.Text = reason; return false; }
        var target = CloudFileManagerService.Join(_currentPath, name);
        try
        {
            StatusText.Text = $"正在创建文件夹“{name}”…";
            await _files.CreateDirectoryAsync(account.Name, target);
            await BrowseAsync();
            StatusText.Text = $"已创建文件夹：{target}";
            return true;
        }
        catch (Exception ex) { ReportFailure("新建文件夹失败", ex, "cloud-mkdir"); return false; }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e) => await RenameSelectedAsync();

    /// <summary>
    /// 重命名 for whatever is selected, which is one row or none: a rename has exactly one new name, so a multi-
    /// selection has no single answer to give it. The keyboard's F2 comes through here too, so the two routes
    /// cannot grow different rules about what "selected" means.
    /// </summary>
    internal async Task RenameSelectedAsync()
    {
        var selected = SelectedEntries();
        if (selected.Count != 1) { StatusText.Text = "请先选择要重命名的单个项目。"; return; }
        await PromptRenameAsync(selected[0]);
    }

    private async Task PromptRenameAsync(CloudFileEntry entry)
    {
        var name = (PromptRename ?? (target => TextPrompt.Ask(this, "重命名",
            $"“{target.Name}”的新名称：", target.Name, CloudFileNames.Validate)))(entry);
        if (name is null || name == entry.Name) return;
        await RenameEntryAsync(entry, name);
    }

    /// <summary>Renames one entry inside its own directory. Returns false when it did not happen.</summary>
    internal async Task<bool> RenameEntryAsync(CloudFileEntry entry, string newName)
    {
        if (Selected is not CloudEndpointAccount account) return false;
        var reason = CloudFileNames.Validate(newName);
        if (reason is not null) { StatusText.Text = reason; return false; }
        try
        {
            StatusText.Text = $"正在重命名“{entry.Name}”…";
            await _files.RenameAsync(account.Name, entry.Path, newName, entry.IsDirectory);
            await BrowseAsync();
            StatusText.Text = $"已重命名为：{newName}";
            return true;
        }
        catch (Exception ex) { ReportFailure("重命名失败", ex, "cloud-rename"); return false; }
    }

    private async void DeleteEntries_Click(object sender, RoutedEventArgs e) => await DeleteSelectedAsync();

    /// <summary>
    /// 删除 for whatever is selected — the same question, the same bulk threshold and the same second
    /// acknowledgement whether the user clicked the button or pressed Shift+Del. Standing in the trash adds
    /// <see cref="CloudDeleteSafety.Describe"/>'s one extra line, because a row on screen in a trash view is by
    /// definition trash's own content, and the net being removed is the fact that belongs in the confirmation.
    /// </summary>
    internal async Task DeleteSelectedAsync()
    {
        var selected = SelectedEntries();
        if (selected.Count == 0) { StatusText.Text = "请先选择要删除的项目。"; return; }
        var insideTrash = Selected is { } account && CloudRecycleBin.IsTrashView(account.RootPath, _currentPath);
        await ConfirmAndDeleteAsync(selected, insideTrash);
    }

    /// <summary>
    /// The delete questions, behind a seam because a test cannot answer a modal. It receives each message in the
    /// order the user would see it: always <see cref="CloudDeleteSafety.Describe"/>'s, then the bulk
    /// acknowledgement only when the batch is bulk. Both 删除 and 清空回收站 ask through here, which is how
    /// "one delete rule, not a second one" stays provable rather than merely intended.
    /// </summary>
    internal Func<string, bool>? ConfirmDelete { get; set; }

    /// <summary>
    /// Asks before deleting, and asks twice when the batch is large or the backend cannot undo it: the first
    /// dialog is easy to dismiss by reflex, and there is no undo behind either of them.
    /// </summary>
    private async Task ConfirmAndDeleteAsync(IReadOnlyList<CloudFileEntry> entries, bool insideRecycleBin = false)
    {
        var confirmation = CloudDeleteSafety.Describe(entries, CloudDeleteSafety.SupportsTrash(KindOf(Selected)), insideRecycleBin);
        if (!Ask(confirmation.Message, "删除")) return;
        if (confirmation.IsBulk && !Ask(
                $"即将删除 {entries.Count} 项，其中文件夹会连同其全部内容一起删除。\n\n确定继续？",
                "再次确认删除")) return;
        await DeleteEntriesAsync(entries);

        bool Ask(string message, string title) =>
            (ConfirmDelete ?? (text => TextPrompt.Confirm(this, title, text, danger: true)))(message);
    }

    /// <summary>
    /// Deletes the given entries, reporting per-item failures instead of stopping at the first one: a batch
    /// delete that silently gave up after one bad path would leave the user unsure what is left.
    /// </summary>
    internal async Task<int> DeleteEntriesAsync(IReadOnlyList<CloudFileEntry> entries)
    {
        if (Selected is not CloudEndpointAccount account) return 0;
        var removed = 0;
        var failures = new List<string>();
        foreach (var entry in entries)
        {
            try { await _files.DeleteAsync(account.Name, entry.Path, entry.IsDirectory); removed++; }
            catch (Exception ex) { failures.Add($"{entry.Path}：{RcloneUiError.Describe(ex, "cloud-delete")}"); }
        }
        await BrowseAsync();
        StatusText.Text = failures.Count == 0
            ? $"已删除 {removed} 项。"
            : $"已删除 {removed} 项，{failures.Count} 项失败：{string.Join("；", failures.Take(3))}";
        if (failures.Count > 0) MessageBox.Show(StatusText.Text, "删除", MessageBoxButton.OK, MessageBoxImage.Warning);
        return removed;
    }

    private async void DownloadSelected_Click(object sender, RoutedEventArgs e) => await DownloadSelectionAsync();

    // ---- 回收站（软删除） ----

    /// <summary>
    /// The question 移到回收站 asks, behind a seam because a test cannot answer a modal — and the property worth
    /// pinning is that nothing moves until the user agrees. It is deliberately <em>not</em> a
    /// <see cref="CloudDeleteSafety"/> question: recycling is reversible, so the bulk threshold and its second
    /// confirmation stay on 删除 and 清空回收站, which are the two that take bytes away for good.
    /// </summary>
    internal Func<IReadOnlyList<CloudFileEntry>, bool>? ConfirmRecycle { get; set; }

    private async void Recycle_Click(object sender, RoutedEventArgs e) => await RecycleSelectedAsync();

    /// <summary>
    /// 移到回收站 for whatever is selected. Del and the button land here, so the empty-selection sentence is
    /// written once — and it is a different sentence from 删除's, because the two commands undo each other and a
    /// user must be able to tell which one the keystroke asked for.
    /// </summary>
    internal async Task RecycleSelectedAsync()
    {
        var selected = SelectedEntries();
        if (selected.Count == 0) { StatusText.Text = "请先选择要移到回收站的项目。"; return; }
        await RecycleAsync(selected);
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedEntries();
        if (selected.Count == 0) { StatusText.Text = "请先选择要还原的项目。"; return; }
        await RestoreAsync(selected);
    }

    private async void EmptyTrash_Click(object sender, RoutedEventArgs e) => await EmptyTrashAsync();

    private async void OpenTrash_Click(object sender, RoutedEventArgs e) => await OpenTrashAsync();

    /// <summary>
    /// 移到回收站 for the given rows: refuse what cannot be recycled, ask once, then move. A row that is already
    /// in the trash and the trash folder itself are said as that instead of being shuffled deeper in.
    /// </summary>
    internal async Task<CloudRecycleResult> RecycleAsync(IReadOnlyList<CloudFileEntry> entries)
    {
        if (Selected is not CloudEndpointAccount account) return Say(Failed());
        var (movable, refusal) = CloudRecycleBin.SelectForMove(account.RootPath, entries);
        if (refusal is { } denied) return Say(new CloudRecycleResult(denied, []));
        if ((ConfirmRecycle ?? (targets => TextPrompt.Confirm(this, "移到回收站", CloudRecycleBin.ConfirmFor(targets))))(movable))
            return Say(await MoveTowardTrashAsync(account, movable, restore: false));
        return Say(new CloudRecycleResult(CloudRecycleOutcome.Declined, []));
    }

    /// <summary>
    /// 还原 for the given rows. Nothing is confirmed: the command returns an object to where it came from, and a
    /// name that has since been taken is answered by a different destination in the status line rather than by a
    /// dialog — the user asked for the file back, and the honest report is where it actually landed.
    /// </summary>
    internal async Task<CloudRecycleResult> RestoreAsync(IReadOnlyList<CloudFileEntry> entries)
    {
        if (Selected is not CloudEndpointAccount account) return Say(Failed());
        var restorable = CloudRecycleBin.SelectForRestore(account.RootPath, entries);
        if (restorable.Count == 0) return Say(new CloudRecycleResult(CloudRecycleOutcome.NotInTrash, []));
        return Say(await MoveTowardTrashAsync(account, restorable, restore: true));
    }

    /// <summary>
    /// 清空回收站. The trash's own contents go through the 删除 path rather than a second delete rule, so the bulk
    /// threshold and its second confirmation apply to the biggest soft-deleted pile in the endpoint; a trash that
    /// was never used is not an error, it is simply empty.
    /// </summary>
    internal async Task EmptyTrashAsync()
    {
        if (Selected is not CloudEndpointAccount account) { StatusText.Text = "请先选择云盘。"; return; }
        var entries = await _files.ListOrEmptyAsync(account.Name, CloudRecycleBin.RootOf(account.RootPath));
        if (entries.Count == 0) { StatusText.Text = "回收站是空的，没有可删除的项目。"; return; }
        await ConfirmAndDeleteAsync(entries, insideRecycleBin: true);
    }

    /// <summary>
    /// 回收站: opens the mirrored folder. It is created on the way in because it does not exist until the first
    /// object is recycled, and a first click answering "目录已不存在" would read as a broken feature; measured on
    /// v1.75.0, <c>operations/mkdir</c> creates missing parents, is idempotent, and refuses loudly to overwrite a
    /// file that happens to carry the folder's name.
    /// </summary>
    internal async Task OpenTrashAsync()
    {
        if (Selected is not CloudEndpointAccount account) { StatusText.Text = "请先选择云盘。"; return; }
        var trash = CloudRecycleBin.RootOf(account.RootPath);
        try { await _files.CreateDirectoryAsync(account.Name, trash); }
        catch (Exception ex) { ReportFailure("无法打开回收站", ex, "cloud-trash-open"); return; }
        await NavigateToAsync(trash);
    }

    /// <summary>
    /// Moves a batch between its current directory and the mirrored one on the other side of the trash boundary.
    /// Landing names come from each destination directory's own listing, read once per directory before anything
    /// moves, because both move legs overwrite a name that is already taken. A directory then has its husk
    /// removed: measured on v1.75.0, a directory move onto a destination that exists merges and leaves the source
    /// sitting there empty, while a move onto a fresh path takes the source with it. Per-row failures are
    /// collected rather than thrown, so a batch that half-worked is said as a half-work.
    /// </summary>
    private async Task<CloudRecycleResult> MoveTowardTrashAsync(CloudEndpointAccount account,
        IReadOnlyList<CloudFileEntry> entries, bool restore)
    {
        var root = account.RootPath;
        var directories = entries
            .Select(entry => (restore
                ? CloudRecycleBin.OriginalDirectory(root, entry.Path)
                : CloudRecycleBin.LandingDirectory(root, entry.Path)).Trim('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var taken = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
            taken[directory] = await _files.NamesAsync(account.Name, directory);
        var placements = CloudRecycleBin.PlacementsFor(root, entries,
            directory => taken.TryGetValue(directory, out var names) ? names : [], restore);
        var isDirectory = entries.ToDictionary(entry => entry.Path, entry => entry.IsDirectory, StringComparer.OrdinalIgnoreCase);

        var moved = new List<CloudRecyclePlacement>();
        var failures = new List<string>();
        foreach (var placement in placements)
        {
            try
            {
                await _files.MoveIntoAsync(account.Name, placement.Source, placement.Destination, isDirectory[placement.Source]);
                if (isDirectory[placement.Source]) await _files.RemoveIfEmptyAsync(account.Name, placement.Source);
                moved.Add(placement);
            }
            catch (Exception ex)
            {
                failures.Add($"{placement.Source}：{RcloneUiError.Describe(ex, restore ? "cloud-restore" : "cloud-recycle")}");
            }
        }
        await BrowseAsync();
        if (moved.Count == 0)
            return new CloudRecycleResult(CloudRecycleOutcome.Failed, [], failures.Count, Detail(failures));
        return new CloudRecycleResult(
            restore ? CloudRecycleOutcome.Restored : CloudRecycleOutcome.Moved,
            moved,
            failures.Count,
            failures.Count == 0 ? null : Detail(failures));

        static string Detail(IEnumerable<string> reasons) => string.Join("；", reasons.Take(3));
    }

    private static CloudRecycleResult Failed() =>
        new(CloudRecycleOutcome.Failed, [], Detail: "请先选择云端端点。");

    /// <summary>Says which outcome a recycle or restore batch produced, in the sentence that fact belongs to.</summary>
    private CloudRecycleResult Say(CloudRecycleResult result)
    {
        StatusText.Text = CloudRecycleBin.Explain(result);
        return result;
    }

    // ---- 复制 / 剪切 / 粘贴 ----

    /// <summary>
    /// Puts rows on hold for a paste, and says what that promise is: a copy leaves the source alone, a cut
    /// removes it once the copy has landed, and a folder in the selection will not be pasted at all. Saying so
    /// at the moment of the gesture is what keeps the difference from being discovered at the destination.
    /// </summary>
    internal void SetClipboard(CloudClipboardMode mode, IReadOnlyList<CloudFileEntry> entries)
    {
        if (Selected is not { } account) { StatusText.Text = "请先选择云盘。"; return; }
        if (entries.Count == 0) { StatusText.Text = "请先选择要" + (mode == CloudClipboardMode.Cut ? "剪切" : "复制") + "的项目。"; return; }
        _clipboard.Clear();
        _clipboard.AddRange(entries.Select(entry => new CloudClipboardItem(account, entry)));
        _clipboardMode = mode;
        UpdateFileButtons();
        var folders = entries.Count(entry => entry.IsDirectory);
        StatusText.Text = $"已{(mode == CloudClipboardMode.Cut ? "剪切" : "复制")} {entries.Count} 项，进入目标目录后点“粘贴”。"
            + (folders > 0 ? $"其中 {folders} 个文件夹不会被粘贴：粘贴的单位是文件。" : "")
            + (mode == CloudClipboardMode.Cut ? "复制成功后源文件会被删除，云端删除无法恢复。" : "");
    }

    /// <summary>
    /// What a paste would do right now. The destination directory is listed rather than taken from the rows on
    /// screen, because a same-name collision is the one thing the user has to be asked about <em>before</em>
    /// anything is queued.
    /// </summary>
    internal async Task<CloudPasteOffer?> PlanPasteAsync()
    {
        if (Selected is not { } account || _clipboard.Count == 0) return null;
        try
        {
            var listing = await _files.ListAsync(account.Name, _currentPath);
            return new CloudPasteOffer(CloudPastePlanner.Build([.. _clipboard], account, _currentPath, listing),
                listing.Select(entry => entry.Name).ToArray());
        }
        catch (Exception ex) { ReportFailure("无法读取目标目录", ex, "cloud-paste-conflict"); return null; }
    }

    /// <summary>
    /// The conflict question, behind a seam so a test can hand in answers instead of waiting on a modal
    /// dialog: a test cannot answer a window, and a batch whose decisions are only reachable through one would
    /// go untested — which is how a paste starts replacing a file nobody agreed to replace. The default is the
    /// real prompt.
    /// </summary>
    internal Func<string, int, IReadOnlyCollection<string>, CloudConflictAnswer?>? AskConflict { get; set; }

    /// <summary>
    /// Where a batch of destinations actually goes. <paramref name="conflicts"/> is the subset of
    /// <paramref name="destinations"/> whose name is already taken, and <paramref name="takenOf"/> reports the
    /// names one directory holds — per directory rather than one shared set, because a 保留两者 name is only free
    /// relative to the directory it lands in. A cancelled batch returns <see langword="null"/> so the caller
    /// queues nothing rather than guessing what the user meant; otherwise the result is one placement per
    /// destination, in the order given, and a destination answered 跳过 says so rather than going missing.
    /// </summary>
    private IReadOnlyList<CloudConflictPlacement>? PlaceDestinations(
        IReadOnlyList<string> destinations,
        IReadOnlyList<string> conflicts,
        Func<string, ICollection<string>> takenOf)
    {
        var choices = ResolveConflicts(conflicts, path => takenOf(CloudTransferQueue.ParentOf(path)));
        return choices is null
            ? null
            : CloudConflictResolver.Apply(destinations, takenOf,
                path => choices.TryGetValue(path, out var choice) ? choice : CloudConflictChoice.Overwrite);
    }

    /// <summary>
    /// Indexes the placements a paste can act on by the destination the plan proposed; a destination answered
    /// 跳过 gets no entry, which is how "what goes" is read here. An upload does not use this — its rows pair
    /// with the placements by position, and two files dropped onto one remote name share a key, so indexing
    /// them would send both to the last one's renamed destination.
    /// </summary>
    private static IReadOnlyDictionary<string, CloudConflictPlacement> Indexed(IReadOnlyList<CloudConflictPlacement> placements)
    {
        var byDestination = new Dictionary<string, CloudConflictPlacement>(StringComparer.OrdinalIgnoreCase);
        foreach (var placement in placements)
            if (placement.Choice != CloudConflictChoice.Skip) byDestination[placement.Original] = placement;
        return byDestination;
    }

    /// <summary>
    /// The answer for every conflicted name in a batch. A saved policy answers for all of them without asking;
    /// otherwise each name is asked one at a time and an answer can be applied to the ones still waiting —
    /// which is why the question is one call over the whole batch, not one per directory: 应用到全部 has to mean
    /// the rest of the drop, not the rest of one directory. A cancellation returns <see langword="null"/>,
    /// distinct from the empty dictionary that means "there was nothing to ask".
    /// </summary>
    private IReadOnlyDictionary<string, CloudConflictChoice>? ResolveConflicts(
        IReadOnlyList<string> conflicts,
        Func<string, IEnumerable<string>> takenOf)
    {
        if (conflicts.Count == 0) return new Dictionary<string, CloudConflictChoice>(StringComparer.OrdinalIgnoreCase);
        if (CloudConflictResolver.ChoiceFor(_settings.CloudUploadConflict) is { } batch)
            return conflicts.ToDictionary(path => path, _ => batch, StringComparer.OrdinalIgnoreCase);
        return CloudConflictResolver.AskFor(conflicts, (path, remaining) =>
        {
            var taken = takenOf(path).ToArray();
            // The seam's null <em>is</em> the user's cancellation, so it must not be read as "no seam
            // installed": a coalescing call would open the real dialog from a test that has nobody to answer it,
            // and WPF refuses to own a window that was never shown.
            return AskConflict is { } ask
                ? ask(path, remaining, taken)
                : CloudConflictPrompt.Ask(this, path, remaining, taken);
        });
    }

    /// <summary>
    /// Runs a paste. Every copy goes through <see cref="CloudTransferQueue"/> — including a copy between two
    /// directories of one endpoint — so it gets the same progress, cancellation, retry and history as an
    /// upload, instead of a second copy path that has none of them. A cut inside one endpoint is the exception:
    /// rclone answers that with <c>operations/movefile</c> and no bytes move, and it is the same call 重命名
    /// already uses. <paramref name="placements"/> is the conflict answer, addressed by the destination the
    /// plan proposed: a target missing from it was skipped, and one whose destination changed was kept beside
    /// the file that was already there. Returns the number of items the paste acted on.
    /// </summary>
    internal async Task<int> ExecutePasteAsync(CloudPastePlan plan, IReadOnlyDictionary<string, CloudConflictPlacement> placements)
    {
        if (Selected is not { } account) return 0;
        var targets = new List<(CloudPasteTarget Target, string Path, string RelativePath)>();
        var renamed = 0;
        foreach (var target in plan.Targets)
        {
            if (!placements.TryGetValue(target.DestinationPath, out var placement)) continue;
            var relative = target.DestinationRelativePath;
            if (placement.Destination != target.DestinationPath)
            {
                // Only the last segment changes, so the renamed file stays in the directory the user was
                // looking at — and the endpoint-relative leg has to move with it or the copy lands on the
                // name 保留两者 was chosen to avoid.
                renamed++;
                relative = CloudFileManagerService.Join(CloudFileManagerService.Parent(relative),
                    CloudTransferTask.NameOf(placement.Destination));
            }
            targets.Add((target, placement.Destination, relative));
        }
        if (targets.Count == 0)
        {
            StatusText.Text = $"已跳过 {plan.Conflicts} 个同名项目。";
            return 0;
        }
        var cutting = _clipboardMode == CloudClipboardMode.Cut;
        var moved = 0;
        var copies = new List<(CloudTransferTask Task, CloudClipboardItem Item)>();
        var failures = new List<string>();
        foreach (var (target, destinationPath, destinationRelativePath) in targets)
        {
            try
            {
                if (cutting && target.SameEndpoint)
                {
                    // The destination directory is the one being browsed, so it exists and needs no mkdir.
                    await _files.MoveAsync(target.Item.Source.Name, target.Item.Entry.Path, destinationPath);
                    moved++;
                    continue;
                }
                var task = _transfers.EnqueueCopy(EndpointUri(target.Item.Source), target.SourceRelativePath,
                    EndpointUri(account), destinationRelativePath, target.Item.Entry.Size);
                copies.Add((task, target.Item));
            }
            catch (Exception ex) { failures.Add($"{target.Item.Entry.Name}：{RcloneUiError.Describe(ex, "cloud-paste")}"); }
        }
        if (cutting) _clipboard.Clear();
        if (moved > 0) await BrowseAsync();
        // Only a cut owes the world a delete: 复制 leaves the source exactly where it is, so queueing the
        // removal for a plain copy would turn the paste the user expects to be additive into data loss.
        if (cutting)
            foreach (var (task, item) in copies) DeleteSourceWhenCopied(task, item, account);
        var parts = new List<string>();
        if (moved > 0) parts.Add($"已移动 {moved} 项");
        if (copies.Count > 0) parts.Add($"已排队复制 {copies.Count} 个文件");
        if (renamed > 0) parts.Add($"{renamed} 个同名文件改名为保留两者");
        if (failures.Count > 0) parts.Add($"{failures.Count} 项失败：{string.Join("；", failures.Take(3))}");
        if (plan.Skipped.Count > 0) parts.Add(string.Join(" ", plan.Skipped));
        StatusText.Text = string.Join("。", parts) + "。";
        return moved + copies.Count;
    }

    /// <summary>
    /// Deletes a cut's source once, and only once, its copy has completed — so a copy that failed or was
    /// cancelled leaves the source exactly where it was. The wait is on the queue rather than in the click
    /// handler: a batch of cross-endpoint copies may take a long time, and the browser has to stay usable
    /// while they run. This window owns the deletion, so closing it before a copy lands leaves a duplicate
    /// rather than losing a file, which is the safe half of that trade.
    /// </summary>
    private void DeleteSourceWhenCopied(CloudTransferTask task, CloudClipboardItem item, CloudEndpointAccount destination)
    {
        _ = DeleteAfterCopyAsync();

        async Task DeleteAfterCopyAsync()
        {
            string message;
            var refreshed = false;
            if (!await _transfers.WaitForAsync(task))
                message = $"“{item.Entry.Name}”没有复制完成，源文件仍然保留。";
            else
            {
                try
                {
                    await _files.DeleteAsync(item.Source.Name, item.Entry.Path, false);
                    message = $"已剪切到 {destination.Name}：{item.Entry.Name}";
                }
                catch (Exception ex)
                {
                    // The copy landed and the source is still there, so the user now has two files. Saying
                    // "已复制但源未删除" is the whole report; calling it a success would hide the duplicate.
                    message = $"“{item.Entry.Name}”已复制但源未删除：{RcloneUiError.Describe(ex, "cloud-delete")}";
                }
                // The copy only lands in the directory on screen once the job has finished, so this is the
                // moment the listing needs to be re-read.
                refreshed = true;
            }
            // The listing is re-read first and the status line written last, because BrowseAsync claims the
            // status line for itself ("正在读取目录…") and never gives it back: reporting the outcome before
            // the refresh would leave the row reading a spinner that finished long ago.
            await Dispatcher.InvokeAsync(async () =>
            {
                if (refreshed) await BrowseAsync();
                StatusText.Text = message;
            }).Task.Unwrap();
        }
    }

    /// <summary>
    /// Confirms the delete a cut ends with, before any copy starts. Asking after the bytes moved would be
    /// asking the user to decide with their file already duplicated. Only the rows that leave the endpoint they
    /// came from <em>and actually go</em> are listed: a cut inside one endpoint is a move, and a move is not
    /// something this app asks twice about — 重命名 is the same call — while a row the user chose to skip
    /// would otherwise be confirmed for a delete that never happens.
    /// </summary>
    private bool ConfirmCut(CloudPastePlan plan, IReadOnlyDictionary<string, CloudConflictPlacement> placements)
    {
        var sources = plan.Targets.Where(target => !target.SameEndpoint && placements.ContainsKey(target.DestinationPath))
            .Select(target => target.Item).ToList();
        if (sources.Count == 0) return true;
        var entries = sources.Select(item => item.Entry).ToList();
        // Whether the delete can be undone is the source endpoint's answer, not the destination's.
        var confirmation = CloudDeleteSafety.Describe(entries, CloudDeleteSafety.SupportsTrash(KindOf(sources[0].Source)));
        if (!TextPrompt.Confirm(this, "剪切后删除源", $"粘贴会先复制到目标端点，复制成功后再删除下面这些源文件。\n\n{confirmation.Message}", danger: true)) return false;
        return !confirmation.IsBulk
            || TextPrompt.Confirm(this, "再次确认删除", $"即将在复制成功后删除 {entries.Count} 项源文件。\n\n确定继续？", danger: true);
    }

    private async void Paste_Click(object sender, RoutedEventArgs e) => await PasteAsync();

    /// <summary>
    /// 粘贴 into the directory on screen. Ctrl+V and the button both come through here, which is what keeps the
    /// keyboard from growing a second, cheaper set of checks in front of the same queue: the empty-clipboard and
    /// no-endpoint refusals, the destination's own listing, the collision question and the cut's delete
    /// confirmation are the paste, whatever opened it.
    /// </summary>
    internal async Task PasteAsync()
    {
        if (_clipboard.Count == 0) { StatusText.Text = "剪贴板是空的：先选中项目，再用右键菜单的“复制”或“剪切”。"; return; }
        if (Selected is null) { StatusText.Text = "请先选择要粘贴到的云盘。"; return; }
        var offer = await PlanPasteAsync();
        if (offer is null) return;
        var plan = offer.Plan;
        if (plan.Targets.Count == 0) { StatusText.Text = "没有可粘贴的项目。" + string.Join(" ", plan.Skipped); return; }
        var taken = new HashSet<string>(offer.TakenNames, StringComparer.OrdinalIgnoreCase);
        var placed = PlaceDestinations([.. plan.Targets.Select(target => target.DestinationPath)],
            [.. plan.Targets.Where(target => target.OverwritesExisting).Select(target => target.DestinationPath)],
            _ => taken);
        if (placed is null) { StatusText.Text = "已取消粘贴。"; return; }
        var placements = Indexed(placed);
        if (placements.Count == 0) { StatusText.Text = $"已跳过 {plan.Conflicts} 个同名项目。"; return; }
        if (_clipboardMode == CloudClipboardMode.Cut && !ConfirmCut(plan, placements)) { StatusText.Text = "已取消粘贴。"; return; }
        await ExecutePasteAsync(plan, placements);
    }

    /// <summary>Queues every selected file, asking for a destination only when there is no usable default.</summary>
    internal async Task DownloadSelectionAsync()
    {
        if (Selected is not CloudEndpointAccount account) { StatusText.Text = "请先选择云盘。"; return; }
        var files = SelectedEntries().Where(entry => !entry.IsDirectory).ToList();
        if (files.Count == 0) { StatusText.Text = "请先选择要下载的文件；文件夹请先进入后再选择其中的文件。"; return; }
        if (files.Count == 1) { await DownloadAsync(files[0]); return; }

        var directory = _settings.CloudDownloadDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            var dialog = new OpenFolderDialog { Title = "选择下载目录" };
            if (dialog.ShowDialog(this) != true) return;
            directory = dialog.FolderName;
        }
        foreach (var file in files)
            _transfers.Enqueue(CloudTransferDirection.Download, EndpointUri(account), CloudFileManagerService.RelativeToEndpoint(account, file.Path), Path.Combine(directory, file.Name), file.Size);
        StatusText.Text = $"已加入传输队列：{files.Count} 个文件。";
        ShowTransferWindow();
    }

    private async Task DownloadAsync(CloudFileEntry entry)
    {
        if (Selected is not CloudEndpointAccount account) return;
        var directory = _settings.CloudDownloadDirectory;
        string destination;
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            destination = Path.Combine(directory, entry.Name);
            // A default directory makes downloading one click, which also makes overwriting one click; the
            // confirmation is what keeps that from silently replacing a file the user already had.
            if (File.Exists(destination)
                && !TextPrompt.Confirm(this, "文件已存在", $"“{destination}”已存在，是否覆盖？", danger: true)) return;
        }
        else
        {
            var dialog = new SaveFileDialog { FileName = entry.Name, Title = "下载文件" };
            if (dialog.ShowDialog(this) != true) return;
            destination = dialog.FileName;
        }
        _transfers.Enqueue(CloudTransferDirection.Download, EndpointUri(account), CloudFileManagerService.RelativeToEndpoint(account, entry.Path), destination, entry.Size);
        StatusText.Text = "已加入传输队列：" + entry.Name;
        ShowTransferWindow();
        await Task.CompletedTask;
    }

    private async Task OpenAsync(CloudFileEntry entry)
    {
        if (Selected is not CloudEndpointAccount account) return;
        var folder = Path.Combine(Path.GetTempPath(), "FengSync", "cloud-open", Guid.NewGuid().ToString("N"));
        var local = Path.Combine(folder, entry.Name);
        var task = _transfers.Enqueue(CloudTransferDirection.Download, EndpointUri(account), CloudFileManagerService.RelativeToEndpoint(account, entry.Path), local, entry.Size);
        ShowTransferWindow();
        StatusText.Text = "正在下载以打开：" + entry.Name;
        if (!await _transfers.WaitForAsync(task))
        {
            StatusText.Text = "打开失败：" + (task.Error ?? "传输未完成。");
            return;
        }
        Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
        StatusText.Text = "已打开：" + entry.Name;
    }

    /// <summary>
    /// The rows 属性 shows for one entry, and the seam the dialog is built on. The values come from a
    /// <c>stat</c> of the object itself rather than from the row on screen, because the listing is a snapshot:
    /// it can name a file the backend has already dropped, and it has no MIME type to show. A read that fails
    /// still produces the listing's facts plus the reason, so the panel is never an empty window.
    /// <para>
    /// The two optional reads are the expensive ones and each is asked for by its own button: <paramref
    /// name="withHash"/> passes the checksums rclone may then compute, <paramref name="withSize"/> walks the
    /// subtree for a directory's total. Neither is a re-read of something the panel already has, and neither
    /// result is kept here — a fresh call starts from 未计算 / 未统计 again, which is the whole point of asking
    /// the user before spending the bandwidth.
    /// </para>
    /// </summary>
    internal async Task<CloudDetailsPanel> DetailsAsync(CloudFileEntry entry, bool withHash = false, bool withSize = false)
    {
        var account = Selected;
        if (account is null) return new CloudDetailsPanel(CloudItemDetails.For(entry, null, null));
        CloudItemStat? stat = null;
        CloudSizeResult? size = null;
        var failure = (CloudDetailRow?)null;
        try
        {
            stat = await _files.StatAsync(account.Name, entry.Path, withHash ? CloudItemDetails.HashPreference : null);
        }
        catch (Exception ex) { failure = new CloudDetailRow("读取详情", RcloneUiError.Describe(ex, "cloud-stat")); }
        // Only a directory the backend still claims is worth walking: the panel offers no other case, and a read
        // that this method made on its own would be a second representation of that rule.
        if (withSize && stat is { IsDirectory: true, Exists: true })
        {
            try
            {
                var answer = await _files.SizeAsync(account.Name, entry.Path);
                size = answer is null ? CloudSizeResult.NoAnswer : CloudSizeResult.Of(answer);
            }
            catch (Exception ex) { size = CloudFolderSize.FromFailure(ex); }
        }
        var rows = new List<CloudDetailRow>(CloudItemDetails.For(entry, account, stat, withHash, size));
        if (failure is { } reason) rows.Add(reason);
        return new CloudDetailsPanel(rows, CloudItemDetails.ActionFor(entry, stat, withHash, size,
            () => DetailsAsync(entry, withHash: true), () => DetailsAsync(entry, withSize: true)));
    }

    private async Task ShowDetailsAsync(CloudFileEntry entry)
    {
        var panel = await DetailsAsync(entry);
        // The menu item stays available either way: a folder's 属性 and a gone object's 属性 are exactly the
        // two answers the user needs, and a panel with nothing left to read shows no button.
        CloudDetailsPrompt.Show(this, "属性 · " + entry.Name, panel.Rows, panel.Action);
    }

    private void ShowSelectionDetails() =>
        CloudDetailsPrompt.Show(this, "所选项目的属性", CloudItemDetails.ForSelection(Selection, Selected));

    /// <summary>
    /// The panel 预览 shows for one row, behind a seam because a test cannot sit through a modal. Whether a row
    /// may be painted at all is <see cref="CloudBrowserPreview"/>'s decision; this only carries it out, and the two
    /// halves stay separate — a row that cannot be painted is explained <em>before</em> any read is spent on it,
    /// and a row that can gets its own sentence for each thing the cloud might answer instead (gone, bigger than
    /// the ceiling, bytes no decoder claims, text in a code page the program will not guess).
    /// </summary>
    internal async Task<CloudPreviewPanel> PreviewAsync(CloudFileEntry entry)
    {
        var decision = CloudBrowserPreview.DecideFor(entry);
        if (!CloudBrowserPreview.IsPaintable(decision))
            return CloudPreviewPanel.Refused(decision, CloudBrowserPreview.ReasonFor(decision, entry));
        if (Selected is not { } account)
            return CloudPreviewPanel.Refused(CloudPreviewOutcome.Failed, "没有选中的云盘端点，读不到任何内容。");
        RcloneObjectRead read;
        try
        {
            read = await _files.ReadAsync(account.Name, entry.Path, CloudBrowserPreview.ReadCapFor(decision));
        }
        catch (Exception ex)
        {
            return CloudPreviewPanel.Refused(CloudPreviewOutcome.Failed,
                CloudBrowserPreview.ReasonFor(CloudPreviewOutcome.Failed, entry, -1, RcloneUiError.Describe(ex, "cloud-preview")));
        }
        var outcome = CloudBrowserPreview.OutcomeOf(decision, read);
        if (outcome == CloudPreviewOutcome.Picture)
        {
            var image = CloudBrowserPreview.DecodePicture(read.Bytes);
            return image is null
                ? CloudPreviewPanel.Refused(CloudPreviewOutcome.UndecodableBytes,
                    CloudBrowserPreview.ReasonFor(CloudPreviewOutcome.UndecodableBytes, entry))
                : CloudPreviewPanel.Picture(image);
        }
        if (outcome == CloudPreviewOutcome.Text)
        {
            var (state, text, label) = CloudBrowserPreview.ExtractText(read.Bytes);
            return state == CloudPreviewOutcome.Text
                ? CloudPreviewPanel.Text(text, label)
                : CloudPreviewPanel.Refused(state, CloudBrowserPreview.ReasonFor(state, entry));
        }
        return CloudPreviewPanel.Refused(outcome, CloudBrowserPreview.ReasonFor(outcome, entry, read.Total, read.Detail));
    }

    private async Task ShowPreviewAsync(CloudFileEntry entry) =>
        CloudPreviewPrompt.Show(this, "预览 · " + entry.Name, entry, await PreviewAsync(entry));

    /// <summary>
    /// The question a share link asks before anything is created, behind a seam because a test cannot answer a
    /// modal — and the property worth pinning is exactly that nothing reaches the cloud until the user agrees.
    /// A link is a grant rather than a read: rclone's default is one anyone holding the URL can use without an
    /// account, and how long it survives is the backend's decision, so this is asked once per file, in the
    /// user's own language, and never on a batch.
    /// </summary>
    internal Func<CloudFileEntry, bool>? ConfirmShare { get; set; }

    /// <summary>
    /// Where a finished link goes. Behind a seam because WPF's clipboard is process-wide state a desktop-free
    /// test must not fight over, and because a failed copy is a different sentence from a missing link.
    /// </summary>
    internal Action<string>? CopyLink { get; set; }

    /// <summary>
    /// One share request, end to end: confirm, ask the backend, hand the link to the clipboard. Every branch
    /// returns a result the status line can name, so a request that created nothing is never reported as one
    /// that copied something.
    /// </summary>
    internal async Task<CloudShareLinkResult> ShareLinkAsync(CloudFileEntry entry)
    {
        if (!CloudShareLinks.OffersLink(entry))
            return new CloudShareLinkResult(CloudShareLinkOutcome.NotAFile);
        if ((ConfirmShare ?? (target => TextPrompt.Confirm(this, CloudShareLinks.ConfirmationTitle,
                CloudShareLinks.ConfirmationFor(target), danger: true)))(entry))
        {
            var account = Selected;
            if (account is null) return new CloudShareLinkResult(CloudShareLinkOutcome.Failed, Detail: "请先选择云端端点。");
            CloudShareLinkResult result;
            try { result = CloudShareLinks.FromAnswer(await _files.PublicLinkAsync(account.Name, entry.Path)); }
            catch (Exception ex) { result = CloudShareLinks.FromFailure(ex); }
            if (result is { Outcome: CloudShareLinkOutcome.Copied, Url: { } url })
            {
                try { (CopyLink ?? (value => Clipboard.SetText(value)))(url); }
                // The link exists by now, so a clipboard that will not take it must be said as that rather than
                // as "no link", which would send the user to retry a request the cloud has already answered.
                catch (Exception ex) { return new CloudShareLinkResult(CloudShareLinkOutcome.CopiedNotStored, Url: url, Detail: RcloneUiError.Describe(ex, "cloud-share-clipboard")); }
                return result;
            }
            return result;
        }
        return new CloudShareLinkResult(CloudShareLinkOutcome.Declined);
    }

    /// <summary>Runs one share request and tells the user what it was. The sentence is the whole product here:
    /// a link the user cannot see explained is a grant they did not know they made.</summary>
    internal async Task ShareAsync(CloudFileEntry entry) =>
        StatusText.Text = CloudShareLinks.Explain(await ShareLinkAsync(entry));

    /// <summary>Builds the row menu. Its contents depend on what is selected, so it is rebuilt on each open.</summary>
    private void FileList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (BuildRowMenu() is not { } menu) { e.Handled = true; return; }
        FileList.ContextMenu = menu;
    }

    /// <summary>
    /// The row menu for the current selection, or <see langword="null"/> when there is nothing to put a menu
    /// on. The commands are decided apart from WPF's opening event so which ones a selection earns — 属性 for
    /// one row and for many, a folder's 属性 but never its checksum — is checkable without a shown window.
    /// </summary>
    internal ContextMenu? BuildRowMenu()
    {
        var selected = SelectedEntries();
        if (selected.Count == 0) return null;
        var menu = new ContextMenu();
        // The menu is built in code, so nothing in the XAML names it for UI Automation; the id is what lets the
        // desktop scenario prove the row menu is reachable in the real shell rather than only on paper.
        AutomationProperties.SetAutomationId(menu, "CloudRowMenu");
        if (selected.Count == 1)
        {
            var entry = selected[0];
            if (entry.IsDirectory)
            {
                Add(menu, "打开", async () => await NavigateToAsync(entry.Path));
            }
            else
            {
                // 预览 only for a row it can actually paint: a command that can only refuse is noise, not a
                // shortcut — the same rule that keeps 还原 off a row that did not come from the trash.
                if (CloudBrowserPreview.OffersPreview(entry))
                    Add(menu, "预览", async () => await ShowPreviewAsync(entry));
                Add(menu, "下载…", async () => await DownloadAsync(entry));
                Add(menu, "打开", async () => await OpenAsync(entry));
                // A link is the one command here that gives somebody else access, so it is offered per file and
                // never for a folder: the backend's own answer for a prefix is 404, and a folder would grant a
                // whole subtree the selection never named.
                Add(menu, "复制分享链接…", async () => await ShareAsync(entry));
            }
            menu.Items.Add(new Separator());
            Add(menu, "重命名…", async () => await PromptRenameAsync(entry));
        }
        else
        {
            Add(menu, $"下载所选的 {selected.Count} 项…", DownloadSelectionAsync);
            menu.Items.Add(new Separator());
        }
        // Both commands hold the whole selection, exactly as Explorer's do, and the difference between them is
        // only visible at the destination: a cut deletes once its copy lands.
        Add(menu, "复制", () => { SetClipboard(CloudClipboardMode.Copy, selected); return Task.CompletedTask; });
        Add(menu, "剪切", () => { SetClipboard(CloudClipboardMode.Cut, selected); return Task.CompletedTask; });
        // Which of the three move-commands a selection earns is decided by the same rules that run it: 还原 only
        // for rows that came out of the trash, 移到回收站 only for rows the trash can still accept, 清空回收站
        // only while looking at the trash. An item that would only refuse is noise, not a shortcut.
        var root = Selected?.RootPath ?? "";
        if (CloudRecycleBin.SelectForRestore(root, selected).Count > 0)
            Add(menu, selected.Count == 1 ? "还原…" : $"还原所选的 {selected.Count} 项…", async () => await RestoreAsync(selected));
        if (CloudRecycleBin.SelectForMove(root, selected).Refusal is null)
            Add(menu, selected.Count == 1 ? "移到回收站…" : $"把所选的 {selected.Count} 项移到回收站…", async () => await RecycleAsync(selected));
        Add(menu, selected.Count == 1 ? "删除…" : $"删除所选的 {selected.Count} 项…", DeleteSelectedAsync);
        if (CloudRecycleBin.IsTrashView(root, _currentPath))
            Add(menu, "清空回收站…", EmptyTrashAsync);
        Add(menu, "新建文件夹…", PromptNewFolderAsync);
        // 属性 is last and after a separator, like Explorer's: it is the read-only answer, and the one that
        // must never be mistaken for an action that changes the cloud.
        menu.Items.Add(new Separator());
        Add(menu, selected.Count == 1 ? "属性" : $"查看所选 {selected.Count} 项的属性",
            selected.Count == 1 ? new Func<Task>(() => ShowDetailsAsync(selected[0])) : () => { ShowSelectionDetails(); return Task.CompletedTask; });
        return menu;

        static void Add(ContextMenu menu, string header, Func<Task> action)
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => await action();
            menu.Items.Add(item);
        }
    }

    private void OpenTransfers_Click(object sender, RoutedEventArgs e) => ShowTransferWindow();

    /// <summary>
    /// Reuses the single transfer window rather than owning a new one: an owned window is closed by WPF
    /// when its owner closes, which would hide progress and cancellation while transfers keep running.
    /// </summary>
    private void ShowTransferWindow()
    {
        var existing = Application.Current?.Windows.OfType<CloudTransferWindow>().FirstOrDefault();
        if (existing is null)
        {
            new CloudTransferWindow(_transfers).Show();
            return;
        }
        if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
        existing.Activate();
    }

    private void Transfers_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CloudTransferQueue.OverallPercentage)) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => TransferProgress.Value = _transfers.OverallPercentage); return; }
        TransferProgress.Value = _transfers.OverallPercentage;
    }

    /// <summary>Reports a failure on the status line and, for an action the user asked for, in a dialog.</summary>
    private void ReportFailure(string title, Exception exception, string operation)
    {
        var message = RcloneUiError.Describe(exception, operation);
        StatusText.Text = title + "：" + message;
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // ---- Drag in: upload whatever Explorer hands us, preserving dropped folder structure ----

    private void FileBrowser_DragOver(object sender, DragEventArgs e)
    {
        var acceptable = Selected is not null && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (!acceptable) { DropHint.Visibility = Visibility.Collapsed; return; }
        DropHintText.Text = $"上传到 {Selected!.Name}:/{_currentPath}";
        DropHint.Visibility = Visibility.Visible;
    }

    private void FileBrowser_DragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    private async void FileBrowser_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (Selected is null) { StatusText.Text = "请先选择云盘。"; return; }
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0) return;
        e.Handled = true;
        var items = CloudDropPlanner.Expand(dropped, _currentPath);
        if (items.Count == 0) { StatusText.Text = "拖入的内容中没有可上传的文件。"; return; }
        await EnqueueUploadsAsync(items);
    }

    private async void UploadFiles_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) { StatusText.Text = "请先选择云盘。"; return; }
        var dialog = new OpenFileDialog { Multiselect = true, Title = "选择要上传的文件" };
        if (dialog.ShowDialog(this) != true) return;
        await EnqueueUploadsAsync(CloudDropPlanner.Expand(dialog.FileNames, _currentPath));
    }

    private async void UploadFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) { StatusText.Text = "请先选择云盘。"; return; }
        var dialog = new OpenFolderDialog { Title = "选择要上传的文件夹" };
        if (dialog.ShowDialog(this) != true) return;
        await EnqueueUploadsAsync(CloudDropPlanner.Expand([dialog.FolderName], _currentPath));
    }

    /// <summary>
    /// Queues a batch of uploads. A same-name conflict is answered here exactly the way a 粘贴 answers one —
    /// the saved policy for the whole batch, or one question per name whose answer can cover the rest — so a
    /// dropped tree and a clipboard paste cannot give different answers about the same file, and 保留两者 is
    /// reachable from both. Each destination directory is listed once, because a dropped tree would otherwise
    /// list a directory per file.
    /// </summary>
    private async Task EnqueueUploadsAsync(IReadOnlyList<CloudDropPlanner.UploadItem> items)
    {
        if (Selected is not CloudEndpointAccount account) { StatusText.Text = "请先选择云盘。"; return; }
        if (items.Count == 0) { StatusText.Text = "没有可上传的文件。"; return; }

        // One listing per destination directory: a dropped tree would otherwise list a directory per file.
        var listings = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var directory in items.Select(item => CloudTransferQueue.ParentOf(item.RemotePath)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in await _files.ListAsync(account.Name, directory)) if (!entry.IsDirectory) names.Add(entry.Name);
                listings[directory] = names;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "无法读取目标目录：" + RcloneUiError.Describe(ex, "cloud-upload-conflict");
            return;
        }

        var destinations = items.Select(item => item.RemotePath).ToArray();
        var conflicts = destinations
            .Where(path => listings[CloudTransferQueue.ParentOf(path)].Contains(CloudTransferTask.NameOf(path)))
            .ToArray();
        var placements = PlaceDestinations(destinations, conflicts, directory => listings[directory]);
        if (placements is null) { StatusText.Text = "已取消上传。"; return; }
        var accepted = new List<CloudDropPlanner.UploadItem>();
        var renamed = 0;
        var skipped = 0;
        // One placement per destination, in this same order, so the rows pair with the answers by position —
        // and must not pair by name: two dropped folders can hold one same-named file, and both of those rows
        // aim at the same remote name, which is exactly the case that renames them apart.
        foreach (var (item, placement) in items.Zip(placements))
        {
            if (placement.Choice == CloudConflictChoice.Skip) { skipped++; continue; }
            if (placement.Destination != item.RemotePath) renamed++;
            accepted.Add(item with { RemotePath = placement.Destination });
        }
        if (accepted.Count == 0) { StatusText.Text = "已跳过全部同名文件。"; return; }

        var uri = EndpointUri(account);
        foreach (var item in accepted)
            _transfers.Enqueue(CloudTransferDirection.Upload, uri, CloudFileManagerService.RelativeToEndpoint(account, item.RemotePath), item.LocalPath, new FileInfo(item.LocalPath).Length);

        StatusText.Text = $"已加入传输队列：{accepted.Count} 个文件。"
            + (renamed > 0 ? $"其中 {renamed} 个同名文件改名为保留两者。" : "")
            + (skipped > 0 ? $"已跳过 {skipped} 个同名文件。" : "");
        ShowTransferWindow();
        await BrowseAsync();
    }

    // ---- Drag out: hand Explorer a real file so the drop lands in the folder the user chose ----

    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(FileList);
        _dragArmed = true;
    }

    private async void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        // Only a gesture that began on a row drags a file; a scrollbar or header drag must stay native.
        if (ItemsControl.ContainerFromElement(FileList, e.OriginalSource as DependencyObject) is not ListViewItem) return;
        if (FileList.SelectedItem is not CloudFileEntry entry) return;
        var position = e.GetPosition(FileList);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragArmed = false;
        await BeginDragOutAsync(entry);
    }

    private async Task BeginDragOutAsync(CloudFileEntry entry)
    {
        if (Selected is not CloudEndpointAccount account) return;
        if (entry.IsDirectory)
        {
            StatusText.Text = "暂不支持直接拖出整个文件夹，请先进入文件夹再拖出其中的文件。";
            return;
        }

        var relative = CloudFileManagerService.RelativeToEndpoint(account, entry.Path);
        if (relative.Length == 0) return;
        var local = CloudDragCache.LocalPathFor(account.Name, relative);

        CloudTransferTask? task = null;
        if (!CloudDragCache.IsUsable(local, entry.Size))
        {
            task = _transfers.Enqueue(CloudTransferDirection.Download, EndpointUri(account), relative, local, entry.Size);
            ShowTransferWindow();
            // Explorer copies the file at drop time, so the bytes must exist first. A small file often
            // finishes inside the same gesture, which lets the drop land in the target folder for real;
            // a large one falls through to the reveal path instead of stalling the drag.
            await Task.WhenAny(_transfers.WaitForAsync(task), Task.Delay(DragHydrationTimeout));
        }

        if (CloudDragCache.IsUsable(local, entry.Size) && Mouse.LeftButton == MouseButtonState.Pressed)
        {
            StatusText.Text = "正在拖出：" + entry.Name;
            DragDrop.DoDragDrop(FileList, new DataObject(DataFormats.FileDrop, new[] { local }), DragDropEffects.Copy);
            StatusText.Text = "已拖出：" + entry.Name;
            return;
        }

        if (task is not null) RevealWhenReady(task, local);
    }

    /// <summary>
    /// The fallback the user asked for: when the download cannot finish inside the drag gesture, finish it
    /// in the background and then put the user in front of the file so a second drag lands in their folder.
    /// </summary>
    private void RevealWhenReady(CloudTransferTask task, string local)
    {
        _ = RevealAsync(task, local);

        async Task RevealAsync(CloudTransferTask pending, string path)
        {
            if (!await _transfers.WaitForAsync(pending) || !File.Exists(path)) return;
            await Dispatcher.InvokeAsync(() =>
            {
                StatusText.Text = $"“{Path.GetFileName(path)}”已下载完成，把它从打开的文件夹拖到目标位置即可。";
                RevealInExplorer(path);
            });
        }
    }

    private static void RevealInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Unable to reveal a downloaded cloud file: " + ex); }
    }

    private static string EndpointUri(CloudEndpointAccount account) =>
        CloudEndpointService.BuildUri(CloudEndpointService.KindFromRcloneType(account.Type), account.Name, account.RootPath);
}
