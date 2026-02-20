using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.System;

/// <summary>
/// Captures a comprehensive snapshot of the system state for diagnostic purposes.
/// </summary>
public class SystemStateSnapshot
{
    public DateTime Timestamp { get; } = DateTime.Now;
    public ProcessSnapshotInfo ProcessInfo { get; } = new();
    public DeviceSnapshotInfo DeviceInfo { get; } = new();
    public NetworkSnapshotInfo NetworkInfo { get; } = new();
    public PowerSnapshotInfo PowerInfo { get; } = new();
    public string AdditionalInfo { get; set; } = string.Empty;

    /// <summary>
    /// Process-related system state information.
    /// </summary>
    public class ProcessSnapshotInfo
    {
        public int TotalProcessCount { get; set; }
        public List<ProcessEntry> TopProcessesByCpu { get; } = new();
        public List<ProcessEntry> TopProcessesByMemory { get; } = new();
        public List<string> NewProcesses { get; } = new();

        public class ProcessEntry
        {
            public string Name { get; set; } = string.Empty;
            public int ProcessId { get; set; }
            public double CpuUsage { get; set; }
            public long MemoryUsageMB { get; set; }
            public string MainWindowTitle { get; set; } = string.Empty;

            public override string ToString()
            {
                return $"{Name} (PID: {ProcessId}, CPU: {CpuUsage:F1}%, Memory: {MemoryUsageMB}MB)";
            }
        }
    }

    /// <summary>
    /// Device-related system state information.
    /// </summary>
    public class DeviceSnapshotInfo
    {
        public List<string> InputDevices { get; } = new();
        public List<string> CameraDevices { get; } = new();
        public List<string> AudioDevices { get; } = new();
        public bool LidOpen { get; set; }
        public bool PowerAcConnected { get; set; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Lid Open: {LidOpen}");
            sb.AppendLine($"AC Power: {PowerAcConnected}");
            sb.AppendLine($"Input Devices: {InputDevices.Count}");
            sb.AppendLine($"Camera Devices: {CameraDevices.Count}");
            sb.AppendLine($"Audio Devices: {AudioDevices.Count}");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Network-related system state information.
    /// </summary>
    public class NetworkSnapshotInfo
    {
        public List<NetworkInterfaceEntry> ActiveInterfaces { get; } = new();
        public long TotalBytesReceived { get; set; }
        public long TotalBytesSent { get; set; }

        public class NetworkInterfaceEntry
        {
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public bool IsOperational { get; set; }
            public string InterfaceType { get; set; } = string.Empty;
            public long BytesReceived { get; set; }
            public long BytesSent { get; set; }

            public override string ToString()
            {
                return $"{Name} ({Description}) - Status: {(IsOperational ? "Up" : "Down")}, Rx: {BytesReceived}, Tx: {BytesSent}";
            }
        }
    }

    /// <summary>
    /// Power-related system state information.
    /// </summary>
    public class PowerSnapshotInfo
    {
        public List<string> ActivePowerRequests { get; } = new();
        public List<string> ActiveWakeTimers { get; } = new();
        public string LastWakeSource { get; set; } = string.Empty;
        public int BatteryLevel { get; set; }
        public bool BatteryCharging { get; set; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Last Wake Source: {LastWakeSource}");
            sb.AppendLine($"Battery: {BatteryLevel}%, {(BatteryCharging ? "Charging" : "Discharging")}");
            sb.AppendLine($"Active Power Requests: {ActivePowerRequests.Count}");
            sb.AppendLine($"Active Wake Timers: {ActiveWakeTimers.Count}");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Captures a comprehensive system state snapshot.
    /// </summary>
    public static SystemStateSnapshot Capture()
    {
        var snapshot = new SystemStateSnapshot();

        try
        {
            CaptureProcessInfo(snapshot.ProcessInfo);
            CaptureDeviceInfo(snapshot.DeviceInfo);
            CaptureNetworkInfo(snapshot.NetworkInfo);
            CapturePowerInfo(snapshot.PowerInfo);
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[SystemStateSnapshot] Error capturing snapshot: {ex.Message}");

            snapshot.AdditionalInfo = $"Error during capture: {ex.Message}";
        }

        return snapshot;
    }

    private static void CaptureProcessInfo(ProcessSnapshotInfo info)
    {
        try
        {
            var processes = Process.GetProcesses();
            info.TotalProcessCount = processes.Length;

            // Get top processes by CPU
            var totalProcessorTime = new Dictionary<int, TimeSpan>();
            foreach (var proc in processes)
            {
                try
                {
                    totalProcessorTime[proc.Id] = proc.TotalProcessorTime;
                }
                catch { /* Ignore processes we can't access */ }
            }

            // Wait a bit to get CPU usage
            global::System.Threading.Thread.Sleep(100);

            var cpuUsage = new Dictionary<int, double>();
            foreach (var proc in processes)
            {
                try
                {
                    if (totalProcessorTime.ContainsKey(proc.Id))
                    {
                        var current = proc.TotalProcessorTime;
                        var previous = totalProcessorTime[proc.Id];
                        var cpu = (current - previous).TotalSeconds / Environment.ProcessorCount * 100;
                        cpuUsage[proc.Id] = cpu;
                    }
                }
                catch { /* Ignore */ }
            }

            info.TopProcessesByCpu.AddRange(
                processes
                .Where(p => cpuUsage.ContainsKey(p.Id))
                .OrderByDescending(p => cpuUsage[p.Id])
                .Take(10)
                .Select(p => new ProcessSnapshotInfo.ProcessEntry
                {
                    Name = p.ProcessName,
                    ProcessId = p.Id,
                    CpuUsage = cpuUsage[p.Id],
                    MemoryUsageMB = p.WorkingSet64 / 1024 / 1024,
                    MainWindowTitle = p.MainWindowTitle ?? string.Empty
                })
                .ToList());

            // Get top processes by memory
            info.TopProcessesByMemory.AddRange(
                processes
                .OrderByDescending(p => p.WorkingSet64)
                .Take(10)
                .Select(p => new ProcessSnapshotInfo.ProcessEntry
                {
                    Name = p.ProcessName,
                    ProcessId = p.Id,
                    CpuUsage = cpuUsage.ContainsKey(p.Id) ? cpuUsage[p.Id] : 0,
                    MemoryUsageMB = p.WorkingSet64 / 1024 / 1024,
                    MainWindowTitle = p.MainWindowTitle ?? string.Empty
                })
                .ToList());
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[SystemStateSnapshot] Error capturing process info: {ex.Message}");
        }
    }

    private static void CaptureDeviceInfo(DeviceSnapshotInfo info)
    {
        try
        {
            // Check power status (AC only)
            GetSystemPowerStatus(out var systemStatus);
            info.PowerAcConnected = systemStatus.ACLineStatus == 1;

            // Try to get lid status
            // Note: This requires P/Invoke to GetSystemPowerStatusEx
            info.LidOpen = true; // Default to true, will be enhanced later

            // Input devices (simplified)
            info.InputDevices.AddRange(EnumerateInputDevices());

            // Camera devices (simplified)
            info.CameraDevices.AddRange(EnumerateCameraDevices());

            // Audio devices (simplified)
            info.AudioDevices.AddRange(EnumerateAudioDevices());
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[SystemStateSnapshot] Error capturing device info: {ex.Message}");
        }
    }

    private static void CaptureNetworkInfo(NetworkSnapshotInfo info)
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (var ni in interfaces)
            {
                try
                {
                    var stats = ni.GetIPv4Statistics();
                    info.ActiveInterfaces.Add(new NetworkSnapshotInfo.NetworkInterfaceEntry
                    {
                        Name = ni.Name,
                        Description = ni.Description,
                        IsOperational = ni.OperationalStatus == OperationalStatus.Up,
                        InterfaceType = ni.NetworkInterfaceType.ToString(),
                        BytesReceived = stats.BytesReceived,
                        BytesSent = stats.BytesSent
                    });

                    info.TotalBytesReceived += stats.BytesReceived;
                    info.TotalBytesSent += stats.BytesSent;
                }
                catch { /* Skip interfaces we can't access */ }
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[SystemStateSnapshot] Error capturing network info: {ex.Message}");
        }
    }

    private static void CapturePowerInfo(PowerSnapshotInfo info)
    {
        try
        {
            // Note: This will be enhanced later with actual powercfg calls
            // For now, we'll capture basic power information

            // Battery status
            GetSystemPowerStatus(out var systemStatus);
            info.BatteryLevel = systemStatus.BatteryLifePercent;
            info.BatteryCharging = systemStatus.BatteryFlag == 8 || systemStatus.ACLineStatus == 1;

            // Placeholder for power requests and wake timers
            // These will be populated by calling the existing powercfg methods
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[SystemStateSnapshot] Error capturing power info: {ex.Message}");
        }
    }

    private static List<string> EnumerateInputDevices()
    {
        var devices = new List<string>();

        try
        {
            // Simplified input device enumeration
            // This will be enhanced with P/Invoke to get detailed device info
            devices.Add("Keyboard");
            devices.Add("Mouse");
        }
        catch { }

        return devices;
    }

    private static List<string> EnumerateCameraDevices()
    {
        var devices = new List<string>();

        try
        {
            // Simplified camera device enumeration
            // This will be enhanced with proper device enumeration
            devices.Add("Camera (Enumeration not yet implemented)");
        }
        catch { }

        return devices;
    }

    private static List<string> EnumerateAudioDevices()
    {
        var devices = new List<string>();

        try
        {
            // Simplified audio device enumeration
            // This will be enhanced with proper device enumeration
            devices.Add("Audio Output (Enumeration not yet implemented)");
            devices.Add("Audio Input (Enumeration not yet implemented)");
        }
        catch { }

        return devices;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved1;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    /// <summary>
    /// Converts the snapshot to a detailed string representation.
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== System State Snapshot ===");
        sb.AppendLine($"Timestamp: {Timestamp:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine();

        sb.AppendLine($"--- Process Information ---");
        sb.AppendLine($"Total Processes: {ProcessInfo.TotalProcessCount}");
        sb.AppendLine($"Top 10 by CPU:");
        foreach (var proc in ProcessInfo.TopProcessesByCpu)
        {
            sb.AppendLine($"  {proc}");
        }
        sb.AppendLine($"Top 10 by Memory:");
        foreach (var proc in ProcessInfo.TopProcessesByMemory)
        {
            sb.AppendLine($"  {proc}");
        }
        sb.AppendLine();

        sb.AppendLine($"--- Device Information ---");
        sb.AppendLine(DeviceInfo.ToString());
        sb.AppendLine();

        sb.AppendLine($"--- Network Information ---");
        sb.AppendLine($"Active Interfaces: {NetworkInfo.ActiveInterfaces.Count}");
        sb.AppendLine($"Total Bytes Received: {NetworkInfo.TotalBytesReceived:N0}");
        sb.AppendLine($"Total Bytes Sent: {NetworkInfo.TotalBytesSent:N0}");
        foreach (var ni in NetworkInfo.ActiveInterfaces)
        {
            sb.AppendLine($"  {ni}");
        }
        sb.AppendLine();

        sb.AppendLine($"--- Power Information ---");
        sb.AppendLine(PowerInfo.ToString());
        sb.AppendLine();

        if (!string.IsNullOrEmpty(AdditionalInfo))
        {
            sb.AppendLine($"--- Additional Information ---");
            sb.AppendLine(AdditionalInfo);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Converts the snapshot to a compact string representation for logging.
    /// </summary>
    public string ToCompactString()
    {
        var sb = new StringBuilder();
        sb.Append($"[{Timestamp:HH:mm:ss.fff}] ");
        sb.Append($"Processes: {ProcessInfo.TotalProcessCount}, ");
        sb.Append($"Net: {NetworkInfo.ActiveInterfaces.Count}, ");
        sb.Append($"Power: AC={DeviceInfo.PowerAcConnected}, Batt={PowerInfo.BatteryLevel}%");

        return sb.ToString();
    }
}