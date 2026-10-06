using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FengSync.Core;
using FengSync.Core.Configuration;
using FengSync.Localization;
using FengSync.Services;

namespace FengSync.Views;

/// <summary>
/// The standalone transfer window (独立传输窗口). It is deliberately a separate window rather than an
/// inline panel: uploads and downloads keep running while the user browses another directory, and the
/// window is the single place that owns per-task progress, pausing, cancellation, and 断点续传.
/// </summary>
/// <remarks>
/// Everything that leaves the process — the clipboard, a browser window, Explorer, a shutdown — goes through a
/// settable seam, for the same reason the file browser does it: the decision is worth testing and the side
/// effect is not. The seams are <see cref="CopyToClipboard"/>, <see cref="ShowText"/>, <see cref="OpenFolder"/>,
/// <see cref="ShareAsync"/>, <see cref="ConfirmShare"/>, <see cref="ShowHistory"/>, and the two post-transfer
/// hooks.
/// </remarks>
public partial class CloudTransferWindow : Window
{
    /// <summary>The concurrency values the picker offers; the queue clamps anything else anyway.</summary>
    private static readonly int[] ConcurrencyOptions = [1, 2, 3, 4, 6, 8];

    private readonly CloudTransferQueue _queue;
    private readonly CloudFileManagerService? _files;
    private readonly CloudCompletionNotifier _notifier;
    private readonly CloudPostTransferAction _afterTransfer;

    public CloudTransferWindow(CloudTransferQueue queue) : this(queue, null) { }

    /// <param name="files">
    /// The service 复制分享链接 asks, so the queue reuses the call B4 already built instead of reaching for
    /// rclone itself. Absent means the link command reports that it is unavailable rather than opening a second
    /// connection to the same cloud; both shells pass the browser's own service in.
    /// </param>
    internal CloudTransferWindow(CloudTransferQueue queue, CloudFileManagerService? files)
    {
        _queue = queue;
        _files = files;
        _notifier = new CloudCompletionNotifier(queue);
        _afterTransfer = new CloudPostTransferAction(queue);
        InitializeComponent();
        TaskList.ItemsSource = _queue.Tasks;
        _queue.PropertyChanged += Queue_PropertyChanged;
        _notifier.Notified += Notifier_Notified;
        _afterTransfer.Noticed += Notifier_Notified;
        // The queue is application-owned, so another window may enqueue while this one is open.
        _queue.Tasks.CollectionChanged += Tasks_CollectionChanged;
        Loaded += async (_, _) => await LoadResumableAsync();
        Closed += (_, _) =>
        {
            _queue.PropertyChanged -= Queue_PropertyChanged;
            _queue.Tasks.CollectionChanged -= Tasks_CollectionChanged;
            _notifier.Notified -= Notifier_Notified;
            _afterTransfer.Noticed -= Notifier_Notified;
            _notifier.Dispose();
            _afterTransfer.Dispose();
        };
        BuildConcurrencyOptions();
        BuildAfterTransferOptions();
        UpdateState();
    }

    public CloudTransferQueue Queue => _queue;

    // ---- seams: the decisions are here, the side effects are replaceable ---------------------------------

    /// <summary>Puts text on the system clipboard. The system clipboard is machine state, so it is a seam.</summary>
    internal Action<string>? CopyToClipboard { get; set; }

    /// <summary>Shows a read-only block of text (the detail or the log). A seam so no window opens in a test.</summary>
    internal Action<string, string>? ShowText { get; set; }

    /// <summary>Shows a folder in Explorer.</summary>
    internal Action<string>? OpenFolder { get; set; }

    /// <summary>Asks the cloud for a link. A seam so the queue's side of C7 is testable without a cloud.</summary>
    internal Func<CloudTransferTask, Task<CloudShareLinkResult>>? ShareAsync { get; set; }

    /// <summary>Confirms a link before it is created. A seam because the real one is a modal dialog.</summary>
    internal Func<string, bool>? ConfirmShare { get; set; }

    /// <summary>Opens the archive. A seam because the real one is a modal window.</summary>
    internal Action? ShowHistory { get; set; }

    /// <summary>Confirms a machine-level action before it happens.</summary>
    internal Func<CloudAfterTransferAction, bool>? ConfirmPostTransfer
    {
        get => _afterTransfer.Confirm;
        set => _afterTransfer.Confirm = value;
    }

    /// <summary>Performs a machine-level action. Never the real one in a test.</summary>
    internal Action<CloudAfterTransferAction>? PerformPostTransfer
    {
        get => _afterTransfer.Perform;
        set => _afterTransfer.Perform = value;
    }

    /// <summary>The menu most recently built for a row, so a test can assert what a right-click offers.</summary>
    internal IReadOnlyList<CloudTransferRowMenuItem> LastMenu { get; private set; } = [];

    /// <summary>The task the last menu was built for.</summary>
    internal CloudTransferTask? LastMenuTask { get; private set; }

    internal string PolicyLabel => PolicyText.Text;
    internal string LimitErrorLabel => LimitErrorText.Text;
    internal bool LimitErrorVisible => LimitErrorText.Visibility == Visibility.Visible;
    internal string CompletionLabel => CompletionText.Text;
    internal string HintLabel => HintText.Text;

    /// <summary>Runs one row command as the menu would, so a test drives the real dispatch.</summary>
    internal async Task InvokeRowCommandAsync(CloudTransferTask task, CloudTransferRowCommand command)
    {
        var menu = CloudTransferRowMenu.Build(task, new CloudTransferRowContext(_queue.CanMoveUp(task), _queue.CanMoveDown(task)));
        LastMenu = menu;
        LastMenuTask = task;
        var item = menu.First(entry => entry.Command == command);
        if (!item.Enabled) { HintText.Text = item.Hint ?? Tr.F("Transfer.ActionUnavailable"); return; }
        await ExecuteAsync(task, command);
    }

    private async Task ExecuteAsync(CloudTransferTask task, CloudTransferRowCommand command)
    {
        switch (command)
        {
            case CloudTransferRowCommand.MoveUp: _queue.MoveUp(task); UpdateState(); break;
            case CloudTransferRowCommand.MoveDown: _queue.MoveDown(task); UpdateState(); break;
            case CloudTransferRowCommand.OpenFolder: OpenTaskFolder(task); break;
            case CloudTransferRowCommand.CopyLink: await ShareAsync2(task); break;
            case CloudTransferRowCommand.CopyDetail: CopyDetail(task); break;
            case CloudTransferRowCommand.ViewLog: ShowLog(); break;
        }
    }

    /// <summary>
    /// Fills the picker from the queue's own current value, so a concurrency changed elsewhere — the
    /// settings page, or a previous session of this window — is what the box shows rather than the default.
    /// Rebuilt from scratch because the settings load can lower or raise the current value afterwards.
    /// </summary>
    private void BuildConcurrencyOptions()
    {
        ConcurrencyBox.SelectionChanged -= Concurrency_Changed;
        ConcurrencyBox.Items.Clear();
        foreach (var option in ConcurrencyOptions) ConcurrencyBox.Items.Add(option);
        if (!ConcurrencyOptions.Contains(_queue.Concurrency)) ConcurrencyBox.Items.Add(_queue.Concurrency);
        ConcurrencyBox.SelectedItem = _queue.Concurrency;
        ConcurrencyBox.SelectionChanged += Concurrency_Changed;
    }

    private void BuildAfterTransferOptions()
    {
        AfterTransferBox.SelectionChanged -= AfterTransfer_Changed;
        AfterTransferBox.Items.Clear();
        foreach (var action in Enum.GetValues<CloudAfterTransferAction>()) AfterTransferBox.Items.Add(CloudPostTransferAction.Describe(action));
        AfterTransferBox.SelectedIndex = (int)_afterTransfer.Action;
        AfterTransferBox.SelectionChanged += AfterTransfer_Changed;
    }

    private void Tasks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateState();

    private async Task LoadResumableAsync()
    {
        try
        {
            // The window is also reachable from DriveHub, which has no main window to push settings onto the
            // shared queue, so the queue is configured from here too. Assigning the values twice is harmless.
            var settings = (await new SettingsStore().LoadAsync()).Settings;
            CloudTransferPolicy.Apply(_queue, settings);
            _afterTransfer.Action = settings.CloudAfterTransfer;
            _notifier.Enabled = settings.CloudNotifyOnCompletion;
            BuildConcurrencyOptions();
            BuildAfterTransferOptions();
            await _queue.LoadResumableAsync();
        }
        catch (Exception ex) { HintText.Text = Tr.Format("Transfer.LoadResumableFailed", RcloneUiError.Describe(ex, "cloud-transfer-journal")); }
        UpdateState();
    }

    private void Notifier_Notified(object? sender, string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Notifier_Notified(sender, message)); return; }
        CompletionText.Text = message;
        CompletionBanner.Visibility = Visibility.Visible;
    }

    private void Queue_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Progress ticks arrive from background threads; the summary and button enablement are UI state.
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(UpdateState); return; }
        UpdateState();
    }

    private void UpdateState()
    {
        SummaryText.Text = _queue.SummaryText;
        // A new batch makes the previous batch's "已完成" line stale, so it goes away as soon as work resumes.
        if (_queue.HasActiveTransfers) CompletionBanner.Visibility = Visibility.Collapsed;
        ResumeButton.IsEnabled = _queue.HasResumableTasks;
        PauseAllButton.IsEnabled = _queue.HasActiveTransfers;
        CancelAllButton.IsEnabled = _queue.HasActiveTransfers;
        ClearButton.IsEnabled = _queue.HasCompletedTasks;
        HistoryButton.IsEnabled = _queue.History is not null;
        ConcurrencyHint.Text = Tr.Format("Transfer.ConcurrencyCurrent", _queue.Concurrency)
            + (_queue.MaxRetries > 0 ? Tr.Format("Transfer.AutoRetry", _queue.MaxRetries) : Tr.F("Transfer.NoAutoRetry"));
        PolicyText.Text = DescribePolicy();
        var limitError = _queue.LimitError;
        LimitErrorText.Text = limitError ?? "";
        LimitErrorText.Visibility = string.IsNullOrEmpty(limitError) ? Visibility.Collapsed : Visibility.Visible;
        HintText.Text = _queue.HasTasks
            ? Tr.F("Transfer.HintResumeMechanism")
              + Tr.F("Transfer.HintResumeDetail")
              // A copy is the case the sentence above only hints at: rclone moves the bytes between two
              // remotes, so this program never holds them and has no verified prefix to append to.
              + (_queue.HasCopyTasks ? Tr.F("Transfer.HintCopy") : "")
              + Tr.F("Transfer.HintContextMenu")
            : Tr.F("Transfer.HintDragDrop");
    }

    /// <summary>
    /// The one line that explains what the queue is doing about rate and time, including the case where the
    /// answer is "nothing is happening on purpose".
    /// </summary>
    private string DescribePolicy()
    {
        var parts = new List<string> { _queue.LimitText };
        if (_queue.TimeWindow is { } window) parts.Add(Tr.Format("Transfer.TransferWindow", window.Describe()) + (_queue.WaitingForWindow ? Tr.F("Transfer.WindowWaiting") : ""));
        else parts.Add(Tr.F("Transfer.NoWindow"));
        return string.Join(" · ", parts) + Tr.F("Transfer.PolicyFooter");
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        var resumed = _queue.ResumeFinished();
        UpdateState();
        if (resumed == 0) HintText.Text = Tr.F("Transfer.NoResumable");
    }

    private void PauseAll_Click(object sender, RoutedEventArgs e)
    {
        var paused = _queue.PauseAll();
        UpdateState();
        if (paused == 0) HintText.Text = Tr.F("Transfer.NoPausable");
    }

    private void CancelAll_Click(object sender, RoutedEventArgs e)
    {
        _queue.CancelAll();
        UpdateState();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var cleared = _queue.ClearFinished();
        UpdateState();
        HintText.Text = cleared == 0
            ? Tr.F("Transfer.NoCompleted")
            : _queue.History is not null
                ? Tr.Format("Transfer.ClearedArchived", cleared)
                : Tr.Format("Transfer.Cleared", cleared);
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        if (_queue.History is null) { HintText.Text = Tr.F("Transfer.NoHistoryArchive"); return; }
        (ShowHistory ?? ShowHistoryWindow)();
    }

    private void ShowHistoryWindow() => new CloudTransferHistoryWindow(_queue.History!) { Owner = this }.ShowDialog();

    private void PauseTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CloudTransferTask task) return;
        _queue.Pause(task);
        UpdateState();
    }

    private void RestartTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CloudTransferTask task) return;
        _queue.Retry(task);
        UpdateState();
    }

    private void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CloudTransferTask task) return;
        _queue.Cancel(task);
        UpdateState();
    }

    /// <summary>
    /// Applies a new concurrency to the live queue. It is intentionally not persisted here: the window is a
    /// per-session control surface, and the startup default belongs to the settings page where it survives
    /// a restart.
    /// </summary>
    private void Concurrency_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ConcurrencyBox.SelectedItem is not int value) return;
        _queue.Concurrency = value;
        UpdateState();
    }

    /// <summary>
    /// Records the "when it finishes" choice for this session. Persisted from the settings page instead,
    /// because a choice that can shut a machine down should be visible on the page that survives a restart.
    /// </summary>
    private void AfterTransfer_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AfterTransferBox.SelectedIndex < 0) return;
        _afterTransfer.Action = (CloudAfterTransferAction)AfterTransferBox.SelectedIndex;
        if (_afterTransfer.Action == CloudAfterTransferAction.None) return;
        HintText.Text = Tr.Format("Transfer.AfterTransferChosen", CloudPostTransferAction.Describe(_afterTransfer.Action))
            + Tr.F("Transfer.AfterTransferKeepOpen");
    }

    // ---- the row menu -----------------------------------------------------------------------------------

    private void TaskList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (TaskList.SelectedItem is not CloudTransferTask task) { e.Handled = true; return; }
        var menu = CloudTransferRowMenu.Build(task, new CloudTransferRowContext(_queue.CanMoveUp(task), _queue.CanMoveDown(task)));
        LastMenu = menu;
        LastMenuTask = task;
        var context = new ContextMenu();
        foreach (var item in menu)
        {
            if (item.SeparatorBefore && context.Items.Count > 0) context.Items.Add(new Separator());
            var entry = new MenuItem { Header = item.Label, IsEnabled = item.Enabled, ToolTip = item.Hint };
            var command = item.Command;
            entry.Click += async (_, _) => await ExecuteAsync(task, command);
            context.Items.Add(entry);
        }
        TaskList.ContextMenu = context;
    }

    private void OpenTaskFolder(CloudTransferTask task)
    {
        var folder = CloudTransferRowMenu.LocalFolderOf(task);
        if (folder is null) { HintText.Text = Tr.F("Transfer.NoLocalFolder"); return; }
        try { (OpenFolder ?? (value => Process.Start(new ProcessStartInfo(value) { UseShellExecute = true })))(folder); }
        catch (Exception ex) { HintText.Text = Tr.Format("Transfer.OpenFolderFailed", RcloneUiError.Describe(ex, "cloud-transfer-folder")); }
    }

    private void CopyDetail(CloudTransferTask task)
    {
        var text = CloudTransferDiagnostics.DetailFor(task);
        var copied = true;
        try { (CopyToClipboard ?? Clipboard.SetText)(text); }
        catch (Exception ex)
        {
            copied = false;
            (ShowText ?? ShowTextWindow)(Tr.F("Transfer.DetailTitle"), text + Environment.NewLine + Environment.NewLine + RcloneUiError.Describe(ex, "cloud-transfer-clipboard"));
        }
        HintText.Text = CloudTransferDiagnostics.DescribeCopy(copied);
    }

    private void ShowLog() => (ShowText ?? ShowTextWindow)(Tr.F("Transfer.LogTitle"), CloudTransferDiagnostics.ReadLog(CloudTransferDiagnostics.LogPath(_queue.Clock())));

    /// <summary>
    /// Asks the cloud for a link to the task's remote object and copies it. The confirmation, the outcome
    /// vocabulary and the wording all come from B4 — this path only has to name the object, which for an upload
    /// is the destination it just wrote and for everything else is the source it read.
    /// </summary>
    private async Task ShareAsync2(CloudTransferTask task)
    {
        var name = task.Direction == CloudTransferDirection.Upload
            ? System.IO.Path.GetFileName(task.LocalPath)
            : CloudTransferTask.NameOf(task.RemotePath);
        if (!(ConfirmShare ?? (message => TextPrompt.Confirm(this, CloudShareLinks.ConfirmationTitle, message)))(CloudShareLinks.ConfirmationFor(name)))
        {
            HintText.Text = CloudShareLinks.Explain(new CloudShareLinkResult(CloudShareLinkOutcome.Declined));
            return;
        }
        CloudShareLinkResult result;
        try { result = await (ShareAsync ?? ShareDefaultAsync)(task); }
        catch (Exception ex) { result = CloudShareLinks.FromFailure(ex); }
        if (result is { Outcome: CloudShareLinkOutcome.Copied, Url: { } url })
        {
            try { (CopyToClipboard ?? Clipboard.SetText)(url); }
            catch (Exception ex) { result = new CloudShareLinkResult(CloudShareLinkOutcome.CopiedNotStored, url, RcloneUiError.Describe(ex, "cloud-transfer-clipboard")); }
        }
        HintText.Text = CloudShareLinks.Explain(result);
    }

    private async Task<CloudShareLinkResult> ShareDefaultAsync(CloudTransferTask task)
    {
        if (_files is null) return new CloudShareLinkResult(CloudShareLinkOutcome.Unsupported, Detail: Tr.F("Transfer.NoCloudService"));
        if (EndpointSchemes.RemoteNameOf(task.RemoteUri) is not { } remote)
            return new CloudShareLinkResult(CloudShareLinkOutcome.Failed, Detail: Tr.F("Transfer.NotAnEndpoint"));
        return CloudShareLinks.FromAnswer(await _files.PublicLinkAsync(remote, task.RemotePath));
    }

    /// <summary>
    /// The read-only text window, built the same way the browser's prompts are: a plain scrollable box rather
    /// than a page, because it shows one opaque string and has no layout worth sharing.
    /// </summary>
    private void ShowTextWindow(string title, string body)
    {
        var box = new TextBox
        {
            Text = body,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(14)
        };
        new Window
        {
            Title = title,
            Owner = this,
            Content = box,
            Width = 720,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        }.ShowDialog();
    }
}
