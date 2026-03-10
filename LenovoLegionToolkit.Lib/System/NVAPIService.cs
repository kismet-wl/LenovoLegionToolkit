using System;
using System.Linq;
using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native.Exceptions;
using NvAPIWrapper.Native.GPU;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.System;

/// <summary>
/// NVAPI 全局管理服务，避免重复初始化/卸载导致的 GPU 驱动冲突
/// </summary>
public class NVAPIService
{
    private readonly object _lock = new();
    private bool _isInitialized;
    private bool _isInitializing;

    public bool IsInitialized => _isInitialized;

    /// <summary>
    /// 初始化 NVAPI。如果已初始化则直接返回。
    /// </summary>
    public void Initialize()
    {
        lock (_lock)
        {
            if (_isInitialized || _isInitializing)
                return;

            _isInitializing = true;
            try
            {
                NVIDIA.Initialize();
                _isInitialized = true;

                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[NVAPIService] NVAPI initialized");
            }
            catch (Exception ex)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[NVAPIService] Failed to initialize NVAPI", ex);
                throw;
            }
            finally
            {
                _isInitializing = false;
            }
        }
    }

    /// <summary>
    /// 卸载 NVAPI。在应用退出时调用。
    /// </summary>
    public void Unload()
    {
        lock (_lock)
        {
            if (!_isInitialized)
                return;

            try
            {
                NVIDIA.Unload();
                _isInitialized = false;

                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[NVAPIService] NVAPI unloaded");
            }
            catch (Exception ex)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[NVAPIService] Failed to unload NVAPI", ex);
            }
        }
    }

    /// <summary>
    /// 获取笔记本 GPU。如果 NVAPI 未初始化会自动初始化。
    /// </summary>
    public PhysicalGPU? GetGPU()
    {
        EnsureInitialized();

        try
        {
            return PhysicalGPU.GetPhysicalGPUs().FirstOrDefault(gpu => gpu.SystemType == SystemType.Laptop);
        }
        catch (NVIDIAApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// 检查是否有可用的 NVIDIA GPU
    /// </summary>
    public bool HasGPU()
    {
        return GetGPU() is not null;
    }

    /// <summary>
    /// 确保 NVAPI 已初始化
    /// </summary>
    private void EnsureInitialized()
    {
        if (!_isInitialized)
            Initialize();
    }
}
