using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OpenClawDebugger;

/// <summary>
/// Small Windows Credential Manager adapter. Only opaque target names and non-secret
/// indexes remain in the application's private directory; secret material is kept in
/// the per-user Windows vault.
/// </summary>
internal static class WindowsCredentialStore
{
    private const uint Generic = 1;
    private const uint PersistLocalMachine = 2;

    public static void Write(string target, string secret)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("凭据目标不能为空。", nameof(target));
        if (secret is null) throw new ArgumentNullException(nameof(secret));
        var blob = Encoding.UTF8.GetBytes(secret);
        var blobPointer = IntPtr.Zero;
        try
        {
            blobPointer = Marshal.AllocHGlobal(Math.Max(1, blob.Length));
            if (blob.Length > 0) Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new NativeCredential
            {
                Type = Generic,
                TargetName = target,
                CredentialBlob = blobPointer,
                CredentialBlobSize = (uint)blob.Length,
                Persist = PersistLocalMachine,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager 写入失败。");
        }
        finally
        {
            if (blobPointer != IntPtr.Zero) Marshal.FreeHGlobal(blobPointer);
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    public static string? Read(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        if (!CredRead(target, Generic, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            // ERROR_NOT_FOUND is a normal cache miss.
            if (error == 1168) return null;
            throw new Win32Exception(error, "Windows Credential Manager 读取失败。");
        }
        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (native.CredentialBlob == IntPtr.Zero || native.CredentialBlobSize == 0) return "";
            var bytes = new byte[native.CredentialBlobSize];
            Marshal.Copy(native.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public static void Delete(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        if (!CredDelete(target, Generic, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168) throw new Win32Exception(error, "Windows Credential Manager 删除失败。");
        }
    }

    public static string ModelApiKeyTarget(ConnectionSettings settings, string modelRef) =>
        "OpenClawDebugger/ModelApiKey/" + StableKey(settings.Target + ":" + settings.Port + ":" + modelRef);

    public static string SshSecretTarget(ConnectionSettings settings) =>
        "OpenClawDebugger/SshSecret/" + StableKey(settings.Target + ":" + settings.Port);

    private static string StableKey(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
