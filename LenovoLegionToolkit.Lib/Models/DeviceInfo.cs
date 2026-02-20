namespace LenovoLegionToolkit.Lib.Models;

/// <summary>
/// 设备类型枚举
/// </summary>
public enum DeviceType
{
    Unknown,
    Touchpad,
    ExternalMouse,
    InternalMouse,
    OtherInput
}

/// <summary>
/// 设备信息
/// </summary>
public class DeviceInfo
{
    /// <summary>
    /// 设备名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 设备类型
    /// </summary>
    public DeviceType Type { get; set; }

    /// <summary>
    /// 硬件ID
    /// </summary>
    public string HardwareId { get; set; } = string.Empty;

    /// <summary>
    /// 厂商ID
    /// </summary>
    public ushort VendorId { get; set; }

    /// <summary>
    /// 产品ID
    /// </summary>
    public ushort ProductId { get; set; }

    /// <summary>
    /// 是否启用唤醒权限
    /// </summary>
    public bool IsWakeEnabled { get; set; }

    /// <summary>
    /// 是否为外部设备
    /// </summary>
    public bool IsExternal { get; set; }

    /// <summary>
    /// 原始状态（用于恢复）
    /// </summary>
    public string OriginalState { get; set; } = string.Empty;

    public override string ToString()
    {
        return $"[{Type}] {Name} (Vendor: 0x{VendorId:X4}, Product: 0x{ProductId:X4}, Wake: {IsWakeEnabled})";
    }
}