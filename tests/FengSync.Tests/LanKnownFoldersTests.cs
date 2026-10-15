using FengSync.Lan;
using Microsoft.Win32;
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
            : @"C:\Users\example");

        Assert.Equal(@"D:\Redirected\Downloads", resolved);
    }

    [Fact]
    public void A_blank_shell_answer_falls_back_to_the_profile()
    {
        var resolved = LanKnownFolders.Downloads(id => id == LanKnownFolders.DownloadsFolderId
            ? "   "
            : @"C:\Users\example");

        Assert.Equal(Path.Combine(@"C:\Users\example", "Downloads"), resolved);
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
        // The one case that has to reach the real shell. Measured here: the id that is one character away from
        // FOLDERID_Downloads resolves to C:\Windows and resolves *successfully*, so an assertion about the shape
        // of the answer cannot see the mistake — only a comparison against the machine's own folder table can.
        var resolved = LanKnownFolders.Downloads();

        Assert.False(string.IsNullOrWhiteSpace(resolved));
        Assert.True(Directory.Exists(resolved), $"系统未能解析出下载目录：{resolved}");

        // A received file must never be pointed at a machine folder, whoever the user is and whatever a domain
        // policy has redirected. These four are the answers a wrong id actually produces.
        foreach (var machine in new[]
                 {
                     Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
                     Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.CommonApplicationData,
                 })
        {
            var never = Environment.GetFolderPath(machine).TrimEnd(Path.DirectorySeparatorChar);
            Assert.False(string.Equals(resolved!.TrimEnd(Path.DirectorySeparatorChar), never, StringComparison.OrdinalIgnoreCase),
                $"接收目录落到了系统目录 {never}，说明请求的已知文件夹 id 不对。");
        }
    }

    [Fact]
    public void The_downloads_id_is_the_one_this_machine_calls_Downloads()
    {
        // The shell's own table, not a copy of Microsoft's list: every FolderDescriptions subkey is named by its
        // KNOWNFOLDERID and carries the name Windows gives it, so this is the machine saying which id it calls
        // Downloads — and a transposed character turns red here, on the machine where it does damage. Measured:
        // the id that was in the code for Downloads is this table's Windows folder, which resolves successfully
        // to C:\Windows, and the one for the profile is not in the table at all, so the fallback answered
        // nothing.
        using var table = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FolderDescriptions");
        Assert.NotNull(table);

        string IdFor(string folderName)
        {
            var matches = table!.GetSubKeyNames().Where(name =>
            {
                using var subKey = table.OpenSubKey(name);
                return string.Equals(subKey?.GetValue("Name") as string, folderName, StringComparison.OrdinalIgnoreCase);
            }).ToList();
            return Assert.Single(matches);
        }

        Assert.True(string.Equals(IdFor("Downloads"), LanKnownFolders.DownloadsFolderId, StringComparison.OrdinalIgnoreCase),
            $"这台机器把 Downloads 叫做 {IdFor("Downloads")}，代码请求的却是 {LanKnownFolders.DownloadsFolderId}。");
        Assert.True(string.Equals(IdFor("Profile"), LanKnownFolders.UserProfileFolderId, StringComparison.OrdinalIgnoreCase),
            $"这台机器把用户目录叫做 {IdFor("Profile")}，代码请求的却是 {LanKnownFolders.UserProfileFolderId}。");
    }
}
