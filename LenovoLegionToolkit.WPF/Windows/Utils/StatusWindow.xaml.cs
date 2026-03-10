using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Controllers;
using LenovoLegionToolkit.Lib.Controllers.GodMode;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.Features;
using LenovoLegionToolkit.Lib.Listeners;
using LenovoLegionToolkit.Lib.System;
using System.Drawing;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Extensions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Common;

namespace LenovoLegionToolkit.WPF.Windows.Utils;

public partial class StatusWindow
{
    private readonly GPUController _gpuController = IoCContainer.Resolve<GPUController>();
    private readonly BatteryStatusListener _batteryStatusListener = IoCContainer.Resolve<BatteryStatusListener>();
    private readonly BatteryFeature _batteryFeature = IoCContainer.Resolve<BatteryFeature>();

    private CancellationTokenSource? _batteryRefreshCts;
    private Task? _batteryRefreshTask;

    private Rectangle? _iconRectangle;

    private readonly struct StatusWindowData(
        Rectangle? iconRectangle,
        PowerModeState? powerModeState,
        string? godModePresetName,
        GPUStatus? gpuStatus,
        BatteryInformation? batteryInformation,
        BatteryState? batteryState,
        bool hasUpdate)
    {
        public Rectangle? IconRectangle { get; } = iconRectangle;
        public PowerModeState? PowerModeState { get; } = powerModeState;
        public string? GodModePresetName { get; } = godModePresetName;
        public GPUStatus? GPUStatus { get; } = gpuStatus;
        public BatteryInformation? BatteryInformation { get; } = batteryInformation;
        public BatteryState? BatteryState { get; } = batteryState;
        public bool HasUpdate { get; } = hasUpdate;
    }

    public static async Task<StatusWindow> CreateAsync(Rectangle? iconRectangle)
    {
        var gpuController = IoCContainer.Resolve<GPUController>();
        await gpuController.ResumeIfPausedAsync();
        return new(await GetStatusWindowDataAsync(iconRectangle));
    }

    private static async Task<StatusWindowData> GetStatusWindowDataAsync(Rectangle? iconRectangle)
    {
        var powerModeFeature = IoCContainer.Resolve<PowerModeFeature>();
        var godModeController = IoCContainer.Resolve<GodModeController>();
        var gpuController = IoCContainer.Resolve<GPUController>();
        var batteryFeature = IoCContainer.Resolve<BatteryFeature>();
        var updateChecker = IoCContainer.Resolve<UpdateChecker>();

        PowerModeState? state = null;
        string? godModePresetName = null;
        GPUStatus? gpuStatus = null;
        BatteryInformation? batteryInformation = null;
        BatteryState? batteryState = null;
        var hasUpdate = false;

        try
        {
            if (await powerModeFeature.IsSupportedAsync())
            {
                state = await powerModeFeature.GetStateAsync();

                if (state == PowerModeState.GodMode)
                    godModePresetName = await godModeController.GetActivePresetNameAsync();
            }
        }
        catch { /* Ignored */ }

        try
        {
            if (gpuController.IsSupported())
            {
                // ��� GPUController ��������ʹ�û���״̬������ˢ�»�ȡ
                if (gpuController.IsStarted)
                    gpuStatus = await gpuController.GetLastKnownStatusAsync();
                else
                    gpuStatus = await gpuController.RefreshNowAsync();
            }
        }
        catch { /* Ignored */ }

        try
        {
            batteryInformation = Battery.GetBatteryInformation();
        }
        catch { /* Ignored */ }

        try
        {
            if (await batteryFeature.IsSupportedAsync())
                batteryState = await batteryFeature.GetStateAsync();

        }
        catch { /* Ignored */ }

        try
        {
            hasUpdate = await updateChecker.CheckAsync(false) is not null;
        }
        catch { /* Ignored */ }

        return new(iconRectangle, state, godModePresetName, gpuStatus, batteryInformation, batteryState, hasUpdate);
    }

    private StatusWindow(StatusWindowData data)
    {
        InitializeComponent();

        _iconRectangle = data.IconRectangle;

        Loaded += StatusWindow_Loaded;
        Closed += StatusWindow_Closed;

        // ���� GPU ״̬�仯�¼�
        _gpuController.Refreshed += OnGPURefreshed;

        // ���ĵ��״̬�仯�¼�
        _batteryStatusListener.Changed += OnBatteryChanged;

        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowBackdropType = BackgroundType.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;

        Focusable = false;
        Topmost = true;
        ExtendsContentIntoTitleBar = true;
        ShowInTaskbar = false;
        ShowActivated = false;

#if DEBUG
        _title.Text += " [DEBUG]";
#else
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        if (version == new Version(0, 0, 1, 0) || version?.Build == 99)
            _title.Text += " [BETA]";
#endif

        if (Log.Instance.IsTraceEnabled)
            _title.Text += "\n[LOGGING ENABLED]";

        RefreshPowerMode(data.PowerModeState, data.GodModePresetName);
        RefreshDiscreteGpu(data.GPUStatus);
        RefreshBattery(data.BatteryInformation, data.BatteryState);
        RefreshUpdate(data.HasUpdate);
    }

    private void StatusWindow_Closed(object? sender, EventArgs e)
    {
        _gpuController.Refreshed -= OnGPURefreshed;
        _batteryStatusListener.Changed -= OnBatteryChanged;

        // ֹͣ�����ѯ����
        _batteryRefreshCts?.Cancel();
        _batteryRefreshCts = null;
        _batteryRefreshTask = null;
    }

    private void OnGPURefreshed(object? sender, GPUStatus e)
    {
        Dispatcher.Invoke(() => RefreshDiscreteGpu(e));
    }

    private void OnBatteryChanged(object? sender, BatteryStatusListener.ChangedEventArgs e)
    {
        Dispatcher.Invoke(async () =>
        {
            try
            {
                var batteryInfo = Battery.GetBatteryInformation();
                var batteryState = await _batteryFeature.GetStateAsync();
                RefreshBattery(batteryInfo, batteryState);
            }
            catch { /* Ignored */ }
        });
    }

    private void StatusWindow_Loaded(object sender, RoutedEventArgs e)
    {
        PositionWindowRelativeToTrayIcon();

        // �������������ѯ����ÿ 2 ��ˢ��һ�Σ�
        StartBatteryRefreshTask();
    }

    private void StartBatteryRefreshTask()
    {
        _batteryRefreshCts = new CancellationTokenSource();
        var token = _batteryRefreshCts.Token;

        _batteryRefreshTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var batteryInfo = Battery.GetBatteryInformation();
                    var batteryState = await _batteryFeature.GetStateAsync();
                    Dispatcher.Invoke(() => RefreshBattery(batteryInfo, batteryState));

                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                }
                catch (OperationCanceledException) { }
                catch { /* Ignored */ }
            }
        }, token);
    }

    private void PositionWindowRelativeToTrayIcon()
    {
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        if (!transform.HasValue)
        {
            Left = 0;
            Top = 0;
            return;
        }

        // 无法获取图标位置时回退到鼠标位置定位
        if (_iconRectangle is null)
        {
            MoveBottomRightEdgeOfWindowToMousePosition();
            return;
        }

        var iconRect = _iconRectangle.Value;
        var screenRectangle = Screen.FromPoint(new System.Drawing.Point(iconRect.Left, iconRect.Top)).WorkingArea;

        // 图标中心点
        var iconCenterX = (iconRect.Left + iconRect.Right) / 2.0;

        // 转换到 WPF 坐标
        var screenLeft = transform.Value.Transform(new System.Windows.Point(screenRectangle.Left, 0)).X;
        var screenRight = transform.Value.Transform(new System.Windows.Point(screenRectangle.Right, 0)).X;
        var iconCenterWpf = transform.Value.Transform(new System.Windows.Point(iconCenterX, 0)).X;

        // 水平：窗口中心对准图标中心
        Left = iconCenterWpf - ActualWidth / 2;

        // 确保不超出屏幕边界
        Left = Math.Max(screenLeft, Left);
        Left = Math.Min(screenRight - ActualWidth, Left);

        // 垂直：根据任务栏位置
        if (iconRect.Bottom >= screenRectangle.Bottom - 10)
        {
            // 任务栏在底部 - 窗口在图标上方
            Top = transform.Value.Transform(new System.Windows.Point(0, iconRect.Top)).Y - ActualHeight - 4;
        }
        else if (iconRect.Top <= screenRectangle.Top + 10)
        {
            // 任务栏在顶部 - 窗口在图标下方
            Top = transform.Value.Transform(new System.Windows.Point(0, iconRect.Bottom)).Y + 4;
        }
        else
        {
            // 侧边任务栏 - 回退到鼠标位置
            MoveBottomRightEdgeOfWindowToMousePosition();
        }
    }

    private void MoveBottomRightEdgeOfWindowToMousePosition()
    {
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        if (!transform.HasValue)
        {
            Left = 0;
            Top = 0;
            return;
        }

        const double offset = 8;

        var mousePoint = Control.MousePosition;
        var screenRectangle = Screen.FromPoint(mousePoint).WorkingArea;

        var mouse = transform.Value.Transform(new System.Windows.Point(mousePoint.X, mousePoint.Y));
        var screen = transform.Value.Transform(new Vector(screenRectangle.Width, screenRectangle.Height));

        if (mouse.X + offset + ActualWidth > screen.X)
            Left = mouse.X - ActualWidth - offset;
        else
            Left = mouse.X + offset;

        if (mouse.Y + offset + ActualHeight > screen.Y)
            Top = mouse.Y - ActualHeight - offset;
        else
            Top = mouse.Y + offset;
    }

    private void RefreshPowerMode(PowerModeState? powerModeState, string? godModePresetName)
    {
        _powerModeValueLabel.Content = powerModeState?.GetDisplayName() ?? "-";
        _powerModeValueIndicator.Fill = powerModeState?.GetSolidColorBrush() ?? new(Colors.Transparent);

        if (powerModeState == PowerModeState.GodMode)
        {
            _powerModePresetValueLabel.Content = godModePresetName ?? "-";

            _powerModePresetLabel.Visibility = Visibility.Visible;
            _powerModePresetValueLabel.Visibility = Visibility.Visible;
        }
        else
        {
            _powerModePresetLabel.Visibility = Visibility.Collapsed;
            _powerModePresetValueLabel.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshDiscreteGpu(GPUStatus? status)
    {
        if (!status.HasValue)
        {
            _gpuGrid.Visibility = Visibility.Collapsed;
            return;
        }

        if (status.Value.State is GPUState.Active or GPUState.MonitorConnected)
        {
            _gpuPowerStateValueLabel.Content = status.Value.PerformanceState ?? "-";

            _gpuActive.Visibility = Visibility.Visible;
            _gpuInactive.Visibility = Visibility.Collapsed;
            _gpuPoweredOff.Visibility = Visibility.Collapsed;
            _gpuPowerStateValue.Visibility = Visibility.Visible;
            _gpuPowerStateValueLabel.Visibility = Visibility.Visible;
        }
        else if (status.Value.State is GPUState.PoweredOff)
        {
            _gpuPowerStateValueLabel.Content = null;

            _gpuActive.Visibility = Visibility.Collapsed;
            _gpuInactive.Visibility = Visibility.Collapsed;
            _gpuPoweredOff.Visibility = Visibility.Visible;
            _gpuPowerStateValue.Visibility = Visibility.Collapsed;
            _gpuPowerStateValueLabel.Visibility = Visibility.Collapsed;
        }
        else
        {
            _gpuPowerStateValueLabel.Content = status.Value.PerformanceState ?? "-";

            _gpuActive.Visibility = Visibility.Collapsed;
            _gpuInactive.Visibility = Visibility.Visible;
            _gpuPoweredOff.Visibility = Visibility.Collapsed;
            _gpuPowerStateValue.Visibility = Visibility.Visible;
            _gpuPowerStateValueLabel.Visibility = Visibility.Visible;
        }

        _gpuGrid.Visibility = Visibility.Visible;
    }

    private void RefreshBattery(BatteryInformation? batteryInformation, BatteryState? batteryState)
    {
        if (!batteryInformation.HasValue || !batteryState.HasValue)
        {
            _batteryIcon.Symbol = SymbolRegular.Battery024;
            _batteryValueLabel.Content = "-";
            _batteryModeValueLabel.Content = "-";
            _batteryDischargeValueLabel.Content = "-";
            _batteryMinDischargeValueLabel.Content = "-";
            _batteryMaxDischargeValueLabel.Content = "-";
            return;
        }

        var symbol = (int)Math.Round(batteryInformation.Value.BatteryPercentage / 10.0) switch
        {
            10 => SymbolRegular.Battery1024,
            9 => SymbolRegular.Battery924,
            8 => SymbolRegular.Battery824,
            7 => SymbolRegular.Battery724,
            6 => SymbolRegular.Battery624,
            5 => SymbolRegular.Battery524,
            4 => SymbolRegular.Battery424,
            3 => SymbolRegular.Battery324,
            2 => SymbolRegular.Battery224,
            1 => SymbolRegular.Battery124,
            _ => SymbolRegular.Battery024,
        };

        if (batteryInformation.Value.IsCharging)
            symbol = batteryState == BatteryState.Conservation ? SymbolRegular.BatterySaver24 : SymbolRegular.BatteryCharge24;

        if (batteryInformation.Value.IsLowBattery)
            _batteryValueLabel.SetResourceReference(ForegroundProperty, "SystemFillColorCautionBrush");

        _batteryIcon.Symbol = symbol;
        _batteryValueLabel.Content = $"{batteryInformation.Value.BatteryPercentage}%";
        _batteryModeValueLabel.Content = batteryState.GetDisplayName();
        _batteryDischargeValueLabel.Content = $"{batteryInformation.Value.DischargeRate / 1000.0:+0.00;-0.00;0.00} W";
        _batteryMinDischargeValueLabel.Content = $"{batteryInformation.Value.MinDischargeRate / 1000.0:+0.00;-0.00;0.00} W";
        _batteryMaxDischargeValueLabel.Content = $"{batteryInformation.Value.MaxDischargeRate / 1000.0:+0.00;-0.00;0.00} W";
    }

    private void RefreshUpdate(bool hasUpdate) => _updateIndicator.Visibility = hasUpdate ? Visibility.Visible : Visibility.Collapsed;
}
