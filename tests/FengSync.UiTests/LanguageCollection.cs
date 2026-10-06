using FengSync.Localization;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// The interface language is process-wide mutable state, and the rest of this assembly asserts in Chinese
/// because that is the language the resource files' neutral set holds. So the language tests are grouped here
/// and kept off every other collection: one of them switching the language while another test reads a label is
/// a failure that reproduces only under load, which is the worst kind.
/// </summary>
/// <remarks>
/// This fixture pins the starting value rather than restoring it after each test. Restoration is
/// <see cref="LanguageScope"/>'s job, and it runs in a <c>finally</c>, so a test that fails halfway through a
/// switch still puts the language back. The fixture is the belt to that braces: it makes the assumption true
/// at the start of a run even if a future test forgets the scope entirely.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageCollection : ICollectionFixture<LanguageCollection.StartInChinese>
{
    public const string Name = "interface language";

    public sealed class StartInChinese : IDisposable
    {
        public StartInChinese() => AppLanguageContext.Current.Apply(FengSync.Core.Configuration.AppLanguage.Chinese);

        public void Dispose() => AppLanguageContext.Current.Apply(FengSync.Core.Configuration.AppLanguage.Chinese);
    }
}
