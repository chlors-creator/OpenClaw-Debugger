using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClawDebugger;

/// <summary>维护复用 SSH 进程，并将 JSON 请求转发给远程代理。</summary>
internal sealed class RemoteAgentSession : IDisposable
{
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private Process? _sessionProcess;
    private Task<string>? _sessionErrorTask;
    private string? _sessionKey;

    public async Task<JsonObject> InvokeAsync(
        ConnectionSettings settings, JsonObject request, CancellationToken cancellationToken,
        TimeSpan? operationTimeout = null)
    {
        SshCommandRunner.ValidateSettings(settings);
        request["workspace"] = settings.WorkspacePath;
        request["stickers"] = settings.StickersPath;
        var retryable = IsSafeToRetry(request);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await InvokeOnceAsync(settings, request, cancellationToken, operationTimeout);
            }
            catch (SnapshotTransferInterruptedException) when (retryable && attempt == 0 && !cancellationToken.IsCancellationRequested)
            {
                OperationLogStore.Current?.Stage("ssh-retry", "复用 SSH 连接中断，正在重建会话并重试只读操作", 1);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    private async Task<JsonObject> InvokeOnceAsync(
        ConnectionSettings settings, JsonObject request, CancellationToken cancellationToken,
        TimeSpan? operationTimeout)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.ToJsonString()));
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            var process = await EnsureSessionAsync(settings, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(operationTimeout ?? TimeSpan.FromMinutes(3));
            await process.StandardInput.WriteAsync(payload.AsMemory(), timeout.Token);
            await process.StandardInput.WriteAsync("\n".AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) throw new IOException("SSH 复用连接已关闭。");
            return ParseRemoteResponse(line);
        }
        catch (OperationCanceledException)
        {
            ResetSession();
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("SSH 操作超时。请检查网络、SSH 密钥代理和服务器状态。");
        }
        catch (SnapshotTransferInterruptedException)
        {
            ResetSession();
            throw;
        }
        catch (IOException ex)
        {
            var process = _sessionProcess;
            var diagnostic = await ReadSessionDiagnosticAsync(process);
            ResetSession();
            var message = string.IsNullOrWhiteSpace(diagnostic)
                ? "SSH 复用连接中断。"
                : "SSH 复用连接中断：" + diagnostic;
            throw new SnapshotTransferInterruptedException(message, ex);
        }
        catch
        {
            if (_sessionProcess is null || _sessionProcess.HasExited) ResetSession();
            throw;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task<Process> EnsureSessionAsync(ConnectionSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            return EnsureSession(settings);
        }
        catch (SnapshotTransferInterruptedException) when (!cancellationToken.IsCancellationRequested)
        {
            OperationLogStore.Current?.Stage("ssh-retry", "SSH 握手未完成，1 秒后重试", 1);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            return EnsureSession(settings);
        }
    }

    private Process EnsureSession(ConnectionSettings settings)
    {
        var key = settings.Target + ":" + settings.Port.ToString(CultureInfo.InvariantCulture) + "|" + settings.WorkspacePath + "|" + settings.StickersPath;
        if (_sessionProcess is not null && !_sessionProcess.HasExited && string.Equals(_sessionKey, key, StringComparison.Ordinal))
            return _sessionProcess;
        ResetSession();
        var start = new ProcessStartInfo
        {
            FileName = SshCommandRunner.ResolveExecutable(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        SshCommandRunner.AddSshArguments(start, settings);
        SshCommandRunner.AddPythonBootstrapArgument(start);
        var process = new Process { StartInfo = start };
        Task<string>? errorTask = null;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动 Windows OpenSSH。");
            errorTask = process.StandardError.ReadToEndAsync();
            SshCommandRunner.WritePythonBootstrap(process.StandardInput.BaseStream, RemoteAgentProgram.Main);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var stderr = SshCommandRunner.ReadStartupError(process, errorTask);
            var failure = SshCommandRunner.CreateStartupFailure(process, start.FileName, stderr, ex);
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            throw failure;
        }
        _sessionProcess = process;
        _sessionErrorTask = errorTask;
        _sessionKey = key;
        return process;
    }

    private void ResetSession()
    {
        var process = _sessionProcess;
        _sessionProcess = null;
        _sessionErrorTask = null;
        _sessionKey = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
    }

    private async Task<string> ReadSessionDiagnosticAsync(Process? process)
    {
        var parts = new List<string>();
        var errorTask = _sessionErrorTask;
        if (errorTask is not null)
        {
            try
            {
                if (!errorTask.IsCompleted)
                    await Task.WhenAny(errorTask, Task.Delay(TimeSpan.FromMilliseconds(250)));
                if (errorTask.IsCompletedSuccessfully && !string.IsNullOrWhiteSpace(errorTask.Result))
                    parts.Add(errorTask.Result.Trim());
            }
            catch { }
        }
        try
        {
            if (process is not null && process.HasExited)
                parts.Add("ssh.exe 退出码 " + process.ExitCode.ToString(CultureInfo.InvariantCulture));
        }
        catch { }
        return OperationLogStore.Redact(string.Join("；", parts));
    }

    private static bool IsSafeToRetry(JsonObject request)
    {
        var action = request["action"]?.GetValue<string>();
        return action is "inventory" or "read" or "models_inventory" or "models_test_latency";
    }

    /// <summary>主动关闭复用 SSH 进程，但保留会话对象以便之后重新连接。</summary>
    public void Disconnect() => ResetSession();

    public void Dispose()
    {
        ResetSession();
        _sessionGate.Dispose();
    }

    private static JsonObject ParseRemoteResponse(string line)
    {
        JsonObject? response;
        try { response = JsonNode.Parse(line.Trim()) as JsonObject; }
        catch (JsonException ex) { throw new InvalidOperationException("服务器返回的数据无法识别。", ex); }
        if (response is null) throw new InvalidOperationException("服务器返回的数据为空。");
        if (response["ok"]?.GetValue<bool>() != true)
        {
            var code = response["code"]?.GetValue<string>() ?? "";
            var message = response["error"]?.GetValue<string>() ?? "远程操作失败。";
            if (code == "conflict") throw new RemoteConflictException(message);
            throw new InvalidOperationException(message);
        }
        return response;
    }
}
