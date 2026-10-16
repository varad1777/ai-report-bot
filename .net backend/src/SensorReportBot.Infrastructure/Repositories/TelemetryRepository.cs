namespace SensorReportBot.Infrastructure.Repositories;

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using SensorReportBot.Application.DTOs;
using SensorReportBot.Application.Interfaces;

public class TelemetryRepository : ITelemetryRepository
{
    private readonly string _connectionString;
    private readonly ILogger<TelemetryRepository> _logger;

    public TelemetryRepository(IConfiguration configuration, ILogger<TelemetryRepository> logger)
    {
        _connectionString = configuration.GetConnectionString("TimescaleDb")
            ?? "Host=localhost;Port=5432;Database=iam_db;Username=iam_user;Password=iam_pass";
        _logger = logger;
    }

    private IDbConnection CreateConnection() => new NpgsqlConnection(_connectionString);

    public async Task<IReadOnlyList<AssetDto>> GetAssetsAsync(CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        const string sql = "SELECT asset_id AS AssetId, name AS Name, asset_type AS AssetType, location AS Location FROM assets ORDER BY asset_id;";
        var results = await conn.QueryAsync<AssetDto>(sql);
        return results.ToList();
    }

    public async Task<IReadOnlyList<SignalDto>> GetSignalsByAssetAsync(int assetId, CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        const string sql = @"
            SELECT signal_id AS SignalId, asset_id AS AssetId, name AS Name, unit AS Unit, 
                   min_value AS MinValue, max_value AS MaxValue 
            FROM signals 
            WHERE asset_id = @assetId 
            ORDER BY signal_id;";
        var results = await conn.QueryAsync<SignalDto>(sql, new { assetId });
        return results.ToList();
    }

    public async Task<ReportDataDto> GenerateReportDataAsync(ReportRequestDto request, CancellationToken ct = default)
    {
        using var conn = CreateConnection();

        // 1. Fetch Asset
        const string assetSql = "SELECT asset_id AS AssetId, name AS Name, asset_type AS AssetType, location AS Location FROM assets WHERE asset_id = @AssetId;";
        var asset = await conn.QueryFirstOrDefaultAsync<AssetDto>(assetSql, new { request.AssetId });
        if (asset == null)
        {
            throw new KeyNotFoundException($"Asset with ID {request.AssetId} not found.");
        }

        // 2. Fetch Signals (all if none specified)
        List<SignalDto> targetSignals;
        if (request.SignalIds != null && request.SignalIds.Count > 0)
        {
            const string sigSql = "SELECT signal_id AS SignalId, asset_id AS AssetId, name AS Name, unit AS Unit, min_value AS MinValue, max_value AS MaxValue FROM signals WHERE signal_id = ANY(@Ids);";
            var sigs = await conn.QueryAsync<SignalDto>(sigSql, new { Ids = request.SignalIds.ToArray() });
            targetSignals = sigs.ToList();
        }
        else
        {
            var sigs = await GetSignalsByAssetAsync(request.AssetId, ct);
            targetSignals = sigs.ToList();
        }

        var signalIds = targetSignals.Select(s => s.SignalId).ToArray();

        // Determine Time Range
        DateTime toTime = request.To ?? DateTime.UtcNow;
        DateTime fromTime = request.From ?? toTime.AddDays(-7);

        // Adjust if requested date is outside recorded dataset
        const string maxDateSql = "SELECT COALESCE(MAX(time), now()) FROM signal_data WHERE signal_id = ANY(@Ids);";
        var maxTimeRecorded = await conn.QueryFirstOrDefaultAsync<DateTime>(maxDateSql, new { Ids = signalIds });
        if (toTime > maxTimeRecorded && maxTimeRecorded != default)
        {
            var span = toTime - fromTime;
            toTime = maxTimeRecorded;
            fromTime = toTime - span;
        }

        var report = new ReportDataDto
        {
            Asset = asset,
            From = fromTime,
            To = toTime,
            GeneratedAt = DateTime.UtcNow,
            IncludeFullRawData = request.IncludeFullRawData
        };

        if (signalIds.Length == 0)
        {
            return report;
        }

        // 3. Raw Unaggregated Query: Fetch EVERY SINGLE ROW in the timeframe for all selected signals
        const string rawDataSql = @"
            SELECT 
                signal_id AS SignalId, 
                time AS Time, 
                ROUND(value::numeric, 2) AS Value
            FROM signal_data
            WHERE signal_id = ANY(@SignalIds) 
              AND time >= @FromTime 
              AND time <= @ToTime 
            ORDER BY signal_id, time ASC;";

        var rawRows = (await conn.QueryAsync<RawTelemetryRow>(rawDataSql, new { 
            SignalIds = signalIds, 
            FromTime = fromTime, 
            ToTime = toTime 
        })).ToList();

        // 4. Overall statistics per signal from the raw dataset
        const string statsSql = @"
            SELECT 
                signal_id AS SignalId,
                ROUND(AVG(value)::numeric, 2) AS AvgValue,
                ROUND(MIN(value)::numeric, 2) AS LowestValue,
                ROUND(MAX(value)::numeric, 2) AS PeakValue,
                COUNT(*) AS TotalCount
            FROM signal_data
            WHERE signal_id = ANY(@SignalIds) AND time >= @FromTime AND time <= @ToTime
            GROUP BY signal_id;";

        var statsDict = (await conn.QueryAsync(statsSql, new { 
            SignalIds = signalIds, 
            FromTime = fromTime, 
            ToTime = toTime 
        })).ToDictionary(
            r => (int)r.signalid, 
            r => (Avg: (double)r.avgvalue, Min: (double)r.lowestvalue, Max: (double)r.peakvalue, Count: (int)(long)r.totalcount)
        );

        // Group unaggregated data points by signal
        var pointsBySignal = rawRows.GroupBy(r => r.SignalId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => new SignalDataPointDto
                {
                    Time = r.Time,
                    Value = r.Value,
                    Min = r.Value,
                    Max = r.Value
                }).ToList()
            );

        foreach (var sig in targetSignals)
        {
            statsDict.TryGetValue(sig.SignalId, out var stat);
            pointsBySignal.TryGetValue(sig.SignalId, out var points);

            int pointCount = points?.Count ?? 0;
            report.Signals.Add(new SignalSummaryDto
            {
                SignalId = sig.SignalId,
                Name = sig.Name,
                Unit = sig.Unit ?? "",
                DesignMin = sig.MinValue,
                DesignMax = sig.MaxValue,
                AvgValue = stat.Avg,
                LowestValue = stat.Min,
                PeakValue = stat.Max,
                TotalReadings = pointCount > 0 ? pointCount : stat.Count,
                DataPoints = points ?? new List<SignalDataPointDto>()
            });
        }

        // 6. Events (if requested)
        if (request.IncludeEvents)
        {
            const string eventSql = @"
                SELECT 
                    e.event_id AS EventId,
                    e.signal_id AS SignalId,
                    s.name AS SignalName,
                    e.event_type AS EventType,
                    e.start_time AS StartTime,
                    e.end_time AS EndTime,
                    e.peak_value AS PeakValue,
                    e.threshold AS Threshold
                FROM events e
                JOIN signals s ON e.signal_id = s.signal_id
                WHERE e.asset_id = @AssetId AND e.start_time >= @FromTime AND e.start_time <= @ToTime
                ORDER BY e.start_time DESC;";

            var events = await conn.QueryAsync<EventItemDto>(eventSql, new { request.AssetId, FromTime = fromTime, ToTime = toTime });
            report.Events = events.ToList();

            // Count excursions per signal
            foreach (var evt in report.Events)
            {
                var sig = report.Signals.FirstOrDefault(s => s.SignalId == evt.SignalId);
                if (sig != null) sig.ExcursionCount++;
            }
        }

        // 7. Alerts (if requested)
        if (request.IncludeAlerts)
        {
            const string alertSql = @"
                SELECT 
                    a.alert_id AS AlertId,
                    a.signal_id AS SignalId,
                    s.name AS SignalName,
                    a.triggered_at AS TriggeredAt,
                    a.trigger_value AS TriggerValue,
                    a.threshold_value AS ThresholdValue,
                    a.severity AS Severity,
                    a.status AS Status,
                    a.resolved_at AS ResolvedAt
                FROM alerts a
                JOIN signals s ON a.signal_id = s.signal_id
                WHERE a.asset_id = @AssetId AND a.triggered_at >= @FromTime AND a.triggered_at <= @ToTime
                ORDER BY a.triggered_at DESC;";

            var alerts = await conn.QueryAsync<AlertItemDto>(alertSql, new { request.AssetId, FromTime = fromTime, ToTime = toTime });
            report.Alerts = alerts.ToList();
        }

        // 8. Health Score & Insights Calculation
        int health = 100;
        int critAlerts = report.Alerts.Count(a => a.Severity.Equals("CRITICAL", StringComparison.OrdinalIgnoreCase));
        int warnAlerts = report.Alerts.Count(a => a.Severity.Equals("WARNING", StringComparison.OrdinalIgnoreCase));
        int activeAlerts = report.Alerts.Count(a => a.Status.Equals("OPEN", StringComparison.OrdinalIgnoreCase));

        health -= (critAlerts * 15);
        health -= (warnAlerts * 5);
        health -= (activeAlerts * 5);
        health -= (report.Events.Count * 3);
        health = Math.Clamp(health, 20, 100);

        report.HealthScore = health;
        report.HealthStatus = health switch
        {
            >= 90 => "Optimal",
            >= 75 => "Good",
            >= 50 => "Needs Attention",
            _ => "Critical Risk"
        };

        // Synthesize Insights
        var insights = new List<string>();
        insights.Add($"Operating Health Score is evaluated at {health}% ({report.HealthStatus}).");

        if (critAlerts > 0)
        {
            insights.Add($"Identified {critAlerts} critical severity alert(s) during the period requiring priority maintenance intervention.");
        }
        else
        {
            insights.Add("Zero critical severity threshold events were triggered during this inspection interval.");
        }

        var breachedSignals = report.Signals.Where(s => s.HasViolation).ToList();
        if (breachedSignals.Any())
        {
            var names = string.Join(", ", breachedSignals.Take(3).Select(s => s.Name));
            insights.Add($"Limit exceedances detected on: {names}. Peak values exceeded nominal operational limits.");
        }
        else
        {
            insights.Add("All monitored telemetry channels remained within manufacturer engineering boundaries.");
        }

        if (report.Events.Any())
        {
            var longest = report.Events.OrderByDescending(e => e.DurationMinutes).First();
            insights.Add($"Longest continuous excursion event: {longest.SignalName} duration {longest.DurationMinutes:F1} mins ({longest.EventType}).");
        }

        report.KeyInsights = insights;

        return report;
    }

    public async Task<(bool HasData, long PointCount, DateTime? MinRecordedTime, DateTime? MaxRecordedTime)> CheckDataAvailabilityAsync(
        int assetId, 
        DateTime from, 
        DateTime to, 
        CancellationToken ct = default)
    {
        using var conn = CreateConnection();

        const string sql = @"
            SELECT 
                COUNT(*) AS PointCount,
                MIN(time) AS MinPeriodTime,
                MAX(time) AS MaxPeriodTime
            FROM signal_data sd
            JOIN signals s ON s.signal_id = sd.signal_id
            WHERE s.asset_id = @AssetId
              AND sd.time >= @From AND sd.time <= @To;";

        var row = await conn.QueryFirstOrDefaultAsync(sql, new { AssetId = assetId, From = from, To = to });
        long count = row != null ? (long)row.pointcount : 0L;

        DateTime? minGlobal = null;
        DateTime? maxGlobal = null;

        if (count == 0)
        {
            // Query global boundaries for this asset to provide helpful guidance to user
            const string boundarySql = @"
                SELECT 
                    MIN(sd.time) AS MinTime, 
                    MAX(sd.time) AS MaxTime 
                FROM signal_data sd
                JOIN signals s ON s.signal_id = sd.signal_id
                WHERE s.asset_id = @AssetId;";
            var bounds = await conn.QueryFirstOrDefaultAsync(boundarySql, new { AssetId = assetId });
            if (bounds != null)
            {
                minGlobal = bounds.mintime != null ? (DateTime)bounds.mintime : null;
                maxGlobal = bounds.maxtime != null ? (DateTime)bounds.maxtime : null;
            }
        }

        return (count > 0, count, minGlobal, maxGlobal);
    }

    private class RawTelemetryRow
    {
        public int SignalId { get; set; }
        public DateTime Time { get; set; }
        public double Value { get; set; }
    }
}
