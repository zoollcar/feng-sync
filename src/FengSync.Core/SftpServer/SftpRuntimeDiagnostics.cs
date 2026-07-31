namespace FengSync.Core.SftpServer;

public sealed record SftpRuntimeStatus(bool CanStart, string Summary, string RcloneExecutable);

/// <summary>Validates the rclone runtime shipped with Feng Sync; Node.js is deliberately not involved.</summary>
public sealed class SftpRuntimeDiagnostics
{
    public SftpRuntimeStatus Inspect(SftpServerOptions options)
    {
        try
        {
            var rclone = BundledRclone.ExecutablePath;
            var problems = new List<string>();
            if (options.Enabled)
            {
                try { options.Validate(); } catch (InvalidOperationException ex) { problems.Add(ex.Message); }
            }
            return new(problems.Count == 0, problems.Count == 0 ? "rclone SFTP 运行时已就绪。" : string.Join(Environment.NewLine, problems), rclone);
        }
        catch (FileNotFoundException ex) { return new(false, ex.Message, ex.FileName ?? "rclone.exe"); }
    }
}
