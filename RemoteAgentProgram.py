import base64, contextlib, hashlib, hmac, io, json, os, re, stat, subprocess, sys, tempfile, time, socket, struct, select, threading
import urllib.error, urllib.parse, urllib.request
from pathlib import PurePosixPath
from concurrent.futures import FIRST_COMPLETED, ThreadPoolExecutor, wait
try:
    import sqlite3
except ImportError:
    sqlite3 = None

P = {}
ROOTS = {}
PROTOCOL_VERSION = 3
SCRIPT_HASH = "sha256:1cbb9c112d1c6458fea37ff9a54f91a18cc06de77c18bf972d7443f4f83efdb9"
MODEL_CACHE = None
MODEL_CACHE_AT = 0.0
MODEL_CACHE_TTL = 30.0
MODEL_CACHE_SCOPE = None
MODEL_CACHE_SOURCE_HASH = None
MODEL_GATEWAY = None
MODEL_PROVIDER_CACHE = None
MODEL_PROVIDER_CACHE_AT = 0.0
MODEL_PROVIDER_CACHE_TTL = 60.0
MODEL_AUTH_CACHE = None
MODEL_AUTH_CACHE_AT = 0.0
MODEL_AUTH_CACHE_TTL = 60.0
REPLAY_CACHE = {}
REPLAY_CACHE_TTL = 60.0
REPLAY_CACHE_MAX = 32
CLI_PROBE_LIMITER = threading.BoundedSemaphore(3)
ROOT_DOCS = {"MEMORY.md", "USER.md", "AGENTS.md", "SOUL.md", "DREAMS.md"}
IMAGE_EXTS = {".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"}
MAX_TEXT = 4 * 1024 * 1024
MAX_IMAGE = 30 * 1024 * 1024

def openclaw_config():
    candidates = []
    configured = os.environ.get("OPENCLAW_CONFIG_PATH", "").strip()
    if configured: candidates.append(os.path.expanduser(configured))
    candidates.extend([
        os.path.expanduser("~/.openclaw/openclaw.json"),
        os.path.expanduser("~/.openclaw/config.json"),
    ])
    for candidate in candidates:
        try:
            with open(candidate, "r", encoding="utf-8") as stream:
                value = json.load(stream)
            if isinstance(value, dict): return value
        except (OSError, ValueError):
            pass
    return {}

def gateway_settings():
    config = openclaw_config()
    gateway = config.get("gateway") if isinstance(config.get("gateway"), dict) else {}
    auth = gateway.get("auth") if isinstance(gateway.get("auth"), dict) else {}
    try:
        port = int(os.environ.get("OPENCLAW_GATEWAY_PORT", "") or gateway.get("port") or 18789)
    except (TypeError, ValueError):
        port = 18789
    mode = str(auth.get("mode") or "token").lower()
    if mode == "password":
        secret = os.environ.get("OPENCLAW_GATEWAY_PASSWORD", "") or str(auth.get("password") or "")
        return port, "password", secret
    secret = os.environ.get("OPENCLAW_GATEWAY_TOKEN", "") or str(auth.get("token") or "")
    return port, "token", secret

class ModelGatewayQueryProcess:
    # 常驻 Gateway 查询会话；模型读取不再为每次请求启动 OpenClaw CLI。
    def __init__(self):
        self.sock = None
        self.lock = threading.Lock()
        self.sequence = 0

    def close(self):
        sock, self.sock = self.sock, None
        if sock is not None:
            try: sock.close()
            except OSError: pass

    def _read_exact(self, size):
        chunks = []
        remaining = size
        while remaining:
            chunk = self.sock.recv(remaining)
            if not chunk: raise ConnectionError("OpenClaw Gateway 连接已关闭")
            chunks.append(chunk)
            remaining -= len(chunk)
        return b"".join(chunks)

    def _send_frame(self, opcode, payload):
        payload = payload if isinstance(payload, bytes) else bytes(payload)
        mask = os.urandom(4)
        masked = bytes(value ^ mask[index % 4] for index, value in enumerate(payload))
        length = len(masked)
        header = bytearray([0x80 | opcode])
        if length < 126:
            header.append(0x80 | length)
        elif length < 65536:
            header.append(0x80 | 126)
            header.extend(struct.pack("!H", length))
        else:
            header.append(0x80 | 127)
            header.extend(struct.pack("!Q", length))
        self.sock.sendall(bytes(header) + mask + masked)

    def _receive_frame(self):
        first, second = self._read_exact(2)
        opcode = first & 0x0F
        masked = bool(second & 0x80)
        length = second & 0x7F
        if length == 126: length = struct.unpack("!H", self._read_exact(2))[0]
        elif length == 127: length = struct.unpack("!Q", self._read_exact(8))[0]
        if length > 32 * 1024 * 1024: raise ValueError("Gateway 响应超过允许大小")
        mask = self._read_exact(4) if masked else None
        data = self._read_exact(length)
        if mask is not None:
            data = bytes(value ^ mask[index % 4] for index, value in enumerate(data))
        if opcode == 0x8: raise ConnectionError("OpenClaw Gateway 已关闭查询连接")
        if opcode == 0x9:
            self._send_frame(0xA, data)
            return None
        if opcode != 0x1: return None
        return data.decode("utf-8")

    def _send_json(self, value):
        self._send_frame(0x1, json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8"))

    def _connect(self):
        port, auth_kind, secret = gateway_settings()
        sock = socket.create_connection(("127.0.0.1", port), timeout=10)
        self.sock = sock
        key = base64.b64encode(os.urandom(16)).decode("ascii")
        request = (
            "GET / HTTP/1.1\r\nHost: 127.0.0.1:%d\r\nUpgrade: websocket\r\n"
            "Connection: Upgrade\r\nSec-WebSocket-Key: %s\r\nSec-WebSocket-Version: 13\r\n\r\n"
        ) % (port, key)
        sock.sendall(request.encode("ascii"))
        response = b""
        while b"\r\n\r\n" not in response:
            chunk = sock.recv(4096)
            if not chunk: raise ConnectionError("Gateway WebSocket 握手失败")
            response += chunk
            if len(response) > 65536: raise ConnectionError("Gateway WebSocket 握手响应过大")
        if not response.startswith(b"HTTP/1.1 101"):
            raise ConnectionError("OpenClaw Gateway 未接受 WebSocket 连接")
        sock.settimeout(1.0)
        try:
            if select.select([sock], [], [], 0.8)[0]: self._receive_frame()
        except socket.timeout:
            pass
        sock.settimeout(10.0)
        self.sequence += 1
        params = {
            "minProtocol": 3,
            "maxProtocol": 4,
            "client": {"id": "openclaw-debugger", "displayName": "OpenClaw Debugger", "version": "1.0", "platform": "linux", "mode": "operator"},
            "role": "operator",
            "scopes": ["operator.read", "operator.write"],
            "caps": [],
        }
        if secret: params["auth"] = {auth_kind: secret}
        request_id = "debugger-connect-" + str(self.sequence)
        self._send_json({"type": "req", "id": request_id, "method": "connect", "params": params})
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            message = self._receive_frame()
            if not message: continue
            value = json.loads(message)
            if value.get("type") == "res" and value.get("id") == request_id:
                if value.get("ok") is not True:
                    raise ConnectionError("Gateway 连接认证失败")
                return
        raise TimeoutError("OpenClaw Gateway 握手超时")

    def request(self, method, params=None):
        with self.lock:
            if self.sock is None: self._connect()
            self.sequence += 1
            request_id = "debugger-model-" + str(self.sequence)
            self._send_json({"type": "req", "id": request_id, "method": method, "params": params or {}})
            deadline = time.monotonic() + 30
            try:
                while time.monotonic() < deadline:
                    message = self._receive_frame()
                    if not message: continue
                    value = json.loads(message)
                    if value.get("type") != "res" or value.get("id") != request_id: continue
                    if value.get("ok") is not True:
                        error = value.get("error")
                        raise RuntimeError("Gateway 模型查询失败" + ((": " + str(error)) if error else ""))
                    return value.get("payload") or {}
                raise TimeoutError("Gateway 模型查询超时")
            except Exception:
                self.close()
                raise

def gateway_model_list():
    global MODEL_GATEWAY
    if MODEL_GATEWAY is None: MODEL_GATEWAY = ModelGatewayQueryProcess()
    try:
        # 普通模型选择器只读取已配置/允许的模型，不能使用 view=all。
        return MODEL_GATEWAY.request("models.list", {"view": "configured"})
    except Exception:
        if MODEL_GATEWAY is not None: MODEL_GATEWAY.close()
        raise

def emit_response(value, binary=None):
    if binary is None:
        print(json.dumps(value, ensure_ascii=False, separators=(",", ":")), flush=True)
        return
    header = dict(value)
    header["binaryFrame"] = True
    header["binaryLength"] = len(binary)
    sys.stdout.write(json.dumps(header, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()
    sys.stdout.buffer.write(binary)
    sys.stdout.buffer.write(b"\n")
    sys.stdout.buffer.flush()

def binary_request():
    value = P.get("_binaryPayload")
    return value if isinstance(value, (bytes, bytearray, memoryview)) else None

def fail(message, code="remote_error"):
    # 错误帧使用 ASCII JSON，避免远端 locale 不是 UTF-8 时破坏协议行。
    print(json.dumps({"ok": False, "code": code, "error": message}, ensure_ascii=True, separators=(",", ":")), flush=True)
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
    image = os.path.splitext(path)[1].lower() in IMAGE_EXTS
    limit = MAX_IMAGE if image else MAX_TEXT
    with open(path, "rb") as f:
        data = read_limited(f, limit)
    if len(data) > limit:
        fail("文件超过单文件读取限制", "too_large")
    st = os.stat(path, follow_symlinks=False)
    result = {
        "ok": True, "root": root_name, "relativePath": relative,
        "sha256": sha(data), "size": len(data), "modifiedUnix": st.st_mtime,
        "binary": image
    }
    if P.get("binaryResponse") is True:
        emit_response(result, data)
    else:
        result["content"] = base64.b64encode(data).decode("ascii")
        emit_response(result)

def read_limited(stream, limit):
    """Read with a fixed-size buffer and stop after the configured limit."""
    chunks = []
    total = 0
    while total <= limit:
        chunk = stream.read(min(256 * 1024, limit + 1 - total))
        if not chunk:
            break
        chunks.append(chunk)
        total += len(chunk)
        if total > limit:
            break
    return b"".join(chunks)

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
        new_data = binary_request()
        if new_data is None:
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
        payload = binary_request()
        if payload is not None:
            catalog_length = int(P.get("catalogLength", -1))
            manifest_length = int(P.get("manifestLength", -1))
            if catalog_length < 0 or manifest_length < 0 or catalog_length + manifest_length != len(payload):
                fail("标签文件二进制帧长度无效", "invalid_binary")
            catalog_data = payload[:catalog_length]
            manifest_data = payload[catalog_length:]
        else:
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
        if replaced_manifest and old_manifest is not None:
            try:
                fd, restore_path = tempfile.mkstemp(prefix=".openclaw-labels-rollback-", dir=ROOTS["stickers"])
                with os.fdopen(fd, "wb") as f:
                    f.write(old_manifest)
                    f.flush()
                    os.fsync(f.fileno())
                os.replace(restore_path, manifest_path)
            except Exception:
                pass
        if replaced_catalog and old_catalog is not None:
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
        data = binary_request()
        if data is None:
            data = base64.b64decode(P["content"], validate=True)
    except Exception:
        fail("上传内容不是有效的 Base64 图片", "bad_upload")
    if not data or len(data) > MAX_IMAGE:
        fail("图片为空或超过 30 MiB 限制", "too_large")
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
        fail("Image exceeds the 30 MiB management limit", "too_large")
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

def redact_cli_error(value):
    text = str(value or "")
    text = re.sub(r"(?i)(api[_-]?key|token|secret|password|authorization)\s*[:=]\s*[^\s,;]+", r"\1=<redacted>", text)
    text = re.sub(r"(?i)(/home/[^\s]+|/root/[^\s]+|[A-Za-z]:\\[^\s]+)", "<path>", text)
    return text[-1200:] if len(text) > 1200 else text

def openclaw_json(args, input_text=None, timeout=90):
    command = ["openclaw"] + list(args)
    try:
        completed = subprocess.run(
            command,
            input=input_text,
            text=True,
            capture_output=True,
            timeout=timeout,
            env=os.environ.copy()
        )
    except FileNotFoundError:
        fail("服务器找不到 openclaw 命令，请确认 OpenClaw 已安装", "openclaw_missing")
    except subprocess.TimeoutExpired:
        fail("OpenClaw 模型操作超时", "openclaw_timeout")
    stdout = (completed.stdout or "").strip()
    stderr = redact_cli_error(completed.stderr or "")
    if completed.returncode != 0:
        fail("OpenClaw 命令失败" + ((": " + stderr) if stderr else ""), "openclaw_command")
    candidates = [stdout]
    candidates.extend(line.strip() for line in reversed(stdout.splitlines()) if line.strip())
    for candidate in candidates:
        try:
            value = json.loads(candidate)
            if isinstance(value, dict):
                return value
        except Exception:
            pass
    fail("OpenClaw 没有返回可识别的 JSON", "openclaw_invalid_json")

def openclaw_command(args, input_text=None, timeout=90):
    try:
        completed = subprocess.run(["openclaw"] + list(args), input=input_text, text=True, capture_output=True, timeout=timeout, env=os.environ.copy())
    except FileNotFoundError:
        fail("服务器找不到 openclaw 命令，请确认 OpenClaw 已安装", "openclaw_missing")
    except subprocess.TimeoutExpired:
        fail("OpenClaw 模型操作超时", "openclaw_timeout")
    if completed.returncode != 0:
        detail = redact_cli_error(completed.stderr or completed.stdout or "")
        fail("OpenClaw 命令失败" + ((": " + detail) if detail else ""), "openclaw_command")
    return completed.stdout or ""

def model_ref(value):
    if isinstance(value, str):
        return value.strip()
    if isinstance(value, dict):
        for key in ("id", "model", "ref", "key", "name", "primary", "resolved"):
            candidate = value.get(key)
            if isinstance(candidate, str) and candidate.strip():
                return candidate.strip()
    return ""

def valid_model_ref(value):
    return isinstance(value, str) and 3 <= len(value) <= 240 and "/" in value and not any(ch.isspace() or ord(ch) < 32 for ch in value)

def model_provider(value):
    return value.split("/", 1)[0] if "/" in value else ""

def model_key(value):
    return value.strip().casefold() if isinstance(value, str) else ""

def model_row(value, selected=False):
    ref = model_ref(value)
    if isinstance(value, dict) and isinstance(ref, str) and "/" not in ref:
        provider_hint = value.get("provider")
        if isinstance(provider_hint, str) and provider_hint.strip():
            ref = provider_hint.strip() + "/" + ref
    if not valid_model_ref(ref):
        return None
    if isinstance(value, dict):
        name = str(value.get("name") or value.get("displayName") or ref)
        provider = str(value.get("provider") or model_provider(ref))
        status = str(value.get("status") or value.get("authStatus") or "unknown")
        alias = value.get("alias") if isinstance(value.get("alias"), str) else None
    else:
        name, provider, status, alias = ref, model_provider(ref), "unknown", None
    return {"id": ref, "name": name, "provider": provider, "status": status, "alias": alias}

def nested_model_value(status):
    if not isinstance(status, dict):
        return None
    for key in ("resolvedDefault", "primary", "defaultModel", "model"):
        value = status.get(key)
        ref = model_ref(value)
        if valid_model_ref(ref):
            return ref
    config = status.get("config")
    if isinstance(config, dict):
        agents = config.get("agents")
        defaults = agents.get("defaults") if isinstance(agents, dict) else None
        model = defaults.get("model") if isinstance(defaults, dict) else None
        if isinstance(model, dict):
            ref = model_ref(model.get("primary"))
        else:
            ref = model_ref(model)
        if valid_model_ref(ref):
            return ref
    return None

def configured_fallbacks(status):
    if not isinstance(status, dict):
        return []
    holders = [status.get("fallbacks"), status.get("model")]
    config = status.get("config")
    if isinstance(config, dict):
        agents = config.get("agents")
        defaults = agents.get("defaults") if isinstance(agents, dict) else None
        holders.append(defaults.get("model") if isinstance(defaults, dict) else None)
    for holder in holders:
        if isinstance(holder, dict): holder = holder.get("fallbacks")
        if isinstance(holder, list):
            values = [model_ref(item) for item in holder]
            return [value for value in values if valid_model_ref(value)]
    return []

def model_config_source_hash(config=None):
    if config is None:
        config = openclaw_config()
    try:
        encoded = json.dumps(config if isinstance(config, dict) else {}, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    except (TypeError, ValueError):
        encoded = b"{}"
    return hashlib.sha256(encoded).hexdigest()

def model_config_hash(primary, fallbacks, available):
    payload = {
        "primary": primary,
        "fallbacks": fallbacks,
        # 可用目录的展示顺序由不同 Gateway/CLI 版本决定，哈希只关心模型内容。
        "available": sorted(available or [], key=lambda row: str(row.get("id", "")).casefold()) if isinstance(available, list) else available,
    }
    encoded = json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return "sha256:" + hashlib.sha256(encoded).hexdigest()

def models_inventory_core(force=False):
    global MODEL_CACHE, MODEL_CACHE_AT, MODEL_CACHE_SOURCE_HASH
    source_hash = model_config_source_hash()
    if not force and MODEL_CACHE is not None and MODEL_CACHE_SOURCE_HASH == source_hash and (time.monotonic() - MODEL_CACHE_AT) < MODEL_CACHE_TTL:
        return MODEL_CACHE
    # 优先调用服务器常驻 Gateway 的 configured models.list RPC。Gateway 已经加载了
    # OpenClaw 运行时和模型配置，避免把完整发布目录误显示为可用模型，
    # 也避免每次查询重新启动 OpenClaw CLI。
    # Gateway 不可用时才回退到 CLI，保证旧版/未启动 Gateway 的服务器仍能使用。
    try:
        catalog = gateway_model_list()
        status = {"config": openclaw_config()}
    except Exception:
        status = openclaw_json(["models", "status", "--json"])
        catalog = openclaw_json(["models", "list", "--json"])
    primary_ref = nested_model_value(status)
    fallback_refs = configured_fallbacks(status)
    catalog_values = catalog.get("models") or catalog.get("items") or catalog.get("data") or []
    if isinstance(catalog_values, dict): catalog_values = list(catalog_values.values())
    available = []
    by_id = {}
    for value in catalog_values if isinstance(catalog_values, list) else []:
        row = model_row(value)
        if row:
            key = model_key(row["id"])
            if key and key not in by_id:
                by_id[key] = row
                available.append(row)
    for ref in [primary_ref] + fallback_refs:
        key = model_key(ref)
        if ref and key not in by_id:
            row = model_row(ref)
            if row:
                by_id[key] = row
                available.append(row)
    if not primary_ref:
        for value in catalog_values if isinstance(catalog_values, list) else []:
            tags = value.get("tags") if isinstance(value, dict) else None
            if isinstance(tags, list) and any(str(tag).lower() in ("default", "primary") for tag in tags):
                primary_ref = model_ref(value)
                break
    primary = by_id.get(model_key(primary_ref)) if primary_ref else None
    primary_key = model_key(primary_ref)
    fallbacks = [by_id[model_key(ref)] for ref in fallback_refs if model_key(ref) in by_id and model_key(ref) != primary_key]
    selected = {model_key(row["id"]) for row in fallbacks}
    if primary: selected.add(model_key(primary["id"]))
    available = [row for row in available if model_key(row["id"]) not in selected]
    snapshot = {"ok": True, "primary": primary, "fallbacks": fallbacks, "available": available, "retrievedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    snapshot["configHash"] = model_config_hash(primary, fallbacks, available)
    MODEL_CACHE = snapshot
    MODEL_CACHE_AT = time.monotonic()
    MODEL_CACHE_SOURCE_HASH = source_hash
    return snapshot

def models_inventory():
    snapshot = models_inventory_core()
    known = P.get("knownConfigHash") if isinstance(P.get("knownConfigHash"), str) else ""
    if known and snapshot.get("configHash") and hmac.compare_digest(known, snapshot["configHash"]):
        print(json.dumps({
            "ok": True,
            "unchanged": True,
            "configHash": snapshot["configHash"],
            "retrievedAtUtc": snapshot.get("retrievedAtUtc"),
        }, ensure_ascii=False, separators=(",", ":")))
        return
    print(json.dumps(snapshot, ensure_ascii=False, separators=(",", ":")))

def snapshot_with_order(base, primary_ref, fallback_refs):
    rows = []
    for row in [base.get("primary")] + list(base.get("fallbacks") or []) + list(base.get("available") or []):
        if isinstance(row, dict):
            rows.append(row)
    by_id = {}
    for row in rows:
        key = model_key(row.get("id", ""))
        if key and key not in by_id:
            by_id[key] = row
    primary = by_id.get(model_key(primary_ref)) or model_row(primary_ref)
    if primary:
        by_id.setdefault(model_key(primary.get("id", "")), primary)
    fallbacks = []
    fallback_keys = set()
    for ref in fallback_refs:
        key = model_key(ref)
        if not key or key == model_key(primary_ref) or key in fallback_keys:
            continue
        row = by_id.get(key) or model_row(ref)
        if row:
            by_id.setdefault(key, row)
            fallbacks.append(row)
            fallback_keys.add(key)
    selected = {model_key(row.get("id", "")) for row in fallbacks}
    if primary:
        selected.add(model_key(primary.get("id", "")))
    available = []
    available_keys = set()
    for row in rows:
        key = model_key(row.get("id", ""))
        if key and key not in selected and key not in available_keys:
            available.append(row)
            available_keys.add(key)
    snapshot = {"ok": True, "primary": primary, "fallbacks": fallbacks, "available": available, "retrievedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    snapshot["configHash"] = model_config_hash(primary, fallbacks, available)
    return snapshot
def apply_model_order(primary, fallbacks):
    subprocess.run(["openclaw", "models", "set", primary], check=True, capture_output=True, text=True, timeout=90)
    subprocess.run(["openclaw", "models", "fallbacks", "clear"], check=True, capture_output=True, text=True, timeout=90)
    for ref in fallbacks:
        subprocess.run(["openclaw", "models", "fallbacks", "add", ref], check=True, capture_output=True, text=True, timeout=90)

def models_set_order():
    global MODEL_CACHE, MODEL_CACHE_AT, MODEL_CACHE_SOURCE_HASH
    primary = P.get("primary", "")
    fallbacks = P.get("fallbacks", [])
    if not valid_model_ref(primary) or not isinstance(fallbacks, list) or len(fallbacks) > 20:
        fail("模型顺序参数无效", "bad_model_order")
    fallbacks = [item.strip() if isinstance(item, str) else "" for item in fallbacks]
    if any(not valid_model_ref(item) for item in fallbacks) or len(set(fallbacks)) != len(fallbacks) or primary in fallbacks:
        fail("模型顺序包含无效或重复项目", "bad_model_order")
    before = MODEL_CACHE or models_inventory_core()
    old_primary = before.get("primary", {}).get("id") if before.get("primary") else ""
    old_fallbacks = [item.get("id") for item in before.get("fallbacks", [])]
    try:
        append_only = (
            model_key(primary) == model_key(old_primary)
            and len(fallbacks) == len(old_fallbacks) + 1
            and all(model_key(left) == model_key(right) for left, right in zip(fallbacks[:-1], old_fallbacks))
        )
        if append_only:
            subprocess.run(["openclaw", "models", "fallbacks", "add", fallbacks[-1]], check=True, capture_output=True, text=True, timeout=30)
        else:
            apply_model_order(primary, fallbacks)
    except Exception as error:
        if valid_model_ref(old_primary):
            try: apply_model_order(old_primary, [item for item in old_fallbacks if item != old_primary])
            except Exception: pass
        fail("模型顺序保存失败，已尝试恢复旧顺序：" + redact_cli_error(error), "model_order_failed")
    MODEL_CACHE = snapshot_with_order(before, primary, fallbacks)
    MODEL_CACHE_AT = time.monotonic()
    MODEL_CACHE_SOURCE_HASH = model_config_source_hash()
    print(json.dumps(MODEL_CACHE, ensure_ascii=False, separators=(",", ":")))

def models_add():
    ref = P.get("modelRef", "").strip() if isinstance(P.get("modelRef"), str) else ""
    alias = P.get("alias", "").strip() if isinstance(P.get("alias"), str) else ""
    display = P.get("displayName", "").strip() if isinstance(P.get("displayName"), str) else ""
    base_url = P.get("baseUrl", "").strip() if isinstance(P.get("baseUrl"), str) else ""
    api_key = P.get("apiKey", "") if isinstance(P.get("apiKey"), str) else ""
    if not valid_model_ref(ref): fail("模型引用需要使用 provider/model 格式", "bad_model")
    provider, model_id = ref.split("/", 1)
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,63}", provider): fail("模型提供商名称无效", "bad_model")
    if alias and (len(alias) > 80 or any(ord(ch) < 32 for ch in alias)): fail("模型别名无效", "bad_model")
    if display and (len(display) > 180 or any(ord(ch) < 32 for ch in display)): fail("模型显示名无效", "bad_model")
    if base_url and not re.match(r"^https?://", base_url, re.I): fail("API 地址必须是 http 或 https URL", "bad_model")
    if api_key:
        openclaw_command(["models", "auth", "paste-api-key", "--provider", provider, "--profile-id", provider + ":manual"], api_key + "\n", 90)
    entry = {ref: ({"alias": alias} if alias else {})}
    openclaw_command(["config", "set", "agents.defaults.models", json.dumps(entry, ensure_ascii=False, separators=(",", ":")), "--strict-json", "--merge"], timeout=90)
    if base_url:
        provider_entry = {"baseUrl": base_url, "api": "openai-completions", "models": [{"id": model_id, "name": display or model_id}]}
        openclaw_command(["config", "set", "models.providers." + provider, json.dumps(provider_entry, ensure_ascii=False, separators=(",", ":")), "--strict-json", "--merge"], timeout=90)
    # 配置已改变，不能返回旧清单。
    print(json.dumps(models_inventory_core(force=True), ensure_ascii=False, separators=(",", ":")))

def auth_store_paths():
    state_dir = os.environ.get("OPENCLAW_STATE_DIR", "").strip() or "~/.openclaw"
    state_dir = os.path.abspath(os.path.expanduser(state_dir))
    config = openclaw_config()
    agents = config.get("agents") if isinstance(config.get("agents"), dict) else {}
    defaults = agents.get("defaults") if isinstance(agents.get("defaults"), dict) else {}
    system_agent = defaults.get("systemAgent") if isinstance(defaults.get("systemAgent"), dict) else {}
    agent_id = str(system_agent.get("agentId") or "main")
    paths = []
    configured_agent_dir = os.environ.get("OPENCLAW_AGENT_DIR", "").strip()
    if configured_agent_dir:
        paths.append(os.path.join(os.path.abspath(os.path.expanduser(configured_agent_dir)), "openclaw-agent.sqlite"))
    entries = agents.get("entries") if isinstance(agents.get("entries"), dict) else {}
    entry = entries.get(agent_id) if isinstance(entries.get(agent_id), dict) else {}
    entry_agent_dir = entry.get("agentDir") if isinstance(entry, dict) else None
    if isinstance(entry_agent_dir, str) and entry_agent_dir.strip():
        paths.append(os.path.join(os.path.abspath(os.path.expanduser(entry_agent_dir.strip())), "openclaw-agent.sqlite"))
    paths.append(os.path.join(state_dir, "agents", agent_id, "agent", "openclaw-agent.sqlite"))
    if agent_id != "main":
        paths.append(os.path.join(state_dir, "agents", "main", "agent", "openclaw-agent.sqlite"))
    paths.append(os.path.join(state_dir, "state", "openclaw.sqlite"))
    return list(dict.fromkeys(paths))

def load_auth_profiles():
    profiles = {}
    if sqlite3 is None: return profiles
    for path in auth_store_paths():
        if not os.path.isfile(path):
            continue
        connection = None
        try:
            connection = sqlite3.connect("file:" + path + "?mode=ro", uri=True, timeout=0.5)
            rows = connection.execute("select store_json from auth_profile_store").fetchall()
            for row in rows:
                try: value = json.loads(row[0]) if isinstance(row[0], str) else None
                except (TypeError, ValueError): value = None
                if not isinstance(value, dict):
                    continue
                candidates = value.get("profiles") if isinstance(value.get("profiles"), dict) else value
                if not isinstance(candidates, dict):
                    continue
                for profile_id, profile in candidates.items():
                    if isinstance(profile_id, str) and isinstance(profile, dict):
                        # Agent-local stores intentionally override shared profiles.
                        profiles.setdefault(profile_id, profile)
        except (OSError, sqlite3.Error):
            pass
        finally:
            if connection is not None:
                try: connection.close()
                except Exception: pass
    return profiles

def secret_ref_value(value):
    if isinstance(value, str) and value.strip(): return value.strip()
    if not isinstance(value, dict): return None
    if str(value.get("source") or "").lower() != "env": return None
    env_name = value.get("id")
    if not isinstance(env_name, str) or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", env_name): return None
    resolved = os.environ.get(env_name, "")
    if not resolved:
        config = openclaw_config()
        env = config.get("env") if isinstance(config.get("env"), dict) else {}
        values = env.get("vars") if isinstance(env.get("vars"), dict) else {}
        configured = values.get(env_name)
        if isinstance(configured, str): resolved = configured
    return resolved.strip() or None

def model_auth_key(provider):
    global MODEL_AUTH_CACHE, MODEL_AUTH_CACHE_AT
    now = time.monotonic()
    if MODEL_AUTH_CACHE is None or now - MODEL_AUTH_CACHE_AT >= MODEL_AUTH_CACHE_TTL:
        MODEL_AUTH_CACHE = load_auth_profiles()
        MODEL_AUTH_CACHE_AT = now
    config = openclaw_config()
    auth = config.get("auth") if isinstance(config.get("auth"), dict) else {}
    order = auth.get("order") if isinstance(auth.get("order"), dict) else {}
    preferred = order.get(provider) if isinstance(order.get(provider), list) else []
    profile_ids = [item for item in preferred if isinstance(item, str)]
    profile_ids.extend(item for item in MODEL_AUTH_CACHE if item not in profile_ids)
    for profile_id in profile_ids:
        profile = MODEL_AUTH_CACHE.get(profile_id)
        if not isinstance(profile, dict): continue
        profile_provider = str(profile.get("provider") or (profile_id.split(":", 1)[0] if ":" in profile_id else ""))
        if profile_provider != provider: continue
        key = secret_ref_value(profile.get("key"))
        if key is None: key = secret_ref_value(profile.get("apiKey"))
        if key is None: key = secret_ref_value(profile.get("token"))
        if key is None: key = secret_ref_value(profile.get("keyRef"))
        if key is None: key = secret_ref_value(profile.get("tokenRef"))
        if key is not None: return key
    return None

def model_provider_config(ref):
    # 返回可安全直接探测的 OpenAI-compatible 配置；密钥只留在远程进程内。
    global MODEL_PROVIDER_CACHE, MODEL_PROVIDER_CACHE_AT
    now = time.monotonic()
    if MODEL_PROVIDER_CACHE is None or now - MODEL_PROVIDER_CACHE_AT >= MODEL_PROVIDER_CACHE_TTL:
        config = openclaw_config()
        models_config = config.get("models") if isinstance(config, dict) else None
        providers = models_config.get("providers") if isinstance(models_config, dict) else None
        MODEL_PROVIDER_CACHE = providers if isinstance(providers, dict) else {}
        MODEL_PROVIDER_CACHE_AT = now
    if not isinstance(ref, str) or "/" not in ref:
        return None
    provider, model_id = ref.split("/", 1)
    value = MODEL_PROVIDER_CACHE.get(provider)
    if not isinstance(value, dict):
        return None
    api = str(value.get("api") or "").lower()
    base_url = value.get("baseUrl")
    api_key = secret_ref_value(value.get("apiKey")) or secret_ref_value(value.get("key")) or model_auth_key(provider)
    if api not in ("openai-completions", "openai-chat-completions"):
        return None
    if not isinstance(base_url, str) or not re.match(r"^https?://", base_url, re.I):
        return None
    if not isinstance(api_key, str) or not api_key.strip():
        return None
    return base_url.rstrip("/") + "/chat/completions", api_key.strip(), model_id

def read_probe_response(response, started, measurement):
    content_type = str(response.headers.get("content-type") or "").lower()
    try:
        if "text/event-stream" in content_type:
            while True:
                line = response.readline()
                if not line:
                    break
                text = line.decode("utf-8", "replace").strip()
                if not text.lower().startswith("data:"):
                    continue
                data = text[5:].strip()
                if data and data != "[DONE]":
                    return {"success": True, "latencyMs": max(1, int((time.perf_counter() - started) * 1000)), "error": None, "measurement": measurement}
            return {"success": False, "latencyMs": None, "error": "模型未返回首个流式事件", "measurement": measurement}
        response.read(64 * 1024)
        return {"success": True, "latencyMs": max(1, int((time.perf_counter() - started) * 1000)), "error": None, "measurement": measurement}
    finally:
        try: response.close()
        except Exception: pass

def gateway_model_probe(ref):
    # 官方模型密钥由 Gateway 的认证存储管理，不能从 models.providers 直接读取。
    # 通过本机 OpenAI-compatible Gateway 端点探测，避免每次启动 openclaw CLI。
    port, auth_kind, secret = gateway_settings()
    started = time.perf_counter()
    payload = json.dumps({
        "model": "openclaw/default",
        "messages": [{"role": "user", "content": "Reply with exactly: ok"}],
        "max_tokens": 1,
        "temperature": 0,
        "stream": True,
    }, separators=(",", ":")).encode("utf-8")
    headers = {
        "Accept": "text/event-stream",
        "Content-Type": "application/json",
        "User-Agent": "OpenClaw-Debugger/1.0",
        "x-openclaw-model": ref,
        "x-openclaw-agent-id": "main",
    }
    if secret:
        headers["Authorization"] = "Bearer " + secret
    request = urllib.request.Request(
        "http://127.0.0.1:%d/v1/chat/completions" % port,
        data=payload,
        method="POST",
        headers=headers,
    )
    try:
        response = urllib.request.urlopen(request, timeout=8)
        return read_probe_response(response, started, "gateway_first_event")
    except urllib.error.HTTPError as error:
        detail = ""
        try: detail = error.read(512).decode("utf-8", "replace")
        except Exception: pass
        # 未开启兼容端点时交给 direct/CLI 回退；认证和模型错误应直接展示。
        if error.code in (401, 403, 404, 405, 501): return None
        return {"success": False, "latencyMs": None, "error": redact_cli_error("Gateway HTTP " + str(error.code) + (": " + detail if detail else "")), "measurement": "gateway_first_event"}
    except (urllib.error.URLError, TimeoutError, OSError):
        # Gateway HTTP 兼容端点可能未启用，不把连接失败误报为模型失败。
        return None

def direct_model_probe(ref):
    # 以最小流式请求测首个 SSE 事件，避免启动一次完整 OpenClaw CLI。
    configured = model_provider_config(ref)
    if configured is None:
        return None
    url, api_key, model_id = configured
    started = time.perf_counter()
    payload = json.dumps({
        "model": model_id,
        "messages": [{"role": "user", "content": "Reply with exactly: ok"}],
        "max_tokens": 1,
        "temperature": 0,
        "stream": True,
    }, separators=(",", ":")).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=payload,
        method="POST",
        headers={
            "Accept": "text/event-stream",
            "Authorization": "Bearer " + api_key,
            "Content-Type": "application/json",
            "User-Agent": "OpenClaw-Debugger/1.0",
        },
    )
    try:
        response = urllib.request.urlopen(request, timeout=8)
        return read_probe_response(response, started, "first_event")
    except urllib.error.HTTPError as error:
        detail = ""
        try: detail = error.read(512).decode("utf-8", "replace")
        except Exception: pass
        return {"success": False, "latencyMs": None, "error": redact_cli_error("HTTP " + str(error.code) + (": " + detail if detail else "")), "measurement": "first_event"}
    except (urllib.error.URLError, TimeoutError, OSError) as error:
        return {"success": False, "latencyMs": None, "error": redact_cli_error(error), "measurement": "first_event"}

def models_test_latency():
    models = P.get("models", [])
    if not isinstance(models, list) or not 1 <= len(models) <= 100:
        fail("测试模型列表无效", "bad_model_test")
    models = [item.strip() if isinstance(item, str) else "" for item in models]
    if any(not valid_model_ref(item) for item in models): fail("测试模型引用无效", "bad_model_test")
    tested = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())

    def probe(ref):
        started = time.perf_counter()
        try:
            gateway = gateway_model_probe(ref)
            if gateway is not None:
                gateway["modelId"] = ref
                gateway["testedAtUtc"] = tested
                return gateway
            direct = direct_model_probe(ref)
            if direct is not None:
                direct["modelId"] = ref
                direct["testedAtUtc"] = tested
                return direct
            # Keep HTTP probes at up to ten workers, but limit expensive CLI
            # fallbacks to three processes on small servers.
            with CLI_PROBE_LIMITER:
                completed = subprocess.run(
                    ["openclaw", "infer", "model", "run", "--local", "--model", ref, "--prompt", "Reply with exactly: openclaw-debugger-latency", "--json"],
                    capture_output=True, text=True, timeout=15, env=os.environ.copy())
            if completed.returncode == 0:
                return {"modelId": ref, "success": True, "latencyMs": max(1, int((time.perf_counter() - started) * 1000)), "error": None, "measurement": "cli_full", "testedAtUtc": tested}
            return {"modelId": ref, "success": False, "latencyMs": None, "error": redact_cli_error(completed.stderr or completed.stdout or "模型探测失败"), "measurement": "cli_full", "testedAtUtc": tested}
        except subprocess.TimeoutExpired:
            # 超时模型在当前 future 完成后立即出队，不占用后续探测槽位。
            return {"modelId": ref, "success": False, "latencyMs": None, "error": "模型探测超时（超过 15 秒）", "measurement": "cli_full", "timedOut": True, "testedAtUtc": tested}
        except FileNotFoundError:
            return {"modelId": ref, "success": False, "latencyMs": None, "error": "服务器找不到 openclaw 命令，请确认 OpenClaw 已安装", "measurement": "cli_full", "fatalCode": "openclaw_missing", "testedAtUtc": tested}
        except Exception as error:
            return {"modelId": ref, "success": False, "latencyMs": None, "error": redact_cli_error(error), "measurement": "cli_full", "testedAtUtc": tested}

    # 保持最多 10 个探测同时运行；任意一个完成后立即从待测队列补充下一个。
    max_workers = min(10, len(models))
    pending = iter(models)
    active = {}
    results_by_id = {}
    fatal_code = None
    with ThreadPoolExecutor(max_workers=max_workers) as executor:
        for _ in range(max_workers):
            ref = next(pending, None)
            if ref is None: break
            active[executor.submit(probe, ref)] = ref
        while active:
            completed, _ = wait(active, return_when=FIRST_COMPLETED)
            for future in completed:
                ref = active.pop(future)
                try:
                    result = future.result()
                except Exception as error:
                    # 单个探测异常不能阻塞队列；将它视为失败并继续补充下一个模型。
                    result = {"modelId": ref, "success": False, "latencyMs": None, "error": redact_cli_error(error), "measurement": "cli_full", "testedAtUtc": tested}
                fatal_code = fatal_code or result.pop("fatalCode", None)
                results_by_id[ref] = result
                next_ref = next(pending, None)
                if next_ref is not None:
                    active[executor.submit(probe, next_ref)] = next_ref
    if fatal_code == "openclaw_missing":
        fail("服务器找不到 openclaw 命令，请确认 OpenClaw 已安装", "openclaw_missing")
    results = [results_by_id[ref] for ref in models]
    print(json.dumps({"ok": True, "testedAtUtc": tested, "results": results}, ensure_ascii=False, separators=(",", ":")))
def read_exact_stdin(size):
    if size < 0 or size > MAX_IMAGE + MAX_TEXT:
        raise ValueError("二进制请求长度超出限制")
    payload = bytearray(size)
    offset = 0
    remaining = size
    while remaining:
        chunk = sys.stdin.buffer.read(min(256 * 1024, remaining))
        if not chunk:
            raise EOFError("二进制请求在传输完成前中断")
        payload[offset:offset + len(chunk)] = chunk
        offset += len(chunk)
        remaining -= len(chunk)
    return payload

def handle(encoded, binary_payload=None):
    global P, ROOTS, MODEL_CACHE, MODEL_CACHE_AT, MODEL_CACHE_SCOPE, MODEL_CACHE_SOURCE_HASH
    try:
        P = json.loads(base64.b64decode(encoded).decode("utf-8"))
        if P.get("protocolVersion") != PROTOCOL_VERSION:
            fail("远程代理协议版本不匹配", "protocol_mismatch")
        # scriptHash remains accepted for protocol compatibility, but is not a
        # connection prerequisite. The desktop client can update the embedded
        # agent without synchronizing a generated digest.
        P["_binaryPayload"] = binary_payload
        ROOTS = {
            "workspace": os.path.realpath(P["workspace"]),
            "stickers": os.path.realpath(P["stickers"]),
        }
        scope = (ROOTS["workspace"], ROOTS["stickers"])
        if MODEL_CACHE_SCOPE != scope:
            MODEL_CACHE = None
            MODEL_CACHE_AT = 0.0
            MODEL_CACHE_SOURCE_HASH = None
            MODEL_CACHE_SCOPE = scope
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
        elif action == "models_inventory":
            models_inventory()
        elif action == "models_set_order":
            models_set_order()
        elif action == "models_add":
            models_add()
        elif action == "models_test_latency":
            models_test_latency()
        else:
            fail("不支持的操作")
    except SystemExit:
        return
    except Exception as e:
        fail(type(e).__name__ + ": " + str(e))

REPLAYABLE_ACTIONS = {"inventory", "models_inventory", "models_test_latency"}

def decode_request(encoded):
    try:
        value = json.loads(base64.b64decode(encoded).decode("utf-8"))
        return value if isinstance(value, dict) else {}
    except Exception:
        return {}

def cleanup_replay_cache(now):
    expired = [key for key, (created, _, _, _) in REPLAY_CACHE.items() if now - created >= REPLAY_CACHE_TTL]
    for key in expired:
        REPLAY_CACHE.pop(key, None)
    while len(REPLAY_CACHE) > REPLAY_CACHE_MAX:
        oldest = min(REPLAY_CACHE.items(), key=lambda item: item[1][0])[0]
        REPLAY_CACHE.pop(oldest, None)

def run_request(encoded, binary_payload=None):
    request = decode_request(encoded)
    request_id = request.get("requestId") if isinstance(request.get("requestId"), str) else ""
    action = request.get("action") if isinstance(request.get("action"), str) else ""
    # 只有 read 会在 JSON 行后附加原始二进制帧；带二进制请求体的 write/upload
    # 仍通过捕获后的一行 JSON 返回，避免 TextIOWrapper 留下未刷新的输出。
    if request.get("binaryResponse") is True and action == "read":
        handle(encoded, binary_payload)
        return
    now = time.monotonic()
    cleanup_replay_cache(now)
    fingerprint = hashlib.sha256(encoded.encode("ascii", "replace")).hexdigest()
    if request_id and action in REPLAYABLE_ACTIONS:
        cached = REPLAY_CACHE.get(request_id)
        if cached and now - cached[0] < REPLAY_CACHE_TTL:
            if cached[1] != action or cached[2] != fingerprint:
                fail("请求 ID 已经用于另一条请求，不能重复使用", "request_id_conflict")
            sys.stdout.write(cached[3])
            sys.stdout.flush()
            return
    output = io.StringIO()
    with contextlib.redirect_stdout(output):
        handle(encoded, binary_payload)
    text = output.getvalue()
    if request_id and action in REPLAYABLE_ACTIONS and text:
        REPLAY_CACHE[request_id] = (now, action, fingerprint, text)
        cleanup_replay_cache(now)
    sys.stdout.write(text)
    sys.stdout.flush()

def read_request_line():
    # 不使用 ``for line in stdin``：BufferedReader 的迭代器可能预读二进制正文，
    # 导致随后按长度读取时等待永远不会到达的字节。
    chunks = []
    while True:
        value = sys.stdin.buffer.read(1)
        if not value:
            return b"" if not chunks else b"".join(chunks)
        chunks.append(value)
        if value == b"\n":
            return b"".join(chunks)

while True:
    raw_line = read_request_line()
    if not raw_line:
        break
    encoded = raw_line.decode("ascii", "replace").strip()
    if not encoded:
        continue
    request = decode_request(encoded)
    length = request.get("binaryLength", 0)
    try:
        length = int(length or 0)
    except (TypeError, ValueError):
        length = -1
    binary_payload = read_exact_stdin(length) if length > 0 else None
    run_request(encoded, binary_payload)
