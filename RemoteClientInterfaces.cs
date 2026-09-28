using System.IO;

namespace OpenClawDebugger;

/// <summary>允许管理的工作区和表情包文件操作。</summary>
public interface IRemoteFileClient
{
    Task<IReadOnlyList<RemoteFile>> ConnectAndListAsync(ConnectionSettings settings, CancellationToken cancellationToken = default);
    Task<RemoteFileContent> ReadAsync(ConnectionSettings settings, RemoteFile file, CancellationToken cancellationToken = default);
    Task<string> WriteAsync(ConnectionSettings settings, RemoteFile file, string text, string expectedSha256, CancellationToken cancellationToken = default);
    Task<string> WriteBytesAsync(ConnectionSettings settings, RemoteFile file, byte[] bytes, string expectedSha256, CancellationToken cancellationToken = default);
}

/// <summary>表情包标签、上传和重命名操作。</summary>
public interface IRemoteStickerClient
{
    Task<RemoteStickerPairWriteResult> WriteStickerPairAsync(ConnectionSettings settings, string catalogText, string expectedCatalogSha256, string manifestText, string expectedManifestSha256, CancellationToken cancellationToken = default);
    Task<RemoteStickerUploadResult> UploadStickerAsync(ConnectionSettings settings, string fileName, byte[] bytes, CancellationToken cancellationToken = default);
    Task<RemoteStickerUploadResult> UploadStickerAsync(ConnectionSettings settings, string fileName, Stream source, long size, CancellationToken cancellationToken = default);
    Task<RemoteStickerRenameResult> RenameStickerAsync(ConnectionSettings settings, string oldFileName, string newFileName, string expectedSha256, CancellationToken cancellationToken = default);
}

/// <summary>服务器整机快照操作。快照传输使用独立 SSH 流，避免干扰交互式 JSON 会话。</summary>
public interface IRemoteSnapshotClient
{
    Task CreateServerSnapshotAsync(ConnectionSettings settings, string remotePath, BackupPauseController? pauseController = null, CancellationToken cancellationToken = default);
    Task<long> GetServerSnapshotSizeAsync(ConnectionSettings settings, string remotePath, CancellationToken cancellationToken = default);
    Task<RemoteSnapshotTransferResult> ResumeServerSnapshotAsync(ConnectionSettings settings, string remotePath, Stream destination, long offset, long totalBytes, BackupPauseController? pauseController = null, IProgress<ServerSnapshotProgress>? progress = null, CancellationToken cancellationToken = default);
    Task RemoveServerSnapshotAsync(ConnectionSettings settings, string remotePath, CancellationToken cancellationToken = default);
}
