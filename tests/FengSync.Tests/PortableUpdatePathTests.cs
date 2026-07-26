using FengSync.Core.Updates;

namespace FengSync.Tests;

public sealed class PortableUpdatePathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FengSync-installation-tests", Guid.NewGuid().ToString("N"));
    public PortableUpdatePathTests() => Directory.CreateDirectory(_root);
    [Fact]
    public void Installation_safety_rejects_a_nonexistent_or_unrelated_installation()
    {
        Assert.False(InstallationSafety.TryValidate(Path.Combine(Path.GetTempPath(), "missing", "FengSync.exe"), Path.Combine(Path.GetTempPath(), "stage"), out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Installation_safety_accepts_an_isolated_writable_portable_layout()
    {
        var installation = Path.Combine(_root, "portable"); Directory.CreateDirectory(installation);
        var executable = Path.Combine(installation, "FengSync.exe"); File.WriteAllText(executable, "test");
        Assert.True(InstallationSafety.TryValidate(executable, Path.Combine(_root, "task", "staging"), installation, out var actual, out var error), error);
        Assert.Equal(Path.GetFullPath(installation), actual);
    }

    [Fact]
    public void Installation_safety_rejects_mismatched_base_repository_and_staging_overlap()
    {
        var installation = Path.Combine(_root, "portable"); Directory.CreateDirectory(installation);
        var executable = Path.Combine(installation, "FengSync.exe"); File.WriteAllText(executable, "test");
        Assert.False(InstallationSafety.TryValidate(executable, Path.Combine(_root, "task", "staging"), Path.Combine(_root, "other"), out _, out _));
        Assert.False(InstallationSafety.TryValidate(executable, Path.Combine(installation, "staging"), installation, out _, out _));
        File.WriteAllText(Path.Combine(installation, "FengSync.sln"), "repo");
        Assert.False(InstallationSafety.TryValidate(executable, Path.Combine(_root, "task", "staging"), installation, out _, out _));
    }

    [Fact]
    public void Installation_safety_rejects_user_and_drive_root_paths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var homeExe = Path.Combine(home, "FengSync-installation-safety-test.exe");
        // No file is created in the user profile: the identity check must reject first.
        Assert.False(InstallationSafety.TryValidate(homeExe, Path.Combine(_root, "staging"), home, out _, out _));
        var drive = Path.GetPathRoot(_root)!;
        Assert.False(InstallationSafety.TryValidate(Path.Combine(drive, "FengSync.exe"), Path.Combine(_root, "staging"), drive, out _, out _));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
