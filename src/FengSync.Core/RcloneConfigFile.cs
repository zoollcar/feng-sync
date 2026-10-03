namespace FengSync.Core;

/// <summary>
/// Edits rclone's ini configuration file in place. rclone exposes no rename operation, and re-creating a
/// remote through <c>config/create</c> is not a substitute for one: measured against the bundled v1.75.0,
/// that call runs the backend's configuration state machine, so cloning an OAuth remote walks back into
/// <c>oauth-confirm → oauth-islocal → oauth-authorize</c> and needs a fresh browser authorization instead
/// of copying the stored token.
/// </summary>
/// <remarks>
/// Rewriting the section keeps every value byte-identical — an obscured SFTP password, a multi-line PEM
/// key, an OAuth token — because nothing is parsed or re-encoded. Two measurements make that safe to rely
/// on: a running <c>rclone rcd</c> reports a hand-edited section through <c>config/listremotes</c> and
/// <c>config/get</c> on the very next call, and a later daemon-side <c>config/create</c> merges into the
/// reloaded copy rather than clobbering it. A section header is only recognised at column 0, which is how
/// rclone writes them, so an indented continuation line of a multi-line value can never be mistaken for one.
/// </remarks>
public static class RcloneConfigFile
{
    /// <summary>True when the file text already holds a section with this name.</summary>
    public static bool ContainsSection(string content, string name) => FindSection(Split(content), name) is not null;

    /// <summary>
    /// Renames one section, leaving every other byte of the file alone. <paramref name="valueOverrides"/>
    /// replaces the value of the named keys inside that section — the way an <c>alias</c> that points at a
    /// renamed backing remote is repointed.
    /// </summary>
    public static string RenameSection(
        string content,
        string oldName,
        string newName,
        IReadOnlyDictionary<string, string>? valueOverrides = null)
    {
        var lines = Split(content);
        var section = FindSection(lines, oldName)
            ?? throw new InvalidOperationException($"rclone 配置中没有名为“{oldName}”的端点。");
        var header = "[" + newName + "]" + LineEnding(lines[section.Start]);
        var body = valueOverrides is { Count: > 0 }
            ? RewriteValues(lines, section, valueOverrides)
            : lines[(section.Start + 1)..section.End];

        var result = new List<string>(lines.Length + 1);
        result.AddRange(lines[..section.Start]);
        result.Add(header);
        result.AddRange(body);
        result.AddRange(lines[section.End..]);
        return string.Join('\n', result);
    }

    /// <summary>Drops one section. An unknown name is not an error: the file is the only record, and a
    /// second attempt must not fail on a rename that already succeeded.</summary>
    public static string RemoveSection(string content, string name)
    {
        var lines = Split(content);
        if (FindSection(lines, name) is not { } section) return content;
        var result = new List<string>(lines.Length);
        result.AddRange(lines[..section.Start]);
        result.AddRange(lines[section.End..]);
        return string.Join('\n', result);
    }

    /// <summary>
    /// Splits on <c>\n</c> only, so a <c>\r</c> stays attached to its line and the original CRLF layout —
    /// and whether the file ends with a newline — survives the round trip unchanged.
    /// </summary>
    private static string[] Split(string content) => (content ?? "").Split('\n');

    private static string LineEnding(string line) => line.EndsWith('\r') ? "\r" : "";

    private static (int Start, int End)? FindSection(string[] lines, string name)
    {
        for (var index = 0; index < lines.Length; index++)
        {
            if (!IsHeader(lines[index], out var headerName)) continue;
            if (!headerName.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var end = index + 1;
            while (end < lines.Length && !IsHeader(lines[end], out _)) end++;
            return (index, end);
        }
        return null;
    }

    /// <summary>
    /// A section header starts at column 0 and holds nothing but the bracketed name. Requiring column 0 is
    /// what makes this safe against multi-line values, whose continuation lines rclone indents.
    /// </summary>
    private static bool IsHeader(string line, out string name)
    {
        name = "";
        if (line.Length < 2 || line[0] != '[') return false;
        var close = line.IndexOf(']');
        if (close < 1) return false;
        if (line[(close + 1)..].Trim().Length > 0) return false;
        name = line[1..close].Trim();
        return name.Length > 0;
    }

    private static IEnumerable<string> RewriteValues(
        string[] lines,
        (int Start, int End) section,
        IReadOnlyDictionary<string, string> overrides)
    {
        for (var index = section.Start + 1; index < section.End; index++)
        {
            var line = lines[index];
            // Indented lines belong to the previous value, so they are copied as they are.
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
            {
                yield return line;
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                yield return line;
                continue;
            }

            var key = line[..separator].Trim();
            yield return overrides.TryGetValue(key, out var replacement)
                ? key + " = " + replacement + LineEnding(line)
                : line;
        }
    }
}
