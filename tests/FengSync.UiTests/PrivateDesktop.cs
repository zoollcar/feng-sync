using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FengSync.UiTests;

/// <summary>
/// A private Windows desktop for the UI acceptance scenarios.
/// </summary>
/// <remarks>
/// The scenarios drive a real WPF application with real UI Automation, and that application has to open
/// real windows. Parking those windows outside the visible desktop was never enough: a window placed at
/// -32000 is still a window, it still activates, and it therefore still takes the foreground away from
/// whoever is using the machine. What actually keeps a test run out of the way is not moving the windows
/// but putting them somewhere else: a desktop object of our own.
///
/// A desktop is a display space inside a window station. Windows created on a desktop that is not the
/// active one are never composited to a monitor, and the input queue and foreground window belong to that
/// desktop too — so a process running here can raise its windows, move the pointer and take the foreground
/// as much as it likes without any of it reaching the user's session. Measured on this repository: with the
/// application started on a private desktop, the user's desktop reports zero windows owned by it, the user's
/// foreground window is the same process before, during and after, and the application's own window has
/// ordinary geometry (it is displayed, just not to anyone).
///
/// The desktop is handed to a child process through <c>STARTUPINFO.lpDesktop</c>, which only
/// <c>CreateProcess</c> can set — <see cref="ProcessStartInfo"/> has no equivalent. Children of that process
/// inherit the desktop, so launching the harness host here is enough for everything it starts in turn: the
/// application, rclone, schtasks and the CLI all stay on this desktop.
/// </remarks>
internal sealed class PrivateDesktop : IDisposable
{
    private const uint DesktopAllAccess = 0x000F01FF;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint GenericWrite = 0x40000000;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint CreateAlways = 2;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    private IntPtr _handle;

    private PrivateDesktop(IntPtr handle, string name) { _handle = handle; Name = name; }

    /// <summary>The desktop's own name, as passed to <c>CreateDesktop</c> and to the child's startup info.</summary>
    public string Name { get; }

    /// <summary>
    /// Creates a desktop for one scenario. Throws rather than falling back to the visible desktop: a run
    /// that silently lands on the user's screen is worse than a run that fails, because the whole point of
    /// this type is that it does not.
    /// </summary>
    public static PrivateDesktop Create()
    {
        var name = "FengSyncUi-" + Guid.NewGuid().ToString("N")[..12];
        var handle = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, DesktopAllAccess, IntPtr.Zero);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Could not create the private desktop '{name}' (Win32 error {Marshal.GetLastWin32Error()}).");
        return new PrivateDesktop(handle, name);
    }

    /// <summary>
    /// Starts <paramref name="application"/> on this desktop, with its standard output and error merged into
    /// <paramref name="outputPath"/>. A file rather than a pipe: the harness writes far more than a pipe
    /// buffer holds, and nothing here would be draining it while the test waits for the process to exit.
    /// </summary>
    public Process Start(string application, IReadOnlyList<string> arguments, string workingDirectory, string outputPath)
    {
        var executable = ResolveApplication(application, workingDirectory);
        var security = new SecurityAttributes { nLength = Marshal.SizeOf<SecurityAttributes>(), bInheritHandle = true };
        var output = CreateFileW(outputPath, GenericWrite, FileShareReadWrite, ref security, CreateAlways, FileAttributeNormal, IntPtr.Zero);
        if (output == InvalidHandleValue)
            throw new InvalidOperationException($"Could not create the scenario log '{outputPath}' (Win32 error {Marshal.GetLastWin32Error()}).");
        var input = CreateFileW("NUL", GenericRead, FileShareReadWrite, ref security, OpenExisting, 0, IntPtr.Zero);

        try
        {
            var startup = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = Name,
                dwFlags = (int)StartfUseStdHandles,
                hStdInput = input,
                hStdOutput = output,
                hStdError = output,
            };
            var commandLine = new StringBuilder(Quote(executable));
            foreach (var argument in arguments) commandLine.Append(' ').Append(Quote(argument));

            if (!CreateProcessW(executable, commandLine.ToString(), IntPtr.Zero, IntPtr.Zero, true,
                    CreateNoWindow | CreateUnicodeEnvironment, IntPtr.Zero, workingDirectory, ref startup, out var info))
                throw new InvalidOperationException(
                    $"Could not start '{executable}' on the private desktop (Win32 error {Marshal.GetLastWin32Error()}).");

            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return Process.GetProcessById(info.dwProcessId);
        }
        finally
        {
            CloseHandle(output);
            if (input != InvalidHandleValue) CloseHandle(input);
        }
    }

    /// <summary>
    /// Closes the desktop. A failure is not an error worth failing a scenario over — it only means a process
    /// still holds the desktop, which the operating system reclaims when this test host exits.
    /// </summary>
    public void Dispose()
    {
        var handle = _handle;
        _handle = IntPtr.Zero;
        if (handle != IntPtr.Zero) CloseDesktop(handle);
    }

    /// <summary>
    /// The executable to hand to <c>CreateProcess</c>. A bare name is not enough: <c>CreateProcess</c>
    /// searches only the application directory and the current directory, while
    /// <see cref="Process.Start(string)"/> searches <c>PATH</c> — so "pwsh" would fail here with a bare
    /// "file not found" where it works everywhere else in this suite. Resolving first keeps the two
    /// equivalent, and turns a typo into a message that names what could not be found.
    /// </summary>
    private static string ResolveApplication(string application, string workingDirectory)
    {
        if (Path.IsPathRooted(application))
            return File.Exists(application)
                ? application
                : throw new FileNotFoundException($"The application to start was not found: {application}", application);

        if (application.Contains(Path.DirectorySeparatorChar) || application.Contains(Path.AltDirectorySeparatorChar))
        {
            var relative = Path.GetFullPath(Path.Combine(workingDirectory, application));
            return File.Exists(relative)
                ? relative
                : throw new FileNotFoundException($"The application to start was not found: {relative}", relative);
        }

        var names = Path.HasExtension(application) ? [application] : new[] { application, application + ".exe" };
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // A PATH entry is sometimes quoted, and an unparsable one is not a reason to stop looking.
            var directory = entry.Trim('"');
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { break; }
            }
        }

        throw new FileNotFoundException($"The application to start was not found on PATH: {application}", application);
    }

    /// <summary>
    /// Windows command-line quoting, following the rules <c>CommandLineToArgvW</c> parses by: a backslash is
    /// only special when it precedes a quote, so only those runs get doubled — and a run at the end of a
    /// quoted argument has to be doubled as well. Doubling every backslash, the tempting one-liner, turns
    /// <c>C:\Program Files\App</c> into a path that no longer exists.
    /// </summary>
    private static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        var builder = new StringBuilder(value.Length + 8).Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var backslashes = 0;
            while (i < value.Length && value[i] == '\\') { backslashes++; i++; }
            if (i == value.Length)
            {
                // Trailing backslashes would escape the closing quote.
                builder.Append('\\', backslashes * 2);
                break;
            }
            if (value[i] == '"') builder.Append('\\', backslashes * 2 + 1).Append('"');
            else builder.Append('\\', backslashes).Append(value[i]);
        }
        return builder.Append('"').ToString();
    }

    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpReserved;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDesktop;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr attributes);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode, ref SecurityAttributes attributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
