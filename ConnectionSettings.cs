using System.ComponentModel;

namespace OpenClawDebugger;

public sealed class ConnectionSettings
{
    public string Host { get; set; } = "106.14.173.90";
    public string Username { get; set; } = "admin";
    public int Port { get; set; } = 22;
    public string WorkspacePath { get; set; } = "/home/admin/.openclaw/workspace";
    public string StickersPath { get; set; } = "/home/admin/.openclaw/workspace/stickers";

    public string Target => $"{Username}@{Host}";
}
