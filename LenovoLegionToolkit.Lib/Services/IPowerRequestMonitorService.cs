using System.Collections.Generic;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Models;

namespace LenovoLegionToolkit.Lib.Services;

public interface IPowerRequestMonitorService
{
    /// <summary>
    /// Get current power requests from powercfg /requests
    /// </summary>
    Task<List<PowerRequestInfo>> GetPowerRequestsAsync();

    /// <summary>
    /// Check if there are any active DISPLAY power requests
    /// </summary>
    Task<bool> HasDisplayPowerRequestsAsync();
}