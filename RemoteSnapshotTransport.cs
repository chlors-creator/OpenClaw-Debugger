using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenClawDebugger;

public sealed record ServerSnapshotProgress(
    string Phase,
    long Bytes,
    long? TotalBytes,
    double? BytesPerSecond,
    long? RemainingSeconds,
    int Attempt = 1,
    int MaxAttempts = 1,
    string? Message = null);

public sealed class SnapshotTransferInterruptedException(string message, Exception? innerException = null)
    : IOException(message, innerException);

public sealed class RemoteSnapshotTransport
{
    public async Task CreateServerSnapshotAsync(
        ConnectionSettings settings,
        string remotePath,
        BackupPauseController? pauseController = null,
        CancellationToken cancellationToken = default)
    {
        SshCommandRunner.ValidateSettings(settings);
        ValidateRemoteSnapshotPath(remotePath);
        if (pauseController is not null) await pauseController.WaitIfPausedAsync(cancellationToken);
        var start = CreateSnapshotStart(settings, redirectOutput: true);
        start.ArgumentList.Add("sudo -n tar --create --gzip --file='" + remotePath + "' --numeric-owner --acls --xattrs --xattrs-include='*' --sparse --exclude=./proc --exclude=./sys --exclude=./dev --exclude=./run -C / .");
        using var process = StartProcess(start);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(12));
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdoutTask;
            var error = await stderrTask;
            if (process.ExitCode != 0) throw SshCommandRunner.CreateSshFailure(error, process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("服务器生成快照超过 12 小时，操作已中止。");
        }
        finally
        {
            if (!process.HasExited)
                try { process.Kill(entireProcessTree: true); } catch { }
        }
    }

    public async Task<long> GetServerSnapshotSizeAsync(
        ConnectionSettings settings,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        SshCommandRunner.ValidateSettings(settings);
        ValidateRemoteSnapshotPath(remotePath);
        var start = CreateSnapshotStart(settings, redirectOutput: true);
        start.ArgumentList.Add("sudo -n stat -c %s -- '" + remotePath + "'");
        using var process = StartProcess(start);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await stderrTask;
            if (process.ExitCode != 0) throw SshCommandRunner.CreateSshFailure(error, process.ExitCode);
            var text = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) || bytes <= 0)
                throw new InvalidDataException("服务器未返回有效的快照大小。");
            return bytes;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("读取服务器快照大小超时。");
        }
        finally
        {
            if (!process.HasExited)
                try { process.Kill(entireProcessTree: true); } catch { }
        }
    }

    public async Task<RemoteSnapshotTransferResult> ResumeServerSnapshotAsync(
        ConnectionSettings settings,
        string remotePath,
        Stream destination,
        long offset,
        long totalBytes,
        BackupPauseController? pauseController = null,
        IProgress<ServerSnapshotProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        SshCommandRunner.ValidateSettings(settings);
        ValidateRemoteSnapshotPath(remotePath);
        if (offset < 0 || totalBytes <= 0 || offset > totalBytes) throw new ArgumentOutOfRangeException(nameof(offset));
        var start = CreateSnapshotStart(settings, redirectOutput: true);
        start.ArgumentList.Add("sudo -n tail -c +" + (offset + 1).ToString(CultureInfo.InvariantCulture) + " -- '" + remotePath + "'");
        using var process = StartProcess(start);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(12));
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        var buffer = new byte[256 * 1024];
        var timer = Stopwatch.StartNew();
        var transferred = offset;
        var chunkBytes = 0L;
        var lastReportAt = TimeSpan.Zero;
        try
        {
            if (offset == 0)
            {
                var header = new byte[2];
                var readHeader = 0;
                while (readHeader < header.Length)
                {
                    if (pauseController is not null) await pauseController.WaitIfPausedAsync(timeout.Token);
                    var read = await ReadSnapshotOutputAsync(process.StandardOutput.BaseStream, header.AsMemory(readHeader), timeout.Token);
                    if (read == 0) throw new SnapshotTransferInterruptedException("服务器快照在读取 gzip 头时提前结束。");
                    readHeader += read;
                }
                if (header[0] != 0x1f || header[1] != 0x8b)
                    throw new InvalidDataException("远程快照不是 gzip 数据。");
                await destination.WriteAsync(header, timeout.Token);
                transferred += header.Length;
                chunkBytes += header.Length;
            }
            while (true)
            {
                if (pauseController is not null) await pauseController.WaitIfPausedAsync(timeout.Token);
                var read = await ReadSnapshotOutputAsync(process.StandardOutput.BaseStream, buffer, timeout.Token);
                if (read == 0) break;
                if (transferred + read > totalBytes) throw new InvalidDataException("服务器快照大小在传输期间发生变化。");
                await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                transferred += read;
                chunkBytes += read;
                var elapsed = timer.Elapsed;
                if (chunkBytes >= 1024 * 1024 || elapsed - lastReportAt >= TimeSpan.FromMilliseconds(500))
                {
                    var speed = elapsed.TotalSeconds > 0 ? chunkBytes / elapsed.TotalSeconds : 0;
                    var remaining = totalBytes > transferred && speed > 0
                        ? (long?)Math.Ceiling((totalBytes - transferred) / speed) : null;
                    progress?.Report(new ServerSnapshotProgress("transferring", transferred, totalBytes, speed, remaining));
                    chunkBytes = 0;
                    lastReportAt = elapsed;
                }
            }
            await process.WaitForExitAsync(timeout.Token);
            var error = await stderrTask;
            if (process.ExitCode != 0) throw SshCommandRunner.CreateSshFailure(error, process.ExitCode);
            if (transferred != totalBytes)
                throw new SnapshotTransferInterruptedException($"服务器快照传输提前结束，已收到 {transferred} / {totalBytes} 字节。");
            var finalSpeed = timer.Elapsed.TotalSeconds > 0 ? Math.Max(0, transferred - offset) / timer.Elapsed.TotalSeconds : 0;
            progress?.Report(new ServerSnapshotProgress("transferring", transferred, totalBytes, finalSpeed, 0));
            return new RemoteSnapshotTransferResult(transferred, "");
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("服务器快照传输超过 12 小时，操作已中止。");
        }
        finally
        {
            if (!process.HasExited)
                try { process.Kill(entireProcessTree: true); } catch { }
        }
    }

    public async Task RemoveServerSnapshotAsync(
        ConnectionSettings settings,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            SshCommandRunner.ValidateSettings(settings);
            ValidateRemoteSnapshotPath(remotePath);
            var start = CreateSnapshotStart(settings, redirectOutput: true);
            start.ArgumentList.Add("sudo -n rm -f -- '" + remotePath + "'");
            using var process = StartProcess(start);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdoutTask;
            _ = await stderrTask;
        }
        catch { }
    }

    private static void ValidateRemoteSnapshotPath(string remotePath)
    {
        if (!Regex.IsMatch(remotePath, @"^/tmp/\.openclaw-debugger-[0-9a-f]{32}\.tar\.gz$"))
            throw new InvalidDataException("服务器快照临时路径无效。");
    }

    private static ProcessStartInfo CreateSnapshotStart(ConnectionSettings settings, bool redirectOutput)
    {
        var start = new ProcessStartInfo
        {
            FileName = "ssh.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        SshCommandRunner.AddSshArguments(start, settings);
        return start;
    }

    private static Process StartProcess(ProcessStartInfo start)
    {
        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动 Windows OpenSSH。");
            return process;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            process.Dispose();
            throw new InvalidOperationException("找不到或无法启动 ssh.exe，请确认 Windows OpenSSH Client 已安装。", ex);
        }
    }


    private static async Task<int> ReadSnapshotOutputAsync(Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await source.ReadAsync(buffer, cancellationToken);
        }
        catch (IOException ex)
        {
            throw new SnapshotTransferInterruptedException("SSH 数据流在传输服务器快照时中断。", ex);
        }
    }


}

public sealed record RemoteSnapshotTransferResult(long Bytes, string Sha256);
