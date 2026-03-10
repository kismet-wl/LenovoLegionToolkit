using System;
using System.Linq;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.System;
using LenovoLegionToolkit.Lib.Utils;
using WindowsDisplayAPI;
using WindowsDisplayAPI.Native.DeviceContext;

namespace LenovoLegionToolkit.Lib.Features;

public class ResolutionFeature : IFeature<Resolution>
{
    private readonly DisplaySettingsCache _displaySettingsCache;

    public ResolutionFeature(DisplaySettingsCache displaySettingsCache)
    {
        _displaySettingsCache = displaySettingsCache;
    }

    public Task<bool> IsSupportedAsync() => Task.FromResult(true);

    public Task<Resolution[]> GetAllStatesAsync()
    {
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Getting all resolutions...");

        var currentSettings = _displaySettingsCache.GetCurrentSetting();
        if (currentSettings is null)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Built in display not found");

            return Task.FromResult(Array.Empty<Resolution>());
        }

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Current built in display settings: {currentSettings.ToExtendedString()}");

        var possibleSettings = _displaySettingsCache.GetPossibleSettings();
        var result = possibleSettings
            .Where(dps => Match(dps, currentSettings))
            .Select(dps => dps.Resolution)
            .Select(res => new Resolution(res))
            .Distinct()
            .OrderByDescending(res => res)
            .ToArray();

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Possible resolutions are {string.Join(", ", result)}");

        return Task.FromResult(result);
    }

    public Task<Resolution> GetStateAsync()
    {
        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Getting current resolution...");

        var currentSettings = _displaySettingsCache.GetCurrentSetting();
        if (currentSettings is null)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Built in display not found");

            return Task.FromResult(default(Resolution));
        }

        var result = new Resolution(currentSettings.Resolution);

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Current resolution is {result} [currentSettings={currentSettings.ToExtendedString()}]");

        return Task.FromResult(result);
    }

    public Task SetStateAsync(Resolution state)
    {
        var display = _displaySettingsCache.GetDisplay();
        if (display is null)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Built in display not found");
            throw new InvalidOperationException("Built in display not found");
        }

        var currentSettings = _displaySettingsCache.GetCurrentSetting();
        if (currentSettings is null)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Current settings not found");
            throw new InvalidOperationException("Current settings not found");
        }

        if (currentSettings.Resolution == state)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Resolution already set to {state}");

            return Task.CompletedTask;
        }

        var possibleSettings = _displaySettingsCache.GetPossibleSettings();

        if (Log.Instance.IsTraceEnabled)
            Log.Instance.Trace($"Current built in display settings: {currentSettings.ToExtendedString()}");

        var newSettings = possibleSettings
            .Where(dps => Match(dps, currentSettings))
            .Where(dps => dps.Resolution == state)
            .Select(dps => new DisplaySetting(dps, currentSettings.Position, currentSettings.Orientation, DisplayFixedOutput.Default))
            .FirstOrDefault();

        if (newSettings is not null)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Setting display to {newSettings.ToExtendedString()}");

            display.SetSettingsUsingPathInfo(newSettings);
            
            // 清除缓存，因为设置已更改
            _displaySettingsCache.ClearCache();

            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Display set to {newSettings.ToExtendedString()}");
        }
        else
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"Could not find matching settings for resolution {state}");
        }

        return Task.CompletedTask;
    }

    private static bool Match(DisplayPossibleSetting dps, DisplaySetting ds)
    {
        if (dps.IsTooSmall())
            return false;

        var result = true;
        result &= dps.Frequency == ds.Frequency;
        result &= dps.ColorDepth == ds.ColorDepth;
        result &= dps.IsInterlaced == ds.IsInterlaced;
        return result;
    }
}
