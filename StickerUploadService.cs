using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OpenClawDebugger;

public sealed record StickerUploadBeginResult(string UploadId, int ChunkBytes);
public sealed record StickerUploadAppendResult(long ReceivedBytes, long TotalBytes);

public sealed class StickerUploadSession : IDisposable
{
    public StickerUploadSession(string fileName, long size, string temporaryPath)
    {
        FileName = fileName;
        Size = size;
        TemporaryPath = temporaryPath;
        Content = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
    }

    public string FileName { get; }
    public long Size { get; }
    public string TemporaryPath { get; }
    public FileStream Content { get; }
    public Dictionary<long, string> ChunkHashes { get; } = new();

    public void Dispose()
    {
        Content.Dispose();
        try { File.Delete(TemporaryPath); } catch { }
    }
}

public sealed class StickerUploadService : IDisposable
{
    public const int MaximumUploadSize = 16 * 1024 * 1024;
    public const int ChunkSize = 192 * 1024;
    private const int MaximumSessions = 3;

    private readonly Dictionary<string, StickerUploadSession> _sessions = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public StickerUploadService(string? privateDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(privateDirectory))
            CleanupStaging(privateDirectory);
    }

    public static int CleanupStaging(string privateDirectory)
    {
        var stagingDirectory = Path.Combine(Path.GetFullPath(privateDirectory), "UploadStaging");
        if (!Directory.Exists(stagingDirectory)) return 0;
        if ((new DirectoryInfo(stagingDirectory).Attributes & FileAttributes.ReparsePoint) != 0) return 0;
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(stagingDirectory, ".upload-*.part", SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(path);
                deleted++;
            }
            catch
            {
                // A current process may still own a staging file; leave it for the next startup pass.
            }
        }
        OperationLogStore.Current?.Stage("upload-staging-cleanup", $"清理未完成上传临时文件：{deleted} 个");
        return deleted;
    }

    public StickerUploadBeginResult Begin(
        string fileName,
        long size,
        IReadOnlyList<RemoteFile> files,
        string privateDirectory)
    {
        ValidateFileName(fileName, allowMessageSpacing: true);
        if (size is < 1 or > MaximumUploadSize) throw new InvalidDataException("每张图片大小需在 1 B 到 16 MiB 之间。");
        if (files.Any(x => x.Root == "stickers" && x.RelativePath.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("表情包目录中已有同名文件，请先重命名图片。");

        var id = Guid.NewGuid().ToString("N");
        var stagingDirectory = Path.Combine(privateDirectory, "UploadStaging");
        Directory.CreateDirectory(stagingDirectory);
        var temporaryPath = Path.Combine(stagingDirectory, ".upload-" + id + ".part");
        lock (_gate)
        {
            if (_sessions.Count >= MaximumSessions) throw new InvalidOperationException("请先完成当前上传，再开始其他上传。");
            _sessions[id] = new StickerUploadSession(fileName, size, temporaryPath);
        }
        return new StickerUploadBeginResult(id, ChunkSize);
    }

    public StickerUploadAppendResult Append(string uploadId, long offset, string contentBase64, string expectedSha256)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(uploadId, out var session))
                throw new InvalidOperationException("上传会话已失效，请重新选择图片。");

            byte[] chunk;
            try { chunk = Convert.FromBase64String(contentBase64); }
            catch (FormatException)
            {
                _sessions.Remove(uploadId);
                session.Dispose();
                throw new InvalidDataException("上传数据编码无效。");
            }

            if (chunk.Length is < 1 or > ChunkSize || offset < 0 || offset > session.Size || offset + chunk.Length > session.Size)
                throw new InvalidDataException("上传分块大小或总长度超出限制。");
            var actualSha256 = Convert.ToHexString(SHA256.HashData(chunk)).ToLowerInvariant();
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("上传分块校验失败，请重试当前分块。");

            if (offset < session.Content.Length)
            {
                if (offset + chunk.Length == session.Content.Length &&
                    session.ChunkHashes.TryGetValue(offset, out var priorHash) &&
                    string.Equals(priorHash, actualSha256, StringComparison.OrdinalIgnoreCase))
                    return new StickerUploadAppendResult(session.Content.Length, session.Size);
                throw new InvalidDataException("上传分块位置不匹配，请重新开始上传。");
            }
            if (offset != session.Content.Length)
                throw new InvalidDataException("上传分块位置不匹配，请重新开始上传。");
            session.Content.Write(chunk, 0, chunk.Length);
            session.ChunkHashes[offset] = actualSha256;
            return new StickerUploadAppendResult(session.Content.Length, session.Size);
        }
    }

    public StickerUploadSession TakeForCommit(string uploadId)
    {
        lock (_gate)
        {
            if (_sessions.Remove(uploadId, out var session)) return session;
        }
        throw new InvalidOperationException("上传会话已失效，请重新选择图片。");
    }

    public void Cancel(string uploadId)
    {
        StickerUploadSession? session = null;
        lock (_gate) _sessions.Remove(uploadId, out session);
        session?.Dispose();
    }

    public void Dispose()
    {
        StickerUploadSession[] sessions;
        lock (_gate)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }
        foreach (var session in sessions) session.Dispose();
    }

    public static void ValidateFileName(string fileName, bool allowMessageSpacing = false)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." ||
            fileName.Length > 180 || fileName.Contains('/') || fileName.Contains('\\') ||
            fileName.Any(char.IsControl) || fileName.EndsWith('.') || fileName.EndsWith(' ') ||
            Path.GetFileName(fileName) != fileName)
            throw new InvalidDataException(allowMessageSpacing
                ? "图片文件名无效；请使用单层文件名。 "
                : "图片文件名无效；请使用不超过 180 个字符的单层文件名。");
        if (Path.GetExtension(fileName).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp"))
            throw new InvalidDataException("仅支持 PNG、JPG、GIF、WEBP、BMP 图片。");
    }

    public static bool MatchesImage(string fileName, ReadOnlySpan<byte> bytes)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".png" => bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            ".jpg" or ".jpeg" => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
            ".gif" => bytes.Length >= 6 && Encoding.ASCII.GetString(bytes[..6]) is "GIF87a" or "GIF89a",
            ".webp" => bytes.Length >= 12 && Encoding.ASCII.GetString(bytes[..4]) == "RIFF" && Encoding.ASCII.GetString(bytes.Slice(8, 4)) == "WEBP",
            ".bmp" => bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M',
            _ => false
        };
    }

}
