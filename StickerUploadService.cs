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
    internal byte[] DecodeBuffer { get; } = new byte[StickerUploadService.ChunkSize];

    public void Dispose()
    {
        Content.Dispose();
        try { File.Delete(TemporaryPath); } catch { }
    }
}

public sealed class StickerUploadService : IDisposable
{
    public const int MaximumUploadSize = 30 * 1024 * 1024;
    public const int ChunkSize = 192 * 1024;
    public const int MaximumImageDimension = 16384;
    public const long MaximumImagePixels = 64_000_000;
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
        if (size is < 1 or > MaximumUploadSize) throw new InvalidDataException("每张图片大小需在 1 B 到 30 MiB 之间。");
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

            if (string.IsNullOrWhiteSpace(contentBase64) || contentBase64.Length > ((ChunkSize + 2) / 3) * 4)
            {
                _sessions.Remove(uploadId);
                session.Dispose();
                throw new InvalidDataException("上传数据编码无效。");
            }

            var chunk = session.DecodeBuffer;
            if (!Convert.TryFromBase64String(contentBase64, chunk, out var chunkLength))
            {
                _sessions.Remove(uploadId);
                session.Dispose();
                throw new InvalidDataException("上传数据编码无效。");
            }

            if (chunkLength is < 1 or > ChunkSize || offset < 0 || offset > session.Size || offset + chunkLength > session.Size)
                throw new InvalidDataException("上传分块大小或总长度超出限制。");
            var actualSha256 = Convert.ToHexString(SHA256.HashData(chunk.AsSpan(0, chunkLength))).ToLowerInvariant();
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("上传分块校验失败，请重试当前分块。");

            if (offset < session.Content.Length)
            {
                if (offset + chunkLength == session.Content.Length &&
                    session.ChunkHashes.TryGetValue(offset, out var priorHash) &&
                    string.Equals(priorHash, actualSha256, StringComparison.OrdinalIgnoreCase))
                    return new StickerUploadAppendResult(session.Content.Length, session.Size);
                throw new InvalidDataException("上传分块位置不匹配，请重新开始上传。");
            }
            if (offset != session.Content.Length)
                throw new InvalidDataException("上传分块位置不匹配，请重新开始上传。");
            session.Content.Write(chunk, 0, chunkLength);
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

    /// <summary>Validates the image signature and dimensions from a bounded header.</summary>
    public static void ValidateImage(Stream stream, string fileName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateFileName(fileName);
        if (!stream.CanSeek) throw new InvalidDataException("上传内容不可回退，无法安全校验图片。");
        var originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            var length = checked((int)Math.Min(stream.Length, 256 * 1024));
            var header = new byte[length];
            var read = 0;
            while (read < header.Length)
            {
                var count = stream.Read(header, read, header.Length - read);
                if (count == 0) break;
                read += count;
            }
            if (!MatchesImage(fileName, header.AsSpan(0, read)))
                throw new InvalidDataException("文件内容与扩展名不匹配，或图片格式不受支持。");
            if (!TryReadDimensions(fileName, header.AsSpan(0, read), out var width, out var height))
                throw new InvalidDataException("无法读取图片尺寸，已拒绝可能损坏或伪装的图片。");
            if (width < 1 || height < 1 || width > MaximumImageDimension || height > MaximumImageDimension ||
                (long)width * height > MaximumImagePixels)
                throw new InvalidDataException($"图片尺寸过大（最大 {MaximumImageDimension}×{MaximumImageDimension}，且像素不超过 {MaximumImagePixels:N0}）。");
        }
        finally { stream.Position = originalPosition; }
    }

    private static bool TryReadDimensions(string fileName, ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension == ".png" && bytes.Length >= 24)
        {
            width = checked((int)ReadUInt32BigEndian(bytes[16..20]));
            height = checked((int)ReadUInt32BigEndian(bytes[20..24]));
            return true;
        }
        if (extension == ".gif" && bytes.Length >= 10)
        {
            width = bytes[6] | (bytes[7] << 8);
            height = bytes[8] | (bytes[9] << 8);
            return true;
        }
        if (extension == ".bmp" && bytes.Length >= 26)
        {
            width = Math.Abs(BitConverter.ToInt32(bytes[18..22]));
            height = Math.Abs(BitConverter.ToInt32(bytes[22..26]));
            return true;
        }
        if (extension == ".webp" && bytes.Length >= 30 && Encoding.ASCII.GetString(bytes[12..16]) == "VP8X")
        {
            width = 1 + bytes[24] + (bytes[25] << 8) + (bytes[26] << 16);
            height = 1 + bytes[27] + (bytes[28] << 8) + (bytes[29] << 16);
            return true;
        }
        if ((extension is ".jpg" or ".jpeg") && bytes.Length >= 4)
            return TryReadJpegDimensions(bytes, out width, out height);
        return extension == ".webp" && TryReadWebpDimensions(bytes, out width, out height);
    }

    private static bool TryReadWebpDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length >= 29 && Encoding.ASCII.GetString(bytes[12..16]) == "VP8L" && bytes[20] == 0x2f)
        {
            var bits = bytes[21] | (bytes[22] << 8) | (bytes[23] << 16) | (bytes[24] << 24);
            width = (bits & 0x3fff) + 1;
            height = ((bits >> 14) & 0x3fff) + 1;
            return true;
        }
        if (bytes.Length >= 34 && Encoding.ASCII.GetString(bytes[12..16]) == "VP8 " &&
            bytes[26] == 0x9d && bytes[27] == 0x01 && bytes[28] == 0x2a)
        {
            width = (bytes[30] | (bytes[31] << 8)) & 0x3fff;
            height = (bytes[32] | (bytes[33] << 8)) & 0x3fff;
            return true;
        }
        return false;
    }

    private static bool TryReadJpegDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        var offset = 2;
        while (offset + 4 <= bytes.Length)
        {
            if (bytes[offset] != 0xff) { offset++; continue; }
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) break;
            var marker = bytes[offset++];
            if (marker is 0xd8 or 0xd9) continue;
            if (marker is >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > bytes.Length) break;
            var segmentLength = (bytes[offset] << 8) | bytes[offset + 1];
            if (segmentLength < 2 || offset + segmentLength > bytes.Length) break;
            var sof = marker is >= 0xc0 and <= 0xc3 || marker is >= 0xc5 and <= 0xc7 ||
                marker is >= 0xc9 and <= 0xcb || marker is >= 0xcd and <= 0xcf;
            if (sof && segmentLength >= 7)
            {
                height = (bytes[offset + 3] << 8) | bytes[offset + 4];
                width = (bytes[offset + 5] << 8) | bytes[offset + 6];
                return true;
            }
            offset += segmentLength;
        }
        return false;
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> value) =>
        ((uint)value[0] << 24) | ((uint)value[1] << 16) | ((uint)value[2] << 8) | value[3];

}
