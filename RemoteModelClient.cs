using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClawDebugger;

public sealed record RemoteModelInfo(
    string Id,
    string Name,
    string Provider,
    string Status,
    string? Alias = null,
    int? LatencyMs = null,
    DateTimeOffset? LastTestedAtUtc = null,
    string? LastError = null,
    string? LatencyMeasurement = null);

public sealed record RemoteModelLatencyResult(
    string ModelId,
    bool Success,
    int? LatencyMs,
    string? Error,
    DateTimeOffset TestedAtUtc,
    string? LatencyMeasurement = null);

public sealed record RemoteModelSnapshot(
    RemoteModelInfo? Primary,
    IReadOnlyList<RemoteModelInfo> Fallbacks,
    IReadOnlyList<RemoteModelInfo> Available,
    DateTimeOffset RetrievedAtUtc,
    string? ConfigHash = null);

public sealed record RemoteModelFetchResult(RemoteModelSnapshot Snapshot, bool Unchanged);

public sealed record RemoteModelAddRequest(
    string ModelRef,
    string Alias,
    string DisplayName,
    string BaseUrl,
    string ApiKey);

public interface IRemoteModelClient
{
    Task<RemoteModelFetchResult> GetSnapshotAsync(ConnectionSettings settings, string? knownConfigHash = null, CancellationToken cancellationToken = default);
    Task<RemoteModelSnapshot> SetOrderAsync(ConnectionSettings settings, string primary, IReadOnlyList<string> fallbacks, CancellationToken cancellationToken = default);
    Task<RemoteModelSnapshot> AddModelAsync(ConnectionSettings settings, RemoteModelAddRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RemoteModelLatencyResult>> TestLatencyAsync(ConnectionSettings settings, IReadOnlyList<string> modelIds, CancellationToken cancellationToken = default);
}

/// <summary>模型领域客户端。模型读取优先使用服务器常驻 Gateway 查询会话，写入仍由服务器端 OpenClaw 配置命令负责。</summary>
public sealed class RemoteModelClient : IRemoteModelClient
{
    private readonly RemoteOpenClawClient _remote;

    public RemoteModelClient(RemoteOpenClawClient remote) => _remote = remote;

    public async Task<RemoteModelFetchResult> GetSnapshotAsync(
        ConnectionSettings settings,
        string? knownConfigHash = null,
        CancellationToken cancellationToken = default)
    {
        var request = new JsonObject { ["action"] = "models_inventory" };
        if (!string.IsNullOrWhiteSpace(knownConfigHash)) request["knownConfigHash"] = knownConfigHash;
        var response = await _remote.InvokeModelAsync(
            settings,
            request,
            cancellationToken);
        return new RemoteModelFetchResult(
            ParseSnapshot(response),
            response["unchanged"] is JsonValue unchanged && unchanged.TryGetValue<bool>(out var value) && value);
    }

    public async Task<RemoteModelSnapshot> SetOrderAsync(
        ConnectionSettings settings,
        string primary,
        IReadOnlyList<string> fallbacks,
        CancellationToken cancellationToken = default)
    {
        var request = new JsonObject
        {
            ["action"] = "models_set_order",
            ["primary"] = primary,
            ["fallbacks"] = new JsonArray(fallbacks.Select(item => JsonValue.Create(item)).ToArray())
        };
        var response = await _remote.InvokeModelAsync(settings, request, cancellationToken);
        return ParseSnapshot(response);
    }

    public async Task<RemoteModelSnapshot> AddModelAsync(
        ConnectionSettings settings,
        RemoteModelAddRequest request,
        CancellationToken cancellationToken = default)
    {
        var apiKey = request.ApiKey;
        var credentialTarget = WindowsCredentialStore.ModelApiKeyTarget(settings, request.ModelRef);
        if (!string.IsNullOrWhiteSpace(apiKey))
            WindowsCredentialStore.Write(credentialTarget, apiKey);
        else
            apiKey = WindowsCredentialStore.Read(credentialTarget) ?? "";
        var payload = new JsonObject
        {
            ["action"] = "models_add",
            ["modelRef"] = request.ModelRef,
            ["alias"] = request.Alias,
            ["displayName"] = request.DisplayName,
            ["baseUrl"] = request.BaseUrl,
            ["apiKey"] = apiKey
        };
        var response = await _remote.InvokeModelAsync(settings, payload, cancellationToken);
        return ParseSnapshot(response);
    }

    public async Task<IReadOnlyList<RemoteModelLatencyResult>> TestLatencyAsync(
        ConnectionSettings settings,
        IReadOnlyList<string> modelIds,
        CancellationToken cancellationToken = default)
    {
        var request = new JsonObject
        {
            ["action"] = "models_test_latency",
            ["models"] = new JsonArray(modelIds.Select(item => JsonValue.Create(item)).ToArray())
        };
        var response = await _remote.InvokeModelAsync(settings, request, cancellationToken);
        var testedAt = ParseDateTime(GetString(response["testedAtUtc"])) ?? DateTimeOffset.UtcNow;
        var results = new List<RemoteModelLatencyResult>();
        foreach (var item in response["results"]?.AsArray() ?? new JsonArray())
        {
            if (item is not JsonObject row) continue;
            results.Add(new RemoteModelLatencyResult(
                GetString(row["modelId"]) ?? "",
                GetBoolean(row["success"]),
                GetInt(row["latencyMs"]),
                GetString(row["error"]),
                ParseDateTime(GetString(row["testedAtUtc"])) ?? testedAt,
                GetString(row["measurement"])));
        }
        return results;
    }

    private static RemoteModelSnapshot ParseSnapshot(JsonObject response)
    {
        var primary = ParseModel(response["primary"] as JsonObject);
        var fallbacks = ParseModels(response["fallbacks"]?.AsArray());
        var available = ParseModels(response["available"]?.AsArray());
        var retrieved = ParseDateTime(GetString(response["retrievedAtUtc"])) ?? DateTimeOffset.UtcNow;
        return new RemoteModelSnapshot(primary, fallbacks, available, retrieved, GetString(response["configHash"]));
    }

    private static IReadOnlyList<RemoteModelInfo> ParseModels(JsonArray? array)
    {
        if (array is null) return [];
        return array.OfType<JsonObject>().Select(ParseModel).Where(model => model is not null).Cast<RemoteModelInfo>().ToArray();
    }

    private static RemoteModelInfo? ParseModel(JsonObject? node)
    {
        if (node is null) return null;
        var id = GetString(node["id"]) ?? "";
        if (string.IsNullOrWhiteSpace(id)) return null;
        return new RemoteModelInfo(
            id,
            GetString(node["name"]) ?? id,
            GetString(node["provider"]) ?? id.Split('/', 2)[0],
            GetString(node["status"]) ?? "unknown",
            GetString(node["alias"]),
            GetInt(node["latencyMs"]),
            ParseDateTime(GetString(node["lastTestedAtUtc"])),
            GetString(node["lastError"]),
            GetString(node["latencyMeasurement"]));
    }

    private static string? GetString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;

    private static int? GetInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;

    private static bool GetBoolean(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    private static DateTimeOffset? ParseDateTime(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
}

