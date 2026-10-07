namespace SensorReportBot.Application.DTOs;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

// ─────────────────────────────────────────────────────────────────────────────
// PHASE 1 — TimescaleDB Evidence & Statistical Aggregation DTOs
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Summary metrics in Section 1.1 Context relevant to the monitored asset.</summary>
public class ContextSummaryDto
{
    public string AssetName         { get; set; } = string.Empty;
    public string AssetType         { get; set; } = "Rotating Machine";
    public string Location          { get; set; } = string.Empty;
    public int    MonitoredSignals  { get; set; } = 0;
    public int    TotalReadings     { get; set; } = 0;
    public int    ExcursionsCount   { get; set; } = 0;
    public int    ActiveAlertsCount { get; set; } = 0;
    public int    HealthScore       { get; set; } = 100;
}

/// <summary>Counts for the 3x3 Criticality vs Health Matrix.</summary>
public class HealthMatrixCellDto
{
    public string Criticality { get; set; } = "MEDIUM"; // HIGH | MEDIUM | LOW
    public string Health      { get; set; } = "GOOD";   // GOOD | MEDIUM | POOR
    public int    Count       { get; set; } = 0;
}

/// <summary>Partial health summary row in Section 1.4 Asset Analysis.</summary>
public class AssetAnalysisRowDto
{
    public string AssetName             { get; set; } = string.Empty;
    public int    MotorElectricalHealth { get; set; } = 1; // 1-5 index
    public int    MotorComponentHealth  { get; set; } = 1;
    public int    LoadHealth            { get; set; } = 1;
    public int    ShaftTrainHealth      { get; set; } = 1;
    public string HealthStatus          { get; set; } = "Good";
}

/// <summary>Operational metrics row in Section 1.5 Motor Insights.</summary>
public class MotorInsightRowDto
{
    public string AssetName    { get; set; } = string.Empty;
    public double MaxCurrent   { get; set; }
    public double MaxFrequency { get; set; }
    public int    SpeedRpm     { get; set; }
}

/// <summary>Sub-component health gauge and worst score record computed from real sensor domains.</summary>
public class SubcomponentScoreDto
{
    public string Name           { get; set; } = string.Empty;
    public double LastScore      { get; set; } // 0.0 to 10.0 scale (<7 Good/Green, 7-9 Warning/Amber, >9 Critical/Red)
    public double WorstScore     { get; set; }
    public string OperatingPoint { get; set; } = string.Empty;
    public string Date           { get; set; } = string.Empty;
    public string HealthCategory { get; set; } = "Good"; // Good | Medium | Poor
}

/// <summary>Power supply telemetry for Section 1.6.1 dual trend charts.</summary>
public class PowerSupplyGraphDto
{
    public List<ChartPointDto> FrequencyPoints { get; set; } = new();
    public List<ChartPointDto> CurrentPoints   { get; set; } = new();
}

/// <summary>Detailed deep-dive section for the monitored rotating motor/asset.</summary>
public class MotorDetailSectionDto
{
    public int    AssetId                { get; set; }
    public string AssetName              { get; set; } = string.Empty;
    public string SectionNumber          { get; set; } = "1.6.1";
    public string HealthIndexBadge       { get; set; } = "1/5";
    public string HealthIndexStatus      { get; set; } = "GOOD"; // GOOD | MEDIUM | POOR
    public string EvaluationTimestamp    { get; set; } = DateTime.UtcNow.ToString("yyyy/MM/dd\nHH:mm:ss");
    
    public string MotorElectricalHealth  { get; set; } = "Good";
    public string MotorMechanicalHealth  { get; set; } = "Good";
    public string ShaftTrainHealth       { get; set; } = "Good";

    public List<SubcomponentScoreDto> Subcomponents { get; set; } = new();
    public PowerSupplyGraphDto        PowerSupply   { get; set; } = new();

    // AI-generated qualitative inference lines for this motor
    public string WorstScoreInference { get; set; } = string.Empty;
    public string PowerSupplyInference{ get; set; } = string.Empty;
}

/// <summary>Asset specification row in Section 3.3 Asset Details.</summary>
public class AssetSpecificationDto
{
    public string AssetName       { get; set; } = string.Empty;
    public string Manufacturer    { get; set; } = "Schneider Electric";
    public string Model           { get; set; } = "IE3-160L";
    public double RatedCurrent    { get; set; } = 23.3;
    public string ConnectionDate  { get; set; } = "Sep 2024";
}

/// <summary>Per-signal statistical evidence computed by TimescaleDB SQL.</summary>
public class SignalEvidenceDto
{
    public int    SignalId            { get; set; }
    public string Name               { get; set; } = string.Empty;
    public string Unit               { get; set; } = string.Empty;
    public double MinLimit           { get; set; }
    public double MaxLimit           { get; set; }

    public double StatMin            { get; set; }
    public double StatMax            { get; set; }
    public double StatAvg            { get; set; }
    public double StatP95            { get; set; }
    public double StatStdDev         { get; set; }

    public int    ExcursionCount     { get; set; }
    public double HoursAboveMaxLimit { get; set; }
    public double HoursBelowMinLimit { get; set; }
    public double? MaxExcursionValue { get; set; }

    [JsonIgnore]
    public bool IsBreached => StatMax > MaxLimit || StatMin < MinLimit;

    [JsonIgnore]
    public string HealthStatus =>
        ExcursionCount == 0     ? "Good"   :
        ExcursionCount <= 3     ? "Medium" : "Poor";

    public List<ChartPointDto> TrendPoints { get; set; } = new();
}

public class ChartPointDto
{
    public DateTime Time  { get; set; }
    public double   Value { get; set; }
}

public class AlertEvidenceDto
{
    public string   SignalName      { get; set; } = string.Empty;
    public DateTime TriggeredAt     { get; set; }
    public double   TriggerValue    { get; set; }
    public double   ThresholdValue  { get; set; }
    public string   Severity        { get; set; } = string.Empty;
    public string   Status          { get; set; } = string.Empty;
}

/// <summary>Full consolidated evidence object passed to the narrative generator.</summary>
public class ReportEvidenceDto
{
    public AssetDto                     Asset             { get; set; } = new();
    public List<AssetDto>               AllAssets         { get; set; } = new();
    public DateTime                     From              { get; set; }
    public DateTime                     To                { get; set; }
    public string                       OverallHealth     { get; set; } = "Good";
    public int                          HealthScore       { get; set; } = 100;
    public int                          TotalExcursions   { get; set; }
    public int                          CriticalAlerts    { get; set; }
    public int                          WarningAlerts     { get; set; }
    public List<SignalEvidenceDto>       Signals           { get; set; } = new();
    public List<AlertEvidenceDto>        TopAlerts         { get; set; } = new();

    // Structural report datasets
    public ContextSummaryDto            Context           { get; set; } = new();
    public List<HealthMatrixCellDto>    HealthMatrix      { get; set; } = new();
    public List<AssetAnalysisRowDto>    AssetAnalysisRows { get; set; } = new();
    public List<MotorInsightRowDto>     MotorInsights     { get; set; } = new();
    public List<MotorDetailSectionDto>  MotorDetails      { get; set; } = new();
    public List<AssetSpecificationDto>  AssetSpecs        { get; set; } = new();
}

// ─────────────────────────────────────────────────────────────────────────────
// PHASE 2 — LLM Inference Contract (Exact Qualitative Text Lines)
// ─────────────────────────────────────────────────────────────────────────────

public class ReportNarrativeDto
{
    [JsonPropertyName("executiveSummary")]
    public string ExecutiveSummary { get; set; } = string.Empty;

    [JsonPropertyName("healthMatrixInference")]
    public string HealthMatrixInference { get; set; } = string.Empty;

    [JsonPropertyName("assetAnalysisInference")]
    public string AssetAnalysisInference { get; set; } = string.Empty;

    [JsonPropertyName("healthIndexBulletPoints")]
    public List<string> HealthIndexBulletPoints { get; set; } = new();

    [JsonPropertyName("worstScoreInference")]
    public string WorstScoreInference { get; set; } = string.Empty;

    [JsonPropertyName("powerSupplyInference")]
    public string PowerSupplyInference { get; set; } = string.Empty;

    [JsonPropertyName("sensorInterpretations")]
    public List<SensorInterpretationDto> SensorInterpretations { get; set; } = new();

    [JsonPropertyName("rootCauseAnalysis")]
    public List<RcaFindingDto> RootCauseAnalysis { get; set; } = new();

    [JsonPropertyName("recommendations")]
    public List<RecommendationDto> Recommendations { get; set; } = new();

    [JsonPropertyName("conclusion")]
    public string Conclusion { get; set; } = string.Empty;
}

public class SensorInterpretationDto
{
    [JsonPropertyName("signalName")]
    public string SignalName   { get; set; } = string.Empty;

    [JsonPropertyName("observation")]
    public string Observation  { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity     { get; set; } = "Low";
}

public class RcaFindingDto
{
    [JsonPropertyName("finding")]
    public string Finding       { get; set; } = string.Empty;

    [JsonPropertyName("possibleCause")]
    public string PossibleCause { get; set; } = string.Empty;

    [JsonPropertyName("likelihood")]
    public string Likelihood    { get; set; } = "Medium";
}

public class RecommendationDto
{
    [JsonPropertyName("action")]
    public string Action    { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    public string Priority  { get; set; } = "Medium";

    [JsonPropertyName("timeframe")]
    public string Timeframe { get; set; } = string.Empty;
}

// ─────────────────────────────────────────────────────────────────────────────
// PHASE 3 — Master Model for PDF Template Rendering
// ─────────────────────────────────────────────────────────────────────────────

public class EcoReportModel
{
    public ReportEvidenceDto  Evidence    { get; set; } = new();
    public ReportNarrativeDto Narrative   { get; set; } = new();
    public DateTime           GeneratedAt { get; set; } = DateTime.UtcNow;
    public string             ReportId    { get; set; } = string.Empty;
}
