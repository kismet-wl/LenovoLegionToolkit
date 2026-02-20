using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.System;

/// <summary>
/// Monitors Raw Input events to distinguish between real hardware input and system-generated input.
/// This helps identify false input events detected by GetLastInputInfo.
/// </summary>
public class RawInputMonitor : IDisposable
{
    private bool _isInitialized;
    private IntPtr _hwnd;
    private Dictionary<IntPtr, string> _deviceMap = new();
    private List<RawInputEvent> _recentEvents = new();
    private readonly object _lock = new object();

    /// <summary>
    /// Safely logs a trace message, catching any ArrayPool allocation failures.
    /// </summary>
    private static void SafeLog(FormattableString message)
    {
        try
        {
            if (Log.Instance?.IsTraceEnabled == true)
                Log.Instance.Trace(message);
        }
        catch
        {
            // Ignore logging failures - don't let them crash the application
        }
    }

    // Raw Input API declarations
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList(
        IntPtr pRawInputDeviceList,
        ref uint puiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr hRawInput,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetRawInputDeviceInfo(
        IntPtr hDevice,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    // Raw Input structure definitions
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RAWINPUT
    {
        [FieldOffset(0)]
        public RAWINPUTHEADER header;
        
        [FieldOffset(24)] // Size of RAWINPUTHEADER on 64-bit (4+4+8+8)
        public RAWMOUSE mouse;
        
        [FieldOffset(24)]
        public RAWKEYBOARD keyboard;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RAWMOUSE
    {
        [FieldOffset(0)]
        public ushort usFlags;
        
        [FieldOffset(2)]
        public ushort usButtonFlags;
        
        [FieldOffset(4)]
        public ushort usButtonData;
        
        [FieldOffset(2)]
        public uint ulButtons;  // Union with usButtonFlags + usButtonData
        
        [FieldOffset(8)]
        public uint ulRawButtons;
        
        [FieldOffset(12)]
        public int lLastX;
        
        [FieldOffset(16)]
        public int lLastY;
        
        [FieldOffset(20)]
        public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    // Constants
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_REMOVE = 0x00000001;
    private const uint RIDI_DEVICENAME = 0x200000b;
    private const uint RIDI_DEVICEINFO = 0x2000000c;

    private const uint HID_USAGE_PAGE_GENERIC = 0x01;
    private const uint HID_USAGE_GENERIC_MOUSE = 0x02;
    private const uint HID_USAGE_GENERIC_KEYBOARD = 0x06;

    private const int RI_MOUSE_MOVE_ABSOLUTE = 0x0001;
    private const int RI_MOUSE_MOVE_RELATIVE = 0;
    private const int RI_MOUSE_BUTTON_DOWN = 0x0002;
    private const int RI_MOUSE_BUTTON_UP = 0x0004;
    private const int RI_MOUSE_WHEEL = 0x0400;
    private const int RI_MOUSE_HWHEEL = 0x0800;

    private const int RI_KEY_MAKE = 0;
    private const int RI_KEY_BREAK = 1;
    private const int RI_KEY_E0 = 2;
    private const int RI_KEY_E1 = 4;

    /// <summary>
    /// Initializes the Raw Input monitor.
    /// </summary>
    public bool Initialize(IntPtr hwnd)
    {
        if (_isInitialized)
                    {
                        SafeLog($"[RawInputMonitor] Already initialized");
                        return true;
                    }
        try
        {
            _hwnd = hwnd;

            // Register Raw Input devices
            var devices = new[]
            {
                new RAWINPUTDEVICE
                {
                    usUsagePage = (ushort)HID_USAGE_PAGE_GENERIC,
                    usUsage = (ushort)HID_USAGE_GENERIC_MOUSE,
                    dwFlags = RIDEV_INPUTSINK,
                    hwndTarget = hwnd
                },
                new RAWINPUTDEVICE
                {
                    usUsagePage = (ushort)HID_USAGE_PAGE_GENERIC,
                    usUsage = (ushort)HID_USAGE_GENERIC_KEYBOARD,
                    dwFlags = RIDEV_INPUTSINK,
                    hwndTarget = hwnd
                }
            };

            if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            {
                var error = Marshal.GetLastWin32Error();
                SafeLog($"[RawInputMonitor] Failed to register raw input devices: Error {error}");
                return false;
            }

            // Enumerate devices
            EnumerateDevices();

            _isInitialized = true;
            SafeLog($"[RawInputMonitor] Initialized successfully, found {_deviceMap.Count} devices");

            return true;
        }
        catch (Exception ex)
        {
            SafeLog($"[RawInputMonitor] Initialization failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Processes a Raw Input message (WM_INPUT).
    /// </summary>
    public void ProcessRawInput(IntPtr lParam)
    {
        if (!_isInitialized)
            return;

        try
        {
            // First, get the size of the raw input data
            uint dwSize = 0;
            var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            
            if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref dwSize, headerSize) != 0)
                return;

            if (dwSize == 0)
                return;

            // Allocate buffer and get the actual data
            IntPtr buffer = IntPtr.Zero;
            try
            {
                buffer = Marshal.AllocHGlobal((int)dwSize);
                var result = GetRawInputData(lParam, RID_INPUT, buffer, ref dwSize, headerSize);
                
                if (result == 0xFFFFFFFF || result == 0)
                    return;

                // Convert to RAWINPUT structure
                var raw = Marshal.PtrToStructure<RAWINPUT>(buffer);
                var deviceName = _deviceMap.GetValueOrDefault(raw.header.hDevice, "Unknown Device");

                var evt = new RawInputEvent
                {
                    Timestamp = DateTime.Now,
                    DeviceHandle = raw.header.hDevice,
                    DeviceName = deviceName,
                    Type = raw.header.dwType switch
                    {
                        0 => RawInputType.Mouse,
                        1 => RawInputType.Keyboard,
                        _ => RawInputType.Other
                    }
                };

                if (raw.header.dwType == 0) // Mouse
                {
                    evt.MouseFlags = raw.mouse.usFlags;
                    evt.MouseButtons = raw.mouse.ulButtons;
                    evt.MouseX = raw.mouse.lLastX;
                    evt.MouseY = raw.mouse.lLastY;
                }
                else if (raw.header.dwType == 1) // Keyboard
                {
                    evt.KeyboardMakeCode = raw.keyboard.MakeCode;
                    evt.KeyboardFlags = raw.keyboard.Flags;
                    evt.KeyboardVKey = raw.keyboard.VKey;
                    evt.KeyboardMessage = raw.keyboard.Message;
                }

                lock (_lock)
                {
                    _recentEvents.Add(evt);
                    // Keep only events from the last 10 seconds
                    var cutoff = DateTime.Now.AddSeconds(-10);
                    _recentEvents.RemoveAll(e => e.Timestamp < cutoff);
                }

                if (evt.Type == RawInputType.Mouse)
                {
                    var buttonInfo = GetMouseButtonDescription(evt.MouseButtons);
                    SafeLog($"[RawInput] Mouse input from: {deviceName} (Handle: 0x{raw.header.hDevice.ToInt64():X}), Buttons: {buttonInfo}, X: {evt.MouseX}, Y: {evt.MouseY}");
                }
                else if (evt.Type == RawInputType.Keyboard)
                {
                    var keyInfo = GetKeyboardDescription(evt);
                    SafeLog($"[RawInput] Keyboard input from: {deviceName} (Handle: 0x{raw.header.hDevice.ToInt64():X}), {keyInfo}");
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            SafeLog($"[RawInputMonitor] Failed to process raw input: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets recent Raw Input events within the specified time window.
    /// </summary>
    public List<RawInputEvent> GetRecentEvents(TimeSpan timeWindow)
    {
        lock (_lock)
        {
            var cutoff = DateTime.Now - timeWindow;
            return _recentEvents.FindAll(e => e.Timestamp >= cutoff);
        }
    }

    /// <summary>
    /// Checks if there were any Raw Input events in the specified time window.
    /// </summary>
    public bool HasRecentInput(TimeSpan timeWindow)
    {
        return GetRecentEvents(timeWindow).Count > 0;
    }

    /// <summary>
    /// Enumerates all Raw Input devices.
    /// </summary>
    private void EnumerateDevices()
    {
        try
        {
            uint deviceCount = 0;
            var result = GetRawInputDeviceList(IntPtr.Zero, ref deviceCount, (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>());

            if (deviceCount == 0)
            {
                SafeLog($"[RawInputMonitor] No Raw Input devices found");
                return;
            }

            // Allocate buffer for RAWINPUTDEVICELIST structures
            var structSize = Marshal.SizeOf<RAWINPUTDEVICELIST>();
            var buffer = Marshal.AllocHGlobal((int)(deviceCount * structSize));
            try
            {
                result = GetRawInputDeviceList(buffer, ref deviceCount, (uint)structSize);

                if (result != deviceCount)
                {
                    SafeLog($"[RawInputMonitor] Device count mismatch: expected {deviceCount}, got {result}");
                    return;
                }

                _deviceMap.Clear();
                
                // Parse each RAWINPUTDEVICELIST structure
                for (int i = 0; i < deviceCount; i++)
                {
                    var ptr = buffer + i * structSize;
                    var deviceListItem = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(ptr);
                    var hDevice = deviceListItem.hDevice;
                    var dwType = deviceListItem.dwType;
                    
                    var deviceName = GetDeviceName(hDevice);
                    var deviceTypeName = dwType switch
                    {
                        0 => "Mouse",
                        1 => "Keyboard",
                        2 => "HID",
                        _ => "Unknown"
                    };
                    
                    // Store device info with type
                    _deviceMap[hDevice] = deviceName;
                    
                    SafeLog($"[RawInput] Device: {deviceName}, Type: {deviceTypeName}, Handle: 0x{hDevice.ToInt64():X}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            SafeLog($"[RawInputMonitor] Failed to enumerate devices: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the device name for a device handle.
    /// </summary>
    private string GetDeviceName(IntPtr handle)
    {
        try
        {
            uint size = 0;
            var result = GetRawInputDeviceInfo(handle, RIDI_DEVICENAME, IntPtr.Zero, ref size);

            if (size == 0)
                return "Unknown Device";

            var buffer = new byte[size];
            var ptr = Marshal.AllocHGlobal((int)size);
            try
            {
                result = GetRawInputDeviceInfo(handle, RIDI_DEVICENAME, ptr, ref size);
                Marshal.Copy(ptr, buffer, 0, (int)size);
                return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
                {
                    SafeLog($"[RawInputMonitor] Failed to get device name: {ex.Message}");
                    return "Unknown Device";
                }    }

    /// <summary>
    /// Gets device information for a device handle.
    /// </summary>
    private string GetDeviceInfo(IntPtr handle)
    {
        try
        {
            uint size = 0;
            var result = GetRawInputDeviceInfo(handle, RIDI_DEVICEINFO, IntPtr.Zero, ref size);

            if (size == 0)
                return "";

            var ptr = Marshal.AllocHGlobal((int)size);
            try
            {
                result = GetRawInputDeviceInfo(handle, RIDI_DEVICEINFO, ptr, ref size);
                return "Size: " + size + " bytes";
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
        {
            SafeLog($"[RawInputMonitor] Failed to get device info: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Gets a description of mouse button state.
    /// </summary>
    private string GetMouseButtonDescription(uint buttons)
    {
        var parts = new List<string>();
        
        if ((buttons & 0x0001) != 0) parts.Add("Left Button");
        if ((buttons & 0x0002) != 0) parts.Add("Right Button");
        if ((buttons & 0x0004) != 0) parts.Add("Middle Button");
        if ((buttons & 0x0008) != 0) parts.Add("X Button 1");
        if ((buttons & 0x0010) != 0) parts.Add("X Button 2");
        if ((buttons & 0x0020) != 0) parts.Add("X Button 3");
        if ((buttons & 0x0040) != 0) parts.Add("X Button 4");
        if ((buttons & 0x0080) != 0) parts.Add("X Button 5");
        
        return parts.Count > 0 ? string.Join(", ", parts) : "Move";
    }

    /// <summary>
    /// Gets a description of keyboard event.
    /// </summary>
    private string GetKeyboardDescription(RawInputEvent evt)
    {
        var parts = new List<string>();
        
        var isKeyDown = (evt.KeyboardFlags & RI_KEY_BREAK) == 0;
        parts.Add(isKeyDown ? "Down" : "Up");
        
        if ((evt.KeyboardFlags & RI_KEY_E0) != 0) parts.Add("E0");
        if ((evt.KeyboardFlags & RI_KEY_E1) != 0) parts.Add("E1");
        
        parts.Add("VKey: " + evt.KeyboardVKey);
        parts.Add("Message: 0x" + evt.KeyboardMessage.ToString("X"));
        
        return string.Join(", ", parts);
    }

    public void Dispose()
    {
        if (!_isInitialized)
            return;

        try
        {
            // Unregister devices
            var devices = new[]
            {
                new RAWINPUTDEVICE
                {
                    usUsagePage = (ushort)HID_USAGE_PAGE_GENERIC,
                    usUsage = (ushort)HID_USAGE_GENERIC_MOUSE,
                    dwFlags = RIDEV_REMOVE,
                    hwndTarget = IntPtr.Zero
                },
                new RAWINPUTDEVICE
                {
                    usUsagePage = (ushort)HID_USAGE_PAGE_GENERIC,
                    usUsage = (ushort)HID_USAGE_GENERIC_KEYBOARD,
                    dwFlags = RIDEV_REMOVE,
                    hwndTarget = IntPtr.Zero
                }
            };

            RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            
            _isInitialized = false;
            _deviceMap.Clear();
            _recentEvents.Clear();
            
            SafeLog($"[RawInputMonitor] Disposed");
        }
        catch (Exception ex)
        {
            SafeLog($"[RawInputMonitor] Disposal failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Raw Input event information.
/// </summary>
public class RawInputEvent
{
    public DateTime Timestamp { get; set; }
    public IntPtr DeviceHandle { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public RawInputType Type { get; set; }
    
    // Mouse data
    public ushort MouseFlags { get; set; }
    public uint MouseButtons { get; set; }
    public int MouseX { get; set; }
    public int MouseY { get; set; }
    
    // Keyboard data
    public ushort KeyboardMakeCode { get; set; }
    public ushort KeyboardFlags { get; set; }
    public ushort KeyboardVKey { get; set; }
    public uint KeyboardMessage { get; set; }
}

/// <summary>
/// Raw Input event type.
/// </summary>
public enum RawInputType
{
    Mouse = 0,
    Keyboard = 1,
    Other = 2
}
