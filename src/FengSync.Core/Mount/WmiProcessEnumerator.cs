using System.Diagnostics;
using System.Runtime.Versioning;

namespace FengSync.Core.Mount;

/// <summary>Real WMI-backed implementation; enumerates rclone.exe processes with their command line.</summary>
[SupportedOSPlatform("windows")]
public sealed class WmiProcessEnumerator : IProcessEnumerator
{
    public IReadOnlyList<RcloneProcessSnapshot> EnumerateRcloneProcesses()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var result = new List<RcloneProcessSnapshot>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT ProcessId, CommandLine, CreationDate FROM Win32_Process WHERE Name='rclone.exe'");
            using var collection = searcher.Get();
            foreach (var item in collection)
            {
                int pid;
                try { pid = Convert.ToInt32(item["ProcessId"]); } catch { continue; }
                var raw = item["CommandLine"] as string;
                var readable = !string.IsNullOrEmpty(raw);
                DateTimeOffset? started = null;
                if (item["CreationDate"] is string created && TryParseWmiDate(created, out var dt)) started = dt;
                result.Add(new RcloneProcessSnapshot(pid, raw, started, readable));
            }
        }
        catch (Exception ex)
        {
            // WMI can fail in restricted environments; surface the error to the caller via a synthetic entry.
            result.Add(new RcloneProcessSnapshot(-1, $"<enumerator error: {ex.Message}>", null, false));
        }
        return result;
    }

    private static bool TryParseWmiDate(string value, out DateTimeOffset result)
    {
        // WMI dates are formatted like 20250701030405.123456+060 — round-trip through DateTime and assume local.
        if (DateTime.TryParseExact(value[..Math.Min(value.Length, 25)], "yyyyMMddHHmmss.ffffffzzz", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var parsed))
        {
            result = new DateTimeOffset(parsed, TimeSpan.Zero);
            return true;
        }
        if (DateTime.TryParse(value, out var fallback)) { result = new DateTimeOffset(fallback, TimeSpan.Zero); return true; }
        result = default;
        return false;
    }
}