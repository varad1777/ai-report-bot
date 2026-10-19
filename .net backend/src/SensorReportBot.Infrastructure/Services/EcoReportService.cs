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
///   1. SQL evidence extraction  (TimescaleDB → ReportEvidenceDto)
///   2. HTML template render     (Evidence → HTML string)
///   3. Chromium PDF conversion  (HTML → PDF bytes)
/// </summary>
public class EcoReportService : IPdfReportService
{
    private readonly IReportEvidenceService _evidenceService;
    private readonly EcoReportHtmlBuilder _htmlBuilder;
    private readonly ILogger<EcoReportService> _logger;

    public EcoReportService(
        IReportEvidenceService evidenceService,
        EcoReportHtmlBuilder htmlBuilder,
        ILogger<EcoReportService> logger)
    {
        _evidenceService  = evidenceService;
        _htmlBuilder      = htmlBuilder;
        _logger           = logger;
    }

    /// <summary>
    /// Full pipeline: TimescaleDB → HTML → PDF bytes.
    /// Called by PdfGenerationBackgroundService via IPdfReportService directly with asset parameters.
    /// </summary>
    public async Task<byte[]> GeneratePdfAsync(
        int assetId, 
        DateTime from, 
        DateTime to, 
        string? category = null,
        Func<int, string, Task>? onProgress = null, 
        CancellationToken ct = default)
    {
        var result = await GenerateReportResultAsync(assetId, from, to, category, onProgress, ct);
        return result.PdfBytes;
    }

    public async Task<PdfGenerationResult> GenerateReportResultAsync(
        int assetId, 
        DateTime from, 
        DateTime to, 
        string? category = null,
        Func<int, string, Task>? onProgress = null, 
        CancellationToken ct = default)
    {
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("EcoReportService.GenerateReportResultAsync started for AssetId={AssetId}, Category={Category}", assetId, category);

        // ── Phase 1: SQL Evidence extraction ──────────────────────────────
        if (onProgress != null)
        {
            await onProgress(25, "Extracting telemetry evidence and domain aggregates from TimescaleDB...");
        }

        var dbSw = System.Diagnostics.Stopwatch.StartNew();
        var evidence = await _evidenceService.BuildEvidenceAsync(
            assetId,
            from,
            to,
            category,
            ct);
        dbSw.Stop();
        long dbDurationMs = dbSw.ElapsedMilliseconds;
        _logger.LogInformation("Phase 1 (SQL Evidence) complete — {Ms}ms", dbDurationMs);

        // ── Phase 2: Build HTML string ────────────────────────────────────
        if (onProgress != null)
        {
            await onProgress(65, "Compiling EcoStruxure™ Motor Predict publication layout...");
        }

        var renderSw = System.Diagnostics.Stopwatch.StartNew();
        string reportId = $"ECO-{DateTime.UtcNow:yyyyMMdd}-{assetId:D4}";
        var model = new EcoReportModel
        {
            Evidence    = evidence,
            GeneratedAt = DateTime.UtcNow,
            ReportId    = reportId
        };

        string html = _htmlBuilder.Build(model);
        _logger.LogInformation("Phase 2 (HTML Template) complete — HTML chars={C}", html.Length);

        // ── Phase 3: HTML → PDF via Chromium/Playwright ───────────────────
        if (onProgress != null)
        {
            await onProgress(85, "Rendering publication-grade PDF via headless Chromium...");
        }

        byte[] pdfBytes = await ConvertHtmlToPdfAsync(html, ct);
        renderSw.Stop();
        long renderDurationMs = renderSw.ElapsedMilliseconds;
        totalSw.Stop();
        long totalDurationMs = totalSw.ElapsedMilliseconds;

        _logger.LogInformation(
            "EcoReportService complete — DbMs={DbMs}, RenderMs={RenderMs}, TotalMs={TotalMs}, PDFBytes={B}",
            dbDurationMs, renderDurationMs, totalDurationMs, pdfBytes.Length);

        if (onProgress != null)
        {
            await onProgress(100, "Finalizing report file...");
        }

        return new PdfGenerationResult
        {
            PdfBytes = pdfBytes,
            Html = html,
            DbDurationMs = dbDurationMs,
            RenderDurationMs = renderDurationMs,
            TotalDurationMs = totalDurationMs
        };
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
            string? customExecutablePath = Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH");
            var launchOptions = new Microsoft.Playwright.BrowserTypeLaunchOptions
            {
                Headless = true,
                ExecutablePath = string.IsNullOrWhiteSpace(customExecutablePath) ? null : customExecutablePath,
                Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage" }
            };

            try
            {
                browser = await playwright.Chromium.LaunchAsync(launchOptions);
            }
            catch (Microsoft.Playwright.PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist") || ex.Message.Contains("playwright.ps1 install"))
            {
                Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
                browser = await playwright.Chromium.LaunchAsync(launchOptions);
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
