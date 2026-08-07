namespace FengSync.Core;

/// <summary>
/// Creates endpoint pairs over either a per-run daemon (CLI/scheduled execution) or an
/// externally owned application-lifetime RC client. Credentials remain in rclone.conf.
/// </summary>
public static class EndpointFactory
{
    public static bool IsRemote(string value) => value.StartsWith("gdrive://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("s3://", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Creates one endpoint from Feng Sync's stable URI form: sftp://remote/root, gdrive://remote/root, or s3://remote/root.</summary>
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
        var type = scheme.ToLowerInvariant() switch
        {
            "gdrive" => EndpointType.GoogleDrive,
            "sftp" => EndpointType.Sftp,
            "s3" => EndpointType.S3,
            _ => throw new InvalidOperationException("不支持的云端端点协议。")
        };
        return new RcloneEndpoint(client,
            new EndpointProfile(Guid.NewGuid(), type,
                remoteAndRoot.Length == 2 ? remoteAndRoot[1] : "", remoteAndRoot[0]),
            new(false, true, type == EndpointType.GoogleDrive, TimeSpan.FromSeconds(1),
                type == EndpointType.S3
                    ? new(MoveEvidenceCapabilities.SizeAndTime, EndpointMoveExecution.ServerCopyDelete, EndpointMoveExecution.None)
                    : new(MoveEvidenceCapabilities.StrongHash | MoveEvidenceCapabilities.SizeAndTime,
                        EndpointMoveExecution.NativeRename,
                        type == EndpointType.GoogleDrive ? EndpointMoveExecution.NativeRename : EndpointMoveExecution.None,
                        type == EndpointType.Sftp),
                new(type != EndpointType.Sftp, System.Text.NormalizationForm.FormC)));
    }
}

/// <summary>Owns the optional remote transport process as well as the two endpoint objects.</summary>
public sealed class EndpointPair(IEndpoint left, IEndpoint right, RcloneDaemon? daemon) : IAsyncDisposable
{
    public IEndpoint Left { get; } = left;
    public IEndpoint Right { get; } = right;
    public ValueTask DisposeAsync() => daemon?.DisposeAsync() ?? ValueTask.CompletedTask;
}
