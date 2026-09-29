using System.Reflection;
using System.Text;
using System.IO;

namespace OpenClawDebugger;

/// <summary>
/// Loads the remote Python agent from an embedded resource. Keeping the agent in a
/// standalone .py file makes it reviewable and keeps the SSH protocol easy to update.
/// </summary>
internal static class RemoteAgentProgram
{
    private const string ResourceName = "OpenClawDebugger.RemoteAgentProgram.py";
    private static readonly Lazy<string> Script = new(LoadScript, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Main => Script.Value;

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
}
