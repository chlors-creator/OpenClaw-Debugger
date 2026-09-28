using Microsoft.Web.WebView2.Core;
using System.Text.Json;

namespace OpenClawDebugger;

public sealed class BridgeResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private CoreWebView2? _webView;

    public void Attach(CoreWebView2 webView) => _webView = webView;

    public void Respond(string id, bool ok, object? data, string? error)
    {
        if (_webView is null) return;
        try { _webView.PostWebMessageAsJson(JsonSerializer.Serialize(new BridgeResponse(id, ok, data, error), JsonOptions)); }
        catch (InvalidOperationException) { }
    }

    public void SendProgress(string command, object data)
    {
        if (_webView is null) return;
        try { _webView.PostWebMessageAsJson(JsonSerializer.Serialize(new BridgeEvent("progress", command, data), JsonOptions)); }
        catch (InvalidOperationException) { }
    }

    private sealed record BridgeResponse(string Id, bool Ok, object? Data, string? Error);
    private sealed record BridgeEvent(string Type, string Command, object Data);
}
