using System;
using System.Collections.Generic;
using System.Linq;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.System;

/// <summary>
/// Validates input events to distinguish between real user input and false input.
/// Uses multiple detection methods (GetLastInputInfo, Raw Input, Low-level hooks) for cross-validation.
/// </summary>
public class InputValidator
{
    private readonly RawInputMonitor _rawInputMonitor;
    private DateTime _lastHookInputTime = DateTime.MinValue;
    private readonly List<FalseInputEvent> _falseInputHistory = new();
    private readonly object _lock = new object();

    public InputValidator(RawInputMonitor rawInputMonitor)
    {
        _rawInputMonitor = rawInputMonitor ?? throw new ArgumentNullException(nameof(rawInputMonitor));
    }

    /// <summary>
    /// Validates input detected by GetLastInputInfo.
    /// </summary>
    /// <param name="getlastInputIdleMs">Idle time in milliseconds from GetLastInputInfo</param>
    /// <returns>Validation result</returns>
    public ValidationResult ValidateInput(int getlastInputIdleMs)
    {
        var result = new ValidationResult
        {
            Timestamp = DateTime.Now,
            GetLastInputIdleMs = getlastInputIdleMs
        };

        // GetLastInputInfo detected input
        if (getlastInputIdleMs < 500)
        {
            result.GetLastInputDetected = true;

            // Check if low-level hook detected input
            var hookTimeSince = DateTime.Now - _lastHookInputTime;
            result.HookDetected = hookTimeSince.TotalMilliseconds < 1000;
            result.HookTimeSinceMs = (int)hookTimeSince.TotalMilliseconds;

            // Check if Raw Input detected input
            var rawInputEvents = _rawInputMonitor.GetRecentEvents(TimeSpan.FromSeconds(1));
            result.RawInputDetected = rawInputEvents.Count > 0;
            result.RawInputEventCount = rawInputEvents.Count;

            if (rawInputEvents.Count > 0)
            {
                result.RawInputDevices = rawInputEvents.Select(e => e.DeviceName).Distinct().ToList();
                result.RawInputEventTypes = rawInputEvents.Select(e => e.Type.ToString()).Distinct().ToList();
            }

            // Validate input
            result.IsValid = ValidateInputInternal(result);

            if (!result.IsValid)
            {
                // Record false input event
                RecordFalseInputEvent(result);
            }
        }

        return result;
    }

    /// <summary>
    /// Internal validation logic.
    /// </summary>
    private bool ValidateInputInternal(ValidationResult result)
    {
        // If hook detected input, it's valid
        if (result.HookDetected)
        {
            result.Reason = "Valid: Low-level hook detected input";
            result.Confidence = 0.95;
            return true;
        }

        // If Raw Input detected input, it's likely valid
        if (result.RawInputDetected)
        {
            result.Reason = "Valid: Raw Input detected input from hardware devices";
            result.Confidence = 0.90;
            return true;
        }

        // Neither hook nor Raw Input detected input
        // This is likely a false input from GetLastInputInfo
        result.Reason = "Invalid: GetLastInputInfo detected input but no other detection method confirmed it";
        result.Confidence = 0.85;
        result.SuspectedSources = AnalyzeSuspectedSources();

        return false;
    }

    /// <summary>
    /// Analyzes suspected sources of false input.
    /// </summary>
    private List<string> AnalyzeSuspectedSources()
    {
        var sources = new List<string>();

        // Check recent system events
        sources.Add("System internal event (possible causes: touchpad driver, device driver, Modern Standby)");
        sources.Add("Network activity (possible WOL packet or network traffic)");
        sources.Add("Power management event");
        sources.Add("USB device state change");
        sources.Add("Touchpad/touchscreen interference");

        return sources;
    }

    /// <summary>
    /// Records a false input event for analysis.
    /// </summary>
    private void RecordFalseInputEvent(ValidationResult result)
    {
        lock (_lock)
        {
            var falseEvent = new FalseInputEvent
            {
                Timestamp = DateTime.Now,
                GetLastInputIdleMs = result.GetLastInputIdleMs,
                HookDetected = result.HookDetected,
                RawInputDetected = result.RawInputDetected,
                RawInputEventCount = result.RawInputEventCount,
                Reason = result.Reason,
                SuspectedSources = result.SuspectedSources
            };

            _falseInputHistory.Add(falseEvent);

            // Keep only last 100 false input events
            if (_falseInputHistory.Count > 100)
            {
                _falseInputHistory.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Gets the history of false input events.
    /// </summary>
    public List<FalseInputEvent> GetFalseInputHistory(int count = 10)
    {
        lock (_lock)
        {
            return _falseInputHistory.TakeLast(count).ToList();
        }
    }

    /// <summary>
    /// Gets statistics about false input events.
    /// </summary>
    public FalseInputStatistics GetStatistics(TimeSpan timeWindow)
    {
        lock (_lock)
        {
            var cutoff = DateTime.Now - timeWindow;
            var recentEvents = _falseInputHistory.Where(e => e.Timestamp >= cutoff).ToList();

            var stats = new FalseInputStatistics
            {
                TimeWindow = timeWindow,
                TotalFalseInputs = recentEvents.Count,
                AverageIdleMs = recentEvents.Any() ? (int)recentEvents.Average(e => e.GetLastInputIdleMs) : 0
            };

            if (recentEvents.Any())
            {
                stats.MinIdleMs = recentEvents.Min(e => e.GetLastInputIdleMs);
                stats.MaxIdleMs = recentEvents.Max(e => e.GetLastInputIdleMs);
                stats.FirstEvent = recentEvents.First().Timestamp;
                stats.LastEvent = recentEvents.Last().Timestamp;
            }

            return stats;
        }
    }

    /// <summary>
    /// Called when a low-level hook detects input.
    /// </summary>
    public void OnHookInput()
    {
        _lastHookInputTime = DateTime.Now;
    }

    /// <summary>
    /// Clears the false input history.
    /// </summary>
    public void ClearHistory()
    {
        lock (_lock)
        {
            _falseInputHistory.Clear();
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[InputValidator] False input history cleared");
        }
    }
}

/// <summary>
/// Result of input validation.
/// </summary>
public class ValidationResult
{
    public DateTime Timestamp { get; set; }
    public int GetLastInputIdleMs { get; set; }
    public bool GetLastInputDetected { get; set; }
    public bool HookDetected { get; set; }
    public int HookTimeSinceMs { get; set; }
    public bool RawInputDetected { get; set; }
    public int RawInputEventCount { get; set; }
    public List<string> RawInputDevices { get; set; } = new();
    public List<string> RawInputEventTypes { get; set; } = new();
    public bool IsValid { get; set; }
    public string Reason { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public List<string> SuspectedSources { get; set; } = new();

    public override string ToString()
    {
        return $"ValidationResult: IsValid={IsValid}, GetLastInputIdleMs={GetLastInputIdleMs}ms, HookDetected={HookDetected}, RawInputDetected={RawInputDetected}, Confidence={Confidence:F2}";
    }
}

/// <summary>
/// False input event record.
/// </summary>
public class FalseInputEvent
{
    public DateTime Timestamp { get; set; }
    public int GetLastInputIdleMs { get; set; }
    public bool HookDetected { get; set; }
    public bool RawInputDetected { get; set; }
    public int RawInputEventCount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public List<string> SuspectedSources { get; set; } = new();

    public override string ToString()
    {
        return $"FalseInputEvent: {Timestamp:yyyy-MM-dd HH:mm:ss.fff}, IdleMs={GetLastInputIdleMs}, Hook={HookDetected}, RawInput={RawInputDetected}, Reason={Reason}";
    }
}

/// <summary>
/// Statistics about false input events.
/// </summary>
public class FalseInputStatistics
{
    public TimeSpan TimeWindow { get; set; }
    public int TotalFalseInputs { get; set; }
    public int AverageIdleMs { get; set; }
    public int MinIdleMs { get; set; }
    public int MaxIdleMs { get; set; }
    public DateTime? FirstEvent { get; set; }
    public DateTime? LastEvent { get; set; }

    public override string ToString()
    {
        return $"FalseInputStatistics: Window={TimeWindow.TotalMinutes:F1}min, Total={TotalFalseInputs}, AvgIdleMs={AverageIdleMs}ms, Min={MinIdleMs}ms, Max={MaxIdleMs}ms";
    }
}