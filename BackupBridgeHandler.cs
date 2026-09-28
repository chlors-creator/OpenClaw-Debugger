using System.Text.Json;

namespace OpenClawDebugger;

/// <summary>服务器快照和备份按钮相关的 WebView 命令。</summary>
public sealed class BackupBridgeHandler
{
    private readonly Func<BackupCoordinator?> _service;
    private readonly Func<ConnectionSettings> _settings;
    private readonly Action<string> _openFolder;

    public BackupBridgeHandler(
        Func<BackupCoordinator?> service,
        Func<ConnectionSettings> settings,
        Action<string> openFolder)
    {
        _service = service;
        _settings = settings;
        _openFolder = openFolder;
    }

    public void Register(BridgeCommandRouter router)
    {
        router.Map("backup", BackupAsync);
        router.Map("toggleBackupPause", _ => Task.FromResult<object?>(Require().TogglePause()));
        router.Map("cancelBackup", _ => Task.FromResult<object?>(Require().Cancel()));
        router.Map("openBackupFolder", _ => OpenBackupFolder());
    }

    private async Task<object?> BackupAsync(JsonElement _)
    {
        var result = await Require().StartAsync(_settings());
        return new
        {
            directory = result.Directory,
            archivePath = result.ArchivePath,
            archiveBytes = result.ArchiveBytes,
            sha256 = result.Sha256
        };
    }

    private Task<object?> OpenBackupFolder()
    {
        var path = SettingsRepository.DefaultBackupDirectory;
        _openFolder(path);
        return Task.FromResult<object?>(new { opened = true });
    }

    private BackupCoordinator Require() => _service() ?? throw new InvalidOperationException("备份服务未初始化。");
}
