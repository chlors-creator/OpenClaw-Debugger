using Microsoft.Web.WebView2.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace OpenClawDebugger;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly RemoteOpenClawClient _remote = new();
    private readonly Dictionary<string, RemoteFileContent> _loadedMemory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StickerUploadSession> _stickerUploads = new(StringComparer.Ordinal);
    private const int StickerUploadLimit = 16 * 1024 * 1024;
    private const int StickerUploadChunkLimit = 192 * 1024;
    private UserSettings _settings = new();
    private LocalSnapshotStore? _snapshots;
    private IReadOnlyList<RemoteFile> _files = [];
    private RemoteFile? _catalogFile;
    private RemoteFile? _manifestFile;
    private RemoteFileContent? _catalogContent;
    private RemoteFileContent? _manifestContent;
    private ParsedStickerCatalog? _catalog;
    private bool _serverConnected;
    private bool _busy;
    private bool _hasDrafts;
    private bool _closingApproved;

    public MainWindow() => InitializeComponent();

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = await SettingsRepository.LoadAsync();
            _settings.PrivateDirectory = SettingsRepository.DefaultPrivateDirectory;
            if (_settings.ThemeName is not ("Atri" or "Luoxi" or "Light")) _settings.ThemeName = "Atri";
            _snapshots = new LocalSnapshotStore(_settings.PrivateDirectory);

            await MainWebView.EnsureCoreWebView2Async();
            var core = MainWebView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            core.WebMessageReceived += WebMessageReceived;
            core.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ||
                    uri.Scheme != "https" || !uri.Host.Equals("openclaw.local", StringComparison.OrdinalIgnoreCase))
                    args.Cancel = true;
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;

            var uiDirectory = Path.Combine(AppContext.BaseDirectory, "WebUi");
            if (!Directory.Exists(uiDirectory)) throw new DirectoryNotFoundException("找不到 WebUi 界面资源目录：" + uiDirectory);
            core.SetVirtualHostNameToFolderMapping("openclaw.local", uiDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Navigate("https://openclaw.local/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "应用界面初始化失败。请确认已安装 Microsoft Edge WebView2 Runtime，并从完整发布目录启动。\n\n" + ex.Message,
                "OpenClaw Debugger 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            _closingApproved = true;
            Close();
        }
    }

    private async void WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || source.Scheme != "https" ||
            !source.Host.Equals("openclaw.local", StringComparison.OrdinalIgnoreCase)) return;

        string? id = null;
        try
        {
            var request = JsonSerializer.Deserialize<BridgeRequest>(e.WebMessageAsJson, JsonOptions)
                ?? throw new InvalidDataException("无效的界面请求。");
            id = request.Id;
            var result = await DispatchAsync(request.Command, request.Payload);
            Respond(id, true, result, null);
        }
        catch (Exception ex)
        {
            if (id is not null) Respond(id, false, null, ex.Message);
        }
    }

    private async Task<object?> DispatchAsync(string command, JsonElement payload)
    {
        switch (command)
        {
            case "initialize":
                return new
                {
                    settings = _settings,
                    privateDirectory = _settings.PrivateDirectory,
                    backupDirectory = SettingsRepository.DefaultBackupDirectory,
                    themes = new[] { "Atri", "洛茜", "浅色" },
                    connected = _serverConnected
                };
            case "saveSettings":
                await SaveSettingsAsync(payload);
                return new { settings = _settings, privateDirectory = _settings.PrivateDirectory, backupDirectory = SettingsRepository.DefaultBackupDirectory };
            case "setTheme":
                var theme = payload.GetProperty("theme").GetString() ?? "Atri";
                if (theme is not ("Atri" or "Luoxi" or "Light")) throw new InvalidDataException("未知主题。");
                _settings.ThemeName = theme;
                await SettingsRepository.SaveAsync(_settings);
                return new { theme };
            case "connect":
                return await ConnectAsync();
            case "readMemory":
                return await ReadMemoryAsync(payload);
            case "saveMemory":
                return await SaveMemoryAsync(payload);
            case "readSticker":
                return await ReadStickerAsync(payload);
            case "previewStickerRows":
                return PreviewStickerRows(payload);
            case "saveStickerRows":
                return await SaveStickerRowsAsync(payload);
            case "saveStickerRaw":
                return await SaveStickerRawAsync(payload);
            case "beginStickerUpload":
                return BeginStickerUpload(payload);
            case "appendStickerUpload":
                return AppendStickerUpload(payload);
            case "commitStickerUpload":
                return await CommitStickerUploadAsync(payload);
            case "cancelStickerUpload":
                return CancelStickerUpload(payload);
            case "backup":
                return await BackupAsync();
            case "openBackupFolder":
                Directory.CreateDirectory(SettingsRepository.DefaultBackupDirectory);
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { SettingsRepository.DefaultBackupDirectory } });
                return new { opened = true };
            case "openPrivateFolder":
                Directory.CreateDirectory(_settings.PrivateDirectory);
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { _settings.PrivateDirectory } });
                return new { opened = true };
            case "draftState":
                _hasDrafts = payload.TryGetProperty("dirty", out var dirty) && dirty.GetBoolean();
                return new { accepted = true };
            case "close":
                _closingApproved = true;
                Close();
                return new { closing = true };
            default:
                throw new InvalidDataException("不支持的界面操作：" + command);
        }
    }

    private async Task SaveSettingsAsync(JsonElement payload)
    {
        var incoming = payload.Deserialize<ConnectionSettings>(JsonOptions) ?? throw new InvalidDataException("设置内容无效。");
        incoming.Host = incoming.Host.Trim();
        incoming.Username = incoming.Username.Trim();
        incoming.WorkspacePath = incoming.WorkspacePath.Trim();
        incoming.StickersPath = incoming.StickersPath.Trim();
        if (incoming.Host.Length is < 1 or > 253 || incoming.Host.Any(char.IsWhiteSpace) || incoming.Host.Any(char.IsControl))
            throw new InvalidDataException("服务器地址格式无效。");
        if (!Regex.IsMatch(incoming.Username, "^[a-zA-Z0-9_.-]{1,64}$")) throw new InvalidDataException("SSH 用户名格式无效。");
        if (incoming.Port is < 1 or > 65535) throw new InvalidDataException("SSH 端口需要在 1–65535 之间。");
        ValidateRemotePath(incoming.WorkspacePath, "工作区");
        ValidateRemotePath(incoming.StickersPath, "表情包目录");
        _settings.Connection = incoming;
        _settings.PrivateDirectory = SettingsRepository.DefaultPrivateDirectory;
        await SettingsRepository.SaveAsync(_settings);
    }

    private static void ValidateRemotePath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.Contains('\n') || path.Contains('\r') || path.Contains('\0'))
            throw new InvalidDataException(label + "路径必须是合法的绝对路径。");
    }

    private async Task<object> ConnectAsync()
    {
        if (_busy) throw new InvalidOperationException("当前有操作正在进行。");
        SetBusy(true);
        try
        {
            await SettingsRepository.SaveAsync(_settings);
            _files = await _remote.ConnectAndListAsync(_settings.Connection);
            _serverConnected = true;
            _loadedMemory.Clear();
            _catalogFile = _files.FirstOrDefault(x => x.Root == "stickers" && x.RelativePath == "catalog.json");
            _manifestFile = _files.FirstOrDefault(x => x.Root == "stickers" && x.RelativePath == "MANIFEST.md");
            _catalog = null;
            _catalogContent = null;
            _manifestContent = null;
            string stickerStatus;
            var stickerRows = new List<StickerRow>();
            if (_catalogFile is null || _manifestFile is null)
            {
                stickerRows = _files.Where(x => x.Root == "stickers" && x.IsImage)
                    .Select(x => new StickerRow { Id = Path.GetFileNameWithoutExtension(x.RelativePath), ImagePath = x.RelativePath }).ToList();
                stickerStatus = "找不到 catalog.json 或 MANIFEST.md；标签编辑已关闭以避免不完整写入。";
            }
            else
            {
                try
                {
                    var catTask = _remote.ReadAsync(_settings.Connection, _catalogFile);
                    var manifestTask = _remote.ReadAsync(_settings.Connection, _manifestFile);
                    await Task.WhenAll(catTask, manifestTask);
                    _catalogContent = catTask.Result;
                    _manifestContent = manifestTask.Result;
                    var images = _files.Where(x => x.Root == "stickers" && x.IsImage).ToList();
                    if (StickerCatalogEditor.TryParse(_catalogContent.Text ?? "", images, out _catalog, out var error))
                    {
                        stickerRows = _catalog!.Rows.ToList();
                        stickerStatus = "已读取 " + stickerRows.Count + " 条目录记录。标签保存会同时更新目录和说明文件，并在写入前生成加密快照。";
                    }
                    else
                    {
                        stickerRows = images.Select(x => new StickerRow { Id = Path.GetFileNameWithoutExtension(x.RelativePath), ImagePath = x.RelativePath }).ToList();
                        stickerStatus = error;
                    }
                }
                catch (Exception ex) { stickerStatus = "读取目录失败：" + ex.Message; }
            }
            var memories = _files.Where(x => x.Root == "workspace" && !x.IsImage && x.Editable)
                .OrderBy(x => x.RelativePath.StartsWith("memory/", StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
            var imagesCount = _files.Count(x => x.Root == "stickers" && x.IsImage);
            return new
            {
                connected = true,
                connectionStatus = "已连接 · " + memories.Count + " 个文档 · " + imagesCount + " 张图片",
                memoryFiles = memories,
                memoryCount = memories.Count,
                stickerFiles = GetStickerUiRows(),
                stickerCount = imagesCount,
                stickerEditingEnabled = _catalog is not null && _catalogFile is not null && _manifestFile is not null,
                catalogText = _catalogContent?.Text,
                manifestText = _manifestContent?.Text,
                stickerStatus,
                workspacePath = _settings.Connection.WorkspacePath,
                stickersPath = _settings.Connection.StickersPath
            };
        }
        catch
        {
            _serverConnected = false;
            throw;
        }
        finally { SetBusy(false); }
    }

    private async Task<object> ReadMemoryAsync(JsonElement payload)
    {
        EnsureConnected();
        var path = payload.GetProperty("path").GetString() ?? "";
        var file = _files.FirstOrDefault(x => x.Root == "workspace" && x.RelativePath == path && x.Editable)
            ?? throw new InvalidDataException("文件不在本次扫描的可编辑范围内。");
        var content = await _remote.ReadAsync(_settings.Connection, file);
        _loadedMemory[file.Key] = content;
        return new { path = content.RelativePath, text = content.Text ?? "", sha256 = content.Sha256, size = content.Size, modifiedUtc = content.ModifiedUtc };
    }

    private async Task<object> SaveMemoryAsync(JsonElement payload)
    {
        EnsureConnected();
        if (_snapshots is null) throw new InvalidOperationException("本机加密回滚存储未初始化。");
        var path = payload.GetProperty("path").GetString() ?? "";
        var expectedHash = payload.GetProperty("expectedSha256").GetString() ?? "";
        var text = payload.GetProperty("text").GetString() ?? "";
        var file = _files.FirstOrDefault(x => x.Root == "workspace" && x.RelativePath == path && x.Editable)
            ?? throw new InvalidDataException("文件不在本次扫描的可编辑范围内。");
        if (!_loadedMemory.TryGetValue(file.Key, out var loaded) || !loaded.Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new RemoteConflictException("编辑期间本地基准已变化。请重新读取文件后再保存。");
        if (text.Length > 8 * 1024 * 1024) throw new InvalidDataException("单个记忆文件不能超过 8 MiB。");
        SetBusy(true);
        try
        {
            var latest = await _remote.ReadAsync(_settings.Connection, file);
            if (!latest.Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new RemoteConflictException("服务器文件在读取后发生了变化。没有覆盖它；请重新读取文件并合并修改。");
            var snapshot = await _snapshots.SaveAsync(latest.Root, latest.RelativePath, latest.RawBytes, latest.Sha256);
            var newHash = await _remote.WriteAsync(_settings.Connection, file, text, latest.Sha256);
            var bytes = new UTF8Encoding(false).GetBytes(text);
            _loadedMemory[file.Key] = latest with { Sha256 = newHash, Size = bytes.Length, ModifiedUtc = DateTimeOffset.UtcNow, Text = text, RawBytes = bytes };
            return new { sha256 = newHash, size = bytes.Length, snapshotId = snapshot.Id };
        }
        finally { SetBusy(false); }
    }

    private async Task<object> ReadStickerAsync(JsonElement payload)
    {
        EnsureConnected();
        var path = payload.GetProperty("path").GetString() ?? "";
        var file = _files.FirstOrDefault(x => x.Root == "stickers" && x.IsImage && x.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("图片不在本次扫描的表情包目录内。");
        var content = await _remote.ReadAsync(_settings.Connection, file);
        if (content.Binary is null) throw new InvalidDataException("服务器返回的图片数据为空。");
        var mime = Path.GetExtension(file.RelativePath).ToLowerInvariant() switch
        {
            ".gif" => "image/gif", ".png" => "image/png", ".webp" => "image/webp", ".jpg" or ".jpeg" => "image/jpeg", ".bmp" => "image/bmp", _ => "application/octet-stream"
        };
        return new { path = path, dataUrl = "data:" + mime + ";base64," + Convert.ToBase64String(content.Binary), size = content.Size };
    }

    private object PreviewStickerRows(JsonElement payload)
    {
        EnsureConnected();
        if (_catalog is null || _catalogContent?.Text is null || _manifestContent?.Text is null)
            throw new InvalidOperationException("标签目录不可编辑。");
        var edits = payload.GetProperty("rows").Deserialize<List<StickerEditRow>>(JsonOptions) ?? [];
        var byKey = edits.ToDictionary(x => x.Id + "\0" + x.ImagePath, StringComparer.OrdinalIgnoreCase);
        var oldTags = _catalog.Rows.Select(x => x.TagsText).ToArray();
        string newCatalog;
        string newManifest;
        try
        {
            for (var i = 0; i < _catalog.Rows.Count; i++)
            {
                var row = _catalog.Rows[i];
                if (!byKey.TryGetValue(row.Id + "\0" + row.ImagePath, out var edit))
                    throw new InvalidDataException("标签列表结构已变化，请重新扫描。");
                if (edit.TagsText.Length > 4000) throw new InvalidDataException("单条标签不能超过 4000 个字符。");
                row.TagsText = edit.TagsText;
            }
            newCatalog = _catalog.SerializeWithEdits();
            using var _ = JsonDocument.Parse(newCatalog);
            if (!StickerManifestSynchronizer.TryUpdate(_manifestContent.Text, _catalog.Rows, out newManifest, out var error))
                throw new InvalidDataException(error);
        }
        finally
        {
            for (var i = 0; i < _catalog.Rows.Count; i++) _catalog.Rows[i].TagsText = oldTags[i];
        }
        return new { before = "catalog.json\n" + _catalogContent.Text + "\n\n----- MANIFEST.md -----\n" + _manifestContent.Text,
            after = "catalog.json\n" + newCatalog + "\n\n----- MANIFEST.md -----\n" + newManifest };
    }
    private async Task<object> SaveStickerRowsAsync(JsonElement payload)
    {
        EnsureConnected();
        if (_catalog is null) throw new InvalidOperationException("标签目录不可编辑。");
        var rows = payload.GetProperty("rows").Deserialize<List<StickerEditRow>>(JsonOptions) ?? [];
        var byKey = rows.ToDictionary(x => x.Id + "\0" + x.ImagePath, StringComparer.OrdinalIgnoreCase);
        foreach (var row in _catalog.Rows)
        {
            if (!byKey.TryGetValue(row.Id + "\0" + row.ImagePath, out var edit)) throw new InvalidDataException("标签列表结构已变化，请重新扫描。");
            if (edit.TagsText.Length > 4000) throw new InvalidDataException("单条标签不能超过 4000 个字符。");
            row.TagsText = edit.TagsText;
        }
        var updatedCatalog = _catalog.SerializeWithEdits();
        using var _ = JsonDocument.Parse(updatedCatalog);
        if (_catalogContent?.Text is null || _manifestContent?.Text is null) throw new InvalidOperationException("标签原始内容没有加载。");
        if (!StickerManifestSynchronizer.TryUpdate(_manifestContent.Text, _catalog.Rows, out var updatedManifest, out var error))
            throw new InvalidDataException(error);
        return await SaveStickerPairAsync(updatedCatalog, updatedManifest);
    }

    private async Task<object> SaveStickerRawAsync(JsonElement payload)
    {
        EnsureConnected();
        if (_catalogContent?.Text is null || _manifestContent?.Text is null) throw new InvalidOperationException("请先读取表情包目录。");
        var catalog = payload.GetProperty("catalog").GetString() ?? "";
        var manifest = payload.GetProperty("manifest").GetString() ?? "";
        using var _ = JsonDocument.Parse(catalog);
        return await SaveStickerPairAsync(catalog, manifest);
    }

    private async Task<object> SaveStickerPairAsync(string updatedCatalog, string updatedManifest)
    {
        if (_catalogFile is null || _manifestFile is null || _catalogContent is null || _manifestContent is null || _snapshots is null)
            throw new InvalidOperationException("标签文件或快照存储未就绪。");
        var oldCatalog = _catalogContent;
        var oldManifest = _manifestContent;
        if (updatedCatalog == oldCatalog.Text && updatedManifest == oldManifest.Text) return new { changed = false, snapshotCount = 0, stickerFiles = GetStickerUiRows(), stickerEditingEnabled = _catalog is not null, catalogText = oldCatalog.Text, manifestText = oldManifest.Text };
        SetBusy(true);
        string? catalogNewHash = null;
        try
        {
            var catTask = _remote.ReadAsync(_settings.Connection, _catalogFile);
            var manifestTask = _remote.ReadAsync(_settings.Connection, _manifestFile);
            await Task.WhenAll(catTask, manifestTask);
            var latestCatalog = catTask.Result;
            var latestManifest = manifestTask.Result;
            if (latestCatalog.Sha256 != oldCatalog.Sha256 || latestManifest.Sha256 != oldManifest.Sha256)
                throw new RemoteConflictException("服务器上的目录文件已发生变化。没有覆盖；请重新扫描后再编辑。");

            var snapshotCount = 0;
            if (updatedCatalog != oldCatalog.Text)
            {
                await _snapshots.SaveAsync("stickers", "catalog.json", oldCatalog.RawBytes, oldCatalog.Sha256);
                snapshotCount++;
            }
            if (updatedManifest != oldManifest.Text)
            {
                await _snapshots.SaveAsync("stickers", "MANIFEST.md", oldManifest.RawBytes, oldManifest.Sha256);
                snapshotCount++;
            }
            if (updatedCatalog != oldCatalog.Text)
                catalogNewHash = await _remote.WriteAsync(_settings.Connection, _catalogFile, updatedCatalog, oldCatalog.Sha256);
            string manifestNewHash = oldManifest.Sha256;
            try
            {
                if (updatedManifest != oldManifest.Text)
                    manifestNewHash = await _remote.WriteAsync(_settings.Connection, _manifestFile, updatedManifest, oldManifest.Sha256);
            }
            catch (Exception writeError)
            {
                if (catalogNewHash is not null)
                {
                    try
                    {
                        var current = await _remote.ReadAsync(_settings.Connection, _catalogFile);
                        if (current.Sha256 == catalogNewHash)
                            await _remote.WriteBytesAsync(_settings.Connection, _catalogFile, oldCatalog.RawBytes, catalogNewHash);
                    }
                    catch (Exception rollbackError)
                    {
                        throw new InvalidOperationException("MANIFEST.md 写入失败，catalog.json 自动回滚也失败。请保留本机快照并检查服务器。原因：" + rollbackError.Message, writeError);
                    }
                }
                throw;
            }

            var catalogBytes = new UTF8Encoding(false).GetBytes(updatedCatalog);
            var manifestBytes = new UTF8Encoding(false).GetBytes(updatedManifest);
            _catalogContent = oldCatalog with { Text = updatedCatalog, RawBytes = catalogBytes, Sha256 = catalogNewHash ?? oldCatalog.Sha256, Size = catalogBytes.Length, ModifiedUtc = DateTimeOffset.UtcNow };
            _manifestContent = oldManifest with { Text = updatedManifest, RawBytes = manifestBytes, Sha256 = manifestNewHash, Size = manifestBytes.Length, ModifiedUtc = DateTimeOffset.UtcNow };
            var images = _files.Where(x => x.Root == "stickers" && x.IsImage).ToList();
            StickerCatalogEditor.TryParse(updatedCatalog, images, out _catalog, out _);
            return new { changed = true, snapshotCount, stickerFiles = GetStickerUiRows(), stickerEditingEnabled = _catalog is not null, catalogText = _catalogContent.Text, manifestText = _manifestContent.Text };
        }
        finally { SetBusy(false); }
    }

    private object BeginStickerUpload(JsonElement payload)
    {
        EnsureConnected();
        if (_stickerUploads.Count >= 3) throw new InvalidOperationException("请先完成当前上传，再开始其他上传。");
        var fileName = payload.GetProperty("fileName").GetString() ?? "";
        var size = payload.GetProperty("size").GetInt64();
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." ||
            fileName.Length > 180 || fileName.Contains('/') || fileName.Contains('\\') ||
            fileName.Any(char.IsControl) || fileName.EndsWith('.') || fileName.EndsWith(' ') ||
            Path.GetFileName(fileName) != fileName)
            throw new InvalidDataException("图片文件名无效；请使用单层文件名。 ");
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp"))
            throw new InvalidDataException("仅支持 PNG、JPG、GIF、WEBP、BMP 图片。");
        if (size is < 1 or > StickerUploadLimit) throw new InvalidDataException("每张图片大小需在 1 B 到 16 MiB 之间。");
        if (_files.Any(x => x.Root == "stickers" && x.RelativePath.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("表情包目录中已有同名文件，请先重命名图片。");
        var id = Guid.NewGuid().ToString("N");
        _stickerUploads[id] = new StickerUploadSession(fileName, size);
        return new { uploadId = id, chunkBytes = StickerUploadChunkLimit };
    }

    private object AppendStickerUpload(JsonElement payload)
    {
        var uploadId = payload.GetProperty("uploadId").GetString() ?? "";
        if (!_stickerUploads.TryGetValue(uploadId, out var session)) throw new InvalidOperationException("上传会话已失效，请重新选择图片。");
        byte[] chunk;
        try { chunk = Convert.FromBase64String(payload.GetProperty("contentBase64").GetString() ?? ""); }
        catch (FormatException) { _stickerUploads.Remove(uploadId); session.Dispose(); throw new InvalidDataException("上传数据编码无效。"); }
        if (chunk.Length is < 1 or > StickerUploadChunkLimit || session.Content.Length + chunk.Length > session.Size)
        {
            _stickerUploads.Remove(uploadId);
            session.Dispose();
            throw new InvalidDataException("上传分块大小或总长度超出限制。");
        }
        session.Content.Write(chunk, 0, chunk.Length);
        return new { receivedBytes = session.Content.Length, totalBytes = session.Size };
    }

    private async Task<object> CommitStickerUploadAsync(JsonElement payload)
    {
        EnsureConnected();
        var uploadId = payload.GetProperty("uploadId").GetString() ?? "";
        if (!_stickerUploads.Remove(uploadId, out var session)) throw new InvalidOperationException("上传会话已失效，请重新选择图片。");
        using (session)
        {
            if (session.Content.Length != session.Size) throw new InvalidDataException("上传内容长度不完整，请重新上传。");
            var bytes = session.Content.ToArray();
            if (!MatchesStickerImage(session.FileName, bytes)) throw new InvalidDataException("文件内容与扩展名不匹配，或图片格式不受支持。");
            SetBusy(true);
            try
            {
                var result = await _remote.UploadStickerAsync(_settings.Connection, session.FileName, bytes);
                _files = _files.Append(new RemoteFile
                {
                    Root = "stickers", RelativePath = result.RelativePath, Kind = "image", Size = result.Size,
                    Sha256 = result.Sha256, ModifiedUtc = DateTimeOffset.UtcNow, Editable = false
                }).ToList();
                var count = _files.Count(x => x.Root == "stickers" && x.IsImage);
                return new { fileName = result.RelativePath, size = result.Size, sha256 = result.Sha256, stickerCount = count, stickerFiles = GetStickerUiRows(), stickerEditingEnabled = _catalog is not null };
            }
            finally { SetBusy(false); }
        }
    }

    private object CancelStickerUpload(JsonElement payload)
    {
        var uploadId = payload.GetProperty("uploadId").GetString() ?? "";
        if (_stickerUploads.Remove(uploadId, out var session)) session.Dispose();
        return new { cancelled = true };
    }

    private static bool MatchesStickerImage(string fileName, byte[] bytes)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".png" => bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            ".jpg" or ".jpeg" => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
            ".gif" => bytes.Length >= 6 && (Encoding.ASCII.GetString(bytes, 0, 6) is "GIF87a" or "GIF89a"),
            ".webp" => bytes.Length >= 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP",
            ".bmp" => bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M',
            _ => false
        };
    }

    private List<StickerUiRow> GetStickerUiRows()
    {
        var rows = (_catalog?.Rows ?? []).Select(row => new StickerUiRow(row.Id, row.ImagePath, row.TagsText, true)).ToList();
        var known = new HashSet<string>(rows.Select(x => x.ImagePath), StringComparer.OrdinalIgnoreCase);
        foreach (var file in _files.Where(x => x.Root == "stickers" && x.IsImage))
        {
            if (known.Add(file.RelativePath))
                rows.Add(new StickerUiRow(Path.GetFileNameWithoutExtension(file.RelativePath), file.RelativePath, "", false));
        }
        return rows;
    }
    private async Task<object> BackupAsync()
    {
        EnsureConnected();
        if (_busy) throw new InvalidOperationException("当前有操作正在进行。");
        SetBusy(true);
        try
        {
            var progress = new Progress<long>(bytes => SendProgress("backup", new { bytes }));
            var result = await new LocalServerBackupStore(SettingsRepository.DefaultBackupDirectory)
                .CreateAsync(_settings.Connection, _remote, progress);
            return new { directory = result.Directory, archivePath = result.ArchivePath, archiveBytes = result.ArchiveBytes, sha256 = result.Sha256 };
        }
        finally { SetBusy(false); }
    }

    private void EnsureConnected()
    {
        if (!_serverConnected) throw new InvalidOperationException("请先连接服务器并扫描文件。");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SendProgress("busy", new { busy });
    }

    private void Respond(string id, bool ok, object? data, string? error)
    {
        if (MainWebView.CoreWebView2 is null) return;
        MainWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new BridgeResponse(id, ok, data, error), JsonOptions));
    }

    private void SendProgress(string command, object data)
    {
        if (MainWebView.CoreWebView2 is null) return;
        MainWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new BridgeEvent("progress", command, data), JsonOptions));
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_closingApproved && _hasDrafts)
        {
            var answer = MessageBox.Show(this, "还有未保存的记忆或标签修改。确定放弃并关闭吗？", "存在未保存内容", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) e.Cancel = true;
        }
        if (e.Cancel) return;
        foreach (var session in _stickerUploads.Values) session.Dispose();
        _stickerUploads.Clear();
    }

    private sealed record BridgeRequest(string Id, string Command, JsonElement Payload);
    private sealed record BridgeResponse(string Id, bool Ok, object? Data, string? Error);
    private sealed record BridgeEvent(string Type, string Command, object Data);
    private sealed record StickerEditRow(string Id, string ImagePath, string TagsText);
    private sealed record StickerUiRow(string Id, string ImagePath, string TagsText, bool Catalogued);
    private sealed class StickerUploadSession(string fileName, long size) : IDisposable
    {
        public string FileName { get; } = fileName;
        public long Size { get; } = size;
        public MemoryStream Content { get; } = new((int)size);
        public void Dispose() => Content.Dispose();
    }
}