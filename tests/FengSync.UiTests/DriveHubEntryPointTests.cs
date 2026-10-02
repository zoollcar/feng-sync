using System.IO;
using System.Windows;
using FengSync.Core.Mount;
using FengSync.Core.Rclone.Lifecycle;
using FengSync.Services;
using FengSync.Views;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// The desktop product ships two executable entry points in one folder: FengSync (the main shell) and
/// DriveHub (the standalone cloud drive manager). These tests pin the two things that rot silently when
/// a second entry point is introduced — that the build really emits DriveHub.exe next to FengSync.exe,
/// and that the shared cloud manager window can be hosted by a shell that is not the main <c>App</c>.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class DriveHubEntryPointTests
{
    [Fact]
    public void The_build_emits_the_DriveHub_entry_point_alongside_the_main_shell()
    {
        var testFolder = new DirectoryInfo(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        // A release folder entry point must be launchable from one folder: the exe, its assembly and its
        // runtime config. A missing runtimeconfig.json is the classic "works in the IDE, not from the zip".
        foreach (var required in new[] { "FengSync.exe", "DriveHub.exe", "DriveHub.dll", "DriveHub.runtimeconfig.json" })
            Assert.True(File.Exists(Path.Combine(testFolder.FullName, required)), $"构建输出缺少 {required}：{testFolder.FullName}");

        // DriveHub is also copied beside the main shell, which is the layout inside the release zip.
        var shellFolder = MainShellOutputFolder(testFolder);
        Assert.True(File.Exists(Path.Combine(shellFolder, "FengSync.exe")), $"主程序输出目录缺少 FengSync.exe：{shellFolder}");
        Assert.True(File.Exists(Path.Combine(shellFolder, "DriveHub.exe")), $"主程序输出目录缺少 DriveHub.exe：{shellFolder}");
    }

    [Fact]
    public void Both_shells_install_the_same_application_services()
    {
        // The main shell and the standalone entry point must satisfy the same host contract; that shared
        // contract is what lets RemoteEndpointManagerWindow run under either one.
        Assert.True(typeof(IAppServiceHost).IsAssignableFrom(typeof(App)), "主程序 App 必须实现 IAppServiceHost。");

        var driveHub = typeof(FengSync.DriveHub.DriveHubApp);
        Assert.True(typeof(Application).IsAssignableFrom(driveHub), "DriveHub 入口必须是一个 WPF Application。");
        Assert.True(typeof(IAppServiceHost).IsAssignableFrom(driveHub), "DriveHub 入口必须自己安装 IAppServiceHost。");
    }

    [Fact]
    public void The_cloud_manager_window_uses_the_installed_shell_services_instead_of_App()
    {
        RunSta(() =>
        {
            var previous = AppServices.TryCurrent;
            var journalRoot = NewDirectory();
            try
            {
                var shell = new StubShell(new CloudTransferQueue(new CloudTransferJournalStore(journalRoot)));
                AppServices.Install(shell);
                EnsureApplicationResources();

                // The regression this guards: the window used to cast Application.Current to the main
                // shell's App. Here the only Application is a bare one, so that cast would throw — a
                // successful construction is the assertion.
                var window = new RemoteEndpointManagerWindow();

                // And it must drive its progress bar from the shell's shared queue, not from a private copy:
                // a task added to the installed queue has to move the window's bar.
                shell.CloudTransfers.Tasks.Add(new CloudTransferTask(
                    CloudTransferDirection.Upload, "s3://archive", "a.txt", @"C:\tmp\a.txt", 100)
                {
                    State = CloudTransferState.Running,
                    CompletedBytes = 40
                });
                // ClearFinished raises OverallPercentage without moving any bytes, so no rclone is needed.
                shell.CloudTransfers.ClearFinished();
                Assert.Equal(40d, window.TransferProgress.Value);

                window.Close();
            }
            finally
            {
                AppServices.Install(previous);
                TryDelete(journalRoot);
            }
        });
    }

    /// <summary>
    /// A shell host without a WPF Application or an rclone daemon: the rclone host is only started on
    /// first use, so this stands in for DriveHub without spawning any process.
    /// </summary>
    private sealed class StubShell : IAppServiceHost
    {
        private readonly RcloneLifecycleHost _rclone = new();

        public StubShell(CloudTransferQueue transfers)
        {
            CloudTransfers = transfers;
            MountService = new RcloneMountService(_rclone);
        }

        public RcloneLifecycleHost RcloneHost => _rclone;
        public IRcloneLifecycleClient RcloneLifecycle => _rclone;
        public RcloneMountService MountService { get; }
        public CloudTransferQueue CloudTransfers { get; }
    }

    /// <summary>The main shell's own output folder: <c>src/FengSync/bin/&lt;configuration&gt;/&lt;tfm&gt;</c>.</summary>
    private static string MainShellOutputFolder(DirectoryInfo testFolder)
    {
        for (var directory = testFolder; directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FengSync.sln")))
                return Path.Combine(directory.FullName, "src", "FengSync", "bin", testFolder.Parent!.Name, testFolder.Name);

        throw new DirectoryNotFoundException($"未能从 {testFolder.FullName} 向上定位 FengSync.sln，无法确定主程序输出目录。");
    }

    /// <summary>
    /// The windows rely on the shared theme dictionaries that the main shell merges in App.xaml. A test
    /// process has no App, so install them on whichever Application already exists (never a second one:
    /// WPF allows a single Application per AppDomain).
    /// </summary>
    private static void EnsureApplicationResources()
    {
        var application = Application.Current ?? new Application();
        if (application.Resources.MergedDictionaries.Count > 0) return;
        foreach (var resource in new[] { "DesignTokens.xaml", "Typography.xaml", "Icons.xaml", "Controls.xaml" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/FengSync;component/Themes/{resource}")
            });
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fengsync-drivehub-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
