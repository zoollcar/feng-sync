namespace FengSync.Core.Mount;

/// <summary>Lightweight snapshot of a running process used by mount discovery.</summary>
public sealed record RcloneProcessSnapshot(int Pid, string? CommandLine, DateTimeOffset? StartedUtc, bool CommandLineReadable);

/// <summary>Injectable so tests can drive mount discovery without touching WMI.</summary>
public interface IProcessEnumerator
{
    IReadOnlyList<RcloneProcessSnapshot> EnumerateRcloneProcesses();
}