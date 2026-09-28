using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenClawDebugger;

public sealed record RemoteStickerUploadResult(string RelativePath, long Size, string Sha256);
public sealed record RemoteStickerRenameResult(string RelativePath, long Size, string Sha256);
public sealed record RemoteStickerPairWriteResult(string CatalogSha256, long CatalogSize, string ManifestSha256, long ManifestSize);
public sealed partial class RemoteOpenClawClient : IDisposable
{
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private Process? _sessionProcess;
    private string? _sessionKey;

    private const string UploadStreamProgram = """
import base64, hashlib, json, os, stat, sys, tempfile
from pathlib import PurePosixPath

def fail(message, code="remote_error"):
    print(json.dumps({"ok": False, "code": code, "error": message}, ensure_ascii=False), flush=True)
    raise SystemExit(0)

try:
    request = json.loads(sys.stdin.buffer.readline().decode("utf-8"))
    stickers = os.path.realpath(request["stickers"])
    filename = request["filename"]
    size = int(request["size"])
    if not os.path.isdir(stickers) or not isinstance(filename, str) or not filename or filename in (".", "..") or "/" in filename or "\\" in filename or any(ord(c) < 32 for c in filename):
        fail("上传文件名或表情包目录无效", "bad_upload")
    if len(filename) > 180 or PurePosixPath(filename).suffix.lower() not in {".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"}:
        fail("仅允许 PNG、JPG、GIF、WEBP、BMP 图片，文件名最长 180 个字符", "bad_upload")
    if size < 1 or size > 16 * 1024 * 1024:
        fail("图片为空或超过 16 MiB 限制", "too_large")
    target = os.path.join(stickers, filename)
    if os.path.commonpath([stickers, os.path.realpath(os.path.dirname(target))]) != stickers or os.path.lexists(target):
        fail("服务器已存在同名表情包，请先重命名本地文件", "conflict")
    fd, temporary = tempfile.mkstemp(prefix=".openclaw-upload-", dir=stickers)
    digest = hashlib.sha256()
    remaining = size
    try:
        with os.fdopen(fd, "wb") as output:
            while remaining:
                chunk = sys.stdin.buffer.read(min(256 * 1024, remaining))
                if not chunk:
                    fail("上传连接在文件完成前中断", "incomplete_upload")
                output.write(chunk)
                digest.update(chunk)
                remaining -= len(chunk)
            output.flush()
            os.fsync(output.fileno())
        os.chmod(temporary, 0o644)
        try:
            os.link(temporary, target)
        except FileExistsError:
            fail("服务器已存在同名表情包，请先重命名本地文件", "conflict")
        os.unlink(temporary)
        temporary = None
        try:
            dfd = os.open(stickers, os.O_DIRECTORY)
            try: os.fsync(dfd)
            finally: os.close(dfd)
        except Exception:
            pass
        print(json.dumps({"ok": True, "relativePath": filename, "size": size, "sha256": digest.hexdigest()}, separators=(",", ":")), flush=True)
    finally:
        if temporary and os.path.exists(temporary):
            try: os.unlink(temporary)
            except Exception: pass
except SystemExit:
    raise
except Exception as error:
    fail(type(error).__name__ + ": " + str(error))
""";

    // The SSH session sends requests to RemoteAgentProgram.Main.


    public async Task<IReadOnlyList<RemoteFile>> ConnectAndListAsync(
        ConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        var response = await InvokeAsync(settings, new JsonObject { ["action"] = "inventory" }, cancellationToken);
        var files = new List<RemoteFile>();
        foreach (var node in response["files"]?.AsArray() ?? new JsonArray())
        {
            if (node is not JsonObject item) continue;
            files.Add(new RemoteFile
            {
                Root = item["root"]?.GetValue<string>() ?? "",
                RelativePath = item["relativePath"]?.GetValue<string>() ?? "",
                Kind = item["kind"]?.GetValue<string>() ?? "text",
                Size = item["size"]?.GetValue<long>() ?? 0,
                Sha256 = item["sha256"]?.GetValue<string>() ?? "",
                ModifiedUtc = FromUnix(item["modifiedUnix"]?.GetValue<double>()),
                Editable = item["editable"]?.GetValue<bool>() ?? false
            });
        }
        return files;
    }

    public async Task<RemoteFileContent> ReadAsync(
        ConnectionSettings settings, RemoteFile file, CancellationToken cancellationToken = default)
    {
        var request = new JsonObject
        {
            ["action"] = "read",
            ["root"] = file.Root,
            ["path"] = file.RelativePath
        };
        var response = await InvokeAsync(settings, request, cancellationToken);
        var isBinary = response["binary"]?.GetValue<bool>() ?? false;
        var encoded = response["content"]?.GetValue<string>() ?? "";
        var raw = Convert.FromBase64String(encoded);
        var text = isBinary ? null : new UTF8Encoding(false, true).GetString(raw).TrimStart('\uFEFF');
        return new RemoteFileContent(
            file.Root,
            file.RelativePath,
            response["sha256"]?.GetValue<string>() ?? "",
            response["size"]?.GetValue<long>() ?? 0,
            FromUnix(response["modifiedUnix"]?.GetValue<double>()) ?? DateTimeOffset.UtcNow,
            text,
            isBinary ? raw : null,
            raw);
    }

    public Task<string> WriteAsync(
        ConnectionSettings settings,
        RemoteFile file,
        string text,
        string expectedSha256,
        CancellationToken cancellationToken = default) =>
        WriteBytesAsync(settings, file, Encoding.UTF8.GetBytes(text), expectedSha256, cancellationToken);

    public async Task<string> WriteBytesAsync(
        ConnectionSettings settings,
        RemoteFile file,
        byte[] bytes,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        var request = new JsonObject
        {
            ["action"] = "write",
            ["root"] = file.Root,
            ["path"] = file.RelativePath,
            ["expectedSha256"] = expectedSha256,
            ["content"] = Convert.ToBase64String(bytes)
        };
        var response = await InvokeAsync(settings, request, cancellationToken);
        return response["sha256"]?.GetValue<string>() ?? "";
    }
    public async Task<RemoteStickerPairWriteResult> WriteStickerPairAsync(
        ConnectionSettings settings,
        string catalogText,
        string expectedCatalogSha256,
        string manifestText,
        string expectedManifestSha256,
        CancellationToken cancellationToken = default)
    {
        var request = new JsonObject
        {
            ["action"] = "write_pair",
            ["root"] = "stickers",
            ["catalogExpectedSha256"] = expectedCatalogSha256,
            ["manifestExpectedSha256"] = expectedManifestSha256,
            ["catalogContent"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(catalogText)),
            ["manifestContent"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(manifestText))
        };
        var response = await InvokeAsync(settings, request, cancellationToken);
        return new RemoteStickerPairWriteResult(
            response["catalogSha256"]?.GetValue<string>() ?? "",
            response["catalogSize"]?.GetValue<long>() ?? Encoding.UTF8.GetByteCount(catalogText),
            response["manifestSha256"]?.GetValue<string>() ?? "",
            response["manifestSize"]?.GetValue<long>() ?? Encoding.UTF8.GetByteCount(manifestText));
    }
    public async Task<RemoteStickerUploadResult> UploadStickerAsync(
        ConnectionSettings settings, string fileName, byte[] bytes, CancellationToken cancellationToken = default)
    {
        var request = new JsonObject
        {
            ["action"] = "upload",
            ["root"] = "stickers",
            ["filename"] = fileName,
            ["content"] = Convert.ToBase64String(bytes)
        };
        var response = await InvokeAsync(settings, request, cancellationToken);
        return new RemoteStickerUploadResult(
            response["relativePath"]?.GetValue<string>() ?? fileName,
            response["size"]?.GetValue<long>() ?? bytes.LongLength,
            response["sha256"]?.GetValue<string>() ?? "");
    }

    public async Task<RemoteStickerUploadResult> UploadStickerAsync(
        ConnectionSettings settings,
        string fileName,
        Stream source,
        long size,
        CancellationToken cancellationToken = default)
    {
        SshCommandRunner.ValidateSettings(settings);
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (size is < 1 or > 16 * 1024 * 1024) throw new InvalidDataException("图片为空或超过 16 MiB 限制。");
        var start = new ProcessStartInfo
        {
            FileName = "ssh.exe",
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
        var program64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(UploadStreamProgram));
        start.ArgumentList.Add("python3 -u -c \"import base64;exec(base64.b64decode('" + program64 + "'))\"");
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动 Windows OpenSSH。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("找不到或无法启动 ssh.exe，请确认 Windows OpenSSH Client 已安装。", ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            var header = JsonSerializer.Serialize(new { stickers = settings.StickersPath, filename = fileName, size }) + "\n";
            var headerBytes = Encoding.UTF8.GetBytes(header);
            await process.StandardInput.BaseStream.WriteAsync(headerBytes, timeout.Token);
            await process.StandardInput.BaseStream.FlushAsync(timeout.Token);
            if (source.CanSeek) source.Position = 0;
            await source.CopyToAsync(process.StandardInput.BaseStream, 256 * 1024, timeout.Token);
            await process.StandardInput.BaseStream.FlushAsync(timeout.Token);
            process.StandardInput.Close();
            var outputLine = await process.StandardOutput.ReadLineAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var error = await errorTask;
            if (process.ExitCode != 0)
                throw SshCommandRunner.CreateSshFailure(error, process.ExitCode);
            if (string.IsNullOrWhiteSpace(outputLine)) throw new InvalidOperationException("服务器没有返回上传结果。");
            var response = JsonNode.Parse(outputLine) as JsonObject
                ?? throw new InvalidOperationException("服务器返回的上传结果无法识别。");
            if (response["ok"]?.GetValue<bool>() != true)
            {
                var code = response["code"]?.GetValue<string>() ?? "";
                var message = response["error"]?.GetValue<string>() ?? "远程上传失败。";
                if (code == "conflict") throw new RemoteConflictException(message);
                throw new InvalidOperationException(message);
            }
            return new RemoteStickerUploadResult(
                response["relativePath"]?.GetValue<string>() ?? fileName,
                response["size"]?.GetValue<long>() ?? size,
                response["sha256"]?.GetValue<string>() ?? "");
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("图片上传超过 30 分钟，操作已中止。");
        }
        catch (IOException ex)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new SnapshotTransferInterruptedException("SSH 连接在上传图片时中断。", ex);
        }
        finally
        {
            if (!process.HasExited)
                try { process.Kill(entireProcessTree: true); } catch { }
        }
    }
    public async Task<RemoteStickerRenameResult> RenameStickerAsync(
        ConnectionSettings settings, string oldFileName, string newFileName, string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        var request = new JsonObject
        {
            ["action"] = "rename",
            ["root"] = "stickers",
            ["oldFilename"] = oldFileName,
            ["newFilename"] = newFileName,
            ["expectedSha256"] = expectedSha256
        };
        var response = await InvokeAsync(settings, request, cancellationToken);
        return new RemoteStickerRenameResult(
            response["relativePath"]?.GetValue<string>() ?? newFileName,
            response["size"]?.GetValue<long>() ?? 0,
            response["sha256"]?.GetValue<string>() ?? expectedSha256);
    }
    private async Task<JsonObject> InvokeAsync(
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
            FileName = "ssh.exe",
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
        var program64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(RemoteAgentProgram.Main));
        start.ArgumentList.Add("python3 -u -c \"import base64;exec(base64.b64decode('" + program64 + "'))\"");
        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动 Windows OpenSSH。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            process.Dispose();
            throw new InvalidOperationException("找不到或无法启动 ssh.exe，请确认 Windows OpenSSH Client 已安装。", ex);
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

    private static DateTimeOffset? FromUnix(double? seconds)
    {
        if (seconds is null) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds.Value * 1000)); }
        catch { return null; }
    }
}

public sealed class RemoteConflictException(string message) : InvalidOperationException(message);
