using System;
using global::System.Diagnostics;

namespace LenovoLegionToolkit.Lib.Models;

public class PowerRequestInfo
{
    /// <summary>
    /// Type of power request (DISPLAY, SYSTEM, EXECUTE, AWAYMODE)
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Full path of the process
    /// </summary>
    public string ProcessPath { get; set; } = string.Empty;

    /// <summary>
    /// Process name (filename)
    /// </summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>
    /// Process ID
    /// </summary>
    public int ProcessId { get; set; }

    /// <summary>
    /// Reason for the power request (if available)
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Whether the process is currently suspended
    /// </summary>
    public bool IsSuspended { get; set; }

    /// <summary>
    /// Process handle (for suspend/resume operations)
    /// </summary>
    public Process? Process { get; set; }

    /// <summary>
    /// Display name for UI
    /// </summary>
    public string DisplayName => string.IsNullOrEmpty(Reason) 
        ? ProcessName 
        : $"{ProcessName} ({Reason})";
}