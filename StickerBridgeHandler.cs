using System.Text.Json;

namespace OpenClawDebugger;

/// <summary>表情包目录、预览、上传和重命名相关的 WebView 命令。</summary>
public sealed class StickerBridgeHandler
{
    private readonly Func<StickerService?> _service;

    public StickerBridgeHandler(Func<StickerService?> service) => _service = service;

    public void Register(BridgeCommandRouter router)
    {
        router.Map("readSticker", async (payload, cancellationToken) => await Require().ReadAsync(payload, cancellationToken));
        router.Map("readStickerThumbnail", async (payload, cancellationToken) => await Require().ReadThumbnailAsync(payload, cancellationToken));
        router.Map("previewStickerRows", payload => Task.FromResult<object?>(Require().PreviewRows(payload)));
        router.Map("saveStickerRows", async (payload, cancellationToken) => await Require().SaveRowsAsync(payload, cancellationToken));
        router.Map("saveStickerRaw", async (payload, cancellationToken) => await Require().SaveRawAsync(payload, cancellationToken));
        router.Map("beginStickerUpload", payload => Task.FromResult<object?>(Require().BeginUpload(payload)));
        router.Map("appendStickerUpload", payload => Task.FromResult<object?>(Require().AppendUpload(payload)));
        router.Map("commitStickerUpload", async (payload, cancellationToken) => await Require().CommitUploadAsync(payload, cancellationToken));
        router.Map("cancelStickerUpload", payload => CancelUpload(payload));
        router.Map("renameSticker", async (payload, cancellationToken) => await Require().RenameAsync(payload, cancellationToken));
    }

    private Task<object?> CancelUpload(JsonElement payload)
    {
        Require().CancelUpload(payload);
        return Task.FromResult<object?>(new { cancelled = true });
    }

    private StickerService Require() => _service() ?? throw new InvalidOperationException("表情包服务未初始化。");
}
