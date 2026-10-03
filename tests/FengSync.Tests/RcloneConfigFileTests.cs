using FengSync.Core;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// Renaming an endpoint rewrites its section in rclone.conf, so the edit has to be exact: a single mangled
/// byte in an obscured password or an OAuth token silently breaks the endpoint, and the file is also the
/// only record of every other endpoint. These tests pin the two properties that make the rewrite safe —
/// nothing but the intended bytes changes, and a section header is only recognised where rclone writes it.
/// </summary>
public sealed class RcloneConfigFileTests
{
    private const string Sample = "[docs]\ntype = sftp\nhost = nas.local\npass = 2Wq9Q3mZ8tLpQ0vR\n\n[other]\ntype = s3\nprovider = AWS\n";

    [Fact]
    public void Renaming_a_section_leaves_every_other_byte_alone()
    {
        var renamed = RcloneConfigFile.RenameSection(Sample, "docs", "archive");

        Assert.Equal("[archive]\ntype = sftp\nhost = nas.local\npass = 2Wq9Q3mZ8tLpQ0vR\n\n[other]\ntype = s3\nprovider = AWS\n", renamed);
    }

    [Fact]
    public void Renaming_keeps_the_line_endings_and_the_missing_final_newline()
    {
        // rclone writes CRLF on Windows. Rebuilding the file with LF would rewrite every endpoint's section
        // on the next daemon-side save, which is exactly the kind of churn a rename must not cause.
        var crlf = "[docs]\r\ntype = sftp\r\n\r\n[other]\r\ntype = s3";
        var renamed = RcloneConfigFile.RenameSection(crlf, "docs", "archive");

        Assert.Equal("[archive]\r\ntype = sftp\r\n\r\n[other]\r\ntype = s3", renamed);
        // Stripping every CRLF must leave no bare LF behind: one would mean a line ending was rewritten.
        Assert.DoesNotContain("\n", renamed.Replace("\r\n", ""));
    }

    [Fact]
    public void A_multi_line_value_survives_and_its_indented_lines_are_not_mistaken_for_a_section()
    {
        // rclone indents continuation lines of a multi-line value (a PEM key is the usual case). One of them
        // can legitimately look like a section header, and treating it as one would truncate the value.
        var content = "[docs]\ntype = sftp\nkey_pem = -----BEGIN KEY-----\n\t[bracketed-looking-data]\n\t-----END KEY-----\n\n[other]\ntype = s3\n";
        var renamed = RcloneConfigFile.RenameSection(content, "docs", "archive");

        Assert.Contains("\t[bracketed-looking-data]\n", renamed);
        Assert.Contains("key_pem = -----BEGIN KEY-----\n\t[bracketed-looking-data]\n\t-----END KEY-----\n", renamed);
        Assert.Contains("\n[other]\ntype = s3\n", renamed);
        Assert.DoesNotContain("[bracketed-looking-data]\ntype", renamed);
    }

    [Fact]
    public void A_similar_name_is_not_the_same_section()
    {
        // The lookup is by whole section name, so a remote whose name starts with another's must not match.
        var content = "[docs2]\ntype = sftp\n\n[docs]\ntype = s3\n";
        var renamed = RcloneConfigFile.RenameSection(content, "docs", "archive");

        Assert.Contains("[docs2]\ntype = sftp", renamed);
        Assert.Contains("[archive]\ntype = s3", renamed);
        Assert.DoesNotContain("[docs]\n", renamed);
    }

    [Fact]
    public void Value_overrides_rewrite_only_the_named_keys()
    {
        // An enforced-prefix S3 endpoint is an alias whose target embeds the backing remote's name. Renaming
        // the backing without repointing the alias would leave the endpoint reading the store it left behind.
        var content = "[docs]\ntype = alias\nremote = docs_s3:bucket/team\n\n[other]\ntype = alias\nremote = other_s3:bucket\n";
        var renamed = RcloneConfigFile.RenameSection(content, "docs", "archive",
            new Dictionary<string, string> { ["remote"] = "archive_s3:bucket/team" });

        Assert.Contains("[archive]\ntype = alias\nremote = archive_s3:bucket/team\n", renamed);
        // The other alias keeps its own target: the override applies to one section only.
        Assert.Contains("[other]\ntype = alias\nremote = other_s3:bucket\n", renamed);
    }

    [Fact]
    public void Removing_a_section_drops_only_that_section()
    {
        var trimmed = RcloneConfigFile.RemoveSection(Sample, "docs");

        // The blank line that separated the two sections belongs to the removed one, so nothing is left behind.
        Assert.Equal("[other]\ntype = s3\nprovider = AWS\n", trimmed);
        Assert.False(RcloneConfigFile.ContainsSection(trimmed, "docs"));
        Assert.True(RcloneConfigFile.ContainsSection(trimmed, "other"));
    }

    [Fact]
    public void Removing_a_section_that_is_not_there_changes_nothing()
    {
        // A rename that already succeeded must not fail on a retry of its cleanup step.
        Assert.Equal(Sample, RcloneConfigFile.RemoveSection(Sample, "missing"));
    }

    [Fact]
    public void Renaming_a_section_that_is_not_there_reports_it()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RcloneConfigFile.RenameSection(Sample, "missing", "archive"));
        Assert.Contains("missing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_file_reports_no_sections()
    {
        Assert.False(RcloneConfigFile.ContainsSection("", "docs"));
        Assert.Equal("", RcloneConfigFile.RemoveSection("", "docs"));
    }

    [Fact]
    public void A_comment_line_after_the_header_does_not_stop_the_section_lookup()
    {
        // rclone tolerates comments and blank lines between sections; neither may end the search.
        var content = "# generated\n\n[docs]\ntype = sftp\n# note\nhost = h\n\n[other]\ntype = s3\n";
        var renamed = RcloneConfigFile.RenameSection(content, "docs", "archive");

        Assert.Contains("[archive]\ntype = sftp\n# note\nhost = h\n", renamed);
        Assert.StartsWith("# generated\n\n[archive]", renamed, StringComparison.Ordinal);
    }
}
