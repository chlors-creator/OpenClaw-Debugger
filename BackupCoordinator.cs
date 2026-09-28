namespace OpenClawDebugger;

public sealed record BackupControlResult(bool Paused = false, bool Cancelling = false);

public sealed class BackupCoordinator : IDisposable
{
    private readonly IRemoteSnapshotClient _remote;
    private readonly string _backupDirectory;
    private readonly Func<bool> _isBusy;
    private readonly Action<bool> _setBusy;
    private readonly Action<string, object> _sendProgress;
    private BackupPauseController? _pauseController;
    private CancellationTokenSource? _cancellation;
    private ServerSnapshotProgress? _lastProgress;

    public BackupCoordinator(
        IRemoteSnapshotClient remote,
        string backupDirectory,
        Func<bool> isBusy,
        Action<bool> setBusy,
        Action<string, object> sendProgress)
    {
        _remote = remote;
        _backupDirectory = backupDirectory;
        _isBusy = isBusy;
        _setBusy = setBusy;
        _sendProgress = sendProgress;
    }

    public bool IsRunning => _cancellation is not null;
    public ServerSnapshotProgress? CurrentProgress => _lastProgress;

    public async Task<ServerSnapshotResult> StartAsync(
        ConnectionSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (_isBusy()) throw new InvalidOperationException("当前有操作正在进行。");
        if (_cancellation is not null) throw new InvalidOperationException("当前已有服务器备份正在运行。");

        var pauseController = new BackupPauseController();
        using var backupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pauseController = pauseController;
        _cancellation = backupCancellation;
        _lastProgress = null;
        _setBusy(true);
        try
        {
            var progress = new Progress<ServerSnapshotProgress>(snapshotProgress =>
            {
                _lastProgress = snapshotProgress;
                _sendProgress("backup", snapshotProgress);
            });
            return await new LocalServerBackupStore(_backupDirectory)
                .CreateAsync(settings, _remote, progress, pauseController, backupCancellation.Token);
        }
        catch (OperationCanceledException) when (backupCancellation.IsCancellationRequested)
        {
            var current = _lastProgress;
            _sendProgress("backup", new ServerSnapshotProgress(
                "cancelled",
                current?.Bytes ?? 0,
                current?.TotalBytes,
                null,
                null,
                current?.Attempt ?? 1,
                current?.MaxAttempts ?? 8,
                "备份已取消，临时文件已清理。"));
            throw;
        }
        finally
        {
            _pauseController = null;
            if (ReferenceEquals(_cancellation, backupCancellation))
                _cancellation = null;
            _lastProgress = null;
            _setBusy(false);
        }
    }

    public BackupControlResult Cancel()
    {
        var cancellation = _cancellation
            ?? throw new InvalidOperationException("当前没有正在运行的服务器备份。");
        if (!cancellation.IsCancellationRequested)
        {
            _pauseController?.Resume();
            cancellation.Cancel();
            var current = _lastProgress ?? new ServerSnapshotProgress("transferring", 0, null, null, null, 1, 8);
            _sendProgress("backup", current with
            {
                Phase = "cancelling",
                BytesPerSecond = 0,
                RemainingSeconds = null,
                Message = "正在取消备份并清理临时文件…"
            });
        }
        return new BackupControlResult(Cancelling: true);
    }

    public BackupControlResult TogglePause()
    {
        var controller = _pauseController
            ?? throw new InvalidOperationException("当前没有正在运行的服务器备份。");
        if (controller.IsPaused)
        {
            controller.Resume();
            if (_lastProgress is not null) _sendProgress("backup", _lastProgress);
            return new BackupControlResult(Paused: false);
        }

        controller.Pause();
        var current = _lastProgress ?? new ServerSnapshotProgress("preparing", 0, null, null, null, 1, 8);
        _sendProgress("backup", current with
        {
            Phase = "paused",
            BytesPerSecond = 0,
            RemainingSeconds = null,
            Message = "备份已暂停；本地临时归档和当前 SSH 数据流均已保留。"
        });
        return new BackupControlResult(Paused: true);
    }

    public void Dispose()
    {
        _pauseController?.Resume();
        _cancellation?.Cancel();
        _pauseController = null;
        _cancellation = null;
        _lastProgress = null;
    }
}
