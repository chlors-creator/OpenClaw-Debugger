using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClawDebugger;

internal sealed record RemoteAgentBinaryResponse(JsonObject Metadata, byte[] Data);

/// <summary>维护复用 SSH 进程，并将 JSON/二进制帧请求转发给远程代理。</summary>
internal sealed class RemoteAgentSession : IDisposable
{
    private const int MaxRetryCount = 5;
    private const int MaxLineBytes = 8 * 1024 * 1024;
    private const int MaxBinaryBytes = 16 * 1024 * 1024;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly byte[] _outputBuffer = new byte[16 * 1024];
    private Process? _sessionProcess;
    private Task<string>? _sessionErrorTask;
    private string? _sessionKey;
    private int _outputOffset;
    private int _outputCount;

    public async Task<JsonObject> InvokeAsync(
        ConnectionSettings settings, JsonObject request, CancellationToken cancellationToken,
        TimeSpan? operationTimeout = null)
    {
        var response = await InvokeCoreAsync(settings, request, cancellationToken, operationTimeout, null, false);
        return response.Metadata;
    }

    public Task<RemoteAgentBinaryResponse> InvokeBinaryAsync(
        ConnectionSettings settings,
        JsonObject request,
        ReadOnlyMemory<byte>? requestBody = null,
        CancellationToken cancellationToken = default,
        TimeSpan? operationTimeout = null) =>
        InvokeCoreAsync(settings, request, cancellationToken, operationTimeout, requestBody, true);

    private async Task<RemoteAgentBinaryResponse> InvokeCoreAsync(
        ConnectionSettings settings,
        JsonObject request,
        CancellationToken cancellationToken,
        TimeSpan? operationTimeout,
        ReadOnlyMemory<byte>? requestBody,
        bool expectBinaryResponse)
    {
        SshCommandRunner.ValidateSettings(settings);
        request["workspace"] = settings.WorkspacePath;
        request["stickers"] = settings.StickersPath;
        request["protocolVersion"] = RemoteAgentProtocol.Version;
        request["scriptHash"] = RemoteAgentProgram.ScriptHash;
        if (requestBody is { } body)
            request["binaryLength"] = body.Length;
        else
            request.Remove("binaryLength");
        request["binaryResponse"] = expectBinaryResponse;
        request["requestId"] ??= Guid.NewGuid().ToString("N");

        var retryable = IsSafeToRetry(request);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await InvokeOnceAsync(settings, request, cancellationToken, operationTimeout, requestBody);
            }
            catch (SnapshotTransferInterruptedException) when (retryable && attempt < MaxRetryCount && !cancellationToken.IsCancellationRequested)
            {
                var delay = RetryDelay(attempt);
                OperationLogStore.Current?.Stage("ssh-retry", $"复用 SSH 连接中断，{delay.TotalSeconds:0} 秒后重建会话并重试只读操作", attempt + 1);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task<RemoteAgentBinaryResponse> InvokeOnceAsync(
        ConnectionSettings settings,
        JsonObject request,
        CancellationToken cancellationToken,
        TimeSpan? operationTimeout,
        ReadOnlyMemory<byte>? requestBody)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.ToJsonString()));
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(operationTimeout ?? TimeSpan.FromMinutes(3));
            var process = await EnsureSessionAsync(settings, timeout.Token);
            var input = process.StandardInput.BaseStream;
            await WriteAsync(input, Encoding.ASCII.GetBytes(payload + "\n"), timeout.Token);
            if (requestBody is { } body && !body.IsEmpty)
                await input.WriteAsync(body, timeout.Token);
            await input.FlushAsync(timeout.Token);
            var line = await ReadProtocolLineAsync(process.StandardOutput.BaseStream, timeout.Token);
            if (line is null) throw new IOException("SSH 复用连接已关闭。");
            var response = ParseRemoteResponse(line);
            var binaryLength = response["binaryFrame"]?.GetValue<bool>() == true
                ? response["binaryLength"]?.GetValue<int>() ?? -1
                : 0;
            if (binaryLength < 0 || binaryLength > MaxBinaryBytes)
                throw new InvalidDataException("服务器返回的二进制帧长度无效。");
            var data = binaryLength == 0 ? [] : await ReadExactAsync(process.StandardOutput.BaseStream, binaryLength, timeout.Token);
            if (binaryLength > 0)
            {
                var delimiter = await ReadOutputByteAsync(process.StandardOutput.BaseStream, timeout.Token);
                if (delimiter == '\r') delimiter = await ReadOutputByteAsync(process.StandardOutput.BaseStream, timeout.Token);
                if (delimiter != '\n') throw new InvalidDataException("服务器二进制帧缺少结束分隔符。");
            }
            return new RemoteAgentBinaryResponse(response, data);
        }
        catch (OperationCanceledException)
        {
            ResetSession();
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("SSH 操作超时。请检查连接、密钥代理和服务器状态。");
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
        cancellationToken.ThrowIfCancellationRequested();
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
            await SshCommandRunner.WritePythonBootstrapAsync(
                process.StandardInput.BaseStream,
                RemoteAgentProgram.Main,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            throw;
        }
        catch (Exception ex)
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
        ResetOutputBuffer();
        return process;
    }

    private void ResetSession()
    {
        var process = _sessionProcess;
        _sessionProcess = null;
        _sessionErrorTask = null;
        _sessionKey = null;
        ResetOutputBuffer();
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
    }

    private void ResetOutputBuffer()
    {
        _outputOffset = 0;
        _outputCount = 0;
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

    private static TimeSpan RetryDelay(int attempt)
    {
        var seconds = Math.Min(16, 1 << Math.Clamp(attempt, 0, 4));
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task<string?> ReadProtocolLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>();
        while (writer.WrittenCount < MaxLineBytes)
        {
            var value = await ReadOutputByteAsync(stream, cancellationToken);
            if (value < 0) return writer.WrittenCount == 0 ? null : throw new EndOfStreamException("服务器响应在行结束前中断。");
            if (value == '\n') return Encoding.UTF8.GetString(writer.WrittenSpan).TrimEnd('\r');
            writer.Write(new[] { (byte)value });
        }
        throw new InvalidDataException("服务器响应行超过允许大小。");
    }

    private async ValueTask<int> ReadOutputByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (_outputOffset >= _outputCount)
        {
            _outputCount = await stream.ReadAsync(_outputBuffer.AsMemory(), cancellationToken);
            _outputOffset = 0;
            if (_outputCount == 0) return -1;
        }
        return _outputBuffer[_outputOffset++];
    }

    private async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        var result = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var value = await ReadOutputByteAsync(stream, cancellationToken);
            if (value < 0) throw new EndOfStreamException("服务器二进制响应提前结束。");
            result[offset++] = (byte)value;
        }
        return result;
    }

    private static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        await stream.WriteAsync(bytes, cancellationToken);

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
