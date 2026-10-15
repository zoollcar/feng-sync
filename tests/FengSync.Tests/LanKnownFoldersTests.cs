using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// Covers where an inbound file goes when the user never chose, including the answer on this machine.
/// </summary>
/// <remarks>
/// The chain is what is under test rather than any particular path: a redirected folder, then the profile's own
/// Downloads, then nothing. The last of those is the one that matters most, because a listener that invents a
/// directory would land files somewhere the user is never going to look.
/// </remarks>
public sealed class LanKnownFoldersTests
{
    [Fact]
    public void The_shell_answer_wins_when_there_is_one()
    {
        var resolved = LanKnownFolders.Downloads(id => id == LanKnownFolders.DownloadsFolderId
            ? @"D:\Redirected\Downloads"
            : @"C:\Users\feng");

        Assert.Equal(@"D:\Redirected\Downloads", resolved);
    }

    [Fact]
    public void A_blank_shell_answer_falls_back_to_the_profile()
    {
        var resolved = LanKnownFolders.Downloads(id => id == LanKnownFolders.DownloadsFolderId
            ? "   "
            : @"C:\Users\feng");

        Assert.Equal(Path.Combine(@"C:\Users\feng", "Downloads"), resolved);
    }

    [Fact]
    public void No_answer_at_all_is_no_directory_rather_than_a_guessed_one()
    {
        Assert.Null(LanKnownFolders.Downloads(_ => null));
        Assert.Null(LanKnownFolders.Downloads(_ => ""));
    }

    [Fact]
    public void Surrounding_space_is_not_part_of_a_path_windows_handed_over()
    {
        var resolved = LanKnownFolders.Downloads(id => id == LanKnownFolders.DownloadsFolderId
            ? "  D:\\Downloads  "
            : null);

        Assert.Equal("D:\\Downloads", resolved);
    }

    [Fact]
    public void This_machine_can_name_the_folder_a_sent_file_lands_in()
    {
        // The one case that has to reach the real shell: the two ids above are strings copied from Microsoft's
        // list, and a transposed character would send every file to C:\Users\<name>\Downloads on a machine where
        // the user's Downloads folder is redirected — an answer that looks right and is silently wrong.
        var resolved = LanKnownFolders.Downloads();

        Assert.False(string.IsNullOrWhiteSpace(resolved));
        Assert.True(Directory.Exists(resolved), $"系统未能解析出下载目录：{resolved}");
    }
}
