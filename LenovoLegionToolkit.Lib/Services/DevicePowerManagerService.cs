using LenovoLegionToolkit.Lib.Models;
using LenovoLegionToolkit.Lib.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace LenovoLegionToolkit.Lib.Services;

/// <summary>
/// 设备电源管理服务实现
/// 使用 powercfg 命令管理设备唤醒权限
/// </summary>
public class DevicePowerManagerService : IDevicePowerManagerService
{
    private readonly List<DeviceInfo> _disabledDevices = new List<DeviceInfo>();
    private readonly Dictionary<string, DeviceInfo> _deviceCache = new Dictionary<string, DeviceInfo>();
    private readonly object _lock = new object();

    private const int EXECUTION_TIMEOUT_MS = 10000; // 10秒超时

    /// <summary>
    /// 获取所有可唤醒的设备
    /// </summary>
    public List<DeviceInfo> GetWakeArmedDevices()
    {
        try
        {
            lock (_lock)
            {
                var result = ExecutePowerCfg("devicequery wake_armed");
                var lines = result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                var devices = new List<DeviceInfo>();

                foreach (var line in lines)
                {
                    var deviceName = line.Trim();
                    if (string.IsNullOrWhiteSpace(deviceName))
                        continue;

                    var deviceInfo = GetDeviceInfo(deviceName);
                    if (deviceInfo != null)
                    {
                        deviceInfo.IsWakeEnabled = true;
                        devices.Add(deviceInfo);
                    }
                }

                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"GetWakeArmedDevices: Found {devices.Count} wake-armed devices");

                return devices;
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"GetWakeArmedDevices failed: {ex.Message}", ex);
            return new List<DeviceInfo>();
        }
    }

    /// <summary>
    /// 禁用外接鼠标的唤醒权限
    /// </summary>
    public int DisableExternalMouseWake()
    {
        try
        {
            lock (_lock)
            {
                var devices = GetWakeArmedDevices();
                var disabledCount = 0;

                foreach (var device in devices)
                {
                    // 只禁用外接鼠标
                    if (device.Type == DeviceType.ExternalMouse)
                    {
                        if (DisableDeviceWake(device.Name))
                        {
                            device.IsWakeEnabled = false;
                            _disabledDevices.Add(device);
                            disabledCount++;

                            if (Log.Instance.IsTraceEnabled)
                                Log.Instance.Trace($"Disabled wake for external mouse: {device.Name}");
                        }
                    }
                    else
                    {
                        if (Log.Instance.IsTraceEnabled)
                            Log.Instance.Trace($"Skipped {device.Type}: {device.Name}");
                    }
                }

                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"DisableExternalMouseWake: Disabled {disabledCount} external mice");

                return disabledCount;
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"DisableExternalMouseWake failed: {ex.Message}", ex);
            return 0;
        }
    }

    /// <summary>
    /// 恢复所有之前禁用的鼠标设备唤醒权限
    /// </summary>
    public int RestoreAllMiceWake()
    {
        try
        {
            lock (_lock)
            {
                var restoredCount = 0;

                foreach (var device in _disabledDevices.ToList())
                {
                    if (EnableDeviceWake(device.Name))
                    {
                        device.IsWakeEnabled = true;
                        _disabledDevices.Remove(device);
                        restoredCount++;

                        if (Log.Instance.IsTraceEnabled)
                            Log.Instance.Trace($"Restored wake for: {device.Name}");
                    }
                }

                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"RestoreAllMiceWake: Restored {restoredCount} devices");

                return restoredCount;
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"RestoreAllMiceWake failed: {ex.Message}", ex);
            return 0;
        }
    }

    /// <summary>
    /// 禁用指定设备的唤醒权限
    /// </summary>
    public bool DisableDeviceWake(string deviceName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(deviceName))
                return false;

            var result = ExecutePowerCfg($"devicedisablewake \"{deviceName}\"");

            // 检查是否成功
            if (result.Contains("successfully", StringComparison.OrdinalIgnoreCase) ||
                result.Contains("disabled", StringComparison.OrdinalIgnoreCase))
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Successfully disabled wake for: {deviceName}");
                return true;
            }

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"DisableDeviceWake failed for {deviceName}: {result}");
            return false;
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"DisableDeviceWake failed for {deviceName}: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// 启用指定设备的唤醒权限
    /// </summary>
    public bool EnableDeviceWake(string deviceName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(deviceName))
                return false;

            var result = ExecutePowerCfg($"deviceenablewake \"{deviceName}\"");

            // 检查是否成功
            if (result.Contains("successfully", StringComparison.OrdinalIgnoreCase) ||
                result.Contains("enabled", StringComparison.OrdinalIgnoreCase))
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"Successfully enabled wake for: {deviceName}");
                return true;
            }

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"EnableDeviceWake failed for {deviceName}: {result}");
            return false;
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"EnableDeviceWake failed for {deviceName}: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// 获取已禁用唤醒的设备列表
    /// </summary>
    public List<DeviceInfo> GetDisabledDevices()
    {
        lock (_lock)
        {
            return new List<DeviceInfo>(_disabledDevices);
        }
    }

    /// <summary>
    /// 清空已禁用设备列表
    /// </summary>
    public void ClearDisabledDevices()
    {
        lock (_lock)
        {
            _disabledDevices.Clear();
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Cleared disabled devices list");
        }
    }

    /// <summary>
    /// 获取设备详细信息
    /// </summary>
    private DeviceInfo GetDeviceInfo(string deviceName)
    {
        // 检查缓存
        if (_deviceCache.TryGetValue(deviceName, out var cached))
            return cached;

        var deviceInfo = new DeviceInfo
        {
            Name = deviceName,
            Type = IdentifyDeviceType(deviceName),
            IsWakeEnabled = true
        };

        // 尝试获取硬件ID
        deviceInfo.HardwareId = TryGetHardwareId(deviceName);

        // 解析 VendorID 和 ProductID
        ParseHardwareId(deviceInfo);

        // 缓存
        _deviceCache[deviceName] = deviceInfo;

        return deviceInfo;
    }

    /// <summary>
    /// 识别设备类型
    /// </summary>
    private DeviceType IdentifyDeviceType(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return DeviceType.Unknown;

        // 标准化设备名称：去除空白字符，转小写
        var name = deviceName.Trim().ToLowerInvariant();

        // 触控板关键词（优先检测，因为触控板也可能包含"mouse"）
        var touchpadKeywords = new[] { "touchpad", "synaptics", "precision touchpad", "elantech", "alps", "trackpad" };
        foreach (var keyword in touchpadKeywords)
        {
            if (name.Contains(keyword))
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"IdentifyDeviceType: '{deviceName}' -> Touchpad (matched '{keyword}')");
                return DeviceType.Touchpad;
            }
        }

        // 键盘设备 - 不应禁用
        var keyboardKeywords = new[] { "keyboard", "keypad" };
        foreach (var keyword in keyboardKeywords)
        {
            if (name.Contains(keyword))
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"IdentifyDeviceType: '{deviceName}' -> OtherInput (keyboard, matched '{keyword}')");
                return DeviceType.OtherInput;
            }
        }

        // 内置鼠标关键词
        var internalKeywords = new[] { "internal", "builtin", "integrated" };
        foreach (var keyword in internalKeywords)
        {
            if (name.Contains(keyword))
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"IdentifyDeviceType: '{deviceName}' -> InternalMouse (matched '{keyword}')");
                return DeviceType.InternalMouse;
            }
        }

        // 外接鼠标关键词 - 包含"mouse"但不包含触控板关键词
        var mouseKeywords = new[] { "mouse", "鼠标" };
        foreach (var keyword in mouseKeywords)
        {
            if (name.Contains(keyword))
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"IdentifyDeviceType: '{deviceName}' -> ExternalMouse (matched '{keyword}')");
                return DeviceType.ExternalMouse;
            }
        }

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"IdentifyDeviceType: '{deviceName}' -> Unknown (no keywords matched)");

        return DeviceType.Unknown;
    }

    /// <summary>
    /// 尝试获取硬件ID（简化版）
    /// </summary>
    private string TryGetHardwareId(string deviceName)
    {
        try
        {
            // 使用 powercfg 获取设备列表
            var result = ExecutePowerCfg("devicequery all_devices");

            // 查找匹配的设备
            var lines = result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Contains(deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return line.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"TryGetHardwareId failed for '{deviceName}': {ex.Message}");
        }

        return string.Empty;
    }

    /// <summary>
    /// 解析硬件ID以获取 VendorID 和 ProductID
    /// </summary>
    private void ParseHardwareId(DeviceInfo deviceInfo)
    {
        try
        {
            if (string.IsNullOrEmpty(deviceInfo.HardwareId))
                return;

            // 匹配 VID_XXXX&PID_XXXX 格式
            var match = Regex.Match(deviceInfo.HardwareId, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
            if (match.Success)
            {
                deviceInfo.VendorId = Convert.ToUInt16(match.Groups[1].Value, 16);
                deviceInfo.ProductId = Convert.ToUInt16(match.Groups[2].Value, 16);
            }
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"ParseHardwareId failed for '{deviceInfo.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// 执行 powercfg 命令
    /// </summary>
    private string ExecutePowerCfg(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Verb = "runas", // 请求管理员权限
        };

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();

            // 异步读取输出以避免死锁
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            // 等待进程完成
            if (!process.WaitForExit(EXECUTION_TIMEOUT_MS))
            {
                process.Kill();
                throw new TimeoutException($"powercfg.exe timed out after {EXECUTION_TIMEOUT_MS}ms");
            }

            var output = outputTask.Result;
            var error = errorTask.Result;

            if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"powercfg exit code {process.ExitCode}: {error}");
            }

            return output;
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"ExecutePowerCfg failed: {ex.Message}");
            throw;
        }
    }
}
