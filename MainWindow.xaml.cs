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

    private readonly BridgeResponseWriter _bridgeResponses = new();
    private BridgeDispatcher? _bridgeDispatcher;
    private readonly RemoteOpenClawClient _remote = new();
    private readonly Dictionary<string, RemoteFileContent> _loadedMemory = new(StringComparer.OrdinalIgnoreCase);
    private UserSettings _settings = new();
    private LocalSnapshotStore? _snapshots;
    private StickerService? _stickers;
    private BackupCoordinator? _backup;
    private IReadOnlyList<RemoteFile> _files = [];
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
            _stickers = new StickerService(
                _remote,
                new StickerThumbnailCache(_settings.PrivateDirectory),
                new StickerUploadService(),
                () => _settings.Connection,
                () => _snapshots,
                () => _settings.PrivateDirectory,
                () => _serverConnected,
                () => _busy,
                value => _serverConnected = value,
                SetBusy);
            _backup = new BackupCoordinator(
                _remote,
                SettingsRepository.DefaultBackupDirectory,
                () => _busy,
                SetBusy,
                (command, data) => _bridgeResponses.SendProgress(command, data));

            await MainWebView.EnsureCoreWebView2Async();
            var core = MainWebView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            _bridgeResponses.Attach(core);
            _bridgeDispatcher = new BridgeDispatcher(DispatchAsync, _bridgeResponses);
            core.WebMessageReceived += (_, args) => _ = _bridgeDispatcher.HandleAsync(args);
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
                return await (_stickers ?? throw new InvalidOperationException("表情包服务未初始化。")).ReadAsync(payload);
            case "readStickerThumbnail":
                return await (_stickers ?? throw new InvalidOperationException("表情包服务未初始化。")).ReadThumbnailAsync(payload);
            case "previewStickerRows":
                return (_stickers ?? throw new InvalidOperationException("表情包服务未初始化。")).PreviewRows(payload);
            case "saveStickerRows":
                return await (_stickers ?? throw new InvalidOperationException("表情包服务未初始化。")).SaveRowsAsync(payload);
            case "saveStickerRaw":
                return await (_stickers ?? throw new InvalidOperationException("表情包服务未初始化。")).SaveRawAsync(payload);
            case "beginStickerUpload":
                return (_stickers ?? throw new InvalidOperationException("表情包服务未初始化。")).BeginUpload(payload);
            case "appendStickerUpload":
                return (_stickers ?? throw new InvalidOperationException("表情包服务未初始化。")).AppendUpload(payload);
            case "commitStickerUpload":
                return await (_stickers ?? throw new InvalidOperationException("表情包服务未初始化。")).CommitUploadAsync(payload);
            case "cancelStickerUpload":
                (_stickers ?? throw new InvalidOperationException("表情包服务未初始化。")).CancelUpload(payload);
                return new { cancelled = true };
            case "renameSticker":
                return await (_stickers ?? throw new InvalidOperationException("表情包服务未初始化。")).RenameAsync(payload);
            case "backup":
                var backup = _backup ?? throw new InvalidOperationException("备份服务未初始化。");
                var backupResult = await backup.StartAsync(_settings.Connection);
                return new { directory = backupResult.Directory, archivePath = backupResult.ArchivePath, archiveBytes = backupResult.ArchiveBytes, sha256 = backupResult.Sha256 };
            case "toggleBackupPause":
                return (_backup ?? throw new InvalidOperationException("备份服务未初始化。")).TogglePause();
            case "cancelBackup":
                return (_backup ?? throw new InvalidOperationException("备份服务未初始化。")).Cancel();
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
            var stickerService = _stickers ?? throw new InvalidOperationException("表情包服务未初始化。");
            var stickerLoad = await stickerService.LoadAsync(_files);
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
                stickerFiles = stickerLoad.StickerFiles,
                stickerCount = stickerLoad.StickerCount,
                stickerEditingEnabled = stickerLoad.StickerEditingEnabled,
                catalogText = stickerLoad.CatalogText,
                manifestText = stickerLoad.ManifestText,
                stickerStatus = stickerLoad.StickerStatus,
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

    private void EnsureConnected()
    {
        if (!_serverConnected) throw new InvalidOperationException("请先连接服务器并扫描文件。");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _bridgeResponses.SendProgress("busy", new { busy });
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_closingApproved && _hasDrafts)
        {
            var answer = MessageBox.Show(this, "还有未保存的记忆或标签修改。确定放弃并关闭吗？", "存在未保存内容", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) e.Cancel = true;
        }
        if (e.Cancel) return;
        _backup?.Dispose();
        _stickers?.Dispose();
        _remote.Dispose();
    }

}
