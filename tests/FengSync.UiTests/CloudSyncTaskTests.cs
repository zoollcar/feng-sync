using FengSync.Core;
using FengSync.Services;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// E1（从一个端点建同步任务）和 E2（一键双向同步向导）的规则。<see cref="CloudSyncTask"/> 是纯函数，
/// 所以「云端那侧到底填了什么」「默认叫什么名字」「什么时候该拒绝」都不需要开窗就能断言。
/// </summary>
/// <remarks>
/// 断言全部落在不随界面语言变化的事实上：URI、路径、名字、能不能继续。拒绝理由的具体措辞不在这里断言
/// ——那是文案，由 <c>LanguageCoverageTests</c> 和资源表管；这里断言的是**有拒绝**、**四条拒绝彼此不同**，
/// 以及**拒绝发生在建之前**。
/// </remarks>
public sealed class CloudSyncTaskTests
{
    private static CloudEndpointAccount Account(string name = "work", string type = "drive", string? root = null) =>
        new(new RcloneAccount(name, type),
            new CloudEndpointMetadata(name, type, "Google Drive", Bucket: "", Subdirectory: root ?? ""));

    [Fact]
    public void The_cloud_side_is_the_directory_being_browsed_not_the_endpoint_root()
    {
        // 用户在 work/team/docs 里点「创建同步任务…」，要的是这个目录。把端点根填进去等于替他同步了整棵树。
        var offer = CloudSyncTask.OfferFor(Account(), browsedEndpoint: "work", currentPath: "team/docs", []);

        Assert.True(offer.CanProceed);
        Assert.Equal("gdrive://work/team/docs", offer.Plan!.RightPath);
        // 本地那格是空的：替用户猜一个本地路径比让他自己点一次「浏览…」更糟。
        Assert.Equal("", offer.Plan.LeftPath);
    }

    [Fact]
    public void Browsing_another_endpoint_falls_back_to_the_endpoint_root()
    {
        // 目录 team/docs 属于 work，不属于 personal。把它挂到 personal 上是错的，所以这时只给根。
        var offer = CloudSyncTask.OfferFor(Account(), browsedEndpoint: "personal", currentPath: "team/docs", []);

        Assert.True(offer.CanProceed);
        Assert.Equal("gdrive://work", offer.Plan!.RightPath);
    }

    [Fact]
    public void An_endpoint_with_no_root_addresses_nothing_twice()
    {
        // alias 端点的 RootPath 本来就是空的（alias 根**就是**前缀）。这里不做特判，因为一旦有了特判，
        // 就会有人以为 alias 端点需要把 bucket/prefix 再拼一次。
        var offer = CloudSyncTask.OfferFor(Account(name: "photos", type: "s3", root: ""),
            browsedEndpoint: "photos", currentPath: "2026", []);

        Assert.Equal("s3://photos/2026", offer.Plan!.RightPath);
        Assert.DoesNotContain("//", offer.Plan.RightPath.Replace("s3://", "", StringComparison.Ordinal));
    }

    [Fact]
    public void The_endpoint_root_and_the_browsed_path_are_joined_without_a_repeated_separator()
    {
        var account = Account(root: "/inbox/");

        Assert.Equal("inbox/team/docs", CloudSyncTask.BrowsedPathOf(account, "work", "/team/docs/"));
        Assert.Equal("inbox", CloudSyncTask.BrowsedPathOf(account, "work", ""));
        Assert.Equal("inbox", CloudSyncTask.BrowsedPathOf(account, "work", null));
    }

    [Fact]
    public void The_default_name_follows_the_directory_and_never_collides_with_an_existing_one()
    {
        // 重名的唯一处理方式是加后缀而不是拒绝：ProfileStore.SaveAsync 是整份覆盖，名字撞了会静默覆盖
        // 别人那份，撞了才报错则是在用户点完保存之后才说。
        var first = CloudSyncTask.SuggestName(Account(), "team/docs", []);
        var second = CloudSyncTask.SuggestName(Account(), "team/docs", [first]);
        var third = CloudSyncTask.SuggestName(Account(), "team/docs", [first, second]);

        Assert.NotEqual(first, second);
        Assert.NotEqual(second, third);
        Assert.Contains("team/docs", first, StringComparison.Ordinal);
        Assert.Contains("work", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Unique_name_also_separates_names_that_differ_only_in_case()
    {
        // Profile 的名字在磁盘上按不区分大小写比较，所以 Documents 和 documents 是同一个名字。
        var wanted = CloudSyncTask.UniqueName("docs", ["DOCS"]);

        Assert.NotEqual("docs", wanted);
        Assert.Equal("docs (2)", wanted);
    }

    [Fact]
    public void Unique_name_keeps_counting_rather_than_giving_up()
    {
        // The desired name plus the first 39 suffixed versions are taken, so the next free is (41) — not a
        // GUID escape hatch, which would mean the loop gave up.
        var taken = new[] { "docs" }.Concat(Enumerable.Range(2, 39).Select(n => $"docs ({n})")).ToArray();

        Assert.Equal("docs (41)", CloudSyncTask.UniqueName("docs", taken));
    }

    [Fact]
    public void No_endpoint_selected_is_a_refusal_rather_than_an_empty_task()
    {
        var offer = CloudSyncTask.OfferFor(null, browsedEndpoint: "work", currentPath: "team", []);

        Assert.False(offer.CanProceed);
        Assert.Null(offer.Plan);
        Assert.False(string.IsNullOrWhiteSpace(offer.Refusal));
    }

    [Fact]
    public void Every_way_of_filling_the_wizard_in_wrongly_is_refused_before_anything_is_built()
    {
        // 四条规则，少一条都只会让用户在第一次运行时才发现。
        var refusals = new[]
        {
            CloudSyncTask.WizardOfferFor("", "gdrive://work", []),
            CloudSyncTask.WizardOfferFor(@"C:\docs", "", []),
            CloudSyncTask.WizardOfferFor(@"C:\docs", @"C:\other", []),              // 云端那侧不是端点
            CloudSyncTask.WizardOfferFor("gdrive://work", "gdrive://personal", [])   // 两端都是云端
        };

        Assert.All(refusals, offer => Assert.False(offer.CanProceed));
        Assert.All(refusals, offer => Assert.Null(offer.Plan));
        Assert.All(refusals, offer => Assert.False(string.IsNullOrWhiteSpace(offer.Refusal)));
        // 四条理由必须是四句不同的话，否则其中一条被改坏时用户看到的是别人的理由，而测试仍然通过。
        Assert.Equal(4, refusals.Select(offer => offer.Refusal).Distinct().Count());
    }

    [Fact]
    public void Two_remote_sides_are_refused_because_the_engine_has_no_third_mode()
    {
        // 同步引擎要么把一端当本地目录读写，要么把两端当远端做 operations/copyfile。让用户建出一个
        // 两端都是 URI 的「双向同步」任务，只会在第一次运行时才告诉他这件事。
        var offer = CloudSyncTask.WizardOfferFor("gdrive://work", "s3://photos", []);

        Assert.False(offer.CanProceed);
    }

    [Fact]
    public void The_two_sides_holding_the_same_place_is_refused()
    {
        // The same string in both boxes — the wizard's job is two distinct endpoints, not one in two boxes.
        var offer = CloudSyncTask.WizardOfferFor(@"C:\docs", @"C:\docs", []);

        Assert.False(offer.CanProceed);
    }

    [Fact]
    public void A_complete_wizard_answer_builds_a_bidirectional_task()
    {
        var offer = CloudSyncTask.WizardOfferFor(@"C:\Users\me\Docs\reports", "gdrive://work/reports", []);

        Assert.True(offer.CanProceed);
        Assert.Equal(@"C:\Users\me\Docs\reports", offer.Plan!.LeftPath);
        Assert.Equal("gdrive://work/reports", offer.Plan.RightPath);
        // 默认名取本地目录的末级名与端点名，两边都认得出来。
        Assert.Contains("reports", offer.Plan.Name, StringComparison.Ordinal);
        Assert.Contains("work", offer.Plan.Name, StringComparison.Ordinal);
        // 描述里那个双向箭头是任务性质的说明：模式默认 TwoWay。
        Assert.Contains("↔", offer.Plan.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wizard_task_whose_name_is_taken_gets_a_suffix_too()
    {
        var first = CloudSyncTask.SuggestWizardName(@"C:\docs", "gdrive://work", []);
        var second = CloudSyncTask.SuggestWizardName(@"C:\docs", "gdrive://work", [first]);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void The_source_description_says_where_it_points_in_words_not_uris()
    {
        // 状态行上那句是给用户看的，URI 那一格已经在那儿了。
        var account = Account();

        Assert.Contains("team/docs", CloudSyncTask.DescribeSource(account, "team/docs"), StringComparison.Ordinal);
        Assert.DoesNotContain("://", CloudSyncTask.DescribeSource(account, "team/docs"), StringComparison.Ordinal);
        Assert.Contains("work", CloudSyncTask.DescribeSource(account, ""), StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_is_recovered_from_a_uri_by_removing_only_the_endpoints_own_root()
    {
        var account = Account(root: "inbox");

        Assert.Equal("team/docs", CloudSyncTask.PathOf("gdrive://work/inbox/team/docs", account));
        // 端点根不是前缀时不能剥：inbox-old 不是 inbox。
        Assert.Equal("inbox-old/docs", CloudSyncTask.PathOf("gdrive://work/inbox-old/docs", account));
        Assert.Equal("", CloudSyncTask.PathOf("gdrive://work", account));
        Assert.Equal("", CloudSyncTask.PathOf("s3://somewhere-else/docs", account));
    }
}
