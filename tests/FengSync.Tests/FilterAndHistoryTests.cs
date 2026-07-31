using FengSync.Core;

namespace FengSync.Tests;

public sealed class FilterAndHistoryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-filter-history-" + Guid.NewGuid().ToString("N"));
    public Task InitializeAsync() { Directory.CreateDirectory(_root); return Task.CompletedTask; }
    public Task DisposeAsync() { if (Directory.Exists(_root)) Directory.Delete(_root, true); return Task.CompletedTask; }

    [Fact]
    public void Filter_engine_explains_last_matching_rule_and_normalizes_windows_paths()
    {
        var engine = new FilterEngine([
            new FilterRule(FilterRuleKind.Exclude, "**/*.tmp", "temporary"),
            new FilterRule(FilterRuleKind.Include, "keep\\**")
        ]);
        var excluded = engine.Evaluate("cache\\draft.tmp");
        var included = engine.Evaluate("keep\\draft.tmp");
        Assert.False(excluded.Included); Assert.Equal("temporary", excluded.Reason);
        Assert.True(included.Included);
    }

    [Fact]
    public void Attribute_filter_applies_size_and_hidden_conditions()
    {
        var engine = new FilterEngine([new(FilterRuleKind.Exclude, "*", MaximumSizeBytes: 9)]);
        Assert.False(engine.Evaluate("tiny.txt", new FilterEntryAttributes(9)).Included);
        Assert.True(engine.Evaluate("large.txt", new FilterEntryAttributes(10)).Included);
    }

    [Fact]
    public void Advanced_filter_rules_preserve_order_comments_enablement_and_attributes()
    {
        var filter = new SyncFilter(Rules:
        [
            new(FilterRuleKind.Exclude, "*.bak", "temporary backup", Enabled: false),
            new(FilterRuleKind.Exclude, "**", "large files", MinimumSizeBytes: 100, Hidden: false)
        ]);

        var rules = filter.ToRules();
        var decision = filter.CreateEngine().Evaluate("report.zip", new FilterEntryAttributes(200, IsHidden: false));

        Assert.Equal("temporary backup", rules[0].Comment);
        Assert.False(rules[0].Enabled);
        Assert.Equal(100, rules[1].MinimumSizeBytes);
        Assert.False(decision.Included);
        Assert.Equal("large files", decision.Reason);
    }

    [Theory]
    [InlineData("draft.tmp")]
    [InlineData("nested/draft.tmp")]
    [InlineData("nested\\draft.tmp")]
    public void Recursive_glob_matches_files_at_every_depth(string path)
    {
        var engine = new FilterEngine([new(FilterRuleKind.Exclude, "**/*.tmp")]);
        Assert.False(engine.Evaluate(path).Included);
    }

    [Fact]
    public void Simple_filename_pattern_matches_at_every_depth()
    {
        var engine = new FilterEngine([new(FilterRuleKind.Exclude, "Thumbs.db")]);
        Assert.False(engine.Evaluate("folder/Thumbs.db").Included);
    }

    [Theory]
    [InlineData(".git")]
    [InlineData(".git/config")]
    public void Directory_recursive_pattern_includes_the_directory_itself(string path)
    {
        var engine = new FilterEngine([new(FilterRuleKind.Exclude, ".git/**")]);
        Assert.False(engine.Evaluate(path).Included);
    }

    [Fact]
    public async Task Run_history_can_filter_by_profile_and_result()
    {
        var store = new RunHistoryRepository(Path.Combine(_root, "history.json"));
        await store.AppendAsync(new RunHistoryEntry("one", RunOutcome.Succeeded, DateTimeOffset.UtcNow, 3, 2, 0, 20));
        await store.AppendAsync(new RunHistoryEntry("two", RunOutcome.Failed, DateTimeOffset.UtcNow, 3, 1, 1, 20));
        var result = await store.QueryAsync(profileId: "two", outcome: RunOutcome.Failed);
        Assert.Single(result); Assert.Equal("two", result[0].ProfileId);
    }

    [Fact]
    public async Task Profile_runner_persists_completed_run_summary()
    {
        var left = Path.Combine(_root, "run-left"); var right = Path.Combine(_root, "run-right");
        Directory.CreateDirectory(left); Directory.CreateDirectory(right);
        await File.WriteAllTextAsync(Path.Combine(left, "file.txt"), "content");
        var profile = SyncProfile.Create("run", left, right) with { Mode = SyncMode.Mirror };
        var history = new RunHistoryRepository(Path.Combine(_root, "runs.json"));

        await new ProfileRunner(history).RunAsync(profile);

        var record = Assert.Single(await history.QueryAsync(profile.Id));
        Assert.Equal(RunOutcome.Succeeded, record.Outcome);
        Assert.Equal(1, record.Planned); Assert.Equal(1, record.Succeeded);
        Assert.True(record.TransferredBytes > 0);
    }

    [Fact]
    public async Task Profile_runner_persists_a_failed_attempt_before_execution_starts()
    {
        var profile = SyncProfile.Create("disabled", Path.Combine(_root, "left"), Path.Combine(_root, "right")) with { Enabled = false };
        var history = new RunHistoryRepository(Path.Combine(_root, "failed-runs.json"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProfileRunner(history).RunAsync(profile));

        var record = Assert.Single(await history.QueryAsync(profile.Id));
        Assert.Equal(RunOutcome.Failed, record.Outcome);
        Assert.NotEqual(default, record.CompletedUtc);
    }

    [Fact]
    public async Task Run_history_applies_retention_before_persisting()
    {
        var store = new RunHistoryRepository(Path.Combine(_root, "retention.json"), maximumEntries: 2);
        for (var i = 0; i < 3; i++)
            await store.AppendAsync(new RunHistoryEntry("profile", RunOutcome.Succeeded, DateTimeOffset.UtcNow.AddMinutes(i), i, i, 0, i));

        var records = await store.QueryAsync();
        Assert.Equal(2, records.Count);
        Assert.Equal(2, records[0].Planned); Assert.Equal(1, records[1].Planned);
    }
}
