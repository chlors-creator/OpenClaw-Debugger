using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenClawDebugger;

/// <summary>集中管理 OpenSSH 参数、设置校验和错误分类。</summary>
internal static class SshCommandRunner
{
    internal static void AddSshArguments(ProcessStartInfo start, ConnectionSettings settings)
    {
        start.ArgumentList.Add("-T");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("BatchMode=yes");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("StrictHostKeyChecking=yes");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("ConnectTimeout=12");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("ServerAliveInterval=15");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("ServerAliveCountMax=3");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("LogLevel=ERROR");
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add(settings.Port.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(settings.Target);
    }

    internal static Exception CreateSshFailure(string error, int exitCode)
    {
        var detail = string.Join(Environment.NewLine,
            error.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Take(8));
        if (detail.Contains("Permission denied (", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Permission denied, please try again", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("No supported authentication methods", StringComparison.OrdinalIgnoreCase))
            return new InvalidOperationException("当前 Windows OpenSSH 身份未通过服务器认证。请确认此 Windows 用户执行 ssh admin@106.14.173.90 能直接登录。");
        if (detail.Contains("a password is required", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase))
            return new InvalidOperationException("完整服务器快照需要 admin 对 tar 命令具备免密 sudo 权限；当前连接不能交互输入 sudo 密码。");
        if (detail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return new InvalidOperationException("完整服务器快照需要 admin 对 tar 命令具备 root 权限。");
        if (detail.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase))
            return new InvalidOperationException("服务器 SSH 主机指纹发生变化，快照已取消。");
        if (exitCode == 255 || IsTransientSshFailure(detail))
            return new SnapshotTransferInterruptedException(string.IsNullOrWhiteSpace(detail)
                ? "SSH 网络连接中断。"
                : "SSH 网络连接中断：" + detail);
        return new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
            ? $"服务器快照失败，SSH 退出码 {exitCode}。"
            : $"服务器快照失败：{detail}");
    }

    internal static bool IsTransientSshFailure(string detail)
    {
        string[] transientMessages =
        [
            "connection reset", "connection timed out", "connection timeout", "connection closed",
            "connection refused", "broken pipe", "network is unreachable", "no route to host",
            "operation timed out", "software caused connection abort", "connection aborted",
            "client_loop: send disconnect", "kex_exchange_identification", "could not resolve hostname"
        ];
        return transientMessages.Any(message => detail.Contains(message, StringComparison.OrdinalIgnoreCase));
    }

internal static void ValidateSettings(ConnectionSettings settings)
    {
        if (!Regex.IsMatch(settings.Host, @"^[A-Za-z0-9._:-]{1,253}$"))
            throw new InvalidOperationException("服务器地址只能包含域名或 IP 字符。");
        if (!Regex.IsMatch(settings.Username, @"^[A-Za-z_][A-Za-z0-9_.-]{0,63}$"))
            throw new InvalidOperationException("SSH 用户名格式无效。");
        if (settings.Port is < 1 or > 65535) throw new InvalidOperationException("SSH 端口必须在 1–65535 范围内。");
        if (!settings.WorkspacePath.StartsWith('/') || settings.WorkspacePath.Contains("..", StringComparison.Ordinal) ||
            !settings.StickersPath.StartsWith('/') || settings.StickersPath.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("远程目录必须是绝对路径，且不能包含 ..。");
    }
}

