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

    private const string PythonProgram = """
import base64, hashlib, json, os, stat, sys, tempfile, time
from pathlib import PurePosixPath

P = {}
ROOTS = {}
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

def write_pair():
    if P.get("root") != "stickers":
        fail("只允许事务写入表情包标签文件", "bad_write")
    catalog_path = safe_file("stickers", "catalog.json", write=True)
    manifest_path = safe_file("stickers", "MANIFEST.md", write=True)
    if not os.path.isfile(catalog_path) or not os.path.isfile(manifest_path):
        fail("标签文件不存在；请先重新扫描服务器", "missing")
    try:
        catalog_data = base64.b64decode(P["catalogContent"], validate=True)
        manifest_data = base64.b64decode(P["manifestContent"], validate=True)
        catalog_data.decode("utf-8")
        manifest_data.decode("utf-8")
    except Exception:
        fail("标签文件内容不是有效的 UTF-8 文本", "invalid_text")
    if len(catalog_data) > MAX_TEXT or len(manifest_data) > MAX_TEXT:
        fail("标签文件超过单文件编辑限制", "too_large")

    lock_file = os.path.join(ROOTS["stickers"], ".openclaw-labels.lock")
    lock_handle = None
    temporary = []
    replaced_catalog = False
    replaced_manifest = False
    old_catalog = None
    old_manifest = None
    try:
        try:
            import fcntl
            lock_handle = open(lock_file, "a+b")
            fcntl.flock(lock_handle.fileno(), fcntl.LOCK_EX)
        except Exception:
            if lock_handle is not None:
                lock_handle.close()
                lock_handle = None
        with open(catalog_path, "rb") as f: old_catalog = f.read(MAX_TEXT + 1)
        with open(manifest_path, "rb") as f: old_manifest = f.read(MAX_TEXT + 1)
        if len(old_catalog) > MAX_TEXT or len(old_manifest) > MAX_TEXT:
            fail("标签文件超过单文件编辑限制", "too_large")
        if sha(old_catalog) != P.get("catalogExpectedSha256", "") or sha(old_manifest) != P.get("manifestExpectedSha256", ""):
            fail("服务器上的标签文件已被修改；请重新加载并比较差异后再保存", "conflict")
        modes = [stat.S_IMODE(os.stat(catalog_path, follow_symlinks=False).st_mode), stat.S_IMODE(os.stat(manifest_path, follow_symlinks=False).st_mode)]
        for data, mode in ((catalog_data, modes[0]), (manifest_data, modes[1])):
            fd, temporary_path = tempfile.mkstemp(prefix=".openclaw-labels-", dir=ROOTS["stickers"])
            temporary.append(temporary_path)
            with os.fdopen(fd, "wb") as f:
                f.write(data)
                f.flush()
                os.fsync(f.fileno())
            os.chmod(temporary_path, mode)
        os.replace(temporary[0], catalog_path)
        replaced_catalog = True
        os.replace(temporary[1], manifest_path)
        replaced_manifest = True
        try:
            dfd = os.open(ROOTS["stickers"], os.O_DIRECTORY)
            try: os.fsync(dfd)
            finally: os.close(dfd)
        except Exception:
            pass
    except SystemExit:
        raise
    except Exception as error:
        if replaced_catalog and not replaced_manifest and old_catalog is not None:
            try:
                fd, restore_path = tempfile.mkstemp(prefix=".openclaw-labels-rollback-", dir=ROOTS["stickers"])
                with os.fdopen(fd, "wb") as f:
                    f.write(old_catalog)
                    f.flush()
                    os.fsync(f.fileno())
                os.replace(restore_path, catalog_path)
            except Exception:
                pass
        fail("两个标签文件事务写入失败：" + str(error), "write_pair_failed")
    finally:
        for path in temporary:
            if os.path.exists(path):
                try: os.unlink(path)
                except Exception: pass
        if lock_handle is not None:
            try:
                import fcntl
                fcntl.flock(lock_handle.fileno(), fcntl.LOCK_UN)
            except Exception:
                pass
            lock_handle.close()
    print(json.dumps({"ok": True, "catalogSha256": sha(catalog_data), "catalogSize": len(catalog_data), "manifestSha256": sha(manifest_data), "manifestSize": len(manifest_data)}, separators=(",", ":")))

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
def handle(encoded):
    global P, ROOTS
    try:
        P = json.loads(base64.b64decode(encoded).decode("utf-8"))
        ROOTS = {
            "workspace": os.path.realpath(P["workspace"]),
            "stickers": os.path.realpath(P["stickers"]),
        }
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
        elif action == "write_pair":
            write_pair()
        elif action == "upload":
            upload_image()
        elif action == "rename":
            rename_image()
        else:
            fail("不支持的操作")
    except SystemExit:
        return
    except Exception as e:
        fail(type(e).__name__ + ": " + str(e))

for line in sys.stdin:
    line = line.strip()
    if line:
        handle(line)
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
        ValidateSettings(settings);
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
        AddSshArguments(start, settings);
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
                throw CreateSshFailure(error, process.ExitCode);
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
    private static void AddSshArguments(ProcessStartInfo start, ConnectionSettings settings)
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

    private static Exception CreateSshFailure(string error, int exitCode)
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

    private static bool IsTransientSshFailure(string detail)
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


    private async Task<JsonObject> InvokeAsync(
        ConnectionSettings settings, JsonObject request, CancellationToken cancellationToken)
    {
        ValidateSettings(settings);
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
        AddSshArguments(start, settings);
        var program64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(PythonProgram));
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
