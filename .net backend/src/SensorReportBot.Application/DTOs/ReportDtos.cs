namespace SensorReportBot.Application.DTOs;

using System;
using System.Collections.Generic;

public class AssetDto
{
    public int AssetId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string AssetType { get; set; } = string.Empty;
    public string? Location { get; set; }
}

public class SignalDto
{
    public int SignalId { get; set; }
    public int AssetId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Unit { get; set; }
    public double MinValue { get; set; }
    public double MaxValue { get; set; }
    public double CriticalMinValue { get; set; } = -2.0;
    public double CriticalMaxValue { get; set; } = 12.0;
}

public class ReportRequestDto
{
    public int AssetId { get; set; }
    public List<int>? SignalIds { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public bool IncludeEvents { get; set; } = true;
    public bool IncludeAlerts { get; set; } = true;
    public bool IncludeInsights { get; set; } = true;
    public bool IncludeCharts { get; set; } = true;
    public bool IncludeFullRawData { get; set; } = true;
    public string? SessionId { get; set; }
}

public class SignalDataPointDto
{
    public DateTime Time { get; set; }
    public double Value { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
}

public class SignalSummaryDto
{
    public int SignalId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public double DesignMin { get; set; }
    public double DesignMax { get; set; }
    public double AvgValue { get; set; }
    public double PeakValue { get; set; }
    public double LowestValue { get; set; }
    public int TotalReadings { get; set; }
    public int ExcursionCount { get; set; }
    public bool HasViolation => PeakValue > DesignMax || LowestValue < DesignMin;
    public List<SignalDataPointDto> DataPoints { get; set; } = new();
}

public class EventItemDto
{
    public long EventId { get; set; }
    public int SignalId { get; set; }
    public string SignalName { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public double? PeakValue { get; set; }
    public double? Threshold { get; set; }
    public double DurationMinutes => (EndTime - StartTime).TotalMinutes;
}

public class AlertItemDto
{
    public long AlertId { get; set; }
    public int SignalId { get; set; }
    public string SignalName { get; set; } = string.Empty;
    public DateTime TriggeredAt { get; set; }
    public double TriggerValue { get; set; }
    public double ThresholdValue { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? ResolvedAt { get; set; }
}

public class ReportDataDto
{
    public AssetDto Asset { get; set; } = new();
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public int HealthScore { get; set; } = 100;
    public string HealthStatus { get; set; } = "Optimal";
    public List<SignalSummaryDto> Signals { get; set; } = new();
    public List<EventItemDto> Events { get; set; } = new();
    public List<AlertItemDto> Alerts { get; set; } = new();
    public List<string> KeyInsights { get; set; } = new();
    public bool IncludeFullRawData { get; set; } = true;
}
