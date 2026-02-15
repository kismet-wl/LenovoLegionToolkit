using System;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Listeners;
using LenovoLegionToolkit.Lib.System;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.Services;

/// <summary>
/// 电池放电率监控服务 - 使用 Windows API 事件驱动
/// </summary>
public class BatteryDischargeRateMonitorService
{
    private BatteryStatusListener? _batteryStatusListener;

    public async Task StartStopIfNeededAsync()
    {
        await StopAsync().ConfigureAwait(false);

        if (_batteryStatusListener != null)
            return;

        _batteryStatusListener = new BatteryStatusListener();
        _batteryStatusListener.StatusChanged += BatteryStatusListener_StatusChanged;
        
        await _batteryStatusListener.StartAsync().ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Stopping...");

        if (_batteryStatusListener != null)
        {
            _batteryStatusListener.StatusChanged -= BatteryStatusListener_StatusChanged;
            await _batteryStatusListener.StopAsync().ConfigureAwait(false);
            _batteryStatusListener = null;
        }

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Stopped.");
    }

    private void BatteryStatusListener_StatusChanged(object? sender, BatteryStatusListener.BatteryStatusChangedEventArgs e)
    {
        try
        {
            // 当电池状态变化时更新放电率
            Battery.SetMinMaxDischargeRate();
            
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Battery status changed: AC={e.IsACOnline}, Battery={e.BatteryPercentage}%, Rate={e.DischargeRate}mW");
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Failed to update battery discharge rate.", ex);
        }
    }
}
