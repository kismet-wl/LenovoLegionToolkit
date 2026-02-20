using System;
using System.Diagnostics;

namespace LenovoLegionToolkit.Lib.Utils;

/// <summary>
/// powercfg.exe 命令执行器
/// 提供统一的 powercfg 命令执行接口
/// </summary>
public static class PowerCfgExecutor
{
    private const int DEFAULT_TIMEOUT_MS = 10000; // 10秒默认超时

    /// <summary>
    /// 执行 powercfg 命令
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <param name="timeoutMs">超时时间（毫秒），默认10秒</param>
    /// <returns>命令输出</returns>
    /// <exception cref="TimeoutException">执行超时</exception>
    /// <exception cref="InvalidOperationException">执行失败</exception>
    public static string Execute(string arguments, int timeoutMs = DEFAULT_TIMEOUT_MS)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Verb = "runas", // 请求管理员权限
        };

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();

            // 异步读取输出以避免死锁
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            // 等待进程完成
            if (!process.WaitForExit(timeoutMs))
            {
                process.Kill();
                throw new TimeoutException($"powercfg.exe timed out after {timeoutMs}ms (arguments: {arguments})");
            }

            var output = outputTask.Result;
            var error = errorTask.Result;

            if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
            {
                Log.Instance.TraceSafe(() => $"powercfg exit code {process.ExitCode}: {error}");
            }

            return output;
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            Log.Instance.TraceSafe(() => $"PowerCfgExecutor.Execute failed: {ex.Message}");
            throw new InvalidOperationException($"Failed to execute powercfg: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 尝试执行 powercfg 命令，不抛出异常
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <param name="output">命令输出</param>
    /// <param name="timeoutMs">超时时间（毫秒）</param>
    /// <returns>是否成功</returns>
    public static bool TryExecute(string arguments, out string output, int timeoutMs = DEFAULT_TIMEOUT_MS)
    {
        try
        {
            output = Execute(arguments, timeoutMs);
            return true;
        }
        catch
        {
            output = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// 检查命令输出是否表示成功
    /// </summary>
    /// <param name="output">命令输出</param>
    /// <param name="successKeywords">成功关键词</param>
    /// <returns>是否成功</returns>
    public static bool IsSuccess(string output, params string[] successKeywords)
    {
        if (string.IsNullOrEmpty(output))
            return false;

        foreach (var keyword in successKeywords)
        {
            if (output.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}