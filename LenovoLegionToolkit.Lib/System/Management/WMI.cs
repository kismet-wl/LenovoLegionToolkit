using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.Utils;
using System.Collections;

namespace LenovoLegionToolkit.Lib.System.Management;

public static partial class WMI
{
    private static async Task<bool> ExistsAsync(string scope, FormattableString query)
    {
        try
        {
            var queryFormatted = query.ToString(WMIPropertyValueFormatter.Instance);
            var managementScope = WMIConnectionPool.GetScope(scope);
            var objectQuery = new ObjectQuery(queryFormatted);
            var mos = new ManagementObjectSearcher(managementScope, objectQuery);
            var managementObjects = await mos.GetAsync().ConfigureAwait(false);
            return managementObjects.Any();
        }
        catch
        {
            return false;
        }
    }

    private static LambdaDisposable Listen(string scope, FormattableString query, Action<PropertyDataCollection> handler)
    {
        var queryFormatted = query.ToString(WMIPropertyValueFormatter.Instance);
        var managementScope = WMIConnectionPool.GetScope(scope);
        var eventQuery = new EventQuery(queryFormatted);
        var watcher = new ManagementEventWatcher(managementScope, eventQuery);
        watcher.EventArrived += (_, e) => handler(e.NewEvent.Properties);
        watcher.Start();

        return new LambdaDisposable(() =>
        {
            watcher.Stop();
            watcher.Dispose();
        });
    }

    private static async Task<IEnumerable<T>> ReadAsync<T>(string scope, FormattableString query, Func<PropertyDataCollection, T> converter)
    {
        try
        {
            var queryFormatted = query.ToString(WMIPropertyValueFormatter.Instance);
            var managementScope = WMIConnectionPool.GetScope(scope);
            var objectQuery = new ObjectQuery(queryFormatted);
            var mos = new ManagementObjectSearcher(managementScope, objectQuery);
            var managementObjects = await mos.GetAsync().ConfigureAwait(false);
            var result = managementObjects.Select(mo => mo.Properties).Select(converter);
            return result;
        }
        catch (ManagementException ex)
        {
            throw new ManagementException($"Read failed: {ex.Message} [scope={scope}, query={query}]", ex);
        }
    }

    private static async Task CallAsync(string scope, FormattableString query, string methodName, Dictionary<string, object> methodParams)
    {
        // 调用有返回值版本，传入空 converter 忽略返回值
        await CallAsync(scope, query, methodName, methodParams, _ => (object?)null!).ConfigureAwait(false);
    }

    private static async Task<T> CallAsync<T>(string scope, FormattableString query, string methodName, Dictionary<string, object> methodParams, Func<PropertyDataCollection, T> converter)
    {
        try
        {
            var queryFormatted = query.ToString(WMIPropertyValueFormatter.Instance);

            // 使用 Task.Run 包装同步 WMI 调用，避免异步上下文问题
            return await Task.Run(() =>
            {
                var managementScope = WMIConnectionPool.GetScope(scope);
                var objectQuery = new ObjectQuery(queryFormatted);
                var mos = new ManagementObjectSearcher(managementScope, objectQuery);
                var managementObjects = mos.Get();
                var managementObject = managementObjects.OfType<ManagementObject>().FirstOrDefault() ?? throw new InvalidOperationException("No results in query");

                var mo = (ManagementObject)managementObject;
                var methodParamsObject = mo.GetMethodParameters(methodName);
                foreach (var pair in methodParams)
                    methodParamsObject[pair.Key] = pair.Value;

                var options = new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(30) };
                var resultProperties = mo.InvokeMethod(methodName, methodParamsObject, options);
                var result = converter(resultProperties.Properties);
                return result;
            }).ConfigureAwait(false);
        }
        catch (ManagementException ex)
        {
            var isMethodNotImplemented = ex.ErrorCode == ManagementStatus.MethodNotImplemented ||
                                          ex.Message.Contains("未在任何类中实现") ||
                                          ex.Message.Contains("not implemented", StringComparison.OrdinalIgnoreCase);

            if (isMethodNotImplemented)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"WMI method not implemented: {methodName} [scope={scope}, query={query}]", ex);
                throw new NotSupportedException(
                    $"WMI method '{methodName}' is not implemented on this device. " +
                    "This usually means the hardware does not support this feature. " +
                    $"[scope={scope}, query={query}]", ex);
            }

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"WMI call failed: {methodName} [scope={scope}, query={query}]", ex);
            throw new ManagementException(
                $"WMI call failed: {ex.Message}. [scope={scope}, query={query}, methodName={methodName}]", ex);
        }
    }

    private class WMIPropertyValueFormatter : IFormatProvider, ICustomFormatter
    {
        public static readonly WMIPropertyValueFormatter Instance = new();

        private WMIPropertyValueFormatter() { }

        public object GetFormat(Type? formatType)
        {
            if (formatType == typeof(ICustomFormatter))
                return this;

            throw new InvalidOperationException("Invalid type of formatted");
        }

        public string Format(string? format, object? arg, IFormatProvider? formatProvider)
        {
            var stringArg = arg?.ToString()?.Replace("\\", "\\\\");
            return stringArg ?? string.Empty;
        }
    }
}
