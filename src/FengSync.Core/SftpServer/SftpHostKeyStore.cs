using System.Security.Cryptography;

namespace FengSync.Core.SftpServer;

public sealed record SftpHostKeyReference(string Path, string Fingerprint);

/// <summary>Owns the private-host-key location independently from user-editable service settings.</summary>
public sealed class SftpHostKeyStore
{
    private readonly string _directory;
    public SftpHostKeyStore(string? directory = null) => _directory = directory ?? Path.Combine(AppDataPaths.Root, "sftp");

    public SftpHostKeyReference GetKeyReference()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.GetFullPath(Path.Combine(_directory, "host-key.pem"));
        // Generate before displaying the fingerprint: the settings UI must never show a hash of an
        // empty future file. The Node protocol host consumes this PKCS#1 PEM unchanged.
        if (!File.Exists(path))
        {
            using var rsa = RSA.Create(3072);
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream);
                writer.Write(rsa.ExportRSAPrivateKeyPem());
            }
            catch (IOException) when (File.Exists(path)) { /* another process generated the shared key */ }
        }
        // Hashing raw key bytes gives a stable, displayable identifier without copying private material into settings JSON.
        var bytes = File.ReadAllBytes(path);
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('=');
        return new(path, fingerprint);
    }
}
