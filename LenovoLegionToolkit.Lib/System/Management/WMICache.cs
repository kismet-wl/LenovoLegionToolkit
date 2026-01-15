using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.System.Management;

public static class WMICache
{
    private static readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private static readonly Timer _cleanupTimer;
    private static readonly object _lock = new();
    private static long _hits = 0;
    private static long _misses = 0;

    private const int MaxCacheSize = 50;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);

    private record CacheEntry(object Value, DateTime Expiry, DateTime Created);

    static WMICache()
    {
        _cleanupTimer = new Timer(CleanupCallback, null, CleanupInterval, CleanupInterval);
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"WMICache initialized with cleanup interval: {CleanupInterval}");
    }

    public static async Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> factory, TimeSpan? ttl = null)
    {
        var expiry = ttl.HasValue ? DateTime.UtcNow.Add(ttl.Value) : DateTime.MaxValue;

        // 检查缓存
        if (_cache.TryGetValue(key, out var entry) && entry.Expiry > DateTime.UtcNow)
        {
            Interlocked.Increment(ref _hits);
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"WMI Cache HIT: {key}");

            return (T)entry.Value;
        }

        // 缓存未命中，执行工厂方法
        Interlocked.Increment(ref _misses);
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"WMI Cache MISS: {key}");

        var value = await factory().ConfigureAwait(false);

        // 更新缓存
        _cache.AddOrUpdate(key, new CacheEntry(value!, expiry, DateTime.UtcNow), (_, _) => new CacheEntry(value!, expiry, DateTime.UtcNow));

        // 检查缓存大小，如果超过限制则清理最旧的条目
        if (_cache.Count > MaxCacheSize)
        {
            TrimCache();
        }

        return value;
    }

    public static void Clear()
    {
        _cache.Clear();
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
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
        var expiredCount = 0;

        foreach (var kvp in _cache)
        {
            if (kvp.Value.Expiry <= now)
            {
                if (_cache.TryRemove(kvp.Key, out _))
                {
                    expiredCount++;
                }
            }
        }

        if (Log.Instance.IsTraceEnabled && expiredCount > 0)
            Log.Instance.Trace($"WMI Cache expired entries cleared: {expiredCount}");
    }

    /// <summary>
    /// 获取缓存统计信息
    /// </summary>
    public static (int Count, long Hits, long Misses, double HitRate) GetStats()
    {
        var hits = Interlocked.Read(ref _hits);
        var misses = Interlocked.Read(ref _misses);
        var total = hits + misses;
        var hitRate = total > 0 ? (double)hits / total * 100 : 0;

        return (_cache.Count, hits, misses, hitRate);
    }

    /// <summary>
    /// 清理回调 - 清理过期条目和超出大小限制的条目
    /// </summary>
    private static void CleanupCallback(object? state)
    {
        try
        {
            lock (_lock)
            {
                var count = _cache.Count;
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"WMI Cache cleanup: {count} entries");

                // 清理过期条目
                ClearExpired();

                // 如果仍然超过限制，清理最旧的条目
                if (_cache.Count > MaxCacheSize)
                {
                    TrimCache();
                }

                var remainingCount = _cache.Count;
                var stats = GetStats();

                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"WMI Cache cleanup completed: {remainingCount} entries, hit rate: {stats.HitRate:F2}%");
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"WMI Cache cleanup failed", ex);
        }
    }

    /// <summary>
    /// 修剪缓存到最大大小限制（LRU 策略）
    /// </summary>
    private static void TrimCache()
    {
        var count = _cache.Count;
        if (count <= MaxCacheSize)
            return;

        var entriesToRemove = _cache
            .OrderBy(kvp => kvp.Value.Created)
            .Take(count - MaxCacheSize)
            .ToList();

        foreach (var entry in entriesToRemove)
        {
            if (_cache.TryRemove(entry.Key, out _))
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Trimmed WMI Cache entry: {entry.Key}");
            }
        }
    }

    /// <summary>
    /// 停止清理定时器（应用关闭时调用）
    /// </summary>
    public static void Shutdown()
    {
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Shutting down WMI Cache...");

        _cleanupTimer?.Dispose();
        Clear();

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"WMI Cache shut down.");
    }
}