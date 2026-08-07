using System.Text.Json;

namespace FengSync.Core.Rclone.Lifecycle;

/// <summary>
/// Injectable control-plane abstraction for long-lived rclone features. Implementations communicate
/// exclusively through rclone's typed JSON RC endpoints; they never emulate the command line.
/// </summary>
public interface IRcloneLifecycleClient
{
    Task<JsonElement> CallAsync(string operation, object payload, CancellationToken cancellationToken = default);
}

