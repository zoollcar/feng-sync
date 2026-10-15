namespace FengSync.Lan;

/// <summary>
/// A refusal that has to travel out of a method whose job is producing bytes on disk.
/// </summary>
/// <remarks>
/// The sink, the path rules and the server all need to name the same refusal, and the server's only sane
/// mapping is one <c>catch</c> that turns a refusal into a status and a reason token. A returned tuple per
/// call would have put that mapping back into every call site.
/// </remarks>
public sealed class LanTransferException(LanRefusal refusal, string? message = null, Exception? inner = null)
    : Exception(message ?? $"局域网快传拒绝了这次请求：{refusal}", inner)
{
    public LanRefusal Refusal { get; } = refusal;

    /// <summary>Throws with the refusal the caller already worked out, so a rule never has to be restated.</summary>
    public static LanTransferException For(LanRefusal refusal, string? message = null) => new(refusal, message);
}
