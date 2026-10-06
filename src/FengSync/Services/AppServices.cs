using FengSync.Core.Mount;
using FengSync.Core.Rclone.Lifecycle;
using FengSync.Localization;

namespace FengSync.Services;

/// <summary>
/// The application-owned services every Feng Sync shell needs. The main shell (<see cref="App"/>) and
/// the standalone DriveHub entry point each install one, so windows and services never cast
/// <c>Application.Current</c> to a concrete shell type — that cast is what previously tied the cloud
/// manager window to the main executable.
/// </summary>
/// <remarks>
/// Deliberately excludes shell-specific services such as the SFTP server: DriveHub must not compete with
/// the main shell for the same listen port, so it neither owns nor starts one.
/// </remarks>
public interface IAppServiceHost
{
    /// <summary>Lazily owns the bundled rclone RC daemon used for cloud listing and transfers.</summary>
    RcloneLifecycleHost RcloneHost { get; }
    /// <summary>The same daemon seen through the narrower lifecycle contract used by mount management.</summary>
    IRcloneLifecycleClient RcloneLifecycle { get; }
    /// <summary>Mount discovery and lifecycle. Scoped to this process's daemon, so shells cannot fight over mounts.</summary>
    RcloneMountService MountService { get; }
    /// <summary>Application-owned cloud transfer queue shared by every window in this process.</summary>
    CloudTransferQueue CloudTransfers { get; }
}

/// <summary>
/// Process-wide install point for <see cref="IAppServiceHost"/>. Installed from an <c>Application</c>
/// constructor, which runs before any window (and before <c>StartupUri</c>) is created.
/// </summary>
public static class AppServices
{
    private static IAppServiceHost? _host;

    /// <summary>Throws when no shell installed its services, which is a programming error rather than user state.</summary>
    public static IAppServiceHost Current =>
        _host ?? throw new InvalidOperationException(Tr.F("Service.NotInstalled"));

    /// <summary>For code that can degrade gracefully instead of failing, such as a preview or a test.</summary>
    public static IAppServiceHost? TryCurrent => _host;

    /// <summary>Installs a shell's services. Pass <see langword="null"/> to detach again (tests, previews).</summary>
    public static void Install(IAppServiceHost? host) => _host = host;
}
