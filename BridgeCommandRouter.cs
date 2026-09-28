using System.IO;
using System.Text.Json;

namespace OpenClawDebugger;

public delegate Task<object?> BridgeCommandHandler(JsonElement payload, CancellationToken cancellationToken);

/// <summary>WebView 命令注册表。命令处理器按功能模块注册，宿主窗口不再维护大型 switch。</summary>
public sealed class BridgeCommandRouter
{
    private readonly Dictionary<string, BridgeCommandHandler> _handlers = new(StringComparer.Ordinal);

    public BridgeCommandRouter Map(string command, BridgeCommandHandler handler)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("命令名不能为空。", nameof(command));
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryAdd(command, handler)) throw new InvalidOperationException("重复注册 WebView 命令：" + command);
        return this;
    }

    public BridgeCommandRouter Map(string command, Func<JsonElement, Task<object?>> handler) =>
        Map(command, (payload, _) => handler(payload));

    public Task<object?> DispatchAsync(string command, JsonElement payload, CancellationToken cancellationToken = default)
    {
        if (!_handlers.TryGetValue(command, out var handler))
            throw new InvalidDataException("不支持的界面操作：" + command);
        return handler(payload, cancellationToken);
    }

    public IReadOnlyCollection<string> Commands => _handlers.Keys.ToArray();
}
