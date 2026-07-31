using System.Text;

namespace FengSync.Core.Mount;

/// <summary>Result of identifying an <c>rclone mount</c> / <c>rclone cmount</c> command line.</summary>
public sealed record ParsedMountCommand(string RemoteSpec, string MountPoint, string CommandLine);

/// <summary>
/// Pure, allocation-light parser for the subset of the rclone CLI we care about. Supports Windows-style
/// quoting and the long-flag forms that rclone itself produces. Designed to be deterministic enough to
/// test without spawning any real processes.
/// </summary>
public static class RcloneCommandLineParser
{
    /// <summary>
    /// True when the command line is an rclone <c>mount</c> or <c>cmount</c> invocation. We don't try to
    /// fully validate the executable path — the caller already filtered on the process name.
    /// </summary>
    public static bool TryParse(string? commandLine, out ParsedMountCommand parsed)
    {
        parsed = null!;
        if (string.IsNullOrWhiteSpace(commandLine)) return false;
        var tokens = Tokenize(commandLine);
        if (tokens.Count < 4) return false;
        // tokens[0] is the executable ("rclone.exe" or absolute path). Find the subcommand.
        var subIndex = -1;
        for (var i = 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.StartsWith("--", StringComparison.Ordinal)) continue;
            subIndex = i; break;
        }
        if (subIndex < 0) return false;
        var sub = tokens[subIndex];
        if (!sub.Equals("mount", StringComparison.OrdinalIgnoreCase) && !sub.Equals("cmount", StringComparison.OrdinalIgnoreCase)) return false;
        // The remote spec is the next token: "remoteName:" or "remoteName:/path".
        if (subIndex + 1 >= tokens.Count) return false;
        var remote = tokens[subIndex + 1];
        if (!IsRemoteSpec(remote)) return false;
        // The mount point is the LAST positional argument after all `--flag [value]` pairs. Options may
        // accept their own argument (e.g. `--cache-dir X`), so we track which flags take a value.
        var mountPoint = ExtractMountPoint(tokens, subIndex + 2);
        if (string.IsNullOrEmpty(mountPoint)) return false;
        parsed = new ParsedMountCommand(remote, mountPoint, commandLine);
        return true;
    }

    private static bool IsRemoteSpec(string value) =>
        value.Contains(':') && !value.StartsWith(":", StringComparison.Ordinal) && value.IndexOf(':') <= 64 && !Path.IsPathRooted(value);

    /// <summary>
    /// Locate the final positional token after every `--flag` and `--flag value` pair. rclone permits
    /// some long flags (e.g. `--log-level DEBUG`) to consume the next argument.
    /// </summary>
    private static string ExtractMountPoint(IReadOnlyList<string> tokens, int start)
    {
        string? positional = null;
        for (var i = start; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.StartsWith("--", StringComparison.Ordinal))
            {
                if (TakesValue(t) && i + 1 < tokens.Count) i++;
                continue;
            }
            positional = t;
        }
        return positional ?? "";
    }

    /// <summary>
    /// Flags whose argument is the next token. Keep in sync with what rclone mount actually consumes.
    /// Unknown flags are assumed to be boolean (most are), which means the trailing positional becomes
    /// the mount point — the desired behavior.
    /// </summary>
    private static bool TakesValue(string flag)
    {
        switch (flag.ToLowerInvariant())
        {
            case "--config":
            case "--cache-dir":
            case "--vfs-cache-mode":
            case "--vfs-cache-max-age":
            case "--vfs-cache-max-size":
            case "--vfs-write-back":
            case "--vfs-read-ahead":
            case "--log-level":
            case "--log-file":
            case "--log-format":
            case "--user-agent":
            case "--rc":
            case "--rc-addr":
            case "--rc-user":
            case "--rc-pass":
            case "--bind-address":
            case "--poll-interval":
            case "--umask":
            case "--dir-cache-time":
            case "--attr-timeout":
            case "--volname":
            case "--network-mode":
                return true;
            default:
                return false;
        }
    }

    /// <summary>Windows-style command-line tokenizer with quoted segments.</summary>
    public static IReadOnlyList<string> Tokenize(string commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(commandLine)) return result;
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '"')
            {
                // rclone preserves doubled quotes inside a quoted segment.
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"') { sb.Append('"'); i++; }
                else inQuotes = !inQuotes;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }
}