namespace SensorReportBot.Domain.Entities;

using System;
using System.Collections.Generic;
using SensorReportBot.Domain.Enums;

public class ReportJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int AssetId { get; set; }
    public string AssetName { get; set; } = string.Empty;
    public string? Category { get; set; }
    public List<int>? SignalIds { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public bool IncludeEvents { get; set; } = true;
    public bool IncludeAlerts { get; set; } = true;
    public bool IncludeInsights { get; set; } = true;
    public bool IncludeCharts { get; set; } = true;
    public bool IncludeFullRawData { get; set; } = true;
    public string? SessionId { get; set; }

    public ReportJobStatus Status { get; set; } = ReportJobStatus.Queued;
    public string StatusMessage { get; set; } = "Job queued in processing pipeline";
    public int ProgressPercentage { get; set; } = 0;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? FileName { get; set; }
    public string? FilePath { get; set; }
    public byte[]? PdfData { get; set; }
    public string? HtmlData { get; set; }
    public long? DbDurationMs { get; set; }
    public long? RenderDurationMs { get; set; }
    public long? TotalDurationMs { get; set; }
}
