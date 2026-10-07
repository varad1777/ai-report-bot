namespace SensorReportBot.Infrastructure.Services;

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SensorReportBot.Application.DTOs;
using SensorReportBot.Application.Interfaces;

/// <summary>
/// Orchestrates the full EcoStruxure report pipeline:
///
///   1. SQL evidence extraction  (TimescaleDB → ReportEvidenceDto)
///   2. LLM narrative generation (Evidence → ReportNarrativeDto)       [parallel with step 3]
///   3. HTML template render     (Evidence + Narrative → HTML string)
///   4. Chromium PDF conversion  (HTML → PDF bytes)
///
/// Steps 1 and 2 run sequentially (LLM requires evidence first).
/// Steps 3 and 4 are fast (< 400ms combined).
///
/// Replaces QuestPdfReportService for the EcoStruxure report format.
/// The existing QuestPdfReportService remains available for the raw telemetry report.
/// </summary>
public class EcoReportService : IPdfReportService
{
    private readonly IReportEvidenceService _evidenceService;
    private readonly IReportNarrativeService _narrativeService;
    private readonly EcoReportHtmlBuilder _htmlBuilder;
    private readonly ILogger<EcoReportService> _logger;

    public EcoReportService(
        IReportEvidenceService evidenceService,
        IReportNarrativeService narrativeService,
        EcoReportHtmlBuilder htmlBuilder,
        ILogger<EcoReportService> logger)
    {
        _evidenceService  = evidenceService;
        _narrativeService = narrativeService;
        _htmlBuilder      = htmlBuilder;
        _logger           = logger;
    }

    /// <summary>
    /// Full pipeline: TimescaleDB → LLM → HTML → PDF bytes.
    /// Called by PdfGenerationBackgroundService via IPdfReportService.
    /// </summary>
    public async Task<byte[]> GeneratePdfAsync(ReportDataDto reportData, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("EcoReportService.GeneratePdfAsync started for AssetId={AssetId}", reportData.Asset.AssetId);

        // ── Phase 1: SQL Evidence extraction ──────────────────────────────
        var evidence = await _evidenceService.BuildEvidenceAsync(
            reportData.Asset.AssetId,
            reportData.From,
            reportData.To,
            ct);
        _logger.LogInformation("Phase 1 (SQL Evidence) complete — {Ms}ms", sw.ElapsedMilliseconds);

        // ── Phase 2: LLM narrative generation ─────────────────────────────
        var narrative = await _narrativeService.GenerateNarrativeAsync(evidence, ct);
        _logger.LogInformation("Phase 2 (LLM Narrative) complete — {Ms}ms", sw.ElapsedMilliseconds);

        // ── Phase 3: Build HTML string ────────────────────────────────────
        string reportId = $"ECO-{DateTime.UtcNow:yyyyMMdd}-{reportData.Asset.AssetId:D4}";
        var model = new EcoReportModel
        {
            Evidence    = evidence,
            Narrative   = narrative,
            GeneratedAt = DateTime.UtcNow,
            ReportId    = reportId
        };

        string html = _htmlBuilder.Build(model);
        _logger.LogInformation("Phase 3 (HTML Template) complete — {Ms}ms, HTML chars={C}", sw.ElapsedMilliseconds, html.Length);

        // ── Phase 4: HTML → PDF via Chromium/Playwright ───────────────────
        byte[] pdfBytes = await ConvertHtmlToPdfAsync(html, ct);
        _logger.LogInformation(
            "EcoReportService complete — TotalMs={Ms}, PDFBytes={B}",
            sw.ElapsedMilliseconds, pdfBytes.Length);

        return pdfBytes;
    }

    // ─────────────────────────────────────────────────────────────────────
    // HTML → PDF via Playwright for .NET
    // Uses a singleton headless Chromium browser instance for performance.
    // ─────────────────────────────────────────────────────────────────────
    private static async Task<byte[]> ConvertHtmlToPdfAsync(string html, CancellationToken ct)
    {
        // Write HTML to a temp file so Playwright can navigate to it via file://
        string tempFile = Path.Combine(Path.GetTempPath(), $"eco_report_{Guid.NewGuid():N}.html");
        try
        {
            await File.WriteAllTextAsync(tempFile, html, Encoding.UTF8, ct);

            using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            Microsoft.Playwright.IBrowser browser;
            try
            {
                browser = await playwright.Chromium.LaunchAsync(new()
                {
                    Headless = true,
                    Args     = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage" }
                });
            }
            catch (Microsoft.Playwright.PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist") || ex.Message.Contains("playwright.ps1 install"))
            {
                Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
                browser = await playwright.Chromium.LaunchAsync(new()
                {
                    Headless = true,
                    Args     = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage" }
                });
            }

            await using (browser)
            {
                var page = await browser.NewPageAsync();
                await page.GotoAsync(
                    $"file:///{tempFile.Replace('\\', '/')}",
                    new() { WaitUntil = Microsoft.Playwright.WaitUntilState.NetworkIdle });

                byte[] pdfBytes = await page.PdfAsync(new()
                {
                    Format          = "A4",
                    PrintBackground = true,
                    Margin          = new()
                    {
                        Top    = "0",
                        Bottom = "0",
                        Left   = "0",
                        Right  = "0"
                    }
                });

                return pdfBytes;
            }
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
