namespace FengSync.Core.Updates;

public static class InstallationSafety
{
    public static bool TryValidate(string executable, string staging, out string installation, out string? error)
        => TryValidate(executable, staging, AppContext.BaseDirectory, out installation, out error);

    // The optional base-directory overload keeps the production invariant intact while
    // allowing path validation to be exercised against an isolated portable layout.
    public static bool TryValidate(string executable, string staging, string baseDirectory, out string installation, out string? error)
    {
        installation = ""; error = null;
        try
        {
            var exe = Path.GetFullPath(executable); installation = Path.GetDirectoryName(exe) ?? throw new InvalidDataException(); var baseDir = Path.GetFullPath(baseDirectory);
            if (!string.Equals(ReleaseManifestValidator.EnsureTrailing(installation), ReleaseManifestValidator.EnsureTrailing(baseDir), StringComparison.OrdinalIgnoreCase)) { error = "实际程序目录与运行目录不一致。"; return false; }
            var root = Path.GetPathRoot(installation); var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows); var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!Directory.Exists(installation) || string.Equals(installation, root, StringComparison.OrdinalIgnoreCase) || string.Equals(installation, home, StringComparison.OrdinalIgnoreCase) || string.Equals(installation, windows, StringComparison.OrdinalIgnoreCase) || string.Equals(installation, pf, StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(installation, "FengSync.sln")) || string.Equals(installation, Path.GetDirectoryName(Path.GetFullPath(staging)), StringComparison.OrdinalIgnoreCase) || !File.Exists(exe)) { error = "安装目录不安全。"; return false; }
            var probe = Path.Combine(installation, ".fengsync-write-probe-" + Guid.NewGuid().ToString("N")); File.WriteAllBytes(probe, []); File.Delete(probe); return true;
        }
        catch (Exception ex) { error = "安装目录不可写：" + ex.Message; return false; }
    }
}
