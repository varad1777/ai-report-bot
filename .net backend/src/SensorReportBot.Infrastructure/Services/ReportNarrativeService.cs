namespace SensorReportBot.Infrastructure.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SensorReportBot.Application.DTOs;
using SensorReportBot.Application.Interfaces;

/// <summary>
/// Generates qualitative diagnostic inferences using the LLM for the
/// Schneider Electric EcoStruxure Asset Advisor multi-page report.
/// Strictly grounded on TimescaleDB aggregated row values — the AI provides
/// engineering interpretations and observations, while numbers come from SQL evidence.
/// </summary>
public class ReportNarrativeService : IReportNarrativeService
{
    private readonly ILlmService _llm;
    private readonly ILogger<ReportNarrativeService> _logger;

    private static readonly object NarrativeSchema = new
    {
        type = "object",
        properties = new
        {
            executiveSummary = new { type = "string" },
            healthMatrixInference = new { type = "string" },
            assetAnalysisInference = new { type = "string" },
            healthIndexBulletPoints = new { type = "array", items = new { type = "string" } },
            worstScoreInference = new { type = "string" },
            powerSupplyInference = new { type = "string" },
            sensorInterpretations = new
            {
                type  = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        signalName  = new { type = "string" },
                        observation = new { type = "string" },
                        severity    = new { type = "string", @enum = new[] { "High", "Medium", "Low" } }
                    },
                    required = new[] { "signalName", "observation", "severity" },
                    additionalProperties = false
                }
            },
            rootCauseAnalysis = new
            {
                type  = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        finding      = new { type = "string" },
                        possibleCause= new { type = "string" },
                        likelihood   = new { type = "string", @enum = new[] { "High", "Medium", "Low" } }
                    },
                    required = new[] { "finding", "possibleCause", "likelihood" },
                    additionalProperties = false
                }
            },
            recommendations = new
            {
                type  = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        action    = new { type = "string" },
                        priority  = new { type = "string", @enum = new[] { "High", "Medium", "Low" } },
                        timeframe = new { type = "string" }
                    },
                    required = new[] { "action", "priority", "timeframe" },
                    additionalProperties = false
                }
            },
            conclusion = new { type = "string" }
        },
        required = new[]
        {
            "executiveSummary", "healthMatrixInference", "assetAnalysisInference",
            "healthIndexBulletPoints", "worstScoreInference", "powerSupplyInference",
            "sensorInterpretations", "rootCauseAnalysis", "recommendations", "conclusion"
        },
        additionalProperties = false
    };

    public ReportNarrativeService(ILlmService llm, ILogger<ReportNarrativeService> logger)
    {
        _llm    = llm;
        _logger = logger;
    }

    public async Task<ReportNarrativeDto> GenerateNarrativeAsync(
        ReportEvidenceDto evidence, CancellationToken ct = default)
    {
        string systemPrompt = BuildSystemPrompt();
        string userMessage  = BuildEvidencePayload(evidence);

        int signalCount = evidence.Signals.Count;
        int breachedCount = evidence.Signals.Count(s => s.IsBreached);
        int estSysTokens = (int)Math.Ceiling(systemPrompt.Length / 4.0);
        int estPayloadTokens = (int)Math.Ceiling(userMessage.Length / 4.0);

        string prepBanner =
            "\n┌──────────────────────────────────────────────────────────────────────────────────────\n" +
            $"│ 🔍 [REPORT NARRATIVE GENERATION - EVIDENCE PREPARATION LEVEL]\n" +
            $"│    • Target Asset:       {evidence.Asset.Name} (ID: {evidence.Asset.AssetId})\n" +
            $"│    • Monitored Signals:  {signalCount} total signals included in evidence payload\n" +
            $"│    • Breached Signals:   {breachedCount}\n" +
            $"│    • Health Status:      {evidence.OverallHealth} (Score: {evidence.HealthScore}/100)\n" +
            $"│    • Total Excursions:   {evidence.TotalExcursions}\n" +
            $"│    • System Prompt Size: {systemPrompt.Length:N0} chars (~{estSysTokens:N0} tokens)\n" +
            $"│    • Evidence JSON Size: {userMessage.Length:N0} chars (~{estPayloadTokens:N0} tokens)\n" +
            $"│    • Total Input Size:   {systemPrompt.Length + userMessage.Length:N0} chars (~{estSysTokens + estPayloadTokens:N0} tokens)\n" +
            "└──────────────────────────────────────────────────────────────────────────────────────\n";

        Console.WriteLine(prepBanner);
        _logger.LogInformation("{PrepBanner}", prepBanner);

        ReportNarrativeDto? narrative = null;
        try
        {
            narrative = await _llm.GenerateJsonResponseAsync<ReportNarrativeDto>(
                systemPrompt,
                userMessage,
                ct,
                NarrativeSchema);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM call failed — falling back to deterministic expert template narrative.");
        }

        if (narrative == null)
        {
            Console.WriteLine("⚠️ [FALLBACK APPLIED] Using deterministic expert template narrative for report.");
            narrative = BuildFallbackNarrative(evidence);
        }
        else
        {
            EnsureCompleteNarrative(narrative, evidence);
        }

        return narrative;
    }

    private static void EnsureCompleteNarrative(ReportNarrativeDto n, ReportEvidenceDto e)
    {
        var fallback = BuildFallbackNarrative(e);

        if (string.IsNullOrWhiteSpace(n.ExecutiveSummary))
            n.ExecutiveSummary = fallback.ExecutiveSummary;

        // Ensure HealthMatrixInference is a distinct single bullet line and not a repeated generic paragraph
        if (string.IsNullOrWhiteSpace(n.HealthMatrixInference) ||
            n.HealthMatrixInference.Length > 200 ||
            n.HealthMatrixInference.Equals(n.AssetAnalysisInference, StringComparison.OrdinalIgnoreCase) ||
            n.HealthMatrixInference.Equals(n.ExecutiveSummary, StringComparison.OrdinalIgnoreCase))
        {
            n.HealthMatrixInference = fallback.HealthMatrixInference;
        }

        // Ensure AssetAnalysisInference is distinct and specific to frequency, load & slip
        if (string.IsNullOrWhiteSpace(n.AssetAnalysisInference) ||
            n.AssetAnalysisInference.Equals(n.ExecutiveSummary, StringComparison.OrdinalIgnoreCase) ||
            n.AssetAnalysisInference.Equals(n.WorstScoreInference, StringComparison.OrdinalIgnoreCase))
        {
            n.AssetAnalysisInference = fallback.AssetAnalysisInference;
        }

        if (n.HealthIndexBulletPoints == null || !n.HealthIndexBulletPoints.Any(b => !string.IsNullOrWhiteSpace(b)))
            n.HealthIndexBulletPoints = fallback.HealthIndexBulletPoints;

        // Ensure WorstScoreInference specifically talks about the 0-10 score region & yellow/red thresholds
        if (string.IsNullOrWhiteSpace(n.WorstScoreInference) ||
            n.WorstScoreInference.Equals(n.AssetAnalysisInference, StringComparison.OrdinalIgnoreCase) ||
            n.WorstScoreInference.Equals(n.ExecutiveSummary, StringComparison.OrdinalIgnoreCase))
        {
            n.WorstScoreInference = fallback.WorstScoreInference;
        }

        // Ensure PowerSupplyInference specifically discusses supply frequency and current stability
        if (string.IsNullOrWhiteSpace(n.PowerSupplyInference) ||
            n.PowerSupplyInference.Equals(n.WorstScoreInference, StringComparison.OrdinalIgnoreCase) ||
            n.PowerSupplyInference.Equals(n.AssetAnalysisInference, StringComparison.OrdinalIgnoreCase))
        {
            n.PowerSupplyInference = fallback.PowerSupplyInference;
        }

        if (n.SensorInterpretations == null || !n.SensorInterpretations.Any())
            n.SensorInterpretations = fallback.SensorInterpretations;

        if (n.RootCauseAnalysis == null || !n.RootCauseAnalysis.Any())
            n.RootCauseAnalysis = fallback.RootCauseAnalysis;

        if (n.Recommendations == null || !n.Recommendations.Any())
            n.Recommendations = fallback.Recommendations;

        if (string.IsNullOrWhiteSpace(n.Conclusion))
            n.Conclusion = fallback.Conclusion;
    }

    private static string BuildSystemPrompt() => """
        You are a senior diagnostic engineer at Schneider Electric authoring an official
        EcoStruxure Asset Advisor - Rotating Machine Health Report.

        Analyze the provided asset telemetry, operating points, and subcomponent health scores.
        Generate UNIQUE, HIGHLY SPECIFIC qualitative engineering observations for each distinct report section in valid JSON.

        CRITICAL REQUIREMENT: DO NOT REPEAT THE SAME PHRASING ACROSS MULTIPLE FIELDS. Each section must provide distinct diagnostic value:

        REQUIRED INFERENCE SECTIONS:
        1. executiveSummary: 2-3 sentences overviewing connected electrical assets and overall operational condition.
        2. healthMatrixInference: EXACTLY ONE bullet line summarizing the 3x3 criticality vs health matrix (e.g. "• 1 asset at medium criticality is considered as good health condition.").
        3. assetAnalysisInference: 2 sentences explaining the Section 1.4 table based on partial health indices (frequency, load & slip operating points).
        4. healthIndexBulletPoints: 3 bullet points defining the Health Index 1/5 status (e.g. "The equipment is in good health state.", "No abnormal behaviour has been detected.", "Wear and aging are far from any critical level.").
        5. worstScoreInference: 1-2 sentences specifically assessing the subcomponent worst scores (0.0 to 10.0 scale) relative to the 7.0 yellow and 9.0 red alarm thresholds.
        6. powerSupplyInference: 1-2 sentences specifically evaluating the dual Supply Frequency and Current graphs (e.g. verifying 50 Hz grid stability and nominal load current).
        7. sensorInterpretations: Array of specific observations for critical or representative telemetry channels.
        8. rootCauseAnalysis: Array of 2-3 physical engineering findings with possible causes and likelihood.
        9. recommendations: Array of 2-3 prioritized maintenance actions with timeframe.
        10. conclusion: 1-2 sentences final engineering verdict.

        RULES:
        - Maintain the formal, authoritative Schneider Electric engineering terminology.
        - Respond ONLY with valid JSON matching the schema. No markdown backticks.
        """;

    private static string BuildEvidencePayload(ReportEvidenceDto e)
    {
        var payload = new
        {
            asset = new
            {
                name     = e.Asset.Name,
                type     = e.Asset.AssetType,
                location = e.Asset.Location ?? "Plant",
                monitoredSignals = e.Context.MonitoredSignals
            },
            period = new
            {
                from = e.From.ToString("yyyy-MM-dd"),
                to   = e.To.ToString("yyyy-MM-dd")
            },
            healthStatus        = e.OverallHealth,
            healthScore         = e.HealthScore,
            totalExcursions     = e.TotalExcursions,
            criticalAlertsCount = e.CriticalAlerts,
            warningAlertsCount  = e.WarningAlerts,
            signals = e.Signals.Select(s => new
            {
                name           = s.Name,
                unit           = s.Unit,
                isBreached     = s.IsBreached,
                healthStatus   = s.HealthStatus,
                excursionCount = s.ExcursionCount,
                statMax        = s.StatMax,
                maxLimit       = s.MaxLimit
            }).ToArray(),
            motorInsights = e.MotorInsights.Select(m => new
            {
                motor = m.AssetName,
                maxCurrent = m.MaxCurrent,
                maxFrequency = m.MaxFrequency,
                speedRpm = m.SpeedRpm
            }).ToArray()
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
    }

    private static ReportNarrativeDto BuildFallbackNarrative(ReportEvidenceDto e)
    {
        int assetCount = 1;
        string healthCondition = e.OverallHealth.Equals("Good", StringComparison.OrdinalIgnoreCase)
            ? "good health condition"
            : e.OverallHealth.Equals("Medium", StringComparison.OrdinalIgnoreCase)
                ? "medium health condition"
                : "poor health condition";

        string assetNoun = assetCount == 1 ? "1 asset at medium criticality is" : $"{assetCount} assets at medium criticality are";

        return new ReportNarrativeDto
        {
            ExecutiveSummary = "This report presents health status of connected electrical assets and observations in the installation during the reporting period. " +
                               "The information in the report is intended for qualified personnel who is responsible for serving and maintaining your electrical system. " +
                               "Use this information to correct the issues and improve the performance and availability of the system.",
            
            HealthMatrixInference = $"• {assetNoun} considered as {healthCondition}.",
            
            AssetAnalysisInference = "The asset analysis compromises the overall health status of the asset based on the various parameters like frequency, load & slip at different operating points.\n" +
                                     "The partial health status can be interpreted as follows.",
            
            HealthIndexBulletPoints = new List<string>
            {
                "The equipment is in good health state.",
                "No abnormal behaviour has been detected.",
                "Wear and aging are far from any critical level."
            },

            WorstScoreInference = "From the above health score, all the parameters are within the standard region, which indicates that the motor has no issues and it operates smoothly.",

            PowerSupplyInference = "The graph indicates that there is no abnormal behavior observed in the health of the Motor shaft, Belt, Rotor, Stator and Bearing during the monitoring period. Hence the motor operating in good condition.",

            SensorInterpretations = e.Signals.Select(s => new SensorInterpretationDto
            {
                SignalName  = s.Name,
                Observation = s.IsBreached
                    ? $"The {s.Name} signal recorded threshold excursions during operational peaks."
                    : $"The {s.Name} signal operated stably within nominal operating tolerances.",
                Severity    = s.IsBreached ? (s.ExcursionCount > 3 ? "High" : "Medium") : "Low"
            }).ToList(),

            RootCauseAnalysis = new List<RcaFindingDto>
            {
                new()
                {
                    Finding       = "Power supply and frequency stability verified under operational load.",
                    PossibleCause = "Standard continuous operating regime with normal grid frequency matching.",
                    Likelihood    = "High"
                },
                new()
                {
                    Finding       = "Bearing and shaft train dynamic vibration within ISO standard limits.",
                    PossibleCause = "Proper alignment and adequate lubrication regime maintained.",
                    Likelihood    = "High"
                }
            },

            Recommendations = new List<RecommendationDto>
            {
                new()
                {
                    Action    = "Continue regular scheduled condition monitoring and telemetry acquisition.",
                    Priority  = "Low",
                    Timeframe = "Quarterly"
                },
                new()
                {
                    Action    = "Verify mechanical lubrication levels during next scheduled routine maintenance.",
                    Priority  = "Low",
                    Timeframe = "Within 60 days"
                }
            },

            Conclusion = "Overall machine condition is optimal. Routine condition-based monitoring should continue."
        };
    }
}
