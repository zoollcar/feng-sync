namespace FengSync.Core;

/// <summary>
/// Creates endpoint pairs over either a per-run daemon (CLI/scheduled execution) or an
/// externally owned application-lifetime RC client. Credentials remain in rclone.conf.
/// </summary>
public static class EndpointFactory
{
    public static bool IsRemote(string value) => EndpointSchemes.IsRemote(value);

    public static async Task<EndpointPair> OpenAsync(string leftPath, string rightPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(leftPath) || string.IsNullOrWhiteSpace(rightPath))
            throw new InvalidOperationException("请先填写两个端点。");
        RcloneDaemon? daemon = null;
        try
        {
            if (IsRemote(leftPath) || IsRemote(rightPath))
                daemon = await RcloneDaemon.StartAsync(BundledRclone.ExecutablePath, BundledRclone.ConfigPath, ct);
            return new(Create(leftPath, daemon), Create(rightPath, daemon), daemon);
        }
        catch
        {
            if (daemon is not null) await daemon.DisposeAsync();
            throw;
        }
    }

    /// <summary>Creates an endpoint pair over an externally owned application-lifetime RC client.</summary>
    public static EndpointPair OpenWithClient(string leftPath, string rightPath, RcloneRcClient client)
    {
        if (string.IsNullOrWhiteSpace(leftPath) || string.IsNullOrWhiteSpace(rightPath))
            throw new InvalidOperationException("请先填写两个端点。");
        return new(CreateWithClient(leftPath, client), CreateWithClient(rightPath, client), null);
    }

    /// <summary>Creates one endpoint from Feng Sync's stable URI form, for example sftp://remote/root.</summary>
    public static IEndpoint Create(string value, RcloneDaemon? daemon = null)
    {
        if (!IsRemote(value)) return new LocalEndpoint(value);
        if (daemon is null) throw new InvalidOperationException("云端连接未启动。");
        return CreateWithClient(value, daemon.Client);
    }

    public static IEndpoint CreateWithClient(string value, RcloneRcClient client)
    {
        if (!IsRemote(value)) return new LocalEndpoint(value);
        var separator = value.IndexOf("://", StringComparison.Ordinal);
        var scheme = value[..separator];
        var remoteAndRoot = value[(separator + 3)..].Split('/', 2, StringSplitOptions.None);
        if (string.IsNullOrWhiteSpace(remoteAndRoot[0]))
            throw new InvalidOperationException("远端端点缺少 rclone remote 名称。");
        var type = EndpointSchemes.TypeForScheme(scheme)
            ?? throw new InvalidOperationException("不支持的云端端点协议。");
        var (move, paths) = Capabilities(type);
        return new RcloneEndpoint(client,
            new EndpointProfile(Guid.NewGuid(), type,
                remoteAndRoot.Length == 2 ? remoteAndRoot[1] : "", remoteAndRoot[0]),
            new(false, true, type is EndpointType.GoogleDrive, TimeSpan.FromSeconds(1), move, paths));
    }

    /// <summary>
    /// How a backend behaves under move detection. Every arm states something about rclone's
    /// implementation of that backend, so an unlisted provider must not be guessed at: the fallback is the
    /// conservative combination (hash evidence, server-side copy then delete, no directory rename), which
    /// degrades to a plain copy+delete rather than skipping a real change. Case sensitivity follows the
    /// existing convention that every cloud backend except SFTP is folded — the planner ORs the two sides,
    /// so declaring one side sensitive would also force a case-insensitive local folder into strict mode.
    /// </summary>
    private static (EndpointMoveCapabilities Move, EndpointPathSemantics Paths) Capabilities(EndpointType type) => type switch
    {
        // S3 exposes no checksum it can compute cheaply, and its "rename" is a server-side copy.
        EndpointType.S3 => (
            new(MoveEvidenceCapabilities.SizeAndTime, EndpointMoveExecution.ServerCopyDelete, EndpointMoveExecution.None),
            new(false, System.Text.NormalizationForm.FormC)),
        // SFTP hashes on request — hence the runtime probe — and renames natively, but not directories.
        EndpointType.Sftp => (
            new(MoveEvidenceCapabilities.StrongHash | MoveEvidenceCapabilities.SizeAndTime,
                EndpointMoveExecution.NativeRename, EndpointMoveExecution.None, RequiresRuntimeProbe: true),
            new(true, System.Text.NormalizationForm.FormC)),
        // Drive and WebDAV rename files and directories server-side.
        EndpointType.GoogleDrive or EndpointType.WebDav => (
            new(MoveEvidenceCapabilities.StrongHash | MoveEvidenceCapabilities.SizeAndTime,
                EndpointMoveExecution.NativeRename, EndpointMoveExecution.NativeRename),
            new(false, System.Text.NormalizationForm.FormC)),
        // OneDrive, Dropbox and Box copy server-side but have no rename of their own.
        _ => (
            new(MoveEvidenceCapabilities.StrongHash | MoveEvidenceCapabilities.SizeAndTime,
                EndpointMoveExecution.ServerCopyDelete, EndpointMoveExecution.None),
            new(false, System.Text.NormalizationForm.FormC))
    };
}

/// <summary>Owns the optional remote transport process as well as the two endpoint objects.</summary>
public sealed class EndpointPair(IEndpoint left, IEndpoint right, RcloneDaemon? daemon) : IAsyncDisposable
{
    public IEndpoint Left { get; } = left;
    public IEndpoint Right { get; } = right;
    public ValueTask DisposeAsync() => daemon?.DisposeAsync() ?? ValueTask.CompletedTask;
}
