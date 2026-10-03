using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly CloudFileManagerService _files = new();
    /// <summary>
    /// Shared so uploads and downloads keep running after this window closes; falls back to a private
    /// queue only when no shell installed its services (a preview or a test).
    /// </summary>
    private readonly CloudTransferQueue _transfers = AppServices.TryCurrent?.CloudTransfers ?? new CloudTransferQueue();
    private ApplicationSettings _settings = new();
    private Point _dragStart;
    private bool _dragArmed;
    private string _currentPath = "";
    /// <summary>
    /// True while the listing came from a recursive search. The search box then no longer filters it: the
    /// results were matched on the whole path, so re-filtering them by name would hide the folder hits the
    /// search deliberately included.
    /// </summary>
    private bool _listingIsSearchResult;
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

    public RemoteEndpointManagerWindow()
    {
        InitializeComponent();
        EndpointList.ItemsSource = _accounts;
        MountsList.ItemsSource = _mounts;
        FileList.ItemsSource = _entries;
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

    /// <summary>The endpoints the list loaded, before the search box and the sort choice are applied.</summary>
    internal IReadOnlyList<CloudEndpointRow> LoadedEndpoints => [.. _loaded];
    /// <summary>The endpoint rows the list is showing, already filtered and ordered.</summary>
    internal IReadOnlyList<CloudEndpointRow> VisibleEndpoints => [.. _accounts];

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
        await RefreshAsync();
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
        if (Selected?.Name == _browsedEndpoint) return;
        _browsedEndpoint = Selected?.Name;
        _currentPath = Selected?.RootPath ?? "";
        // History is per endpoint: "the directory before this one" means nothing across two accounts.
        _history.Clear();
        _historyIndex = -1;
        if (Selected is not null) PushHistory(_currentPath);
        await BrowseAsync();
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
        if (_rearranging) return;
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
        // A folder is downloaded by entering it and taking its files; the queue's unit is a file.
        DownloadButton.IsEnabled = selected.Count > 0 && selected.All(entry => !entry.IsDirectory);
    }

    private IReadOnlyList<CloudFileEntry> SelectedEntries() => [.. FileList.SelectedItems.Cast<CloudFileEntry>()];

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFileButtons();

    private void UpdateHistoryButtons()
    {
        HistoryBackButton.IsEnabled = _historyIndex > 0;
        HistoryForwardButton.IsEnabled = _historyIndex >= 0 && _historyIndex + 1 < _history.Count;
    }

    /// <summary>
    /// Moves to a directory and records it. <paramref name="record"/> is false when the move came from
    /// 后退 or 前进 itself, which must not push a new entry or the two would ping-pong forever.
    /// </summary>
    private async Task NavigateToAsync(string path, bool record = true)
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
        await OpenAsync(entry);
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

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedEntries();
        if (selected.Count != 1) { StatusText.Text = "请先选择要重命名的单个项目。"; return; }
        await PromptRenameAsync(selected[0]);
    }

    private async Task PromptRenameAsync(CloudFileEntry entry)
    {
        var name = TextPrompt.Ask(this, "重命名", $"“{entry.Name}”的新名称：", entry.Name, CloudFileNames.Validate);
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

    private async void DeleteEntries_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedEntries();
        if (selected.Count == 0) { StatusText.Text = "请先选择要删除的项目。"; return; }
        await ConfirmAndDeleteAsync(selected);
    }

    /// <summary>
    /// Asks before deleting, and asks twice when the batch is large or the backend cannot undo it: the first
    /// dialog is easy to dismiss by reflex, and there is no undo behind either of them.
    /// </summary>
    private async Task ConfirmAndDeleteAsync(IReadOnlyList<CloudFileEntry> entries)
    {
        var confirmation = CloudDeleteSafety.Describe(entries, CloudDeleteSafety.SupportsTrash(KindOf(Selected)));
        if (!TextPrompt.Confirm(this, "删除", confirmation.Message, danger: true)) return;
        if (confirmation.IsBulk
            && !TextPrompt.Confirm(this, "再次确认删除",
                $"即将删除 {entries.Count} 项，其中文件夹会连同其全部内容一起删除。\n\n确定继续？", danger: true)) return;
        await DeleteEntriesAsync(entries);
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
            _transfers.Enqueue(CloudTransferDirection.Download, EndpointUri(account), ToEndpointRelative(account, file.Path), Path.Combine(directory, file.Name), file.Size);
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
        _transfers.Enqueue(CloudTransferDirection.Download, EndpointUri(account), ToEndpointRelative(account, entry.Path), destination, entry.Size);
        StatusText.Text = "已加入传输队列：" + entry.Name;
        ShowTransferWindow();
        await Task.CompletedTask;
    }

    private async Task OpenAsync(CloudFileEntry entry)
    {
        if (Selected is not CloudEndpointAccount account) return;
        var folder = Path.Combine(Path.GetTempPath(), "FengSync", "cloud-open", Guid.NewGuid().ToString("N"));
        var local = Path.Combine(folder, entry.Name);
        var task = _transfers.Enqueue(CloudTransferDirection.Download, EndpointUri(account), ToEndpointRelative(account, entry.Path), local, entry.Size);
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

    /// <summary>Builds the row menu. Its contents depend on what is selected, so it is rebuilt on each open.</summary>
    private void FileList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var selected = SelectedEntries();
        if (selected.Count == 0) { e.Handled = true; return; }
        var menu = new ContextMenu();
        if (selected.Count == 1)
        {
            var entry = selected[0];
            if (entry.IsDirectory)
            {
                Add(menu, "打开", async () => await NavigateToAsync(entry.Path));
            }
            else
            {
                Add(menu, "下载…", async () => await DownloadAsync(entry));
                Add(menu, "打开", async () => await OpenAsync(entry));
            }
            menu.Items.Add(new Separator());
            Add(menu, "重命名…", async () => await PromptRenameAsync(entry));
        }
        else
        {
            Add(menu, $"下载所选的 {selected.Count} 项…", DownloadSelectionAsync);
            menu.Items.Add(new Separator());
        }
        Add(menu, selected.Count == 1 ? "删除…" : $"删除所选的 {selected.Count} 项…", async () => await ConfirmAndDeleteAsync(selected));
        Add(menu, "新建文件夹…", PromptNewFolderAsync);
        FileList.ContextMenu = menu;

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
    /// Queues a batch of uploads, asking once about same-name conflicts so a dropped tree cannot produce
    /// a dialog per file. Overwriting stays an explicit user choice unless the settings say otherwise,
    /// matching the single-file flow.
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

        var conflicts = items.Where(item => listings[CloudTransferQueue.ParentOf(item.RemotePath)].Contains(CloudTransferTask.NameOf(item.RemotePath))).ToList();
        var accepted = items;
        if (conflicts.Count > 0)
        {
            var overwrite = CloudDeleteSafety.OverwriteFor(_settings.CloudUploadConflict);
            if (overwrite is null)
            {
                var answer = MessageBox.Show($"{conflicts.Count} 个同名文件已存在于目标目录。\n\n是：全部覆盖；否：全部跳过；取消：取消本次上传。", "文件已存在", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Cancel) { StatusText.Text = "已取消上传。"; return; }
                if (answer == MessageBoxResult.No) accepted = items.Where(item => !conflicts.Contains(item)).ToList();
            }
            else if (overwrite == false)
            {
                accepted = items.Where(item => !conflicts.Contains(item)).ToList();
            }
        }
        if (accepted.Count == 0) { StatusText.Text = "已跳过全部同名文件。"; return; }

        var uri = EndpointUri(account);
        foreach (var item in accepted)
            _transfers.Enqueue(CloudTransferDirection.Upload, uri, ToEndpointRelative(account, item.RemotePath), item.LocalPath, new FileInfo(item.LocalPath).Length);

        StatusText.Text = $"已加入传输队列：{accepted.Count} 个文件。";
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

        var relative = ToEndpointRelative(account, entry.Path);
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

    /// <summary>
    /// The browser navigates paths relative to the rclone remote root, while <see cref="EndpointFactory"/>
    /// addresses an endpoint relative to its configured root (the account's default root folder).
    /// Transfers go through endpoints, so the root prefix has to be stripped.
    /// </summary>
    private static string ToEndpointRelative(CloudEndpointAccount account, string remotePath)
    {
        var root = account.RootPath.Trim('/');
        var path = (remotePath ?? "").Trim('/');
        if (root.Length == 0) return path;
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return "";
        return path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ? path[(root.Length + 1)..] : path;
    }

    private static string EndpointUri(CloudEndpointAccount account) =>
        CloudEndpointService.BuildUri(CloudEndpointService.KindFromRcloneType(account.Type), account.Name, account.RootPath);
}
