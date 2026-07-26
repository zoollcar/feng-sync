using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FengSync.Updater;

internal static partial class Program
{
    private const int ParameterError = 10, UnsafePath = 11, WaitTimeout = 12, BackupFailure = 13, RollbackOk = 14, RollbackFailure = 15, StartFailure = 16;
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var o = Arguments.Parse(args); if (o is null) return ParameterError;
            if (!Safe(o, out var reason)) { Log(o.TaskDirectory, o.Id, "path-safety-failed", reason); return UnsafePath; }
            try { var p = Process.GetProcessById(o.WaitPid); if (!p.WaitForExit(120_000)) { Log(o.TaskDirectory, o.Id, "wait-timeout"); return WaitTimeout; } } catch (ArgumentException) { }
            var old = File.Exists(o.OldManifest) ? await Manifest.Load(o.OldManifest) : new Manifest("FengSync", o.FromVersion, "win-x64", []);
            if (!old.Valid(o.FromVersion, allowEmpty: true)) { Log(o.TaskDirectory, o.Id, "old-manifest-invalid"); return UnsafePath; }
            var next = await Manifest.Load(o.NewManifest);
            if (!next.Valid(o.ToVersion) || !await next.MatchesFilesAsync(o.Staging)) { Log(o.TaskDirectory, o.Id, "new-manifest-invalid-or-tampered"); return UnsafePath; }
            var faultInjector = FaultInjector.Create(o.Installation);
            var backup = Path.Combine(o.TaskDirectory, "backup"); Directory.CreateDirectory(backup);
            try
            {
                foreach (var file in old.Files.Concat(next.Files).GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(x => x.First())) Backup(o.Installation, backup, file.Path);
                if (File.Exists(Path.Combine(o.Installation, "release-manifest.json"))) File.Copy(Path.Combine(o.Installation, "release-manifest.json"), Path.Combine(backup, "release-manifest.json"), true);
            }
            catch (Exception e) { Log(o.TaskDirectory, o.Id, "backup-failed", e.GetType().Name); return BackupFailure; }
            try
            {
                foreach (var file in next.Files) { faultInjector.BeforeCopy(); Replace(o.Staging, o.Installation, file.Path); }
                var newPaths = next.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var file in old.Files.Where(f => !newPaths.Contains(f.Path))) { var path = Target(o.Installation, file.Path); if (File.Exists(path)) File.Delete(path); }
                ReplaceManifest(o.NewManifest, Path.Combine(o.Installation, "release-manifest.json"));
            }
            catch (Exception e)
            {
                Log(o.TaskDirectory, o.Id, "copy-failed", e.GetType().Name);
                try { Rollback(o, old, backup); Start(o.Executable, o.FromVersion, o.TaskDirectory); Log(o.TaskDirectory, o.Id, "rollback-complete"); return RollbackOk; }
                catch (Exception rollback) { Log(o.TaskDirectory, o.Id, "rollback-failed", rollback.GetType().Name); return RollbackFailure; }
            }
            try { Start(o.Executable, o.FromVersion, o.TaskDirectory); }
            catch (Exception e)
            {
                Log(o.TaskDirectory, o.Id, "new-start-failed", e.GetType().Name);
                try { Rollback(o, old, backup); Start(o.Executable, o.FromVersion, o.TaskDirectory); Log(o.TaskDirectory, o.Id, "rollback-complete-after-start-failure"); }
                catch (Exception rollback) { Log(o.TaskDirectory, o.Id, "rollback-failed-after-start-failure", rollback.GetType().Name); return RollbackFailure; }
                return StartFailure;
            }
            var marker = Path.Combine(o.TaskDirectory, "success"); var until = DateTime.UtcNow.AddSeconds(60); while (DateTime.UtcNow < until && !File.Exists(marker)) await Task.Delay(250);
            if (File.Exists(marker)) { try { Directory.Delete(backup, true); Directory.Delete(o.Staging, true); foreach (var file in new[] { "package.zip", "package.zip.sha256", "FengSync.Updater.exe" }) TryDelete(Path.Combine(o.TaskDirectory, file)); } catch (Exception e) { Log(o.TaskDirectory, o.Id, "cleanup-failed", e.Message); } }
            Log(o.TaskDirectory, o.Id, "completed", File.Exists(marker) ? "confirmed" : "confirmation-timeout"); return 0;
        }
        catch (Exception e) { try { Console.Error.WriteLine(e); } catch { } return ParameterError; }
    }
    private static bool Safe(Arguments o, out string reason)
    {
        reason = ""; try
        {
            if (!Path.IsPathFullyQualified(o.Staging) || !Path.IsPathFullyQualified(o.Installation) || !Path.IsPathFullyQualified(o.Executable) || !Path.IsPathFullyQualified(o.NewManifest) || !Directory.Exists(o.Staging) || !Directory.Exists(o.Installation)) { reason = "paths must be existing absolute paths"; return false; }
            var i = Trail(Path.GetFullPath(o.Installation)); var s = Trail(Path.GetFullPath(o.Staging)); var exe = Path.GetFullPath(o.Executable);
            var newManifest = Path.GetFullPath(o.NewManifest);
            if (Path.GetPathRoot(i) == i || s.StartsWith(i, StringComparison.OrdinalIgnoreCase) || i.StartsWith(s, StringComparison.OrdinalIgnoreCase) || !exe.StartsWith(i, StringComparison.OrdinalIgnoreCase) || !File.Exists(exe) || !string.Equals(newManifest, Path.Combine(s, "release-manifest.json"), StringComparison.OrdinalIgnoreCase) || !File.Exists(newManifest)) { reason = "relationship invalid"; return false; }
            if (!string.IsNullOrEmpty(o.OldManifest) && (!Path.IsPathFullyQualified(o.OldManifest) || !File.Exists(o.OldManifest) || !string.Equals(Path.GetFullPath(o.OldManifest), Path.Combine(i, "release-manifest.json"), StringComparison.OrdinalIgnoreCase))) { reason = "old manifest invalid"; return false; }
            var forbidden = new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
            if (forbidden.Where(x => !string.IsNullOrWhiteSpace(x)).Any(x => string.Equals(Trail(Path.GetFullPath(x)), i, StringComparison.OrdinalIgnoreCase)) || File.Exists(Path.Combine(i, "FengSync.sln"))) { reason = "installation directory forbidden"; return false; }
            return true;
        } catch (Exception e) { reason = e.GetType().Name; return false; }
    }
    private static void Backup(string install, string backup, string relative) { var from = Target(install, relative); if (!File.Exists(from)) return; var to = Target(backup, relative); Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(from, to, true); }
    private static void Replace(string staging, string installation, string relative) { var source = Target(staging, relative); if (!File.Exists(source)) throw new FileNotFoundException("Staged file missing", source); var target = Target(installation, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); var temp = target + ".fengsync-new-" + Guid.NewGuid().ToString("N"); File.Copy(source, temp, true); File.Move(temp, target, true); }
    private static void ReplaceManifest(string source, string target) { var temp = target + ".new"; File.Copy(source, temp, true); File.Move(temp, target, true); }
    private static void Rollback(Arguments o, Manifest old, string backup)
    {
        var oldPaths = old.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase); var next = Manifest.Load(o.NewManifest).GetAwaiter().GetResult();
        foreach (var file in next.Files.Where(f => !oldPaths.Contains(f.Path))) { var source = Target(backup, file.Path); var target = Target(o.Installation, file.Path); if (File.Exists(source)) Replace(backup, o.Installation, file.Path); else if (File.Exists(target)) File.Delete(target); }
        foreach (var file in old.Files) { var source = Target(backup, file.Path); if (File.Exists(source)) Replace(backup, o.Installation, file.Path); }
        var oldManifest = Path.Combine(backup, "release-manifest.json"); if (File.Exists(oldManifest)) ReplaceManifest(oldManifest, Path.Combine(o.Installation, "release-manifest.json"));
    }
    private static void Start(string executable, string updatedFrom, string task) { var process = Process.Start(new ProcessStartInfo(executable, "--updated-from " + updatedFrom + " --update-task \"" + task + "\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! }); if (process is null) throw new InvalidOperationException("Unable to start application"); }
    private static string Target(string root, string relative) { if (!Manifest.Safe(relative)) throw new InvalidDataException("Unsafe manifest path"); var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))); if (!full.StartsWith(Trail(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escape"); return full; }
    private static string Trail(string p) => p.EndsWith(Path.DirectorySeparatorChar) ? p : p + Path.DirectorySeparatorChar;
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void Log(string task, string id, string evt, string? detail = null) { try { string Value(string value) => JsonSerializer.Serialize(value, UpdaterJsonContext.Default.String); File.AppendAllText(Path.Combine(task, "FengSync-update-error.log"), $"{{\"timestamp\":{Value(DateTimeOffset.UtcNow.ToString("O"))},\"taskId\":{Value(id)},\"event\":{Value(evt)},\"detail\":{(detail is null ? "null" : Value(detail))}}}" + Environment.NewLine); } catch { } }
    private sealed class FaultInjector
    {
        private readonly int? _failAfter; private int _copied;
        private FaultInjector(int? failAfter) => _failAfter = failAfter;
        public static FaultInjector Create(string installation)
        {
            var configured = Environment.GetEnvironmentVariable("FENGSYNC_UPDATER_TEST_ROOT");
            var configuredFail = Environment.GetEnvironmentVariable("FENGSYNC_UPDATER_FAIL_AFTER_FILE_COUNT");
            if (string.IsNullOrWhiteSpace(configured) || !int.TryParse(configuredFail, out var failAfter) || failAfter < 1) return new(null);
            try
            {
                var approvedBase = Trail(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FengSync-updater-tests")));
                var root = Trail(Path.GetFullPath(configured)); var target = Trail(Path.GetFullPath(installation));
                // Fault injection is physically constrained to a randomized child below the fixed temp test base.
                return root.StartsWith(approvedBase, StringComparison.OrdinalIgnoreCase) && target.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !string.Equals(root, approvedBase, StringComparison.OrdinalIgnoreCase) ? new(failAfter) : new(null);
            }
            catch { return new(null); }
        }
        public void BeforeCopy() { if (_failAfter is not null && ++_copied >= _failAfter) throw new IOException("Injected updater copy failure."); }
    }
    private sealed record Arguments(int WaitPid, string Staging, string Installation, string Executable, string OldManifest, string NewManifest, string FromVersion, string ToVersion)
    { public string TaskDirectory => Path.GetDirectoryName(Staging)!; public string Id => Path.GetFileName(TaskDirectory); public static Arguments? Parse(string[] a) { var d = new Dictionary<string,string>(StringComparer.Ordinal); for (var i=0;i<a.Length;i+=2) { if (i+1>=a.Length || !a[i].StartsWith("--")) return null; d[a[i]]=a[i+1]; } return int.TryParse(d.GetValueOrDefault("--wait-pid"), out var pid) && d.TryGetValue("--staging",out var s) && d.TryGetValue("--installation",out var ins) && d.TryGetValue("--executable",out var e) && d.TryGetValue("--old-manifest",out var old) && d.TryGetValue("--new-manifest",out var n) && d.TryGetValue("--from-version",out var f) && d.TryGetValue("--to-version",out var t) ? new(pid,s,ins,e,old,n,f,t) : null; } }
    private sealed record Manifest(string Product, string Version, string Runtime, List<Entry> Files)
    {
        public static async Task<Manifest> Load(string path) => JsonSerializer.Deserialize(await File.ReadAllTextAsync(path), UpdaterJsonContext.Default.Manifest) ?? throw new InvalidDataException();
        public bool Valid(string version, bool allowEmpty = false)
        {
            if (Product != "FengSync" || Runtime != "win-x64" || Version != version || (!allowEmpty && Files.Count == 0)) return false;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); string? previous = null;
            foreach (var file in Files)
            {
                if (!Safe(file.Path) || file.Size < 0 || file.Sha256.Length != 64 || !file.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') || !paths.Add(file.Path) || (previous is not null && StringComparer.Ordinal.Compare(previous, file.Path) >= 0)) return false;
                previous = file.Path;
            }
            return true;
        }
        public async Task<bool> MatchesFilesAsync(string root)
        {
            foreach (var entry in Files)
            {
                var path = Target(root, entry.Path); if (!File.Exists(path) || new FileInfo(path).Length != entry.Size) return false;
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(path))).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(entry.Sha256))) return false;
            }
            return true;
        }
        public static bool Safe(string path) => !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && !path.Contains(':') && path.Split('/').All(x=>x.Length>0&&x!="."&&x!=".."&&!x.Contains('\\'));
    }
    private sealed record Entry(string Path, long Size, string Sha256);
    [JsonSerializable(typeof(Manifest))]
    [JsonSerializable(typeof(string))]
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    private partial class UpdaterJsonContext : JsonSerializerContext;
}
