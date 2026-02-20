using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.System;

/// <summary>
/// Monitors network activity to detect network-triggered User Presence changes.
/// </summary>
public class NetworkActivityMonitor
{
    private bool _isEnabled = false;
    private readonly object _lock = new();
    private readonly Dictionary<string, NetworkInterfaceStats> _interfaceStats = new();
    private readonly List<NetworkActivityEvent> _activityHistory = new();
    private const int MaxActivityHistory = 50;

    /// <summary>
    /// Represents a network activity event.
    /// </summary>
    public class NetworkActivityEvent
    {
        public DateTime Timestamp { get; set; }
        public string InterfaceName { get; set; } = string.Empty;
        public long BytesReceivedDelta { get; set; }
        public long BytesSentDelta { get; set; }
        public string EventType { get; set; } = string.Empty; // "Activity", "Connection", "Disconnection"

        public override string ToString()
        {
            return $"[{Timestamp:HH:mm:ss.fff}] {InterfaceName}: {EventType} (Rx: {BytesReceivedDelta}, Tx: {BytesSentDelta})";
        }
    }

    /// <summary>
    /// Stores network interface statistics.
    /// </summary>
    private class NetworkInterfaceStats
    {
        public long BytesReceived { get; set; }
        public long BytesSent { get; set; }
        public DateTime LastUpdate { get; set; }
    }

    /// <summary>
    /// Gets whether network activity monitoring is currently enabled.
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
    /// Starts monitoring network activity.
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_isEnabled)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[NetworkActivityMonitor] Already started");
                return;
            }

            _isEnabled = true;
            _interfaceStats.Clear();
            _activityHistory.Clear();

            // Initialize stats for all interfaces
            UpdateInterfaceStats();

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[NetworkActivityMonitor] Started monitoring");
        }
    }

    /// <summary>
    /// Stops monitoring network activity.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!_isEnabled)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[NetworkActivityMonitor] Already stopped");
                return;
            }

            _isEnabled = false;

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[NetworkActivityMonitor] Stopped monitoring");
        }
    }

    /// <summary>
    /// Updates network interface statistics and checks for activity.
    /// </summary>
    public void CheckActivity()
    {
        lock (_lock)
        {
            if (!_isEnabled)
                return;

            try
            {
                var currentStats = GetCurrentInterfaceStats();

                foreach (var current in currentStats)
                {
                    if (_interfaceStats.ContainsKey(current.Key))
                    {
                        var previous = _interfaceStats[current.Key];

                        // Calculate deltas
                        var rxDelta = current.Value.BytesReceived - previous.BytesReceived;
                        var txDelta = current.Value.BytesSent - previous.BytesSent;

                        // Check for significant activity
                        if (rxDelta > 1024 || txDelta > 1024) // More than 1KB
                        {
                            var activityEvent = new NetworkActivityEvent
                            {
                                Timestamp = DateTime.Now,
                                InterfaceName = current.Key,
                                BytesReceivedDelta = rxDelta,
                                BytesSentDelta = txDelta,
                                EventType = "Activity"
                            };

                            _activityHistory.Add(activityEvent);
                            if (_activityHistory.Count > MaxActivityHistory)
                            {
                                _activityHistory.RemoveAt(0);
                            }

                            if (Log.Instance.IsTraceEnabled)
                                Log.Instance.Trace($"[NetworkActivityMonitor] {activityEvent}");
                        }
                    }
                }

                // Update stored stats
                _interfaceStats.Clear();
                foreach (var stat in currentStats)
                {
                    _interfaceStats[stat.Key] = stat.Value;
                }
            }
            catch (Exception ex)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[NetworkActivityMonitor] Error checking activity: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Gets current network interface statistics.
    /// </summary>
    private Dictionary<string, NetworkInterfaceStats> GetCurrentInterfaceStats()
    {
        var stats = new Dictionary<string, NetworkInterfaceStats>();

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (var ni in interfaces)
            {
                try
                {
                    if (ni.OperationalStatus == OperationalStatus.Up)
                    {
                        var niStats = ni.GetIPv4Statistics();

                        stats[ni.Name] = new NetworkInterfaceStats
                        {
                            BytesReceived = niStats.BytesReceived,
                            BytesSent = niStats.BytesSent,
                            LastUpdate = DateTime.Now
                        };
                    }
                }
                catch { /* Skip interfaces we can't access */ }
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[NetworkActivityMonitor] Error getting interface stats: {ex.Message}");
        }

        return stats;
    }

    /// <summary>
    /// Updates stored interface statistics.
    /// </summary>
    private void UpdateInterfaceStats()
    {
        var currentStats = GetCurrentInterfaceStats();
        _interfaceStats.Clear();

        foreach (var stat in currentStats)
        {
            _interfaceStats[stat.Key] = stat.Value;
        }
    }

    /// <summary>
    /// Gets the network activity history.
    /// </summary>
    public List<NetworkActivityEvent> GetActivityHistory()
    {
        lock (_lock)
        {
            return new List<NetworkActivityEvent>(_activityHistory);
        }
    }

    /// <summary>
    /// Gets network activity events from a specific time range.
    /// </summary>
    public List<NetworkActivityEvent> GetActivityHistory(DateTime startTime, DateTime endTime)
    {
        lock (_lock)
        {
            return _activityHistory
                .Where(e => e.Timestamp >= startTime && e.Timestamp <= endTime)
                .ToList();
        }
    }

    /// <summary>
    /// Checks if there was significant network activity in the last N seconds.
    /// </summary>
    public bool WasActivityDetected(int seconds)
    {
        var startTime = DateTime.Now.AddSeconds(-seconds);
        var recentActivity = GetActivityHistory(startTime, DateTime.Now);

        return recentActivity.Any(e => e.EventType == "Activity");
    }

    /// <summary>
    /// Gets a summary of recent network activity.
    /// </summary>
    public string GetSummary()
    {
        lock (_lock)
        {
            if (!_isEnabled)
                return "Network activity monitoring is not enabled";

            if (_activityHistory.Count == 0)
                return "No network activity recorded";

            var summary = $"Network Activity Summary ({_activityHistory.Count} events):\n";

            // Calculate totals
            var totalRx = _activityHistory.Sum(e => e.BytesReceivedDelta);
            var totalTx = _activityHistory.Sum(e => e.BytesSentDelta);

            summary += $"  Total Bytes Received: {totalRx:N0}\n";
            summary += $"  Total Bytes Sent: {totalTx:N0}\n";

            // Show active interfaces
            var activeInterfaces = _activityHistory
                .Select(e => e.InterfaceName)
                .Distinct()
                .ToList();

            summary += $"  Active Interfaces: {activeInterfaces.Count}\n";

            if (_activityHistory.Count > 0)
            {
                var lastEvent = _activityHistory.Last();
                summary += $"\nLast Event:\n  {lastEvent}";
            }

            return summary;
        }
    }
}