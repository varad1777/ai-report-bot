namespace SensorReportBot.Infrastructure.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SensorReportBot.Application.DTOs;
using SensorReportBot.Application.Interfaces;
using SensorReportBot.Domain.Entities;
using SensorReportBot.Domain.Enums;

/// <summary>
/// Thread-safe PDF job queue that persists job metadata and completed PDF files
/// to disk so that restarts of the backend process do not invalidate previous jobs.
/// </summary>
public class InMemoryPdfJobQueue : IPdfJobQueue
{
    private readonly Channel<Guid> _channel;
    private readonly ConcurrentDictionary<Guid, ReportJob> _jobs = new();
    private readonly string _storageDir;
    private readonly ILogger<InMemoryPdfJobQueue> _logger;
    private readonly IJobProgressNotifier? _notifier;

    public InMemoryPdfJobQueue(ILogger<InMemoryPdfJobQueue> logger, IJobProgressNotifier? notifier = null)
    {
        _logger = logger;
        _notifier = notifier;
        _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _storageDir = Path.Combine(AppContext.BaseDirectory, "reports_storage");
        Directory.CreateDirectory(_storageDir);

        LoadPersistedJobs();
    }

    private void LoadPersistedJobs()
    {
        try
        {
            var metaFiles = Directory.GetFiles(_storageDir, "*.json");
            foreach (var metaFile in metaFiles)
            {
                try
                {
                    string json = File.ReadAllText(metaFile);
                    var job = JsonSerializer.Deserialize<ReportJob>(json);
                    if (job != null && job.Id != Guid.Empty)
                    {
                        string pdfPath = Path.Combine(_storageDir, $"{job.Id}.pdf");
                        if (File.Exists(pdfPath))
                        {
                            job.FilePath = pdfPath;
                            job.FileSizeBytes = new FileInfo(pdfPath).Length;
                        }
                        string htmlPath = Path.Combine(_storageDir, $"{job.Id}.html");
                        if (File.Exists(htmlPath))
                        {
                            job.HtmlData = File.ReadAllText(htmlPath);
                        }
                        _jobs[job.Id] = job;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load cached job metadata from {File}", metaFile);
                }
            }
            _logger.LogInformation("Loaded {Count} persisted PDF jobs from disk cache.", _jobs.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize reports storage directory.");
        }
    }

    private void PersistJob(ReportJob job)
    {
        try
        {
            string metaPath = Path.Combine(_storageDir, $"{job.Id}.json");
            string json = JsonSerializer.Serialize(job, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metaPath, json);

            if (job.PdfData != null && job.PdfData.Length > 0)
            {
                string pdfPath = Path.Combine(_storageDir, $"{job.Id}.pdf");
                File.WriteAllBytes(pdfPath, job.PdfData);
                job.FilePath = pdfPath;
            }

            if (!string.IsNullOrEmpty(job.HtmlData))
            {
                string htmlPath = Path.Combine(_storageDir, $"{job.Id}.html");
                File.WriteAllText(htmlPath, job.HtmlData);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist job {JobId} to disk", job.Id);
        }
    }

    public async Task<ReportJob> EnqueueAsync(ReportRequestDto request, string assetName, CancellationToken ct = default)
    {
        var job = new ReportJob
        {
            Id = Guid.NewGuid(),
            AssetId = request.AssetId,
            AssetName = assetName,
            Category = request.Category,
            SignalIds = request.SignalIds,
            From = request.From,
            To = request.To,
            IncludeEvents = request.IncludeEvents,
            IncludeAlerts = request.IncludeAlerts,
            IncludeInsights = request.IncludeInsights,
            IncludeCharts = request.IncludeCharts,
            IncludeFullRawData = request.IncludeFullRawData,
            SessionId = request.SessionId,
            Status = ReportJobStatus.Queued,
            StatusMessage = "Job placed in processing queue",
            ProgressPercentage = 0,
            CreatedAt = DateTime.UtcNow
        };

        _jobs[job.Id] = job;
        PersistJob(job);

        if (_notifier != null)
        {
            await _notifier.NotifyProgressAsync(job.SessionId, job.Id, 0, job.StatusMessage, "Queued");
        }

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
                SessionId = j.SessionId,
                Status = j.Status,
                StatusMessage = j.StatusMessage,
                ProgressPercentage = j.ProgressPercentage,
                CreatedAt = j.CreatedAt,
                StartedAt = j.StartedAt,
                CompletedAt = j.CompletedAt,
                ErrorMessage = j.ErrorMessage,
                FileSizeBytes = j.FileSizeBytes,
                FileName = j.FileName,
                DbDurationMs = j.DbDurationMs,
                RenderDurationMs = j.RenderDurationMs,
                TotalDurationMs = j.TotalDurationMs
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<ReportJobDto>>(list);
    }

    public async Task UpdateJobProgressAsync(Guid jobId, int progressPercentage, string statusMessage)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            if (job.Status == ReportJobStatus.Cancelled) return;

            if (job.StartedAt == null) job.StartedAt = DateTime.UtcNow;
            job.Status = ReportJobStatus.Processing;
            job.ProgressPercentage = progressPercentage;
            job.StatusMessage = statusMessage;
            PersistJob(job);

            if (_notifier != null)
            {
                await _notifier.NotifyProgressAsync(job.SessionId, jobId, progressPercentage, statusMessage, "Processing");
            }
        }
    }

    public async Task CompleteJobAsync(
        Guid jobId, 
        byte[] pdfData, 
        string fileName, 
        string? htmlData = null, 
        long? dbDurationMs = null, 
        long? renderDurationMs = null, 
        long? totalDurationMs = null)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            if (job.Status == ReportJobStatus.Cancelled) return;

            job.Status = ReportJobStatus.Completed;
            job.ProgressPercentage = 100;
            job.StatusMessage = "PDF generated successfully and ready for download";
            job.CompletedAt = DateTime.UtcNow;
            job.PdfData = pdfData;
            job.FileSizeBytes = pdfData.Length;
            job.FileName = fileName;
            job.HtmlData = htmlData;
            job.DbDurationMs = dbDurationMs;
            job.RenderDurationMs = renderDurationMs;
            job.TotalDurationMs = totalDurationMs;

            PersistJob(job);
            _logger.LogInformation("Job {JobId} marked Completed and saved to disk. File size: {Size} bytes, DbDuration: {Db}ms, RenderDuration: {Render}ms", 
                jobId, pdfData.Length, dbDurationMs, renderDurationMs);

            if (_notifier != null)
            {
                await _notifier.NotifyProgressAsync(
                    job.SessionId, 
                    jobId, 
                    100, 
                    job.StatusMessage, 
                    "Completed", 
                    dbDurationMs, 
                    renderDurationMs, 
                    totalDurationMs);
            }
        }
    }

    public async Task FailJobAsync(Guid jobId, string errorMessage)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            if (job.Status == ReportJobStatus.Cancelled) return;

            job.Status = ReportJobStatus.Failed;
            job.StatusMessage = "Failed to generate report";
            job.ErrorMessage = errorMessage;
            job.CompletedAt = DateTime.UtcNow;

            PersistJob(job);
            _logger.LogError("Job {JobId} marked Failed: {Error}", jobId, errorMessage);

            if (_notifier != null)
            {
                await _notifier.NotifyProgressAsync(job.SessionId, jobId, job.ProgressPercentage, errorMessage, "Failed");
            }
        }
    }

    public async Task<bool> CancelJobAsync(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            if (job.Status == ReportJobStatus.Queued || job.Status == ReportJobStatus.Processing)
            {
                job.Status = ReportJobStatus.Cancelled;
                job.StatusMessage = "Job cancelled by user request.";
                job.CompletedAt = DateTime.UtcNow;
                PersistJob(job);
                _logger.LogInformation("Job {JobId} was cancelled by user.", jobId);

                if (_notifier != null)
                {
                    await _notifier.NotifyProgressAsync(job.SessionId, jobId, job.ProgressPercentage, job.StatusMessage, "Cancelled");
                }
                return true;
            }
        }
        return false;
    }

    public async Task<byte[]?> GetJobPdfAsync(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            if (job.PdfData != null) return job.PdfData;
            string pdfPath = Path.Combine(_storageDir, $"{jobId}.pdf");
            if (File.Exists(pdfPath))
            {
                return await File.ReadAllBytesAsync(pdfPath);
            }
        }
        return null;
    }

    public Task<Stream?> GetJobPdfStreamAsync(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            if (job.PdfData != null)
            {
                Stream stream = new MemoryStream(job.PdfData);
                return Task.FromResult<Stream?>(stream);
            }
            string pdfPath = Path.Combine(_storageDir, $"{jobId}.pdf");
            if (File.Exists(pdfPath))
            {
                Stream stream = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return Task.FromResult<Stream?>(stream);
            }
        }
        return Task.FromResult<Stream?>(null);
    }

    public IAsyncEnumerable<Guid> ReadJobsAsync(CancellationToken ct)
    {
        return _channel.Reader.ReadAllAsync(ct);
    }
}
