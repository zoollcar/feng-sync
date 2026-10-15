namespace FengSync.Lan;

/// <summary>
/// Everything the LAN transfer service needs to decide before it binds. One record, one validator, so the
/// window, the server and the tests all read the same numbers.
/// </summary>
/// <param name="Port">
/// Both the TCP transfer port and the UDP discovery port. Fixed rather than ephemeral because the firewall
/// rule is a port rule, and a rule that names a port is the only one that can be checked before asking for
/// elevation.
/// </param>
/// <param name="ReceiveDirectory">
/// Where inbound bytes land. Null means the system Downloads folder, resolved at use rather than stored,
/// because "the user never chose one" and "the user chose one that no longer exists" are different answers
/// the window has to show differently.
/// </param>
public sealed record LanOptions(
    int Port = LanOptions.DefaultPort,
    string? ReceiveDirectory = null,
    bool AutoAcceptFromTrusted = true)
{
    public const int DefaultPort = 59871;

    /// <summary>Below 1024 the port is a privileged service slot; the upper half of the range is where a desktop listener belongs.</summary>
    public const int MinimumPort = 1024;
    public const int MaximumPort = 65535;

    /// <summary>Rejects a port the listener could not bind or a firewall rule could not name.</summary>
    public void Validate()
    {
        if (Port is < MinimumPort or > MaximumPort)
            throw new InvalidOperationException($"局域网快传端口必须介于 {MinimumPort} 和 {MaximumPort} 之间。");
    }
}
