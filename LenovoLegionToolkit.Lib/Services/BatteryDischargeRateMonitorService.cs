using System;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Listeners;
using LenovoLegionToolkit.Lib.System;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.Services;

/// <summary>
/// 电池放电率监控服务 - 使用共享的 BatteryStatusListener
/// </summary>
public class BatteryDischargeRateMonitorService
{
    private readonly BatteryStatusListener _batteryStatusListener;
    private bool _isStarted;

    public BatteryDischargeRateMonitorService(BatteryStatusListener batteryStatusListener)
    {
        _batteryStatusListener = batteryStatusListener;
    }

    public async Task StartStopIfNeededAsync()
    {
        if (_isStarted)
            return;

        _batteryStatusListener.Changed += BatteryStatusListener_StatusChanged;
        _isStarted = true;

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Started listening to battery status changes.");
    }

    public async Task StopAsync()
    {
        if (!_isStarted)
            return;

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Stopping...");

        _batteryStatusListener.Changed -= BatteryStatusListener_StatusChanged;
        _isStarted = false;

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Stopped.");
    }

    private void BatteryStatusListener_StatusChanged(object? sender, BatteryStatusListener.ChangedEventArgs e)
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
