using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;

namespace LenovoLegionToolkit.Lib.Extensions;

// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo

[Flags]
public enum EXECUTION_STATE : uint
{
    ES_AWAYMODE_REQUIRED = 0x00000040,
    ES_CONTINUOUS = 0x80000000,
    ES_DISPLAY_REQUIRED = 0x00000002,
    ES_SYSTEM_REQUIRED = 0x00000001
}

public enum POWER_REQUEST_TYPE : uint
{
    PowerRequestDisplayRequired = 0,
    PowerRequestSystemRequired = 1,
    PowerRequestAwayModeRequired = 2,
    PowerRequestExecutionRequired = 3
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct REASON_CONTEXT
{
    public uint Version;
    public uint Flags;

    [StructLayout(LayoutKind.Explicit)]
    public struct REASON_UNION
    {
        [FieldOffset(0)]
        public REASON_CONTEXT_DETAILED Detailed;

        [FieldOffset(0)]
        public IntPtr SimpleReasonString;
    }

    public REASON_UNION Reason;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct REASON_CONTEXT_DETAILED
{
    public IntPtr LocalizedReasonModule;
    public uint LocalizedReasonId;
    public uint ReasonStringCount;
    public IntPtr ReasonStrings;
}

public static class PInvokeExtensions
{
    public const uint POWER_REQUEST_CONTEXT_VERSION = 0;
    public const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x00000001;
    public const uint POWER_REQUEST_CONTEXT_DETAILED_STRING = 0x00000002;

    public enum CONSOLE_DISPLAY_STATE
    {
        Off = 0,
        On = 1,
        Dimmed = 2
    }

    /// <summary>
    /// User activity presence state for GUID_SESSION_USER_PRESENCE notification
    /// </summary>
    public enum USER_ACTIVITY_PRESENCE
    {
        /// <summary>User is providing input to the session</summary>
        PowerUserPresent = 0,
        /// <summary>User is not present</summary>
        PowerUserNotPresent = 1,
        /// <summary>User activity timeout has elapsed with no interaction</summary>
        PowerUserInactive = 2
    }

    public const int ERROR_SUCCESS = 0;
    public const int ERROR_NO_MORE_ITEMS = 259;

    public const uint KF_FLAG_DEFAULT = 0;

    public const uint VARIABLE_ATTRIBUTE_BOOTSERVICE_ACCESS = 2;
    public const uint VARIABLE_ATTRIBUTE_NON_VOLATILE = 1;
    public const uint VARIABLE_ATTRIBUTE_RUNTIME_ACCESS = 7;

    public static readonly Guid DISPLAY_BRIGTHNESS_SETTING_GUID = Guid.Parse("aded5e82-b909-4619-9949-f5d71dac0bcb");

    public static unsafe bool DeviceIoControl<TIn, TOut>(SafeFileHandle hDevice, uint dwIoControlCode, TIn inVal, out TOut outVal) where TIn : struct where TOut : struct
    {
        var lpInBuffer = IntPtr.Zero;
        var lpOutBuffer = IntPtr.Zero;

        try
        {
            var nInBufferSize = Marshal.SizeOf<TIn>();
            var nOutBufferSize = Marshal.SizeOf<TOut>();

            lpInBuffer = Marshal.AllocHGlobal(nInBufferSize);
            lpOutBuffer = Marshal.AllocHGlobal(nOutBufferSize);

            Marshal.StructureToPtr(inVal, lpInBuffer, false);

            var ret = PInvoke.DeviceIoControl(hDevice,
                dwIoControlCode,
                lpInBuffer.ToPointer(),
                (uint)nInBufferSize,
                lpOutBuffer.ToPointer(),
                (uint)nOutBufferSize,
                null,
                null);

            outVal = ret ? Marshal.PtrToStructure<TOut>(lpOutBuffer) : default;

            return ret;
        }
        finally
        {
            Marshal.FreeHGlobal(lpInBuffer);
            Marshal.FreeHGlobal(lpOutBuffer);
        }
    }

    public static void ThrowIfWin32Error(string description)
    {
        var errorCode = Marshal.GetLastWin32Error();
        ThrowIfWin32Error(errorCode, description);
    }

    public static void ThrowIfWin32Error(int errorCode, string description)
    {
        if (errorCode != 0)
            throw Marshal.GetExceptionForHR(errorCode) ?? throw new Exception($"Unknown Win32 error code {errorCode} in {description}");

        throw new Exception($"{description} failed but Win32 didn't catch an error");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr PowerCreateRequest(
        ref REASON_CONTEXT Context
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PowerSetRequest(IntPtr PowerRequest, POWER_REQUEST_TYPE RequestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PowerClearRequest(IntPtr PowerRequest, POWER_REQUEST_TYPE RequestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);
}

#region DDC/CI Monitor Control

/// <summary>
/// Physical monitor handle for DDC/CI operations
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PHYSICAL_MONITOR
{
    public IntPtr hPhysicalMonitor;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string szPhysicalMonitorDescription;
}

/// <summary>
/// DDC/CI VCP Codes for monitor control
/// </summary>
public static class VcpCode
{
    public const byte DPMSPowerState = 0xD6;  // Power mode control
    public const byte Brightness = 0x10;       // Brightness
    public const byte Contrast = 0x12;         // Contrast
}

/// <summary>
/// DDC/CI power mode values for VCP code D6
/// </summary>
public enum MonitorPowerMode : uint
{
    On = 0,
    Standby = 2,
    Suspend = 3,
    Off = 4,
    SoftOff = 5
}

public static class DDCApi
{
    private const string Dxva2Dll = "dxva2.dll";

    [DllImport(Dxva2Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor,
        out uint pdwNumberOfPhysicalMonitors);

    [DllImport(Dxva2Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor,
        uint dwPhysicalMonitorArraySize,
        [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport(Dxva2Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyPhysicalMonitors(
        uint dwPhysicalMonitorArraySize,
        PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport(Dxva2Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyPhysicalMonitor(
        IntPtr hPhysicalMonitor);

    [DllImport(Dxva2Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetVCPFeature(
        IntPtr hPhysicalMonitor,
        byte bVCPCode,
        uint dwNewValue);

    [DllImport(Dxva2Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVCPFeatureAndVCPFeatureReply(
        IntPtr hPhysicalMonitor,
        byte bVCPCode,
        out LPMC_VCP_CODE_TYPE pvct,
        out uint pdwCurrentValue,
        out uint pdwMaximumValue);

    [DllImport(Dxva2Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorCapabilities(
        IntPtr hPhysicalMonitor,
        out uint pdwMonitorCapabilities,
        out uint pdwSupportedColorTemperatures);

    [DllImport(Dxva2Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CapabilitiesRequestAndCapabilitiesReply(
        IntPtr hPhysicalMonitor,
        [Out] byte[] pszASCIICapabilitiesString,
        ref uint pdwCapabilitiesStringLengthInCharacters);
}

public enum LPMC_VCP_CODE_TYPE
{
    MC_MOMENTARY = 0,
    MC_SET_PARAMETER = 1
}

/// <summary>
/// Monitor capabilities flags
/// </summary>
[Flags]
public enum MonitorCapabilities : uint
{
    MC_CAPS_NONE = 0,
    MC_CAPS_MONITOR_TECHNOLOGY_TYPE = 1,
    MC_CAPS_BRIGHTNESS = 2,
    MC_CAPS_CONTRAST = 4,
    MC_CAPS_COLOR_TEMPERATURE = 8,
    MC_CAPS_RED_GREEN_BLUE_GAIN = 16,
    MC_CAPS_RED_GREEN_BLUE_DRIVE = 32,
    MC_CAPS_DEGAUSS = 64,
    MC_CAPS_DISPLAY_AREA_POSITION = 128,
    MC_CAPS_DISPLAY_AREA_SIZE = 256,
    MC_CAPS_RESTORE_FACTORY_DEFAULTS = 512,
    MC_CAPS_RESTORE_FACTORY_COLOR_DEFAULTS = 1024,
    MC_CAPS_RESTORE_FACTORY_DEFAULTS_EX = 2048,
    MC_RESTORE_FACTORY_DEFAULTS_ENABLES_MONITOR_SETTINGS = 4096,
}

#endregion
