namespace OpenClawDebugger;

/// <summary>连接生命周期、远程客户端装配和一次连接扫描的协调器。</summary>
public sealed class ConnectionCoordinator : IDisposable
{
    private readonly Func<bool> _isBusy;
    private readonly Action<bool> _setBusy;
    private readonly Action<string, object> _sendProgress;
    private readonly RemoteOpenClawClient _remote = new();
    private readonly RemoteFileClient _remoteFiles;
    private readonly RemoteStickerClient _remoteStickers;
    private readonly RemoteSnapshotClient _snapshotClient = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private LocalSnapshotStore? _snapshots;
    private UserSettings? _settings;
    private bool _initialized;
    private bool _disposed;

    public ConnectionCoordinator(
        Func<bool> isBusy,
        Action<bool> setBusy,
        Action<string, object> sendProgress)
    {
        _isBusy = isBusy;
        _setBusy = setBusy;
        _sendProgress = sendProgress;
        _remoteFiles = new RemoteFileClient(_remote);
        _remoteStickers = new RemoteStickerClient(_remote);
    }

    public MemoryService? Memory { get; private set; }
    public StickerService? Stickers { get; private set; }
    public ModelService? Models { get; private set; }
    public BackupCoordinator? Backup { get; private set; }
    public IReadOnlyList<RemoteFile> Files { get; private set; } = [];
    public bool IsConnected { get; private set; }

    public void Initialize(UserSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        if (_initialized) throw new InvalidOperationException("连接协调器已经初始化。" );

        _settings = settings;
        _snapshots = new LocalSnapshotStore(settings.PrivateDirectory);
        Memory = new MemoryService(
            _remoteFiles,
            () => settings.Connection,
            () => _snapshots,
            () => Files,
            () => IsConnected,
            _setBusy);
        Stickers = new StickerService(
            _remoteFiles,
            _remoteStickers,
            new StickerThumbnailCache(settings.PrivateDirectory),
            new StickerUploadService(settings.PrivateDirectory),
            () => settings.Connection,
            () => _snapshots,
            () => settings.PrivateDirectory,
            () => IsConnected,
            _isBusy,
            value => IsConnected = value,
            _setBusy);
        Models = new ModelService(
            new RemoteModelClient(_remote),
            () => settings.Connection,
            () => IsConnected,
            settings.PrivateDirectory,
            _setBusy);
        Backup = new BackupCoordinator(
            _snapshotClient,
            SettingsRepository.DefaultBackupDirectory,
            _isBusy,
            _setBusy,
            _sendProgress,
            () => settings.BackupRetentionCount);
        _initialized = true;
    }

    public async Task<object> ConnectAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || !ReferenceEquals(_settings, settings))
            throw new InvalidOperationException("连接协调器尚未使用当前设置初始化。" );
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (_isBusy()) throw new InvalidOperationException("当前有操作正在进行。" );
            _setBusy(true);
            try
            {
            await SettingsRepository.SaveAsync(settings);
            Files = await _remoteFiles.ConnectAndListAsync(settings.Connection, cancellationToken);
            IsConnected = true;
            Memory?.Reset();
            var stickerService = Stickers ?? throw new InvalidOperationException("表情包服务未初始化。" );
            var stickerLoad = await stickerService.LoadAsync(Files, cancellationToken);
            var memories = Files.Where(x => x.Root == "workspace" && !x.IsImage && x.Editable)
                .OrderBy(x => x.RelativePath.StartsWith("memory/", StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var imagesCount = Files.Count(x => x.Root == "stickers" && x.IsImage);
            return new ConnectionScanResult(
                true,
                "已连接 · " + memories.Count + " 个文档 · " + imagesCount + " 张图片",
                memories,
                memories.Count,
                stickerLoad.StickerFiles,
                stickerLoad.StickerCount,
                stickerLoad.StickerEditingEnabled,
                stickerLoad.CatalogText,
                stickerLoad.ManifestText,
                stickerLoad.StickerStatus,
                settings.Connection.WorkspacePath,
                settings.Connection.StickersPath);
            }
            catch
            {
                IsConnected = false;
                throw;
            }
            finally
            {
                _setBusy(false);
            }
        }
        finally { _connectGate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Backup?.Dispose();
        Models?.Dispose();
        Stickers?.Dispose();
        _snapshotClient.Dispose();
        _remote.Dispose();
    }
}

public sealed record ConnectionScanResult(
    bool Connected,
    string ConnectionStatus,
    IReadOnlyList<RemoteFile> MemoryFiles,
    int MemoryCount,
    IReadOnlyList<StickerUiRow> StickerFiles,
    int StickerCount,
    bool StickerEditingEnabled,
    string? CatalogText,
    string? ManifestText,
    string StickerStatus,
    string WorkspacePath,
    string StickersPath);
