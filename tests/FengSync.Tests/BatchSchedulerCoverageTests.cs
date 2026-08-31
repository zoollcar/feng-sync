using FengSync.Core.Automation;

namespace FengSync.Tests;

public sealed class BatchSchedulerCoverageTests
{
    [Fact]
    public async Task Batch_queue_bounds_concurrency_preserves_order_and_continues_after_failure()
    {
        var running = 0;
        var maximum = 0;
        var jobs = Enumerable.Range(0, 6).Select<int, Func<CancellationToken, Task<int>>>(index => async token =>
        {
            var current = Interlocked.Increment(ref running);
            SetMaximum(ref maximum, current);
            try
            {
                await Task.Delay(40, token);
                if (index == 2) throw new InvalidOperationException("expected failure");
                return index;
            }
            finally { Interlocked.Decrement(ref running); }
        });

        var results = await new BatchScheduler(2).RunAsync(jobs);

        Assert.Equal(2, maximum);
        Assert.Equal(Enumerable.Range(0, 6), results.Select(x => x.Index));
        Assert.False(results[2].Succeeded);
        Assert.Equal("expected failure", results[2].Error?.Message);
        Assert.True(results[5].Succeeded);
    }

    [Fact]
    public async Task Batch_queue_marks_jobs_waiting_on_the_gate_as_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<int>> first = async _ => { entered.SetResult(); await release.Task; return 1; };
        Func<CancellationToken, Task<int>> second = async token => { await Task.Delay(Timeout.Infinite, token); return 2; };

        var run = new BatchScheduler(1).RunAsync([first, second], cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        release.SetResult();
        var results = await run;

        Assert.True(results[0].Succeeded);
        Assert.IsType<OperationCanceledException>(results[1].Error);
    }

    private static void SetMaximum(ref int maximum, int value)
    {
        int snapshot;
        do { snapshot = maximum; if (snapshot >= value) return; }
        while (Interlocked.CompareExchange(ref maximum, value, snapshot) != snapshot);
    }
}
