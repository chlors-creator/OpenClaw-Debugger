using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClawDebugger;

/// <summary>通过一次性 SSH 管道上传图片，避免把整个文件复制到 JSON 请求内存。</summary>
internal sealed class RemoteStickerStreamUploader
{
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
    if size < 1 or size > 30 * 1024 * 1024:
        fail("图片为空或超过 30 MiB 限制", "too_large")
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

    public async Task<RemoteStickerUploadResult> UploadAsync(
        ConnectionSettings settings,
        string fileName,
        Stream source,
        long size,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        SshCommandRunner.ValidateSettings(settings);
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (size is < 1 or > 30 * 1024 * 1024) throw new InvalidDataException("图片为空或超过 30 MiB 限制。");
        StickerUploadService.ValidateImage(source, fileName);
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
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动 Windows OpenSSH。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"无法启动 Windows OpenSSH：{start.FileName}\n{ex.Message}", ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await SshCommandRunner.WritePythonBootstrapAsync(process.StandardInput.BaseStream, UploadStreamProgram, timeout.Token);
            var header = JsonSerializer.Serialize(new { stickers = settings.StickersPath, filename = fileName, size }) + "\n";
            var headerBytes = Encoding.UTF8.GetBytes(header);
            await process.StandardInput.BaseStream.WriteAsync(headerBytes, timeout.Token);
            await process.StandardInput.BaseStream.FlushAsync(timeout.Token);
            if (source.CanSeek) source.Position = 0;
            var buffer = new byte[256 * 1024];
            long sent = 0;
            while (sent < size)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - sent)), timeout.Token);
                if (read == 0) throw new EndOfStreamException("本地上传流在文件完成前结束。");
                await process.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                sent += read;
                progress?.Report(sent);
            }
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

}
