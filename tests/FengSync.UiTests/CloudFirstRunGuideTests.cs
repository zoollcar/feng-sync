using FengSync.Core.Configuration;
using FengSync.Localization;
using FengSync.Services;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// E5 首启引导的规则：该不该出现、几步、用户答完之后记住什么。
/// </summary>
/// <remarks>
/// 这一组同时也是引导这一小块界面的语言测试：十句文案每句都要在两种语言里存在，英文那栏不能还是中文。
/// 放进 <see cref="LanguageCollection"/> 是因为它会切语言，而语言是进程级的一份状态。
/// </remarks>
[Collection(LanguageCollection.Name)]
public sealed class CloudFirstRunGuideTests
{
    [Fact]
    public void It_appears_only_the_first_time_and_only_while_there_is_nothing_to_manage()
    {
        // 引导说的是「先连一个 Google Drive」。一个已经建了三个端点的人看到这句话只会觉得吵 ——
        // 而这份安静是靠端点**数量**换来的，不是靠一个「他大概不需要」的猜测。
        Assert.True(CloudFirstRunGuide.ShouldShow(guideSeen: false, endpointCount: 0));
        Assert.False(CloudFirstRunGuide.ShouldShow(guideSeen: true, endpointCount: 0));
        Assert.False(CloudFirstRunGuide.ShouldShow(guideSeen: false, endpointCount: 1));
        Assert.False(CloudFirstRunGuide.ShouldShow(guideSeen: false, endpointCount: 3));
        Assert.False(CloudFirstRunGuide.ShouldShow(guideSeen: true, endpointCount: 3));
    }

    [Fact]
    public void Only_never_again_is_remembered()
    {
        // 把「不再提示」和「稍后再说」合成一个「我知道了」是这一项最容易犯的错：用户点它的时候想的是
        // 「这次别烦我」，而程序替他决定成了「永远别烦我」。
        Assert.True(CloudFirstRunGuide.Remembers(CloudFirstRunGuide.Choice.Never));
        Assert.False(CloudFirstRunGuide.Remembers(CloudFirstRunGuide.Choice.Later));
        Assert.False(CloudFirstRunGuide.Remembers(CloudFirstRunGuide.Choice.Start));
    }

    [Fact]
    public void Each_answer_says_something_different_after_it_closes()
    {
        var answers = Enum.GetValues<CloudFirstRunGuide.Choice>()
            .Select(CloudFirstRunGuide.StatusFor)
            .ToArray();

        Assert.Equal(3, answers.Length);
        Assert.Equal(3, answers.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_guide_has_four_steps_and_no_step_is_blank()
    {
        using var scope = new LanguageScope();
        foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
        {
            scope.To(language);
            var steps = CloudFirstRunGuide.Steps();
            Assert.Equal(4, steps.Count);
            Assert.All(steps, step =>
            {
                Assert.False(string.IsNullOrWhiteSpace(step.Title));
                Assert.False(string.IsNullOrWhiteSpace(step.Detail));
            });
            // 四步四句话，不能有两步说同一件事。
            Assert.Equal(4, steps.Select(step => step.Title).Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void Every_word_of_the_guide_exists_in_both_languages()
    {
        using var scope = new LanguageScope();
        var chinese = Words(AppLanguage.Chinese);
        var english = Words(AppLanguage.English);

        Assert.NotEmpty(chinese);
        Assert.Equal(chinese.Count, english.Count);
        // 两种语言里不同的句子数必须一样多：把两句不同的中文译成同一句英文，才会让这个数掉下来。
        // 反过来「新建端点」在中文里本来就是按钮与第一步的名字，两栏都重复才是对的。
        Assert.Equal(
            chinese.Distinct(StringComparer.Ordinal).Count(),
            english.Distinct(StringComparer.Ordinal).Count());
        // 英文那栏不能还是中文 —— 那正是从错误的一侧复制过来的样子。
        Assert.DoesNotContain(english, word => word.Any(character => character >= '一'));
    }

    [Fact]
    public void Switching_the_language_rewrites_the_guide_in_place()
    {
        using var scope = new LanguageScope();
        scope.To(AppLanguage.Chinese);
        var chinese = Words(AppLanguage.Chinese);

        scope.To(AppLanguage.English);
        var english = Words(AppLanguage.English);

        // 十句里至少有一句必须真的变了：标题、四个步骤名、四段正文和三个回答之后的提示。
        Assert.NotEqual(chinese, english);
    }

    /// <summary>Every piece of text the guide can show, in one language.</summary>
    private static IReadOnlyList<string> Words(AppLanguage language)
    {
        var previous = AppLanguageContext.Current.Language;
        AppLanguageContext.Current.Apply(language);
        try
        {
            var words = new List<string>
            {
                CloudFirstRunGuide.Title, CloudFirstRunGuide.Intro, CloudFirstRunGuide.NeverAgainLabel,
                CloudFirstRunGuide.LaterLabel, CloudFirstRunGuide.StartLabel,
            };
            foreach (var step in CloudFirstRunGuide.Steps()) words.AddRange([step.Title, step.Detail]);
            foreach (var choice in Enum.GetValues<CloudFirstRunGuide.Choice>()) words.Add(CloudFirstRunGuide.StatusFor(choice));
            return words;
        }
        finally { AppLanguageContext.Current.Apply(previous); }
    }
}
