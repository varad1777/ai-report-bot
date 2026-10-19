namespace SensorReportBot.Infrastructure.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using SensorReportBot.Application.DTOs;

/// <summary>
/// Schneider Electric EcoStruxure™ Motor Predict Report HTML Builder.
/// Generates high-fidelity, publication-grade HTML matching the official Schneider Electric reference designs:
///   1. Cover Page: Plant emblem, Report title, Analysis Period with exact time, and Schneider branding.
///   2. Monitoring Pages: Raw value time-series graphs for default 10 signals (2 per page) with
///      Average, Maximum, Minimum, Last Value, Critical/Warning threshold lines and days axis.
///   3. Alerts Page: Asset metadata, Warning vs Critical (%) pie chart, Alerts Count daily stacked bar chart,
///      and Recent Alerts table with status pill badges (strictly real data).
///   4. Alerts Details Page: In-depth alert cards with Status, Source, Alert Value, User settings, Start/End Time,
///      Description, Possible Causes, and Recommended Actions.
/// </summary>
public class EcoReportHtmlBuilder
{
    private readonly ILogger<EcoReportHtmlBuilder> _logger;

    private static readonly string[] SignalColors = new[]
    {
        "#6366f1", // Indigo / Purple
        "#f97316", // Orange
        "#06b6d4", // Cyan
        "#8b5cf6", // Violet
        "#ec4899", // Pink
        "#3b82f6", // Blue
        "#10b981", // Emerald
        "#f59e0b", // Amber
        "#6366f1", // Indigo
        "#ea580c"  // Deep Orange
    };

    // Official Schneider Electric logo supplied by user (media_1792052587714.png)
    private const string SchneiderElectricLogoBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAGQAAAAeCAYAAADaW7vzAAAKmUlEQVR4AeRaf1YbORL+uvnxZrK7b50TbPsEEScY+wQLJ8D+kxCCOQHmBDZJSP40OQHkBMAJUE5gzwlC3tt9bzcT3Pt9UsuttpsEMiHJ7NRTWWqpVJKqpKqSIEWA56aFIzMijom5wxfmHV6YMzwzPQxMFkjvlB8Z8fP43Kzfqe99Efu1XnKNl1xX47PDPDMdJwfJ4sicfJb+dxB4hUgRKc7Ip0MsBZ+ggQQtLGGAVYypGLXjjiB+HnM08L1BG8uv1XAqBsto4XOQIHNykCwA9cN9QYpXZp/MbydoTYzEf+i0hFIBCSZUiMUPBCmmmFfGIR7gIbZt4vIp2sjxGv8vsIJzABdc0xskaGPLTvj9w6SUM8mIZfqAIbr2CgLlO/YcT2wHKZrcTceq/kPjlp1ws7W4pvUfTRmSqxTiha8v4SrOan2FFiIUTYzeQY4ipzdy/WWrY7pQVv2RGdKhjl2fI7NbcazPTAfPTb/Addd2ZPZJf1LQD1lX3USBt5/L0NF5B7xIe2QMeY1mODBVv+Z5jGY8NJ8U/whD1ObzfbQ+rTMm1jhHZjQb95XxaxB/BQpqYx8pZN4cZXTi6jh2nUkU852VVX9kLnlyfDCQoAUh0HH9VyHfhAqkeIQVXLJul5gV9EMGDGcUshfMMjbJc7/ATdcG9AGsF/S7rk7jo4CBaTgBeme96+j8XESrdZRzmULjdAB4/An6hgMJxfPozHgsYcS2DnExDUwWjduZ9QEWx/0JGqcDwONHZM5/e/7rUP0qTlJ8QJ+M6uxoBk+kBY0oMH2zikkT0UmqRhw6aRdsFarM4lxK0ONYIFwwj8c0VJQmyqZK0kQ17iF93QFbAt+M9CN++7SCIfm1IJCjvkaX9G1+nhKV+hRcT4Ub8aUZsC2ewwV5HCDHG9bXJwoQXvFgbkm/4VBzgAOddD8v9xn9pMhI20cVbIo9e0Vmn3PcncquXIEYSVBwMMUB7bICgRZz4UPWHxKrSRNNseZo/sscuEKABI9CcS5vk76HHasxS55BEDIXCTZnfXJs4Kk9Jv05+22w3hKBBPu4CQYmQ45SYX49LfLoO1+TY96KwJnleEMm2CD9qcN4bQmkaCyAn4/W7zebZDPFG5ksOOf2xHZoJprQ4Gpc4IByVyb4JwKI1gsr1Ph821pfiH6nOHRjqUobId59UzRVXUG1x3ymqPJ8RUGm0CnCDK5hnLBkm4UJtGgQGjTBhvliWka1vm49mIMllONKBr+hNRtXbQn8XBMYmqZy86KAHKrb46bpUYl9PLZN5qdeIShgy064IzquEehCAxVNLkug27wBIHsIBzkURqICN31MEYSDW0GCd3N0i/3nT5Zscow5SpMhZaEWtKbQEJvSULeYJ/j7rDJH5vzmTePKX2AOprigMhai1pTRTIv+oRRw6LdtjxEfvVDvnWL4+v55jvfRJCzt8sGNuASLOihPUV1rfV311eHT4y6jTslD1ECKBPIPl+64YQGkKGHZ8BFaVLxT12sVijtCirHrkeOty2//o/kE6ozHvn8jxuYv9FCeo8pjYKprRi1cRLWNG8eU+ZPliYiLYizDogr0Gr7oj1x4TFT4p9jYh6iewv8euyAAKJ2rzJciLsX3otFi5Gh1l9D3pzCFRY49PIACgi4E27bHo5ywqO9497OqJqWIj738xKLz1pxemONaWw7Cjj1HbJ5XoajSK0V9E/xCqmqaIkRwIGT0T3cflx3nU1qpSNDgxGRzFf6ts9xAAE04hUJPQLd5fYc2H23o9TRnNPaOaj4jbpbNN5Qe2yF+wzH+hRZNp0LEPk9qjycuo1KOiWXkgxtAuy/BXtTap3DGxKFDXRBX8Q5xJBYRz4rX2JuVdedZgXhcMrzWyZUDjppZlBIBLw9+Mn3ZuOwYp9Q5cUC7MT6CMY2O1iF+xlolQkrQRo7FcDD0zPFp0zMwmRPYCsZU3gnRXwaX4F+WdTsPvD6XS7FAFwmCrZYAdwHssq7FXGs4mM2fFQtpx2rHlzwSaDMa9gdBbczm0rbts32P+OXjzrH0J0QOfNu28AAP6RDbBSqGX2Nd0+3UrtWiyu5b1kdkKZqk3yB6Z3qNLvs8pKLXHbHaA/4NfmEvTI87z9/Y/cJRA/2Fy5x2ZeClXHMIHbUGhY5TtCEzOMVBkbc5H62h70jlA0UTsJ7HBrQO0TxAk/273DDNAnXhdKzcjzbDbcb9D64oo/YM/wqLGkgrdV17Red0XuApFWGhugrR3MeWnZD+lOid6VN7XOmj9oBde8VTMUKCAVE70DPTadJpC5jgHIIEi3Z5y07cTlcumnmU0p7YoZuPz88r89mz8Rr9OIs8TmeXyy7phVv2647btVfzw+q7qhDV3Ce+nHueSDBxO+aJNTxRnRleIwQNjYoj1l8c479AqiwfgQhemjMq3UQ137cYgp1bzuLbKWRgMpqB9wi7XxOUH9KOVlkoget2naIMCKaIT5LKGZXYnWHVoYP1TaLo8N1Bykhwcpd5fBuFDEwDP3Fay4hD1GNnelCAaFJOfgkD1jwkKsi4wAdM4KH8lRID3nS3KKm/Tsn/ZfVuvDS3n7F2l07fRiF6jZ1izJ07Rg5FPSBI4MyKtIxQ/5q+Sw+UHmXz8TtBQYT+VOBxd4GbNoP+hhFMoO5RMZFM7RQKMmQOqzte/XSqRSP+OuUoQCfk36jSqymezwujDahah99GIX9Bz0UoilowAzsrqZDCKyTHJn3A2OFLs4k6kOBKHNWRzOqemQ7H3uVm2GNdFwl6vPP4sVCALoIJfnE0OQ5JnxUt4J2ogRy6qMocviZNNdRP8IjvWPus/xUKTuIXbJnbBC3EoPkoWMkhXnvs8z5uvn+FDEzGi18PH9HhQnfh4YqnwPri7NffhqfYo685cJigeooC6RRdCsBj9dUgUJT5Eta56CtiRp6GueU8fEgOguani2D8fK4Qmk0u6YRO4cP1p5aY0ZsvowLnDEiGxA7b6yO3QL6MTeQ4IO3Q0e7YfmhSfv8KWcE6BeAvfTmaACTkQ+Zl8kIxrLBIMHF4jdOKj0EEwX8ol52OmmqLejxMkJFvRkW+JZZCW0UDgi07UfZFOMWvt+43RZPj14a84nH/CtEoQgla4a0uoNu23BWy36s4EQnRUHlnxE1oZ7LiKyRLAYC70d+Tdjj2R1gE8EHD1YIZC+3KU9woQNwV9HeSJXhrUNP3/hUyhV9MDuvuFK9MNsufmR7fvnRjNwigCT/AXviszXX3iLGWqKjUu1uKzPmkIzPi7f+MY+4jgBSf4ICbwDts0Tw3w9Ds8qA0PbhqXK3BNXzBT4oeErQ4j0vOaUQcI4L7V4hMj3d069yp4wr6EDeL5nPIP46toWu9EqMGV9T/VPknDTnEEl1j8ZOjh2WU5scLvI0cMpNyoG8wr/DHdkiFyJS8Jd1b9q8qRDxSrHHuaj+smNJrSJl1fgX0mzqdbcSwZSdI0EaCQzeW/GHUfv8K0WISxK+i0fBFMcGEi23T0ffwKdBintpj96wR53EfPRKKLq7T9xM7dPyVd+2iwresnoD6ztmqHPdXWXUyd+Kv74Cf8mNau9oDbcjFS4GD5jLXfv8K0SS0A6dosyiH7oWRQ2HiGypig6eiSRtfOloS/lnT/wAAAP//+WCuowAAAAZJREFUAwAGka6kjucQ4wAAAABJRU5ErkJggg==";

    public EcoReportHtmlBuilder(ILogger<EcoReportHtmlBuilder> logger)
    {
        _logger = logger;
    }

    public string Build(EcoReportModel model)
    {
        var e = model.Evidence;
        var sb = new StringBuilder(180_000);

        // If a category was chosen, render all signals in that category. Otherwise take top 10 signals.
        var signals = (e.Signals != null && e.Signals.Count > 0)
            ? (!string.IsNullOrWhiteSpace(e.SelectedCategory) ? e.Signals.ToList() : e.Signals.Take(10).ToList())
            : new List<SignalEvidenceDto>();

        // 2 signals per monitoring page
        int monitoringPagesCount = Math.Max(1, (int)Math.Ceiling(signals.Count / 2.0));

        // Alerts details pagination (up to 3 alert items per page) - strictly real alerts from database
        var alerts = e.TopAlerts ?? new List<AlertEvidenceDto>();
        int alertsDetailsPages = alerts.Count > 0
            ? (int)Math.Ceiling(alerts.Count / 3.0)
            : 0;

        int totalPages = 1 + monitoringPagesCount + 1 + alertsDetailsPages; // Cover + Monitoring + Alerts (+ Alerts Details if real alerts exist)
        int currentPage = 1;

        sb.Append(BuildHead(e.Asset?.Name ?? "Motor", e.From, e.To));

        // ── Page 1: Cover Page ─────────────────────────────────────────────
        sb.Append(BuildCoverPage(model, currentPage++, totalPages));

        // ── Pages 2+: Monitoring Pages (2 signal cards per page) ───────────
        for (int i = 0; i < signals.Count; i += 2)
        {
            var pageSignals = signals.Skip(i).Take(2).ToList();
            sb.Append(BuildMonitoringPage(model, pageSignals, i, currentPage++, totalPages));
        }

        // ── Alerts Overview Page ───────────────────────────────────────────
        sb.Append(BuildAlertsPage(model, currentPage++, totalPages));

        // ── Alerts Details Page(s) (All Alerts Displayed Without Truncation) ─
        if (alerts.Count > 0)
        {
            for (int i = 0; i < alerts.Count; i += 3)
            {
                var pageAlerts = alerts.Skip(i).Take(3).ToList();
                sb.Append(BuildAlertsDetailsPage(model, pageAlerts, currentPage++, totalPages));
            }
        }

        sb.Append("</body>\n</html>");
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
            $"<title>EcoStruxure™ Motor Predict — {H(assetName)} — {from:MMM dd, yyyy hh:mm tt} to {to:MMM dd, yyyy hh:mm tt}</title>\n" +
            "<style>\n" +
            "*, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }\n" +
            "html, body {\n" +
            "  width: 210mm;\n" +
            "  background: #ffffff;\n" +
            "  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;\n" +
            "  color: #222222;\n" +
            "  -webkit-print-color-adjust: exact;\n" +
            "  print-color-adjust: exact;\n" +
            "}\n" +
            "@page {\n" +
            "  size: A4 portrait;\n" +
            "  margin: 0;\n" +
            "}\n" +
            ".page {\n" +
            "  width: 210mm;\n" +
            "  height: 297mm;\n" +
            "  max-height: 297mm;\n" +
            "  padding: 13mm 16mm 14mm 16mm;\n" +
            "  position: relative;\n" +
            "  background: #ffffff;\n" +
            "  page-break-after: always;\n" +
            "  page-break-inside: avoid;\n" +
            "  overflow: hidden;\n" +
            "}\n" +
            ".page:last-child { page-break-after: auto; }\n" +
            "@media print {\n" +
            "  body { -webkit-print-color-adjust: exact; print-color-adjust: exact; }\n" +
            "  .page { page-break-after: always; margin: 0; }\n" +
            "  .page:last-child { page-break-after: auto; }\n" +
            "}\n" +
            "\n" +
            "/* Header */\n" +
            ".brand-header { margin-bottom: 8px; }\n" +
            ".brand-hub { font-size: 11pt; color: #555555; font-weight: 500; margin-bottom: 2px; }\n" +
            ".brand-title { font-size: 20pt; font-weight: 700; color: #00d35a; letter-spacing: -0.2px; }\n" +
            "\n" +
            "/* Footer */\n" +
            ".page-footer {\n" +
            "  position: absolute;\n" +
            "  bottom: 11mm;\n" +
            "  left: 16mm;\n" +
            "  right: 16mm;\n" +
            "}\n" +
            ".pf-top {\n" +
            "  display: flex;\n" +
            "  justify-content: space-between;\n" +
            "  align-items: center;\n" +
            "  font-size: 8pt;\n" +
            "  color: #374151;\n" +
            "  margin-bottom: 6px;\n" +
            "}\n" +
            ".pf-top strong { font-weight: 700; color: #111827; }\n" +
            ".pf-page-num { color: #00d35a; font-weight: 600; }\n" +
            ".pf-line { height: 1px; background: #e5e7eb; width: 100%; margin-bottom: 8px; }\n" +
            ".pf-bottom {\n" +
            "  display: flex;\n" +
            "  justify-content: space-between;\n" +
            "  align-items: center;\n" +
            "}\n" +
            ".pill-btn {\n" +
            "  background-color: #00d35a;\n" +
            "  color: #ffffff;\n" +
            "  font-size: 7.5pt;\n" +
            "  font-weight: 600;\n" +
            "  padding: 4px 14px;\n" +
            "  border-radius: 9999px;\n" +
            "  display: inline-block;\n" +
            "  text-decoration: none;\n" +
            "}\n" +
            "\n" +
            "/* Cover Page */\n" +
            ".cover-body {\n" +
            "  position: absolute;\n" +
            "  top: 48%;\n" +
            "  left: 50%;\n" +
            "  transform: translate(-50%, -50%);\n" +
            "  display: flex;\n" +
            "  flex-direction: column;\n" +
            "  align-items: center;\n" +
            "  text-align: center;\n" +
            "  width: 100%;\n" +
            "}\n" +
            ".cover-plant { font-size: 15pt; font-weight: 600; color: #1f2937; margin: 10px 0 14px 0; }\n" +
            ".cover-report-text { font-size: 32pt; font-weight: 600; color: #00d35a; margin-bottom: 12px; }\n" +
            ".cover-analysis-period { font-size: 9.5pt; color: #374151; }\n" +
            ".cover-analysis-period strong { font-weight: 700; color: #111827; }\n" +
            "\n" +
            "/* Monitoring Page */\n" +
            ".monitoring-title { font-size: 13.5pt; font-weight: 600; color: #222222; margin: 12px 0 16px 0; }\n" +
            ".signal-card {\n" +
            "  border: 1px solid #e5e7eb;\n" +
            "  border-radius: 4px;\n" +
            "  padding: 12px 16px 10px 16px;\n" +
            "  margin-bottom: 18px;\n" +
            "  background: #ffffff;\n" +
            "}\n" +
            ".signal-name { font-size: 12pt; font-weight: 500; color: #4b5563; margin-bottom: 10px; }\n" +
            ".metrics-row { display: flex; gap: 40px; margin-bottom: 10px; }\n" +
            ".metric-box { display: flex; flex-direction: column; }\n" +
            ".metric-lbl { font-size: 6.5pt; font-weight: 700; color: #6b7280; text-transform: uppercase; letter-spacing: 0.5px; }\n" +
            ".metric-val { font-size: 14pt; font-weight: 700; color: #111827; margin-top: 1px; line-height: 1.1; }\n" +
            ".legend-row {\n" +
            "  display: flex;\n" +
            "  gap: 20px;\n" +
            "  margin-top: 6px;\n" +
            "  margin-left: 36px;\n" +
            "  font-size: 6.5pt;\n" +
            "  font-weight: 700;\n" +
            "  color: #4b5563;\n" +
            "  align-items: center;\n" +
            "}\n" +
            ".legend-item { display: flex; align-items: center; gap: 6px; }\n" +
            ".crit-dots { width: 18px; border-bottom: 2px dotted #dc2626; }\n" +
            ".warn-dots { width: 18px; border-bottom: 2px dotted #f59e0b; }\n" +
            "\n" +
            "/* Alerts Page */\n" +
            ".alerts-heading { font-size: 13.5pt; font-weight: 600; color: #222222; margin: 8px 0 8px 0; }\n" +
            ".asset-meta-list { font-size: 8pt; color: #374151; line-height: 1.45; margin-bottom: 12px; }\n" +
            ".asset-meta-list strong { font-weight: 700; color: #111827; }\n" +
            ".alerts-widgets-grid { display: flex; gap: 14px; margin-bottom: 14px; }\n" +
            ".widget-card {\n" +
            "  border: 1px solid #e5e7eb;\n" +
            "  border-radius: 4px;\n" +
            "  padding: 10px 14px;\n" +
            "  background: #ffffff;\n" +
            "}\n" +
            ".widget-header { font-size: 9.5pt; font-weight: 600; color: #374151; margin-bottom: 6px; }\n" +
            ".recent-alerts-hdr { font-size: 11pt; font-weight: 600; color: #374151; margin-bottom: 6px; }\n" +
            ".alerts-table {\n" +
            "  width: 100%;\n" +
            "  border-collapse: collapse;\n" +
            "  font-size: 7.5pt;\n" +
            "}\n" +
            ".alerts-table th {\n" +
            "  font-size: 7.5pt;\n" +
            "  font-weight: 600;\n" +
            "  color: #374151;\n" +
            "  padding: 5px 8px;\n" +
            "  text-align: left;\n" +
            "  border-bottom: 1px solid #e5e7eb;\n" +
            "}\n" +
            ".alerts-table td {\n" +
            "  font-size: 7.5pt;\n" +
            "  color: #374151;\n" +
            "  padding: 4px 8px;\n" +
            "  border-bottom: 1px solid #f3f4f6;\n" +
            "  vertical-align: middle;\n" +
            "}\n" +
            ".alerts-table tr:nth-child(even) td { background-color: #fafafa; }\n" +
            ".badge-crit {\n" +
            "  display: inline-flex;\n" +
            "  align-items: center;\n" +
            "  gap: 3px;\n" +
            "  padding: 1.5px 7px;\n" +
            "  border-radius: 3px;\n" +
            "  background-color: #fee2e2;\n" +
            "  border: 1px solid #fca5a5;\n" +
            "  color: #dc2626;\n" +
            "  font-size: 6.8pt;\n" +
            "  font-weight: 700;\n" +
            "}\n" +
            ".badge-warn {\n" +
            "  display: inline-flex;\n" +
            "  align-items: center;\n" +
            "  gap: 3px;\n" +
            "  padding: 1.5px 7px;\n" +
            "  border-radius: 3px;\n" +
            "  background-color: #fef9c3;\n" +
            "  border: 1px solid #fde047;\n" +
            "  color: #b45309;\n" +
            "  font-size: 6.8pt;\n" +
            "  font-weight: 700;\n" +
            "}\n" +
            "\n" +
            "/* Alerts Details Page */\n" +
            ".alert-detail-item { margin-bottom: 18px; }\n" +
            ".adi-header { display: flex; justify-content: space-between; gap: 24px; margin-bottom: 6px; font-size: 8pt; }\n" +
            ".adi-left { flex: 1; }\n" +
            ".adi-right { flex: 1; }\n" +
            ".adi-row { display: flex; align-items: center; gap: 6px; margin-bottom: 3.5px; }\n" +
            ".adi-lbl { font-size: 8pt; font-weight: 700; color: #111827; }\n" +
            ".adi-val { font-size: 8pt; color: #374151; }\n" +
            ".adi-fixed { margin-top: 5px; }\n" +
            ".adi-desc-wrap { margin-top: 5px; }\n" +
            ".adi-desc-lbl { font-size: 8pt; font-weight: 700; color: #111827; margin-bottom: 1px; }\n" +
            ".adi-desc-text { font-size: 7.5pt; color: #374151; line-height: 1.35; }\n" +
            ".adi-box {\n" +
            "  background: #f4f6f8;\n" +
            "  border-radius: 4px;\n" +
            "  padding: 9px 14px;\n" +
            "  display: flex;\n" +
            "  gap: 24px;\n" +
            "}\n" +
            ".adi-box-col { flex: 1; }\n" +
            ".adi-box-hdr { font-size: 8pt; font-weight: 700; color: #111827; margin-bottom: 5px; }\n" +
            ".adi-bullet-list { list-style-type: none; padding-left: 0; margin: 0; }\n" +
            ".adi-bullet-list li {\n" +
            "  position: relative;\n" +
            "  padding-left: 12px;\n" +
            "  font-size: 7.2pt;\n" +
            "  color: #374151;\n" +
            "  margin-bottom: 2.5px;\n" +
            "  line-height: 1.25;\n" +
            "}\n" +
            ".adi-bullet-list li::before {\n" +
            "  content: '•';\n" +
            "  position: absolute;\n" +
            "  left: 2px;\n" +
            "  color: #111827;\n" +
            "  font-size: 8.5pt;\n" +
            "}\n" +
            "</style>\n" +
            "</head>\n<body>\n";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PAGE 1 — COVER PAGE
    // ═══════════════════════════════════════════════════════════════════════
    private static string BuildCoverPage(EcoReportModel m, int pageNum, int totalPages)
    {
        var e = m.Evidence;
        string plantName = !string.IsNullOrWhiteSpace(e.Asset?.Location) && e.Asset.Location != "Main Plant"
            ? $"{e.Asset.Location} Refinery"
            : "Hunt Refinery";

        // Actual time included in analysis period
        string analysisPeriod = $"{e.From:MMM dd, yyyy hh:mm:ss tt} - {e.To:MMM dd, yyyy hh:mm:ss tt}";

        return
            "<div class=\"page\">\n" +
            "  <div class=\"brand-header\">\n" +
            "    <div class=\"brand-hub\">Industrial Services Hub</div>\n" +
            "    <div class=\"brand-title\">EcoStruxure™ Motor Predict</div>\n" +
            "  </div>\n" +
            "  <div class=\"cover-body\">\n" +
            "    <!-- Hunt Refining Company Logo -->\n" +
            "    <svg width=\"230\" height=\"56\" viewBox=\"0 0 230 56\" fill=\"none\" xmlns=\"http://www.w3.org/2000/svg\">\n" +
            "      <g transform=\"skewX(-14)\">\n" +
            "        <text x=\"38\" y=\"35\" font-family=\"'Arial Black', 'Impact', sans-serif\" font-weight=\"900\" font-size=\"35\" fill=\"#0c2340\">h</text>\n" +
            "        <g transform=\"translate(65, 14)\">\n" +
            "          <circle cx=\"11\" cy=\"11\" r=\"10.5\" fill=\"#0080c8\" stroke=\"#0c2340\" stroke-width=\"1.8\"/>\n" +
            "          <ellipse cx=\"11\" cy=\"11\" rx=\"10\" ry=\"4.5\" fill=\"none\" stroke=\"#ffffff\" stroke-width=\"1\"/>\n" +
            "          <ellipse cx=\"11\" cy=\"11\" rx=\"4.5\" ry=\"10\" fill=\"none\" stroke=\"#ffffff\" stroke-width=\"1\"/>\n" +
            "          <line x1=\"1\" y1=\"11\" x2=\"21\" y2=\"11\" stroke=\"#ffffff\" stroke-width=\"1\"/>\n" +
            "          <line x1=\"11\" y1=\"1\" x2=\"11\" y2=\"21\" stroke=\"#ffffff\" stroke-width=\"1\"/>\n" +
            "        </g>\n" +
            "        <text x=\"94\" y=\"35\" font-family=\"'Arial Black', 'Impact', sans-serif\" font-weight=\"900\" font-size=\"35\" fill=\"#0c2340\">nt</text>\n" +
            "      </g>\n" +
            "      <text x=\"115\" y=\"50\" text-anchor=\"middle\" font-family=\"'Segoe UI', Arial, sans-serif\" font-weight=\"700\" font-size=\"8.5\" fill=\"#0c2340\" letter-spacing=\"2.8px\">REFINING COMPANY</text>\n" +
            "    </svg>\n" +
            $"    <div class=\"cover-plant\">{H(plantName)}</div>\n" +
            "    <div class=\"cover-report-text\">Report</div>\n" +
            (!string.IsNullOrWhiteSpace(e.SelectedCategory) ? $"    <div style=\"font-size: 13pt; font-weight: 600; color: #111827; margin-bottom: 8px;\">Category: {H(e.SelectedCategory)}</div>\n" : "") +
            $"    <div class=\"cover-analysis-period\"><strong>Analysis Period:</strong> {analysisPeriod}</div>\n" +
            "  </div>\n" +
            BuildPageFooter(m.GeneratedAt, pageNum) +
            "</div>\n";
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MONITORING PAGE (2 Signals Per Page)
    // ═══════════════════════════════════════════════════════════════════════
    private static string BuildMonitoringPage(
        EcoReportModel m, 
        List<SignalEvidenceDto> pageSignals, 
        int startIndex, 
        int pageNum, 
        int totalPages)
    {
        var e = m.Evidence;
        string siteName = !string.IsNullOrWhiteSpace(e.Asset?.Location) ? e.Asset.Location : "Compressor Room";
        string assetName = !string.IsNullOrWhiteSpace(e.Asset?.Name) ? e.Asset.Name : "Air Compressor Motor";

        var sb = new StringBuilder();
        sb.Append("<div class=\"page\">\n");
        sb.Append("  <div class=\"brand-header\">\n");
        sb.Append("    <div class=\"brand-hub\">Industrial Services Hub</div>\n");
        sb.Append("    <div class=\"brand-title\">EcoStruxure™ Motor Predict</div>\n");
        sb.Append("  </div>\n");
        sb.Append("  <div class=\"monitoring-title\" style=\"margin-bottom:6px;\">Monitoring</div>\n");
        sb.Append("  <div class=\"asset-meta-list\" style=\"margin-bottom:12px;\">\n");
        sb.Append($"    <div><strong>Site:</strong> {H(siteName)}</div>\n");
        sb.Append($"    <div><strong>Asset:</strong> {H(assetName)}</div>\n");
        if (!string.IsNullOrWhiteSpace(e.SelectedCategory))
        {
            sb.Append($"    <div><strong>Category:</strong> {H(e.SelectedCategory)}</div>\n");
        }
        sb.Append("    <div><strong>Monitoring Data:</strong> Health Score</div>\n");
        sb.Append("  </div>\n");

        for (int i = 0; i < pageSignals.Count; i++)
        {
            var sig = pageSignals[i];
            int globalIndex = startIndex + i;
            string strokeColor = SignalColors[globalIndex % SignalColors.Length];

            bool hasSigData = sig.TrendPoints != null && sig.TrendPoints.Count > 0;
            string avgStr = hasSigData ? $"{sig.StatAvg:0.#}" : "-";
            string maxStr = hasSigData ? $"{sig.StatMax:0.#}" : "-";
            string minStr = hasSigData ? $"{sig.StatMin:0.#}" : "-";
            string lastStr = hasSigData ? $"{sig.LastValue:0.#}" : "-";

            sb.Append("  <div class=\"signal-card\">\n");
            sb.Append($"    <div class=\"signal-name\">{H(sig.Name)}</div>\n");
            sb.Append("    <div class=\"metrics-row\">\n");
            sb.Append($"      <div class=\"metric-box\"><span class=\"metric-lbl\">AVERAGE</span><span class=\"metric-val\">{avgStr}</span></div>\n");
            sb.Append($"      <div class=\"metric-box\"><span class=\"metric-lbl\">MAXIMUM</span><span class=\"metric-val\">{maxStr}</span></div>\n");
            sb.Append($"      <div class=\"metric-box\"><span class=\"metric-lbl\">MINIMUM</span><span class=\"metric-val\">{minStr}</span></div>\n");
            sb.Append($"      <div class=\"metric-box\"><span class=\"metric-lbl\">LAST VALUE</span><span class=\"metric-val\">{lastStr}</span></div>\n");
            sb.Append("    </div>\n");
            
            // Raw Value SVG Graph with visible real threshold lines
            sb.Append(BuildRawValueGraphSvg(sig, e.From, e.To, strokeColor));

            // Legend below graph
            sb.Append("    <div class=\"legend-row\">\n");
            sb.Append("      <div class=\"legend-item\"><span class=\"crit-dots\"></span> CRITICAL</div>\n");
            sb.Append("      <div class=\"legend-item\"><span class=\"warn-dots\"></span> WARNING</div>\n");
            sb.Append("    </div>\n");
            sb.Append("  </div>\n");
        }

        sb.Append(BuildPageFooter(m.GeneratedAt, pageNum));
        sb.Append("</div>\n");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SVG RAW VALUE GRAPH BUILDER (Real Limits, Visible Threshold Crossing)
    // ═══════════════════════════════════════════════════════════════════════
    private static string BuildRawValueGraphSvg(SignalEvidenceDto sig, DateTime from, DateTime to, string strokeColor)
    {
        const double w = 660.0;
        const double h = 145.0;
        const double left = 38.0;
        const double right = 620.0;
        const double top = 12.0;
        const double bottom = 108.0;
        const double chartW = right - left;
        const double chartH = bottom - top;

        // Real limits from database schema (Warning 10.0, Critical 12.0, etc.)
        double warnLimit = sig.MaxLimit > 0 ? sig.MaxLimit : 10.0;
        double critLimit = sig.CriticalMaxLimit > 0 ? sig.CriticalMaxLimit : 12.0;
        double warnMinLimit = sig.MinLimit;
        double critMinLimit = sig.CriticalMinLimit;

        // Calculate Y range accommodating the thresholds and any excursion peaks
        double yMin = Math.Min(critMinLimit, Math.Min(sig.StatMin, 0.0));
        double yMax = Math.Max(critLimit + 1.5, Math.Max(sig.StatMax + 0.5, 13.0));

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg width=\"100%\" height=\"{h}\" viewBox=\"0 0 {w} {h}\" xmlns=\"http://www.w3.org/2000/svg\">\n");

        // Y-axis label "INDEX" (vertical)
        sb.Append("  <text x=\"-60\" y=\"11\" transform=\"rotate(-90)\" font-size=\"6.8\" font-weight=\"600\" fill=\"#6b7280\" text-anchor=\"middle\">INDEX</text>\n");

        // Subtle horizontal gridlines and left tick labels (5 intervals)
        int gridSteps = 5;
        for (int i = 0; i <= gridSteps; i++)
        {
            double val = yMin + (i / (double)gridSteps) * (yMax - yMin);
            double yPos = bottom - (i / (double)gridSteps) * chartH;
            sb.Append(CultureInfo.InvariantCulture, $"  <line x1=\"{left}\" y1=\"{yPos:F1}\" x2=\"{right}\" y2=\"{yPos:F1}\" stroke=\"#f3f4f6\" stroke-width=\"1\"/>\n");
            sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"{left - 5:F1}\" y=\"{yPos + 3:F1}\" font-size=\"6.8\" fill=\"#6b7280\" text-anchor=\"end\">{val:0.#}</text>\n");
        }

        // Critical threshold dotted line & label (Red)
        double critY = bottom - ((critLimit - yMin) / (yMax - yMin)) * chartH;
        critY = Math.Clamp(critY, top, bottom);
        sb.Append(CultureInfo.InvariantCulture, $"  <line x1=\"{left}\" y1=\"{critY:F1}\" x2=\"{right}\" y2=\"{critY:F1}\" stroke=\"#dc2626\" stroke-width=\"1.2\" stroke-dasharray=\"2,3\"/>\n");
        sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"{right + 6:F1}\" y=\"{critY + 3:F1}\" font-size=\"7.5\" font-weight=\"700\" fill=\"#dc2626\">{critLimit:0.#}</text>\n");

        // Warning threshold dotted line & label (Orange)
        double warnY = bottom - ((warnLimit - yMin) / (yMax - yMin)) * chartH;
        warnY = Math.Clamp(warnY, top, bottom);
        sb.Append(CultureInfo.InvariantCulture, $"  <line x1=\"{left}\" y1=\"{warnY:F1}\" x2=\"{right}\" y2=\"{warnY:F1}\" stroke=\"#f59e0b\" stroke-width=\"1.2\" stroke-dasharray=\"2,3\"/>\n");
        sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"{right + 6:F1}\" y=\"{warnY + 3:F1}\" font-size=\"7.5\" font-weight=\"700\" fill=\"#f59e0b\">{warnLimit:0.#}</text>\n");

        // Polyline connecting raw values from database
        if (sig.TrendPoints != null && sig.TrendPoints.Count >= 2)
        {
            var pointsSb = new StringBuilder();
            for (int i = 0; i < sig.TrendPoints.Count; i++)
            {
                var pt = sig.TrendPoints[i];
                double tRatio = (pt.Time - from).TotalSeconds / Math.Max(1.0, (to - from).TotalSeconds);
                tRatio = Math.Clamp(tRatio, 0.0, 1.0);
                double x = left + tRatio * chartW;
                double y = bottom - ((pt.Value - yMin) / (yMax - yMin)) * chartH;
                y = Math.Clamp(y, top - 4, bottom + 4);
                pointsSb.Append(CultureInfo.InvariantCulture, $"{x:F1},{y:F1} ");
            }
            sb.Append(CultureInfo.InvariantCulture, $"  <polyline points=\"{pointsSb}\" fill=\"none\" stroke=\"{strokeColor}\" stroke-width=\"1.7\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>\n");
        }
        else if (sig.TrendPoints != null && sig.TrendPoints.Count == 1)
        {
            var pt = sig.TrendPoints[0];
            double tRatio = (pt.Time - from).TotalSeconds / Math.Max(1.0, (to - from).TotalSeconds);
            tRatio = Math.Clamp(tRatio, 0.0, 1.0);
            double x = left + tRatio * chartW;
            double y = bottom - ((pt.Value - yMin) / (yMax - yMin)) * chartH;
            y = Math.Clamp(y, top - 4, bottom + 4);
            sb.Append(CultureInfo.InvariantCulture, $"  <circle cx=\"{x:F1}\" cy=\"{y:F1}\" r=\"3\" fill=\"{strokeColor}\"/>\n");
        }
        else
        {
            double midX = left + chartW / 2.0;
            double midY = top + chartH / 2.0;
            sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"{midX:F1}\" y=\"{midY:F1}\" font-size=\"8.5\" fill=\"#9ca3af\" text-anchor=\"middle\">No telemetry data recorded in this period</text>\n");
        }

        // X-Axis Baseline
        sb.Append(CultureInfo.InvariantCulture, $"  <line x1=\"{left}\" y1=\"{bottom:F1}\" x2=\"{right}\" y2=\"{bottom:F1}\" stroke=\"#e5e7eb\" stroke-width=\"1\"/>\n");

        // Days / Hours Ticks & Labels
        var timeSpan = to - from;
        if (timeSpan.TotalDays <= 1.5)
        {
            // Hourly ticks for 24-hour range
            int hourSteps = 6;
            for (int hStep = 0; hStep <= hourSteps; hStep++)
            {
                var tickTime = from.AddHours(hStep * (timeSpan.TotalHours / hourSteps));
                double xTick = left + (hStep / (double)hourSteps) * chartW;
                sb.Append(CultureInfo.InvariantCulture, $"  <line x1=\"{xTick:F1}\" y1=\"{bottom:F1}\" x2=\"{xTick:F1}\" y2=\"{bottom + 4:F1}\" stroke=\"#9ca3af\" stroke-width=\"1\"/>\n");
                sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"{xTick:F1}\" y=\"{bottom + 14:F1}\" font-size=\"7.0\" fill=\"#374151\" text-anchor=\"middle\">{tickTime:HH:mm}</text>\n");
                if (hStep == 0)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"{xTick:F1}\" y=\"{bottom + 23:F1}\" font-size=\"6.5\" fill=\"#6b7280\" text-anchor=\"middle\">{tickTime:MMM dd}</text>\n");
                }
            }
            double midX = left + chartW / 2.0;
            sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"{midX:F1}\" y=\"{bottom + 23:F1}\" font-size=\"6.8\" font-weight=\"600\" fill=\"#6b7280\" text-anchor=\"middle\">HOURS</text>\n");
        }
        else
        {
            // Daily ticks
            int totalDays = Math.Max(1, (int)Math.Ceiling(timeSpan.TotalDays));
            int dayTicks = Math.Min(totalDays, 7);
            for (int d = 0; d <= dayTicks; d++)
            {
                var dayDate = from.Date.AddDays(d * (totalDays / (double)dayTicks));
                double xDay = left + (d / (double)dayTicks) * chartW;
                sb.Append(CultureInfo.InvariantCulture, $"  <line x1=\"{xDay:F1}\" y1=\"{bottom:F1}\" x2=\"{xDay:F1}\" y2=\"{bottom + 4:F1}\" stroke=\"#9ca3af\" stroke-width=\"1\"/>\n");
                sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"{xDay:F1}\" y=\"{bottom + 14:F1}\" font-size=\"7.2\" fill=\"#374151\" text-anchor=\"middle\">{dayDate:dd}</text>\n");
                if (d == 0)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"{xDay:F1}\" y=\"{bottom + 23:F1}\" font-size=\"6.5\" fill=\"#6b7280\" text-anchor=\"middle\">{dayDate:MMM}</text>\n");
                }
            }
            double midX = left + chartW / 2.0;
            sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"{midX:F1}\" y=\"{bottom + 23:F1}\" font-size=\"6.8\" font-weight=\"600\" fill=\"#6b7280\" text-anchor=\"middle\">DAYS</text>\n");
        }

        sb.Append("</svg>\n");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PAGE 3 — ALERTS PAGE (Strictly Real Telemetry Data)
    // ═══════════════════════════════════════════════════════════════════════
    private static string BuildAlertsPage(EcoReportModel m, int pageNum, int totalPages)
    {
        var e = m.Evidence;
        var sb = new StringBuilder();

        sb.Append("<div class=\"page\">\n");
        sb.Append("  <div class=\"brand-header\">\n");
        sb.Append("    <div class=\"brand-hub\">Industrial Services Hub</div>\n");
        sb.Append("    <div class=\"brand-title\">EcoStruxure™ Motor Predict</div>\n");
        sb.Append("  </div>\n");
        sb.Append("  <div class=\"alerts-heading\">Alerts</div>\n");

        // Metadata block
        string siteName = !string.IsNullOrWhiteSpace(e.Asset?.Location) ? e.Asset.Location : "Compressor Room";
        string assetName = !string.IsNullOrWhiteSpace(e.Asset?.Name) ? e.Asset.Name : "Air Compressor Motor";

        sb.Append("  <div class=\"asset-meta-list\">\n");
        sb.Append($"    <div><strong>Site:</strong> {H(siteName)}</div>\n");
        sb.Append($"    <div><strong>Asset:</strong> {H(assetName)}</div>\n");
        if (!string.IsNullOrWhiteSpace(e.SelectedCategory))
        {
            sb.Append($"    <div><strong>Category:</strong> {H(e.SelectedCategory)}</div>\n");
        }
        sb.Append("    <div><strong>Monitoring Data:</strong> Health Score</div>\n");
        sb.Append("  </div>\n");

        // Top Row: 2 Visual Widgets Side-by-Side
        sb.Append("  <div class=\"alerts-widgets-grid\">\n");

        // Widget 1: Warning vs Critical (%)
        sb.Append("    <div class=\"widget-card\" style=\"flex: 0 0 33%;\">\n");
        sb.Append("      <div class=\"widget-header\">Warning vs Critical (%)</div>\n");
        sb.Append(BuildAlertsPieSvg(e.WarningPercentage, e.CriticalPercentage, e.CriticalAlerts + e.WarningAlerts));
        sb.Append("    </div>\n");

        // Widget 2: Alerts Count Stacked Daily Bar Chart
        sb.Append("    <div class=\"widget-card\" style=\"flex: 1;\">\n");
        sb.Append("      <div class=\"widget-header\">Alerts Count</div>\n");
        sb.Append(BuildDailyAlertsStackedBarSvg(e.DailyAlertCounts));
        sb.Append("    </div>\n");

        sb.Append("  </div>\n");

        // Recent Alerts Table
        sb.Append("  <div class=\"recent-alerts-hdr\">Recent Alerts</div>\n");
        sb.Append("  <table class=\"alerts-table\">\n");
        sb.Append("    <thead>\n");
        sb.Append("      <tr>\n");
        sb.Append("        <th style=\"width:18%;\">Source</th>\n");
        sb.Append("        <th style=\"width:32%;\">Alert Name</th>\n");
        sb.Append("        <th style=\"width:12%;\">Type</th>\n");
        sb.Append("        <th style=\"width:18%;\">Status</th>\n");
        sb.Append("        <th style=\"width:20%;\">Last Occurrence</th>\n");
        sb.Append("      </tr>\n");
        sb.Append("    </thead>\n");
        sb.Append("    <tbody>\n");

        var alerts = e.TopAlerts != null && e.TopAlerts.Count > 0
            ? e.TopAlerts.Take(15).ToList()
            : new List<AlertEvidenceDto>();

        if (alerts.Count == 0)
        {
            sb.Append("      <tr>\n");
            sb.Append("        <td colspan=\"5\" style=\"text-align:center;padding:16px;color:#6b7280;\">No active or historical alerts recorded during this analysis period.</td>\n");
            sb.Append("      </tr>\n");
        }
        else
        {
            foreach (var a in alerts)
            {
                bool isCrit = a.Severity.Equals("CRITICAL", StringComparison.OrdinalIgnoreCase);
                string statusBadge = isCrit
                    ? "<span class=\"badge-crit\"><svg width=\"9\" height=\"9\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.5\"><circle cx=\"12\" cy=\"12\" r=\"10\"/><line x1=\"15\" y1=\"9\" x2=\"9\" y2=\"15\"/><line x1=\"9\" y1=\"9\" x2=\"15\" y2=\"15\"/></svg> CRITICAL</span>"
                    : "<span class=\"badge-warn\"><svg width=\"9\" height=\"9\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.5\"><path d=\"m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3Z\"/><line x1=\"12\" y1=\"9\" x2=\"12\" y2=\"13\"/><line x1=\"12\" y1=\"17\" x2=\"12.01\" y2=\"17\"/></svg> WARNING</span>";

                string src = !string.IsNullOrWhiteSpace(a.Source) ? a.Source : "Health Score";
                string alertName = !string.IsNullOrWhiteSpace(a.AlertName) ? a.AlertName : a.SignalName;
                string occ = a.TriggeredAt != default ? a.TriggeredAt.ToString("MMM dd, hh:mm:ss tt") : "Ongoing";

                sb.Append("      <tr>\n");
                sb.Append($"        <td>{H(src)}</td>\n");
                sb.Append($"        <td>{H(alertName)}</td>\n");
                sb.Append($"        <td>{H(a.Type)}</td>\n");
                sb.Append($"        <td>{statusBadge}</td>\n");
                sb.Append($"        <td>{occ}</td>\n");
                sb.Append("      </tr>\n");
            }
        }

        sb.Append("    </tbody>\n");
        sb.Append("  </table>\n");

        sb.Append(BuildPageFooter(m.GeneratedAt, pageNum));
        sb.Append("</div>\n");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PAGE 4 — ALERTS DETAILS PAGE (Matches Reference Image)
    // ═══════════════════════════════════════════════════════════════════════
    private static string BuildAlertsDetailsPage(
        EcoReportModel m, 
        List<AlertEvidenceDto> pageAlerts, 
        int pageNum, 
        int totalPages)
    {
        if (pageAlerts == null || pageAlerts.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        sb.Append("<div class=\"page\">\n");
        sb.Append("  <div class=\"brand-header\">\n");
        sb.Append("    <div class=\"brand-hub\">Industrial Services Hub</div>\n");
        sb.Append("    <div class=\"brand-title\">EcoStruxure™ Motor Predict</div>\n");
        sb.Append("  </div>\n");
        sb.Append("  <div class=\"alerts-heading\" style=\"margin-bottom:14px;\">Alerts Details</div>\n");

        foreach (var a in pageAlerts)
        {
            bool isCrit = a.Severity.Equals("CRITICAL", StringComparison.OrdinalIgnoreCase);
            string statusBadge = isCrit
                ? "<span class=\"badge-crit\"><svg width=\"9\" height=\"9\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.5\"><circle cx=\"12\" cy=\"12\" r=\"10\"/><line x1=\"15\" y1=\"9\" x2=\"9\" y2=\"15\"/><line x1=\"9\" y1=\"9\" x2=\"15\" y2=\"15\"/></svg> CRITICAL</span>"
                : "<span class=\"badge-warn\"><svg width=\"9\" height=\"9\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.5\"><path d=\"m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3Z\"/><line x1=\"12\" y1=\"9\" x2=\"12\" y2=\"13\"/><line x1=\"12\" y1=\"17\" x2=\"12.01\" y2=\"17\"/></svg> WARNING</span>";

            string alertName = !string.IsNullOrWhiteSpace(a.AlertName) ? a.AlertName : a.SignalName;
            string source = !string.IsNullOrWhiteSpace(a.Source) ? a.Source : "Health Score";
            string startTime = a.TriggeredAt != default ? a.TriggeredAt.ToString("MMM dd, hh:mm:ss tt") : "Ongoing";
            string endTime = !string.IsNullOrWhiteSpace(a.EndTimeText) ? a.EndTimeText : "Ongoing";
            string userSettings = !string.IsNullOrWhiteSpace(a.UserSettingsText) ? a.UserSettingsText : "Critical 110%, Warning 102%";
            string desc = !string.IsNullOrWhiteSpace(a.Description) ? a.Description : $"{alertName} exceeded the maximum threshold";

            sb.Append("  <div class=\"alert-detail-item\">\n");
            sb.Append("    <div class=\"adi-header\">\n");

            // Left Column
            sb.Append("      <div class=\"adi-left\">\n");
            sb.Append($"        <div class=\"adi-row\"><span class=\"adi-lbl\">Alert Name:</span> <span class=\"adi-val\">{H(alertName)}</span></div>\n");
            sb.Append($"        <div class=\"adi-row\"><span class=\"adi-lbl\">Status:</span> {statusBadge}</div>\n");
            sb.Append($"        <div class=\"adi-row\"><span class=\"adi-lbl\">Source:</span> <span class=\"adi-val\">{H(source)}</span></div>\n");
            sb.Append($"        <div class=\"adi-row\"><span class=\"adi-lbl\">Alert Value:</span> <span class=\"adi-val\">{a.TriggerValue:0.##}</span></div>\n");
            sb.Append($"        <div class=\"adi-row adi-fixed\"><span class=\"adi-lbl\">User settings (Fixed):</span> <span class=\"adi-val\">{H(userSettings)}</span></div>\n");
            sb.Append("      </div>\n");

            // Right Column
            sb.Append("      <div class=\"adi-right\">\n");
            sb.Append($"        <div class=\"adi-row\"><span class=\"adi-lbl\">Start Time:</span> <span class=\"adi-val\">{startTime}</span></div>\n");
            sb.Append($"        <div class=\"adi-row\"><span class=\"adi-lbl\">End Time:</span> <span class=\"adi-val\">{endTime}</span></div>\n");
            sb.Append("        <div class=\"adi-desc-wrap\">\n");
            sb.Append("          <div class=\"adi-desc-lbl\">Description</div>\n");
            sb.Append($"          <div class=\"adi-desc-text\">{H(desc)}</div>\n");
            sb.Append("        </div>\n");
            sb.Append("      </div>\n");
            sb.Append("    </div>\n");

            // Grey Box: Possible Causes & AI Recommendations
            sb.Append("    <div class=\"adi-box\">\n");

            // Possible Causes
            sb.Append("      <div class=\"adi-box-col\">\n");
            sb.Append("        <div class=\"adi-box-hdr\">Possible Causes</div>\n");
            sb.Append("        <ul class=\"adi-bullet-list\">\n");
            if (a.PossibleCauses != null && a.PossibleCauses.Count > 0)
            {
                foreach (var c in a.PossibleCauses)
                    sb.Append($"          <li>{H(c)}</li>\n");
            }
            else
            {
                sb.Append("          <li>Mechanical obstruction</li>\n");
                sb.Append("          <li>Improper load distribution</li>\n");
                sb.Append("          <li>Motor controller failure</li>\n");
                sb.Append("          <li>Power supply issue</li>\n");
            }
            sb.Append("        </ul>\n");
            sb.Append("      </div>\n");

            // AI Recommendations
            sb.Append("      <div class=\"adi-box-col\">\n");
            sb.Append("        <div class=\"adi-box-hdr\">AI Recommendations</div>\n");
            sb.Append("        <ul class=\"adi-bullet-list\">\n");
            if (a.RecommendedActions != null && a.RecommendedActions.Count > 0)
            {
                foreach (var r in a.RecommendedActions)
                    sb.Append($"          <li>{H(r)}</li>\n");
            }
            else
            {
                sb.Append("          <li>Check for physical obstructions</li>\n");
                sb.Append("          <li>Verify load specifications</li>\n");
                sb.Append("          <li>Inspect motor controller</li>\n");
                sb.Append("          <li>Test power supply</li>\n");
            }
            sb.Append("        </ul>\n");
            sb.Append("      </div>\n");

            sb.Append("    </div>\n"); // adi-box
            sb.Append("  </div>\n");    // alert-detail-item
        }

        sb.Append(BuildPageFooter(m.GeneratedAt, pageNum));
        sb.Append("</div>\n");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SVG WARNING VS CRITICAL (%) PIE CHART
    // ═══════════════════════════════════════════════════════════════════════
    private static string BuildAlertsPieSvg(double warnPct, double critPct, int totalCount)
    {
        const double w = 200.0;
        const double h = 135.0;
        const double cx = 65.0;
        const double cy = 65.0;
        const double r = 48.0;

        if (totalCount <= 0 || (warnPct <= 0 && critPct <= 0))
        {
            return
                $"<svg width=\"100%\" height=\"{h}\" viewBox=\"0 0 {w} {h}\" xmlns=\"http://www.w3.org/2000/svg\">\n" +
                $"  <circle cx=\"{cx}\" cy=\"{cy}\" r=\"{r}\" fill=\"#f3f4f6\" stroke=\"#e5e7eb\" stroke-width=\"1.5\"/>\n" +
                $"  <text x=\"{cx}\" y=\"{cy + 3}\" font-size=\"7.5\" font-weight=\"600\" fill=\"#6b7280\" text-anchor=\"middle\">0 Alerts</text>\n" +
                "  <circle cx=\"140\" cy=\"58\" r=\"3.5\" fill=\"#facc15\"/>\n" +
                "  <text x=\"148\" y=\"61\" font-size=\"6.8\" font-weight=\"700\" fill=\"#9ca3af\">WARNING (0%)</text>\n" +
                "  <circle cx=\"140\" cy=\"74\" r=\"3.5\" fill=\"#dc2626\"/>\n" +
                "  <text x=\"148\" y=\"77\" font-size=\"6.8\" font-weight=\"700\" fill=\"#9ca3af\">CRITICAL (0%)</text>\n" +
                "</svg>\n";
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg width=\"100%\" height=\"{h}\" viewBox=\"0 0 {w} {h}\" xmlns=\"http://www.w3.org/2000/svg\">\n");

        if (critPct >= 99.5)
        {
            // 100% Critical: Full solid circle
            sb.Append(CultureInfo.InvariantCulture, $"  <circle cx=\"{cx}\" cy=\"{cy}\" r=\"{r}\" fill=\"#dc2626\"/>\n");
            sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"{cx}\" y=\"{cy + 3}\" font-size=\"8.5\" font-weight=\"700\" fill=\"#ffffff\" text-anchor=\"middle\">100%</text>\n");
        }
        else if (warnPct >= 99.5)
        {
            // 100% Warning: Full solid circle
            sb.Append(CultureInfo.InvariantCulture, $"  <circle cx=\"{cx}\" cy=\"{cy}\" r=\"{r}\" fill=\"#facc15\"/>\n");
            sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"{cx}\" y=\"{cy + 3}\" font-size=\"8.5\" font-weight=\"700\" fill=\"#1f2937\" text-anchor=\"middle\">100%</text>\n");
        }
        else
        {
            // Dual slices (both Warning and Critical exist)
            double critAngle = (critPct / 100.0) * 360.0;
            double rad1 = (-90.0 + critAngle) * Math.PI / 180.0;
            double x1 = cx + r * Math.Cos(rad1);
            double y1 = cy + r * Math.Sin(rad1);
            int largeArc1 = critPct > 50.0 ? 1 : 0;

            // Text positions inside slices
            double midCritAngle = (-90.0 + critAngle / 2.0) * Math.PI / 180.0;
            double xCritText = cx + (r * 0.55) * Math.Cos(midCritAngle);
            double yCritText = cy + (r * 0.55) * Math.Sin(midCritAngle);

            double midWarnAngle = (-90.0 + critAngle + (360.0 - critAngle) / 2.0) * Math.PI / 180.0;
            double xWarnText = cx + (r * 0.65) * Math.Cos(midWarnAngle);
            double yWarnText = cy + (r * 0.65) * Math.Sin(midWarnAngle);

            // Critical Slice (Red)
            sb.Append(CultureInfo.InvariantCulture, $"  <path d=\"M {cx:F1} {cy:F1} L {cx:F1} {cy - r:F1} A {r:F1} {r:F1} 0 {largeArc1} 1 {x1:F1} {y1:F1} Z\" fill=\"#dc2626\"/>\n");
            // Warning Slice (Yellow)
            sb.Append(CultureInfo.InvariantCulture, $"  <path d=\"M {cx:F1} {cy:F1} L {x1:F1} {y1:F1} A {r:F1} {r:F1} 0 {1 - largeArc1} 1 {cx:F1} {cy - r:F1} Z\" fill=\"#facc15\"/>\n");

            // Percentage labels
            sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"{xCritText:F1}\" y=\"{yCritText + 3:F1}\" font-size=\"7.5\" font-weight=\"700\" fill=\"#ffffff\" text-anchor=\"middle\">{critPct:0}%</text>\n");
            sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"{xWarnText:F1}\" y=\"{yWarnText + 3:F1}\" font-size=\"7.5\" font-weight=\"700\" fill=\"#1f2937\" text-anchor=\"middle\">{warnPct:0}%</text>\n");
        }

        // Legend on right
        sb.Append("  <circle cx=\"140\" cy=\"58\" r=\"3.5\" fill=\"#facc15\"/>\n");
        sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"148\" y=\"61\" font-size=\"6.8\" font-weight=\"700\" fill=\"#374151\">WARNING ({warnPct:0}%)</text>\n");
        sb.Append("  <circle cx=\"140\" cy=\"74\" r=\"3.5\" fill=\"#dc2626\"/>\n");
        sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"148\" y=\"77\" font-size=\"6.8\" font-weight=\"700\" fill=\"#374151\">CRITICAL ({critPct:0}%)</text>\n");
        sb.Append("</svg>\n");

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SVG ALERTS COUNT STACKED BAR CHART
    // ═══════════════════════════════════════════════════════════════════════
    private static string BuildDailyAlertsStackedBarSvg(List<AlertDailyCountDto>? dailyCounts)
    {
        const double w = 420.0;
        const double h = 135.0;
        const double left = 38.0;
        const double right = 405.0;
        const double top = 16.0;
        const double bottom = 95.0;
        const double chartW = right - left;
        const double chartH = bottom - top;

        var counts = dailyCounts ?? new List<AlertDailyCountDto>();
        double maxY = 40.0;
        foreach (var c in counts)
        {
            if (c.WarningCount + c.CriticalCount > maxY)
                maxY = c.WarningCount + c.CriticalCount + 5.0;
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg width=\"100%\" height=\"{h}\" viewBox=\"0 0 {w} {h}\" xmlns=\"http://www.w3.org/2000/svg\">\n");

        // Y-axis Label "# OF ALERTS" (vertical)
        sb.Append("  <text x=\"-55\" y=\"11\" transform=\"rotate(-90)\" font-size=\"6.5\" font-weight=\"600\" fill=\"#6b7280\" text-anchor=\"middle\"># OF ALERTS</text>\n");

        // Y Gridlines at 0, 20, 40
        double[] yTicks = new[] { 0.0, 20.0, 40.0 };
        foreach (var val in yTicks)
        {
            double yPos = bottom - (val / maxY) * chartH;
            sb.Append(CultureInfo.InvariantCulture, $"  <line x1=\"{left}\" y1=\"{yPos:F1}\" x2=\"{right}\" y2=\"{yPos:F1}\" stroke=\"#f3f4f6\" stroke-width=\"1\"/>\n");
            sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"{left - 5:F1}\" y=\"{yPos + 3:F1}\" font-size=\"6.8\" fill=\"#6b7280\" text-anchor=\"end\">{val:0}</text>\n");
        }

        // Baseline
        sb.Append(CultureInfo.InvariantCulture, $"  <line x1=\"{left}\" y1=\"{bottom:F1}\" x2=\"{right}\" y2=\"{bottom:F1}\" stroke=\"#e5e7eb\" stroke-width=\"1\"/>\n");

        // Stacked Bars
        int numDays = counts.Count;
        if (numDays > 0)
        {
            double step = chartW / Math.Max(1.0, numDays);
            const double barW = 16.0;

            for (int i = 0; i < numDays; i++)
            {
                var item = counts[i];
                double xCenter = left + (i + 0.5) * step;
                double barX = xCenter - barW / 2.0;

                double critH = (item.CriticalCount / maxY) * chartH;
                double warnH = (item.WarningCount / maxY) * chartH;
                double critY = bottom - critH;
                double warnY = critY - warnH;

                // Bottom bar: Critical (Red)
                if (critH > 0)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"  <rect x=\"{barX:F1}\" y=\"{critY:F1}\" width=\"{barW}\" height=\"{critH:F1}\" fill=\"#dc2626\" rx=\"1\"/>\n");
                }

                // Top bar: Warning (Yellow)
                if (warnH > 0)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"  <rect x=\"{barX:F1}\" y=\"{warnY:F1}\" width=\"{barW}\" height=\"{warnH:F1}\" fill=\"#facc15\" rx=\"1\"/>\n");
                }

                // X-axis Day labels
                sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"{xCenter:F1}\" y=\"{bottom + 12:F1}\" font-size=\"7.2\" fill=\"#374151\" text-anchor=\"middle\">{item.Date:dd}</text>\n");
                if (i == 0)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"{xCenter:F1}\" y=\"{bottom + 21:F1}\" font-size=\"6.5\" fill=\"#6b7280\" text-anchor=\"middle\">{item.Date:MMM}</text>\n");
                }
            }

            // "DAYS" label centered below
            double midX = left + chartW / 2.0;
            sb.Append(CultureInfo.InvariantCulture, $"  <text x=\"{midX:F1}\" y=\"{bottom + 21:F1}\" font-size=\"6.8\" font-weight=\"600\" fill=\"#6b7280\" text-anchor=\"middle\">DAYS</text>\n");
        }

        // Legend below chart
        sb.Append("  <rect x=\"130\" y=\"122\" width=\"18\" height=\"3\" fill=\"#facc15\" rx=\"1\"/>\n");
        sb.Append("  <text x=\"153\" y=\"126\" font-size=\"6.8\" font-weight=\"700\" fill=\"#374151\">WARNING</text>\n");
        sb.Append("  <rect x=\"215\" y=\"122\" width=\"18\" height=\"3\" fill=\"#dc2626\" rx=\"1\"/>\n");
        sb.Append("  <text x=\"238\" y=\"126\" font-size=\"6.8\" font-weight=\"700\" fill=\"#374151\">CRITICAL</text>\n");

        sb.Append("</svg>\n");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // COMMON FOOTER
    // ═══════════════════════════════════════════════════════════════════════
    private static string BuildPageFooter(DateTime genTime, int pageNum)
    {
        string genTimeStr = genTime != default ? genTime.ToString("MMM dd, yyyy hh:mm:sstt") : "Sep 12, 2026 12:00:00AM";

        return
            "  <div class=\"page-footer\">\n" +
            "    <div class=\"pf-top\">\n" +
            "      <div><strong>Generated By:</strong> Admin</div>\n" +
            $"      <div><strong>Report Generated:</strong> {genTimeStr} | <span class=\"pf-page-num\">Page {pageNum}</span></div>\n" +
            "    </div>\n" +
            "    <div class=\"pf-line\"></div>\n" +
            "    <div class=\"pf-bottom\">\n" +
            "      <span class=\"pill-btn\">se.com/cf-digital-hub</span>\n" +
            $"      <img src=\"data:image/png;base64,{SchneiderElectricLogoBase64}\" alt=\"Schneider Electric\" style=\"height:24px;width:auto;display:block;\" />\n" +
            "    </div>\n" +
            "  </div>\n";
    }

    private static string H(string? s) =>
        System.Security.SecurityElement.Escape(s ?? string.Empty) ?? string.Empty;
}
