using System.IO;
using System.Text.Json;

namespace OpenClawDebugger;

internal sealed record ModelSnapshotCacheEntry(
    RemoteModelSnapshot Snapshot,
    DateTimeOffset SavedAtUtc);

/// <summary>
/// 持久化模型清单，不保存 API 密钥或模型响应内容。
/// 每个连接作用域使用独立哈希键，避免切换服务器时串用清单。
/// </summary>
internal sealed class ModelSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, ModelSnapshotCacheEntry> _entries;

    public ModelSnapshotStore(string privateDirectory)
    {
        Directory.CreateDirectory(privateDirectory);
        _path = Path.Combine(privateDirectory, "model-cache.json");
        _entries = Load();
    }

    public ModelSnapshotCacheEntry? Get(string scope)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(scope, out var entry) ? entry : null;
        }
    }

    public ModelSnapshotCacheEntry Save(string scope, RemoteModelSnapshot snapshot)
    {
        var entry = new ModelSnapshotCacheEntry(snapshot, DateTimeOffset.UtcNow);
        lock (_gate)
        {
            _entries[scope] = entry;
            // 保留最近的 8 个连接作用域，防止切换过多服务器后缓存文件无限增长。
            if (_entries.Count > 8)
            {
                foreach (var key in _entries
                    .OrderByDescending(item => item.Value.SavedAtUtc)
                    .Skip(8)
                    .Select(item => item.Key)
                    .ToArray())
                    _entries.Remove(key);
            }
            SaveLocked();
        }
        return entry;
    }

    private Dictionary<string, ModelSnapshotCacheEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.Ordinal);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, ModelSnapshotCacheEntry>>(
                File.ReadAllText(_path), JsonOptions);
            return loaded is null
                ? new(StringComparer.Ordinal)
                : new Dictionary<string, ModelSnapshotCacheEntry>(loaded, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private void SaveLocked()
    {
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_entries, JsonOptions));
            File.Move(temporary, _path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
