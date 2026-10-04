using FengSync.Services;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// The rules behind 在网页端查看历史版本. They are pure because the whole feature is pure: rclone exposes no
/// version list, so there is no cloud call to make and nothing to wait for — what is left to get right is
/// which rows are offered the trip, which endpoints have somewhere to send them, and the sentence that has to
/// carry the reason. That sentence is the product here, so it is asserted rather than left to the panel.
/// </summary>
public sealed class CloudVersionHistoryTests
{
    [Fact]
    public void Only_a_file_is_offered_a_history_lookup()
    {
        Assert.True(CloudVersionHistory.Offers(File()));
        // A folder is not an object the cloud keeps versions of, and the same rule keeps 复制分享链接 off it.
        Assert.False(CloudVersionHistory.Offers(Folder()));
    }

    [Theory]
    [InlineData("drive", "Google Drive", "https://drive.google.com/")]
    [InlineData("onedrive", "OneDrive", "https://onedrive.live.com/")]
    [InlineData("dropbox", "Dropbox", "https://www.dropbox.com/home")]
    [InlineData("box", "Box", "https://app.box.com/")]
    public void A_provider_with_exactly_one_console_offers_it(string type, string provider, string url)
    {
        var offer = CloudVersionHistory.For(type, provider);

        Assert.Equal(url, offer.Url);
        Assert.True(offer.CanOpen);
        Assert.Equal($"打开 {provider} 网页端", offer.OpenLabel);
        Assert.Contains(provider, offer.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sftp", "SFTP")]
    [InlineData("webdav", "WebDAV")]
    [InlineData("s3", "S3")]
    public void An_endpoint_with_no_console_offers_no_link_instead_of_a_guess(string type, string provider)
    {
        var offer = CloudVersionHistory.For(type, provider);

        // A self-hosted SFTP server has no console at all, and an S3-compatible endpoint's belongs to whichever
        // service it points at — which this descriptor does not model. No console is a normal answer here, not
        // an error, so the sentence says where to go instead of opening something arbitrary.
        Assert.Null(offer.Url);
        Assert.False(offer.CanOpen);
        Assert.Contains("没有可打开的网页端", offer.Explanation, StringComparison.Ordinal);
        Assert.Contains("控制台", offer.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_explanation_names_rclone_as_the_reason_and_the_switch_it_refuses_to_flip()
    {
        var explanation = CloudVersionHistory.For("drive", "Google Drive").Explanation;

        Assert.Contains("rclone", explanation, StringComparison.Ordinal);
        Assert.Contains("不提供版本列表", explanation, StringComparison.Ordinal);
        // The one lever that does exist rewrites the listing, so the sentence has to say why it is not used —
        // otherwise the next person reads the code, finds the option and turns it on, and the file list starts
        // showing names that are not the objects the user has.
        Assert.Contains("旧版本列进目录", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_status_line_says_which_of_the_two_answers_happened()
    {
        var opened = CloudVersionHistory.StatusFor(CloudVersionHistory.For("drive", "Google Drive"));
        var noConsole = CloudVersionHistory.StatusFor(CloudVersionHistory.For("sftp", "SFTP"));

        Assert.Contains("已在浏览器里打开 Google Drive 网页端", opened, StringComparison.Ordinal);
        Assert.Contains("也没有可打开的网页端", noConsole, StringComparison.Ordinal);
        // Both lines carry the reason: with no console to open, the status bar is the only thing the user sees
        // after the click, so a line that only said "nothing happened" would leave them thinking it was broken.
        Assert.Contains("rclone", opened, StringComparison.Ordinal);
        Assert.Contains("rclone", noConsole, StringComparison.Ordinal);
    }

    [Fact]
    public void An_endpoint_whose_provider_name_is_missing_still_gets_a_sentence()
    {
        // The provider name is only for reading; the console comes from the rclone type, so a row whose metadata
        // could not be read still gets a usable answer rather than a blank one.
        var offer = CloudVersionHistory.For("drive", "");

        Assert.Equal("drive", offer.Provider);
        Assert.Equal("https://drive.google.com/", offer.Url);
    }

    private static CloudFileEntry File() =>
        new("report.txt", "team/report.txt", false, 1024, DateTimeOffset.UnixEpoch);

    private static CloudFileEntry Folder() => new("docs", "team/docs", true, 0, null);
}
