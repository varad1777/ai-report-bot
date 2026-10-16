namespace SensorReportBot.Infrastructure.Services;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SensorReportBot.Application.DTOs;
using SensorReportBot.Application.Interfaces;
using SensorReportBot.Domain.Entities;

public class PdfGenerationBackgroundService : BackgroundService
{
    private readonly IPdfJobQueue _jobQueue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PdfGenerationBackgroundService> _logger;

    public PdfGenerationBackgroundService(
        IPdfJobQueue jobQueue,
        IServiceScopeFactory scopeFactory,
        ILogger<PdfGenerationBackgroundService> logger)
    {
        _jobQueue = jobQueue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PdfGenerationBackgroundService started and monitoring job queue.");

        await foreach (var jobId in _jobQueue.ReadJobsAsync(stoppingToken))
        {
            try
            {
                var job = await _jobQueue.GetJobAsync(jobId);
                if (job == null) continue;

                _logger.LogInformation("Background worker picked up Job {JobId} for asset '{Asset}'", jobId, job.AssetName);

                await ProcessJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error processing Job {JobId}", jobId);
                await _jobQueue.FailJobAsync(jobId, ex.Message);
            }
        }

        _logger.LogInformation("PdfGenerationBackgroundService stopping.");
    }

    private async Task ProcessJobAsync(ReportJob job, CancellationToken ct)
    {
        if (job.Status == SensorReportBot.Domain.Enums.ReportJobStatus.Cancelled)
        {
            _logger.LogInformation("Job {JobId} was cancelled before worker pickup. Skipping execution.", job.Id);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var pdfService = scope.ServiceProvider.GetRequiredService<IPdfReportService>();

        DateTime toTime = job.To ?? DateTime.UtcNow;
        DateTime fromTime = job.From ?? toTime.AddDays(-7);

        // Step 1: Initialize
        await _jobQueue.UpdateJobProgressAsync(job.Id, 10, "Initializing report generation pipeline...");

        if (job.Status == SensorReportBot.Domain.Enums.ReportJobStatus.Cancelled)
        {
            _logger.LogInformation("Job {JobId} was cancelled. Aborting processing.", job.Id);
            return;
        }

        // Step 2: Render PDF through all phases (SQL Evidence, HTML compilation, Chromium render)
        var result = await pdfService.GenerateReportResultAsync(
            job.AssetId, 
            fromTime, 
            toTime, 
            async (percent, message) =>
            {
                if (job.Status != SensorReportBot.Domain.Enums.ReportJobStatus.Cancelled)
                {
                    await _jobQueue.UpdateJobProgressAsync(job.Id, percent, message);
                }
            },
            ct);

        if (job.Status == SensorReportBot.Domain.Enums.ReportJobStatus.Cancelled)
        {
            _logger.LogInformation("Job {JobId} was cancelled after PDF render. Aborting completion.", job.Id);
            return;
        }

        // Step 3: Complete
        string safeAssetName = string.Join("_", job.AssetName.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_");
        string fileName = $"{safeAssetName}_Report_{DateTime.UtcNow:yyyyMMdd_HHmm}.pdf";

        await _jobQueue.CompleteJobAsync(
            job.Id, 
            result.PdfBytes, 
            fileName, 
            result.Html, 
            result.DbDurationMs, 
            result.RenderDurationMs, 
            result.TotalDurationMs);
    }
}
