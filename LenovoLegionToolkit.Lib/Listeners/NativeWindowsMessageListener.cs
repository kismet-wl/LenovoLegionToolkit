using System;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LenovoLegionToolkit.Lib.Controllers;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.Features;
using LenovoLegionToolkit.Lib.Features.Hybrid.Notify;
using LenovoLegionToolkit.Lib.Messaging;
using LenovoLegionToolkit.Lib.Messaging.Messages;
using LenovoLegionToolkit.Lib.Services;
using LenovoLegionToolkit.Lib.System;
using LenovoLegionToolkit.Lib.Utils;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace LenovoLegionToolkit.Lib.Listeners;

public class NativeWindowsMessageListener : NativeWindow, IListener<NativeWindowsMessageListener.ChangedEventArgs>
{
    public class ChangedEventArgs(NativeWindowsMessage message, object? data = null) : EventArgs
    {
        public NativeWindowsMessage Message { get; } = message;
        public object? Data { get; } = data;
    }

    private readonly IMainThreadDispatcher _mainThreadDispatcher;
    private readonly DGPUNotify _dgpuNotify;
    private readonly SmartFnLockController _smartFnLockController;
    private readonly PowerModeFeature _powerModeFeature;
    private readonly IDevicePowerManagerService _devicePowerManagerService;

    private readonly HOOKPROC _kbProc;
    private readonly HOOKPROC _mouseProc;

    private readonly TaskCompletionSource _isMonitorOnTaskCompletionSource = new();
    private readonly TaskCompletionSource _isLidOpenTaskCompletionSource = new();

    private HDEVNOTIFY _deviceNotificationHandle;
    private HPOWERNOTIFY _consoleDisplayStateNotificationHandle;
    private HPOWERNOTIFY _lidSwitchStateChangeNotificationHandle;
    private HPOWERNOTIFY _powerSavingStateChangeNotificationHandle;
    private HPOWERNOTIFY _userPresenceNotificationHandle;
    private HHOOK _kbHook;
    private HHOOK _mouseHook;

    // Thread-safe state management
    private readonly object _stateLock = new();
    private volatile bool _keepMonitorOff = false;
    private volatile bool _useDDCCI = false;  // Track if DDC/CI was used for last power off
    private volatile bool _autoRecloseEnabled = true;  // Controls whether to auto-reclose monitor when it wakes up
    private PInvokeExtensions.USER_ACTIVITY_PRESENCE _lastUserPresence = PInvokeExtensions.USER_ACTIVITY_PRESENCE.PowerUserNotPresent;
    private string _lastUserInputType = "None";  // Records the last detected user input type
    private DateTime _lastUserInputTypeTimestamp = DateTime.MinValue;  // When _lastUserInputType was last updated
    private global::System.Threading.Timer? _inputMonitorTimer;  // Timer for monitoring user input via GetLastInputInfo

    // Mouse movement filtering fields to prevent screen wake from tiny movements
    private int _lastMouseX;
    private int _lastMouseY;
    private bool _isMousePositionInitialized;
    private const int MOUSE_MOVE_THRESHOLD = 8;  // Minimum pixels to consider as real movement (increased from 5)
    private volatile bool _filterMouseMovementEnabled;    // Enable filtering only when monitor is off
    private DateTime _lastSignificantMoveTime = DateTime.MinValue;
    private const int SIGNIFICANT_MOVE_COOLDOWN_MS = 50;  // Cooldown between significant moves (reduced from 100)

    // Timing constants for input detection
    private const int RAW_INPUT_EVENT_WINDOW_MS = 500;  // Time window for checking recent Raw Input events
    private const int STALE_INPUT_THRESHOLD_SECONDS = 5;  // Seconds before input data is considered stale
    private const int INPUT_MONITOR_INTERVAL_MS = 250;  // Interval for input monitoring timer

    // Diagnostic and monitoring fields
    private readonly UserPresenceTracker _userPresenceTracker = new();
    private readonly ActivityAwarenessMonitor _activityAwarenessMonitor = new();
    private readonly NetworkActivityMonitor _networkActivityMonitor = new();
    private readonly ProcessActivityMonitor _processActivityMonitor = new();
    private readonly EventSequenceAnalyzer _eventSequenceAnalyzer = new();
    private PInvokeExtensions.USER_ACTIVITY_PRESENCE _previousUserPresence = PInvokeExtensions.USER_ACTIVITY_PRESENCE.PowerUserNotPresent;
    
    // Raw Input monitoring fields
    private readonly RawInputMonitor _rawInputMonitor = new();
    private readonly InputValidator _inputValidator;

    public bool IsMonitorOn { get; private set; }
    public bool IsLidOpen { get; private set; }

    public event EventHandler<ChangedEventArgs>? Changed;
    public event EventHandler<bool>? MonitorStateChanged;

    public NativeWindowsMessageListener(IMainThreadDispatcher mainThreadDispatcher, DGPUNotify dgpuNotify, SmartFnLockController smartFnLockController, PowerModeFeature powerModeFeature, IDevicePowerManagerService devicePowerManagerService)
    {
        _mainThreadDispatcher = mainThreadDispatcher;
        _dgpuNotify = dgpuNotify;
        _smartFnLockController = smartFnLockController;
        _powerModeFeature = powerModeFeature;
        _devicePowerManagerService = devicePowerManagerService;

        _kbProc = LowLevelKeyboardProc;
        _mouseProc = LowLevelMouseProc;
        
        // Initialize InputValidator
        _inputValidator = new InputValidator(_rawInputMonitor);

        // Initialize diagnostic monitors
        _ = InitializeDiagnosticMonitorsAsync();
    }

    private async Task InitializeDiagnosticMonitorsAsync()
    {
        try
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[NativeWindowsMessageListener] Initializing diagnostic monitors...");

            // Initialize Activity Awareness Monitor
            await _activityAwarenessMonitor.InitializeAsync().ConfigureAwait(false);

            // Initialize Raw Input Monitor
            var hwnd = Handle;
            if (!_rawInputMonitor.Initialize(hwnd))
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[NativeWindowsMessageListener] Raw Input Monitor initialization failed");
            }

            // Start monitoring
            if (_activityAwarenessMonitor.IsSupported)
            {
                _activityAwarenessMonitor.Start();
            }

            // Start Network and Process monitoring
            _networkActivityMonitor.Start();
            _processActivityMonitor.Start();

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[NativeWindowsMessageListener] Diagnostic monitors initialized successfully");
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[NativeWindowsMessageListener] Error initializing diagnostic monitors: {ex.Message}");
        }
    }

    public async Task TurnOffMonitorAsync()
    {
        _keepMonitorOff = true;
        _useDDCCI = false;

        // Disable external mouse wake permissions to prevent automatic wake
        try
        {
            var disabledCount = _devicePowerManagerService.DisableExternalMouseWake();
            if (Log.Instance.IsTraceEnabled)
            {
                Log.Instance.Trace($"TurnOffMonitorAsync: Disabled wake for {disabledCount} external mice");
                var disabledDevices = _devicePowerManagerService.GetDisabledDevices();
                foreach (var device in disabledDevices)
                {
                    Log.Instance.Trace($"  - {device}");
                }
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Failed to disable external mouse wake: {ex.Message}");
            // Continue with normal operation even if device management fails
        }

        // Enable mouse movement filtering to prevent screen wake from tiny movements
        _filterMouseMovementEnabled = true;
        _isMousePositionInitialized = false;  // Reset position tracking
        _lastSignificantMoveTime = DateTime.MinValue;

        // Reset input type tracking to avoid stale data from before screen was off
        _lastUserInputType = "None";
        _lastUserInputTypeTimestamp = DateTime.MinValue;

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Keep monitor off mode enabled with mouse movement filtering (threshold={MOUSE_MOVE_THRESHOLD}px)");

        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        // Try DDC/CI first (hardware-level control, no wake issues)
        if (MonitorPowerControl.IsSupported)
        {
            if (MonitorPowerControl.TryTurnOffMonitor())
            {
                _useDDCCI = true;
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Monitor turned off via DDC/CI (hardware control)");
                return;
            }
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"DDC/CI available but failed, falling back to SendMessage");
        }

        // Fallback to Windows API
        await _mainThreadDispatcher.DispatchAsync(() =>
        {
            PInvoke.SendMessage(new HWND(Handle), PInvoke.WM_SYSCOMMAND, new WPARAM(PInvoke.SC_MONITORPOWER), new LPARAM(2));
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        // Tell Windows we want to stay in away mode - this prevents automatic screen wake
        // ES_AWAYMODE_REQUIRED: Stay in away mode (screen off, system running)
        // ES_CONTINUOUS: Keep this state until explicitly changed
        try
        {
            PInvoke.SetThreadExecutionState(Windows.Win32.System.Power.EXECUTION_STATE.ES_AWAYMODE_REQUIRED | Windows.Win32.System.Power.EXECUTION_STATE.ES_CONTINUOUS);
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"SetThreadExecutionState: Away mode enabled to prevent automatic screen wake");
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"SetThreadExecutionState failed: {ex.Message}");
        }

        // Start input monitoring timer to detect user activity via GetLastInputInfo
        StartInputMonitoring();
    }

    /// <summary>
    /// Sets whether the monitor should be automatically re-closed when it wakes up while keep-monitor-off mode is active.
    /// </summary>
    /// <param name="enabled">true to enable auto-reclose (default), false to disable it</param>
    public void SetAutoRecloseEnabled(bool enabled)
    {
        _autoRecloseEnabled = enabled;
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Auto-reclose {(enabled ? "enabled" : "disabled")}");
    }

    /// <summary>
    /// Gets whether auto-reclose is currently enabled.
    /// </summary>
    public bool IsAutoRecloseEnabled => _autoRecloseEnabled;

    /// <summary>
    /// Starts monitoring user input via GetLastInputInfo timer.
    /// This provides a fallback when low-level hooks fail to detect input during monitor off state.
    /// </summary>
    private void StartInputMonitoring()
    {
        StopInputMonitoring(); // Stop any existing timer first
        
        _inputMonitorTimer = new global::System.Threading.Timer(new global::System.Threading.TimerCallback(state =>
        {
            if (!_keepMonitorOff) return;
            
            try
            {
                var lastInput = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
                if (NativeInput.GetLastInputInfo(out lastInput))
                {
                    var idleTime = Environment.TickCount - (int)lastInput.dwTime;
                    
                    // If there was input in the last 500ms, consider it as user activity
                    if (idleTime < 500)
                    {
                        // Validate input using InputValidator
                        var validation = _inputValidator.ValidateInput(idleTime);
                        
                        // Analyze RawInput events for real movement
                        var recentRawInputEvents = _rawInputMonitor.GetRecentEvents(TimeSpan.FromMilliseconds(RAW_INPUT_EVENT_WINDOW_MS));
                        var hasRealMovement = recentRawInputEvents.Any(e => 
                            e.Type == RawInputType.Mouse && 
                            (Math.Abs(e.MouseX) >= MOUSE_MOVE_THRESHOLD || Math.Abs(e.MouseY) >= MOUSE_MOVE_THRESHOLD));
                        
                        // Check if input is truly valid:
                        // 1. Hook detected (real click/movement)
                        // 2. OR has real movement in RawInput (not just zero-movement noise)
                        var isTrulyValid = validation.HookDetected || hasRealMovement;
                        
                        if (validation.IsValid && isTrulyValid)
                        {
                            // Valid input detected
                            var inputType = string.IsNullOrEmpty(_lastUserInputType) || _lastUserInputType == "None"
                                ? "Unknown Input" 
                                : _lastUserInputType;
                            
                            CancelKeepMonitorOff();
                            
                            if (Log.Instance.IsTraceEnabled)
                            {
                                var rawInputInfo = validation.RawInputDetected 
                                    ? $"RawInput: {validation.RawInputEventCount} events from [{string.Join(", ", validation.RawInputDevices)}]" 
                                    : "";
                                var inputTypeAge = DateTime.Now - _lastUserInputTypeTimestamp;
                                var isStale = inputTypeAge.TotalSeconds > STALE_INPUT_THRESHOLD_SECONDS;  // Consider data older than threshold as stale
                                var staleWarning = isStale ? $" [STALE! age={inputTypeAge.TotalSeconds:F1}s]" : "";
                                var realMoveInfo = hasRealMovement ? ", hasRealMovement=True" : ", hasRealMovement=False";
                                Log.Instance.Trace($"User activity detected via GetLastInputInfo ({inputType}{staleWarning}, idle={idleTime}ms){realMoveInfo}, {rawInputInfo}, canceling keep-monitor-off mode");
                                
                                if (isStale)
                                {
                                    Log.Instance.Trace($"  WARNING: _lastUserInputType is {inputTypeAge.TotalSeconds:F1} seconds old!");
                                    Log.Instance.Trace($"    Last update: {_lastUserInputTypeTimestamp:HH:mm:ss.fff}");
                                    Log.Instance.Trace($"    Validation: Hook={validation.HookDetected}, RawInput={validation.RawInputDetected}, RealMovement={hasRealMovement}");
                                }
                            }
                        }
                        else if (validation.IsValid && !isTrulyValid)
                        {
                            // GetLastInputInfo detected input but no real movement - likely accumulated tiny movements
                            if (Log.Instance.IsTraceEnabled)
                            {
                                var rawInputEvents = recentRawInputEvents.Where(e => e.Type == RawInputType.Mouse).ToList();
                                var zeroMoveCount = rawInputEvents.Count(e => e.MouseX == 0 && e.MouseY == 0);
                                var tinyMoveCount = rawInputEvents.Count(e => Math.Abs(e.MouseX) < MOUSE_MOVE_THRESHOLD && Math.Abs(e.MouseY) < MOUSE_MOVE_THRESHOLD && (e.MouseX != 0 || e.MouseY != 0));
                                
                                Log.Instance.Trace($"FILTERED: GetLastInputInfo detected input but no real movement (idle={idleTime}ms)");
                                Log.Instance.Trace($"  RawInput events in last 500ms: {rawInputEvents.Count}");
                                Log.Instance.Trace($"    Zero movement (X=0,Y=0): {zeroMoveCount}");
                                Log.Instance.Trace($"    Tiny movement (<{MOUSE_MOVE_THRESHOLD}px): {tinyMoveCount}");
                                Log.Instance.Trace($"  Keeping monitor off - accumulated noise filtered");
                            }
                            // Do NOT cancel keep-monitor-off mode
                        }
                        else
                        {
                            // False input detected - GetLastInputInfo detected input but no other method confirmed it
                            if (Log.Instance.IsTraceEnabled)
                            {
                                Log.Instance.Trace($"False input detected via GetLastInputInfo (idle={idleTime}ms)");
                                Log.Instance.Trace($"  Reason: {validation.Reason}");
                                Log.Instance.Trace($"  Confidence: {validation.Confidence:F2}");
                                Log.Instance.Trace($"  Detection results: Hook={validation.HookDetected}, RawInput={validation.RawInputDetected}");
                                if (validation.SuspectedSources.Count > 0)
                                {
                                    Log.Instance.Trace($"  Suspected sources: {string.Join(", ", validation.SuspectedSources)}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"GetLastInputInfo monitoring failed: {ex.Message}");
            }
        }), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(INPUT_MONITOR_INTERVAL_MS));
        
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Input monitoring timer started with validation");
    }

    /// <summary>
    /// Stops the input monitoring timer.
    /// </summary>
    private void StopInputMonitoring()
    {
        if (_inputMonitorTimer != null)
        {
            _inputMonitorTimer.Dispose();
            _inputMonitorTimer = null;
            
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Input monitoring timer stopped");
        }
    }

    /// <summary>
    /// Cancels keep-monitor-off mode.
    /// Also turns the monitor back on via DDC/CI if it was used.
    /// Note: This does NOT restore _autoRecloseEnabled - user's choice is preserved.
    /// </summary>
    private void CancelKeepMonitorOff()
    {
        // Restore mouse wake permissions when canceling keep-monitor-off mode
        try
        {
            if (_keepMonitorOff)
            {
                var restoredCount = _devicePowerManagerService.RestoreAllMiceWake();
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"CancelKeepMonitorOff: Restored wake for {restoredCount} devices");
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Failed to restore mouse wake permissions: {ex.Message}");
            // Continue with normal operation even if device restoration fails
        }

        _keepMonitorOff = false;
        _useDDCCI = false;
        // Don't restore _autoRecloseEnabled - preserve user's choice

        // Disable mouse movement filtering when monitor is back on
        _filterMouseMovementEnabled = false;
        _isMousePositionInitialized = false;

        // Stop input monitoring timer
        StopInputMonitoring();

        // Turn monitor back on via DDC/CI if it was used
        if (MonitorPowerControl.IsSupported)
            MonitorPowerControl.TryTurnOnMonitor();

        // Reset execution state to allow normal power management
        try
        {
            PInvoke.SetThreadExecutionState(Windows.Win32.System.Power.EXECUTION_STATE.ES_CONTINUOUS); // Clear other flags
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"SetThreadExecutionState: Reset to normal power management");
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"SetThreadExecutionState reset failed: {ex.Message}");
        }

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Keep monitor off mode disabled, mouse movement filtering turned off");
    }

    public Task StartAsync() => _mainThreadDispatcher.DispatchAsync(() =>
    {
        CreateHandle(new CreateParams
        {
            Caption = "LenovoLegionToolkit_MessageWindow",
            Parent = new IntPtr(-3)
        });

        _kbHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _kbProc, HINSTANCE.Null, 0);
        _mouseHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, _mouseProc, HINSTANCE.Null, 0);

        _deviceNotificationHandle = RegisterDeviceNotification(Handle);
        _consoleDisplayStateNotificationHandle = RegisterPowerNotification(PInvoke.GUID_CONSOLE_DISPLAY_STATE);
        _lidSwitchStateChangeNotificationHandle = RegisterPowerNotification(PInvoke.GUID_LIDSWITCH_STATE_CHANGE);
        _powerSavingStateChangeNotificationHandle = RegisterPowerNotification(PInvoke.GUID_POWER_SAVING_STATUS);
        _userPresenceNotificationHandle = RegisterPowerNotification(PInvoke.GUID_SESSION_USER_PRESENCE);

        return WaitForInit();
    });

    public Task StopAsync() => _mainThreadDispatcher.DispatchAsync(() =>
    {
        PInvoke.UnhookWindowsHookEx(_kbHook);
        PInvoke.UnhookWindowsHookEx(_mouseHook);

        PInvoke.UnregisterDeviceNotification(_deviceNotificationHandle);
        PInvoke.UnregisterPowerSettingNotification(_consoleDisplayStateNotificationHandle);
        PInvoke.UnregisterPowerSettingNotification(_lidSwitchStateChangeNotificationHandle);
        PInvoke.UnregisterPowerSettingNotification(_powerSavingStateChangeNotificationHandle);
        PInvoke.UnregisterPowerSettingNotification(_userPresenceNotificationHandle);

        _kbHook = default;
        _mouseHook = default;
        _deviceNotificationHandle = default;
        _consoleDisplayStateNotificationHandle = default;

        // Clean up DDC/CI resources
        MonitorPowerControl.Cleanup();

        ReleaseHandle();

        return Task.CompletedTask;
    });

    protected override unsafe void WndProc(ref Message m)
    {
        if (m.Msg == PInvoke.WM_DEVICECHANGE && m.LParam != IntPtr.Zero)
        {
            ref var devBroadcastHdr = ref Unsafe.AsRef<DEV_BROADCAST_HDR>((void*)m.LParam);
            if (devBroadcastHdr.dbch_devicetype == DEV_BROADCAST_HDR_DEVICE_TYPE.DBT_DEVTYP_DEVICEINTERFACE)
            {
                ref var devBroadcastDeviceInterface = ref Unsafe.AsRef<DEV_BROADCAST_DEVICEINTERFACE_W>((void*)m.LParam);
                var length = ((int)devBroadcastDeviceInterface.dbcc_size - sizeof(DEV_BROADCAST_DEVICEINTERFACE_W)) / sizeof(char);
                var name = devBroadcastDeviceInterface.dbcc_name.AsSpan(length).ToString();

                var state = (uint)m.WParam.ToInt32();
                switch (state)
                {
                    case PInvoke.DBT_DEVICEARRIVAL:
                        {
                            if (Log.Instance.IsTraceEnabled)
                                Log.Instance.Trace($"Event received: Device Arrival [name={name}]");

                            OnDeviceConnected(name);
                            break;
                        }
                    case PInvoke.DBT_DEVICEREMOVECOMPLETE:
                        {
                            if (Log.Instance.IsTraceEnabled)
                                Log.Instance.Trace($"Event received: Device Removal Complete [name={name}]");

                            OnDeviceDisconnected(name);
                            break;
                        }
                }

                if (devBroadcastDeviceInterface.dbcc_classguid == PInvoke.GUID_DISPLAY_DEVICE_ARRIVAL)
                {
                    if (Log.Instance.IsTraceEnabled)
                        Log.Instance.Trace($"Event received: Display Device Arrival");

                    OnDisplayDeviceArrival();
                }

                if (devBroadcastDeviceInterface.dbcc_classguid == PInvoke.GUID_DEVINTERFACE_MONITOR)
                {
                    var id = InternalDisplay.Get();
                    var isExternal = !name.Equals(id?.DevicePath, StringComparison.Ordinal);

                    switch (state)
                    {
                        case PInvoke.DBT_DEVICEARRIVAL:
                            {
                                if (Log.Instance.IsTraceEnabled)
                                    Log.Instance.Trace($"Event received: Monitor Connected");

                                OnMonitorConnected(isExternal);
                                break;
                            }
                        case PInvoke.DBT_DEVICEREMOVECOMPLETE:
                            {
                                if (Log.Instance.IsTraceEnabled)
                                    Log.Instance.Trace($"Event received: Monitor Disconnected");

                                OnMonitorDisconnected(isExternal);
                                break;
                            }
                    }
                }
            }
        }

        if (m.Msg == PInvoke.WM_POWERBROADCAST && m.WParam == (IntPtr)PInvoke.PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
        {
            ref var str = ref Unsafe.AsRef<POWERBROADCAST_SETTING>((void*)m.LParam);

            if (str.PowerSetting == PInvoke.GUID_CONSOLE_DISPLAY_STATE)
            {
                var state = (PInvokeExtensions.CONSOLE_DISPLAY_STATE)str.Data[0];
                switch (state)
                {
                    case PInvokeExtensions.CONSOLE_DISPLAY_STATE.On:
                        {
                            // Filter: If keep-monitor-off is active, verify this is real user input
                            // Windows may auto-wake the monitor after ~3 minutes via this notification
                            if (_keepMonitorOff)
                            {
                                // Check for real user input in the last 500ms
                                var recentRawInputEvents = _rawInputMonitor.GetRecentEvents(TimeSpan.FromMilliseconds(500));
                                var hasRealMovement = recentRawInputEvents.Any(e =>
                                    e.Type == RawInputType.Mouse &&
                                    (Math.Abs(e.MouseX) >= MOUSE_MOVE_THRESHOLD || Math.Abs(e.MouseY) >= MOUSE_MOVE_THRESHOLD));

                                var inputTypeAge = DateTime.Now - _lastUserInputTypeTimestamp;
                                var hasRecentHookInput = inputTypeAge.TotalMilliseconds < RAW_INPUT_EVENT_WINDOW_MS;

                                if (!hasRealMovement && !hasRecentHookInput)
                                {
                                    // No real input - this is an automatic wake from Windows power management
                                    if (Log.Instance.IsTraceEnabled)
                                    {
                                        var rawInputCount = recentRawInputEvents.Count(e => e.Type == RawInputType.Mouse);
                                        Log.Instance.Trace($"FILTERED: Monitor On event blocked - automatic wake detected");
                                        Log.Instance.Trace($"  RawInput mouse events: {rawInputCount}, hasRealMovement={hasRealMovement}");
                                        Log.Instance.Trace($"  Hook input age: {inputTypeAge.TotalMilliseconds:F0}ms, hasRecentHookInput={hasRecentHookInput}");
                                        Log.Instance.Trace($"  Re-closing monitor immediately via PostMessage...");
                                    }

                                    // Use PostMessage for immediate async close (~1ms delay)
                                    // PostMessage is async and doesn't block WndProc
                                    PInvoke.PostMessage(new HWND(Handle), PInvoke.WM_SYSCOMMAND, new WPARAM(PInvoke.SC_MONITORPOWER), new LPARAM(2));
                                    
                                    if (Log.Instance.IsTraceEnabled)
                                        Log.Instance.Trace($"Monitor re-close message posted");

                                    // Don't process the Monitor On event normally
                                    break;
                                }
                            }

                            if (Log.Instance.IsTraceEnabled)
                                Log.Instance.Trace($"Event received: Monitor On");

                            OnMonitorOn();
                            break;
                        }
                    case PInvokeExtensions.CONSOLE_DISPLAY_STATE.Off:
                        {
                            if (Log.Instance.IsTraceEnabled)
                                Log.Instance.Trace($"Event received: Monitor Off");

                            OnMonitorOff();
                            break;
                        }
                }
            }

            if (str.PowerSetting == PInvoke.GUID_LIDSWITCH_STATE_CHANGE)
            {
                var isOpened = str.Data[0] != 0;
                if (isOpened)
                {
                    if (Log.Instance.IsTraceEnabled)
                        Log.Instance.Trace($"Event received: Lid Opened");

                    OnLidOpened();
                }
                else
                {
                    if (Log.Instance.IsTraceEnabled)
                        Log.Instance.Trace($"Event received: Lid Closed");

                    OnLidClosed();
                }
            }

            if (str.PowerSetting == PInvoke.GUID_POWER_SAVING_STATUS && str.Data[0] == 0)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Event received: Battery Saver enabled");

                OnBatterySaverEnabled();
            }

            if (str.PowerSetting == PInvoke.GUID_SESSION_USER_PRESENCE)
            {
                var presence = (PInvokeExtensions.USER_ACTIVITY_PRESENCE)str.Data[0];
                
                // Filter: If user becomes "Present" but keep-monitor-off is enabled and no real input detected,
                // this is likely a false positive from Windows User Presence system
                if (presence == PInvokeExtensions.USER_ACTIVITY_PRESENCE.PowerUserPresent && 
                    _keepMonitorOff)
                {
                    // Check if there's real input in the last 500ms
                    var recentRawInputEvents = _rawInputMonitor.GetRecentEvents(TimeSpan.FromMilliseconds(500));
                    var hasRealMovement = recentRawInputEvents.Any(e => 
                        e.Type == RawInputType.Mouse && 
                        (Math.Abs(e.MouseX) >= MOUSE_MOVE_THRESHOLD || Math.Abs(e.MouseY) >= MOUSE_MOVE_THRESHOLD));
                    
                    // Also check if LowLevelHook detected any input recently
                    var inputTypeAge = DateTime.Now - _lastUserInputTypeTimestamp;
                    var hasRecentHookInput = inputTypeAge.TotalMilliseconds < RAW_INPUT_EVENT_WINDOW_MS;
                    
                    if (!hasRealMovement && !hasRecentHookInput)
                    {
                        // No real input detected - this is likely accumulated noise triggering User Presence
                        if (Log.Instance.IsTraceEnabled)
                        {
                            var rawInputCount = recentRawInputEvents.Count(e => e.Type == RawInputType.Mouse);
                            Log.Instance.Trace($"FILTERED: User Presence change to 'Present' blocked - no real input detected");
                            Log.Instance.Trace($"  RawInput mouse events in last 500ms: {rawInputCount}, hasRealMovement={hasRealMovement}");
                            Log.Instance.Trace($"  Hook input age: {inputTypeAge.TotalMilliseconds:F0}ms, hasRecentHookInput={hasRecentHookInput}");
                            Log.Instance.Trace($"  Keeping User Presence as 'Not Present' to prevent monitor wake");
                        }
                        
                        // Block this User Presence change by not updating _lastUserPresence
                        // This prevents OnMonitorOn() from being called with "User Input" source
                        return;
                    }
                }
                
                _lastUserPresence = presence;

                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Event received: User Presence changed to {presence}");

                // Record User Presence change event for diagnostic purposes
                _userPresenceTracker.RecordEvent(_previousUserPresence, presence, "Windows Notification");

                // Add to event sequence analyzer
                _eventSequenceAnalyzer.AddEvent(new EventSequenceAnalyzer.TimelineEvent
                {
                    Timestamp = DateTime.Now,
                    EventType = "UserPresence",
                    Description = $"User Presence changed from {_previousUserPresence} to {presence}",
                    Data = new { Previous = _previousUserPresence, Current = presence }
                });

                // Capture sensor data if available
                if (_activityAwarenessMonitor.IsEnabled)
                {
                    var sensorData = _activityAwarenessMonitor.CaptureSensorData();
                    _eventSequenceAnalyzer.AddEvent(new EventSequenceAnalyzer.TimelineEvent
                    {
                        Timestamp = DateTime.Now,
                        EventType = "Sensor",
                        Description = sensorData.ToString(),
                        Data = sensorData
                    });
                }

                // Update previous presence
                _previousUserPresence = presence;
            }
        }

        // Handle Raw Input messages (WM_INPUT)
        if (m.Msg == 0x00FF) // WM_INPUT
        {
            _rawInputMonitor.ProcessRawInput(m.LParam);
        }

        base.WndProc(ref m);
    }

    private async Task WaitForInit()
    {
        var delayTask = Task.Delay(TimeSpan.FromSeconds(3));
        var task = Task.WhenAll(
            _isMonitorOnTaskCompletionSource.Task,
            _isLidOpenTaskCompletionSource.Task
        );

        var completed = await Task.WhenAny(task, delayTask).ConfigureAwait(false);

        if (completed == delayTask)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Delay expired, state might be inconsistent! [IsMonitorOn={IsMonitorOn}, IsLidOpen={IsLidOpen}]");
        }
    }

    private void OnMonitorOn()
    {
        // Determine wake source based on user presence state
        var wakeSource = _lastUserPresence == PInvokeExtensions.USER_ACTIVITY_PRESENCE.PowerUserPresent
            ? "User Input"
            : "Background Activity (Windows Update, UWP notifications, network, etc.)";
        
        // If keep-monitor-off mode is active, check if auto-reclose is enabled
        // This happens when Windows auto-wakes the monitor after ~3 minutes
        if (_keepMonitorOff)
        {
            // If auto-reclose is disabled, cancel keep-monitor-off mode and let monitor stay on
            if (!_autoRecloseEnabled)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Monitor woke up but auto-reclose is disabled [source: {wakeSource}]. Canceling keep-monitor-off mode.");
                
                CancelKeepMonitorOff();
                // Continue to normal flow - monitor will stay on
            }
            else
            {
                // Auto-reclose is enabled, re-close the monitor immediately
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Monitor auto-wake detected [source: {wakeSource}, userPresence: {_lastUserPresence}], re-closing immediately...");
                
                // Try DDC/CI first if it was used originally
                if (_useDDCCI && MonitorPowerControl.TryTurnOffMonitor())
                {
                    if (Log.Instance.IsTraceEnabled)
                        Log.Instance.Trace($"Re-closed monitor via DDC/CI");
                }
                else
                {
                    // Fallback to SendMessage
                    PInvoke.SendMessage(new HWND(Handle), PInvoke.WM_SYSCOMMAND, new WPARAM(PInvoke.SC_MONITORPOWER), new LPARAM(2));
                }
                
                // Log power requests for debugging when background activity wakes the display
                if (Log.Instance.IsTraceEnabled && _lastUserPresence != PInvokeExtensions.USER_ACTIVITY_PRESENCE.PowerUserPresent)
                {
                    Task.Run(async () =>
                    {
                        var powerRequests = await GetPowerRequestsAsync().ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(powerRequests))
                            Log.Instance.Trace($"Power requests at wake:\n{powerRequests}");
                    });
                }
                
                return; // Don't set IsMonitorOn = true, don't trigger events
            }
        }
        
        // Log detailed wake information
        if (Log.Instance.IsTraceEnabled)
        {
            // Capture the current values before they are reset
            var capturedUserInputType = _lastUserInputType;
            var capturedUserPresence = _lastUserPresence;

            Task.Run(async () =>
            {
                var lastWake = await GetLastWakeInfoAsync().ConfigureAwait(false);
                var wakeTimers = await GetWakeTimersAsync().ConfigureAwait(false);
                var powerRequests = await GetPowerRequestsAsync().ConfigureAwait(false);
                var eventLogWakeSource = await GetWakeSourceFromEventLogAsync().ConfigureAwait(false);
                var wakeArmedDevices = await GetWakeArmedDevicesAsync().ConfigureAwait(false);

                var userInputDetail = wakeSource == "User Input" ? $" ({capturedUserInputType})" : "";
                var details = $"Monitor wake details:{Environment.NewLine}" +
                    $"  Source: {wakeSource}{userInputDetail}{Environment.NewLine}" +
                    $"  UserPresence: {capturedUserPresence}{Environment.NewLine}" +
                    $"  LastWake: {lastWake}{Environment.NewLine}" +
                    $"  WakeTimers: {wakeTimers}{Environment.NewLine}" +
                    $"  PowerRequests:{Environment.NewLine}{powerRequests}{Environment.NewLine}" +
                    $"  EventLog-WakeSource:{Environment.NewLine}{eventLogWakeSource}{Environment.NewLine}" +
                    $"  WakeArmedDevices: {wakeArmedDevices}";

                Log.Instance.Trace($"{details}");

                // Capture system state snapshot for diagnostic purposes
                try
                {
                    var systemSnapshot = SystemStateSnapshot.Capture();
                    Log.Instance.Trace($"System State Snapshot at wake:{Environment.NewLine}{systemSnapshot.ToCompactString()}");
                }
                catch (Exception ex)
                {
                    Log.Instance.Trace($"Error capturing system state snapshot: {ex.Message}");
                }

                // Check network and process activity
                try
                {
                    _networkActivityMonitor.CheckActivity();
                    _processActivityMonitor.CheckActivity();

                    var networkActivity = _networkActivityMonitor.WasActivityDetected(30);
                    var processActivity = _processActivityMonitor.WasActivityDetected(30);

                    if (networkActivity || processActivity)
                    {
                        Log.Instance.Trace($"Activity detected at wake - Network: {networkActivity}, Process: {processActivity}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Instance.Trace($"Error checking activity monitors: {ex.Message}");
                }

                // Analyze event sequence for anomalies
                try
                {
                    var anomalies = _eventSequenceAnalyzer.Analyze();
                    if (anomalies.Count > 0)
                    {
                        Log.Instance.Trace($"Event sequence analysis detected {anomalies.Count} anomalies:");
                        foreach (var anomaly in anomalies.OrderByDescending(a => a.ConfidenceScore).Take(3))
                        {
                            Log.Instance.Trace($"  - {anomaly}");
                            if (!string.IsNullOrEmpty(anomaly.PossibleCause))
                            {
                                Log.Instance.Trace($"    Possible cause: {anomaly.PossibleCause}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Instance.Trace($"Error analyzing event sequence: {ex.Message}");
                }
            });
        }
        
        IsMonitorOn = true;
        _isMonitorOnTaskCompletionSource.TrySetResult();

        MonitorStateChanged?.Invoke(this, true);
        RaiseChanged(NativeWindowsMessage.MonitorOn);
        
        // Reset user input type after logging
        _lastUserInputType = "None";
    }

    private void OnMonitorOff()
    {
        IsMonitorOn = false;
        _isMonitorOnTaskCompletionSource.TrySetResult();

        MonitorStateChanged?.Invoke(this, false);
        RaiseChanged(NativeWindowsMessage.MonitorOff);
    }

    private void OnLidOpened()
    {
        IsLidOpen = true;
        _isLidOpenTaskCompletionSource.TrySetResult();

        RaiseChanged(NativeWindowsMessage.LidOpened);
    }

    private void OnLidClosed()
    {
        IsLidOpen = false;
        _isLidOpenTaskCompletionSource.TrySetResult();

        RaiseChanged(NativeWindowsMessage.LidClosed);
    }

    private void OnBatterySaverEnabled()
    {
        Task.Run(_powerModeFeature.EnsureCorrectWindowsPowerSettingsAreSetAsync);

        RaiseChanged(NativeWindowsMessage.BatterySaverEnabled);
    }

    private void OnDeviceConnected(string name)
    {
        RaiseChanged(NativeWindowsMessage.DeviceConnected, ConvertDeviceNameToDeviceInstanceId(name));
    }

    private void OnDeviceDisconnected(string name)
    {
        RaiseChanged(NativeWindowsMessage.DeviceDisconnected, ConvertDeviceNameToDeviceInstanceId(name));
    }

    private void OnMonitorConnected(bool isExternal)
    {
        RaiseChanged(NativeWindowsMessage.MonitorConnected);

        if (isExternal)
            RaiseChanged(NativeWindowsMessage.ExternalMonitorConnected);
    }

    private void OnMonitorDisconnected(bool isExternal)
    {
        RaiseChanged(NativeWindowsMessage.MonitorDisconnected);

        if (isExternal)
            RaiseChanged(NativeWindowsMessage.ExternalMonitorDisconnected);
    }

    private void OnDisplayDeviceArrival()
    {
        Task.Run(async () =>
        {
            if (await _dgpuNotify.IsSupportedAsync().ConfigureAwait(false))
                await _dgpuNotify.NotifyAsync().ConfigureAwait(false);
        });

        RaiseChanged(NativeWindowsMessage.OnDisplayDeviceArrival);
    }

    private void RaiseChanged(NativeWindowsMessage message, object? data = null) => Changed?.Invoke(this, new ChangedEventArgs(message, data));

    private unsafe LRESULT LowLevelKeyboardProc(int nCode, WPARAM wParam, LPARAM lParam)
    {
        // Debug logging to confirm hook is being called
        if (Log.Instance.IsTraceEnabled && nCode == PInvoke.HC_ACTION)
            Log.Instance.Trace($"Keyboard hook triggered: nCode={nCode}, wParam={wParam.Value}");

        if (nCode != PInvoke.HC_ACTION)
            return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);

        ref var kbStruct = ref Unsafe.AsRef<KBDLLHOOKSTRUCT>((void*)lParam.Value);

        // Always record user input type when key is pressed
        if (wParam.Value == PInvoke.WM_KEYDOWN || wParam.Value == PInvoke.WM_SYSKEYDOWN)
        {
            var vkCode = kbStruct.vkCode;
            var keyType = GetKeyTypeDescription(vkCode);
            _lastUserInputType = $"Keyboard - {keyType}";
            _lastUserInputTypeTimestamp = DateTime.Now;
            
            // Notify InputValidator about hook input
            _inputValidator.OnHookInput();

            // Add to event sequence analyzer
            _eventSequenceAnalyzer.AddEvent(new EventSequenceAnalyzer.TimelineEvent
            {
                Timestamp = DateTime.Now,
                EventType = "Keyboard",
                Description = $"Key pressed: {keyType}",
                Data = new { KeyType = keyType }
            });
        }

        // User keyboard activity - cancel keep-monitor-off mode
        if (_keepMonitorOff && (wParam.Value == PInvoke.WM_KEYDOWN || wParam.Value == PInvoke.WM_SYSKEYDOWN))
        {
            var vkCode = kbStruct.vkCode;
            var keyType = GetKeyTypeDescription(vkCode);
            
            CancelKeepMonitorOff();
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"User keyboard activity detected ({keyType}), canceling keep-monitor-off mode");
        }

        _smartFnLockController.OnKeyboardEvent(wParam.Value, kbStruct);

        if (wParam.Value != PInvoke.WM_KEYUP)
            return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);

        if (kbStruct.vkCode == (ulong)VIRTUAL_KEY.VK_CAPITAL)
        {
            var isOn = (PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_CAPITAL) & 0x1) != 0;
            var type = isOn ? NotificationType.CapsLockOn : NotificationType.CapsLockOff;
            MessagingCenter.Publish(new NotificationMessage(type));
        }

        if (kbStruct.vkCode == (ulong)VIRTUAL_KEY.VK_NUMLOCK)
        {
            var isOn = (PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_NUMLOCK) & 0x1) != 0;
            var type = isOn ? NotificationType.NumLockOn : NotificationType.NumLockOff;
            MessagingCenter.Publish(new NotificationMessage(type));
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
    }

    private unsafe LRESULT LowLevelMouseProc(int nCode, WPARAM wParam, LPARAM lParam)
    {
        // Debug logging to confirm hook is being called
        if (Log.Instance.IsTraceEnabled && nCode == PInvoke.HC_ACTION)
            Log.Instance.Trace($"Mouse hook triggered: nCode={nCode}, wParam={wParam.Value}");

        if (nCode != PInvoke.HC_ACTION)
            return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);

        var mouseMessage = (uint)wParam.Value;
        
        // Handle mouse movement with filtering
        if (mouseMessage == PInvoke.WM_MOUSEMOVE)
        {
            // Only filter when monitor is off (keep-monitor-off mode)
            if (_filterMouseMovementEnabled)
            {
                ref var mouseStruct = ref Unsafe.AsRef<MSLLHOOKSTRUCT>((void*)lParam.Value);
                int currentX = mouseStruct.pt.X;
                int currentY = mouseStruct.pt.Y;
                
                // Initialize mouse position on first move
                if (!_isMousePositionInitialized)
                {
                    _lastMouseX = currentX;
                    _lastMouseY = currentY;
                    _isMousePositionInitialized = true;
                    return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
                }
                
                // Calculate displacement
                int dx = currentX - _lastMouseX;
                int dy = currentY - _lastMouseY;
                int distanceSq = dx * dx + dy * dy;
                int thresholdSq = MOUSE_MOVE_THRESHOLD * MOUSE_MOVE_THRESHOLD; // 25
                
                // Check cooldown to prevent rapid successive significant moves
                var now = DateTime.Now;
                bool isCooldown = (now - _lastSignificantMoveTime).TotalMilliseconds < SIGNIFICANT_MOVE_COOLDOWN_MS;
                
                if (distanceSq < thresholdSq || isCooldown)
                {
                    // Tiny movement or during cooldown - filter it out
                    // Return 1 to indicate event is "handled" but don't pass to system
                    // This prevents Windows from updating GetLastInputInfo
                    if (Log.Instance.IsTraceEnabled && distanceSq > 0)
                        Log.Instance.Trace($"[LowLevelMouseProc] Filtering tiny movement: dx={dx}, dy={dy}, distance={Math.Sqrt(distanceSq):F1}px");
                    
                    // Don't update last position for tiny moves to allow accumulation
                    return (LRESULT)1;
                }
                else
                {
                    // Significant movement - update position and allow through
                    _lastMouseX = currentX;
                    _lastMouseY = currentY;
                    _lastSignificantMoveTime = now;
                    
                    if (Log.Instance.IsTraceEnabled)
                        Log.Instance.Trace($"[LowLevelMouseProc] Significant movement: dx={dx}, dy={dy}, distance={Math.Sqrt(distanceSq):F1}px");
                    
                    // Cancel keep-monitor-off mode for real user activity
                    if (_keepMonitorOff)
                    {
                        CancelKeepMonitorOff();
                        Log.Instance.Trace($"[LowLevelMouseProc] Significant mouse movement detected, canceling keep-monitor-off mode");
                    }
                }
            }
            
            // Pass movement through to system
            return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
        }
        
        // Determine mouse action type (clicks, scrolls)
        string? mouseAction = null;
        if (mouseMessage == PInvoke.WM_LBUTTONDOWN)
            mouseAction = "Left Click";
        else if (mouseMessage == PInvoke.WM_RBUTTONDOWN)
            mouseAction = "Right Click";
        else if (mouseMessage == PInvoke.WM_MBUTTONDOWN)
            mouseAction = "Middle Click";
        else if (mouseMessage == PInvoke.WM_XBUTTONDOWN)
            mouseAction = "X Button Click";
        else if (mouseMessage == PInvoke.WM_MOUSEWHEEL)
            mouseAction = "Vertical Scroll";
        else if (mouseMessage == PInvoke.WM_MOUSEHWHEEL)
            mouseAction = "Horizontal Scroll";

        // Always record user input type when mouse action is detected
        if (mouseAction != null)
        {
            _lastUserInputType = $"Mouse - {mouseAction}";
            _lastUserInputTypeTimestamp = DateTime.Now;
            
            // Log click details for debugging
            if (Log.Instance.IsTraceEnabled)
            {
                ref var clickStruct = ref Unsafe.AsRef<MSLLHOOKSTRUCT>((void*)lParam.Value);
                Log.Instance.Trace($"[LowLevelMouseProc] Mouse {mouseAction} at ({clickStruct.pt.X},{clickStruct.pt.Y}), filterEnabled={_filterMouseMovementEnabled}");
            }
            
            // Notify InputValidator about hook input
            _inputValidator.OnHookInput();

            // Add to event sequence analyzer
            _eventSequenceAnalyzer.AddEvent(new EventSequenceAnalyzer.TimelineEvent
            {
                Timestamp = DateTime.Now,
                EventType = "Mouse",
                Description = $"Mouse action: {mouseAction}",
                Data = new { MouseAction = mouseAction }
            });
        }

        // User mouse activity - cancel keep-monitor-off mode
        if (_keepMonitorOff && mouseAction != null)
        {
            CancelKeepMonitorOff();
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"User mouse activity detected ({mouseAction}), canceling keep-monitor-off mode");
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
    }

    private static unsafe HDEVNOTIFY RegisterDeviceNotification(IntPtr handle)
    {
        var ptr = IntPtr.Zero;
        try
        {
            var str = new DEV_BROADCAST_DEVICEINTERFACE_W();
            str.dbcc_size = (uint)Marshal.SizeOf(str);
            str.dbcc_devicetype = (uint)DEV_BROADCAST_HDR_DEVICE_TYPE.DBT_DEVTYP_DEVICEINTERFACE;
            ptr = Marshal.AllocHGlobal(Marshal.SizeOf(str));
            Marshal.StructureToPtr(str, ptr, true);
            return PInvoke.RegisterDeviceNotification(new HANDLE(handle),
                ptr.ToPointer(),
                REGISTER_NOTIFICATION_FLAGS.DEVICE_NOTIFY_WINDOW_HANDLE | REGISTER_NOTIFICATION_FLAGS.DEVICE_NOTIFY_ALL_INTERFACE_CLASSES);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private unsafe HPOWERNOTIFY RegisterPowerNotification(Guid guid)
    {
        return PInvoke.RegisterPowerSettingNotification(new HANDLE(Handle), &guid, 0);
    }

    private static string? ConvertDeviceNameToDeviceInstanceId(string name)
    {
        var parts = name.Split('#');
        if (parts.Length < 3)
            return null;

        var part1 = parts[0].TrimStart('\\', '?');
        var part2 = parts[1].Replace('#', '\\');
        var part3 = parts[2];
        return $@"{part1}\{part2}\{part3}".ToUpperInvariant();
    }

    /// <summary>
    /// Get power requests that are preventing display from turning off
    /// </summary>
    private static async Task<string> GetPowerRequestsAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = "/requests",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = new Process();
            proc.StartInfo = psi;
            proc.Start();

            var output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var error = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);

            await Task.Run(() => proc.WaitForExit(5000)).ConfigureAwait(false);

            if (proc.ExitCode != 0)
                return $"Failed to get power requests: {error}";

            // Truncate if too long
            if (output.Length > 2000)
                output = output.Substring(0, 2000) + "... [truncated]";

            return output.Trim();
        }
        catch (Exception ex)
        {
            return $"Failed to get power requests: {ex.Message}";
        }
    }

    /// <summary>
    /// Get last wake source information
    /// </summary>
    private static async Task<string> GetLastWakeInfoAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = "/lastwake",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = new Process();
            proc.StartInfo = psi;
            proc.Start();

            var output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var error = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);

            await Task.Run(() => proc.WaitForExit(5000)).ConfigureAwait(false);

            if (proc.ExitCode != 0)
                return $"Failed to get last wake info: {error}";

            // Parse and simplify output
            var lines = output.Split('\n');
            var result = new global::System.Text.StringBuilder();
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("唤醒历史记录计数") || trimmed.StartsWith("唤醒历史记录") || trimmed.StartsWith("Wake"))
                    continue;
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;
                if (trimmed.Contains("实例路径") || trimmed.Contains("Instance Path"))
                    continue;
                result.AppendLine(trimmed);
            }

            var finalResult = result.ToString().Trim();
            return string.IsNullOrEmpty(finalResult) ? "无唤醒记录" : finalResult;
        }
        catch (Exception ex)
        {
            return $"Failed to get last wake info: {ex.Message}";
        }
    }

    /// <summary>
    /// Get active wake timers
    /// </summary>
    private static async Task<string> GetWakeTimersAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = "/waketimers",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = new Process();
            proc.StartInfo = psi;
            proc.Start();

            var output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var error = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);

            await Task.Run(() => proc.WaitForExit(5000)).ConfigureAwait(false);

            if (proc.ExitCode != 0)
                return $"Failed to get wake timers: {error}";

            // Parse and simplify output
            var lines = output.Split('\n');
            var result = new global::System.Text.StringBuilder();
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("唤醒定时器计数") || trimmed.StartsWith("Timer Count") || trimmed.StartsWith("唤醒定时器") || trimmed.StartsWith("Wake Timers"))
                    continue;
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;
                result.AppendLine(trimmed);
            }

            var finalResult = result.ToString().Trim();
            return string.IsNullOrEmpty(finalResult) ? "无活动唤醒定时器" : finalResult;
        }
        catch (Exception ex)
        {
            return $"Failed to get wake timers: {ex.Message}";
        }
    }

    /// <summary>
    /// Get key type description without exposing sensitive information like actual character keys
    /// </summary>
    private static string GetKeyTypeDescription(ulong vkCode)
    {
        // Function keys (F1-F24)
        if (vkCode >= (ulong)VIRTUAL_KEY.VK_F1 && vkCode <= (ulong)VIRTUAL_KEY.VK_F24)
            return $"F{vkCode - (ulong)VIRTUAL_KEY.VK_F1 + 1}";

        // Modifier keys
        if (vkCode == (ulong)VIRTUAL_KEY.VK_LSHIFT || vkCode == (ulong)VIRTUAL_KEY.VK_RSHIFT || vkCode == (ulong)VIRTUAL_KEY.VK_SHIFT)
            return "Shift";
        if (vkCode == (ulong)VIRTUAL_KEY.VK_LCONTROL || vkCode == (ulong)VIRTUAL_KEY.VK_RCONTROL || vkCode == (ulong)VIRTUAL_KEY.VK_CONTROL)
            return "Ctrl";
        if (vkCode == (ulong)VIRTUAL_KEY.VK_LMENU || vkCode == (ulong)VIRTUAL_KEY.VK_RMENU || vkCode == (ulong)VIRTUAL_KEY.VK_MENU)
            return "Alt";
        if (vkCode == (ulong)VIRTUAL_KEY.VK_LWIN || vkCode == (ulong)VIRTUAL_KEY.VK_RWIN)
            return "Win";

        // Navigation keys
        if (vkCode == (ulong)VIRTUAL_KEY.VK_UP || vkCode == (ulong)VIRTUAL_KEY.VK_DOWN ||
            vkCode == (ulong)VIRTUAL_KEY.VK_LEFT || vkCode == (ulong)VIRTUAL_KEY.VK_RIGHT)
            return "Arrow Key";
        if (vkCode == (ulong)VIRTUAL_KEY.VK_HOME || vkCode == (ulong)VIRTUAL_KEY.VK_END)
            return "Navigation";
        if (vkCode == (ulong)VIRTUAL_KEY.VK_PRIOR || vkCode == (ulong)VIRTUAL_KEY.VK_NEXT)
            return "Page Key";
        if (vkCode == (ulong)VIRTUAL_KEY.VK_INSERT || vkCode == (ulong)VIRTUAL_KEY.VK_DELETE)
            return "Edit Key";

        // Special keys
        if (vkCode == (ulong)VIRTUAL_KEY.VK_RETURN || vkCode == (ulong)VIRTUAL_KEY.VK_SEPARATOR)
            return "Enter";
        if (vkCode == (ulong)VIRTUAL_KEY.VK_SPACE)
            return "Space";
        if (vkCode == (ulong)VIRTUAL_KEY.VK_TAB)
            return "Tab";
        if (vkCode == (ulong)VIRTUAL_KEY.VK_BACK)
            return "Backspace";
        if (vkCode == (ulong)VIRTUAL_KEY.VK_ESCAPE)
            return "Escape";
        if (vkCode == (ulong)VIRTUAL_KEY.VK_CAPITAL)
            return "CapsLock";
        if (vkCode == (ulong)VIRTUAL_KEY.VK_NUMLOCK)
            return "NumLock";
        if (vkCode == (ulong)VIRTUAL_KEY.VK_SCROLL)
            return "ScrollLock";

        // Numpad
        if (vkCode >= (ulong)VIRTUAL_KEY.VK_NUMPAD0 && vkCode <= (ulong)VIRTUAL_KEY.VK_NUMPAD9)
            return "Numpad";
        if (vkCode == (ulong)VIRTUAL_KEY.VK_MULTIPLY || vkCode == (ulong)VIRTUAL_KEY.VK_ADD ||
            vkCode == (ulong)VIRTUAL_KEY.VK_SUBTRACT || vkCode == (ulong)VIRTUAL_KEY.VK_DECIMAL ||
            vkCode == (ulong)VIRTUAL_KEY.VK_DIVIDE)
            return "Numpad Symbol";

        // Media/Extended keys
        if (vkCode == (ulong)VIRTUAL_KEY.VK_VOLUME_UP || vkCode == (ulong)VIRTUAL_KEY.VK_VOLUME_DOWN ||
            vkCode == (ulong)VIRTUAL_KEY.VK_VOLUME_MUTE)
            return "Media Volume";
        if (vkCode == (ulong)VIRTUAL_KEY.VK_MEDIA_PLAY_PAUSE || vkCode == (ulong)VIRTUAL_KEY.VK_MEDIA_STOP ||
            vkCode == (ulong)VIRTUAL_KEY.VK_MEDIA_NEXT_TRACK || vkCode == (ulong)VIRTUAL_KEY.VK_MEDIA_PREV_TRACK)
            return "Media Control";

        // Generic (hide actual character for privacy)
        if ((vkCode >= 0x30 && vkCode <= 0x39) || (vkCode >= 0x41 && vkCode <= 0x5A))
            return "Character Key";

        return "Other Key";
    }

    /// <summary>
    /// Get wake source from Windows Event Log (Power-Troubleshooter and Kernel-Power)
    /// </summary>
    private static async Task<string> GetWakeSourceFromEventLogAsync()
    {
        try
        {
            var result = new global::System.Text.StringBuilder();
            
            // Query Power-Troubleshooter events (Event ID 1) - most detailed wake info
            try
            {
                var query = new EventLogQuery("System", PathType.LogName, 
                    "*[System[Provider[@Name='Power-Troubleshooter'] and EventID=1]]");
                
                using var reader = new EventLogReader(query);
                var evt = reader.ReadEvent();
                if (evt != null)
                {
                    // Parse the event properties
                    var wakeTime = evt.TimeCreated?.ToString("HH:mm:ss") ?? "Unknown";
                    string wakeSource = "Unknown";
                    
                    // Try to extract wake source from event properties
                    if (evt.Properties.Count > 0)
                    {
                        wakeSource = evt.Properties[0]?.Value?.ToString() ?? "Unknown";
                    }
                    
                    result.AppendLine($"Power-Troubleshooter: {wakeSource} (at {wakeTime})");
                }
                else
                {
                    result.AppendLine("Power-Troubleshooter: No recent wake events");
                }
            }
            catch (Exception ex)
            {
                result.AppendLine($"Power-Troubleshooter: Failed to read - {ex.Message}");
            }
            
            // Query Kernel-Power events (Event ID 107) - system resume from sleep
            try
            {
                var query = new EventLogQuery("System", PathType.LogName, 
                    "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=107]]");
                
                using var reader = new EventLogReader(query);
                var evt = reader.ReadEvent();
                if (evt != null)
                {
                    var wakeTime = evt.TimeCreated?.ToString("HH:mm:ss") ?? "Unknown";
                    result.AppendLine($"Kernel-Power: System resumed from sleep (at {wakeTime})");
                }
            }
            catch (Exception ex)
            {
                result.AppendLine($"Kernel-Power: Failed to read - {ex.Message}");
            }
            
            return result.ToString().Trim();
        }
        catch (Exception ex)
        {
            return $"Failed to get wake source from event log: {ex.Message}";
        }
    }

    /// <summary>
    /// Get devices that are configured to wake the system
    /// </summary>
    private static async Task<string> GetWakeArmedDevicesAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = "/devicequery wake_armed",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = new Process();
            proc.StartInfo = psi;
            proc.Start();

            var output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var error = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);

            await Task.Run(() => proc.WaitForExit(5000)).ConfigureAwait(false);

            if (proc.ExitCode != 0)
                return $"Failed to get wake armed devices: {error}";

            var devices = output.Trim();
            if (string.IsNullOrEmpty(devices) || devices.Contains("NONE"))
                return "None";

            // Format: one device per line, convert to comma-separated
            var deviceList = devices.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.Trim())
                .Where(d => !string.IsNullOrEmpty(d))
                .ToList();

            return deviceList.Count > 0 ? string.Join(", ", deviceList) : "None";
        }
        catch (Exception ex)
        {
            return $"Failed to get wake armed devices: {ex.Message}";
        }
    }

    /// <summary>
    /// Structure for GetLastInputInfo API
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    /// <summary>
    /// Native Windows API declarations for input monitoring
    /// </summary>
    private static class NativeInput
    {
        [DllImport("user32.dll")]
        public static extern bool GetLastInputInfo(out LASTINPUTINFO plii);
    }
}
