using System.Text.Json;

namespace OpenClawDebugger;

public sealed record StoredModelLatency(
    int? LatencyMs,
    bool Success,
    DateTimeOffset TestedAtUtc,
    string? Error);

/// <summary>只保存模型探测结果，不保存提示词、响应内容或任何凭据。</summary>
public sealed class ModelLatencyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, StoredModelLatency> _values;

    public ModelLatencyStore(string privateDirectory)
    {
        Directory.CreateDirectory(privateDirectory);
        _path = Path.Combine(privateDirectory, "model-latency.json");
        _values = Load();
    }

    public StoredModelLatency? Get(string modelId)
    {
        lock (_gate) return _values.TryGetValue(modelId, out var value) ? value : null;
    }

    public IReadOnlyList<RemoteModelLatencyResult> Merge(IReadOnlyList<RemoteModelLatencyResult> results)
    {
        lock (_gate)
        {
            foreach (var result in results)
            {
                if (string.IsNullOrWhiteSpace(result.ModelId)) continue;
                _values[result.ModelId] = new StoredModelLatency(result.LatencyMs, result.Success, result.TestedAtUtc, result.Error);
            }
            SaveLocked();
        }
        return results;
    }

    public RemoteModelSnapshot Apply(RemoteModelSnapshot snapshot)
    {
        RemoteModelInfo? ApplyOne(RemoteModelInfo? model)
        {
            if (model is null) return null;
            var stored = Get(model.Id);
            return stored is null
                ? model
                : model with { LatencyMs = stored.LatencyMs, LastTestedAtUtc = stored.TestedAtUtc, LastError = stored.Error };
        }

        return snapshot with
        {
            Primary = ApplyOne(snapshot.Primary),
            Fallbacks = snapshot.Fallbacks.Select(model => ApplyOne(model)!).ToArray(),
            Available = snapshot.Available.Select(model => ApplyOne(model)!).ToArray()
        };
    }

    private Dictionary<string, StoredModelLatency> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, StoredModelLatency>>(File.ReadAllText(_path), JsonOptions);
            return loaded is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, StoredModelLatency>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveLocked()
    {
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_values, JsonOptions));
            File.Move(temporary, _path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}

