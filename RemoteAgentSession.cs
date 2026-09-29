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
    private string? _sessionKey;

    public async Task<JsonObject> InvokeAsync(
        ConnectionSettings settings, JsonObject request, CancellationToken cancellationToken)
    {
        SshCommandRunner.ValidateSettings(settings);
        request["workspace"] = settings.WorkspacePath;
        request["stickers"] = settings.StickersPath;
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.ToJsonString()));
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            var process = EnsureSession(settings);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
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
        catch (IOException ex)
        {
            ResetSession();
            throw new SnapshotTransferInterruptedException("SSH 复用连接中断。", ex);
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
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动 Windows OpenSSH。");
            SshCommandRunner.WritePythonBootstrap(process.StandardInput.BaseStream, RemoteAgentProgram.Main);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            process.Dispose();
            throw new InvalidOperationException($"无法启动 Windows OpenSSH：{start.FileName}\n{ex.Message}", ex);
        }
        _ = process.StandardError.ReadToEndAsync();
        _sessionProcess = process;
        _sessionKey = key;
        return process;
    }

    private void ResetSession()
    {
        var process = _sessionProcess;
        _sessionProcess = null;
        _sessionKey = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
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
