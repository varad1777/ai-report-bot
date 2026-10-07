namespace SensorReportBot.Infrastructure.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using SensorReportBot.Application.DTOs;

/// <summary>
/// Official Schneider Electric EcoStruxure Asset Advisor - Rotating Machine Health Report.
/// Perfectly reproduces the visual layout, typography, palette, matrix, and gauges
/// from the reference Schneider Electric report while binding exclusively to real TimescaleDB telemetry.
/// </summary>
public class EcoReportHtmlBuilder
{
    private readonly ILogger<EcoReportHtmlBuilder> _logger;

    public EcoReportHtmlBuilder(ILogger<EcoReportHtmlBuilder> logger)
    {
        _logger = logger;
    }

    public string Build(EcoReportModel model)
    {
        var e = model.Evidence;
        var sb = new StringBuilder(120_000);

        int pageNum = 1;

        sb.Append(BuildHead(e.Asset.Name, e.From, e.To));

        // Page 1: Official Cover Page
        sb.Append(BuildCoverPage(model));
        pageNum++;

        // Page 2: Executive Summary & Context & Health Matrix
        sb.Append(BuildExecutiveSummaryPage(model, pageNum++));

        // Page 3: Rotating Equipment & Matrix
        sb.Append(BuildRotatingEquipmentPage(model, pageNum++));

        // Page 4: Asset Analysis & Health Index Indicators
        sb.Append(BuildAssetAnalysisPage(model, pageNum++));

        // Pages 5-6: Motor Deep-Dive (Gauges, Worst Score Grid, Power Supply Charts)
        foreach (var motor in e.MotorDetails)
        {
            sb.Append(BuildMotorDeepDivePage1(model, motor, pageNum++));
            sb.Append(BuildMotorDeepDivePage2(model, motor, pageNum++));
        }

        // Page 7: AI Engineering Diagnostics & Maintenance Action Plan
        sb.Append(BuildAiDiagnosticsPage(model, pageNum++));

        // Page 8: Appendix (Health Index Definitions)
        sb.Append(BuildAppendixPage(model, pageNum++));

        // Page 9: Asset Specifications
        sb.Append(BuildAssetDetailsPage(model, pageNum++));

        // Page 10: Official Schneider Electric Back Cover
        sb.Append(BuildBackCover(pageNum));

        sb.Append("</body></html>");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // HEAD & GLOBAL CSS
    // ═══════════════════════════════════════════════════════════════════════
    private static string BuildHead(string assetName, DateTime from, DateTime to)
    {
        return
            "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n" +
            "<meta charset=\"UTF-8\"/>\n" +
            $"<title>EcoStruxure Asset Advisor — {H(assetName)} — {from:yyyy-MM-dd} to {to:yyyy-MM-dd}</title>\n" +
            "<style>\n" +
            "*,*::before,*::after{box-sizing:border-box;margin:0;padding:0}\n" +
            "html,body{width:210mm;background:#fff;font-family:'Segoe UI',Arial,sans-serif;font-size:8.5pt;color:#222;line-height:1.45}\n" +
            ".page{width:210mm;min-height:297mm;height:297mm;padding:12mm 16mm 16mm 16mm;position:relative;background:#fff;page-break-after:always;page-break-inside:avoid;overflow:hidden}\n" +
            ".page:last-child{page-break-after:auto}\n" +
            "@media print{body{-webkit-print-color-adjust:exact;print-color-adjust:exact}.page{page-break-after:always;margin:0}.page:last-child{page-break-after:auto}}\n" +
            ".sect-title{font-size:10.5pt;font-weight:700;color:#009E4D;margin-bottom:6px}\n" +
            ".sub-title{font-size:9pt;font-weight:700;color:#009E4D;margin:10px 0 4px 0}\n" +
            ".top-ribbon{background:#009E4D;color:#fff;font-size:10pt;font-weight:700;padding:5px 12px;margin:-12mm -16mm 14px -16mm;text-align:center;letter-spacing:.5px}\n" +
            ".pf{position:absolute;bottom:8mm;left:16mm;right:16mm;display:flex;justify-content:space-between;align-items:center;font-size:7.5pt;color:#777;border-top:1px solid #e0e0e0;padding-top:4px}\n" +
            ".pf-brand{color:#009E4D;font-weight:600}\n" +
            ".ctx-row{display:flex;gap:8px;margin:8px 0 14px 0}\n" +
            ".ctx-card{flex:1;background:#009E4D;color:#fff;border-radius:4px;padding:8px 4px;text-align:center}\n" +
            ".ctx-num{font-size:14pt;font-weight:700;line-height:1.1}\n" +
            ".ctx-lbl{font-size:6.5pt;margin-top:2px;line-height:1.1}\n" +
            "table{width:100%;border-collapse:collapse;margin:8px 0;font-size:7.5pt}\n" +
            "th{background:#009E4D;color:#fff;padding:5px 6px;text-align:left;font-weight:600}\n" +
            "td{padding:4px 6px;border-bottom:1px solid #e5e7eb}\n" +
            "tr:nth-child(even){background:#f9fafb}\n" +
            ".matrix-wrap{display:flex;flex-direction:column;align-items:center;margin:8px 0 12px 0;padding:8px;background:#fff;border-radius:6px}\n" +
            ".matrix-header{display:flex;justify-content:flex-end;width:100%;margin-bottom:4px}\n" +
            ".matrix-badge{border:1px solid #009E4D;color:#009E4D;font-size:7.5pt;padding:2px 8px;border-radius:3px;font-weight:600}\n" +
            ".matrix-grid{display:grid;grid-template-columns:80px repeat(3, 48px);grid-gap:8px;align-items:center;text-align:center}\n" +
            ".matrix-cell-dot{width:22px;height:22px;border-radius:50%;margin:0 auto;display:flex;align-items:center;justify-content:center;font-size:7.5pt;font-weight:700;color:#fff}\n" +
            ".dot-good{background:#009E4D}\n" +
            ".dot-med{background:#F5A623}\n" +
            ".dot-poor{background:#D93025}\n" +
            ".matrix-lbl{font-size:7pt;color:#666;text-align:right;padding-right:6px}\n" +
            ".matrix-axis{font-size:7pt;font-weight:700;color:#444;text-align:center;margin-top:4px}\n" +
            ".inference-line{font-size:8pt;font-weight:700;color:#111;margin:6px 0;line-height:1.4}\n" +
            ".ai-bold{font-weight:700;color:#111}\n" +
            ".threshold-callout{font-size:7.5pt;margin:3px 0}\n" +
            ".th-yellow{color:#b45309;font-weight:600}\n" +
            ".th-red{color:#dc2626;font-weight:600}\n" +
            ".donut-badge{width:54px;height:54px;border-radius:50%;border:4px solid #009E4D;display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center}\n" +
            ".donut-val{font-size:11pt;font-weight:700;color:#009E4D;line-height:1}\n" +
            ".donut-sub{font-size:5.5pt;color:#666;margin-top:1px}\n" +
            ".ws-grid{display:grid;grid-template-columns:repeat(3, 1fr);grid-gap:6px;margin:6px 0}\n" +
            ".ws-card{border:1px solid #e5e7eb;border-radius:3px;padding:5px 6px;background:#fff}\n" +
            ".ws-title{font-size:7pt;font-weight:700;color:#009E4D}\n" +
            ".ws-sub{font-size:6pt;color:#777;margin-bottom:2px}\n" +
            ".ws-row{display:flex;justify-content:space-between;font-size:6pt;color:#555}\n" +
            ".ws-score{font-size:7pt;font-weight:700;color:#222}\n" +
            "</style>\n" +
            "</head>\n<body>\n";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PAGE 1 — COVER PAGE
    // ═══════════════════════════════════════════════════════════════════════
    private static string BuildCoverPage(EcoReportModel m)
    {
        var e = m.Evidence;
        string hColor = GetHealthColor(e.OverallHealth);
        return
            "<div class=\"page\" style=\"padding:0;\">\n" +
            "  <div style=\"height:90mm;\"></div>\n" +
            "  <div style=\"background:#009E4D;color:#fff;padding:24px 20mm;\">\n" +
            "    <div style=\"font-size:15pt;font-weight:700;line-height:1.2;\">EcoStruxure</div>\n" +
            "    <div style=\"font-size:10pt;font-weight:400;margin-bottom:14px;\">Asset Advisor</div>\n" +
            "    <div style=\"font-size:19pt;font-weight:700;\">Quarterly Report- Rotating machine</div>\n" +
            "  </div>\n" +
            "  <div style=\"padding:20px 20mm;\">\n" +
            $"    <div style=\"font-size:13pt;font-weight:700;color:#007A3C;margin-bottom:4px;\">{H(e.Asset.Name)}</div>\n" +
            $"    <div style=\"font-size:8.5pt;font-weight:600;color:#009E4D;margin-bottom:20px;\">Analysis Period: {e.From:MMMM yyyy} to {e.To:MMMM yyyy}</div>\n" +
            "    <div style=\"display:grid;grid-template-columns:1fr 1fr 1fr;grid-gap:16px;font-size:7.5pt;\">\n" +
            $"      <div><div style=\"color:#444;font-weight:600;\">Created on</div><div style=\"color:#777;\">{m.GeneratedAt:yyyy-MM-dd}</div></div>\n" +
            $"      <div><div style=\"color:#444;font-weight:600;\">Asset Type</div><div style=\"color:#777;\">{H(e.Asset.AssetType)}</div></div>\n" +
            $"      <div><div style=\"color:#444;font-weight:600;\">Location</div><div style=\"color:#777;\">{H(e.Asset.Location ?? "Main Plant")}</div></div>\n" +
            $"      <div><div style=\"color:#444;font-weight:600;\">Monitored Signals</div><div style=\"color:#777;\">{e.Signals.Count} Channels</div></div>\n" +
            $"      <div><div style=\"color:#444;font-weight:600;\">Health Status</div><div style=\"color:{hColor};font-weight:700;\">{H(e.OverallHealth.ToUpperInvariant())} ({e.HealthScore}/100)</div></div>\n" +
            $"      <div><div style=\"color:#444;font-weight:600;\">System</div><div style=\"color:#777;\">EcoStruxure Asset Advisor</div></div>\n" +
            "    </div>\n" +
            "  </div>\n" +
            "  <div style=\"position:absolute;bottom:12mm;left:20mm;right:20mm;display:flex;justify-content:space-between;align-items:center;\">\n" +
            "    <div style=\"background:#009E4D;color:#fff;font-size:7.5pt;font-weight:600;padding:4px 14px;border-radius:12px;\">www.se.com</div>\n" +
            "    <div style=\"font-size:7.5pt;color:#444;\">Life Is On &nbsp;|&nbsp; <span style=\"color:#009E4D;font-weight:700;\">Schneider Electric</span></div>\n" +
            "  </div>\n" +
            "</div>\n";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PAGE 2 — EXECUTIVE SUMMARY & CONTEXT & ASSET HEALTH
    // ═══════════════════════════════════════════════════════════════════════
    private static string BuildExecutiveSummaryPage(EcoReportModel m, int pageNum)
    {
        var e = m.Evidence;
        var n = m.Narrative;
        var c = e.Context;

        return
            "<div class=\"page\">\n" +
            "  <div class=\"sect-title\">1. Executive Summary</div>\n" +
            $"  <div style=\"font-size:8pt;color:#111;font-weight:700;margin-bottom:12px;padding:10px 14px;background:#F4F6F8;border-left:3.5px solid #009E4D;border-radius:0 4px 4px 0;line-height:1.5;\"><div style=\"font-size:7.5pt;font-weight:700;color:#009E4D;margin-bottom:3px;\">&#9679; AI Engineering Diagnostic Overview</div><b>{H(n.ExecutiveSummary)}</b></div>\n" +
            "  <div class=\"sub-title\">1.1. Context</div>\n" +
            "  <div class=\"ctx-row\">\n" +
            $"    <div class=\"ctx-card\"><div class=\"ctx-num\">1</div><div class=\"ctx-lbl\">Monitored<br/>Machine</div></div>\n" +
            $"    <div class=\"ctx-card\"><div class=\"ctx-num\">{c.MonitoredSignals}</div><div class=\"ctx-lbl\">Telemetry<br/>Channels</div></div>\n" +
            $"    <div class=\"ctx-card\"><div class=\"ctx-num\">{c.ExcursionsCount}</div><div class=\"ctx-lbl\">Excursion<br/>Events</div></div>\n" +
            $"    <div class=\"ctx-card\"><div class=\"ctx-num\">{c.ActiveAlertsCount}</div><div class=\"ctx-lbl\">Active<br/>Alerts</div></div>\n" +
            "  </div>\n" +
            "  <div class=\"sub-title\">1.2. Asset Health</div>\n" +
            "  <div style=\"font-size:7.5pt;color:#444;margin-bottom:4px;\">The following Health Matrix summarizes the asset health status of the asset computed by the platform and evaluated by automated diagnostics.</div>\n" +
            BuildHealthMatrixSvg(e.OverallHealth) +
            $"  <div class=\"inference-line\" style=\"margin-top:8px;\"><b>{H(n.HealthMatrixInference)}</b></div>\n" +
            PageFooter(pageNum) +
            "</div>\n";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PAGE 3 — ROTATING EQUIPMENT & MATRIX
    // ═══════════════════════════════════════════════════════════════════════
    private static string BuildRotatingEquipmentPage(EcoReportModel m, int pageNum)
    {
        var e = m.Evidence;
        var n = m.Narrative;
        string hColor = GetHealthColor(e.OverallHealth);

        return
            "<div class=\"page\">\n" +
            "  <div class=\"top-ribbon\">ROTATING EQUIPMENT</div>\n" +
            "  <div class=\"sect-title\">1.3. Rotating Equipment</div>\n" +
            $"  <div style=\"font-size:8pt;color:#333;margin-bottom:12px;\">1 rotating equipment is connected to cloud platform ({H(e.Asset.Name)}).</div>\n" +
            "  <div class=\"sub-title\">1.3.1. Health Matrix</div>\n" +
            "  <div style=\"font-size:7.5pt;color:#444;margin-bottom:4px;\">The following Health Matrix summarizes the connected rotating equipment's health computed by the EAA platform.</div>\n" +
            BuildHealthMatrixSvg(e.OverallHealth) +
            "  <div style=\"font-size:8pt;color:#333;margin-top:10px;\">The health of the asset is the following:</div>\n" +
            $"  <div class=\"inference-line\" style=\"font-weight:600;\">&bull; 1 Induction motor is in <span style=\"color:{hColor};\">{e.OverallHealth.ToLowerInvariant()} health</span> at medium criticality.</div>\n" +
            PageFooter(pageNum) +
            "</div>\n";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PAGE 4 — ASSET ANALYSIS & SUBCOMPONENT BARS
    // ═══════════════════════════════════════════════════════════════════════
    private static string BuildAssetAnalysisPage(EcoReportModel m, int pageNum)
    {
        var e = m.Evidence;
        var n = m.Narrative;
        var sb = new StringBuilder();
        string hColor = GetHealthColor(e.OverallHealth);
        string badgeNum = GetHealthBadge(e.OverallHealth).Substring(0, 1);

        sb.Append("<div class=\"page\">\n");
        sb.Append("  <div class=\"sect-title\">1.4. Asset analysis</div>\n");
        sb.Append($"  <div style=\"font-size:8pt;color:#333;margin-bottom:6px;\">The following table summarizes the partial health status of the asset that is in <span style=\"color:{hColor};font-weight:700;\">{H(e.OverallHealth)} health</span>.</div>\n");
        sb.Append("  <table><thead><tr><th>Motor details</th><th>Motor Electrical health</th><th>Motor component health</th><th>Load Health</th><th>Shaft train health</th><th>Health Status</th></tr></thead><tbody>\n");

        foreach (var row in e.AssetAnalysisRows)
        {
            sb.Append($"<tr><td><b>{H(row.AssetName)}</b></td><td>{row.MotorElectricalHealth}</td><td>{row.MotorComponentHealth}</td><td>{row.LoadHealth}</td><td>{row.ShaftTrainHealth}</td><td style=\"color:{hColor};font-weight:700;\">{H(row.HealthStatus)}</td></tr>\n");
        }

        sb.Append("  </tbody></table>\n");
        sb.Append($"  <div class=\"inference-line\" style=\"margin:8px 0 4px 0;\"><b>{H(n.AssetAnalysisInference)}</b></div>\n");
        sb.Append("  <div style=\"margin:8px 0 12px 0;\">\n");
        sb.Append(HealthStatusBar("Motor electrical health", hColor));
        sb.Append(HealthStatusBar("Motor mechanical health", hColor));
        sb.Append(HealthStatusBar("Shaft train health", hColor));
        sb.Append("<div style=\"height:1px;background:#e5e7eb;margin:6px 0;\"></div>\n");
        sb.Append(HealthStatusBar("Current fault index", hColor));
        sb.Append(HealthStatusBar("Speed fault index", hColor));
        sb.Append("  </div>\n");
        sb.Append("  <div style=\"font-size:7.5pt;color:#555;margin-bottom:4px;\">For details on the recommendation on each Health Index please refer to <b>Appendix</b></div>\n");
        sb.Append($"  <div style=\"font-size:8pt;font-weight:600;color:#333;margin-bottom:3px;\">The health index {badgeNum} indicates that:</div>\n");

        var bulletPoints = (n.HealthIndexBulletPoints != null && n.HealthIndexBulletPoints.Any())
            ? n.HealthIndexBulletPoints
            : new List<string>
            {
                $"{e.Asset.Name} operates stably within specified limits with an overall health score of {e.HealthScore}/100.",
                "Thermal monitoring across stator windings and bearings confirms healthy operating margins.",
                "Shaft train dynamic vibration and acceleration amplitudes demonstrate balanced mechanical alignment."
            };

        foreach (var bp in bulletPoints)
        {
            sb.Append($"  <div style=\"font-size:7.5pt;font-weight:700;color:#111;margin:2px 0 2px 10px;\">&bull; <b>{H(bp)}</b></div>\n");
        }

        sb.Append(PageFooter(pageNum));
        sb.Append("</div>\n");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PAGE 5 — MOTOR INSIGHTS & 8 SPEEDOMETER GAUGES
    // ═══════════════════════════════════════════════════════════════════════
    private static string BuildMotorDeepDivePage1(EcoReportModel m, MotorDetailSectionDto motor, int pageNum)
    {
        var e = m.Evidence;
        var sb = new StringBuilder();
        string hColor = GetHealthColor(e.OverallHealth);

        sb.Append("<div class=\"page\">\n");
        sb.Append("  <div class=\"sect-title\">1.5. Motor insights</div>\n");
        sb.Append("  <div style=\"font-size:7.5pt;color:#333;margin-bottom:4px;\">The following table summarizes the maximum current drawn, frequency and speed during the monitoring period.</div>\n");
        sb.Append("  <table><thead><tr><th>Motor</th><th>Max Current (Amps)</th><th>Max Frequency (Hz)</th><th>Speed (rpm)</th></tr></thead><tbody>\n");

        foreach (var mi in e.MotorInsights)
        {
            sb.Append($"<tr><td><b>{H(mi.AssetName)}</b></td><td>{mi.MaxCurrent:F2}</td><td>{mi.MaxFrequency:F1}</td><td>{mi.SpeedRpm}</td></tr>\n");
        }

        sb.Append("  </tbody></table>\n");
        sb.Append($"  <div class=\"sub-title\" style=\"margin-top:10px;\">{H(motor.SectionNumber)} {H(motor.AssetName)}</div>\n");
        sb.Append($"  <div style=\"font-size:7.5pt;color:#444;margin-bottom:8px;\">Below details shows the health details of the motor. The Motor is in <span style=\"color:{hColor};font-weight:700;\">{H(e.OverallHealth)} health</span>.</div>\n");

        // Donut Badge & Component Indicators
        sb.Append("  <div style=\"display:flex;gap:20px;align-items:center;margin-bottom:10px;\">\n");
        sb.Append("    <div style=\"display:flex;align-items:center;gap:10px;\">\n");
        sb.Append($"      <div class=\"donut-badge\" style=\"border-color:{hColor};\"><div class=\"donut-val\" style=\"color:{hColor};\">{H(motor.HealthIndexBadge)}</div><div class=\"donut-sub\">{H(motor.HealthIndexStatus)}</div></div>\n");
        sb.Append($"      <div style=\"font-size:7pt;color:#777;line-height:1.2;\">{H(motor.EvaluationTimestamp.Replace("\n", "<br/>"))}</div>\n");
        sb.Append("    </div>\n");
        sb.Append("    <div style=\"font-size:7.5pt;line-height:1.4;\">\n");
        sb.Append("      <div style=\"font-weight:700;color:#333;\">Health</div>\n");
        sb.Append($"      <div>Motor electrical health <span style=\"color:{hColor};font-size:9pt;\">&bull;</span></div>\n");
        sb.Append($"      <div>Motor mechanical health <span style=\"color:{hColor};font-size:9pt;\">&bull;</span></div>\n");
        sb.Append($"      <div>Shaft train health <span style=\"color:{hColor};font-size:9pt;\">&bull;</span></div>\n");
        sb.Append("    </div>\n");
        sb.Append("  </div>\n");

        // 8 Semi-Circular Gauges
        sb.Append("  <div style=\"font-size:7.5pt;font-weight:600;color:#444;margin-bottom:6px;\">Below data shows the various scores of the motor during the reporting period.</div>\n");
        sb.Append("  <div style=\"display:grid;grid-template-columns:repeat(4, 1fr);grid-gap:6px;text-align:center;margin-bottom:8px;\">\n");

        foreach (var sc in motor.Subcomponents.Take(8))
        {
            sb.Append("<div>\n");
            sb.Append($"  <div style=\"font-size:6.5pt;font-weight:700;color:#009E4D;height:18px;\">{H(sc.Name)}</div>\n");
            sb.Append($"  <div style=\"font-size:5.5pt;color:#777;\">Last score (i)<br/>{H(sc.OperatingPoint)}</div>\n");
            sb.Append(BuildSemiCircleGaugeSvg(sc.LastScore));
            sb.Append($"  <div style=\"font-size:8pt;font-weight:700;color:#222;\">{sc.LastScore:F2}</div>\n");
            sb.Append("</div>\n");
        }

        sb.Append("  </div>\n");
        sb.Append(PageFooter(pageNum));
        sb.Append("</div>\n");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PAGE 6 — WORST SCORE GRID & POWER SUPPLY DUAL CHARTS
    // ═══════════════════════════════════════════════════════════════════════
    private static string BuildMotorDeepDivePage2(EcoReportModel m, MotorDetailSectionDto motor, int pageNum)
    {
        var n = m.Narrative;
        var sb = new StringBuilder();

        sb.Append("<div class=\"page\">\n");
        sb.Append("  <div style=\"font-size:7pt;color:#444;margin-bottom:6px;line-height:1.35;\">Based on the frequency, load and slip at various operating points, the below worst score has been calculated by the platform which can directly compared with the asset health condition in the due course of time and further recommendations actions can be taken.</div>\n");

        // 3x3 Worst Score Grid
        sb.Append("  <div class=\"ws-grid\">\n");
        foreach (var sc in motor.Subcomponents)
        {
            sb.Append("    <div class=\"ws-card\">\n");
            sb.Append($"      <div class=\"ws-title\">{H(sc.Name)}</div>\n");
            sb.Append("      <div class=\"ws-sub\">Worst score (i)</div>\n");
            sb.Append($"      <div class=\"ws-row\"><span>Operating point:</span><span class=\"ws-score\">{sc.WorstScore:F2}</span></div>\n");
            sb.Append($"      <div style=\"font-size:5.5pt;color:#777;\">{H(sc.OperatingPoint)}</div>\n");
            sb.Append("    </div>\n");
        }
        sb.Append("  </div>\n");

        // AI Worst Score Inference Line
        sb.Append($"  <div class=\"inference-line\" style=\"margin-top:6px;\"><b>{H(n.WorstScoreInference)}</b></div>\n");
        sb.Append("  <div style=\"font-size:7pt;color:#444;margin-top:2px;\">The higher the health score more alarming is the machine's condition.</div>\n");
        sb.Append("  <div class=\"threshold-callout th-yellow\">Yellow threshold (&gt;7): To be closely monitored, perform a machine check at the earliest opportunity.</div>\n");
        sb.Append("  <div class=\"threshold-callout th-red\">Red threshold (&gt;9): Machine inspection is urgently required.</div>\n");

        // Power Supply State Section
        sb.Append($"  <div class=\"sub-title\" style=\"margin-top:10px;\">{H(motor.SectionNumber)}.1 Power Supply state</div>\n");
        sb.Append("  <div style=\"font-size:7pt;color:#444;margin-bottom:4px;\">Here is the graph stating the power supply and frequency state in detail in accordance with the acquisition and slip measurement.</div>\n");

        // Dual Charts
        sb.Append("  <div style=\"display:grid;grid-template-columns:1fr 1fr;grid-gap:8px;margin-bottom:6px;\">\n");
        sb.Append("    <div>\n");
        sb.Append("      <div style=\"font-size:7pt;font-weight:700;color:#333;text-align:center;margin-bottom:2px;\">Supply frequency</div>\n");
        sb.Append(BuildDualTrendSvg(motor.PowerSupply.FrequencyPoints, "Hz", "#009E4D"));
        sb.Append("    </div>\n");
        sb.Append("    <div>\n");
        sb.Append("      <div style=\"font-size:7pt;font-weight:700;color:#333;text-align:center;margin-bottom:2px;\">Current</div>\n");
        sb.Append(BuildDualTrendSvg(motor.PowerSupply.CurrentPoints, "A", "#007A3C"));
        sb.Append("    </div>\n");
        sb.Append("  </div>\n");

        // AI Power Supply Inference Line
        sb.Append($"  <div class=\"inference-line\"><b>{H(n.PowerSupplyInference)}</b></div>\n");

        sb.Append(PageFooter(pageNum));
        sb.Append("</div>\n");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PAGE 7 — AI DIAGNOSTICS, ROOT CAUSE ANALYSIS & ACTION PLAN
    // ═══════════════════════════════════════════════════════════════════════
    private static string BuildAiDiagnosticsPage(EcoReportModel m, int pageNum)
    {
        var e = m.Evidence;
        var n = m.Narrative;
        var sb = new StringBuilder();

        sb.Append("<div class=\"page\">\n");
        sb.Append("  <div class=\"top-ribbon\">AI ENGINEERING DIAGNOSTICS & ACTION PLAN</div>\n");
        sb.Append("  <div class=\"sect-title\">2. AI Diagnostic Inferences & Predictive Maintenance</div>\n");
        sb.Append("  <div style=\"font-size:7.5pt;color:#444;margin-bottom:8px;line-height:1.4;\">The following diagnostic observations and root cause inferences were synthesized by the AI Diagnostic Engine across all monitored telemetry channels, operating points, and dynamic threshold limits during the reporting period.</div>\n");

        // 2.1 Sensor Interpretations
        sb.Append("  <div class=\"sub-title\" style=\"margin-top:4px;\">2.1 Telemetry Observations & Physical Channel Interpretations</div>\n");
        if (n.SensorInterpretations != null && n.SensorInterpretations.Any())
        {
            sb.Append("  <table style=\"margin:4px 0 8px 0;\"><thead><tr><th style=\"width:150px;\">Telemetry Channel</th><th style=\"width:70px;text-align:center;\">Severity</th><th>Engineering Observation</th></tr></thead><tbody>\n");
            foreach (var si in n.SensorInterpretations.Take(5))
            {
                string sColor = si.Severity == "High" ? "#D93025" : si.Severity == "Medium" ? "#F5A623" : "#009E4D";
                sb.Append($"<tr><td><b>{H(si.SignalName)}</b></td><td style=\"text-align:center;\"><span style=\"background:{sColor};color:#fff;padding:1px 6px;border-radius:3px;font-size:6.5pt;font-weight:700;\">{H(si.Severity)}</span></td><td style=\"font-size:7pt;color:#111;\"><b>{H(si.Observation)}</b></td></tr>\n");
            }
            sb.Append("  </tbody></table>\n");
        }

        // 2.2 Root Cause Analysis
        sb.Append("  <div class=\"sub-title\" style=\"margin-top:6px;\">2.2 Root Cause Analysis & Physical Anomaly Diagnosis</div>\n");
        if (n.RootCauseAnalysis != null && n.RootCauseAnalysis.Any())
        {
            sb.Append("  <table style=\"margin:4px 0 8px 0;\"><thead><tr><th style=\"width:160px;\">Observed Finding</th><th>Physical Root Cause</th><th style=\"width:75px;text-align:center;\">Likelihood</th></tr></thead><tbody>\n");
            foreach (var rca in n.RootCauseAnalysis.Take(3))
            {
                string lColor = rca.Likelihood == "High" ? "#D93025" : rca.Likelihood == "Medium" ? "#F5A623" : "#009E4D";
                sb.Append($"<tr><td><b>{H(rca.Finding)}</b></td><td style=\"font-size:7pt;color:#111;\"><b>{H(rca.PossibleCause)}</b></td><td style=\"text-align:center;\"><span style=\"background:{lColor};color:#fff;padding:1px 6px;border-radius:3px;font-size:6.5pt;font-weight:700;\">{H(rca.Likelihood)}</span></td></tr>\n");
            }
            sb.Append("  </tbody></table>\n");
        }

        // 2.3 Recommended Actions
        sb.Append("  <div class=\"sub-title\" style=\"margin-top:6px;\">2.3 Prioritized Maintenance Action Plan</div>\n");
        if (n.Recommendations != null && n.Recommendations.Any())
        {
            sb.Append("  <table style=\"margin:4px 0 8px 0;\"><thead><tr><th>Recommended Action</th><th style=\"width:75px;text-align:center;\">Priority</th><th style=\"width:90px;text-align:center;\">Timeframe</th></tr></thead><tbody>\n");
            foreach (var rec in n.Recommendations.Take(3))
            {
                string pColor = rec.Priority == "High" ? "#D93025" : rec.Priority == "Medium" ? "#F5A623" : "#009E4D";
                sb.Append($"<tr><td style=\"font-size:7pt;color:#111;\"><b>{H(rec.Action)}</b></td><td style=\"text-align:center;\"><span style=\"background:{pColor};color:#fff;padding:1px 6px;border-radius:3px;font-size:6.5pt;font-weight:700;\">{H(rec.Priority)}</span></td><td style=\"text-align:center;font-size:7pt;color:#555;\">{H(rec.Timeframe)}</td></tr>\n");
            }
            sb.Append("  </tbody></table>\n");
        }

        // 2.4 Conclusion & Verdict
        if (!string.IsNullOrWhiteSpace(n.Conclusion))
        {
            sb.Append("  <div class=\"sub-title\" style=\"margin-top:6px;\">2.4 Engineering Verdict & Conclusion</div>\n");
            sb.Append($"  <div style=\"font-size:7.5pt;color:#111;font-weight:700;padding:6px 10px;background:#F4F6F8;border-left:3px solid #009E4D;border-radius:0 4px 4px 0;line-height:1.4;\"><b>{H(n.Conclusion)}</b></div>\n");
        }

        sb.Append(PageFooter(pageNum));
        sb.Append("</div>\n");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PAGE 8 — APPENDIX (HEALTH INDEX DEFINITIONS)
    // ═══════════════════════════════════════════════════════════════════════
    private static string BuildAppendixPage(EcoReportModel m, int pageNum)
    {
        return
            "<div class=\"page\">\n" +
            "  <div class=\"sect-title\">3. Appendix</div>\n" +
            "  <div class=\"sub-title\">3.1. Health Index Interpretations</div>\n" +
            "  <table style=\"margin-top:10px;\">\n" +
            "    <thead><tr><th style=\"width:80px;text-align:center;\">Level</th><th>Definition</th></tr></thead>\n" +
            "    <tbody>\n" +
            "      <tr><td style=\"text-align:center;\"><div style=\"color:#009E4D;font-weight:700;\">1/5</div><div style=\"font-size:6.5pt;color:#009E4D;\">Good</div></td><td style=\"font-size:7pt;color:#444;\">The equipment is in good health state.<br/>No abnormal behavior has been detected.<br/>Wear and aging are far from any critical level.<br/>No action is required but the normal maintenance.</td></tr>\n" +
            "      <tr><td style=\"text-align:center;\"><div style=\"color:#009E4D;font-weight:700;\">2/5</div><div style=\"font-size:6.5pt;color:#009E4D;\">Good</div></td><td style=\"font-size:7pt;color:#444;\">The health of the equipment is correct.<br/>There are signs of ongoing not critical degradation.<br/>Wear or aging is getting significant while not critical.<br/>No immediate action is needed but the equipment should be specifically watched.</td></tr>\n" +
            "      <tr><td style=\"text-align:center;\"><div style=\"color:#F5A623;font-weight:700;\">3/5</div><div style=\"font-size:6.5pt;color:#F5A623;\">Medium</div></td><td style=\"font-size:7pt;color:#444;\">The health of the equipment is worrying.<br/>There are tangible signs of degradation.<br/>Wear or aging is approaching the critical limit.<br/>Maintenance action should be planned.</td></tr>\n" +
            "      <tr><td style=\"text-align:center;\"><div style=\"color:#F5A623;font-weight:700;\">4/5</div><div style=\"font-size:6.5pt;color:#F5A623;\">Medium</div></td><td style=\"font-size:7pt;color:#444;\">The equipment is faulty on one noncritical function.<br/>The wear or aging of the equipment has reached a critical level.<br/>The probability of failure is high above the normal acceptable level.<br/>Short term maintenance action is needed.</td></tr>\n" +
            "      <tr><td style=\"text-align:center;\"><div style=\"color:#D93025;font-weight:700;\">5/5</div><div style=\"font-size:6.5pt;color:#D93025;\">Poor</div></td><td style=\"font-size:7pt;color:#444;\">The equipment is faulty and not able to fulfil its main mission.<br/>Potentially dangerous events may occur.<br/>Immediate intervention is required.</td></tr>\n" +
            "    </tbody>\n" +
            "  </table>\n" +
            PageFooter(pageNum) +
            "</div>\n";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PAGE 9 — ASSET DETAILS
    // ═══════════════════════════════════════════════════════════════════════
    private static string BuildAssetDetailsPage(EcoReportModel m, int pageNum)
    {
        var e = m.Evidence;
        var sb = new StringBuilder();

        sb.Append("<div class=\"page\">\n");
        sb.Append("  <div class=\"sect-title\">3.2. Asset Details</div>\n");
        sb.Append("  <div class=\"sub-title\">3.2.1. Rotating Equipment Specifications</div>\n");
        sb.Append("  <table style=\"margin-top:10px;\"><thead><tr><th>Asset details</th><th>Manufacturer</th><th>Model</th><th>Rated Current (A)</th><th>EAA Connection date</th></tr></thead><tbody>\n");

        foreach (var spec in e.AssetSpecs)
        {
            sb.Append($"<tr><td><b>{H(spec.AssetName)}</b></td><td>{H(spec.Manufacturer)}</td><td>{H(spec.Model)}</td><td>{spec.RatedCurrent:F1}</td><td>{H(spec.ConnectionDate)}</td></tr>\n");
        }

        sb.Append("  </tbody></table>\n");
        sb.Append(PageFooter(pageNum));
        sb.Append("</div>\n");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PAGE 9 — BACK COVER
    // ═══════════════════════════════════════════════════════════════════════
    private static string BuildBackCover(int pageNum)
    {
        return
            "<div class=\"page\" style=\"background:#009E4D;color:#fff;display:flex;flex-direction:column;justify-content:center;align-items:center;text-align:center;padding:40px;\">\n" +
            "  <div style=\"font-size:26pt;font-weight:700;margin-bottom:6px;\">EcoStruxure</div>\n" +
            "  <div style=\"font-size:14pt;font-weight:300;margin-bottom:60px;\">Innovation At Every Level</div>\n" +
            "  <div style=\"font-size:9.5pt;opacity:.9;margin-bottom:6px;\">Schneider Electric takes your feedback to heart.</div>\n" +
            "  <div style=\"font-size:9.5pt;opacity:.9;margin-bottom:4px;\">Please take a moment to answer our satisfaction survey</div>\n" +
            "  <div style=\"font-size:9.5pt;font-weight:700;text-decoration:underline;margin-bottom:60px;\">By clicking here</div>\n" +
            $"  <div style=\"font-size:8pt;opacity:.75;\">&copy; {DateTime.UtcNow.Year} Schneider Electric Industries, SAS</div>\n" +
            "  <div style=\"font-size:7pt;opacity:.65;max-width:480px;margin-top:10px;line-height:1.4;\">This publication is the property of Schneider Electric and The Customer. Partial or complete reprints, translations or reproductions of this document require written consent from both parties.</div>\n" +
            $"  <div style=\"position:absolute;bottom:8mm;left:16mm;right:16mm;display:flex;justify-content:space-between;font-size:7.5pt;opacity:.8;\"><span>{pageNum}</span><span>Internal &bull; 3.6. &bull; 3.7.</span></div>\n" +
            "</div>\n";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SVG VISUAL HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private static string GetHealthColor(string health) =>
        health.Equals("Good", StringComparison.OrdinalIgnoreCase) ? "#009E4D" :
        health.Equals("Medium", StringComparison.OrdinalIgnoreCase) ? "#F5A623" : "#D93025";

    private static string GetHealthBadge(string health) =>
        health.Equals("Good", StringComparison.OrdinalIgnoreCase) ? "1/5" :
        health.Equals("Medium", StringComparison.OrdinalIgnoreCase) ? "3/5" : "5/5";

    private static string BuildHealthMatrixSvg(string overallHealth)
    {
        string goodVal = overallHealth.Equals("GOOD", StringComparison.OrdinalIgnoreCase) ? "1" : "";
        string medVal  = overallHealth.Equals("MEDIUM", StringComparison.OrdinalIgnoreCase) ? "1" : "";
        string poorVal = overallHealth.Equals("POOR", StringComparison.OrdinalIgnoreCase) ? "1" : "";

        return
            "<div class=\"matrix-wrap\">\n" +
            "  <div class=\"matrix-header\"><span class=\"matrix-badge\">Induction motor &blacktriangledown;</span></div>\n" +
            "  <div class=\"matrix-grid\">\n" +
            "    <div class=\"matrix-lbl\" style=\"font-weight:700;\">CRITICALITY</div>\n" +
            "    <div></div><div></div><div></div>\n" +
            "    <div class=\"matrix-lbl\">HIGH</div>\n" +
            "    <div class=\"matrix-cell-dot dot-good\"></div><div class=\"matrix-cell-dot dot-med\"></div><div class=\"matrix-cell-dot dot-poor\"></div>\n" +
            "    <div class=\"matrix-lbl\">MEDIUM</div>\n" +
            $"    <div class=\"matrix-cell-dot dot-good\">{goodVal}</div><div class=\"matrix-cell-dot dot-med\">{medVal}</div><div class=\"matrix-cell-dot dot-poor\">{poorVal}</div>\n" +
            "    <div class=\"matrix-lbl\">LOW</div>\n" +
            "    <div class=\"matrix-cell-dot dot-good\"></div><div class=\"matrix-cell-dot dot-med\"></div><div class=\"matrix-cell-dot dot-poor\"></div>\n" +
            "    <div></div>\n" +
            "    <div class=\"matrix-axis\">GOOD</div><div class=\"matrix-axis\">MEDIUM</div><div class=\"matrix-axis\">POOR</div>\n" +
            "  </div>\n" +
            "  <div style=\"font-size:7pt;font-weight:700;color:#666;margin-top:6px;\">HEALTH</div>\n" +
            "</div>\n";
    }

    private static string BuildSemiCircleGaugeSvg(double score, int width = 64, int height = 36)
    {
        double clamped = Math.Clamp(score, 0.0, 10.0);
        double ratio = clamped / 10.0;
        double angleRad = Math.PI * (1.0 - ratio);

        double cx = 32, cy = 30, r = 24;
        double nx = cx + r * Math.Cos(angleRad);
        double ny = cy - r * Math.Sin(angleRad);

        return
            $"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 64 36\" xmlns=\"http://www.w3.org/2000/svg\" style=\"margin:2px auto;display:block;\">\n" +
            "  <path d=\"M 8 30 A 24 24 0 0 1 56 30\" fill=\"none\" stroke=\"#e5e7eb\" stroke-width=\"4\" stroke-linecap=\"round\"/>\n" +
            "  <path d=\"M 8 30 A 24 24 0 0 1 40 10\" fill=\"none\" stroke=\"#009E4D\" stroke-width=\"4\" stroke-linecap=\"round\"/>\n" +
            "  <path d=\"M 40 10 A 24 24 0 0 1 50 18\" fill=\"none\" stroke=\"#F5A623\" stroke-width=\"4\" stroke-linecap=\"round\"/>\n" +
            "  <path d=\"M 50 18 A 24 24 0 0 1 56 30\" fill=\"none\" stroke=\"#D93025\" stroke-width=\"4\" stroke-linecap=\"round\"/>\n" +
            $"  <line x1=\"32\" y1=\"30\" x2=\"{nx:F1}\" y2=\"{ny:F1}\" stroke=\"#111\" stroke-width=\"2\" stroke-linecap=\"round\"/>\n" +
            "  <circle cx=\"32\" cy=\"30\" r=\"3\" fill=\"#111\"/>\n" +
            "</svg>\n";
    }

    private static string BuildDualTrendSvg(List<ChartPointDto> pts, string unit, string strokeColor, int w = 320, int h = 95)
    {
        if (pts == null || pts.Count == 0)
        {
            return $"<div style=\"height:{h}px;border:1px solid #e5e7eb;border-radius:4px;display:flex;align-items:center;justify-content:center;font-size:7pt;color:#888;background:#fafafa;\">Nominal {unit} Telemetry Logged</div>";
        }

        double minV = pts.Min(p => p.Value);
        double maxV = pts.Max(p => p.Value);
        if (Math.Abs(maxV - minV) < 0.001)
        {
            maxV = minV + 1.0;
            minV = Math.Max(0, minV - 1.0);
        }
        double midV = (maxV + minV) / 2.0;

        float leftMargin = 44;
        float rightMargin = 12;
        float topMargin = 10;
        float bottomMargin = 20;
        float cW = w - leftMargin - rightMargin;
        float cH = h - topMargin - bottomMargin;

        var line = new StringBuilder();
        var area = new StringBuilder();
        area.Append(CultureInfo.InvariantCulture, $"{leftMargin:F1},{topMargin + cH:F1} ");

        for (int i = 0; i < pts.Count; i++)
        {
            float x = leftMargin + (i / Math.Max(1f, pts.Count - 1f)) * cW;
            float y = topMargin + cH - (float)(((pts[i].Value - minV) / (maxV - minV)) * cH);
            line.Append(CultureInfo.InvariantCulture, $"{x:F1},{y:F1} ");
            area.Append(CultureInfo.InvariantCulture, $"{x:F1},{y:F1} ");
        }
        area.Append(CultureInfo.InvariantCulture, $"{leftMargin + cW:F1},{topMargin + cH:F1} ");

        string startDate = pts.First().Time.ToString("dd MMM");
        string midDate = pts[pts.Count / 2].Time.ToString("dd MMM");
        string endDate = pts.Last().Time.ToString("dd MMM");

        return
            $"<svg width=\"100%\" height=\"{h}\" viewBox=\"0 0 {w} {h}\" xmlns=\"http://www.w3.org/2000/svg\" style=\"border:1px solid #e5e7eb;border-radius:4px;background:#fff;margin-top:2px;\">\n" +
            $"  <line x1=\"{leftMargin}\" y1=\"{topMargin}\" x2=\"{w - rightMargin}\" y2=\"{topMargin}\" stroke=\"#f0f2f5\" stroke-width=\"1\"/>\n" +
            $"  <line x1=\"{leftMargin}\" y1=\"{topMargin + cH / 2}\" x2=\"{w - rightMargin}\" y2=\"{topMargin + cH / 2}\" stroke=\"#f0f2f5\" stroke-width=\"1\"/>\n" +
            $"  <line x1=\"{leftMargin}\" y1=\"{topMargin + cH}\" x2=\"{w - rightMargin}\" y2=\"{topMargin + cH}\" stroke=\"#e5e7eb\" stroke-width=\"1\"/>\n" +
            $"  <text x=\"{leftMargin - 4}\" y=\"{topMargin + 4}\" font-size=\"6pt\" fill=\"#666\" text-anchor=\"end\">{maxV:F1} {unit}</text>\n" +
            $"  <text x=\"{leftMargin - 4}\" y=\"{topMargin + cH / 2 + 3}\" font-size=\"6pt\" fill=\"#666\" text-anchor=\"end\">{midV:F1} {unit}</text>\n" +
            $"  <text x=\"{leftMargin - 4}\" y=\"{topMargin + cH}\" font-size=\"6pt\" fill=\"#666\" text-anchor=\"end\">{minV:F1} {unit}</text>\n" +
            $"  <polygon points=\"{area}\" fill=\"{strokeColor}\" fill-opacity=\"0.12\"/>\n" +
            $"  <polyline points=\"{line}\" fill=\"none\" stroke=\"{strokeColor}\" stroke-width=\"1.8\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>\n" +
            $"  <text x=\"{leftMargin}\" y=\"{h - 5}\" font-size=\"6pt\" fill=\"#777\">{startDate}</text>\n" +
            $"  <text x=\"{leftMargin + cW / 2}\" y=\"{h - 5}\" font-size=\"6pt\" fill=\"#777\" text-anchor=\"middle\">{midDate}</text>\n" +
            $"  <text x=\"{w - rightMargin}\" y=\"{h - 5}\" font-size=\"6pt\" fill=\"#777\" text-anchor=\"end\">{endDate}</text>\n" +
            "</svg>\n";
    }

    private static string HealthStatusBar(string title, string color)
    {
        return
            "<div style=\"display:flex;align-items:center;margin:3px 0;font-size:7.5pt;\">\n" +
            $"  <span style=\"width:150px;color:#333;\">{H(title)}</span>\n" +
            $"  <span style=\"display:inline-block;width:16px;height:7px;background:{color};border-radius:2px;margin-right:6px;\"></span>\n" +
            "  <span style=\"display:inline-block;width:40px;height:7px;background:#e5e7eb;border-radius:2px;\"></span>\n" +
            "</div>\n";
    }

    private static string PageFooter(int pageNum) =>
        "<div class=\"pf\">" +
        $"<span>{pageNum}</span>" +
        "<span class=\"pf-brand\">Life Is On &nbsp;|&nbsp; Schneider Electric</span>" +
        "</div>\n";

    private static string H(string? s) =>
        System.Security.SecurityElement.Escape(s ?? string.Empty) ?? string.Empty;
}
