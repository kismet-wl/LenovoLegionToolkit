using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.System;

/// <summary>
/// Monitors process activity to detect process-triggered User Presence changes.
/// </summary>
public class ProcessActivityMonitor
{
    private bool _isEnabled = false;
    private readonly object _lock = new();
    private readonly HashSet<int> _trackedProcesses = new();
    private readonly List<ProcessActivityEvent> _activityHistory = new();
    private const int MaxActivityHistory = 100;

    /// <summary>
    /// Represents a process activity event.
    /// </summary>
    public class ProcessActivityEvent
    {
        public DateTime Timestamp { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public string EventType { get; set; } = string.Empty; // "Start", "Stop", "HighCPU", "HighMemory"
        public double CpuUsage { get; set; }
        public long MemoryUsageMB { get; set; }
        public string? AdditionalInfo { get; set; }

        public override string ToString()
        {
            return $"[{Timestamp:HH:mm:ss.fff}] {ProcessName} (PID: {ProcessId}): {EventType} (CPU: {CpuUsage:F1}%, Memory: {MemoryUsageMB}MB)";
        }
    }

    /// <summary>
    /// Gets whether process activity monitoring is currently enabled.
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            lock (_lock)
            {
                return _isEnabled;
            }
        }
    }

    /// <summary>
    /// Starts monitoring process activity.
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_isEnabled)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[ProcessActivityMonitor] Already started");
                return;
            }

            _isEnabled = true;
            _trackedProcesses.Clear();
            _activityHistory.Clear();

            // Initialize tracked processes
            var currentProcesses = Process.GetProcesses();
            foreach (var proc in currentProcesses)
            {
                try
                {
                    _trackedProcesses.Add(proc.Id);
                }
                catch { /* Ignore */ }
            }

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[ProcessActivityMonitor] Started monitoring {_trackedProcesses.Count} processes");
        }
    }

    /// <summary>
    /// Stops monitoring process activity.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!_isEnabled)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[ProcessActivityMonitor] Already stopped");
                return;
            }

            _isEnabled = false;

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[ProcessActivityMonitor] Stopped monitoring");
        }
    }

    /// <summary>
    /// Checks for process activity (new processes, stopped processes, high CPU/memory).
    /// </summary>
    public void CheckActivity()
    {
        lock (_lock)
        {
            if (!_isEnabled)
                return;

            try
            {
                var currentProcesses = Process.GetProcesses();
                var currentProcessIds = new HashSet<int>();

                // Check for new processes
                foreach (var proc in currentProcesses)
                {
                    try
                    {
                        currentProcessIds.Add(proc.Id);

                        if (!_trackedProcesses.Contains(proc.Id))
                        {
                            // New process started
                            var activityEvent = new ProcessActivityEvent
                            {
                                Timestamp = DateTime.Now,
                                ProcessName = proc.ProcessName,
                                ProcessId = proc.Id,
                                EventType = "Start",
                                CpuUsage = 0,
                                MemoryUsageMB = proc.WorkingSet64 / 1024 / 1024,
                                AdditionalInfo = $"Command Line: {GetCommandLine(proc.Id)}"
                            };

                            _activityHistory.Add(activityEvent);
                            _trackedProcesses.Add(proc.Id);

                            if (Log.Instance.IsTraceEnabled)
                                Log.Instance.Trace($"[ProcessActivityMonitor] {activityEvent}");
                        }
                        else
                        {
                            // Check for high CPU/memory usage
                            var cpuUsage = GetCpuUsage(proc);
                            var memoryMB = proc.WorkingSet64 / 1024 / 1024;

                            if (cpuUsage > 50 || memoryMB > 500) // High CPU (>50%) or high memory (>500MB)
                            {
                                var activityEvent = new ProcessActivityEvent
                                {
                                    Timestamp = DateTime.Now,
                                    ProcessName = proc.ProcessName,
                                    ProcessId = proc.Id,
                                    EventType = cpuUsage > 50 ? "HighCPU" : "HighMemory",
                                    CpuUsage = cpuUsage,
                                    MemoryUsageMB = memoryMB
                                };

                                _activityHistory.Add(activityEvent);

                                if (Log.Instance.IsTraceEnabled)
                                    Log.Instance.Trace($"[ProcessActivityMonitor] {activityEvent}");
                            }
                        }
                    }
                    catch { /* Ignore processes we can't access */ }
                }

                // Check for stopped processes
                var stoppedProcesses = _trackedProcesses.Except(currentProcessIds).ToList();
                foreach (var pid in stoppedProcesses)
                {
                    // Find the process name from history
                    var lastEvent = _activityHistory
                        .Where(e => e.ProcessId == pid)
                        .OrderByDescending(e => e.Timestamp)
                        .FirstOrDefault();

                    var processName = lastEvent?.ProcessName ?? "Unknown";

                    var activityEvent = new ProcessActivityEvent
                    {
                        Timestamp = DateTime.Now,
                        ProcessName = processName,
                        ProcessId = pid,
                        EventType = "Stop",
                        CpuUsage = 0,
                        MemoryUsageMB = 0
                    };

                    _activityHistory.Add(activityEvent);
                    _trackedProcesses.Remove(pid);

                    if (Log.Instance.IsTraceEnabled)
                        Log.Instance.Trace($"[ProcessActivityMonitor] {activityEvent}");
                }

                // Trim history
                while (_activityHistory.Count > MaxActivityHistory)
                {
                    _activityHistory.RemoveAt(0);
                }
            }
            catch (Exception ex)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[ProcessActivityMonitor] Error checking activity: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Gets the CPU usage of a process.
    /// </summary>
    private double GetCpuUsage(Process proc)
    {
        try
        {
            var startTime = DateTime.UtcNow;
            var startCpuUsage = proc.TotalProcessorTime;

            global::System.Threading.Thread.Sleep(100);

            var endTime = DateTime.UtcNow;
            var endCpuUsage = proc.TotalProcessorTime;

            var cpuUsedMs = (endCpuUsage - startCpuUsage).TotalMilliseconds;
            var totalMsPassed = (endTime - startTime).TotalMilliseconds;

            var cpuUsageTotal = cpuUsedMs / (totalMsPassed * Environment.ProcessorCount);
            return cpuUsageTotal * 100;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets the command line for a process.
    /// </summary>
    private string GetCommandLine(int processId)
    {
        try
        {
            var proc = Process.GetProcessById(processId);
            using (var searcher = new global::System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}"))
            {
                foreach (global::System.Management.ManagementObject obj in searcher.Get())
                {
                    return obj["CommandLine"]?.ToString() ?? string.Empty;
                }
            }
        }
        catch { }

        return string.Empty;
    }

    /// <summary>
    /// Gets the process activity history.
    /// </summary>
    public List<ProcessActivityEvent> GetActivityHistory()
    {
        lock (_lock)
        {
            return new List<ProcessActivityEvent>(_activityHistory);
        }
    }

    /// <summary>
    /// Gets process activity events from a specific time range.
    /// </summary>
    public List<ProcessActivityEvent> GetActivityHistory(DateTime startTime, DateTime endTime)
    {
        lock (_lock)
        {
            return _activityHistory
                .Where(e => e.Timestamp >= startTime && e.Timestamp <= endTime)
                .ToList();
        }
    }

    /// <summary>
    /// Gets process activity events for a specific process.
    /// </summary>
    public List<ProcessActivityEvent> GetActivityHistory(string processName)
    {
        lock (_lock)
        {
            return _activityHistory
                .Where(e => e.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    /// <summary>
    /// Checks if there was significant process activity in the last N seconds.
    /// </summary>
    public bool WasActivityDetected(int seconds)
    {
        var startTime = DateTime.Now.AddSeconds(-seconds);
        var recentActivity = GetActivityHistory(startTime, DateTime.Now);

        // Check for new processes or high resource usage
        return recentActivity.Any(e =>
            e.EventType == "Start" ||
            e.EventType == "HighCPU" ||
            e.EventType == "HighMemory");
    }

    /// <summary>
    /// Gets processes that are likely to trigger User Presence changes.
    /// </summary>
    public List<string> GetSuspectProcesses()
    {
        lock (_lock)
        {
            var suspects = new List<string>();

            // Process types that might trigger User Presence changes
            var suspectProcessPatterns = new[]
            {
                "System.", "svchost", "RuntimeBroker", "SearchIndexer",
                "WindowsUpdate", "UWP", "BackgroundTaskHost",
                "PushNotification", "SyncHost", "OneDrive",
                "Teams", "Discord", "Zoom", "Skype"
            };

            var recentActivity = GetActivityHistory(DateTime.Now.AddMinutes(-5), DateTime.Now);

            foreach (var pattern in suspectProcessPatterns)
            {
                if (recentActivity.Any(e => e.ProcessName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    suspects.Add(pattern);
                }
            }

            return suspects;
        }
    }

    /// <summary>
    /// Gets a summary of recent process activity.
    /// </summary>
    public string GetSummary()
    {
        lock (_lock)
        {
            if (!_isEnabled)
                return "Process activity monitoring is not enabled";

            if (_activityHistory.Count == 0)
                return "No process activity recorded";

            var summary = $"Process Activity Summary ({_activityHistory.Count} events):\n";

            // Count by event type
            var startCount = _activityHistory.Count(e => e.EventType == "Start");
            var stopCount = _activityHistory.Count(e => e.EventType == "Stop");
            var highCpuCount = _activityHistory.Count(e => e.EventType == "HighCPU");
            var highMemoryCount = _activityHistory.Count(e => e.EventType == "HighMemory");

            summary += $"  New Processes: {startCount}\n";
            summary += $"  Stopped Processes: {stopCount}\n";
            summary += $"  High CPU Events: {highCpuCount}\n";
            summary += $"  High Memory Events: {highMemoryCount}\n";

            // Get suspect processes
            var suspects = GetSuspectProcesses();
            if (suspects.Count > 0)
            {
                summary += $"\nSuspect Processes:\n";
                foreach (var suspect in suspects)
                {
                    summary += $"  - {suspect}\n";
                }
            }

            if (_activityHistory.Count > 0)
            {
                var lastEvent = _activityHistory.Last();
                summary += $"\nLast Event:\n  {lastEvent}";
            }

            return summary;
        }
    }
}
