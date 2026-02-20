using System;
using System.Collections.Generic;
using System.Linq;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.System;

/// <summary>
/// Analyzes event sequences to identify patterns and anomalous User Presence changes.
/// </summary>
public class EventSequenceAnalyzer
{
    /// <summary>
    /// Represents a timeline event for analysis.
    /// </summary>
    public class TimelineEvent
    {
        public DateTime Timestamp { get; set; }
        public string EventType { get; set; } = string.Empty; // "UserPresence", "MonitorOn", "MonitorOff", "Sensor", "Network", "Process"
        public string Description { get; set; } = string.Empty;
        public object? Data { get; set; }

        public override string ToString()
        {
            return $"[{Timestamp:HH:mm:ss.fff}] {EventType}: {Description}";
        }
    }

    /// <summary>
    /// Represents an anomaly detected in the event sequence.
    /// </summary>
    public class Anomaly
    {
        public DateTime Timestamp { get; set; }
        public string AnomalyType { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<TimelineEvent> RelatedEvents { get; set; } = new();
        public string? PossibleCause { get; set; }
        public float ConfidenceScore { get; set; } // 0.0 to 1.0

        public override string ToString()
        {
            return $"[{Timestamp:HH:mm:ss.fff}] {AnomalyType}: {Description} (Confidence: {ConfidenceScore:F2})";
        }
    }

    private readonly List<TimelineEvent> _timeline = new();
    private readonly List<Anomaly> _detectedAnomalies = new();
    private readonly object _lock = new();

    /// <summary>
    /// Adds an event to the timeline.
    /// </summary>
    public void AddEvent(TimelineEvent @event)
    {
        lock (_lock)
        {
            _timeline.Add(@event);

            // Keep only the most recent 100 events
            if (_timeline.Count > 100)
            {
                _timeline.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Analyzes the timeline for anomalies.
    /// </summary>
    public List<Anomaly> Analyze()
    {
        lock (_lock)
        {
            _detectedAnomalies.Clear();

            // Run various anomaly detection algorithms
            DetectUserPresenceAnomalies();
            DetectRapidChanges();
            DetectMissingInputPresenceChanges();
            DetectPeriodicPatterns();
            DetectSensorTriggers();
            DetectNetworkProcessTriggers();

            return new List<Anomaly>(_detectedAnomalies);
        }
    }

    /// <summary>
    /// Detects anomalies in User Presence state changes.
    /// </summary>
    private void DetectUserPresenceAnomalies()
    {
        var presenceEvents = _timeline
            .Where(e => e.EventType == "UserPresence")
            .OrderBy(e => e.Timestamp)
            .ToList();

        for (int i = 1; i < presenceEvents.Count; i++)
        {
            var previous = presenceEvents[i - 1];
            var current = presenceEvents[i];
            var timeDiff = (current.Timestamp - previous.Timestamp).TotalSeconds;

            // Check for very rapid changes (less than 1 second)
            if (timeDiff < 1.0)
            {
                var anomaly = new Anomaly
                {
                    Timestamp = current.Timestamp,
                    AnomalyType = "RapidUserPresenceChange",
                    Description = $"User Presence changed rapidly in {timeDiff:F2} seconds",
                    RelatedEvents = new List<TimelineEvent> { previous, current },
                    PossibleCause = "Sensor noise or system instability",
                    ConfidenceScore = 0.7f
                };

                _detectedAnomalies.Add(anomaly);
            }
        }
    }

    /// <summary>
    /// Detects rapid changes in any event type.
    /// </summary>
    private void DetectRapidChanges()
    {
        // Group events by type
        var eventsByType = _timeline
            .GroupBy(e => e.EventType)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in eventsByType)
        {
            var orderedEvents = group.OrderBy(e => e.Timestamp).ToList();

            for (int i = 1; i < orderedEvents.Count; i++)
            {
                var previous = orderedEvents[i - 1];
                var current = orderedEvents[i];
                var timeDiff = (current.Timestamp - previous.Timestamp).TotalSeconds;

                // Check for very rapid events (less than 100ms)
                if (timeDiff < 0.1)
                {
                    var anomaly = new Anomaly
                    {
                        Timestamp = current.Timestamp,
                        AnomalyType = "RapidEventSequence",
                        Description = $"Rapid {group.Key} events detected ({timeDiff * 1000:F0}ms apart)",
                        RelatedEvents = new List<TimelineEvent> { previous, current },
                        PossibleCause = "System glitch or high-frequency sensor readings",
                        ConfidenceScore = 0.6f
                    };

                    _detectedAnomalies.Add(anomaly);
                }
            }
        }
    }

    /// <summary>
    /// Detects User Presence changes without corresponding user input.
    /// </summary>
    private void DetectMissingInputPresenceChanges()
    {
        var presenceEvents = _timeline
            .Where(e => e.EventType == "UserPresence")
            .OrderBy(e => e.Timestamp)
            .ToList();

        var inputEvents = _timeline
            .Where(e => e.EventType == "Keyboard" || e.EventType == "Mouse")
            .ToList();

        foreach (var presenceEvent in presenceEvents)
        {
            // Look for input events in a 5-second window before the presence change
            var inputBefore = inputEvents
                .Where(e => e.Timestamp >= presenceEvent.Timestamp.AddSeconds(-5) &&
                           e.Timestamp < presenceEvent.Timestamp)
                .ToList();

            // Look for sensor events that might explain the change
            var sensorEvents = _timeline
                .Where(e => e.EventType == "Sensor" &&
                           e.Timestamp >= presenceEvent.Timestamp.AddSeconds(-10) &&
                           e.Timestamp <= presenceEvent.Timestamp.AddSeconds(1))
                .ToList();

            // If no input events detected, and it's a change to PowerUserPresent
            if (inputBefore.Count == 0 &&
                presenceEvent.Description.Contains("PowerUserPresent") &&
                sensorEvents.Count == 0)
            {
                var anomaly = new Anomaly
                {
                    Timestamp = presenceEvent.Timestamp,
                    AnomalyType = "MissingInputPresenceChange",
                    Description = "User Presence changed to PowerUserPresent without detected input",
                    RelatedEvents = new List<TimelineEvent> { presenceEvent },
                    PossibleCause = "Unknown sensor trigger, system maintenance, or background service",
                    ConfidenceScore = 0.8f
                };

                _detectedAnomalies.Add(anomaly);
            }
        }
    }

    /// <summary>
    /// Detects periodic patterns in events.
    /// </summary>
    private void DetectPeriodicPatterns()
    {
        // Look for User Presence changes that occur at regular intervals
        var presenceEvents = _timeline
            .Where(e => e.EventType == "UserPresence")
            .OrderBy(e => e.Timestamp)
            .ToList();

        if (presenceEvents.Count < 3)
            return;

        // Check for consistent intervals (within 10% tolerance)
        var intervals = new List<double>();
        for (int i = 1; i < presenceEvents.Count; i++)
        {
            var diff = (presenceEvents[i].Timestamp - presenceEvents[i - 1].Timestamp).TotalSeconds;
            intervals.Add(diff);
        }

        // Calculate average interval
        var avgInterval = intervals.Average();

        // Check if intervals are consistent
        var consistentCount = intervals.Count(i => Math.Abs(i - avgInterval) < avgInterval * 0.1);

        if (consistentCount >= intervals.Count * 0.7)
        {
            var anomaly = new Anomaly
            {
                Timestamp = DateTime.Now,
                AnomalyType = "PeriodicPattern",
                Description = $"Periodic User Presence changes detected (interval: {avgInterval:F0}s)",
                RelatedEvents = presenceEvents,
                PossibleCause = "Scheduled task, maintenance timer, or sensor polling",
                ConfidenceScore = 0.9f
            };

            _detectedAnomalies.Add(anomaly);
        }
    }

    /// <summary>
    /// Detects sensor-triggered events.
    /// </summary>
    private void DetectSensorTriggers()
    {
        var sensorEvents = _timeline
            .Where(e => e.EventType == "Sensor")
            .OrderBy(e => e.Timestamp)
            .ToList();

        var presenceEvents = _timeline
            .Where(e => e.EventType == "UserPresence")
            .OrderBy(e => e.Timestamp)
            .ToList();

        foreach (var sensorEvent in sensorEvents)
        {
            // Look for User Presence changes within 1 second of sensor events
            var nearbyPresenceEvents = presenceEvents
                .Where(e => Math.Abs((e.Timestamp - sensorEvent.Timestamp).TotalSeconds) < 1.0)
                .ToList();

            if (nearbyPresenceEvents.Count > 0)
            {
                var anomaly = new Anomaly
                {
                    Timestamp = sensorEvent.Timestamp,
                    AnomalyType = "SensorTrigger",
                    Description = $"Sensor activity: {sensorEvent.Description}",
                    RelatedEvents = new List<TimelineEvent> { sensorEvent }.Concat(nearbyPresenceEvents).ToList(),
                    PossibleCause = "Sensor-based User Presence detection",
                    ConfidenceScore = 0.85f
                };

                _detectedAnomalies.Add(anomaly);
            }
        }
    }

    /// <summary>
    /// Detects network or process-triggered events.
    /// </summary>
    private void DetectNetworkProcessTriggers()
    {
        var networkEvents = _timeline
            .Where(e => e.EventType == "Network")
            .OrderBy(e => e.Timestamp)
            .ToList();

        var processEvents = _timeline
            .Where(e => e.EventType == "Process")
            .OrderBy(e => e.Timestamp)
            .ToList();

        var presenceEvents = _timeline
            .Where(e => e.EventType == "UserPresence")
            .OrderBy(e => e.Timestamp)
            .ToList();

        // Check for network triggers
        foreach (var networkEvent in networkEvents)
        {
            var nearbyPresenceEvents = presenceEvents
                .Where(e => Math.Abs((e.Timestamp - networkEvent.Timestamp).TotalSeconds) < 2.0)
                .ToList();

            if (nearbyPresenceEvents.Count > 0)
            {
                var anomaly = new Anomaly
                {
                    Timestamp = networkEvent.Timestamp,
                    AnomalyType = "NetworkTrigger",
                    Description = $"Network activity: {networkEvent.Description}",
                    RelatedEvents = new List<TimelineEvent> { networkEvent }.Concat(nearbyPresenceEvents).ToList(),
                    PossibleCause = "Network activity triggered User Presence change",
                    ConfidenceScore = 0.5f
                };

                _detectedAnomalies.Add(anomaly);
            }
        }

        // Check for process triggers
        foreach (var processEvent in processEvents)
        {
            var nearbyPresenceEvents = presenceEvents
                .Where(e => Math.Abs((e.Timestamp - processEvent.Timestamp).TotalSeconds) < 2.0)
                .ToList();

            if (nearbyPresenceEvents.Count > 0)
            {
                var anomaly = new Anomaly
                {
                    Timestamp = processEvent.Timestamp,
                    AnomalyType = "ProcessTrigger",
                    Description = $"Process activity: {processEvent.Description}",
                    RelatedEvents = new List<TimelineEvent> { processEvent }.Concat(nearbyPresenceEvents).ToList(),
                    PossibleCause = "Process activity triggered User Presence change",
                    ConfidenceScore = 0.6f
                };

                _detectedAnomalies.Add(anomaly);
            }
        }
    }

    /// <summary>
    /// Gets the timeline.
    /// </summary>
    public List<TimelineEvent> GetTimeline()
    {
        lock (_lock)
        {
            return new List<TimelineEvent>(_timeline);
        }
    }

    /// <summary>
    /// Gets timeline events from a specific time range.
    /// </summary>
    public List<TimelineEvent> GetTimeline(DateTime startTime, DateTime endTime)
    {
        lock (_lock)
        {
            return _timeline
                .Where(e => e.Timestamp >= startTime && e.Timestamp <= endTime)
                .ToList();
        }
    }

    /// <summary>
    /// Gets detected anomalies.
    /// </summary>
    public List<Anomaly> GetAnomalies()
    {
        lock (_lock)
        {
            return new List<Anomaly>(_detectedAnomalies);
        }
    }

    /// <summary>
    /// Clears the timeline and anomalies.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _timeline.Clear();
            _detectedAnomalies.Clear();
        }
    }

    /// <summary>
    /// Gets a summary of the analysis.
    /// </summary>
    public string GetSummary()
    {
        lock (_lock)
        {
            var summary = "Event Sequence Analysis Summary:\n";
            summary += $"Total Events: {_timeline.Count}\n";
            summary += $"Detected Anomalies: {_detectedAnomalies.Count}\n";

            if (_detectedAnomalies.Count > 0)
            {
                summary += "\nAnomalies:\n";

                foreach (var anomaly in _detectedAnomalies.OrderByDescending(a => a.ConfidenceScore))
                {
                    summary += $"  - {anomaly}\n";
                    if (!string.IsNullOrEmpty(anomaly.PossibleCause))
                    {
                        summary += $"    Possible Cause: {anomaly.PossibleCause}\n";
                    }
                }
            }

            return summary;
        }
    }
}
