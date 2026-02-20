using System.Collections.Generic;
using System.Diagnostics;
using LenovoLegionToolkit.Lib.Models;

namespace LenovoLegionToolkit.Lib.Services;

public interface IProcessManagementService
{
    /// <summary>
    /// Check if the application is running as administrator
    /// </summary>
    bool IsRunningAsAdministrator();

    /// <summary>
    /// Suspend a process by process ID
    /// </summary>
    bool SuspendProcess(int processId);

    /// <summary>
    /// Resume a process by process ID
    /// </summary>
    bool ResumeProcess(int processId);

    /// <summary>
    /// Check if a process is suspended
    /// </summary>
    bool IsProcessSuspended(int processId);

    /// <summary>
    /// Get Process object for a process ID
    /// </summary>
    Process? GetProcessById(int processId);

    /// <summary>
    /// Update suspended status for power requests
    /// </summary>
    void UpdateSuspendedStatus(List<PowerRequestInfo> requests);
}