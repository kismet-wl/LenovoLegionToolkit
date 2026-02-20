using System;
using System.Runtime.InteropServices;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.System;

/// <summary>
/// Provides DDC/CI based monitor power control functionality.
/// This allows hardware-level monitor power off which is not affected by Windows power management.
/// </summary>
public static class MonitorPowerControl
{
    private static bool? _isSupported;
    private static PHYSICAL_MONITOR[]? _physicalMonitors;

    // P/Invoke for monitor enumeration
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    /// <summary>
    /// Gets whether DDC/CI monitor power control is supported on this system.
    /// </summary>
    public static bool IsSupported
    {
        get
        {
            if (_isSupported.HasValue)
                return _isSupported.Value;

            _isSupported = CheckSupport();
            return _isSupported.Value;
        }
    }

    /// <summary>
    /// Attempts to turn off the monitor using DDC/CI hardware control.
    /// </summary>
    /// <returns>True if successful, false if DDC/CI is not supported or failed.</returns>
    public static bool TryTurnOffMonitor()
    {
        try
        {
            // Initialize physical monitors if not already done
            if (_physicalMonitors == null || _physicalMonitors.Length == 0)
            {
                if (!InitializePhysicalMonitors())
                {
                    if (Log.Instance.IsTraceEnabled)
                        Log.Instance.Trace($"[MonitorPowerControl] Failed to initialize physical monitors");
                    return false;
                }
            }

            if (_physicalMonitors == null || _physicalMonitors.Length == 0)
                return false;

            // Try to turn off each physical monitor using DDC/CI
            var success = false;
            foreach (var monitor in _physicalMonitors)
            {
                if (monitor.hPhysicalMonitor == IntPtr.Zero)
                    continue;

                // Set VCP code D6 (Power Mode) to value 4 (Off)
                if (DDCApi.SetVCPFeature(monitor.hPhysicalMonitor, VcpCode.DPMSPowerState, (uint)MonitorPowerMode.Off))
                {
                    success = true;
                    if (Log.Instance.IsTraceEnabled)
                        Log.Instance.Trace($"[MonitorPowerControl] Successfully turned off monitor via DDC/CI: {monitor.szPhysicalMonitorDescription}");
                }
                else
                {
                    var error = Marshal.GetLastWin32Error();
                    if (Log.Instance.IsTraceEnabled)
                        Log.Instance.Trace($"[MonitorPowerControl] Failed to set VCP feature for monitor: {monitor.szPhysicalMonitorDescription}, error={error}");
                }
            }

            return success;
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[MonitorPowerControl] Exception in TryTurnOffMonitor: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Attempts to turn on the monitor using DDC/CI hardware control.
    /// </summary>
    /// <returns>True if successful, false if DDC/CI is not supported or failed.</returns>
    public static bool TryTurnOnMonitor()
    {
        try
        {
            if (_physicalMonitors == null || _physicalMonitors.Length == 0)
                return false;

            var success = false;
            foreach (var monitor in _physicalMonitors)
            {
                if (monitor.hPhysicalMonitor == IntPtr.Zero)
                    continue;

                if (DDCApi.SetVCPFeature(monitor.hPhysicalMonitor, VcpCode.DPMSPowerState, (uint)MonitorPowerMode.On))
                {
                    success = true;
                }
            }

            return success;
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[MonitorPowerControl] Exception in TryTurnOnMonitor: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Releases resources used for DDC/CI control.
    /// Should be called when the application exits or monitor control is no longer needed.
    /// </summary>
    public static void Cleanup()
    {
        if (_physicalMonitors != null && _physicalMonitors.Length > 0)
        {
            try
            {
                DDCApi.DestroyPhysicalMonitors((uint)_physicalMonitors.Length, _physicalMonitors);
            }
            catch
            {
                // Ignore cleanup errors
            }
            _physicalMonitors = null;
        }
        _isSupported = null;
    }

    private static bool CheckSupport()
    {
        try
        {
            // Get the primary monitor handle
            var primaryMonitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);

            if (primaryMonitor == IntPtr.Zero)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[MonitorPowerControl] No primary monitor found");
                return false;
            }

            // Check if we can get physical monitors
            if (!DDCApi.GetNumberOfPhysicalMonitorsFromHMONITOR(primaryMonitor, out var count))
            {
                var error = Marshal.GetLastWin32Error();
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[MonitorPowerControl] GetNumberOfPhysicalMonitorsFromHMONITOR failed, error={error}");
                return false;
            }

            if (count == 0)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[MonitorPowerControl] No physical monitors found (likely laptop internal display)");
                return false;
            }

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[MonitorPowerControl] Found {count} physical monitor(s) supporting DDC/CI");

            return true;
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[MonitorPowerControl] Exception in CheckSupport: {ex.Message}");
            return false;
        }
    }

    private static bool InitializePhysicalMonitors()
    {
        try
        {
            // Clean up any existing monitors
            if (_physicalMonitors != null && _physicalMonitors.Length > 0)
            {
                DDCApi.DestroyPhysicalMonitors((uint)_physicalMonitors.Length, _physicalMonitors);
                _physicalMonitors = null;
            }

            // Get the primary monitor handle
            var primaryMonitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);

            if (primaryMonitor == IntPtr.Zero)
                return false;

            // Get number of physical monitors
            if (!DDCApi.GetNumberOfPhysicalMonitorsFromHMONITOR(primaryMonitor, out var count) || count == 0)
                return false;

            // Get physical monitor handles
            _physicalMonitors = new PHYSICAL_MONITOR[count];
            if (!DDCApi.GetPhysicalMonitorsFromHMONITOR(primaryMonitor, count, _physicalMonitors))
            {
                var error = Marshal.GetLastWin32Error();
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[MonitorPowerControl] GetPhysicalMonitorsFromHMONITOR failed, error={error}");
                _physicalMonitors = null;
                return false;
            }

            if (Log.Instance.IsTraceEnabled)
            {
                foreach (var monitor in _physicalMonitors)
                {
                    Log.Instance.Trace($"[MonitorPowerControl] Initialized physical monitor: {monitor.szPhysicalMonitorDescription}");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[MonitorPowerControl] Exception in InitializePhysicalMonitors: {ex.Message}");
            return false;
        }
    }
}