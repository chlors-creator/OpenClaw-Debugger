using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenClawDebugger;

/// <summary>模型页的校验、排序、延迟测试和添加模型业务。</summary>
public sealed class ModelService : IDisposable
{
    private readonly IRemoteModelClient _remote;
    private readonly Func<ConnectionSettings> _settings;
    private readonly Func<bool> _isConnected;
    private readonly Action<bool> _setBusy;
    private readonly ModelLatencyStore _latencies;
    private readonly ModelSnapshotStore _snapshotStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheGate = new();
    private RemoteModelSnapshot? _cachedSnapshot;
    private DateTimeOffset _cachedSavedAtUtc;
    private string? _cachedScope;
    private Task<RemoteModelSnapshot>? _refreshTask;
    private CancellationTokenSource _refreshCancellation = new();
    private string? _refreshScope;
    private long _refreshGeneration;
    private bool _disposed;

    public ModelService(
        IRemoteModelClient remote,
        Func<ConnectionSettings> settings,
        Func<bool> isConnected,
        string privateDirectory,
        Action<bool> setBusy)
    {
        _remote = remote;
        _settings = settings;
        _isConnected = isConnected;
        _setBusy = setBusy;
        _latencies = new ModelLatencyStore(privateDirectory);
        _snapshotStore = new ModelSnapshotStore(privateDirectory);
    }

    public async Task<object> GetAsync(JsonElement payload, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var settings = _settings();
        var scope = CreateScopeKey(settings);
        var waitForUpdate = payload.TryGetProperty("waitForUpdate", out var waitValue) &&
            waitValue.ValueKind is JsonValueKind.True;

        if (waitForUpdate)
        {
            var refresh = EnsureRefresh(settings, scope);
            try
            {
                var snapshot = await refresh.WaitAsync(cancellationToken);
                var saved = GetCachedEntry(scope);
                return ToResponse(
                    _latencies.Apply(snapshot),
                    "remote",
                    saved?.SavedAtUtc,
                    false,
                    null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (GetCachedEntry(scope) is not null)
            {
                var cached = GetCachedEntry(scope)!;
                return ToResponse(
                    _latencies.Apply(cached.Snapshot),
                    "cache",
                    cached.SavedAtUtc,
                    false,
                    ex.Message);
            }
        }

        var cachedEntry = GetCachedEntry(scope);
        if (cachedEntry is not null)
        {
            // 返回持久化清单后由前端发起第二个 waitForUpdate 请求。这样缓存可以
            // 立即显示，同时更新请求仍然经过统一的远程操作闸门和忙碌状态管理。
            return ToResponse(
                _latencies.Apply(cachedEntry.Snapshot),
                "cache",
                cachedEntry.SavedAtUtc,
                true,
                null);
        }

        var freshSnapshot = await EnsureRefresh(settings, scope).WaitAsync(cancellationToken);
        var savedEntry = GetCachedEntry(scope);
        return ToResponse(
            _latencies.Apply(freshSnapshot),
            "remote",
            savedEntry?.SavedAtUtc,
            false,
            null);
    }

    public async Task<object> SetOrderAsync(JsonElement payload, CancellationToken cancellationToken = default)
    {
        var (primary, fallbacks) = ParseOrder(payload);
        await EnterAsync(cancellationToken);
        try
        {
            var snapshot = await _remote.SetOrderAsync(_settings(), primary, fallbacks, cancellationToken);
            var cached = CacheSnapshot(CreateScopeKey(_settings()), snapshot);
            return ToResponse(_latencies.Apply(snapshot), "remote", cached.SavedAtUtc, false, null);
        }
        finally { _setBusy(false); _gate.Release(); }
    }

    public async Task<object> TestLatencyAsync(JsonElement payload, CancellationToken cancellationToken = default)
    {
        var models = ParseModels(payload);
        await EnterAsync(cancellationToken);
        try
        {
            var results = await _remote.TestLatencyAsync(_settings(), models, cancellationToken);
            _latencies.Merge(results);
            var snapshot = _latencies.Apply(await _remote.GetSnapshotAsync(_settings(), cancellationToken));
            var cached = CacheSnapshot(CreateScopeKey(_settings()), snapshot);
            return new
            {
                snapshot.Primary,
                snapshot.Fallbacks,
                snapshot.Available,
                snapshot.RetrievedAtUtc,
                cacheSource = "remote",
                cacheSavedAtUtc = cached.SavedAtUtc,
                refreshPending = false,
                refreshError = (string?)null,
                results
            };
        }
        finally { _setBusy(false); _gate.Release(); }
    }

    public async Task<object> AddAsync(JsonElement payload, CancellationToken cancellationToken = default)
    {
        var request = ParseAddRequest(payload);
        await EnterAsync(cancellationToken);
        try
        {
            var snapshot = await _remote.AddModelAsync(_settings(), request, cancellationToken);
            var cached = CacheSnapshot(CreateScopeKey(_settings()), snapshot);
            return ToResponse(_latencies.Apply(snapshot), "remote", cached.SavedAtUtc, false, null);
        }
        finally { _setBusy(false); _gate.Release(); }
    }

    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isConnected()) throw new InvalidOperationException("请先连接服务器，再管理模型。");
        await _gate.WaitAsync(cancellationToken);
        if (_disposed)
        {
            _gate.Release();
            throw new ObjectDisposedException(nameof(ModelService));
        }
        try
        {
            _setBusy(true);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isConnected()) throw new InvalidOperationException("请先连接服务器，再管理模型。");
    }

    private ModelSnapshotCacheEntry? GetCachedEntry(string scope)
    {
        lock (_cacheGate)
        {
            if (_cachedSnapshot is not null && string.Equals(_cachedScope, scope, StringComparison.Ordinal))
                return new ModelSnapshotCacheEntry(_cachedSnapshot, _cachedSavedAtUtc);

            var persisted = _snapshotStore.Get(scope);
            if (persisted is null) return null;
            _cachedSnapshot = persisted.Snapshot;
            _cachedSavedAtUtc = persisted.SavedAtUtc;
            _cachedScope = scope;
            return persisted;
        }
    }

    private ModelSnapshotCacheEntry CacheSnapshot(string scope, RemoteModelSnapshot snapshot)
    {
        var cached = _snapshotStore.Save(scope, snapshot);
        lock (_cacheGate)
        {
            _cachedSnapshot = snapshot;
            _cachedSavedAtUtc = cached.SavedAtUtc;
            _cachedScope = scope;
        }
        return cached;
    }

    private Task<RemoteModelSnapshot> EnsureRefresh(ConnectionSettings settings, string scope)
    {
        lock (_cacheGate)
        {
            if (_refreshTask is not null && string.Equals(_refreshScope, scope, StringComparison.Ordinal))
                return _refreshTask;

            try { _refreshCancellation.Cancel(); } catch (ObjectDisposedException) { }
            try { _refreshCancellation.Dispose(); } catch (ObjectDisposedException) { }
            _refreshCancellation = new CancellationTokenSource();
            _refreshScope = scope;
            var generation = ++_refreshGeneration;
            var task = RefreshAsync(settings, scope, generation, _refreshCancellation.Token);
            _refreshTask = task;
            _ = ObserveRefreshFaultAsync(task);
            return task;
        }
    }

    private async Task<RemoteModelSnapshot> RefreshAsync(
        ConnectionSettings settings,
        string scope,
        long generation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            _setBusy(true);
            try
            {
                var snapshot = await _remote.GetSnapshotAsync(settings, cancellationToken);
                lock (_cacheGate)
                {
                    if (generation != _refreshGeneration || !string.Equals(_refreshScope, scope, StringComparison.Ordinal))
                        throw new OperationCanceledException(cancellationToken);
                }
                CacheSnapshot(scope, snapshot);
                return snapshot;
            }
            finally { _setBusy(false); }
        }
        finally { _gate.Release(); }
    }

    private static async Task ObserveRefreshFaultAsync(Task<RemoteModelSnapshot> task)
    {
        try { await task; } catch { }
    }

    /// <summary>连接切换或服务器配置发生变化时清除内存状态，但保留持久化清单。</summary>
    public void ResetCache()
    {
        CancellationTokenSource cancellation;
        lock (_cacheGate)
        {
            _cachedSnapshot = null;
            _cachedSavedAtUtc = default;
            _cachedScope = null;
            _refreshTask = null;
            _refreshScope = null;
            _refreshGeneration++;
            cancellation = _refreshCancellation;
            _refreshCancellation = new CancellationTokenSource();
        }
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        try { cancellation.Dispose(); } catch (ObjectDisposedException) { }
    }

    private static object ToResponse(
        RemoteModelSnapshot snapshot,
        string cacheSource,
        DateTimeOffset? cacheSavedAtUtc,
        bool refreshPending,
        string? refreshError) => new
    {
        snapshot.Primary,
        snapshot.Fallbacks,
        snapshot.Available,
        snapshot.RetrievedAtUtc,
        cacheSource,
        cacheSavedAtUtc,
        refreshPending,
        refreshError
    };

    private static string CreateScopeKey(ConnectionSettings settings)
    {
        var value = string.Join("\n", new[]
        {
            settings.Target.Trim().ToLowerInvariant(),
            settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            settings.WorkspacePath.Trim(),
            settings.StickersPath.Trim()
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static (string Primary, IReadOnlyList<string> Fallbacks) ParseOrder(JsonElement payload)
    {
        var primary = ReadModelRef(payload, "primary");
        if (!payload.TryGetProperty("fallbacks", out var value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("备选模型顺序必须是数组。");
        var fallbacks = value.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.String) throw new InvalidDataException("备选模型必须是字符串。");
            return ValidateModelRef(item.GetString() ?? "");
        }).ToList();
        if (fallbacks.Count > 20) throw new InvalidDataException("最多只能设置 20 个备选模型。");
        if (fallbacks.Contains(primary, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("当前模型不能同时出现在备选列表中。");
        if (fallbacks.Distinct(StringComparer.OrdinalIgnoreCase).Count() != fallbacks.Count) throw new InvalidDataException("备选模型不能重复。");
        return (primary, fallbacks);
    }

    private static IReadOnlyList<string> ParseModels(JsonElement payload)
    {
        if (!payload.TryGetProperty("models", out var value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("测试模型列表必须是数组。");
        var models = value.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.String) throw new InvalidDataException("测试模型必须是字符串。");
            return ValidateModelRef(item.GetString() ?? "");
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (models.Count is < 1 or > 100) throw new InvalidDataException("一次最多测试 100 个模型。");
        return models;
    }

    private static RemoteModelAddRequest ParseAddRequest(JsonElement payload)
    {
        var modelRef = ReadModelRef(payload, "modelRef");
        var alias = ReadOptional(payload, "alias", 80);
        var displayName = ReadOptional(payload, "displayName", 180);
        var baseUrl = ReadOptional(payload, "baseUrl", 500);
        var apiKey = ReadOptional(payload, "apiKey", 500);
        if (baseUrl.Length > 0 && (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new InvalidDataException("API 地址必须是 http 或 https URL。");
        return new RemoteModelAddRequest(modelRef, alias, displayName, baseUrl, apiKey);
    }

    private static string ReadModelRef(JsonElement payload, string property)
    {
        if (!payload.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("模型引用必须是字符串：" + property);
        return ValidateModelRef(value.GetString() ?? "");
    }

    private static string ValidateModelRef(string value)
    {
        value = value.Trim();
        if (value.Length is < 3 or > 240 || value.Any(char.IsControl) || value.Any(char.IsWhiteSpace) || !value.Contains('/'))
            throw new InvalidDataException("模型引用需要使用 provider/model 格式。");
        var slash = value.IndexOf('/');
        if (slash < 1 || slash == value.Length - 1) throw new InvalidDataException("模型引用需要使用 provider/model 格式。");
        var provider = value[..slash];
        if (provider.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-')))
            throw new InvalidDataException("模型提供商名称包含无效字符。");
        return value;
    }

    private static string ReadOptional(JsonElement payload, string property, int maxLength)
    {
        if (!payload.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return "";
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("参数必须是字符串：" + property);
        var text = value.GetString() ?? "";
        if (text.Length > maxLength || text.Any(char.IsControl)) throw new InvalidDataException("参数过长或包含控制字符：" + property);
        return text.Trim();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetCache();
        lock (_cacheGate)
        {
            _refreshCancellation.Dispose();
        }
        // SemaphoreSlim has no unmanaged state. Keep it alive so an operation
        // cancelled during window shutdown can still release the gate safely.
    }
}

