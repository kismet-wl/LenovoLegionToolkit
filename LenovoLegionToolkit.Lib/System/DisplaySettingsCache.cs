using System;
using System.Collections.Generic;
using System.Linq;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.Listeners;
using LenovoLegionToolkit.Lib.Utils;
using WindowsDisplayAPI;
using WindowsDisplayAPI.Native.DeviceContext;

namespace LenovoLegionToolkit.Lib.System;

/// <summary>
/// 显示设置缓存服务，避免多个控制项重复查询显示设置
/// </summary>
public class DisplaySettingsCache : IDisposable
{
    private readonly NativeWindowsMessageListener _windowsMessageListener;
    private readonly DisplayConfigurationListener _displayConfigurationListener;
    private readonly object _lock = new();

    // 缓存的显示设备
    private Display? _cachedDisplay;
    private DateTime _displayCacheTime;
    private readonly TimeSpan _displayCacheTimeout = TimeSpan.FromSeconds(5);

    // 缓存的可能设置
    private DisplayPossibleSetting[]? _cachedPossibleSettings;
    private DateTime _possibleSettingsCacheTime;
    private readonly TimeSpan _possibleSettingsCacheTimeout = TimeSpan.FromSeconds(2);

    // 缓存的当前设置
    private DisplaySetting? _cachedCurrentSetting;
    private DateTime _currentSettingCacheTime;
    private readonly TimeSpan _currentSettingCacheTimeout = TimeSpan.FromSeconds(1);

    private bool _disposed;

    public DisplaySettingsCache(NativeWindowsMessageListener windowsMessageListener, DisplayConfigurationListener displayConfigurationListener)
    {
        _windowsMessageListener = windowsMessageListener;
        _displayConfigurationListener = displayConfigurationListener;
        
        _windowsMessageListener.Changed += OnWindowsMessageChanged;
        _windowsMessageListener.MonitorStateChanged += OnMonitorStateChanged;
        _displayConfigurationListener.Changed += OnDisplayConfigurationChanged;
    }

    /// <summary>
    /// 获取内置显示器，带缓存
    /// </summary>
    public Display? GetDisplay()
    {
        lock (_lock)
        {
            // 检查缓存是否有效
            if (_cachedDisplay is not null && DateTime.Now - _displayCacheTime < _displayCacheTimeout)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[DisplaySettingsCache] Returning cached display");
                return _cachedDisplay;
            }

            // 重新获取
            _cachedDisplay = InternalDisplay.Get();
            _displayCacheTime = DateTime.Now;

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[DisplaySettingsCache] Display refreshed: {_cachedDisplay}");

            return _cachedDisplay;
        }
    }

    /// <summary>
    /// 获取所有可能的显示设置，带缓存
    /// </summary>
    public DisplayPossibleSetting[] GetPossibleSettings()
    {
        lock (_lock)
        {
            // 检查缓存是否有效
            if (_cachedPossibleSettings is not null && DateTime.Now - _possibleSettingsCacheTime < _possibleSettingsCacheTimeout)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[DisplaySettingsCache] Returning cached possible settings");
                return _cachedPossibleSettings;
            }

            // 获取显示器（已在锁内，不会重复获取）
            var display = GetDisplayInternal();
            if (display is null)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[DisplaySettingsCache] Display not found, returning empty array");
                return Array.Empty<DisplayPossibleSetting>();
            }

            // 重新获取可能设置
            _cachedPossibleSettings = display.GetPossibleSettings().ToArray();
            _possibleSettingsCacheTime = DateTime.Now;

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[DisplaySettingsCache] Possible settings refreshed, count: {_cachedPossibleSettings?.Length ?? 0}");

            return _cachedPossibleSettings ?? Array.Empty<DisplayPossibleSetting>();
        }
    }

    /// <summary>
    /// 获取当前显示设置，带缓存
    /// </summary>
    public DisplaySetting? GetCurrentSetting()
    {
        lock (_lock)
        {
            // 检查缓存是否有效
            if (_cachedCurrentSetting is not null && DateTime.Now - _currentSettingCacheTime < _currentSettingCacheTimeout)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[DisplaySettingsCache] Returning cached current setting");
                return _cachedCurrentSetting;
            }

            // 获取显示器（已在锁内，不会重复获取）
            var display = GetDisplayInternal();
            if (display is null)
            {
                if (Log.Instance.IsTraceEnabled)
                    Log.Instance.Trace($"[DisplaySettingsCache] Display not found for current setting");
                return null;
            }

            // 获取当前设置
            _cachedCurrentSetting = display.CurrentSetting;
            _currentSettingCacheTime = DateTime.Now;

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[DisplaySettingsCache] Current setting refreshed: {_cachedCurrentSetting?.ToExtendedString()}");

            return _cachedCurrentSetting;
        }
    }

    /// <summary>
    /// 内部方法：获取显示器（不加锁，由调用方保证线程安全）
    /// </summary>
    private Display? GetDisplayInternal()
    {
        // 检查缓存是否有效
        if (_cachedDisplay is not null && DateTime.Now - _displayCacheTime < _displayCacheTimeout)
        {
            return _cachedDisplay;
        }

        // 重新获取
        _cachedDisplay = InternalDisplay.Get();
        _displayCacheTime = DateTime.Now;

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"[DisplaySettingsCache] Display refreshed: {_cachedDisplay}");

        return _cachedDisplay;
    }

    /// <summary>
    /// 获取指定分辨率的可能刷新率列表
    /// </summary>
    public IEnumerable<int> GetRefreshRatesForResolution(Resolution resolution)
    {
        var currentSetting = GetCurrentSetting();
        if (currentSetting is null)
            return Enumerable.Empty<int>();

        var possibleSettings = GetPossibleSettings();
        return possibleSettings
            .Where(dps => MatchForRefreshRate(dps, currentSetting, resolution))
            .Select(dps => dps.Frequency)
            .Distinct()
            .OrderBy(freq => freq);
    }

    /// <summary>
    /// 获取指定刷新率的可能分辨率列表
    /// </summary>
    public IEnumerable<Resolution> GetResolutionsForRefreshRate(int frequency)
    {
        var currentSetting = GetCurrentSetting();
        if (currentSetting is null)
            return Enumerable.Empty<Resolution>();

        var possibleSettings = GetPossibleSettings();
        return possibleSettings
            .Where(dps => MatchForResolution(dps, currentSetting, frequency))
            .Select(dps => dps.Resolution)
            .Select(res => new Resolution(res))
            .Distinct()
            .OrderByDescending(res => res);
    }

    /// <summary>
    /// 清除所有缓存
    /// </summary>
    public void ClearCache()
    {
        lock (_lock)
        {
            _cachedDisplay = null;
            _cachedPossibleSettings = null;
            _cachedCurrentSetting = null;

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[DisplaySettingsCache] Cache cleared");
        }
    }

    private void OnWindowsMessageChanged(object? sender, NativeWindowsMessageListener.ChangedEventArgs e)
    {
        // 显示相关事件发生时清除缓存
        if (e.Message == NativeWindowsMessage.OnDisplayDeviceArrival ||
            e.Message == NativeWindowsMessage.MonitorConnected ||
            e.Message == NativeWindowsMessage.MonitorDisconnected ||
            e.Message == NativeWindowsMessage.ExternalMonitorConnected ||
            e.Message == NativeWindowsMessage.ExternalMonitorDisconnected)
        {
            ClearCache();
        }
    }

    private void OnMonitorStateChanged(object? sender, bool isMonitorOn)
    {
        // 显示器开关时清除缓存
        ClearCache();
    }

    private void OnDisplayConfigurationChanged(object? sender, DisplayConfigurationListener.ChangedEventArgs e)
    {
        // 分辨率/刷新率变化时清除缓存
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"[DisplaySettingsCache] Display configuration changed, clearing cache");
        ClearCache();
    }

    private static bool MatchForRefreshRate(DisplayPossibleSetting dps, DisplaySetting current, Resolution targetResolution)
    {
        if (dps.IsTooSmall())
            return false;

        return dps.Resolution.Width == targetResolution.Width &&
               dps.Resolution.Height == targetResolution.Height &&
               dps.ColorDepth == current.ColorDepth &&
               dps.IsInterlaced == current.IsInterlaced;
    }

    private static bool MatchForResolution(DisplayPossibleSetting dps, DisplaySetting current, int targetFrequency)
    {
        if (dps.IsTooSmall())
            return false;

        return dps.Frequency == targetFrequency &&
               dps.ColorDepth == current.ColorDepth &&
               dps.IsInterlaced == current.IsInterlaced;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _windowsMessageListener.Changed -= OnWindowsMessageChanged;
        _windowsMessageListener.MonitorStateChanged -= OnMonitorStateChanged;
        _displayConfigurationListener.Changed -= OnDisplayConfigurationChanged;
        _disposed = true;
    }
}
