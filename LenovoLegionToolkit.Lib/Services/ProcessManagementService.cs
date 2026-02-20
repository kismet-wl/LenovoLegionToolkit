using System;
using global::System.Collections.Generic;
using global::System.Diagnostics;
using global::System.Linq;
using global::System.Security.Principal;
using LenovoLegionToolkit.Lib.Models;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.Services;

public class ProcessManagementService : IProcessManagementService
{
    private readonly Log _log = Log.Instance;

    /// <summary>
    /// Check if the application is running as administrator
    /// </summary>
    public bool IsRunningAsAdministrator()
    {
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            if (_log.IsTraceEnabled)
                _log.Trace($"Failed to check administrator status: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Suspend a process by process ID
    /// </summary>
    public bool SuspendProcess(int processId)
    {
        if (!IsRunningAsAdministrator())
        {
            if (_log.IsTraceEnabled)
                _log.Trace($"Cannot suspend process: Not running as administrator");
            return false;
        }

        try
        {
            var process = Process.GetProcessById(processId);

            if (process is null)
                return false;

            // Suspend all threads in the process
            foreach (ProcessThread thread in process.Threads)
            {
                var threadHandle = Native.OpenThread(0x0002, false, (uint)thread.Id); // THREAD_SUSPEND_RESUME = 0x0002
                if (threadHandle != IntPtr.Zero)
                {
                    Native.SuspendThread(threadHandle);
                    Native.CloseHandle(threadHandle);
                }
            }

            if (_log.IsTraceEnabled)
                _log.Trace($"Suspended process: {process.ProcessName} (PID: {processId})");
            return true;
        }
        catch (Exception ex)
        {
            if (_log.IsTraceEnabled)
                _log.Trace($"Failed to suspend process {processId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Resume a process by process ID
    /// </summary>
    public bool ResumeProcess(int processId)
    {
        if (!IsRunningAsAdministrator())
        {
            if (_log.IsTraceEnabled)
                _log.Trace($"Cannot resume process: Not running as administrator");
            return false;
        }

        try
        {
            var process = Process.GetProcessById(processId);

            if (process is null)
                return false;

            // Resume all threads in the process
            foreach (ProcessThread thread in process.Threads)
            {
                var threadHandle = Native.OpenThread(0x0002, false, (uint)thread.Id); // THREAD_SUSPEND_RESUME = 0x0002
                if (threadHandle != IntPtr.Zero)
                {
                    Native.ResumeThread(threadHandle);
                    Native.CloseHandle(threadHandle);
                }
            }

            if (_log.IsTraceEnabled)
                _log.Trace($"Resumed process: {process.ProcessName} (PID: {processId})");
            return true;
        }
        catch (Exception ex)
        {
            if (_log.IsTraceEnabled)
                _log.Trace($"Failed to resume process {processId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Check if a process is suspended
    /// </summary>
    public bool IsProcessSuspended(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            if (process is null)
                return false;

            // Check if process is responding (suspended processes don't respond)
            // This is a simplified check; there might be better ways
            try
            {
                var mainWindowHandle = process.MainWindowHandle;
                return mainWindowHandle == IntPtr.Zero && !process.HasExited;
            }
            catch
            {
                // If we can't get the main window handle, the process might be suspended
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get Process object for a process ID
    /// </summary>
    public Process? GetProcessById(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (Exception ex)
        {
            if (_log.IsTraceEnabled)
                _log.Trace($"Failed to get process {processId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Update suspended status for power requests
    /// </summary>
    public void UpdateSuspendedStatus(List<PowerRequestInfo> requests)
    {
        foreach (var request in requests)
        {
            if (request.ProcessId > 0)
            {
                request.IsSuspended = IsProcessSuspended(request.ProcessId);
                request.Process = GetProcessById(request.ProcessId);
            }
        }
    }

    /// <summary>
    /// Native Windows API declarations for thread management
    /// </summary>
    private static class Native
    {
        [global::System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

        [global::System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern uint SuspendThread(IntPtr hThread);

        [global::System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern uint ResumeThread(IntPtr hThread);

        [global::System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}