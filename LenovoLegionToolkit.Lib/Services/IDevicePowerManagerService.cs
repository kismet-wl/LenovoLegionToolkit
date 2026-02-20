using System.Collections.Generic;
using LenovoLegionToolkit.Lib.Models;

namespace LenovoLegionToolkit.Lib.Services;

/// <summary>
/// 设备电源管理服务接口
/// </summary>
public interface IDevicePowerManagerService
{
    /// <summary>
    /// 获取所有可唤醒的设备
    /// </summary>
    /// <returns>可唤醒设备列表</returns>
    List<DeviceInfo> GetWakeArmedDevices();

    /// <summary>
    /// 禁用外接鼠标的唤醒权限
    /// </summary>
    /// <returns>禁用的设备数量</returns>
    int DisableExternalMouseWake();

    /// <summary>
    /// 恢复所有之前禁用的鼠标设备唤醒权限
    /// </summary>
    /// <returns>恢复的设备数量</returns>
    int RestoreAllMiceWake();

    /// <summary>
    /// 禁用指定设备的唤醒权限
    /// </summary>
    /// <param name="deviceName">设备名称</param>
    /// <returns>是否成功</returns>
    bool DisableDeviceWake(string deviceName);

    /// <summary>
    /// 启用指定设备的唤醒权限
    /// </summary>
    /// <param name="deviceName">设备名称</param>
    /// <returns>是否成功</returns>
    bool EnableDeviceWake(string deviceName);

    /// <summary>
    /// 获取已禁用唤醒的设备列表
    /// </summary>
    /// <returns>已禁用设备列表</returns>
    List<DeviceInfo> GetDisabledDevices();

    /// <summary>
    /// 清空已禁用设备列表（用于状态重置）
    /// </summary>
    void ClearDisabledDevices();
}