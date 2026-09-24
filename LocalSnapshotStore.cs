using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenClawDebugger;

public sealed record SnapshotInfo(string Id, string Root, string RelativePath, string Sha256, DateTimeOffset CreatedUtc, string EncryptedFile);

public sealed class LocalSnapshotStore(string privateDirectory)
{
    private readonly string _root = Path.Combine(privateDirectory, "Rollback");

    public string RootDirectory => _root;

    public async Task<SnapshotInfo> SaveAsync(string root, string relativePath, byte[] content, string sha256)
    {
        Directory.CreateDirectory(_root);
        var id = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}_{Guid.NewGuid():N}";
        var encryptedName = id + ".dpapi";
        var encryptedPath = Path.Combine(_root, encryptedName);
        var metadataPath = Path.Combine(_root, id + ".json");
        var protectedBytes = Dpapi.Protect(content);
        var temporary = encryptedPath + ".tmp";
        await File.WriteAllBytesAsync(temporary, protectedBytes);
        File.Move(temporary, encryptedPath, overwrite: false);
        var info = new SnapshotInfo(id, root, relativePath, sha256, DateTimeOffset.UtcNow, encryptedName);
        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(metadataPath, json, new UTF8Encoding(false));
        return info;
    }

    public async Task<byte[]> ReadAsync(SnapshotInfo info)
    {
        var encryptedPath = Path.Combine(_root, Path.GetFileName(info.EncryptedFile));
        return Dpapi.Unprotect(await File.ReadAllBytesAsync(encryptedPath));
    }
}

internal static class Dpapi
{
    private const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob input, string? description, IntPtr optionalEntropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob output);

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input, IntPtr description, IntPtr optionalEntropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob output);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Protect(byte[] input) => Transform(input, protect: true);
    public static byte[] Unprotect(byte[] input) => Transform(input, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputPointer = Marshal.AllocHGlobal(Math.Max(input.Length, 1));
        try
        {
            Marshal.Copy(input, 0, inputPointer, input.Length);
            var blob = new DataBlob { Length = input.Length, Data = inputPointer };
            DataBlob output;
            var success = protect
                ? CryptProtectData(ref blob, "OpenClaw Manager rollback snapshot", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(inputPointer); }
    }
}