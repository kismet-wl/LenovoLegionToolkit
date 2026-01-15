using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.System.Management;

public static class WMICache
{
    private static readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private record CacheEntry(object Value, DateTime Expiry);

    public static async Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> factory, TimeSpan? ttl = null)
    {
        var expiry = ttl.HasValue ? DateTime.UtcNow.Add(ttl.Value) : DateTime.MaxValue;

        // 检查缓存
        if (_cache.TryGetValue(key, out var entry) && entry.Expiry > DateTime.UtcNow)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"WMI Cache HIT: {key}");

            return (T)entry.Value;
        }

        // 缓存未命中，执行工厂方法
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"WMI Cache MISS: {key}");

        var value = await factory().ConfigureAwait(false);

        // 更新缓存
        _cache.AddOrUpdate(key, new CacheEntry(value!, expiry), (_, _) => new CacheEntry(value!, expiry));

        return value;
    }

    public static void Clear()
    {
        _cache.Clear();
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"WMI Cache cleared");
    }

    public static void Clear(string key)
    {
        _cache.TryRemove(key, out _);
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"WMI Cache cleared: {key}");
    }

    public static void ClearExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _cache)
        {
            if (kvp.Value.Expiry <= now)
            {
                _cache.TryRemove(kvp.Key, out _);
            }
        }

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"WMI Cache expired entries cleared");
    }
}