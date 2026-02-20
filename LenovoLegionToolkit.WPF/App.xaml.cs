#if !DEBUG
using LenovoLegionToolkit.Lib.System;
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Automation;
using LenovoLegionToolkit.Lib.Controllers;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.Features;
using LenovoLegionToolkit.Lib.Features.Hybrid;
using LenovoLegionToolkit.Lib.Features.Hybrid.Notify;
using LenovoLegionToolkit.Lib.Features.PanelLogo;
using LenovoLegionToolkit.Lib.Features.WhiteKeyboardBacklight;
using LenovoLegionToolkit.Lib.Integrations;
using LenovoLegionToolkit.Lib.Listeners;
using LenovoLegionToolkit.Lib.Macro;
using LenovoLegionToolkit.Lib.Services;
using LenovoLegionToolkit.Lib.SoftwareDisabler;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.CLI;
using LenovoLegionToolkit.WPF.Extensions;
using LenovoLegionToolkit.WPF.Pages;
using LenovoLegionToolkit.WPF.Resources;
using LenovoLegionToolkit.WPF.Utils;
using LenovoLegionToolkit.WPF.Windows;
using LenovoLegionToolkit.WPF.Windows.Utils;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using WinFormsApp = System.Windows.Forms.Application;
using WinFormsHighDpiMode = System.Windows.Forms.HighDpiMode;

namespace LenovoLegionToolkit.WPF;

public partial class App
{
    private const string MUTEX_NAME = "LenovoLegionToolkit_Mutex_6efcc882-924c-4cbc-8fec-f45c25696f98";
    private const string EVENT_NAME = "LenovoLegionToolkit_Event_6efcc882-924c-4cbc-8fec-f45c25696f98";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _singleInstanceWaitHandle;

    public new static App Current => (App)Application.Current;

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
#if DEBUG
        if (Debugger.IsAttached)
        {
            Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName)
                .Where(p => p.Id != Environment.ProcessId)
                .ForEach(p =>
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit();
                    }
                    catch (Exception ex)
                    {
                        if (Log.Instance.IsTraceEnabled)
                            Log.Instance.Trace($"[App] Cannot kill process {p.Id}: {ex.Message}");
                    }
                });
        }
#endif

        var flags = new Flags(e.Args);

        Log.Instance.IsTraceEnabled = flags.IsTraceEnabled;

        AppDomain.CurrentDomain.UnhandledException += AppDomain_UnhandledException;

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Flags: {flags}");

        EnsureSingleInstance();

        await LocalizationHelper.SetLanguageAsync(true);

        if (!flags.SkipCompatibilityCheck)
        {
            try
            {
                if (!await CheckBasicCompatibilityAsync())
                    return;
                if (!await CheckCompatibilityAsync())
                    return;
            }
            catch (Exception ex)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Failed to check device compatibility", ex);

                MessageBox.Show(Resource.CompatibilityCheckError_Message, Resource.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(200);
                return;
            }
        }

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Starting... [version={Assembly.GetEntryAssembly()?.GetName().Version}, build={Assembly.GetEntryAssembly()?.GetBuildDateTimeString()}, os={Environment.OSVersion}, dotnet={Environment.Version}]");

        WinFormsApp.SetHighDpiMode(WinFormsHighDpiMode.PerMonitorV2);
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        IoCContainer.Initialize(
            new Lib.IoCModule(),
            new Lib.Automation.IoCModule(),
            new Lib.Macro.IoCModule(),
            new IoCModule()
        );

        IoCContainer.Resolve<HttpClientFactory>().SetProxy(flags.ProxyUrl, flags.ProxyUsername, flags.ProxyPassword, flags.ProxyAllowAllCerts);

        IoCContainer.Resolve<PowerModeFeature>().AllowAllPowerModesOnBattery = flags.AllowAllPowerModesOnBattery;
        IoCContainer.Resolve<RGBKeyboardBacklightController>().ForceDisable = flags.ForceDisableRgbKeyboardSupport;
        IoCContainer.Resolve<SpectrumKeyboardBacklightController>().ForceDisable = flags.ForceDisableSpectrumKeyboardSupport;
        IoCContainer.Resolve<WhiteKeyboardLenovoLightingBacklightFeature>().ForceDisable = flags.ForceDisableLenovoLighting;
        IoCContainer.Resolve<PanelLogoLenovoLightingBacklightFeature>().ForceDisable = flags.ForceDisableLenovoLighting;
        IoCContainer.Resolve<PortsBacklightFeature>().ForceDisable = flags.ForceDisableLenovoLighting;
        IoCContainer.Resolve<IGPUModeFeature>().ExperimentalGPUWorkingMode = flags.ExperimentalGPUWorkingMode;
        IoCContainer.Resolve<DGPUNotify>().ExperimentalGPUWorkingMode = flags.ExperimentalGPUWorkingMode;
        IoCContainer.Resolve<UpdateChecker>().Disable = flags.DisableUpdateChecker;

        AutomationPage.EnableHybridModeAutomation = flags.EnableHybridModeAutomation;

        // 并行执行初始化任务，提升启动速度
        var initializationTasks = new List<Task>
        {
            LogSoftwareStatusAsync(),
            InitPowerModeFeatureAsync(),
            InitBatteryFeatureAsync(),
            InitRgbKeyboardControllerAsync(),
            InitSpectrumKeyboardControllerAsync(),
            InitGpuOverclockControllerAsync(),
            InitHybridModeAsync(),
            InitAutomationProcessorAsync(),
            InitGPUControllerAsync()
        };

        // InitMacroController 是同步方法，单独调用
        InitMacroController();

        // 等待所有异步初始化任务完成
        await Task.WhenAll(initializationTasks).ConfigureAwait(true);  // 改为 true，确保在 UI 线程上继续

        await IoCContainer.Resolve<AIController>().StartIfNeededAsync().ConfigureAwait(true);
        await IoCContainer.Resolve<HWiNFOIntegration>().StartStopIfNeededAsync().ConfigureAwait(true);
        await IoCContainer.Resolve<IpcServer>().StartStopIfNeededAsync().ConfigureAwait(true);
        await IoCContainer.Resolve<BatteryDischargeRateMonitorService>().StartStopIfNeededAsync().ConfigureAwait(true);

#if !DEBUG
        Autorun.Validate();
#endif

        var mainWindow = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            TrayTooltipEnabled = !flags.DisableTrayTooltip,
            DisableConflictingSoftwareWarning = flags.DisableConflictingSoftwareWarning
        };
        MainWindow = mainWindow;

        IoCContainer.Resolve<ThemeManager>().Apply();

        if (flags.Minimized)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Sending MainWindow to tray...");

            mainWindow.WindowState = WindowState.Minimized;
            mainWindow.Show();
            mainWindow.SendToTray();
        }
        else
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Showing MainWindow...");

            mainWindow.Show();
        }

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Start up complete");
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        // Restore mouse wake permissions on exit
        try
        {
            if (IoCContainer.TryResolve<IDevicePowerManagerService>() is { } devicePowerManagerService)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Restoring mouse wake permissions on exit");

                var restoredCount = devicePowerManagerService.RestoreAllMiceWake();
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Restored wake for {restoredCount} devices on exit");
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Failed to restore mouse wake permissions on exit: {ex.Message}");
        }

#if !DEBUG
        try
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Cleaning up WMI resources...");

            LenovoLegionToolkit.Lib.System.Management.WMICache.Shutdown();
            LenovoLegionToolkit.Lib.System.Management.WMIConnectionPool.Shutdown();

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"WMI resources cleaned up.");
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Failed to clean up WMI resources", ex);
        }
#endif

        _singleInstanceMutex?.Close();
    }

    public void RestartMainWindow()
    {
        if (MainWindow is MainWindow mw)
        {
            mw.SuppressClosingEventHandler = true;
            mw.Close();
        }

        var mainWindow = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    public async Task ShutdownAsync()
    {
        try
        {
            if (IoCContainer.TryResolve<AIController>() is { } aiController)
                await aiController.StopAsync();
        }
        catch {  /* Ignored. */ }

        try
        {
            if (IoCContainer.TryResolve<GPUController>() is { } gpuController)
                await gpuController.StopAsync();
        }
        catch {  /* Ignored. */ }

        try
        {
            if (IoCContainer.TryResolve<RGBKeyboardBacklightController>() is { } rgbKeyboardBacklightController)
            {
                if (await rgbKeyboardBacklightController.IsSupportedAsync())
                    await rgbKeyboardBacklightController.SetLightControlOwnerAsync(false);
            }
        }
        catch {  /* Ignored. */ }

        try
        {
            if (IoCContainer.TryResolve<SpectrumKeyboardBacklightController>() is { } spectrumKeyboardBacklightController)
            {
                if (await spectrumKeyboardBacklightController.IsSupportedAsync())
                    await spectrumKeyboardBacklightController.StopAuroraIfNeededAsync();
            }
        }
        catch {  /* Ignored. */ }

        try
        {
            if (IoCContainer.TryResolve<NativeWindowsMessageListener>() is { } nativeMessageWindowListener)
            {
                await nativeMessageWindowListener.StopAsync();
            }
        }
        catch {  /* Ignored. */ }

        try
        {
            if (IoCContainer.TryResolve<SessionLockUnlockListener>() is { } sessionLockUnlockListener)
            {
                await sessionLockUnlockListener.StopAsync();
            }
        }
        catch { /* Ignored. */ }

        try
        {
            if (IoCContainer.TryResolve<HWiNFOIntegration>() is { } hwinfoIntegration)
            {
                await hwinfoIntegration.StopAsync();
            }
        }
        catch { /* Ignored. */ }

        try
        {
            if (IoCContainer.TryResolve<IpcServer>() is { } ipcServer)
            {
                await ipcServer.StopAsync();
            }
        }
        catch { /* Ignored. */ }

        try
        {
            if (IoCContainer.TryResolve<BatteryDischargeRateMonitorService>() is { } batteryDischargeMon)
            {
                await batteryDischargeMon.StopAsync();
            }
        }
        catch { /* Ignored. */ }

        Shutdown();
    }

    private void AppDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;

        Log.Instance.ErrorReport("AppDomain_UnhandledException", exception ?? new Exception($"Unknown exception caught: {e.ExceptionObject}"));
        Log.Instance.Trace($"Unhandled exception occurred.", exception);

        MessageBox.Show(string.Format(Resource.UnexpectedException, exception?.ToStringDemystified() ?? "Unknown exception."),
            "Application Domain Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(100);
    }

    private void Application_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Instance.ErrorReport("Application_DispatcherUnhandledException", e.Exception);
        Log.Instance.Trace($"Unhandled exception occurred.", e.Exception);

        MessageBox.Show(string.Format(Resource.UnexpectedException, e.Exception.ToStringDemystified()),
            "Application Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(101);
    }

    private async Task<bool> CheckBasicCompatibilityAsync()
    {
        var isCompatible = await Compatibility.CheckBasicCompatibilityAsync();
        if (isCompatible)
            return true;

        MessageBox.Show(Resource.IncompatibleDevice_Message, Resource.AppName, MessageBoxButton.OK, MessageBoxImage.Error);

        Shutdown(201);
        return false;
    }

    private async Task<bool> CheckCompatibilityAsync()
    {
        var (isCompatible, mi) = await Compatibility.IsCompatibleAsync();
        if (isCompatible)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Compatibility check passed. [Vendor={mi.Vendor}, Model={mi.Model}, MachineType={mi.MachineType}, BIOS={mi.BiosVersion}]");
            return true;
        }

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Incompatible system detected. [Vendor={mi.Vendor}, Model={mi.Model}, MachineType={mi.MachineType}, BIOS={mi.BiosVersion}]");

        var unsupportedWindow = new UnsupportedWindow(mi);
        unsupportedWindow.Show();

        var result = await unsupportedWindow.ShouldContinue;
        if (result)
        {
            Log.Instance.IsTraceEnabled = true;

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Compatibility check OVERRIDE. [Vendor={mi.Vendor}, Model={mi.Model}, MachineType={mi.MachineType}, version={Assembly.GetEntryAssembly()?.GetName().Version}, build={Assembly.GetEntryAssembly()?.GetBuildDateTimeString() ?? string.Empty}]");
            return true;
        }

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Shutting down... [Vendor={mi.Vendor}, Model={mi.Model}, MachineType={mi.MachineType}]");

        Shutdown(202);
        return false;
    }

    private void EnsureSingleInstance()
    {
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Checking for other instances...");

        _singleInstanceMutex = new Mutex(true, MUTEX_NAME, out var isOwned);
        _singleInstanceWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, EVENT_NAME);

        if (!isOwned)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Another instance running, closing...");

            _singleInstanceWaitHandle.Set();
            Shutdown();
            return;
        }

        new Thread(() =>
        {
            while (_singleInstanceWaitHandle.WaitOne())
            {
                Current.Dispatcher.BeginInvoke(async () =>
                {
                    if (Current.MainWindow is { } window)
                    {
                        if (Log.Instance.IsTraceEnabled)
                            Log.Instance.Trace($"Another instance started, bringing this one to front instead...");

                        window.BringToForeground();
                    }
                    else
                    {
                        if (Log.Instance.IsTraceEnabled)
                            Log.Instance.Trace($"!!! PANIC !!! This instance is missing main window. Shutting down.");

                        await ShutdownAsync();
                    }
                });
            }
        })
        {
            IsBackground = true
        }.Start();
    }

    private static async Task LogSoftwareStatusAsync()
    {
        if (!Log.Instance.IsTraceEnabled)
            return;

        var vantageStatus = await IoCContainer.Resolve<VantageDisabler>().GetStatusAsync();
        Log.Instance.Trace($"Vantage status: {vantageStatus}");

        var legionZoneStatus = await IoCContainer.Resolve<LegionZoneDisabler>().GetStatusAsync();
        Log.Instance.Trace($"LegionZone status: {legionZoneStatus}");

        var fnKeysStatus = await IoCContainer.Resolve<FnKeysDisabler>().GetStatusAsync();
        Log.Instance.Trace($"FnKeys status: {fnKeysStatus}");
    }

    private static async Task InitHybridModeAsync()
    {
        try
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Initializing hybrid mode...");

            var feature = IoCContainer.Resolve<HybridModeFeature>();
            await feature.EnsureDGPUEjectedIfNeededAsync();
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Couldn't initialize hybrid mode.", ex);
        }
    }

    private static async Task InitAutomationProcessorAsync()
    {
        try
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Initializing automation processor...");

            var automationProcessor = IoCContainer.Resolve<AutomationProcessor>();
            await automationProcessor.InitializeAsync();
            automationProcessor.RunOnStartup();
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Couldn't initialize automation processor.", ex);
        }
    }

    private static async Task InitPowerModeFeatureAsync()
    {
        try
        {
            var feature = IoCContainer.Resolve<PowerModeFeature>();
            if (await feature.IsSupportedAsync())
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Ensuring god mode state is applied...");

                await feature.EnsureGodModeStateIsAppliedAsync();
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Couldn't ensure god mode state.", ex);
        }

        try
        {
            var feature = IoCContainer.Resolve<PowerModeFeature>();
            if (await feature.IsSupportedAsync())
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Ensuring correct power plan is set...");

                await feature.EnsureCorrectWindowsPowerSettingsAreSetAsync();
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Couldn't ensure correct power plan.", ex);
        }
    }

    private static async Task InitBatteryFeatureAsync()
    {
        try
        {
            var feature = IoCContainer.Resolve<BatteryFeature>();
            if (await feature.IsSupportedAsync())
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Ensuring correct battery mode is set...");

                await feature.EnsureCorrectBatteryModeIsSetAsync();
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Couldn't ensure correct battery mode.", ex);
        }
    }

    private static async Task InitRgbKeyboardControllerAsync()
    {
        try
        {
            var controller = IoCContainer.Resolve<RGBKeyboardBacklightController>();
            if (await controller.IsSupportedAsync())
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Setting light control owner and restoring preset...");

                await controller.SetLightControlOwnerAsync(true, true);
            }
            else
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"RGB keyboard is not supported.");
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Couldn't set light control owner or current preset.", ex);
        }
    }

    private static async Task InitSpectrumKeyboardControllerAsync()
    {
        try
        {
            var controller = IoCContainer.Resolve<SpectrumKeyboardBacklightController>();
            if (await controller.IsSupportedAsync())
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Starting Aurora if needed...");

                var result = await controller.StartAuroraIfNeededAsync();
                if (result)
                {
                    if (Log.Instance.IsTraceEnabled)
                        Log.Instance.Trace($"Aurora started.");
                }
                else
                {
                    if (Log.Instance.IsTraceEnabled)
                        Log.Instance.Trace($"Aurora not needed.");
                }
            }
            else
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Spectrum keyboard is not supported.");
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Couldn't start Aurora if needed.", ex);
        }
    }

    private static async Task InitGpuOverclockControllerAsync()
    {
        try
        {
            var controller = IoCContainer.Resolve<GPUOverclockController>();
            if (await controller.IsSupportedAsync())
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Ensuring GPU overclock is applied...");

                var result = await controller.EnsureOverclockIsAppliedAsync();
                if (result)
                {
                    if (Log.Instance.IsTraceEnabled)
                        Log.Instance.Trace($"GPU overclock applied.");
                }
                else
                {
                    if (Log.Instance.IsTraceEnabled)
                        Log.Instance.Trace($"GPU overclock not needed.");
                }
            }
            else
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"GPU overclock is not supported.");
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Couldn't overclock GPU.", ex);
        }
    }

    private static void InitMacroController()
    {
        var controller = IoCContainer.Resolve<MacroController>();
        controller.Start();
    }

    private static async Task InitGPUControllerAsync()
    {
        try
        {
            var controller = IoCContainer.Resolve<GPUController>();
            if (controller.IsSupported())
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Starting GPU controller...");

                await controller.StartAsync();

                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"GPU controller started.");
            }
            else
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"GPU controller is not supported.");
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Couldn't start GPU controller.", ex);
        }
    }
}
