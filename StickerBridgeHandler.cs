using System.Text.Json;

namespace OpenClawDebugger;

/// <summary>表情包目录、预览、上传和重命名相关的 WebView 命令。</summary>
public sealed class StickerBridgeHandler
{
    private readonly Func<StickerService?> _service;

    public StickerBridgeHandler(Func<StickerService?> service) => _service = service;

    public void Register(BridgeCommandRouter router)
    {
        router.Map("readSticker", async payload => await Require().ReadAsync(payload));
        router.Map("readStickerThumbnail", async payload => await Require().ReadThumbnailAsync(payload));
        router.Map("previewStickerRows", payload => Task.FromResult<object?>(Require().PreviewRows(payload)));
        router.Map("saveStickerRows", async payload => await Require().SaveRowsAsync(payload));
        router.Map("saveStickerRaw", async payload => await Require().SaveRawAsync(payload));
        router.Map("beginStickerUpload", payload => Task.FromResult<object?>(Require().BeginUpload(payload)));
        router.Map("appendStickerUpload", payload => Task.FromResult<object?>(Require().AppendUpload(payload)));
        router.Map("commitStickerUpload", async payload => await Require().CommitUploadAsync(payload));
        router.Map("cancelStickerUpload", payload => CancelUpload(payload));
        router.Map("renameSticker", async payload => await Require().RenameAsync(payload));
    }

    private Task<object?> CancelUpload(JsonElement payload)
    {
        Require().CancelUpload(payload);
        return Task.FromResult<object?>(new { cancelled = true });
    }

    private StickerService Require() => _service() ?? throw new InvalidOperationException("表情包服务未初始化。");
}
