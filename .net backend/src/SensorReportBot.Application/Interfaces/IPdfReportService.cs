namespace SensorReportBot.Application.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;
using SensorReportBot.Application.DTOs;

public interface IPdfReportService
{
    Task<byte[]> GeneratePdfAsync(int assetId, DateTime from, DateTime to, Func<int, string, Task>? onProgress = null, CancellationToken ct = default);
}
