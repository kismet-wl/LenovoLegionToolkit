using System;
using System.Runtime.CompilerServices;

namespace LenovoLegionToolkit.Lib.Utils;

/// <summary>
/// Log 类的扩展方法，简化日志代码
/// </summary>
public static class LogExtensions
{
    /// <summary>
    /// 安全地记录跟踪日志，自动检查 IsTraceEnabled
    /// </summary>
    /// <param name="log">Log 实例</param>
    /// <param name="messageFactory">消息工厂函数，仅在启用跟踪时调用</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TraceSafe(this Log log, Func<FormattableString> messageFactory)
    {
        if (log.IsTraceEnabled && messageFactory != null)
            log.Trace(messageFactory());
    }

    /// <summary>
    /// 安全地记录跟踪日志（带异常），自动检查 IsTraceEnabled
    /// </summary>
    /// <param name="log">Log 实例</param>
    /// <param name="messageFactory">消息工厂函数，仅在启用跟踪时调用</param>
    /// <param name="ex">异常对象</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TraceSafe(this Log log, Func<FormattableString> messageFactory, Exception? ex)
    {
        if (log.IsTraceEnabled && messageFactory != null)
            log.Trace(messageFactory(), ex);
    }
}