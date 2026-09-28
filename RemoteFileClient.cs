using System.IO;

namespace OpenClawDebugger;

/// <summary>文件域客户端。与 SSH 会话实现隔离，供记忆服务和缩略图缓存使用。</summary>
public sealed class RemoteFileClient : IRemoteFileClient
{
    private readonly RemoteOpenClawClient _transport;

    public RemoteFileClient(RemoteOpenClawClient transport) => _transport = transport;

    public Task<IReadOnlyList<RemoteFile>> ConnectAndListAsync(ConnectionSettings settings, CancellationToken cancellationToken = default) =>
        _transport.ConnectAndListAsync(settings, cancellationToken);

    public Task<RemoteFileContent> ReadAsync(ConnectionSettings settings, RemoteFile file, CancellationToken cancellationToken = default) =>
        _transport.ReadAsync(settings, file, cancellationToken);

    public Task<string> WriteAsync(ConnectionSettings settings, RemoteFile file, string text, string expectedSha256, CancellationToken cancellationToken = default) =>
        _transport.WriteAsync(settings, file, text, expectedSha256, cancellationToken);

    public Task<string> WriteBytesAsync(ConnectionSettings settings, RemoteFile file, byte[] bytes, string expectedSha256, CancellationToken cancellationToken = default) =>
        _transport.WriteBytesAsync(settings, file, bytes, expectedSha256, cancellationToken);
}
