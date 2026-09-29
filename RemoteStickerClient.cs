using System.IO;

namespace OpenClawDebugger;

/// <summary>表情包域客户端。标签事务、上传和重命名共用同一个 SSH 会话。</summary>
public sealed class RemoteStickerClient : IRemoteStickerClient
{
    private readonly RemoteOpenClawClient _transport;

    public RemoteStickerClient(RemoteOpenClawClient transport) => _transport = transport;

    public Task<RemoteStickerPairWriteResult> WriteStickerPairAsync(ConnectionSettings settings, string catalogText, string expectedCatalogSha256, string manifestText, string expectedManifestSha256, CancellationToken cancellationToken = default) =>
        _transport.WriteStickerPairAsync(settings, catalogText, expectedCatalogSha256, manifestText, expectedManifestSha256, cancellationToken);

    public Task<RemoteStickerUploadResult> UploadStickerAsync(ConnectionSettings settings, string fileName, byte[] bytes, CancellationToken cancellationToken = default) =>
        _transport.UploadStickerAsync(settings, fileName, bytes, cancellationToken);

    public Task<RemoteStickerUploadResult> UploadStickerAsync(ConnectionSettings settings, string fileName, Stream source, long size, CancellationToken cancellationToken = default) =>
        _transport.UploadStickerAsync(settings, fileName, source, size, cancellationToken);

    public Task<RemoteStickerUploadResult> UploadStickerAsync(ConnectionSettings settings, string fileName, Stream source, long size, IProgress<long>? progress, CancellationToken cancellationToken = default) =>
        _transport.UploadStickerAsync(settings, fileName, source, size, progress, cancellationToken);

    public Task<RemoteStickerRenameResult> RenameStickerAsync(ConnectionSettings settings, string oldFileName, string newFileName, string expectedSha256, CancellationToken cancellationToken = default) =>
        _transport.RenameStickerAsync(settings, oldFileName, newFileName, expectedSha256, cancellationToken);
}
