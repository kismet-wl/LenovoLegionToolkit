using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using LenovoLegionToolkit.Lib.System;
using LenovoLegionToolkit.Lib.Utils;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;
using Windows.Win32.UI.WindowsAndMessaging;

namespace LenovoLegionToolkit.Lib.Listeners;

/// <summary>
/// 电池状态监听器 - 使用 Windows 电源事件 API 监听电源状态变化（事件驱动，无轮询）
/// </summary>
public class BatteryStatusListener : NativeWindow, IListener<BatteryStatusListener.ChangedEventArgs>
{
    private const int PBT_POWERSETTINGCHANGE = 0x0001;

    // 电源设置 GUID - 电池百分比变化
    private static readonly Guid GUID_BATTERY_PERCENTAGE_REMAINING = new("A7AD8041-B45A-4CAE-87DA-5A7CE631B63A");
    // 电源设置 GUID - AC/DC 电源切换
    private static readonly Guid GUID_ACDC_POWER_SOURCE = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");

    private HPOWERNOTIFY _batteryPercentageNotificationHandle;
    private HPOWERNOTIFY _acdcPowerSourceNotificationHandle;
    private bool _isStarted;

    public event EventHandler<ChangedEventArgs>? Changed;
    
    // 向后兼容：保留旧的事件名称
    public event EventHandler<BatteryStatusChangedEventArgs>? StatusChanged;

    public class ChangedEventArgs : EventArgs
    {
        public bool IsACOnline { get; set; }
        public int BatteryPercentage { get; set; }
        public int DischargeRate { get; set; }
    }

    // 向后兼容：使用相同的类型别名
    public class BatteryStatusChangedEventArgs : EventArgs
    {
        public bool IsACOnline { get; set; }
        public int BatteryPercentage { get; set; }
        public int DischargeRate { get; set; }
    }

    public Task StartAsync() => Task.Run(() =>
    {
        if (_isStarted)
            return;

        CreateHandle(new CreateParams
        {
            Caption = "LenovoLegionToolkit_BatteryStatusListener",
            Parent = new IntPtr(-3)
        });

        _batteryPercentageNotificationHandle = RegisterPowerSettingNotification(GUID_BATTERY_PERCENTAGE_REMAINING);
        _acdcPowerSourceNotificationHandle = RegisterPowerSettingNotification(GUID_ACDC_POWER_SOURCE);

        _isStarted = true;

        // 获取初始状态
        RefreshBatteryStatus();

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Battery status listener started (event-driven mode).");
    });

    public Task StopAsync() => Task.Run(() =>
    {
        if (!_isStarted)
            return;

        PInvoke.UnregisterPowerSettingNotification(_batteryPercentageNotificationHandle);
        PInvoke.UnregisterPowerSettingNotification(_acdcPowerSourceNotificationHandle);

        _batteryPercentageNotificationHandle = default;
        _acdcPowerSourceNotificationHandle = default;

        ReleaseHandle();

        _isStarted = false;

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Battery status listener stopped.");
    });

    protected override unsafe void WndProc(ref Message m)
    {
        if (m.Msg == PInvoke.WM_POWERBROADCAST && m.WParam == (IntPtr)PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
        {
            ref var str = ref Unsafe.AsRef<POWERBROADCAST_SETTING>((void*)m.LParam);

            if (str.PowerSetting == GUID_BATTERY_PERCENTAGE_REMAINING)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Battery percentage change event received.");

                RefreshBatteryStatus();
            }
            else if (str.PowerSetting == GUID_ACDC_POWER_SOURCE)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"AC/DC power source change event received.");

                RefreshBatteryStatus();
            }
        }

        base.WndProc(ref m);
    }

    private void RefreshBatteryStatus()
    {
        try
        {
            var powerStatus = PInvoke.GetSystemPowerStatus(out var sps);
            if (powerStatus)
            {
                var batteryTag = Battery.GetBatteryTag();
                var status = Battery.GetBatteryStatus(batteryTag);

                var changedArgs = new ChangedEventArgs
                {
                    IsACOnline = sps.ACLineStatus == 1,
                    BatteryPercentage = (int)sps.BatteryLifePercent,
                    DischargeRate = status.Rate
                };

                var statusChangedArgs = new BatteryStatusChangedEventArgs
                {
                    IsACOnline = sps.ACLineStatus == 1,
                    BatteryPercentage = (int)sps.BatteryLifePercent,
                    DischargeRate = status.Rate
                };

                Changed?.Invoke(this, changedArgs);
                StatusChanged?.Invoke(this, statusChangedArgs);
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Failed to refresh battery status.", ex);
        }
    }

    private unsafe HPOWERNOTIFY RegisterPowerSettingNotification(Guid guid)
    {
        return PInvoke.RegisterPowerSettingNotification(new HANDLE(Handle), &guid, 0);
    }
}