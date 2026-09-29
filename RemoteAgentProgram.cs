using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;

namespace OpenClawDebugger;

/// <summary>
/// Loads the remote Python agent from an embedded resource. Keeping the agent in a
/// standalone .py file makes it reviewable and lets the SSH protocol verify its build hash.
/// </summary>
internal static class RemoteAgentProgram
{
    private const string ResourceName = "OpenClawDebugger.RemoteAgentProgram.py";
    private static readonly Lazy<string> Script = new(LoadScript, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<string> Hash = new(() => RemoteAgentProtocol.ComputeScriptHash(Script.Value), LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Main => Script.Value;
    public static string ScriptHash => Hash.Value;

    private static string LoadScript()
    {
        var assembly = typeof(RemoteAgentProgram).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("远程代理资源未嵌入：" + ResourceName);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

internal static class RemoteAgentProtocol
{
    public const int Version = 3;
    private const string Placeholder = "sha256:pending";

    public static string ComputeScriptHash(string script)
    {
        var canonical = Regex.Replace(
            script.Replace("\r\n", "\n", StringComparison.Ordinal),
            "(?m)^SCRIPT_HASH = \"sha256:[0-9a-f]+\"$",
            "SCRIPT_HASH = \"" + Placeholder + "\"");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }
}
