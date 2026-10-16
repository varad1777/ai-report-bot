namespace SensorReportBot.Api.Controllers;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using SensorReportBot.Application.DTOs;
using SensorReportBot.Application.Interfaces;

[ApiController]
[Route("api/[controller]")]
public class ReportsController : ControllerBase
{
    private readonly ITelemetryRepository _telemetryRepository;
    private readonly IPdfJobQueue _pdfJobQueue;

    public ReportsController(
        ITelemetryRepository telemetryRepository, 
        IPdfJobQueue pdfJobQueue)
    {
        _telemetryRepository = telemetryRepository;
        _pdfJobQueue = pdfJobQueue;
    }

    [HttpGet("assets")]
    public async Task<IActionResult> GetAssets(CancellationToken ct)
    {
        var assets = await _telemetryRepository.GetAssetsAsync(ct);
        return Ok(assets);
    }

    [HttpGet("assets/{assetId:int}/signals")]
    public async Task<IActionResult> GetSignals(int assetId, CancellationToken ct)
    {
        var signals = await _telemetryRepository.GetSignalsByAssetAsync(assetId, ct);
        return Ok(signals);
    }

    [HttpPost("preview")]
    public async Task<IActionResult> GeneratePreview([FromBody] ReportRequestDto request, CancellationToken ct)
    {
        if (request.AssetId <= 0)
        {
            return BadRequest(new { error = "Valid AssetId is required." });
        }

        DateTime to = request.To ?? DateTime.UtcNow;
        DateTime from = request.From ?? to.AddDays(-7);

        var (hasData, pointCount, minRecorded, maxRecorded) = await _telemetryRepository.CheckDataAvailabilityAsync(
            request.AssetId, from, to, ct);

        if (!hasData)
        {
            string notice = (minRecorded.HasValue && maxRecorded.HasValue)
                ? $" Recorded telemetry is available between {minRecorded.Value:yyyy-MM-dd} and {maxRecorded.Value:yyyy-MM-dd}."
                : "";
            return BadRequest(new { error = $"No telemetry data exists for this asset between {from:yyyy-MM-dd} and {to:yyyy-MM-dd}.{notice}" });
        }

        var reportData = await _telemetryRepository.GenerateReportDataAsync(request, ct);
        return Ok(reportData);
    }

    // ==========================================
    // ASYNCHRONOUS PDF JOB QUEUE ENDPOINTS
    // ==========================================

    [HttpPost("queue")]
    public async Task<IActionResult> QueueReportGeneration([FromBody] ReportRequestDto request, CancellationToken ct)
    {
        if (request.AssetId <= 0)
        {
            return BadRequest(new { error = "Valid AssetId is required." });
        }

        var assets = await _telemetryRepository.GetAssetsAsync(ct);
        var asset = assets.FirstOrDefault(a => a.AssetId == request.AssetId);
        string assetName = asset?.Name ?? $"Asset-{request.AssetId}";

        DateTime to = request.To ?? DateTime.UtcNow;
        DateTime from = request.From ?? to.AddDays(-7);

        var (hasData, pointCount, minRecorded, maxRecorded) = await _telemetryRepository.CheckDataAvailabilityAsync(
            request.AssetId, from, to, ct);

        if (!hasData)
        {
            string notice = (minRecorded.HasValue && maxRecorded.HasValue)
                ? $" Recorded telemetry is available between {minRecorded.Value:yyyy-MM-dd} and {maxRecorded.Value:yyyy-MM-dd}."
                : "";
            return BadRequest(new { error = $"No telemetry data exists for '{assetName}' between {from:yyyy-MM-dd} and {to:yyyy-MM-dd}.{notice}" });
        }

        var job = await _pdfJobQueue.EnqueueAsync(request, assetName, ct);

        return Accepted($"/api/reports/jobs/{job.Id}", new EnqueueJobResponseDto
        {
            JobId = job.Id,
            Status = "Queued",
            Message = $"Report for '{assetName}' placed in background queue.",
            CreatedAt = job.CreatedAt
        });
    }

    [HttpGet("jobs")]
    public async Task<IActionResult> GetAllJobs()
    {
        var jobs = await _pdfJobQueue.GetAllJobsAsync();
        return Ok(jobs);
    }

    [HttpGet("jobs/{jobId:guid}")]
    public async Task<IActionResult> GetJobStatus(Guid jobId)
    {
        var job = await _pdfJobQueue.GetJobAsync(jobId);
        if (job == null)
        {
            return NotFound(new { error = $"Job {jobId} not found." });
        }

        return Ok(new ReportJobDto
        {
            JobId = job.Id,
            AssetId = job.AssetId,
            AssetName = job.AssetName,
            SessionId = job.SessionId,
            Status = job.Status,
            StatusMessage = job.StatusMessage,
            ProgressPercentage = job.ProgressPercentage,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            ErrorMessage = job.ErrorMessage,
            FileSizeBytes = job.FileSizeBytes,
            FileName = job.FileName,
            DbDurationMs = job.DbDurationMs,
            RenderDurationMs = job.RenderDurationMs,
            TotalDurationMs = job.TotalDurationMs
        });
    }

    [HttpDelete("jobs/{jobId:guid}")]
    public async Task<IActionResult> CancelJob(Guid jobId)
    {
        var job = await _pdfJobQueue.GetJobAsync(jobId);
        if (job == null)
        {
            return NotFound(new { error = $"Job {jobId} not found." });
        }

        bool success = await _pdfJobQueue.CancelJobAsync(jobId);
        if (!success)
        {
            return BadRequest(new { error = $"Job {jobId} cannot be cancelled because its current status is '{job.Status}'." });
        }

        return Ok(new { message = $"Job {jobId} has been cancelled successfully.", jobId, status = "Cancelled" });
    }

    [HttpGet("jobs/{jobId:guid}/download")]
    public async Task<IActionResult> DownloadJobPdf(Guid jobId)
    {
        var job = await _pdfJobQueue.GetJobAsync(jobId);
        if (job == null)
        {
            return NotFound(new { error = $"Job {jobId} not found." });
        }

        if (job.Status != SensorReportBot.Domain.Enums.ReportJobStatus.Completed)
        {
            return BadRequest(new { error = $"Job is not completed yet. Current status: {job.Status}", status = job.Status.ToString() });
        }

        var pdfStream = await _pdfJobQueue.GetJobPdfStreamAsync(jobId);
        if (pdfStream == null)
        {
            return NotFound(new { error = "PDF report file not found on server disk storage." });
        }

        string fileName = job.FileName ?? $"Report_{job.AssetName}_{DateTime.UtcNow:yyyyMMdd_HHmm}.pdf";
        return File(pdfStream, "application/pdf", fileName);
    }

    [HttpGet("jobs/{jobId:guid}/preview")]
    public async Task<IActionResult> PreviewJobPdf(Guid jobId)
    {
        var job = await _pdfJobQueue.GetJobAsync(jobId);
        if (job == null)
        {
            return NotFound(new { error = $"Job {jobId} not found." });
        }

        if (job.Status != SensorReportBot.Domain.Enums.ReportJobStatus.Completed)
        {
            return BadRequest(new { error = $"Job is not completed yet. Current status: {job.Status}", status = job.Status.ToString() });
        }

        var pdfStream = await _pdfJobQueue.GetJobPdfStreamAsync(jobId);
        if (pdfStream == null)
        {
            return NotFound(new { error = "PDF report file not found on server disk storage." });
        }

        Response.Headers.Append("Content-Disposition", "inline; filename=\"" + (job.FileName ?? "report.pdf") + "\"");
        return File(pdfStream, "application/pdf");
    }

    [HttpGet("jobs/{jobId:guid}/html")]
    public async Task<IActionResult> GetJobHtml(Guid jobId)
    {
        var job = await _pdfJobQueue.GetJobAsync(jobId);
        if (job == null)
        {
            return NotFound(new { error = $"Job {jobId} not found." });
        }

        if (string.IsNullOrEmpty(job.HtmlData))
        {
            string htmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, "reports_storage", $"{job.Id}.html");
            if (System.IO.File.Exists(htmlPath))
            {
                job.HtmlData = await System.IO.File.ReadAllTextAsync(htmlPath);
            }
        }

        if (string.IsNullOrEmpty(job.HtmlData))
        {
            return NotFound(new { error = "HTML preview not available for this job." });
        }

        return Content(job.HtmlData, "text/html; charset=utf-8");
    }
}
