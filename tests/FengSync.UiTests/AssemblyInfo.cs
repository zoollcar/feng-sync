using Xunit;

// UI Automation talks to one interactive Windows desktop. Parallel test processes
// race for focus and can make otherwise-valid controls disappear between calls.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
