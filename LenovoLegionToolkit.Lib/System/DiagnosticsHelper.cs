using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.System;

/// <summary>
/// Provides diagnostic information collection and report generation.
/// </summary>
public class DiagnosticsHelper
{
    /// <summary>
    /// Generates a comprehensive diagnostic report.
    /// </summary>
    public static string GenerateDiagnosticReport(
        UserPresenceTracker? userPresenceTracker,
        ActivityAwarenessMonitor? activityAwarenessMonitor,
        NetworkActivityMonitor? networkActivityMonitor,
        ProcessActivityMonitor? processActivityMonitor,
        EventSequenceAnalyzer? eventSequenceAnalyzer)
    {
        var report = new StringBuilder();
        var timestamp = DateTime.Now;

        report.AppendLine("=== Lenovo Legion Toolkit - Diagnostic Report ===");
        report.AppendLine($"Generated: {timestamp:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine();

        // System Information
        report.AppendLine("--- System Information ---");
        report.AppendLine($"OS: {Environment.OSVersion}");
        report.AppendLine($"Machine: {Environment.MachineName}");
        report.AppendLine($"User: {Environment.UserName}");
        // Process info skipped due to namespace conflict
        report.AppendLine();

        // User Presence History
        if (userPresenceTracker != null)
        {
            report.AppendLine("--- User Presence History ---");
            report.AppendLine(userPresenceTracker.GetSummary());
            report.AppendLine();
        }

        // Sensor Data
        if (activityAwarenessMonitor != null && activityAwarenessMonitor.IsEnabled)
        {
            report.AppendLine("--- Sensor Activity ---");
            report.AppendLine(activityAwarenessMonitor.GetSummary());
            report.AppendLine();
        }

        // Network Activity
        if (networkActivityMonitor != null && networkActivityMonitor.IsEnabled)
        {
            report.AppendLine("--- Network Activity ---");
            report.AppendLine(networkActivityMonitor.GetSummary());
            report.AppendLine();
        }

        // Process Activity
        if (processActivityMonitor != null && processActivityMonitor.IsEnabled)
        {
            report.AppendLine("--- Process Activity ---");
            report.AppendLine(processActivityMonitor.GetSummary());
            report.AppendLine();
        }

        // Event Sequence Analysis
        if (eventSequenceAnalyzer != null)
        {
            report.AppendLine("--- Event Sequence Analysis ---");
            report.AppendLine(eventSequenceAnalyzer.GetSummary());
            report.AppendLine();
        }

        // System State Snapshot
        report.AppendLine("--- Current System State ---");
        try
        {
            var snapshot = SystemStateSnapshot.Capture();
            report.AppendLine(snapshot.ToString());
        }
        catch (Exception ex)
        {
            report.AppendLine($"Error capturing system state: {ex.Message}");
        }
        report.AppendLine();

        // Recommendations
        report.AppendLine("--- Recommendations ---");
        report.AppendLine(GenerateRecommendations(
            userPresenceTracker,
            activityAwarenessMonitor,
            networkActivityMonitor,
            processActivityMonitor,
            eventSequenceAnalyzer));
        report.AppendLine();

        report.AppendLine("=== End of Report ===");

        return report.ToString();
    }

    /// <summary>
    /// Generates recommendations based on diagnostic data.
    /// </summary>
    private static string GenerateRecommendations(
        UserPresenceTracker? userPresenceTracker,
        ActivityAwarenessMonitor? activityAwarenessMonitor,
        NetworkActivityMonitor? networkActivityMonitor,
        ProcessActivityMonitor? processActivityMonitor,
        EventSequenceAnalyzer? eventSequenceAnalyzer)
    {
        var recommendations = new List<string>();

        // Check for User Presence anomalies
        if (userPresenceTracker != null)
        {
            var events = userPresenceTracker.GetRecentEvents(10);
            if (events.Count > 5)
            {
                recommendations.Add("User Presence changes are occurring frequently. Consider checking for:");
                recommendations.Add("  - Sensor sensitivity settings");
                recommendations.Add("  - Background tasks that may trigger presence detection");
                recommendations.Add("  - Power management settings");
            }
        }

        // Check for sensor activity
        if (activityAwarenessMonitor != null && activityAwarenessMonitor.IsEnabled)
        {
            if (activityAwarenessMonitor.WasActivityDetected(60))
            {
                recommendations.Add("Sensor activity detected in the last minute. This may indicate:");
                recommendations.Add("  - Human presence sensor sensitivity");
                recommendations.Add("  - Ambient light sensor activation");
                recommendations.Add("  - Motion sensor trigger");
            }
        }

        // Check for network activity
        if (networkActivityMonitor != null && networkActivityMonitor.IsEnabled)
        {
            if (networkActivityMonitor.WasActivityDetected(60))
            {
                recommendations.Add("Network activity detected in the last minute. This may indicate:");
                recommendations.Add("  - Background downloads or updates");
                recommendations.Add("  - Network-triggered wake events");
                recommendations.Add("  - Cloud sync activities");
            }
        }

        // Check for process activity
        if (processActivityMonitor != null && processActivityMonitor.IsEnabled)
        {
            var suspects = processActivityMonitor.GetSuspectProcesses();
            if (suspects.Count > 0)
            {
                recommendations.Add("Suspect processes detected that may trigger User Presence changes:");
                foreach (var suspect in suspects)
                {
                    recommendations.Add($"  - {suspect}");
                }
                recommendations.Add("Consider adjusting these processes or their power management settings.");
            }
        }

        // Check for anomalies
        if (eventSequenceAnalyzer != null)
        {
            var anomalies = eventSequenceAnalyzer.GetAnomalies();
            if (anomalies.Count > 0)
            {
                recommendations.Add($"Found {anomalies.Count} anomalies in event sequence:");
                foreach (var anomaly in anomalies.OrderByDescending(a => a.ConfidenceScore).Take(5))
                {
                    recommendations.Add($"  - {anomaly.AnomalyType}: {anomaly.Description}");
                    if (!string.IsNullOrEmpty(anomaly.PossibleCause))
                    {
                        recommendations.Add($"    Possible cause: {anomaly.PossibleCause}");
                    }
                }
            }
        }

        if (recommendations.Count == 0)
        {
            recommendations.Add("No specific issues detected. Monitor is behaving normally.");
            recommendations.Add("Continue monitoring for any unusual patterns.");
        }

        return string.Join("\n", recommendations);
    }

    /// <summary>
    /// Exports diagnostic report to a file.
    /// </summary>
    public static string ExportDiagnosticReport(
        UserPresenceTracker? userPresenceTracker,
        ActivityAwarenessMonitor? activityAwarenessMonitor,
        NetworkActivityMonitor? networkActivityMonitor,
        ProcessActivityMonitor? processActivityMonitor,
        EventSequenceAnalyzer? eventSequenceAnalyzer,
        string? directory = null)
    {
        try
        {
            var report = GenerateDiagnosticReport(
                userPresenceTracker,
                activityAwarenessMonitor,
                networkActivityMonitor,
                processActivityMonitor,
                eventSequenceAnalyzer);

            // Determine output directory
            var outputDir = directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "LLT_Diagnostics");

            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            // Generate filename with timestamp
            var filename = $"diagnostic_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            var filepath = Path.Combine(outputDir, filename);

            // Write report
            File.WriteAllText(filepath, report, Encoding.UTF8);

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[DiagnosticsHelper] Diagnostic report exported to: {filepath}");

            return filepath;
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[DiagnosticsHelper] Error exporting diagnostic report: {ex.Message}");

            return string.Empty;
        }
    }

    /// <summary>
    /// Collects basic system diagnostic information.
    /// </summary>
    public static Dictionary<string, string> CollectBasicSystemInfo()
    {
        var info = new Dictionary<string, string>();

        try
        {
            // OS Information
            info["OS"] = Environment.OSVersion.ToString();
            info["MachineName"] = Environment.MachineName;
            info["UserName"] = Environment.UserName;
            info["ProcessorCount"] = Environment.ProcessorCount.ToString();

            // Process Information (skipped due to namespace conflict)
            // TODO: Add process information when namespace conflict is resolved
            info["ProcessName"] = "Skipped";
            info["ProcessId"] = "Skipped";
            info["StartTime"] = "Skipped";
            info["WorkingSet"] = "Skipped";
            info["PrivateMemory"] = "Skipped";

            // .NET Information
            info["DotNetVersion"] = Environment.Version.ToString();
            info["Is64BitProcess"] = Environment.Is64BitProcess.ToString();
            info["Is64BitOperatingSystem"] = Environment.Is64BitOperatingSystem.ToString();

            // Power Information
            try
            {
                // TODO: Add power status collection when available
                info["PowerStatus"] = "Not available";
            }
            catch (Exception ex)
            {
                info["PowerStatus"] = $"Error: {ex.Message}";
            }

            // Display Information
            try
            {
                // TODO: Add display information collection when available
                info["DisplayCount"] = "Not available";
            }
            catch (Exception ex)
            {
                info["DisplayCount"] = $"Error: {ex.Message}";
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[DiagnosticsHelper] Error collecting system info: {ex.Message}");
        }

        return info;
    }

    /// <summary>
    /// Gets a formatted string of basic system information.
    /// </summary>
    public static string GetBasicSystemInfoString()
    {
        var info = CollectBasicSystemInfo();
        var sb = new StringBuilder();

        sb.AppendLine("=== Basic System Information ===");
        foreach (var kvp in info)
        {
            sb.AppendLine($"{kvp.Key}: {kvp.Value}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Analyzes a specific wake event and provides detailed information.
    /// </summary>
    public static string AnalyzeWakeEvent(
        DateTime wakeTime,
        UserPresenceTracker? userPresenceTracker,
        ActivityAwarenessMonitor? activityAwarenessMonitor,
        NetworkActivityMonitor? networkActivityMonitor,
        ProcessActivityMonitor? processActivityMonitor)
    {
        var analysis = new StringBuilder();
        var startTime = wakeTime.AddSeconds(-30);
        var endTime = wakeTime.AddSeconds(30);

        analysis.AppendLine($"=== Wake Event Analysis ===");
        analysis.AppendLine($"Wake Time: {wakeTime:yyyy-MM-dd HH:mm:ss.fff}");
        analysis.AppendLine($"Analysis Window: {startTime:HH:mm:ss.fff} to {endTime:HH:mm:ss.fff}");
        analysis.AppendLine();

        // User Presence events
        if (userPresenceTracker != null)
        {
            var presenceEvents = userPresenceTracker.GetEventsInRange(startTime, endTime);
            analysis.AppendLine($"User Presence Events ({presenceEvents.Count}):");
            foreach (var evt in presenceEvents)
            {
                analysis.AppendLine($"  {evt}");
            }
            analysis.AppendLine();
        }

        // Sensor data
        if (activityAwarenessMonitor != null && activityAwarenessMonitor.IsEnabled)
        {
            var sensorData = activityAwarenessMonitor.GetSensorHistory(startTime, endTime);
            analysis.AppendLine($"Sensor Readings ({sensorData.Count}):");
            foreach (var data in sensorData)
            {
                analysis.AppendLine($"  {data}");
            }
            analysis.AppendLine();
        }

        // Network activity
        if (networkActivityMonitor != null && networkActivityMonitor.IsEnabled)
        {
            var networkEvents = networkActivityMonitor.GetActivityHistory(startTime, endTime);
            analysis.AppendLine($"Network Events ({networkEvents.Count}):");
            foreach (var evt in networkEvents)
            {
                analysis.AppendLine($"  {evt}");
            }
            analysis.AppendLine();
        }

        // Process activity
        if (processActivityMonitor != null && processActivityMonitor.IsEnabled)
        {
            var processEvents = processActivityMonitor.GetActivityHistory(startTime, endTime);
            analysis.AppendLine($"Process Events ({processEvents.Count}):");
            foreach (var evt in processEvents)
            {
                analysis.AppendLine($"  {evt}");
            }
            analysis.AppendLine();
        }

        analysis.AppendLine("=== End of Analysis ===");

        return analysis.ToString();
    }
}
