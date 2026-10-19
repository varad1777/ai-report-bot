namespace SensorReportBot.Application.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SensorReportBot.Application.DTOs;

public interface ITelemetryRepository
{
    Task<IReadOnlyList<AssetDto>> GetAssetsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SignalDto>> GetSignalsByAssetAsync(int assetId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetSignalCategoriesByAssetAsync(int assetId, CancellationToken ct = default);
    Task<ReportDataDto> GenerateReportDataAsync(ReportRequestDto request, CancellationToken ct = default);
    Task<(bool HasData, long PointCount, DateTime? MinRecordedTime, DateTime? MaxRecordedTime)> CheckDataAvailabilityAsync(int assetId, DateTime from, DateTime to, CancellationToken ct = default);
}
