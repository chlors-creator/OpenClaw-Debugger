using System.Text.Json;

namespace OpenClawDebugger;

/// <summary>记忆文件相关的 WebView 命令。</summary>
public sealed class MemoryBridgeHandler
{
    private readonly Func<MemoryService?> _service;

    public MemoryBridgeHandler(Func<MemoryService?> service) => _service = service;

    public void Register(BridgeCommandRouter router)
    {
        router.Map("readMemory", async (payload, cancellationToken) => await Require().ReadAsync(payload, cancellationToken));
        router.Map("saveMemory", async (payload, cancellationToken) => await Require().SaveAsync(payload, cancellationToken));
    }

    private MemoryService Require() => _service() ?? throw new InvalidOperationException("记忆服务未初始化。");
}
