namespace SensorReportBot.Application.DTOs;

using System;
using SensorReportBot.Domain.Enums;

public class ReportJobDto
{
    public Guid JobId { get; set; }
    public int AssetId { get; set; }
    public string AssetName { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public ReportJobStatus Status { get; set; }
    public string StatusText => Status.ToString();
    public string StatusMessage { get; set; } = string.Empty;
    public int ProgressPercentage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public double? DurationSeconds => CompletedAt.HasValue && StartedAt.HasValue 
        ? (CompletedAt.Value - StartedAt.Value).TotalSeconds 
        : null;
    public string? ErrorMessage { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? FileName { get; set; }
    public string DownloadUrl => $"/api/reports/jobs/{JobId}/download";
}

public class EnqueueJobResponseDto
{
    public Guid JobId { get; set; }
    public string Status { get; set; } = "Queued";
    public string Message { get; set; } = "PDF generation job successfully enqueued.";
    public DateTime CreatedAt { get; set; }
}
