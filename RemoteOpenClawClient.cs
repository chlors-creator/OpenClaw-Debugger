using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenClawDebugger;

public sealed record RemoteStickerUploadResult(string RelativePath, long Size, string Sha256);
public sealed record RemoteStickerRenameResult(string RelativePath, long Size, string Sha256);
public sealed record RemoteStickerPairWriteResult(string CatalogSha256, long CatalogSize, string ManifestSha256, long ManifestSize);
public sealed partial class RemoteOpenClawClient : IDisposable
{
    private readonly RemoteAgentSession _session = new();
    private readonly RemoteStickerStreamUploader _streamUploader = new();



    // The SSH session sends requests to RemoteAgentProgram.Main.

    internal Task<JsonObject> InvokeModelAsync(
        ConnectionSettings settings,
        JsonObject request,
        CancellationToken cancellationToken = default) =>
        _session.InvokeAsync(
            settings,
            request,
            cancellationToken,
            string.Equals(request["action"]?.GetValue<string>(), "models_test_latency", StringComparison.Ordinal)
                ? TimeSpan.FromMinutes(30)
                : null);


    public async Task<IReadOnlyList<RemoteFile>> ConnectAndListAsync(
        ConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        var response = await _session.InvokeAsync(settings, new JsonObject { ["action"] = "inventory" }, cancellationToken);
        var files = new List<RemoteFile>();
        foreach (var node in response["files"]?.AsArray() ?? new JsonArray())
        {
            if (node is not JsonObject item) continue;
            files.Add(new RemoteFile
            {
                Root = item["root"]?.GetValue<string>() ?? "",
                RelativePath = item["relativePath"]?.GetValue<string>() ?? "",
                Kind = item["kind"]?.GetValue<string>() ?? "text",
                Size = item["size"]?.GetValue<long>() ?? 0,
                Sha256 = item["sha256"]?.GetValue<string>() ?? "",
                ModifiedUtc = FromUnix(item["modifiedUnix"]?.GetValue<double>()),
                Editable = item["editable"]?.GetValue<bool>() ?? false
            });
        }
        return files;
    }

    public async Task<RemoteFileContent> ReadAsync(
        ConnectionSettings settings, RemoteFile file, CancellationToken cancellationToken = default)
    {
        var request = new JsonObject
        {
            ["action"] = "read",
            ["root"] = file.Root,
            ["path"] = file.RelativePath
        };
        var response = await _session.InvokeAsync(settings, request, cancellationToken);
        var isBinary = response["binary"]?.GetValue<bool>() ?? false;
        var encoded = response["content"]?.GetValue<string>() ?? "";
        var raw = Convert.FromBase64String(encoded);
        var text = isBinary ? null : new UTF8Encoding(false, true).GetString(raw).TrimStart('\uFEFF');
        return new RemoteFileContent(
            file.Root,
            file.RelativePath,
            response["sha256"]?.GetValue<string>() ?? "",
            response["size"]?.GetValue<long>() ?? 0,
            FromUnix(response["modifiedUnix"]?.GetValue<double>()) ?? DateTimeOffset.UtcNow,
            text,
            isBinary ? raw : null,
            raw);
    }

    public Task<string> WriteAsync(
        ConnectionSettings settings,
        RemoteFile file,
        string text,
        string expectedSha256,
        CancellationToken cancellationToken = default) =>
        WriteBytesAsync(settings, file, Encoding.UTF8.GetBytes(text), expectedSha256, cancellationToken);

    public async Task<string> WriteBytesAsync(
        ConnectionSettings settings,
        RemoteFile file,
        byte[] bytes,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        var request = new JsonObject
        {
            ["action"] = "write",
            ["root"] = file.Root,
            ["path"] = file.RelativePath,
            ["expectedSha256"] = expectedSha256,
            ["content"] = Convert.ToBase64String(bytes)
        };
        var response = await _session.InvokeAsync(settings, request, cancellationToken);
        return response["sha256"]?.GetValue<string>() ?? "";
    }
    public async Task<RemoteStickerPairWriteResult> WriteStickerPairAsync(
        ConnectionSettings settings,
        string catalogText,
        string expectedCatalogSha256,
        string manifestText,
        string expectedManifestSha256,
        CancellationToken cancellationToken = default)
    {
        var request = new JsonObject
        {
            ["action"] = "write_pair",
            ["root"] = "stickers",
            ["catalogExpectedSha256"] = expectedCatalogSha256,
            ["manifestExpectedSha256"] = expectedManifestSha256,
            ["catalogContent"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(catalogText)),
            ["manifestContent"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(manifestText))
        };
        var response = await _session.InvokeAsync(settings, request, cancellationToken);
        return new RemoteStickerPairWriteResult(
            response["catalogSha256"]?.GetValue<string>() ?? "",
            response["catalogSize"]?.GetValue<long>() ?? Encoding.UTF8.GetByteCount(catalogText),
            response["manifestSha256"]?.GetValue<string>() ?? "",
            response["manifestSize"]?.GetValue<long>() ?? Encoding.UTF8.GetByteCount(manifestText));
    }
    public async Task<RemoteStickerUploadResult> UploadStickerAsync(
        ConnectionSettings settings, string fileName, byte[] bytes, CancellationToken cancellationToken = default)
    {
        var request = new JsonObject
        {
            ["action"] = "upload",
            ["root"] = "stickers",
            ["filename"] = fileName,
            ["content"] = Convert.ToBase64String(bytes)
        };
        var response = await _session.InvokeAsync(settings, request, cancellationToken);
        return new RemoteStickerUploadResult(
            response["relativePath"]?.GetValue<string>() ?? fileName,
            response["size"]?.GetValue<long>() ?? bytes.LongLength,
            response["sha256"]?.GetValue<string>() ?? "");
    }

    public Task<RemoteStickerUploadResult> UploadStickerAsync(
        ConnectionSettings settings,
        string fileName,
        Stream source,
        long size,
        CancellationToken cancellationToken = default) =>
        _streamUploader.UploadAsync(settings, fileName, source, size, cancellationToken);

    public async Task<RemoteStickerRenameResult> RenameStickerAsync(
        ConnectionSettings settings, string oldFileName, string newFileName, string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        var request = new JsonObject
        {
            ["action"] = "rename",
            ["root"] = "stickers",
            ["oldFilename"] = oldFileName,
            ["newFilename"] = newFileName,
            ["expectedSha256"] = expectedSha256
        };
        var response = await _session.InvokeAsync(settings, request, cancellationToken);
        return new RemoteStickerRenameResult(
            response["relativePath"]?.GetValue<string>() ?? newFileName,
            response["size"]?.GetValue<long>() ?? 0,
            response["sha256"]?.GetValue<string>() ?? expectedSha256);
    }

    public void Disconnect() => _session.Disconnect();

    public void Dispose() => _session.Dispose();

    private static DateTimeOffset? FromUnix(double? seconds)
    {
        if (seconds is null) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds.Value * 1000)); }
        catch { return null; }
    }
}

public sealed class RemoteConflictException(string message) : InvalidOperationException(message);
