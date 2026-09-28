using System.Text.Json;

namespace OpenClawDebugger;

/// <summary>窗口生命周期、本地目录和草稿状态相关的 WebView 命令。</summary>
public sealed class WindowBridgeHandler
{
    private readonly Func<string> _privateDirectory;
    private readonly Action<bool> _setDrafts;
    private readonly Action _close;
    private readonly Action<string> _openFolder;

    public WindowBridgeHandler(
        Func<string> privateDirectory,
        Action<bool> setDrafts,
        Action close,
        Action<string> openFolder)
    {
        _privateDirectory = privateDirectory;
        _setDrafts = setDrafts;
        _close = close;
        _openFolder = openFolder;
    }

    public void Register(BridgeCommandRouter router)
    {
        router.Map("openPrivateFolder", _ => OpenPrivateFolder());
        router.Map("draftState", SetDraftState);
        router.Map("close", Close);
    }

    private Task<object?> OpenPrivateFolder()
    {
        var path = _privateDirectory();
        _openFolder(path);
        return Task.FromResult<object?>(new { opened = true });
    }

    private Task<object?> SetDraftState(JsonElement payload)
    {
        _setDrafts(payload.TryGetProperty("dirty", out var dirty) && dirty.GetBoolean());
        return Task.FromResult<object?>(new { accepted = true });
    }

    private Task<object?> Close(JsonElement _)
    {
        _close();
        return Task.FromResult<object?>(new { closing = true });
    }
}
