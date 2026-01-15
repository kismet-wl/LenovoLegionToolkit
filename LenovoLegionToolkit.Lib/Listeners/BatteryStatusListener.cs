using System;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.System;
using LenovoLegionToolkit.Lib.Utils;
using Windows.Win32;
using Windows.Win32.System.Power;

namespace LenovoLegionToolkit.Lib.Listeners;

/// <summary>
/// 电池状态监听器 - 使用 Windows API 监听电源状态变化
/// </summary>
public class BatteryStatusListener : IDisposable
{
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMPOWERSTATUSCHANGE = 0x000A;
    private const int PBT_POWERSETTINGCHANGE = 0x0001;
    
    // 电源设置 GUID
    private static readonly Guid GUID_BATTERY_PERCENTAGE_REMAINING = new("A7AD8041-B45A-4CAE-87DA-5A7CE631B63A");
    private static readonly Guid GUID_ACDC_POWER_SOURCE = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    
    public event EventHandler<BatteryStatusChangedEventArgs>? StatusChanged;
    
    public class BatteryStatusChangedEventArgs : EventArgs
    {
        public bool IsACOnline { get; set; }
        public int BatteryPercentage { get; set; }
        public int DischargeRate { get; set; }
    }
    
    public async Task StartAsync()
    {
        Task? listenerTask = null;
        CancellationTokenSource? cts = null;
        
        lock (_lock)
        {
            if (_listenerTask != null)
                return;
            
            cts = new CancellationTokenSource();
            _cts = cts;
        }
        
        listenerTask = Task.Run(() => ListenForPowerChangesAsync(cts.Token));
        
        lock (_lock)
        {
            _listenerTask = listenerTask;
        }
        
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Battery status listener started.");
    }
    
    public async Task StopAsync()
    {
        CancellationTokenSource? cts = null;
        Task? listenerTask = null;
        
        lock (_lock)
        {
            cts = _cts;
            listenerTask = _listenerTask;
            
            if (_cts == null)
                return;
            
            _cts = null;
            _listenerTask = null;
        }
        
        if (cts != null)
            await cts.CancelAsync().ConfigureAwait(false);
        
        if (listenerTask != null)
            await listenerTask.ConfigureAwait(false);
        
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Battery status listener stopped.");
    }
    
    private async Task ListenForPowerChangesAsync(CancellationToken token)
    {
        try
        {
            // 获取初始状态
            var powerStatus = PInvoke.GetSystemPowerStatus(out var sps);
            if (powerStatus)
            {
                var batteryTag = Battery.GetBatteryTag();
                var status = Battery.GetBatteryStatus(batteryTag);
                
                RaiseStatusChanged(
                    sps.ACLineStatus == 1,
                    (int)sps.BatteryLifePercent,
                    status.Rate);
            }
            
            // 持续监听电源状态变化
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                
                // 检查电源状态是否变化
                powerStatus = PInvoke.GetSystemPowerStatus(out var currentSps);
                if (powerStatus)
                {
                    var batteryTag = Battery.GetBatteryTag();
                    var currentStatus = Battery.GetBatteryStatus(batteryTag);
                    
                    RaiseStatusChanged(
                        currentSps.ACLineStatus == 1,
                        (int)currentSps.BatteryLifePercent,
                        currentStatus.Rate);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Battery status listener failed.", ex);
        }
    }
    
    private void RaiseStatusChanged(bool isACOnline, int batteryPercentage, int dischargeRate)
    {
        StatusChanged?.Invoke(this, new BatteryStatusChangedEventArgs
        {
            IsACOnline = isACOnline,
            BatteryPercentage = batteryPercentage,
            DischargeRate = dischargeRate
        });
    }
    
    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}