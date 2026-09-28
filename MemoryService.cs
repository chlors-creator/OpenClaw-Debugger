using System.IO;
using System.Text;
using System.Text.Json;

namespace OpenClawDebugger;

/// <summary>记忆文件的读取、冲突检测、加密回滚快照和保存流程。</summary>
public sealed class MemoryService
{
    private readonly IRemoteFileClient _remote;
    private readonly Func<ConnectionSettings> _connection;
    private readonly Func<LocalSnapshotStore?> _snapshots;
    private readonly Func<IReadOnlyList<RemoteFile>> _files;
    private readonly Func<bool> _isConnected;
    private readonly Action<bool> _setBusy;
    private readonly Dictionary<string, RemoteFileContent> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public MemoryService(
        IRemoteFileClient remote,
        Func<ConnectionSettings> connection,
        Func<LocalSnapshotStore?> snapshots,
        Func<IReadOnlyList<RemoteFile>> files,
        Func<bool> isConnected,
        Action<bool> setBusy)
    {
        _remote = remote;
        _connection = connection;
        _snapshots = snapshots;
        _files = files;
        _isConnected = isConnected;
        _setBusy = setBusy;
    }

    public void Reset() => _loaded.Clear();

    public async Task<object> ReadAsync(JsonElement payload, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var path = payload.GetProperty("path").GetString() ?? "";
        var file = FindFile(path);
        var content = await _remote.ReadAsync(_connection(), file, cancellationToken);
        _loaded[file.Key] = content;
        return new
        {
            path = content.RelativePath,
            text = content.Text ?? "",
            sha256 = content.Sha256,
            size = content.Size,
            modifiedUtc = content.ModifiedUtc
        };
    }

    public async Task<object> SaveAsync(JsonElement payload, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var snapshotStore = _snapshots() ?? throw new InvalidOperationException("本机加密回滚存储未初始化。");
        var path = payload.GetProperty("path").GetString() ?? "";
        var expectedHash = payload.GetProperty("expectedSha256").GetString() ?? "";
        var text = payload.GetProperty("text").GetString() ?? "";
        var file = FindFile(path);
        if (!_loaded.TryGetValue(file.Key, out var loaded) || !loaded.Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new RemoteConflictException("编辑期间本地基准已变化。请重新读取文件后再保存。");
        if (text.Length > 8 * 1024 * 1024) throw new InvalidDataException("单个记忆文件不能超过 8 MiB。");

        _setBusy(true);
        try
        {
            var latest = await _remote.ReadAsync(_connection(), file, cancellationToken);
            if (!latest.Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new RemoteConflictException("服务器文件在读取后发生了变化。没有覆盖它；请重新读取文件并合并修改。");
            var snapshot = await snapshotStore.SaveAsync(latest.Root, latest.RelativePath, latest.RawBytes, latest.Sha256);
            var newHash = await _remote.WriteAsync(_connection(), file, text, latest.Sha256, cancellationToken);
            var bytes = new UTF8Encoding(false).GetBytes(text);
            _loaded[file.Key] = latest with
            {
                Sha256 = newHash,
                Size = bytes.Length,
                ModifiedUtc = DateTimeOffset.UtcNow,
                Text = text,
                RawBytes = bytes
            };
            return new { sha256 = newHash, size = bytes.Length, snapshotId = snapshot.Id };
        }
        finally
        {
            _setBusy(false);
        }
    }

    private RemoteFile FindFile(string path) =>
        _files().FirstOrDefault(x => x.Root == "workspace" && x.RelativePath == path && x.Editable)
        ?? throw new InvalidDataException("文件不在本次扫描的可编辑范围内。");

    private void EnsureConnected()
    {
        if (!_isConnected()) throw new InvalidOperationException("请先连接服务器并扫描文件。");
    }
}
