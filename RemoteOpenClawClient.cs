using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenClawDebugger;

public sealed record RemoteStickerUploadResult(string RelativePath, long Size, string Sha256);
public sealed record RemoteStickerRenameResult(string RelativePath, long Size, string Sha256);

public sealed class RemoteOpenClawClient
{
    private const string PythonProgram = """
import base64, hashlib, json, os, stat, sys, tempfile, time
from pathlib import PurePosixPath

P = json.loads(base64.b64decode("__PAYLOAD__").decode("utf-8"))
ROOTS = {
    "workspace": os.path.realpath(P["workspace"]),
    "stickers": os.path.realpath(P["stickers"]),
}
ROOT_DOCS = {"MEMORY.md", "USER.md", "AGENTS.md", "SOUL.md", "DREAMS.md"}
IMAGE_EXTS = {".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"}
MAX_TEXT = 4 * 1024 * 1024
MAX_IMAGE = 16 * 1024 * 1024

def fail(message, code="remote_error"):
    print(json.dumps({"ok": False, "code": code, "error": message}, ensure_ascii=False))
    raise SystemExit(0)
def safe_file(root_name, relative, write=False):
    if root_name not in ROOTS or not isinstance(relative, str):
        raise ValueError("无效的远程路径")
    rel = PurePosixPath(relative)
    if rel.is_absolute() or not rel.parts or any(part in ("", ".", "..") for part in rel.parts):
        raise ValueError("拒绝访问路径范围之外的文件")
    normalized = rel.as_posix()
    if root_name == "workspace":
        allowed = normalized in ROOT_DOCS or (
            normalized.startswith("memory/") and normalized.lower().endswith(".md")
        )
    else:
        allowed = normalized in ("catalog.json", "MANIFEST.md") or (
            len(rel.parts) == 1 and rel.suffix.lower() in IMAGE_EXTS
        )
    if not allowed:
        raise ValueError("此文件不在软件允许管理的范围内")
    if write:
        writable = (
            root_name == "workspace" and normalized.lower().endswith(".md")
        ) or (
            root_name == "stickers" and normalized in ("catalog.json", "MANIFEST.md")
        )
        if not writable:
            raise ValueError("此文件只允许查看")
    root = ROOTS[root_name]
    candidate = os.path.join(root, *rel.parts)
    parent = os.path.realpath(os.path.dirname(candidate))
    if os.path.commonpath([root, parent]) != root:
        raise ValueError("拒绝访问路径范围之外的文件")
    if os.path.lexists(candidate) and os.path.islink(candidate):
        raise ValueError("不允许通过符号链接访问文件")
    resolved = os.path.realpath(candidate)
    if os.path.commonpath([root, resolved]) != root:
        raise ValueError("拒绝访问路径范围之外的文件")
    return candidate

def sha(data):
    return hashlib.sha256(data).hexdigest()

def metadata(root_name, relative, kind):
    path = safe_file(root_name, relative)
    try:
        st = os.stat(path, follow_symlinks=False)
    except FileNotFoundError:
        return None
    if not stat.S_ISREG(st.st_mode):
        return None
    size = st.st_size
    digest = ""

    return {
        "root": root_name, "relativePath": relative, "kind": kind,
        "size": size, "sha256": digest,
        "modifiedUnix": st.st_mtime, "editable": (
            (root_name == "workspace" and relative.lower().endswith(".md")) or
            (root_name == "stickers" and relative in ("catalog.json", "MANIFEST.md"))
        )
    }

def inventory():
    files = []
    workspace = ROOTS["workspace"]
    for name in sorted(ROOT_DOCS):
        item = metadata("workspace", name, "text")
        if item: files.append(item)
    memdir = os.path.join(workspace, "memory")
    if os.path.isdir(memdir) and not os.path.islink(memdir):
        for current, dirs, names in os.walk(memdir, followlinks=False):
            dirs[:] = [d for d in dirs if not os.path.islink(os.path.join(current, d))]
            for name in sorted(names):
                if name.lower().endswith(".md"):
                    full = os.path.join(current, name)
                    rel = os.path.relpath(full, workspace).replace(os.sep, "/")
                    try:
                        item = metadata("workspace", rel, "text")
                        if item: files.append(item)
                    except Exception:
                        pass
                    if len(files) >= 3000:
                        break
    sticker_root = ROOTS["stickers"]
    for name in ("catalog.json", "MANIFEST.md"):
        item = metadata("stickers", name, "text")
        if item: files.append(item)
    if os.path.isdir(sticker_root) and not os.path.islink(sticker_root):
        for name in sorted(os.listdir(sticker_root)):
            path = os.path.join(sticker_root, name)
            if os.path.isfile(path) and not os.path.islink(path) and os.path.splitext(name)[1].lower() in IMAGE_EXTS:
                item = metadata("stickers", name, "image")
                if item: files.append(item)
    print(json.dumps({"ok": True, "files": files}, ensure_ascii=False, separators=(",", ":")))

def read_file():
    root_name, relative = P["root"], P["path"]
    path = safe_file(root_name, relative)
    with open(path, "rb") as f:
        data = f.read(MAX_IMAGE + 1 if os.path.splitext(path)[1].lower() in IMAGE_EXTS else MAX_TEXT + 1)
    image = os.path.splitext(path)[1].lower() in IMAGE_EXTS
    limit = MAX_IMAGE if image else MAX_TEXT
    if len(data) > limit:
        fail("文件超过单文件读取限制", "too_large")
    st = os.stat(path, follow_symlinks=False)
    result = {
        "ok": True, "root": root_name, "relativePath": relative,
        "sha256": sha(data), "size": len(data), "modifiedUnix": st.st_mtime,
        "binary": image,
        "content": base64.b64encode(data).decode("ascii")
    }
    print(json.dumps(result, ensure_ascii=False, separators=(",", ":")))

def write_file():
    root_name, relative = P["root"], P["path"]
    path = safe_file(root_name, relative, write=True)
    if not os.path.isfile(path):
        fail("文件不存在；首版不会创建新文件", "missing")
    with open(path, "rb") as f:
        old_data = f.read(MAX_TEXT + 1)
    if len(old_data) > MAX_TEXT:
        fail("文件超过单文件编辑限制", "too_large")
    actual = sha(old_data)
    if actual != P.get("expectedSha256", ""):
        fail("服务器上的文件已被修改；请重新加载并比较差异后再保存", "conflict")
    try:
        new_data = base64.b64decode(P["content"], validate=True)
        new_data.decode("utf-8")
    except Exception:
        fail("新内容不是有效的 UTF-8 文本", "invalid_text")
    if len(new_data) > MAX_TEXT:
        fail("文件超过单文件编辑限制", "too_large")
    mode = stat.S_IMODE(os.stat(path, follow_symlinks=False).st_mode)
    fd, temporary = tempfile.mkstemp(prefix=".openclaw-manager-", dir=os.path.dirname(path))
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(new_data)
            f.flush()
            os.fsync(f.fileno())
        os.chmod(temporary, mode)
        if sha(open(path, "rb").read(MAX_TEXT + 1)) != actual:
            fail("保存前检测到文件再次变化，已取消覆盖", "conflict")
        os.replace(temporary, path)
        try:
            dfd = os.open(os.path.dirname(path), os.O_DIRECTORY)
            try: os.fsync(dfd)
            finally: os.close(dfd)
        except Exception:
            pass
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    print(json.dumps({"ok": True, "sha256": sha(new_data), "size": len(new_data)}, separators=(",", ":")))

def upload_image():
    root_name = P.get("root")
    filename = P.get("filename")
    if root_name != "stickers" or not isinstance(filename, str):
        fail("只允许上传到表情包目录", "bad_upload")
    if not filename or filename in (".", "..") or "/" in filename or "\\" in filename or any(ord(c) < 32 for c in filename):
        fail("文件名必须是单层文件名", "bad_upload")
    if len(filename) > 180 or PurePosixPath(filename).suffix.lower() not in IMAGE_EXTS:
        fail("仅允许 PNG、JPG、GIF、WEBP、BMP 图片，文件名最长 180 个字符", "bad_upload")
    path = safe_file("stickers", filename)
    if os.path.lexists(path):
        fail("服务器已存在同名表情包，请先重命名本地文件", "conflict")
    try:
        data = base64.b64decode(P["content"], validate=True)
    except Exception:
        fail("上传内容不是有效的 Base64 图片", "bad_upload")
    if not data or len(data) > MAX_IMAGE:
        fail("图片为空或超过 16 MiB 限制", "too_large")
    fd, temporary = tempfile.mkstemp(prefix=".openclaw-upload-", dir=ROOTS["stickers"])
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(data)
            f.flush()
            os.fsync(f.fileno())
        os.chmod(temporary, 0o644)
        try:
            os.link(temporary, path)
        except FileExistsError:
            fail("服务器已存在同名表情包，请先重命名本地文件", "conflict")
        os.unlink(temporary)
        try:
            dfd = os.open(ROOTS["stickers"], os.O_DIRECTORY)
            try: os.fsync(dfd)
            finally: os.close(dfd)
        except Exception:
            pass
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    print(json.dumps({"ok": True, "relativePath": filename, "size": len(data), "sha256": sha(data)}, separators=(",", ":")))
def rename_image():
    old_name = P.get("oldFilename")
    new_name = P.get("newFilename")
    expected = P.get("expectedSha256")
    def valid_name(name):
        return isinstance(name, str) and bool(name) and name not in (".", "..") and len(name) <= 180 and "/" not in name and "\\" not in name and not name.endswith((".", " ")) and not any(ord(c) < 32 for c in name) and PurePosixPath(name).suffix.lower() in IMAGE_EXTS
    if P.get("root") != "stickers" or not valid_name(old_name) or not valid_name(new_name):
        fail("Invalid sticker filename", "bad_rename")
    if PurePosixPath(old_name).suffix.lower() != PurePosixPath(new_name).suffix.lower():
        fail("Changing an image extension is not supported", "bad_rename")
    source = safe_file("stickers", old_name)
    target = safe_file("stickers", new_name)
    if not os.path.isfile(source) or os.path.islink(source):
        fail("Source image does not exist", "missing")
    if os.path.lexists(target):
        fail("A sticker with that filename already exists", "conflict")
    with open(source, "rb") as f:
        source_data = f.read(MAX_IMAGE + 1)
    if not source_data or len(source_data) > MAX_IMAGE:
        fail("Image exceeds the 16 MiB management limit", "too_large")
    source_hash = sha(source_data)
    if not expected or source_hash != expected:
        fail("Image changed since it was loaded; rescan before renaming", "conflict")
    try:
        os.link(source, target)
    except FileExistsError:
        fail("A sticker with that filename already exists", "conflict")
    try:
        os.unlink(source)
    except Exception:
        try:
            with open(target, "rb") as f: target_hash = sha(f.read(MAX_IMAGE + 1))
            if target_hash == source_hash: os.unlink(target)
        except Exception:
            pass
        raise
    try:
        dfd = os.open(ROOTS["stickers"], os.O_DIRECTORY)
        try: os.fsync(dfd)
        finally: os.close(dfd)
    except Exception:
        pass
    print(json.dumps({"ok": True, "relativePath": new_name, "size": len(source_data), "sha256": source_hash}, separators=(",", ":")))
try:
    for root in ROOTS.values():
        if not os.path.isabs(root) or not os.path.isdir(root):
            fail("配置的远程目录不存在或不是目录", "bad_root")
    action = P.get("action")
    if action == "inventory":
        inventory()
    elif action == "read":
        read_file()
    elif action == "write":
        write_file()
    elif action == "upload":
        upload_image()
    elif action == "rename":
        rename_image()
    else:
        fail("不支持的操作")
except SystemExit:
    raise
except Exception as e:
    fail(type(e).__name__ + ": " + str(e))
""";

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
    public async Task<RemoteSnapshotTransferResult> WriteServerSnapshotAsync(
        ConnectionSettings settings,
        Stream destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSettings(settings);
        var start = new ProcessStartInfo
        {
            FileName = "ssh.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add("-T");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("BatchMode=yes");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("StrictHostKeyChecking=yes");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("ConnectTimeout=12");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("LogLevel=ERROR");
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add(settings.Port.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(settings.Target);
        start.ArgumentList.Add("sudo -n tar --create --gzip --file=- --numeric-owner --acls --xattrs --xattrs-include='*' --sparse --exclude=./proc --exclude=./sys --exclude=./dev --exclude=./run -C / .");

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("无法启动 Windows OpenSSH。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("找不到或无法启动 ssh.exe，请确认 Windows OpenSSH Client 已安装。", ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(12));
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[256 * 1024];
        long transferred = 0;
        long lastReported = 0;
        try
        {
            var gzipHeader = new byte[2];
            var headerRead = 0;
            while (headerRead < gzipHeader.Length)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(
                    gzipHeader.AsMemory(headerRead), timeout.Token);
                if (read == 0)
                    throw new InvalidDataException("服务器没有返回完整的 gzip 快照头。");
                headerRead += read;
            }
            if (gzipHeader[0] != 0x1f || gzipHeader[1] != 0x8b)
                throw new InvalidDataException("SSH 标准输出不是 gzip 快照数据；请检查服务器登录脚本是否向标准输出写入文本。");
            await destination.WriteAsync(gzipHeader, timeout.Token);
            hash.AppendData(gzipHeader);
            transferred = gzipHeader.Length;

            while (true)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(), timeout.Token);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                hash.AppendData(buffer, 0, read);
                transferred += read;
                if (transferred - lastReported >= 4 * 1024 * 1024)
                {
                    progress?.Report(transferred);
                    lastReported = transferred;
                }
            }

            await process.WaitForExitAsync(timeout.Token);
            var error = await stderrTask;
            if (process.ExitCode != 0)
            {
                var detail = string.Join(Environment.NewLine,
                    error.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Take(8));
                if (detail.Contains("Permission denied (", StringComparison.OrdinalIgnoreCase) ||
                    detail.Contains("Permission denied, please try again", StringComparison.OrdinalIgnoreCase) ||
                    detail.Contains("No supported authentication methods", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("当前 Windows OpenSSH 身份未通过服务器认证。请确认此 Windows 用户执行 ssh admin@106.14.173.90 能直接登录。");
                if (detail.Contains("a password is required", StringComparison.OrdinalIgnoreCase) ||
                    detail.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("完整服务器快照需要 admin 对 tar 命令具备免密 sudo 权限；当前连接不能交互输入 sudo 密码。");
                if (detail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("完整服务器快照需要 admin 对 tar 命令具备 root 权限。");
                if (detail.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("服务器 SSH 主机指纹发生变化，快照已取消。");
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                    ? $"服务器快照失败，SSH 退出码 {process.ExitCode}。"
                    : $"服务器快照失败：{detail}");
            }

            if (transferred == 0)
                throw new InvalidDataException("服务器没有返回快照数据。");
            progress?.Report(transferred);
            return new RemoteSnapshotTransferResult(
                transferred, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("服务器快照超过 12 小时，传输已中止。");
        }
        finally
        {
            if (!process.HasExited)
                try { process.Kill(entireProcessTree: true); } catch { }
        }
    }
    private static async Task<JsonObject> InvokeAsync(
        ConnectionSettings settings, JsonObject request, CancellationToken cancellationToken)
    {
        ValidateSettings(settings);
        request["workspace"] = settings.WorkspacePath;
        request["stickers"] = settings.StickersPath;
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.ToJsonString()));
        var program = PythonProgram.Replace("__PAYLOAD__", payload, StringComparison.Ordinal);
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
        start.ArgumentList.Add("-T");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("BatchMode=yes");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("StrictHostKeyChecking=yes");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("ConnectTimeout=12");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("LogLevel=ERROR");
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add(settings.Port.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(settings.Target);
        start.ArgumentList.Add("python3");
        start.ArgumentList.Add("-");

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("无法启动 Windows OpenSSH。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("找不到或无法启动 ssh.exe，请确认 Windows OpenSSH Client 已安装。", ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardInput.WriteAsync(program.AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("SSH 操作超时。请检查网络、SSH 密钥代理和服务器状态。");
        }

        var output = await stdoutTask;
        var error = await stderrTask;
        if (process.ExitCode != 0)
        {
            var detail = string.Join(Environment.NewLine, error.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Take(5));
            if (detail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("当前 Windows OpenSSH 身份未通过服务器认证。请先确认此 Windows 用户执行 ssh admin@106.14.173.90 能直接登录；应用不会要求输入或保存密钥。");
            if (detail.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("服务器 SSH 主机指纹与 known_hosts 不符。请先核实新指纹，再更新本机 SSH 配置。");
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"SSH 操作失败，退出码 {process.ExitCode}。"
                : $"SSH 操作失败：{detail}");
        }

        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        if (line is null) throw new InvalidOperationException("服务器没有返回有效结果。");
        JsonObject? response;
        try { response = JsonNode.Parse(line) as JsonObject; }
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

    private static void ValidateSettings(ConnectionSettings settings)
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

    private static DateTimeOffset? FromUnix(double? seconds)
    {
        if (seconds is null) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds.Value * 1000)); }
        catch { return null; }
    }
}

public sealed class RemoteConflictException(string message) : InvalidOperationException(message);
public sealed record RemoteSnapshotTransferResult(long Bytes, string Sha256);
