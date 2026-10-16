namespace SensorReportBot.Application.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SensorReportBot.Application.DTOs;
using SensorReportBot.Domain.Entities;

public interface IPdfJobQueue
{
    Task<ReportJob> EnqueueAsync(ReportRequestDto request, string assetName, CancellationToken ct = default);
    Task<ReportJob?> GetJobAsync(Guid jobId);
    Task<IReadOnlyList<ReportJobDto>> GetAllJobsAsync();
    Task UpdateJobProgressAsync(Guid jobId, int progressPercentage, string statusMessage);
    Task CompleteJobAsync(Guid jobId, byte[] pdfData, string fileName, string? htmlData = null, long? dbDurationMs = null, long? renderDurationMs = null, long? totalDurationMs = null);
    Task FailJobAsync(Guid jobId, string errorMessage);
    Task<bool> CancelJobAsync(Guid jobId);
    Task<byte[]?> GetJobPdfAsync(Guid jobId);
    Task<System.IO.Stream?> GetJobPdfStreamAsync(Guid jobId);
    IAsyncEnumerable<Guid> ReadJobsAsync(CancellationToken ct);
}
