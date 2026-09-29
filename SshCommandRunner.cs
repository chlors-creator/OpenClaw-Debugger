using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace OpenClawDebugger;

/// <summary>集中管理 OpenSSH 参数、设置校验和错误分类。</summary>
internal static class SshCommandRunner
{
    private static readonly string[] CommonExecutablePaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative", "OpenSSH", "ssh.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "OpenSSH", "ssh.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenSSH", "ssh.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin", "ssh.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OpenSSH", "ssh.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "usr", "bin", "ssh.exe")
    ];

    /// <summary>
    /// Resolve the Windows OpenSSH executable explicitly. A desktop shortcut can inherit a
    /// reduced PATH, so relying on only "ssh.exe" makes a working system look unconfigured.
    /// </summary>
    internal static string ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("OPENCLAW_SSH_PATH")?.Trim();
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        foreach (var candidate in CommonExecutablePaths.Where(x => !string.IsNullOrWhiteSpace(x)))
            if (File.Exists(candidate)) return candidate;

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, "ssh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }

        var checkedPaths = CommonExecutablePaths.Where(x => !string.IsNullOrWhiteSpace(x));
        throw new InvalidOperationException(
            "找不到 Windows OpenSSH 客户端 ssh.exe。已检查系统 OpenSSH、Git OpenSSH 和当前 PATH；" +
            "请安装 Windows OpenSSH Client，或设置 OPENCLAW_SSH_PATH 指向 ssh.exe。\n已检查：" +
            string.Join("；", checkedPaths));
    }

    internal static void AddPythonBootstrapArgument(ProcessStartInfo start)
    {
        // Keep the SSH command short. The first stdin line carries the base64 script;
        // the script then continues reading the JSON/binary protocol from the same stream.
        start.ArgumentList.Add("python3 -u -c \"import sys,base64;exec(base64.b64decode(sys.stdin.readline()))\"");
    }

    internal static void WritePythonBootstrap(Stream input, string program)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(program)) + "\n";
        var bytes = System.Text.Encoding.ASCII.GetBytes(encoded);
        input.Write(bytes, 0, bytes.Length);
        input.Flush();
    }

    internal static async Task WritePythonBootstrapAsync(Stream input, string program, CancellationToken cancellationToken)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(program)) + "\n";
        var bytes = System.Text.Encoding.ASCII.GetBytes(encoded);
        // WaitAsync also covers platforms where anonymous-pipe WriteAsync does not
        // observe cancellation until the underlying SSH process is closed.
        await input.WriteAsync(bytes).AsTask().WaitAsync(cancellationToken);
        await input.FlushAsync().WaitAsync(cancellationToken);
    }

    internal static string ReadStartupError(Process process, Task<string>? errorTask)
    {
        try
        {
            if (!process.HasExited) process.WaitForExit(1500);
        }
        catch { }

        if (errorTask is null) return "";
        try
        {
            if (!errorTask.IsCompleted) errorTask.Wait(1500);
            return errorTask.IsCompletedSuccessfully ? errorTask.Result : "";
        }
        catch { return ""; }
    }

    internal static Exception CreateStartupFailure(
        Process process,
        string executable,
        string stderr,
        Exception cause)
    {
        var exitCode = -1;
        try
        {
            if (process.HasExited) exitCode = process.ExitCode;
        }
        catch { }

        if (exitCode >= 0 || !string.IsNullOrWhiteSpace(stderr))
            return CreateSshFailure(stderr, exitCode);

        return new InvalidOperationException(
            $"无法启动 Windows OpenSSH：{executable}\n{cause.Message}", cause);
    }

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
        OperationLogStore.Current?.SshFailure(detail);
        if (detail.Contains("Permission denied (", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Permission denied, please try again", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("No supported authentication methods", StringComparison.OrdinalIgnoreCase))
            return new InvalidOperationException("当前 Windows OpenSSH 身份未通过服务器认证。请确认当前 Windows 用户可以直接执行 ssh <user>@<host> 登录目标服务器。");
        if (detail.Contains("a password is required", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase))
            return new InvalidOperationException("完整服务器快照需要 admin 对 tar 命令具备免密 sudo 权限；当前连接不能交互输入 sudo 密码。");
        if (detail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return new InvalidOperationException("完整服务器快照需要 admin 对 tar 命令具备 root 权限。");
        if (detail.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase))
            return new InvalidOperationException("服务器 SSH 主机指纹发生变化，快照已取消。");
        if (detail.Contains("banner exchange", StringComparison.OrdinalIgnoreCase))
            return new SnapshotTransferInterruptedException(
                "SSH 服务器在握手阶段没有返回协议横幅。请检查服务器 sshd 是否忙碌或未响应、云安全组和防火墙规则；原始错误：" + detail);
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

