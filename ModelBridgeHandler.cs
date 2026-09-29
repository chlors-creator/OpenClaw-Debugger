using System.Text.Json;

namespace OpenClawDebugger;

/// <summary>模型列表、延迟测试、添加模型和自动排序相关的 WebView 命令。</summary>
public sealed class ModelBridgeHandler
{
    private readonly Func<ModelService?> _service;

    public ModelBridgeHandler(Func<ModelService?> service) => _service = service;

    public void Register(BridgeCommandRouter router)
    {
        router.Map("getModels", async (_, cancellationToken) => await Require().GetAsync(cancellationToken));
        router.Map("testModelLatency", async (payload, cancellationToken) => await Require().TestLatencyAsync(payload, cancellationToken));
        router.Map("setModelOrder", async (payload, cancellationToken) => await Require().SetOrderAsync(payload, cancellationToken));
        router.Map("addModel", async (payload, cancellationToken) => await Require().AddAsync(payload, cancellationToken));
    }

    private ModelService Require() => _service() ?? throw new InvalidOperationException("模型服务未初始化。");
}

