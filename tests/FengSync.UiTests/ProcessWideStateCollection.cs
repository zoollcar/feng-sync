using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// Tests that install process-wide state — the <see cref="FengSync.Services.AppServices"/> host,
/// FENGSYNC_DATA_DIR, or the single WPF <c>Application</c> an AppDomain allows — must not overlap, or one
/// would observe another's services and produce a failure that only reproduces under load. Marking the
/// collection non-parallel keeps them mutually exclusive and exclusive of every other collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWideStateCollection
{
    public const string Name = "process-wide state";
}
