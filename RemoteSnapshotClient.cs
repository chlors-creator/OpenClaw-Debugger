using System.IO;

namespace OpenClawDebugger;

/// <summary>服务器快照专用客户端。它封装快照传输实现，和交互式远程文件客户端解耦。</summary>
public sealed class RemoteSnapshotClient : IRemoteSnapshotClient, IDisposable
{
    private readonly RemoteSnapshotTransport _transport = new();

    public Task CreateServerSnapshotAsync(ConnectionSettings settings, string remotePath, BackupPauseController? pauseController = null, CancellationToken cancellationToken = default) =>
        _transport.CreateServerSnapshotAsync(settings, remotePath, pauseController, cancellationToken);

    public Task<long> GetServerSnapshotSizeAsync(ConnectionSettings settings, string remotePath, CancellationToken cancellationToken = default) =>
        _transport.GetServerSnapshotSizeAsync(settings, remotePath, cancellationToken);

    public Task<RemoteSnapshotTransferResult> ResumeServerSnapshotAsync(ConnectionSettings settings, string remotePath, Stream destination, long offset, long totalBytes, BackupPauseController? pauseController = null, IProgress<ServerSnapshotProgress>? progress = null, CancellationToken cancellationToken = default) =>
        _transport.ResumeServerSnapshotAsync(settings, remotePath, destination, offset, totalBytes, pauseController, progress, cancellationToken);

    public Task RemoveServerSnapshotAsync(ConnectionSettings settings, string remotePath, CancellationToken cancellationToken = default) =>
        _transport.RemoveServerSnapshotAsync(settings, remotePath, cancellationToken);
    public void Dispose() { }
}
