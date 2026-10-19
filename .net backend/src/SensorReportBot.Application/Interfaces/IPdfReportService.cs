namespace SensorReportBot.Application.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;
using SensorReportBot.Application.DTOs;

public class PdfGenerationResult
{
    public byte[] PdfBytes { get; set; } = Array.Empty<byte>();
    public string Html { get; set; } = string.Empty;
    public long DbDurationMs { get; set; }
    public long RenderDurationMs { get; set; }
    public long TotalDurationMs { get; set; }
}

public interface IPdfReportService
{
    Task<byte[]> GeneratePdfAsync(int assetId, DateTime from, DateTime to, string? category = null, Func<int, string, Task>? onProgress = null, CancellationToken ct = default);
    Task<PdfGenerationResult> GenerateReportResultAsync(int assetId, DateTime from, DateTime to, string? category = null, Func<int, string, Task>? onProgress = null, CancellationToken ct = default);
}
