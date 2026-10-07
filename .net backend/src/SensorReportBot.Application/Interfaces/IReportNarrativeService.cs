namespace SensorReportBot.Application.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using SensorReportBot.Application.DTOs;

/// <summary>
/// Calls the LLM with compact evidence and returns a validated narrative DTO.
/// The LLM only generates text — never numbers, HTML, or chart data.
/// </summary>
public interface IReportNarrativeService
{
    Task<ReportNarrativeDto> GenerateNarrativeAsync(
        ReportEvidenceDto evidence,
        CancellationToken ct = default);
}
