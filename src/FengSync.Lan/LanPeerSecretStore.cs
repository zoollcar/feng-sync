using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FengSync.Core;

namespace FengSync.Lan;

/// <summary>
/// The pairing tokens for remembered devices, encrypted for the current Windows user and stored apart from
/// the device list itself.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LanPeerStore"/> holds names, addresses and timestamps — things that are safe to open in an
/// editor or paste into a bug report. A token is not one of those: it is the whole credential for posting
/// files into this machine's receive directory, so it lives in its own DPAPI blob the way the SFTP server's
/// password does. The split is what lets the peer list be a plain JSON file at all.
/// </para>
/// <para>
/// A device that re-pairs gets a <b>new</b> token over the old one. That is deliberate: a phone whose token
/// leaked should not be able to keep using it after the user paired it again, and there is no reason to hold
/// two live credentials for one device.
/// </para>
/// <para>
/// A blob that will not decrypt is treated as "no device is trusted" and replaced on the next issue, not
/// reported as an error. DPAPI fails for two reasons — somebody else wrote the file, or this is a different
/// Windows user — and both mean the same thing to the listener: re-pair.
/// </para>
/// </remarks>
public sealed class LanPeerSecretStore(string? path = null)
{
    /// <summary>256 bits, hex-encoded into the ASCII a header has to carry.</summary>
    public const int TokenBytes = 32;

    private readonly SemaphoreSlim _gate = new(1, 1);
    public string Path { get; } = path ?? System.IO.Path.Combine(AppDataPaths.LanTransfer, "lan-peer-secrets.dat");

    public bool HasSecrets => File.Exists(Path);

    /// <summary>Issues a fresh token for a device, replacing whatever it had before.</summary>
    public async Task<string> IssueAsync(string deviceId, CancellationToken ct = default)
    {
        var token = NewToken();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var secrets = await LoadUnlockedAsync().ConfigureAwait(false);
            secrets[deviceId] = token;
            await WriteUnlockedAsync(secrets).ConfigureAwait(false);
            return token;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Whether this device, presenting this token, is the device it claims to be.</summary>
    public async Task<bool> VerifyAsync(string deviceId, string token, CancellationToken ct = default)
    {
        if (deviceId.Length == 0 || token.Length == 0) return false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var secrets = await LoadUnlockedAsync().ConfigureAwait(false);
            return secrets.TryGetValue(deviceId, out var stored) && FixedTimeEquals(stored, token);
        }
        finally { _gate.Release(); }
    }

    /// <summary>The token held for a device, so this side can call a peer that paired with it earlier.</summary>
    public async Task<string?> TokenForAsync(string deviceId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var secrets = await LoadUnlockedAsync().ConfigureAwait(false);
            return secrets.TryGetValue(deviceId, out var token) ? token : null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Drops one device's credential. Un-trusting a device without this leaves the secret behind.</summary>
    public async Task<bool> ForgetAsync(string deviceId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var secrets = await LoadUnlockedAsync().ConfigureAwait(false);
            if (!secrets.Remove(deviceId)) return false;
            await WriteUnlockedAsync(secrets).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Forgets every device. The peer list is the user's view; this is the credential behind it.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Deleting is the honest outcome: an empty map written to disk still reads as "the file exists and
            // holds nothing", which is a state no reader needs.
            if (File.Exists(Path)) File.Delete(Path);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Which devices hold a credential. Names only, so a window can cross-check its own list.</summary>
    public async Task<IReadOnlyList<string>> SecretDeviceIdsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var secrets = await LoadUnlockedAsync().ConfigureAwait(false);
            return [.. secrets.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase)];
        }
        finally { _gate.Release(); }
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenBytes)).ToLowerInvariant();

    /// <summary>
    /// Compares two secrets without the running time depending on where they first differ. The map is read
    /// from this machine's own disk, so this is defence in depth rather than the main gate — but a token check
    /// that leaks how far a guess got right is the wrong shape for the one call an outsider can hammer.
    /// </summary>
    private static bool FixedTimeEquals(string stored, string presented)
    {
        var left = Encoding.UTF8.GetBytes(stored);
        var right = Encoding.UTF8.GetBytes(presented);
        // A length mismatch is not a secret: a token has one length, and saying so early costs nothing that a
        // count of requests does not already reveal.
        if (left.Length != right.Length) return false;
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    /// <summary>Reads the blob, returning an empty map for anything this class cannot trust.</summary>
    private async Task<Dictionary<string, string>> LoadUnlockedAsync()
    {
        if (!File.Exists(Path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        byte[] cipher;
        try { cipher = await File.ReadAllBytesAsync(Path).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }

        try
        {
            var json = Encoding.UTF8.GetString(Unprotect(cipher));
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return map is null or { Count: 0 }
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is Win32Exception or JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
    }

    private async Task WriteUnlockedAsync(Dictionary<string, string> secrets)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var cipher = Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(secrets)));
        var temporary = Path + ".tmp";
        await File.WriteAllBytesAsync(temporary, cipher).ConfigureAwait(false);
        File.Move(temporary, Path, overwrite: true);
    }

    // The crypt32 shape copied from SftpPasswordStore rather than factored out: that path is tested and owned
    // by the SFTP server, and a shared helper would put a LAN feature in a position to break a working one to
    // save twenty lines.
    private static byte[] Protect(byte[] value) => Transform(value, protect: true);
    private static byte[] Unprotect(byte[] value) => Transform(value, protect: false);

    private static byte[] Transform(byte[] value, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("局域网快传的设备密钥需要 Windows DPAPI。");
        var input = DataBlobFrom(value);
        try
        {
            NativeBlob output;
            var succeeded = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output);
            if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法使用 Windows DPAPI 保护局域网配对密钥。");
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
        }
        finally { Free(ref input); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeBlob { public int Size; public IntPtr Data; }

    private static NativeBlob DataBlobFrom(byte[] bytes)
    {
        // AllocHGlobal(0) is allowed to return IntPtr.Zero, which this struct cannot tell apart from "no data";
        // the map is never empty on this path, so a zero-length blob is refused rather than half-handled.
        if (bytes.Length == 0) throw new ArgumentException("空的密钥不能加密。", nameof(bytes));
        var result = new NativeBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, result.Data, bytes.Length);
        return result;
    }

    private static void Free(ref NativeBlob value)
    {
        if (value.Data != IntPtr.Zero) { Marshal.FreeHGlobal(value.Data); value.Data = IntPtr.Zero; }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref NativeBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out NativeBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref NativeBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out NativeBlob output);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
