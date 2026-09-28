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
    private readonly SemaphoreSlim _remoteOperationGate = new(1, 1);
    private readonly object _busySync = new();
    private UserSettings _settings = new();
    private bool _busy;
    private int _busyCount;
    private bool _hasDrafts;
    private bool _closingApproved;

    public MainWindow()
    {
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

    private async Task<object?> DispatchAsync(string command, JsonElement payload, CancellationToken cancellationToken)
    {
        var contract = _bridgeContract ?? throw new InvalidOperationException("界面协议尚未加载。");
        var definition = contract.GetCommand(command);
        var domain = definition.Domain ?? definition.Mode;
        var allowedWhileBusy = domain is "control" or "upload" or "thumbnail" or "local" ||
            definition.Mode is "uploadChunk" or "uploadFinalize" or "uploadCancel";
        if (_busy && !allowedWhileBusy)
            throw new InvalidOperationException("当前有操作正在进行，请等待完成或先取消当前操作。");

        var router = _commandRouter ?? throw new InvalidOperationException("界面命令路由器未初始化。");
        var serialized = domain is "connection" or "read" or "edit" or "backup" ||
            definition.Mode == "uploadFinalize";
        if (!serialized)
            return await router.DispatchAsync(command, payload, cancellationToken);

        await _remoteOperationGate.WaitAsync(cancellationToken);
        try
        {
            if (_busy && !allowedWhileBusy)
                throw new InvalidOperationException("当前有操作正在进行，请等待完成或先取消当前操作。");
            return await router.DispatchAsync(command, payload, cancellationToken);
        }
        finally
        {
            _remoteOperationGate.Release();
        }
    }

    private BridgeCommandRouter CreateCommandRouter()
    {
        var router = new BridgeCommandRouter();
        new ConnectionBridgeHandler(
            _settingsService,
            () => _settings,
            () => _connection.IsConnected,
        ConnectAsync).Register(router);
        new MemoryBridgeHandler(() => _connection.Memory).Register(router);
        new StickerBridgeHandler(() => _connection.Stickers).Register(router);
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
        _connection.Dispose();
    }

}
