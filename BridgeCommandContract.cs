using System.IO;
using System.Text.Json;

namespace OpenClawDebugger;

public sealed class BridgeCommandContract
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public int ProtocolVersion { get; init; }
    public Dictionary<string, BridgeCommandDefinition> Commands { get; init; } = new(StringComparer.Ordinal);

    public static BridgeCommandContract Load(string path)
    {
        using var stream = File.OpenRead(path);
        var contract = JsonSerializer.Deserialize<BridgeCommandContract>(stream, JsonOptions)
            ?? throw new InvalidDataException("WebView 桥接协议文件为空。");
        if (contract.ProtocolVersion != BridgeProtocol.Version)
            throw new InvalidDataException("桥接协议文件版本与桌面宿主版本不一致。");
        if (contract.Commands.Count == 0)
            throw new InvalidDataException("桥接协议没有注册任何命令。");
        return contract;
    }

    public BridgeCommandDefinition GetCommand(string name) =>
        Commands.TryGetValue(name, out var command)
            ? command
            : throw new InvalidDataException("不支持的界面操作：" + name);

    public void ValidatePayload(string commandName, JsonElement payload)
    {
        var command = GetCommand(commandName);
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("界面操作参数必须是对象：" + commandName);
        foreach (var (name, type) in command.Payload)
        {
            if (!payload.TryGetProperty(name, out var value))
                throw new InvalidDataException($"界面操作缺少参数 {name}：{commandName}");
            var valid = type switch
            {
                "string" => value.ValueKind == JsonValueKind.String,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "number" => value.ValueKind == JsonValueKind.Number,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "array" => value.ValueKind == JsonValueKind.Array,
                "object" => value.ValueKind == JsonValueKind.Object,
                _ => throw new InvalidDataException($"桥接协议中的参数类型无效：{commandName}.{name}")
            };
            if (!valid) throw new InvalidDataException($"界面操作参数类型错误：{commandName}.{name}");
        }
    }

    public void ValidateRegisteredCommands(IEnumerable<string> registered)
    {
        var expected = Commands.Keys
            .Where(command => !string.Equals(command, "cancelOperation", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var actual = registered.ToHashSet(StringComparer.Ordinal);
        if (expected.SetEquals(actual)) return;
        var missing = expected.Except(actual).Order(StringComparer.Ordinal);
        var unexpected = actual.Except(expected).Order(StringComparer.Ordinal);
        throw new InvalidDataException("桥接命令与协议清单不一致。缺少处理器：" + string.Join(", ", missing) +
            "；未登记处理器：" + string.Join(", ", unexpected));
    }
}

public sealed class BridgeCommandDefinition
{
    public string Mode { get; init; } = "local";
    public string? Domain { get; init; }
    public int TimeoutMs { get; init; } = 300000;
    public Dictionary<string, string> Payload { get; init; } = new(StringComparer.Ordinal);
}
