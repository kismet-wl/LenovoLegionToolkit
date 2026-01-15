using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace LenovoLegionToolkit.Lib.Utils;

public class Log
{
    private static Log? _instance;
    public static Log Instance
    {
        get
        {
            _instance ??= new Log();
            return _instance;
        }
    }

    private readonly object _lock = new();
    private readonly string _folderPath;
    private readonly string _logPath;
    private readonly List<string> _buffer = new();
    private readonly Timer _flushTimer;
    private const int BufferSize = 100;
    private const int FlushIntervalMs = 5000;

    public bool IsTraceEnabled { get; set; }

    public string LogPath => _logPath;

    private Log()
    {
        _folderPath = Path.Combine(Folders.AppData, "log");
        Directory.CreateDirectory(_folderPath);
        _logPath = Path.Combine(_folderPath, $"log_{DateTime.UtcNow:yyyy_MM_dd_HH_mm_ss}.txt");
        
        // 设置定时器，定期刷新缓冲区
        _flushTimer = new Timer(FlushBuffer, null, FlushIntervalMs, FlushIntervalMs);
    }

    public void ErrorReport(string header, Exception ex)
    {
        var errorReportPath = Path.Combine(_folderPath, $"error_{DateTime.UtcNow:yyyy_MM_dd_HH_mm_ss}.txt");
        File.AppendAllLines(errorReportPath, [header, Serialize(ex)]);
    }

    public void Trace(FormattableString message,
        Exception? ex = null,
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int lineNumber = -1,
        [CallerMemberName] string? caller = null)
    {
        if (!IsTraceEnabled)
            return;

        LogInternal(_logPath, message, ex, file, lineNumber, caller);
    }

    private void LogInternal(string path,
        FormattableString message,
        Exception? ex,
        string? file,
        int lineNumber,
        string? caller)
    {
        lock (_lock)
        {
            var lines = new List<string>
            {
                $"[{DateTime.UtcNow:dd/MM/yyyy HH:mm:ss.fff}] [{Environment.CurrentManagedThreadId}] [{Path.GetFileName(file)}#{lineNumber}:{caller}] {message}"
            };
            if (ex is not null)
                lines.Add(Serialize(ex));

#if DEBUG
            foreach (var line in lines)
                Debug.WriteLine(line);
#endif

            // 添加到缓冲区
            _buffer.AddRange(lines);

            // 如果缓冲区达到阈值，立即刷新
            if (_buffer.Count >= BufferSize)
            {
                FlushBuffer();
            }
        }
    }

    private void FlushBuffer(object? state = null)
    {
        lock (_lock)
        {
            if (_buffer.Count == 0)
                return;

            try
            {
                File.AppendAllLines(_logPath, _buffer);
                _buffer.Clear();
            }
            catch
            {
                // 忽略日志写入错误，避免影响主程序
            }
        }
    }

    private static string Serialize(Exception ex) => new StringBuilder()
        .AppendLine("=== Exception ===")
        .AppendLine(ex.ToString())
        .AppendLine()
        .AppendLine("=== Exception demystified ===")
        .AppendLine(ex.ToStringDemystified())
        .ToString();
}
