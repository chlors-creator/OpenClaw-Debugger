using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenClawDebugger;

public sealed record StickerThumbnailResult(
    string Path,
    string DataUrl,
    long Size,
    long OriginalSize,
    int ThumbnailEdge,
    bool CacheHit);

public sealed class StickerThumbnailCache
{
    private const int ThumbnailEdge = 256;
    private const long PersistentCacheLimit = 128L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _privateDirectory;

    public StickerThumbnailCache(string privateDirectory)
    {
        _privateDirectory = Path.GetFullPath(privateDirectory);
        Directory.CreateDirectory(Path.Combine(_privateDirectory, "ThumbnailCache"));
    }

    public async Task<StickerThumbnailResult> ReadAsync(
        IRemoteFileClient remote,
        ConnectionSettings settings,
        RemoteFile file,
        CancellationToken cancellationToken = default)
    {
        var cachePath = GetCachePath(file);
        var cached = await ReadCacheAsync(cachePath, cancellationToken);
        if (cached is not null)
        {
            return new StickerThumbnailResult(
                file.RelativePath,
                cached.DataUrl,
                cached.Size,
                cached.OriginalSize,
                ThumbnailEdge,
                true);
        }

        var content = await remote.ReadAsync(settings, file, cancellationToken);
        var original = content.Binary ?? throw new InvalidDataException("服务器返回的图片数据为空。");
        var thumbnail = await Task.Run(() => CreateThumbnail(original, file.RelativePath), cancellationToken);
        var entry = new CachedStickerThumbnail(thumbnail.DataUrl, thumbnail.Size, content.Size, ThumbnailEdge);
        await WriteCacheAsync(cachePath, entry, cancellationToken);
        return new StickerThumbnailResult(
            file.RelativePath,
            thumbnail.DataUrl,
            thumbnail.Size,
            content.Size,
            ThumbnailEdge,
            false);
    }

    private string GetCachePath(RemoteFile file)
    {
        var fingerprint = string.Join("\0",
            file.Key,
            file.Size.ToString(CultureInfo.InvariantCulture),
            (file.ModifiedUtc?.UtcDateTime.Ticks ?? 0).ToString(CultureInfo.InvariantCulture),
            file.Sha256);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint))).ToLowerInvariant();
        return Path.Combine(_privateDirectory, "ThumbnailCache", "thumb-" + hash + ".json");
    }

    private static async Task<CachedStickerThumbnail?> ReadCacheAsync(string cachePath, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(cachePath)) return null;
            CachedStickerThumbnail? cached;
            await using (var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                cached = await JsonSerializer.DeserializeAsync<CachedStickerThumbnail>(stream, JsonOptions, cancellationToken);
            }
            if (cached is null || cached.ThumbnailEdge != ThumbnailEdge || cached.Size <= 0 ||
                cached.OriginalSize < 0 || string.IsNullOrWhiteSpace(cached.DataUrl))
            {
                TryDelete(cachePath);
                return null;
            }

            try { File.SetLastAccessTimeUtc(cachePath, DateTime.UtcNow); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return cached;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException)
        {
            TryDelete(cachePath);
            return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static async Task WriteCacheAsync(
        string cachePath,
        CachedStickerThumbnail cache,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(cachePath);
        if (string.IsNullOrWhiteSpace(directory)) return;
        var temporaryPath = Path.Combine(directory, ".thumb-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(directory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(cache, JsonOptions);
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
            File.Move(temporaryPath, cachePath, overwrite: true);
            Trim(directory);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void Trim(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var files = Directory.EnumerateFiles(directory, "thumb-*.json")
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists)
                .OrderBy(file => file.LastAccessTimeUtc)
                .ThenBy(file => file.LastWriteTimeUtc)
                .ToList();
            long total = files.Sum(file => file.Length);
            foreach (var file in files)
            {
                if (total <= PersistentCacheLimit) break;
                try
                {
                    var length = file.Length;
                    file.Delete();
                    total = Math.Max(0, total - length);
                }
                catch (FileNotFoundException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static (string DataUrl, long Size) CreateThumbnail(byte[] bytes, string path)
    {
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.FirstOrDefault()
                ?? throw new InvalidDataException("图片没有可用的图像帧。");
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
                throw new InvalidDataException("图片尺寸无效。");

            var scale = Math.Min(1d, Math.Min(
                ThumbnailEdge / (double)frame.PixelWidth,
                ThumbnailEdge / (double)frame.PixelHeight));
            BitmapSource source = frame;
            if (scale < 0.999d)
            {
                var transformed = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
                transformed.Freeze();
                source = transformed;
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = new MemoryStream();
            encoder.Save(output);
            var thumbnailBytes = output.ToArray();
            return ("data:image/png;base64," + Convert.ToBase64String(thumbnailBytes), thumbnailBytes.LongLength);
        }
        catch (Exception ex) when (ex is FileFormatException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            var extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{ThumbnailEdge}\" height=\"{ThumbnailEdge}\" viewBox=\"0 0 {ThumbnailEdge} {ThumbnailEdge}\"><rect width=\"100%\" height=\"100%\" rx=\"28\" fill=\"#17302d\"/><text x=\"50%\" y=\"50%\" dominant-baseline=\"middle\" text-anchor=\"middle\" fill=\"#a5e8d4\" font-family=\"Segoe UI,sans-serif\" font-size=\"34\" font-weight=\"700\">{extension}</text></svg>";
            var svgBytes = Encoding.UTF8.GetBytes(svg);
            return ("data:image/svg+xml;base64," + Convert.ToBase64String(svgBytes), svgBytes.LongLength);
        }
    }

    private sealed record CachedStickerThumbnail(string DataUrl, long Size, long OriginalSize, int ThumbnailEdge);
}
