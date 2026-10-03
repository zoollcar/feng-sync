using FengSync.Services;
using FengSync.Views;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// Covers "测试全部": the bounded runner that probes every endpoint, and the wording of its result. The two
/// belong together because the summary is only honest if the runner really attempted every endpoint, and the
/// runner is only useful if one unreachable endpoint cannot make the whole run unreadable.
/// </summary>
public sealed class CloudEndpointCheckAllTests
{
    // ---- the bounded runner ---------------------------------------------------------------------------

    [Fact]
    public async Task The_runner_never_has_more_than_the_concurrency_it_was_given_in_flight()
    {
        var gate = new object();
        var started = new List<int>();
        var atLimit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = CloudEndpointStatusService.RunBoundedAsync(Enumerable.Range(0, 9).ToArray(), 3, async item =>
        {
            lock (gate)
            {
                started.Add(item);
                if (started.Count == 3) atLimit.TrySetResult();
            }
            await release.Task;
        });

        // Three probes take the three slots and park; the other six cannot take one, so the count stops there.
        Assert.Same(atLimit.Task, await Task.WhenAny(atLimit.Task, Task.Delay(TimeSpan.FromSeconds(10))));
        // A moment's grace for an unbounded implementation to start the rest, which it would do immediately.
        await Task.Delay(200);
        lock (gate) Assert.Equal(3, started.Count);

        release.SetResult();
        Assert.Empty(await run);
        lock (gate) Assert.Equal(9, started.Count);
    }

    [Fact]
    public async Task One_endpoint_that_cannot_be_checked_does_not_stop_the_others()
    {
        var attempted = new List<int>();

        var failures = await CloudEndpointStatusService.RunBoundedAsync(Enumerable.Range(1, 4).ToArray(), 2, item =>
        {
            lock (attempted) attempted.Add(item);
            return item == 3
                ? Task.FromException(new InvalidOperationException("rclone 拒绝了这个端点"))
                : Task.CompletedTask;
        });

        // Reported rather than thrown: the state of the other three endpoints is what the user pressed the
        // button for, and stopping at the first failure would leave them all reading "未检查".
        var failure = Assert.Single(failures);
        Assert.Equal("rclone 拒绝了这个端点", failure.Message);
        Assert.Equal([1, 2, 3, 4], attempted.OrderBy(item => item).ToList());
    }

    [Fact]
    public async Task Every_failure_is_reported_and_not_only_the_first()
    {
        var failures = await CloudEndpointStatusService.RunBoundedAsync(
            Enumerable.Range(1, 3).ToArray(), 3, item => Task.FromException(new InvalidOperationException($"端点 {item}")));

        Assert.Equal(3, failures.Count);
        Assert.Equal(["端点 1", "端点 2", "端点 3"], failures.Select(failure => failure.Message).OrderBy(message => message));
    }

    [Fact]
    public async Task A_run_with_nothing_to_check_does_nothing()
    {
        var called = false;

        var failures = await CloudEndpointStatusService.RunBoundedAsync(Array.Empty<int>(), 3, _ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        Assert.Empty(failures);
        Assert.False(called);
    }

    [Fact]
    public async Task Cancelling_stops_the_run_without_reporting_broken_endpoints()
    {
        using var cancellation = new CancellationTokenSource();
        var started = 0;

        var failures = await CloudEndpointStatusService.RunBoundedAsync(Enumerable.Range(0, 6).ToArray(), 2, _ =>
        {
            Interlocked.Increment(ref started);
            cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token);

        // Cancelling is the caller's own decision, so it is not six failures — and it does stop the run rather
        // than probing everything and discarding the answers.
        Assert.Empty(failures);
        Assert.InRange(started, 1, 5);
    }

    [Fact]
    public async Task A_concurrency_below_one_still_runs_every_item_one_at_a_time()
    {
        // A caller passing 0 gets a run, not a hang and not silence: the limit is a preference, not a contract
        // that can deadlock the button.
        var started = new List<int>();

        var failures = await CloudEndpointStatusService.RunBoundedAsync(Enumerable.Range(0, 3).ToArray(), 0, item =>
        {
            lock (started) started.Add(item);
            return Task.CompletedTask;
        });

        Assert.Empty(failures);
        Assert.Equal([0, 1, 2], started);
    }

    [Fact]
    public void The_probe_concurrency_is_a_small_bound()
    {
        // Every probe is a request through one loopback daemon to the same remote service, so the bound has to
        // be low enough to not look like a burst — and high enough that a short list is not serialised.
        Assert.InRange(CloudEndpointStatusService.ProbeConcurrency, 2, 6);
    }

    // ---- the summary line -----------------------------------------------------------------------------

    [Fact]
    public void A_run_where_everything_answered_says_so()
    {
        Assert.Equal("检查结束：3 个端点全部连接正常。", RemoteEndpointManagerWindow.DescribeCheckAll(3, 3, 0));
    }

    [Fact]
    public void A_partial_run_names_both_numbers_and_the_likely_next_step()
    {
        var message = RemoteEndpointManagerWindow.DescribeCheckAll(4, 3, 0);

        Assert.Contains("3/4", message, StringComparison.Ordinal);
        // The endpoints that did not answer are either out of date credentials or a network problem, which are
        // the two things the user can act on.
        Assert.Contains("重新登录", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_where_nothing_answered_does_not_read_like_a_success()
    {
        var message = RemoteEndpointManagerWindow.DescribeCheckAll(3, 0, 0);

        Assert.Contains("都无法连接", message, StringComparison.Ordinal);
        Assert.DoesNotContain("全部连接正常", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_probe_that_could_not_even_be_attempted_is_reported_apart_from_a_failed_one()
    {
        var message = RemoteEndpointManagerWindow.DescribeCheckAll(3, 2, 1);

        Assert.Contains("2/3", message, StringComparison.Ordinal);
        Assert.Contains("1 个未能完成检查", message, StringComparison.Ordinal);
        // It is not phrased as "无法连接": a runner failure is not an endpoint's answer.
        Assert.DoesNotContain("都无法连接", message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_list_is_not_an_all_clear()
    {
        // "0 个端点全部连接正常" would be a true sentence that says the opposite of the truth.
        Assert.Equal("没有可检查的端点。", RemoteEndpointManagerWindow.DescribeCheckAll(0, 0, 0));
    }
}
