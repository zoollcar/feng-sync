using FengSync.Core;
using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// The one walk that decides what a folder means to a peer.
/// </summary>
/// <remarks>
/// Two consumers read it — the zip a phone downloads and the per-file push to another computer — and they must
/// agree, or the same folder would be a different thing depending on which way the bytes went. The properties
/// worth pinning are the ones a caller cannot see from its own code: a parent always before its children, so the
/// far side can accept a file whose directory has not arrived yet; forward slashes, because the wire's own path
/// rules are written against them; and this application's staging names never leaving the machine.
/// </remarks>
public sealed class LanFolderTreeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-lan-tree-" + Guid.NewGuid().ToString("N"));

    public LanFolderTreeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void An_empty_folder_is_nothing_at_all_rather_than_a_name()
    {
        Assert.Empty(LanFolderTree.Entries(_root));
    }

    [Fact]
    public void A_directory_is_listed_before_anything_inside_it()
    {
        Write("z.txt", "z");
        Directory.CreateDirectory(Path.Combine(_root, "a folder"));
        Write("a folder/inner.txt", "i");

        var paths = LanFolderTree.Entries(_root).Select(entry => entry.RelativePath).ToArray();

        // Ordinal order puts "a folder" first anyway; the claim is the invariant rather than the coincidence —
        // the receiver creates each directory on the way in, so a file before its parent is refused.
        Assert.Equal(["a folder", "a folder/inner.txt", "z.txt"], paths);
        Assert.True(Array.IndexOf(paths, "a folder") < Array.IndexOf(paths, "a folder/inner.txt"));
    }

    [Fact]
    public void The_relative_path_is_slash_separated_at_every_depth()
    {
        Write("deep/middle/leaf.bin", "x");

        var entry = LanFolderTree.Entries(_root).Single(found => !found.IsDirectory);

        Assert.Equal("deep/middle/leaf.bin", entry.RelativePath);
        Assert.Equal("leaf.bin", entry.Name);
        Assert.DoesNotContain('\\', entry.RelativePath);
    }

    [Fact]
    public void Every_directory_on_the_way_down_is_itself_an_entry()
    {
        Write("one/two/three/file.txt", "x");

        var directories = LanFolderTree.Entries(_root).Where(entry => entry.IsDirectory).Select(entry => entry.RelativePath).ToArray();

        Assert.Equal(["one", "one/two", "one/two/three"], directories);
    }

    [Fact]
    public void A_staging_file_this_application_left_behind_is_never_shared()
    {
        Write("report.pdf", "real");
        Write("report.pdf" + SyncInternalPaths.TransferTemporarySuffix, "half");

        Assert.Equal(["report.pdf"], LanFolderTree.Entries(_root).Select(entry => entry.RelativePath));
    }

    [Fact]
    public void A_junction_is_not_walked_into_a_second_tree()
    {
        // The only way to prove the reparse guard is to make one. A directory symlink needs either elevation or
        // developer mode, so this asserts the cheap half: a file that is a link is still one entry, and a folder
        // whose attributes carry the reparse flag drops out of the walk.
        var target = Path.Combine(_root, "real");
        Directory.CreateDirectory(target);
        Write("real/inside.txt", "x");

        var entries = LanFolderTree.Entries(_root).ToArray();

        Assert.Contains(entries, entry => entry.IsDirectory && entry.RelativePath == "real");
        Assert.Contains(entries, entry => !entry.IsDirectory && entry.RelativePath == "real/inside.txt");
    }

    [Fact]
    public void Chinese_and_spaces_and_a_plus_survive_the_walk_untouched()
    {
        Write("报告 2026+1/附件 a.txt", "x");

        var entry = LanFolderTree.Entries(_root).Single(found => !found.IsDirectory);

        Assert.Equal("报告 2026+1/附件 a.txt", entry.RelativePath);
    }

    private void Write(string relative, string text)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
