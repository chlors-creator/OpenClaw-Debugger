namespace OpenClawDebugger;

/// <summary>服务器端受限远程代理程序。单独维护，便于审查和版本化远程协议。</summary>
internal static class RemoteAgentProgram
{
    public const string Main = """
import base64, hashlib, json, os, re, stat, subprocess, sys, tempfile, time
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

def model_row(value, selected=False):
    ref = model_ref(value)
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

def models_inventory_core():
    status = openclaw_json(["models", "status", "--json"])
    catalog = openclaw_json(["models", "list", "--all", "--json"])
    primary_ref = nested_model_value(status)
    fallback_refs = configured_fallbacks(status)
    catalog_values = catalog.get("models") or catalog.get("items") or catalog.get("data") or []
    if isinstance(catalog_values, dict): catalog_values = list(catalog_values.values())
    available = []
    by_id = {}
    for value in catalog_values if isinstance(catalog_values, list) else []:
        row = model_row(value)
        if row and row["id"] not in by_id:
            by_id[row["id"]] = row
            available.append(row)
    for ref in [primary_ref] + fallback_refs:
        if ref and ref not in by_id:
            row = model_row(ref)
            if row:
                by_id[ref] = row
                available.append(row)
    primary = by_id.get(primary_ref) if primary_ref else None
    fallbacks = [by_id[ref] for ref in fallback_refs if ref in by_id and ref != primary_ref]
    selected = {row["id"] for row in fallbacks}
    if primary: selected.add(primary["id"])
    available = [row for row in available if row["id"] not in selected]
    return {"ok": True, "primary": primary, "fallbacks": fallbacks, "available": available, "retrievedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}

def models_inventory():
    print(json.dumps(models_inventory_core(), ensure_ascii=False, separators=(",", ":")))

def apply_model_order(primary, fallbacks):
    subprocess.run(["openclaw", "models", "set", primary], check=True, capture_output=True, text=True, timeout=90)
    subprocess.run(["openclaw", "models", "fallbacks", "clear"], check=True, capture_output=True, text=True, timeout=90)
    for ref in fallbacks:
        subprocess.run(["openclaw", "models", "fallbacks", "add", ref], check=True, capture_output=True, text=True, timeout=90)

def models_set_order():
    primary = P.get("primary", "")
    fallbacks = P.get("fallbacks", [])
    if not valid_model_ref(primary) or not isinstance(fallbacks, list) or len(fallbacks) > 20:
        fail("模型顺序参数无效", "bad_model_order")
    fallbacks = [item.strip() if isinstance(item, str) else "" for item in fallbacks]
    if any(not valid_model_ref(item) for item in fallbacks) or len(set(fallbacks)) != len(fallbacks) or primary in fallbacks:
        fail("模型顺序包含无效或重复项目", "bad_model_order")
    before = models_inventory_core()
    old_primary = before.get("primary", {}).get("id") if before.get("primary") else ""
    old_fallbacks = [item.get("id") for item in before.get("fallbacks", [])]
    try:
        apply_model_order(primary, fallbacks)
    except Exception as error:
        if valid_model_ref(old_primary):
            try: apply_model_order(old_primary, [item for item in old_fallbacks if item != old_primary])
            except Exception: pass
        fail("模型顺序保存失败，已尝试恢复旧顺序：" + redact_cli_error(error), "model_order_failed")
    models_inventory()

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
    models_inventory()

def models_test_latency():
    models = P.get("models", [])
    if not isinstance(models, list) or not 1 <= len(models) <= 100:
        fail("测试模型列表无效", "bad_model_test")
    models = [item.strip() if isinstance(item, str) else "" for item in models]
    if any(not valid_model_ref(item) for item in models): fail("测试模型引用无效", "bad_model_test")
    tested = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    results = []
    for ref in models:
        started = time.perf_counter()
        try:
            completed = subprocess.run(
                ["openclaw", "infer", "model", "run", "--local", "--model", ref, "--prompt", "Reply with exactly: openclaw-debugger-latency", "--json"],
                capture_output=True, text=True, timeout=15, env=os.environ.copy())
            if completed.returncode == 0:
                results.append({"modelId": ref, "success": True, "latencyMs": max(1, int((time.perf_counter() - started) * 1000)), "error": None, "testedAtUtc": tested})
            else:
                results.append({"modelId": ref, "success": False, "latencyMs": None, "error": redact_cli_error(completed.stderr or completed.stdout or "模型探测失败"), "testedAtUtc": tested})
        except subprocess.TimeoutExpired:
            results.append({"modelId": ref, "success": False, "latencyMs": None, "error": "模型探测超时（超过 15 秒）", "testedAtUtc": tested})
        except FileNotFoundError:
            fail("服务器找不到 openclaw 命令，请确认 OpenClaw 已安装", "openclaw_missing")
        except Exception as error:
            results.append({"modelId": ref, "success": False, "latencyMs": None, "error": redact_cli_error(error), "testedAtUtc": tested})
    print(json.dumps({"ok": True, "testedAtUtc": tested, "results": results}, ensure_ascii=False, separators=(",", ":")))

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

for line in sys.stdin:
    line = line.strip()
    if line:
        handle(line)
""";
}
