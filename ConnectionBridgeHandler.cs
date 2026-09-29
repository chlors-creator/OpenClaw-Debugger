using System.Text.Json;

namespace OpenClawDebugger;

/// <summary>连接、设置和初始状态相关的 WebView 命令。</summary>
public sealed class ConnectionBridgeHandler
{
    private readonly SettingsService _settingsService;
    private readonly Func<UserSettings> _settings;
    private readonly Func<bool> _isConnected;
    private readonly Func<CancellationToken, Task<object>> _connect;
    private readonly Func<CancellationToken, Task<object>> _disconnect;

    public ConnectionBridgeHandler(
        SettingsService settingsService,
        Func<UserSettings> settings,
        Func<bool> isConnected,
        Func<CancellationToken, Task<object>> connect,
        Func<CancellationToken, Task<object>> disconnect)
    {
        _settingsService = settingsService;
        _settings = settings;
        _isConnected = isConnected;
        _connect = connect;
        _disconnect = disconnect;
    }

    public void Register(BridgeCommandRouter router)
    {
        router.Map("initialize", _ => Task.FromResult<object?>(new
        {
            settings = _settings(),
            privateDirectory = _settings().PrivateDirectory,
            backupDirectory = SettingsRepository.DefaultBackupDirectory,
            themes = new[] { "Atri", "洛茜", "浅色" },
            connected = _isConnected()
        }));
        router.Map("saveSettings", SaveSettingsAsync);
        router.Map("setTheme", SetThemeAsync);
        router.Map("connect", async (_, cancellationToken) => await _connect(cancellationToken));
        router.Map("disconnect", async (_, cancellationToken) => await _disconnect(cancellationToken));
    }

    private async Task<object?> SaveSettingsAsync(JsonElement payload)
    {
        var settings = _settings();
        await _settingsService.SaveConnectionAsync(payload, settings);
        return new
        {
            settings,
            privateDirectory = settings.PrivateDirectory,
            backupDirectory = SettingsRepository.DefaultBackupDirectory
        };
    }

    private async Task<object?> SetThemeAsync(JsonElement payload)
    {
        var theme = await _settingsService.SetThemeAsync(payload, _settings());
        return new { theme };
    }
}
