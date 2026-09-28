using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Text.Json;

namespace OpenClawDebugger;

public sealed class BridgeDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IReadOnlyDictionary<string, Func<JsonElement, Task<object?>>> _handlers;
    private readonly Func<string, JsonElement, Task<object?>>? _dispatch;
    private readonly BridgeResponseWriter _responses;

    public BridgeDispatcher(
        IReadOnlyDictionary<string, Func<JsonElement, Task<object?>>> handlers,
        BridgeResponseWriter responses)
    {
        _handlers = handlers;
        _responses = responses;
    }

    public BridgeDispatcher(
        Func<string, JsonElement, Task<object?>> dispatch,
        BridgeResponseWriter responses)
    {
        _handlers = new Dictionary<string, Func<JsonElement, Task<object?>>>(StringComparer.Ordinal);
        _dispatch = dispatch;
        _responses = responses;
    }

    public async Task HandleAsync(CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!IsAllowedSource(args.Source)) return;
        string? id = null;
        try
        {
            var request = JsonSerializer.Deserialize<BridgeRequest>(args.WebMessageAsJson, JsonOptions)
                ?? throw new InvalidDataException("无效的界面请求。");
            id = request.Id;
            object? result;
            if (_dispatch is not null)
            {
                result = await _dispatch(request.Command, request.Payload);
            }
            else
            {
                if (!_handlers.TryGetValue(request.Command, out var handler))
                    throw new InvalidDataException("不支持的界面操作：" + request.Command);
                result = await handler(request.Payload);
            }
            _responses.Respond(id, true, result, null);
        }
        catch (Exception ex)
        {
            if (id is not null) _responses.Respond(id, false, null, ex.Message);
        }
    }

    private static bool IsAllowedSource(string source)
    {
        return Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            uri.Scheme == "https" && uri.Host.Equals("openclaw.local", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record BridgeRequest(string Id, string Command, JsonElement Payload);
}
