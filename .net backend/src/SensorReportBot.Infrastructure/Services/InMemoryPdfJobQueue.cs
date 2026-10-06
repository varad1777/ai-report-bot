namespace SensorReportBot.Infrastructure.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SensorReportBot.Application.DTOs;
using SensorReportBot.Application.Interfaces;
using SensorReportBot.Domain.Entities;
using SensorReportBot.Domain.Enums;

public class InMemoryPdfJobQueue : IPdfJobQueue
{
    private readonly Channel<Guid> _channel;
    private readonly ConcurrentDictionary<Guid, ReportJob> _jobs = new();
    private readonly ILogger<InMemoryPdfJobQueue> _logger;

    public InMemoryPdfJobQueue(ILogger<InMemoryPdfJobQueue> logger)
    {
        _logger = logger;
        _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public async Task<ReportJob> EnqueueAsync(ReportRequestDto request, string assetName, CancellationToken ct = default)
    {
        var job = new ReportJob
        {
            Id = Guid.NewGuid(),
            AssetId = request.AssetId,
            AssetName = assetName,
            SignalIds = request.SignalIds,
            From = request.From,
            To = request.To,
            IncludeEvents = request.IncludeEvents,
            IncludeAlerts = request.IncludeAlerts,
            IncludeInsights = request.IncludeInsights,
            IncludeCharts = request.IncludeCharts,
            IncludeFullRawData = request.IncludeFullRawData,
            Status = ReportJobStatus.Queued,
            StatusMessage = "Job placed in processing queue",
            ProgressPercentage = 0,
            CreatedAt = DateTime.UtcNow
        };

        _jobs[job.Id] = job;
        await _channel.Writer.WriteAsync(job.Id, ct);
        _logger.LogInformation("Enqueued PDF generation job {JobId} for asset '{AssetName}'", job.Id, assetName);

        return job;
    }

    public Task<ReportJob?> GetJobAsync(Guid jobId)
    {
        _jobs.TryGetValue(jobId, out var job);
        return Task.FromResult(job);
    }

    public Task<IReadOnlyList<ReportJobDto>> GetAllJobsAsync()
    {
        var list = _jobs.Values
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => new ReportJobDto
            {
                JobId = j.Id,
                AssetId = j.AssetId,
                AssetName = j.AssetName,
                Status = j.Status,
                StatusMessage = j.StatusMessage,
                ProgressPercentage = j.ProgressPercentage,
                CreatedAt = j.CreatedAt,
                StartedAt = j.StartedAt,
                CompletedAt = j.CompletedAt,
                ErrorMessage = j.ErrorMessage,
                FileSizeBytes = j.FileSizeBytes,
                FileName = j.FileName
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<ReportJobDto>>(list);
    }

    public Task UpdateJobProgressAsync(Guid jobId, int progressPercentage, string statusMessage)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            if (job.Status == ReportJobStatus.Cancelled) return Task.CompletedTask;

            if (job.StartedAt == null) job.StartedAt = DateTime.UtcNow;
            job.Status = ReportJobStatus.Processing;
            job.ProgressPercentage = progressPercentage;
            job.StatusMessage = statusMessage;
        }
        return Task.CompletedTask;
    }

    public Task CompleteJobAsync(Guid jobId, byte[] pdfData, string fileName)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            if (job.Status == ReportJobStatus.Cancelled) return Task.CompletedTask;

            job.Status = ReportJobStatus.Completed;
            job.ProgressPercentage = 100;
            job.StatusMessage = "PDF generated successfully and ready for download";
            job.CompletedAt = DateTime.UtcNow;
            job.PdfData = pdfData;
            job.FileSizeBytes = pdfData.Length;
            job.FileName = fileName;
            _logger.LogInformation("Job {JobId} marked Completed. File size: {Size} bytes", jobId, pdfData.Length);
        }
        return Task.CompletedTask;
    }

    public Task FailJobAsync(Guid jobId, string errorMessage)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            if (job.Status == ReportJobStatus.Cancelled) return Task.CompletedTask;

            job.Status = ReportJobStatus.Failed;
            job.StatusMessage = "Failed to generate report";
            job.ErrorMessage = errorMessage;
            job.CompletedAt = DateTime.UtcNow;
            _logger.LogError("Job {JobId} marked Failed: {Error}", jobId, errorMessage);
        }
        return Task.CompletedTask;
    }

    public Task<bool> CancelJobAsync(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            if (job.Status == ReportJobStatus.Queued || job.Status == ReportJobStatus.Processing)
            {
                job.Status = ReportJobStatus.Cancelled;
                job.StatusMessage = "Job cancelled by user request.";
                job.CompletedAt = DateTime.UtcNow;
                _logger.LogInformation("Job {JobId} was cancelled by user.", jobId);
                return Task.FromResult(true);
            }
        }
        return Task.FromResult(false);
    }

    public async Task<byte[]?> GetJobPdfAsync(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            if (job.PdfData != null) return job.PdfData;
            if (!string.IsNullOrEmpty(job.FilePath) && System.IO.File.Exists(job.FilePath))
            {
                return await System.IO.File.ReadAllBytesAsync(job.FilePath);
            }
        }
        return null;
    }

    public Task<System.IO.Stream?> GetJobPdfStreamAsync(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            if (job.PdfData != null)
            {
                System.IO.Stream stream = new System.IO.MemoryStream(job.PdfData);
                return Task.FromResult<System.IO.Stream?>(stream);
            }
            if (!string.IsNullOrEmpty(job.FilePath) && System.IO.File.Exists(job.FilePath))
            {
                System.IO.Stream stream = new System.IO.FileStream(job.FilePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);
                return Task.FromResult<System.IO.Stream?>(stream);
            }
        }
        return Task.FromResult<System.IO.Stream?>(null);
    }

    public IAsyncEnumerable<Guid> ReadJobsAsync(CancellationToken ct)
    {
        return _channel.Reader.ReadAllAsync(ct);
    }
}
