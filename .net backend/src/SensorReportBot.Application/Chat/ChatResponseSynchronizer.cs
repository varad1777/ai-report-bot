namespace SensorReportBot.Application.Chat;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SensorReportBot.Application.DTOs;
using SensorReportBot.Domain.Entities;

/// <summary>Result of syncing one LLM reply with the database and the stored conversation state.</summary>
public sealed record SyncedTurn(
    bool IsOnTopic,
    bool IsComplete,
    string SuggestedAction,
    IReadOnlyList<string> SuggestedOptions,
    ExtractedReportParametersDto Parameters,
    string? Problem);

/// <summary>
/// Server-side guard rails around the model's JSON. It never writes text for the user - the reply
/// stays exactly what the LLM wrote - it only makes the *structured* part trustworthy:
///   * grounds assetId/assetName in the catalog (small models mix up ids and names),
///   * validates/normalises the timeframe (and the 90-day cap),
///   * recomputes isComplete from the real state,
///   * keeps suggestedAction consistent with that state,
///   * fills suggestedOptions from the database / presets so the model never has to emit lists.
/// When the model invented an asset or an invalid timeframe, <see cref="SyncedTurn.Problem"/> is set
/// so the caller can ask the model once more (the retry reply is again written by the LLM).
/// </summary>
public static class ChatResponseSynchronizer
{
    public static readonly IReadOnlyList<string> TimeframePresets = new[] { "24h", "5d", "14d", "30d" };
    public static readonly IReadOnlyList<string> ConfirmOptions = new[] { "Yes", "Change Options" };

    public static SyncedTurn Sync(
        LlmChatReplyDto raw,
        ExtractedReportParametersDto? previous,
        IReadOnlyList<AssetDto> assets)
    {
        var prev = previous?.Clone() ?? new ExtractedReportParametersDto();
        string? llmAction = NormalizeAction(raw.SuggestedAction);

        // Unrelated chat never touches the saved configuration.
        if (!raw.IsOnTopic)
        {
            return new SyncedTurn(false, prev.HasAsset && prev.HasTime, SuggestedActions.None,
                Array.Empty<string>(), prev, null);
        }

        var p = raw.ExtractedParameters ?? new ExtractedReportParametersDto();
        string? problem = null;

        // ---- Asset -------------------------------------------------------------------------
        var asset = ResolveAsset(p, assets);
        if (asset == null && !string.IsNullOrWhiteSpace(p.AssetName))
            problem = $"assetName \"{p.AssetName.Trim()}\" is not in the ASSETS catalog. Tell the user it is unknown and ask which catalog asset they want.";

        // The model signalled "let the user pick again" but kept the old value: honour the intent.
        if (llmAction == SuggestedActions.SelectAsset && asset != null && asset.AssetId == prev.AssetId)
            asset = null;

        // ---- Timeframe ---------------------------------------------------------------------
        var (timeRange, fromDate, toDate, timeProblem) = ResolveTime(p);
        problem ??= timeProblem;

        bool timeUnchanged = timeRange == prev.TimeRange && fromDate == prev.FromDate && toDate == prev.ToDate;
        if ((llmAction == SuggestedActions.SelectTimeframe || llmAction == SuggestedActions.SelectAsset || !raw.IsComplete) && !prev.HasTime)
        {
            timeRange = fromDate = toDate = null;
        }
        else if (llmAction == SuggestedActions.SelectTimeframe && timeUnchanged)
        {
            timeRange = fromDate = toDate = null;
        }

        // ---- Assemble ----------------------------------------------------------------------
        var result = new ExtractedReportParametersDto
        {
            AssetId = asset?.AssetId,
            AssetName = asset?.Name,
            TimeRange = timeRange,
            FromDate = fromDate,
            ToDate = toDate,
            Mode = "raw",
            SignalIds = asset != null && asset.AssetId == prev.AssetId
                ? (p.SignalIds is { Count: > 0 } ? p.SignalIds : prev.SignalIds)
                : null
        };

        bool complete = result.HasAsset && result.HasTime;
        bool unchanged = result.AssetId == prev.AssetId && SameTime(result, prev);

        // ---- Action + options --------------------------------------------------------------
        string action =
            !result.HasAsset ? SuggestedActions.SelectAsset :
            !result.HasTime ? SuggestedActions.SelectTimeframe :
            // Fully configured: ask for confirmation, unless this turn was just a side question
            // the model deliberately answered with "none" and nothing in the config changed.
            (llmAction == SuggestedActions.None && unchanged) ? SuggestedActions.None :
            SuggestedActions.ConfirmQueue;

        IReadOnlyList<string> options = action switch
        {
            SuggestedActions.SelectAsset => assets.Select(a => a.Name).ToList(),
            SuggestedActions.SelectTimeframe => TimeframePresets,
            SuggestedActions.ConfirmQueue => ConfirmOptions,
            _ => Array.Empty<string>()
        };

        return new SyncedTurn(true, complete, action, options, result, problem);
    }

    private static bool SameTime(ExtractedReportParametersDto a, ExtractedReportParametersDto b) =>
        a.TimeRange == b.TimeRange && a.FromDate == b.FromDate && a.ToDate == b.ToDate;

    private static string? NormalizeAction(string? action)
    {
        string? a = action?.Trim().ToLowerInvariant();
        return a is SuggestedActions.SelectAsset or SuggestedActions.SelectTimeframe
            or SuggestedActions.ConfirmQueue or SuggestedActions.None
            ? a
            : null;
    }

    private static AssetDto? ResolveAsset(ExtractedReportParametersDto p, IReadOnlyList<AssetDto> assets)
    {
        string? name = p.AssetName?.Trim();

        // 1. Exact catalog name wins (models copy names more reliably than ids)
        if (!string.IsNullOrEmpty(name))
        {
            var exact = assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }

        // 2. Valid catalog id
        if (p.AssetId is > 0)
        {
            var byId = assets.FirstOrDefault(a => a.AssetId == p.AssetId);
            if (byId != null) return byId;
        }

        // 3. Typos / partial names ("boiler pump") via the catalog-driven fuzzy matcher
        if (!string.IsNullOrEmpty(name))
        {
            var match = AssetMatcher.Match(name, assets);
            if (match.IsMatch) return match.Asset;
        }

        return null;
    }

    private static (string? TimeRange, string? FromDate, string? ToDate, string? Problem) ResolveTime(ExtractedReportParametersDto p)
    {
        // Absolute dates take precedence
        if (!string.IsNullOrWhiteSpace(p.FromDate))
        {
            if (TryParseUtc(p.FromDate, out var from))
            {
                DateTime? to = null;
                if (!string.IsNullOrWhiteSpace(p.ToDate) && TryParseUtc(p.ToDate, out var parsedTo)) to = parsedTo;
                if (to.HasValue && to.Value < from) (from, to) = (to.Value, from);

                var end = to ?? DateTime.UtcNow;
                if ((end - from).TotalDays > TimeframeParser.MaxDays)
                    return (null, null, null,
                        $"The requested date range is longer than {TimeframeParser.MaxDays} days, which is the maximum. Explain this and ask for a shorter range.");

                return (null, Iso(from), to.HasValue ? Iso(to.Value) : null, null);
            }

            return (null, null, null,
                $"fromDate \"{p.FromDate}\" is not a valid ISO-8601 date. Ask the user to restate the dates.");
        }

        if (!string.IsNullOrWhiteSpace(p.TimeRange))
        {
            var tf = TimeframeParser.Parse(p.TimeRange, allowBareNumber: true);
            if (tf.HasProblem) return (null, null, null, tf.Problem);
            if (tf.TimeRange != null) return (tf.TimeRange, null, null, null);
            if (tf.FromDate != null) return (null, tf.FromDate, tf.ToDate, null);

            return (null, null, null,
                $"timeRange \"{p.TimeRange}\" is invalid; it must look like 24h, 5d or 14d. Ask the user for a timeframe.");
        }

        return (null, null, null, null);
    }

    private static bool TryParseUtc(string text, out DateTime utc) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);

    private static string Iso(DateTime dt) => dt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
