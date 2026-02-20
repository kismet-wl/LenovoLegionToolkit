using System;
using System.Collections.Generic;
using System.Linq;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.System;

/// <summary>
/// Tracks and records User Presence state changes to help diagnose monitor wake-up issues.
/// </summary>
public class UserPresenceTracker
{
    private const int MaxEventHistory = 50;
    private readonly object _lock = new();
    private readonly List<UserPresenceEvent> _eventHistory = new();

    /// <summary>
    /// Represents a single User Presence state change event.
    /// </summary>
    public class UserPresenceEvent
    {
        public DateTime Timestamp { get; set; }
        public PInvokeExtensions.USER_ACTIVITY_PRESENCE PreviousState { get; set; }
        public PInvokeExtensions.USER_ACTIVITY_PRESENCE CurrentState { get; set; }
        public string? TriggerSource { get; set; }
        public string? SystemStateSnapshot { get; set; }

        public override string ToString()
        {
            return $"[{Timestamp:HH:mm:ss.fff}] {PreviousState} -> {CurrentState} (Trigger: {TriggerSource ?? "Unknown"})";
        }
    }

    /// <summary>
    /// Records a User Presence state change event.
    /// </summary>
    /// <param name="previousState">Previous User Presence state</param>
    /// <param name="currentState">New User Presence state</param>
    /// <param name="triggerSource">Optional: Source that triggered the change</param>
    public void RecordEvent(
        PInvokeExtensions.USER_ACTIVITY_PRESENCE previousState,
        PInvokeExtensions.USER_ACTIVITY_PRESENCE currentState,
        string? triggerSource = null)
    {
        var @event = new UserPresenceEvent
        {
            Timestamp = DateTime.Now,
            PreviousState = previousState,
            CurrentState = currentState,
            TriggerSource = triggerSource,
            SystemStateSnapshot = CaptureSystemState()
        };

        lock (_lock)
        {
            _eventHistory.Add(@event);

            // Keep only the most recent events
            if (_eventHistory.Count > MaxEventHistory)
            {
                _eventHistory.RemoveAt(0);
            }
        }

        if (Log.Instance.IsTraceEnabled)
        {
            Log.Instance.Trace($"[UserPresenceTracker] Event recorded: {@event}");
        }
    }

    /// <summary>
    /// Gets all recorded User Presence events.
    /// </summary>
    public List<UserPresenceEvent> GetAllEvents()
    {
        lock (_lock)
        {
            return new List<UserPresenceEvent>(_eventHistory);
        }
    }

    /// <summary>
    /// Gets the most recent User Presence event.
    /// </summary>
    public UserPresenceEvent? GetMostRecentEvent()
    {
        lock (_lock)
        {
            return _eventHistory.LastOrDefault();
        }
    }

    /// <summary>
    /// Gets User Presence events that occurred within a specific time range.
    /// </summary>
    public List<UserPresenceEvent> GetEventsInRange(DateTime startTime, DateTime endTime)
    {
        lock (_lock)
        {
            return _eventHistory
                .Where(e => e.Timestamp >= startTime && e.Timestamp <= endTime)
                .ToList();
        }
    }

    /// <summary>
    /// Gets User Presence events that occurred in the last N minutes.
    /// </summary>
    public List<UserPresenceEvent> GetRecentEvents(int minutes)
    {
        var startTime = DateTime.Now.AddMinutes(-minutes);
        return GetEventsInRange(startTime, DateTime.Now);
    }

    /// <summary>
    /// Gets events that match a specific state transition.
    /// </summary>
    public List<UserPresenceEvent> GetEventsByTransition(
        PInvokeExtensions.USER_ACTIVITY_PRESENCE fromState,
        PInvokeExtensions.USER_ACTIVITY_PRESENCE toState)
    {
        lock (_lock)
        {
            return _eventHistory
                .Where(e => e.PreviousState == fromState && e.CurrentState == toState)
                .ToList();
        }
    }

    /// <summary>
    /// Gets events that resulted in a specific state.
    /// </summary>
    public List<UserPresenceEvent> GetEventsByState(PInvokeExtensions.USER_ACTIVITY_PRESENCE state)
    {
        lock (_lock)
        {
            return _eventHistory
                .Where(e => e.CurrentState == state)
                .ToList();
        }
    }

    /// <summary>
    /// Clears all recorded events.
    /// </summary>
    public void ClearHistory()
    {
        lock (_lock)
        {
            _eventHistory.Clear();
        }

        if (Log.Instance.IsTraceEnabled)
                    {
                        Log.Instance.Trace($"[UserPresenceTracker] Event history cleared");
                    }    }

    /// <summary>
    /// Gets a summary of the recorded events.
    /// </summary>
    public string GetSummary()
    {
        lock (_lock)
        {
            if (_eventHistory.Count == 0)
                return "No User Presence events recorded";

            var summary = $"User Presence Event History ({_eventHistory.Count} events):\n";

            foreach (var @event in _eventHistory)
            {
                summary += $"  {@event}\n";
            }

            // Add statistics
            var presentCount = _eventHistory.Count(e => e.CurrentState == PInvokeExtensions.USER_ACTIVITY_PRESENCE.PowerUserPresent);
            var notPresentCount = _eventHistory.Count(e => e.CurrentState == PInvokeExtensions.USER_ACTIVITY_PRESENCE.PowerUserNotPresent);
            var inactiveCount = _eventHistory.Count(e => e.CurrentState == PInvokeExtensions.USER_ACTIVITY_PRESENCE.PowerUserInactive);

            summary += $"\nStatistics:\n";
            summary += $"  Total events: {_eventHistory.Count}\n";
            summary += $"  PowerUserPresent: {presentCount}\n";
            summary += $"  PowerUserNotPresent: {notPresentCount}\n";
            summary += $"  PowerUserInactive: {inactiveCount}\n";

            return summary;
        }
    }

    /// <summary>
    /// Captures a comprehensive system state snapshot.
    /// </summary>
    private string CaptureSystemState()
    {
        try
        {
            var snapshot = SystemStateSnapshot.Capture();
            return snapshot.ToCompactString();
        }
        catch (Exception ex)
        {
            if (Log.Instance.IsTraceEnabled)
                Log.Instance.Trace($"[UserPresenceTracker] Failed to capture system state: {ex.Message}");

            return $"Error: {ex.Message}";
        }
    }
}