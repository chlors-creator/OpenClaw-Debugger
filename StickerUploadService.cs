using System.IO;
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

    public StickerUploadBeginResult Begin(
        string fileName,
        long size,
        IReadOnlyList<RemoteFile> files,
        string privateDirectory)
    {
        if (_sessions.Count >= MaximumSessions) throw new InvalidOperationException("请先完成当前上传，再开始其他上传。");
        ValidateFileName(fileName, allowMessageSpacing: true);
        if (size is < 1 or > MaximumUploadSize) throw new InvalidDataException("每张图片大小需在 1 B 到 16 MiB 之间。");
        if (files.Any(x => x.Root == "stickers" && x.RelativePath.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("表情包目录中已有同名文件，请先重命名图片。");

        var id = Guid.NewGuid().ToString("N");
        var stagingDirectory = Path.Combine(privateDirectory, "UploadStaging");
        Directory.CreateDirectory(stagingDirectory);
        var temporaryPath = Path.Combine(stagingDirectory, ".upload-" + id + ".part");
        _sessions[id] = new StickerUploadSession(fileName, size, temporaryPath);
        return new StickerUploadBeginResult(id, ChunkSize);
    }

    public StickerUploadAppendResult Append(string uploadId, string contentBase64)
    {
        if (!_sessions.TryGetValue(uploadId, out var session))
            throw new InvalidOperationException("上传会话已失效，请重新选择图片。");

        byte[] chunk;
        try { chunk = Convert.FromBase64String(contentBase64); }
        catch (FormatException)
        {
            RemoveAndDispose(uploadId, session);
            throw new InvalidDataException("上传数据编码无效。");
        }

        if (chunk.Length is < 1 or > ChunkSize || session.Content.Length + chunk.Length > session.Size)
        {
            RemoveAndDispose(uploadId, session);
            throw new InvalidDataException("上传分块大小或总长度超出限制。");
        }
        session.Content.Write(chunk, 0, chunk.Length);
        return new StickerUploadAppendResult(session.Content.Length, session.Size);
    }

    public StickerUploadSession TakeForCommit(string uploadId)
    {
        if (_sessions.Remove(uploadId, out var session)) return session;
        throw new InvalidOperationException("上传会话已失效，请重新选择图片。");
    }

    public void Cancel(string uploadId)
    {
        if (_sessions.Remove(uploadId, out var session)) session.Dispose();
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
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

    private void RemoveAndDispose(string uploadId, StickerUploadSession session)
    {
        _sessions.Remove(uploadId);
        session.Dispose();
    }
}
