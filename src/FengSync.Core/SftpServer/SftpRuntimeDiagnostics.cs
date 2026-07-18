namespace FengSync.Core.SftpServer;

/// <summary>Checks the separately deployed Node protocol runtime before the listener is enabled.</summary>
public sealed record SftpRuntimeStatus(bool CanStart, string Summary, string NodeExecutable, string ModuleDirectory, string ProtocolHostPath);

public sealed class SftpRuntimeDiagnostics
{
    private readonly string _baseDirectory;

    public SftpRuntimeDiagnostics(string? baseDirectory = null) => _baseDirectory = baseDirectory ?? AppContext.BaseDirectory;

    public SftpRuntimeStatus Inspect(SftpServerOptions options)
    {
        var script = Path.Combine(_baseDirectory, "SftpServer", "node-sftp-host.cjs");
        var node = ResolveNode(options.NodeExecutablePath);
        var modules = options.NodeModulePath ?? Path.Combine(_baseDirectory, "SftpServer", "node_modules");
        var problems = new List<string>();
        if (!File.Exists(script)) problems.Add("未找到 SFTP 协议主机 node-sftp-host.cjs；请重新安装完整应用。");
        if (node is null) problems.Add("未找到 Node.js 运行时；请在 SFTP 设置中指定 node.exe，或安装 Node.js 并重新启动应用。");
        if (!File.Exists(Path.Combine(modules, "ssh2", "package.json"))) problems.Add("未找到固定版本的 ssh2 模块；请使用应用安装包的 SFTP 运行时，或在应用 SftpServer 目录执行 npm ci --omit=dev 后指定该 node_modules 目录。");
        return new(problems.Count == 0, problems.Count == 0 ? "SFTP 协议运行时已就绪。" : string.Join(Environment.NewLine, problems), node ?? options.NodeExecutablePath ?? "node", modules, script);
    }

    private static string? ResolveNode(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        var names = OperatingSystem.IsWindows() ? new[] { "node.exe", "node" } : new[] { "node" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }
}
