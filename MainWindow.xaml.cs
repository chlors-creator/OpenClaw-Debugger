using Microsoft.Web.WebView2.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace OpenClawDebugger;

public partial class MainWindow : Window
{
    private readonly BridgeResponseWriter _bridgeResponses = new();
    private readonly SettingsService _settingsService = new();
    private BridgeDispatcher? _bridgeDispatcher;
    private BridgeCommandRouter? _commandRouter;
    private BridgeCommandContract? _bridgeContract;
    private readonly ConnectionCoordinator _connection;
    private readonly RemoteTaskScheduler _taskScheduler;
    private readonly object _busySync = new();
    private UserSettings _settings = new();
    private bool _busy;
    private int _busyCount;
    private bool _hasDrafts;
    private bool _closingApproved;

    public MainWindow()
    {
        _taskScheduler = new RemoteTaskScheduler(status =>
            _bridgeResponses.SendProgress("task", status));
        _connection = new ConnectionCoordinator(
            () => _busy,
            SetBusy,
            (command, data) => _bridgeResponses.SendProgress(command, data));
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = await SettingsRepository.LoadAsync();
            _settings.PrivateDirectory = SettingsRepository.DefaultPrivateDirectory;
            OperationLogStore.Configure(_settings.PrivateDirectory);
            if (_settings.ThemeName is not ("Atri" or "Luoxi" or "Light")) _settings.ThemeName = "Atri";
            _connection.Initialize(_settings);
            _commandRouter = CreateCommandRouter();
            var contractPath = Path.Combine(AppContext.BaseDirectory, "WebUi", "bridge-contract.json");
            _bridgeContract = BridgeCommandContract.Load(contractPath);
            _bridgeContract.ValidateRegisteredCommands(_commandRouter.Commands);

            await MainWebView.EnsureCoreWebView2Async();
            var core = MainWebView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            _bridgeResponses.Attach(core);
            _bridgeDispatcher = new BridgeDispatcher(DispatchAsync, _bridgeResponses, _bridgeContract);
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
            // 每次启动使用新的查询版本，避免 WebView2 继续复用旧的 HTML/脚本缓存。
            var cacheKey = File.GetLastWriteTimeUtc(typeof(MainWindow).Assembly.Location).Ticks;
            core.Navigate("https://openclaw.local/index.html?v=" + cacheKey.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

    private async Task<object?> DispatchAsync(string operationId, string command, JsonElement payload, IReadOnlyList<CoreWebView2File> files, CancellationToken cancellationToken)
    {
        var contract = _bridgeContract ?? throw new InvalidOperationException("界面协议尚未加载。");
        var definition = contract.GetCommand(command);
        var router = _commandRouter ?? throw new InvalidOperationException("界面命令路由器未初始化。");
        // Local UI work and explicit interruption controls do not use the SSH queue.
        var immediate = definition.Mode is "local" ||
            command is "toggleBackupPause" or "cancelBackup" or "cancelStickerUpload" or "close";
        if (immediate)
        {
            var result = await router.DispatchAsync(command, payload, cancellationToken);
            if (command == "toggleBackupPause")
                _taskScheduler.ReportActive(_connection.Backup?.CurrentProgress?.Phase == "paused"
                    ? RemoteTaskState.Paused : RemoteTaskState.Running);
            else if (command == "cancelBackup")
                _taskScheduler.ReportActive(RemoteTaskState.Cancelling, "正在取消备份…");
            return result;
        }

        return await _taskScheduler.EnqueueAsync(
            operationId,
            command,
            async token => command == "streamStickerUpload"
                ? await (_connection.Stickers ?? throw new InvalidOperationException("表情包服务未初始化。"))
                    .UploadFileAsync(payload, files.FirstOrDefault()?.Path ?? throw new InvalidDataException("没有收到本地上传文件。"), token)
                : await router.DispatchAsync(command, payload, token),
            cancellationToken);
    }

    private BridgeCommandRouter CreateCommandRouter()
    {
        var router = new BridgeCommandRouter();
        new ConnectionBridgeHandler(
            _settingsService,
            () => _settings,
            () => _connection.IsConnected,
            ConnectAsync,
            DisconnectAsync).Register(router);
        new MemoryBridgeHandler(() => _connection.Memory).Register(router);
        new StickerBridgeHandler(() => _connection.Stickers).Register(router);
        new ModelBridgeHandler(() => _connection.Models).Register(router);
        new BackupBridgeHandler(() => _connection.Backup, () => _settings.Connection, OpenFolder).Register(router);
        new WindowBridgeHandler(
            () => _settings.PrivateDirectory,
            dirty => _hasDrafts = dirty,
            () =>
            {
                _closingApproved = true;
                Close();
            },
            OpenFolder).Register(router);
        new LoggingBridgeHandler().Register(router);
        return router;
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = true,
            ArgumentList = { path }
        });
    }

    private Task<object> ConnectAsync(CancellationToken cancellationToken) => _connection.ConnectAsync(_settings, cancellationToken);

    private Task<object> DisconnectAsync(CancellationToken cancellationToken) => _connection.DisconnectAsync(cancellationToken);

    private void SetBusy(bool busy)
    {
        lock (_busySync)
        {
            _busyCount = busy ? _busyCount + 1 : Math.Max(0, _busyCount - 1);
            _busy = _busyCount > 0;
        }
        _bridgeResponses.SendProgress("busy", new { busy = _busy });
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_closingApproved && _hasDrafts)
        {
            var answer = MessageBox.Show(this, "还有未保存的记忆或标签修改。确定放弃并关闭吗？", "存在未保存内容", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) e.Cancel = true;
        }
        if (e.Cancel) return;
        _bridgeDispatcher?.CancelAll();
        _taskScheduler.Dispose();
        _connection.Dispose();
    }

}
