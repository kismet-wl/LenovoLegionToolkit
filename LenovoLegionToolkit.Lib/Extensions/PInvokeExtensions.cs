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
