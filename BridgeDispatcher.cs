using Microsoft.Web.WebView2.Core;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace OpenClawDebugger;

public sealed class BridgeDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Func<string, JsonElement, CancellationToken, Task<object?>> _dispatch;
    private readonly BridgeResponseWriter _responses;
    private readonly BridgeCommandContract _contract;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _operations = new(StringComparer.Ordinal);

    public BridgeDispatcher(
        Func<string, JsonElement, CancellationToken, Task<object?>> dispatch,
        BridgeResponseWriter responses,
        BridgeCommandContract contract)
    {
        _dispatch = dispatch;
        _responses = responses;
        _contract = contract;
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
            if (request.ProtocolVersion != BridgeProtocol.Version)
                throw new InvalidDataException($"界面协议版本不匹配：需要 v{BridgeProtocol.Version}，收到 v{request.ProtocolVersion}。请重启应用。");
            _contract.ValidatePayload(request.Command, request.Payload);

            if (string.Equals(request.Command, "cancelOperation", StringComparison.Ordinal))
            {
                var target = request.Payload.GetProperty("operationId").GetString();
                if (!string.IsNullOrWhiteSpace(target) && _operations.TryGetValue(target, out var targetCancellation))
                    targetCancellation.Cancel();
                _responses.Respond(id, true, new { cancelled = !string.IsNullOrWhiteSpace(target) }, null);
                return;
            }

            var operationId = string.IsNullOrWhiteSpace(request.OperationId) ? request.Id : request.OperationId;
            using var operationCancellation = new CancellationTokenSource();
            var timeoutMs = _contract.GetCommand(request.Command).TimeoutMs;
            if (timeoutMs > 0) operationCancellation.CancelAfter(timeoutMs);
            if (!_operations.TryAdd(operationId, operationCancellation))
                throw new InvalidOperationException("重复的界面操作标识。");
            try
            {
                var result = await _dispatch(request.Command, request.Payload, operationCancellation.Token);
                _responses.Respond(id, true, result, null);
            }
            finally
            {
                _operations.TryRemove(operationId, out _);
            }
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(id)) _responses.Respond(id, false, null, ex.Message);
        }
    }

    public void CancelAll()
    {
        foreach (var cancellation in _operations.Values)
        {
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private static bool IsAllowedSource(string source)
    {
        return Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            uri.Scheme == "https" && uri.Host.Equals("openclaw.local", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record BridgeRequest(
        string Id,
        string Command,
        JsonElement Payload,
        int ProtocolVersion = 0,
        string? OperationId = null);
}
