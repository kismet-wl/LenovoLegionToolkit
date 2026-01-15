using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.System.Management;

/// <summary>
/// WMI 连接池，用于重用 WMI 连接，减少连接建立的开销
/// </summary>
public static class WMIConnectionPool
{
    private static readonly ConcurrentDictionary<string, ManagementScope> _scopes = new();

    /// <summary>
    /// 获取或创建 ManagementScope
    /// </summary>
    public static ManagementScope GetScope(string scopePath)
    {
        return _scopes.GetOrAdd(scopePath, path =>
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Creating new ManagementScope for path: {path}");

            var scope = new ManagementScope(path);
            scope.Connect();
            return scope;
        });
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
}