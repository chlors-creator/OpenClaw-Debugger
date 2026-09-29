using System.Text.Json;

namespace OpenClawDebugger;

public sealed class LoggingBridgeHandler
{
    public void Register(BridgeCommandRouter router)
    {
        router.Map("exportLogs", ExportLogs);
    }

    private static Task<object?> ExportLogs(JsonElement _)
    {
        var store = OperationLogStore.Current ?? throw new InvalidOperationException("操作日志尚未初始化。");
        var path = store.Export();
        return Task.FromResult<object?>(new { path, exportedAt = DateTimeOffset.Now });
    }
}
