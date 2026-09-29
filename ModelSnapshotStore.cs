using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenClawDebugger;

internal sealed record ModelSnapshotCacheEntry(
    RemoteModelSnapshot Snapshot,
    DateTimeOffset SavedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int FormatVersion,
    string PayloadSha256);

/// <summary>
/// 持久化模型清单，不保存 API 密钥或模型响应内容。
/// 缓存记录带格式版本、TTL 和内容校验，损坏或过期时会自动清理。
/// </summary>
internal sealed class ModelSnapshotStore
{
    private const int CacheFormatVersion = 1;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
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
            if (!_entries.TryGetValue(scope, out var entry)) return null;
            if (IsValid(entry) && entry.ExpiresAtUtc > DateTimeOffset.UtcNow) return entry;

            // 只要某个连接作用域的记录失效，就移除它；若文件本身无法解析，
            // Load 已经删除整个文件，下一次读取会重新建立干净缓存。
            _entries.Remove(scope);
            SaveLocked();
            return null;
        }
    }

    public ModelSnapshotCacheEntry Save(string scope, RemoteModelSnapshot snapshot)
    {
        var savedAt = DateTimeOffset.UtcNow;
        var entry = new ModelSnapshotCacheEntry(
            snapshot,
            savedAt,
            savedAt + CacheTtl,
            CacheFormatVersion,
            ComputePayloadSha256(snapshot));
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
            if (!File.Exists(_path)) return NewDictionary();
            var loaded = JsonSerializer.Deserialize<Dictionary<string, ModelSnapshotCacheEntry>>(
                File.ReadAllText(_path), JsonOptions);
            if (loaded is null) return NewDictionary();

            var result = new Dictionary<string, ModelSnapshotCacheEntry>(StringComparer.Ordinal);
            foreach (var pair in loaded)
            {
                if (!IsValid(pair.Value) || pair.Value.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                    continue;
                result[pair.Key] = pair.Value;
            }
            if (result.Count != loaded.Count)
            {
                // 旧格式、过期记录或校验不一致的记录不再保留。
                if (result.Count == 0) TryDeleteCacheFile();
                else
                {
                    _entries = result;
                    SaveLocked();
                }
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException)
        {
            TryDeleteCacheFile();
            return NewDictionary();
        }
    }

    private bool IsValid(ModelSnapshotCacheEntry entry)
    {
        if (entry.Snapshot is null || entry.Snapshot.Fallbacks is null || entry.Snapshot.Available is null ||
            entry.FormatVersion != CacheFormatVersion || string.IsNullOrWhiteSpace(entry.PayloadSha256)) return false;
        return string.Equals(entry.PayloadSha256, ComputePayloadSha256(entry.Snapshot), StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputePayloadSha256(RemoteModelSnapshot snapshot)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static Dictionary<string, ModelSnapshotCacheEntry> NewDictionary() =>
        new(StringComparer.Ordinal);

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

    private void TryDeleteCacheFile()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }
}
