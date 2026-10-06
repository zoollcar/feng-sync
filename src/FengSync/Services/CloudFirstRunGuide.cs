using FengSync.Localization;

namespace FengSync.Services;

/// <summary>
/// E5 首启引导的**全部规则**：该不该出现、几步、每句怎么说、用户答完之后记住什么。纯函数，
/// 所以「第二次打开还会不会再弹」与「已有端点的用户会不会看到它」都不需要开窗就能断言。
/// </summary>
/// <remarks>
/// <para>两条决定值得写下来，因为它们都不是「显然」：</para>
/// <para>1. <b>已经有端点的人看不到它</b>。引导说的是「先连一个 Google Drive」，而一个已经建了三个端点的人
/// 看到这句话只会觉得吵 —— 而这份安静是靠 <see cref="ShouldShow"/> 读**端点数**换来的，不是靠一个
/// 「他大概不需要」的猜测。</para>
/// <para>2. <b>「不再提示」与「稍后再说」是两回事</b>。前者写进设置，后者什么都不写：下次打开还会再问一次。
/// 把两者合成一个「我知道了」是这一项最容易犯的错 —— 用户点它的时候想的是「这次别烦我」，
/// 而程序替他决定成了「永远别烦我」。</para>
/// </remarks>
public static class CloudFirstRunGuide
{
    /// <summary>What the user answered, and whether they asked never to be asked again.</summary>
    public enum Choice
    {
        /// <summary>Close it. Nothing is remembered, so it comes back next time.</summary>
        Later,
        /// <summary>Open the new-endpoint editor. Nothing is remembered either.</summary>
        Start,
        /// <summary>Close it and never show it again.</summary>
        Never
    }

    /// <summary>One numbered step, as a title and the sentence under it.</summary>
    public sealed record Step(string Title, string Detail);

    /// <summary>Title of the guide window.</summary>
    public static string Title => Tr.F("Guide.Title");

    /// <summary>The one sentence above the steps: what this window is for, not what the button is called.</summary>
    public static string Intro => Tr.F("Guide.Intro");

    /// <summary>Checkbox text. Chosen so the sentence says what it does, not what it avoids.</summary>
    public static string NeverAgainLabel => Tr.F("Guide.NeverAgain");

    /// <summary>The button that closes without doing anything, and without remembering anything.</summary>
    public static string LaterLabel => Tr.F("Guide.Later");

    /// <summary>The button that opens the new-endpoint editor.</summary>
    public static string StartLabel => Tr.F("Guide.Start");

    /// <summary>True only the first time, and only while there is nothing to manage yet.</summary>
    public static bool ShouldShow(bool guideSeen, int endpointCount) => !guideSeen && endpointCount == 0;

    /// <summary>
    /// The steps. Google Drive is the one named because it is the backend that needs an interactive browser
    /// authorization — the other six (OneDrive, Dropbox, Box, WebDAV, S3, SFTP) either authenticate with a
    /// token the user pastes or a key pair, so a guide written around the OAuth dance would be the wrong
    /// guide for five of the seven.
    /// </summary>
    public static IReadOnlyList<Step> Steps() =>
    [
        new(Tr.F("Guide.Step1Title"), Tr.F("Guide.Step1Detail")),
        new(Tr.F("Guide.Step2Title"), Tr.F("Guide.Step2Detail")),
        new(Tr.F("Guide.Step3Title"), Tr.F("Guide.Step3Detail")),
        new(Tr.F("Guide.Step4Title"), Tr.F("Guide.Step4Detail"))
    ];

    /// <summary>What the status line says after the guide closes, per answer.</summary>
    public static string StatusFor(Choice choice) => choice switch
    {
        Choice.Start => Tr.F("Guide.StatusStart"),
        Choice.Never => Tr.F("Guide.StatusNever"),
        _ => Tr.F("Guide.StatusLater")
    };

    /// <summary>Whether an answer is one to remember. 稍后再说 deliberately is not.</summary>
    public static bool Remembers(Choice choice) => choice == Choice.Never;
}
