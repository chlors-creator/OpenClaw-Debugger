using System.ComponentModel;

namespace OpenClawDebugger;

public sealed class ConnectionSettings
{
    public string Host { get; set; } = "";
    public string Username { get; set; } = "";
    public int Port { get; set; } = 22;
    public string WorkspacePath { get; set; } = "/home/user/.openclaw/workspace";
    public string StickersPath { get; set; } = "/home/user/.openclaw/workspace/stickers";

    public string Target => $"{Username}@{Host}";
}
