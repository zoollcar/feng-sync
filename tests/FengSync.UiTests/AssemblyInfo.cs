using Xunit;

// UI Automation drives the desktop's shared input and window tree. Running
// scenarios concurrently opens multiple FengSync instances and lets one
// scenario interact with another's dialogs.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
