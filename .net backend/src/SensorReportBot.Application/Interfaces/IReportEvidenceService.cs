namespace SensorReportBot.Application.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using SensorReportBot.Application.DTOs;

/// <summary>
/// Builds a compact ReportEvidenceDto from TimescaleDB telemetry.
/// All numbers are computed here by SQL — zero LLM involvement.
/// </summary>
public interface IReportEvidenceService
{
    Task<ReportEvidenceDto> BuildEvidenceAsync(
        int assetId,
        System.DateTime from,
        System.DateTime to,
        CancellationToken ct = default);
}
