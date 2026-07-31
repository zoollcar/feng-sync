using System.Runtime.InteropServices;

namespace FengSync.Core.SftpServer;

/// <summary>Keeps the server password outside JSON and encrypted for the current Windows user.</summary>
public sealed class SftpPasswordStore
{
    private readonly string _path;
    public SftpPasswordStore(string? path = null) => _path = path ?? Path.Combine(AppDataPaths.Root, "sftp", "server-password.dat");
    public bool HasPassword => File.Exists(_path);

    public async Task SaveAsync(string password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("SFTP 密码不能为空。", nameof(password));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var cipher = Protect(System.Text.Encoding.UTF8.GetBytes(password));
        await File.WriteAllBytesAsync(_path, cipher, ct).ConfigureAwait(false);
    }

    public async Task<string?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return null;
        var cipher = await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(Unprotect(cipher));
    }

    public void Clear() { if (File.Exists(_path)) File.Delete(_path); }

    private static byte[] Protect(byte[] value) => Transform(value, protect: true);
    private static byte[] Unprotect(byte[] value) => Transform(value, protect: false);
    private static byte[] Transform(byte[] value, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SFTP 密码存储需要 Windows DPAPI。");
        var input = DataBlobFrom(value);
        try
        {
            NativeBlob output;
            var succeeded = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output);
            if (!succeeded)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法使用 Windows DPAPI 保护 SFTP 密码。");
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, output.Size); return result; }
            finally { if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
        }
        finally { Free(ref input); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBlob { public int Size; public IntPtr Data; }
    private static NativeBlob DataBlobFrom(byte[] bytes) { var result = new NativeBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) }; Marshal.Copy(bytes, 0, result.Data, bytes.Length); return result; }
    private static void Free(ref NativeBlob value) { if (value.Data != IntPtr.Zero) { Marshal.FreeHGlobal(value.Data); value.Data = IntPtr.Zero; } }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref NativeBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out NativeBlob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptUnprotectData(ref NativeBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out NativeBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
