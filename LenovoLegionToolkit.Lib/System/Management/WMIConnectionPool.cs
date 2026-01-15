using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.System.Management;

/// <summary>
/// WMI 连接池，用于重用 WMI 连接，减少连接建立的开销
/// </summary>
public static class WMIConnectionPool
{
    private static readonly ConcurrentDictionary<string, ScopeEntry> _scopes = new();
    private static readonly Timer _cleanupTimer;
    private static readonly object _lock = new();

    private const int MaxConnections = 20;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(30);

    private record ScopeEntry(ManagementScope Scope, DateTime LastAccessed);

    static WMIConnectionPool()
    {
        _cleanupTimer = new Timer(CleanupCallback, null, CleanupInterval, CleanupInterval);
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"WMIConnectionPool initialized with cleanup interval: {CleanupInterval}");
    }

    /// <summary>
    /// 获取或创建 ManagementScope
    /// </summary>
    public static ManagementScope GetScope(string scopePath)
    {
        return _scopes.AddOrUpdate(scopePath,
            path =>
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Creating new ManagementScope for path: {path}");

                var scope = new ManagementScope(path);
                scope.Connect();
                return new ScopeEntry(scope, DateTime.UtcNow);
            },
            (_, existing) =>
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Reusing existing ManagementScope for path: {scopePath}");

                return new ScopeEntry(existing.Scope, DateTime.UtcNow);
            }).Scope;
    }

    /// <summary>
    /// 清理连接池
    /// </summary>
    public static void Clear()
    {
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Clearing WMI connection pool...");

        foreach (var scope in _scopes.Values)
        {
            try
            {
                // ManagementScope 不需要显式 Dispose
            }
            catch { }
        }

        _scopes.Clear();

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"WMI connection pool cleared.");
    }

    /// <summary>
    /// 获取连接池统计信息
    /// </summary>
    public static int GetStats()
    {
        return _scopes.Count;
    }

    /// <summary>
    /// 清理回调 - 使用 LRU 策略清理最久未使用的连接
    /// </summary>
    private static void CleanupCallback(object? state)
    {
        try
        {
            lock (_lock)
            {
                var count = _scopes.Count;
                if (count <= MaxConnections)
                {
                    if (Log.Instance.IsTraceEnabled)
                        Log.Instance.Trace($"WMI connection pool cleanup: {count} connections (within limit)");

                    return;
                }

                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"WMI connection pool cleanup: {count} connections (exceeds limit of {MaxConnections})");

                // 按 LastAccessed 排序，清理最久未使用的连接
                var entriesToRemove = _scopes
                    .OrderBy(kvp => kvp.Value.LastAccessed)
                    .Take(count - MaxConnections)
                    .ToList();

                foreach (var entry in entriesToRemove)
                {
                    if (_scopes.TryRemove(entry.Key, out var scopeEntry))
                    {
                        try
                        {
                            // ManagementScope 不需要显式 Dispose
                        }
                        catch { }

                        if (Log.Instance.IsTraceEnabled)
                            Log.Instance.Trace($"Removed WMI connection: {entry.Key} (last accessed: {scopeEntry.LastAccessed})");
                    }
                }

                var remainingCount = _scopes.Count;
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"WMI connection pool cleanup completed: {remainingCount} connections remaining");
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"WMI connection pool cleanup failed", ex);
        }
    }

    /// <summary>
    /// 停止清理定时器（应用关闭时调用）
    /// </summary>
    public static void Shutdown()
    {
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Shutting down WMI connection pool...");

        _cleanupTimer?.Dispose();
        Clear();

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"WMI connection pool shut down.");
    }
}