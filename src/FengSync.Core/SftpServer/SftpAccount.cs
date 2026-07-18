using System.Security.Cryptography;

namespace FengSync.Core.SftpServer;

public enum SftpPermission { ReadOnly, ReadWrite }

/// <summary>Account records retain a PBKDF2 verifier only, never a recoverable password.</summary>
public sealed record SftpAccount(string UserName, bool Enabled, string PasswordSalt, string PasswordHash, int PasswordIterations = 210_000, IReadOnlyList<string>? PublicKeys = null, IReadOnlyList<string>? AllowedShares = null)
{
    public static SftpAccount CreatePasswordAccount(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password)) throw new ArgumentException("用户名和密码不能为空。");
        var salt = RandomNumberGenerator.GetBytes(16);
        const int iterations = 210_000;
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return new(userName, true, Convert.ToBase64String(salt), Convert.ToBase64String(hash), iterations);
    }

    public bool VerifyPassword(string password)
    {
        try
        {
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(PasswordSalt), PasswordIterations, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(PasswordHash));
        }
        catch (FormatException) { return false; }
    }
}

public sealed record SftpShare(string VirtualName, string PhysicalPath, SftpPermission Permission);
