using System;
using global::System.Collections.Generic;
using global::System.Diagnostics;
using global::System.IO;
using global::System.Linq;
using global::System.Text;
using global::System.Text.RegularExpressions;
using global::System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Models;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.Services;

public class PowerRequestMonitorService : IPowerRequestMonitorService
{
    private readonly Log _log = Log.Instance;

    /// <summary>
    /// Get current power requests from powercfg /requests
    /// </summary>
    public async Task<List<PowerRequestInfo>> GetPowerRequestsAsync()
    {
        var requests = new List<PowerRequestInfo>();

        try
        {
            var output = await GetPowerRequestsOutputAsync().ConfigureAwait(false);
            requests = ParsePowerRequests(output);
        }
        catch (Exception ex)
        {
            if (_log.IsTraceEnabled)
                _log.Trace($"Failed to get power requests: {ex.Message}");
        }

        return requests;
    }

    /// <summary>
    /// Check if there are any active DISPLAY power requests
    /// </summary>
    public async Task<bool> HasDisplayPowerRequestsAsync()
    {
        var requests = await GetPowerRequestsAsync().ConfigureAwait(false);
        return requests.Any(r => r.Type.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Get power requests output from powercfg /requests command
    /// </summary>
    private static async Task<string> GetPowerRequestsOutputAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            Arguments = "/requests",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = global::System.Text.Encoding.UTF8
        };

        using var proc = new Process();
        proc.StartInfo = psi;
        proc.Start();

        var output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var error = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);

        await Task.Run(() => proc.WaitForExit(5000)).ConfigureAwait(false);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"powercfg /requests failed: {error}");

        return output;
    }

    /// <summary>
    /// Parse powercfg /requests output
    /// </summary>
    private static List<PowerRequestInfo> ParsePowerRequests(string output)
    {
        var requests = new List<PowerRequestInfo>();
        var lines = output.Split('\n');

        string? currentType = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Detect power request type (DISPLAY, SYSTEM, EXECUTE, AWAYMODE)
            if (trimmed.StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("SYSTEM", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("EXECUTE", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("AWAYMODE", StringComparison.OrdinalIgnoreCase))
            {
                currentType = trimmed.Split(':')[0].Trim();
                continue;
            }

            // Parse process line
            if (currentType != null && trimmed.Contains("[PROCESS]"))
            {
                var request = ParseProcessLine(trimmed, currentType);
                if (request != null)
                    requests.Add(request);
            }
        }

        return requests;
    }

    /// <summary>
    /// Parse a single process line from powercfg output
    /// </summary>
    private static PowerRequestInfo? ParseProcessLine(string line, string type)
    {
        try
        {
            // Example formats:
            // [PROCESS] \Device\HarddiskVolume3\Program Files\PowerToys\PowerToys.exe (Awake)
            // [PROCESS] \Device\HarddiskVolume2\Windows\System32\svchost.exe (SystemEventsBroker)

            var match = Regex.Match(line, @"\[PROCESS\]\s+(.*?)(?:\s+\((.*?)\))?$");
            if (!match.Success)
                return null;

            var processPath = match.Groups[1].Value.Trim();
            var reason = match.Groups[2].Value.Trim();

            // Convert device path to normal path
            if (processPath.StartsWith("\\Device\\HarddiskVolume"))
            {
                processPath = ConvertDevicePathToNormalPath(processPath);
            }

            if (string.IsNullOrEmpty(processPath))
                return null;

            var processName = Path.GetFileName(processPath);
            var processId = GetProcessIdByName(processName);

            return new PowerRequestInfo
            {
                Type = type,
                ProcessPath = processPath,
                ProcessName = processName,
                ProcessId = processId,
                Reason = reason,
                IsSuspended = false
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Convert device path (e.g., \Device\HarddiskVolume3\...) to normal path (e.g., C:\...)
    /// </summary>
    private static string ConvertDevicePathToNormalPath(string devicePath)
    {
        try
        {
            // This is a simplified approach
            // In a real implementation, we would use WMI or other methods to get the correct drive letter
            // For now, we'll just try to extract the path part after the device volume

            var match = Regex.Match(devicePath, @"\\Device\\HarddiskVolume\d+\\(.*)");
            if (match.Success)
            {
                var relativePath = match.Groups[1].Value;

                // Try common drive letters
                foreach (var drive in DriveInfo.GetDrives())
                {
                    var testPath = Path.Combine(drive.Name.TrimEnd('\\'), relativePath);
                    if (File.Exists(testPath))
                        return testPath;
                }
            }

            return devicePath;
        }
        catch
        {
            return devicePath;
        }
    }

    /// <summary>
    /// Get process ID by process name
    /// </summary>
    private static int GetProcessIdByName(string processName)
    {
        try
        {
            var processes = Process.GetProcessesByName(processName.Replace(".exe", ""));
            return processes.FirstOrDefault()?.Id ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}