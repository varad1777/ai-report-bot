namespace SensorReportBot.Application.Steps;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SensorReportBot.Application.Chat;
using SensorReportBot.Application.DTOs;
using SensorReportBot.Application.Exceptions;
using SensorReportBot.Application.Interfaces;
using SensorReportBot.Domain.Entities;

/// <summary>
/// The hybrid intelligence engine of the pipeline:
/// 1. Fast-Path (Laya System-1): ~25ms decision model for deterministic, clean inputs (resets, exact buttons, clean asset/time selections).
/// 2. Deep-Path (Ollama Qwen System-2): Handles conversational nuance, typos ("3.5 dddaysss"), slang, and multi-intent queries.
/// Laya's extracted hints are ALWAYS passed to Qwen whenever input is conversational or incomplete,
/// guaranteeing fast pre-digested decisions without false off-topic refusals or accidental resets.
/// </summary>
public class IntentExtractionStep : IChatStep
{
    private readonly ILlmService _llmService;
    private readonly ILayaClient _layaClient;
    private readonly IPromptProvider _promptProvider;
    private readonly ITelemetryRepository _telemetryRepo;
    private readonly ILogger<IntentExtractionStep> _logger;

    private static readonly HashSet<string> ConfirmWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes", "yeah", "yep", "sure", "ok", "okay", "confirm", "proceed", "generate", "queue", "queue it", "generate report", "yes please", "do it"
    };

    private static readonly HashSet<string> ResetWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "reset", "clear", "cancel", "start over", "reset options", "clear options", "restart"
    };

    private static readonly string[] DefaultTimeframeOptions = new[]
    {
        "⚡ Last 24 Hours", "📊 Last 5 Days", "🗓️ Last 2 Weeks", "📈 Last 30 Days", "Custom Calendar Range"
    };

    public IntentExtractionStep(
        ILlmService llmService,
        ILayaClient layaClient,
        IPromptProvider promptProvider,
        ITelemetryRepository telemetryRepo,
        ILogger<IntentExtractionStep> logger)
    {
        _llmService = llmService;
        _layaClient = layaClient;
        _promptProvider = promptProvider;
        _telemetryRepo = telemetryRepo;
        _logger = logger;
    }

    public async Task RunAsync(ChatRequestState state, CancellationToken ct)
    {
        var assets = await _telemetryRepo.GetAssetsAsync(ct);
        string? layaHintNote = null;

        // ── 1. Fast-Path & Hint Extraction (Laya System-1 ~25ms) ──
        if (_layaClient.IsEnabled)
        {
            try
            {
                var analysis = await AnalyzeWithLayaAsync(state, assets, ct);
                if (analysis.FastResult != null)
                {
                    state.Reply = analysis.FastResult.Reply;
                    state.IsOnTopic = analysis.FastResult.IsOnTopic;
                    state.IsComplete = analysis.FastResult.IsComplete;
                    state.SuggestedAction = analysis.FastResult.SuggestedAction;
                    state.SuggestedOptions = analysis.FastResult.SuggestedOptions.ToList();
                    state.ExtractedParameters = analysis.FastResult.Parameters;
                    return;
                }

                // Pass Laya's System-1 pre-analysis hints directly into Qwen
                layaHintNote = BuildLayaHintNote(analysis);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Laya fast-path encountered an issue; falling back to deep LLM.");
            }
        }

        // ── 2. Deep-Path: Ollama Qwen with Laya hints for reasoning (2-3s) ──
        string systemPrompt = await BuildSystemPromptAsync(assets, ct);

        var (reply, turn) = await AskAsync(state, systemPrompt, assets, note: layaHintNote, ct);

        // One corrective retry when reply is empty or invented invalid schema parameters.
        string? problem = string.IsNullOrWhiteSpace(reply?.ReplyMessage)
            ? "replyMessage was empty. Write a helpful reply."
            : turn?.Problem;

        if (problem != null)
        {
            _logger.LogInformation("Retrying LLM call once. Problem: {Problem}", problem);
            string retryNote = string.IsNullOrWhiteSpace(layaHintNote) ? problem : $"{layaHintNote} | RETRY_PROBLEM: {problem}";
            var (retryReply, retryTurn) = await AskAsync(state, systemPrompt, assets, retryNote, ct);
            if (!string.IsNullOrWhiteSpace(retryReply?.ReplyMessage))
            {
                reply = retryReply;
                turn = retryTurn;
            }
        }

        if (reply == null || turn == null || string.IsNullOrWhiteSpace(reply.ReplyMessage))
            throw new LlmUnavailableException("The language model did not return a usable reply.");

        // Guard against empty telemetry dates from LLM deep-path
        if (turn.Parameters.HasAsset && turn.Parameters.HasTime && turn.Parameters.AssetId.HasValue)
        {
            var period = ResolveTimeframeToUtc(null, turn.Parameters);
            if (period.HasValue)
            {
                var (hasData, pointCount, minRecorded, maxRecorded) = await _telemetryRepo.CheckDataAvailabilityAsync(
                    turn.Parameters.AssetId.Value, period.Value.From, period.Value.To, ct);

                if (!hasData)
                {
                    string rangeMsg = $"{period.Value.From:yyyy-MM-dd} to {period.Value.To:yyyy-MM-dd}";
                    string availableNotice = (minRecorded.HasValue && maxRecorded.HasValue)
                        ? $" Recorded telemetry is available between {minRecorded.Value:yyyy-MM-dd} and {maxRecorded.Value:yyyy-MM-dd}."
                        : "";

                    state.Reply = $"No telemetry data was found for **{turn.Parameters.AssetName}** between {rangeMsg}.{availableNotice} Please select a date range within the recorded period.";
                    state.IsOnTopic = true;
                    state.IsComplete = false;
                    state.SuggestedAction = SuggestedActions.SelectTimeframe;
                    state.SuggestedOptions = DefaultTimeframeOptions.ToList();
                    turn.Parameters.TimeRange = null;
                    turn.Parameters.FromDate = null;
                    turn.Parameters.ToDate = null;
                    state.ExtractedParameters = turn.Parameters;
                    return;
                }
            }
        }

        state.Reply = reply.ReplyMessage.Trim();
        state.IsOnTopic = turn.IsOnTopic;
        state.IsComplete = turn.IsComplete;
        state.SuggestedAction = turn.SuggestedAction;
        state.SuggestedOptions = turn.SuggestedOptions.ToList();
        state.ExtractedParameters = turn.Parameters;
    }

    private static string? BuildLayaHintNote(LayaAnalysis analysis)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(analysis.LayaIntent))
            parts.Add($"intent={analysis.LayaIntent}");
        if (analysis.MatchedAsset != null)
            parts.Add($"matched_asset=\"{analysis.MatchedAsset.Name}\" (id={analysis.MatchedAsset.AssetId})");
        if (analysis.ParsedTimeframe is { Found: true })
            parts.Add($"parsed_timeframe=\"{analysis.ParsedTimeframe.TimeRange ?? $"{analysis.ParsedTimeframe.FromDate}..{analysis.ParsedTimeframe.ToDate}"}\"");
        if (analysis.IsRelevant.HasValue)
            parts.Add($"relevance={(analysis.IsRelevant.Value ? "on_topic" : "off_topic")}");

        return parts.Count > 0 ? "LAYA_PREANALYSIS: " + string.Join(" | ", parts) : null;
    }

    private async Task<LayaAnalysis> AnalyzeWithLayaAsync(
        ChatRequestState state,
        IReadOnlyList<AssetDto> assets,
        CancellationToken ct)
    {
        string userMsg = state.UserMessage?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userMsg))
            return new LayaAnalysis(null, null, null, null, null);

        var prevParams = state.PreviousParameters?.Clone() ?? new ExtractedReportParametersDto();

        // ── 1. Deterministic Reset / Clear Request (C# Exact Match) ──
        if (IsResetRequest(userMsg))
        {
            var emptyParams = new ExtractedReportParametersDto();
            var fast = new FastTurnResult(
                Reply: "I have cleared the current report options. Which asset from the catalog would you like to examine?",
                IsOnTopic: true,
                IsComplete: false,
                SuggestedAction: SuggestedActions.SelectAsset,
                SuggestedOptions: assets.Select(a => a.Name).ToList(),
                Parameters: emptyParams
            );
            return new LayaAnalysis(fast, "reset_options", true, null, null);
        }

        // ── 2. Deterministic Confirm Queue when already complete (C# Exact Match) ──
        if (prevParams.HasAsset && prevParams.HasTime && IsConfirmation(userMsg))
        {
            var period = ResolveTimeframeToUtc(null, prevParams);
            if (period.HasValue && prevParams.AssetId.HasValue)
            {
                var (hasData, pointCount, minRecorded, maxRecorded) = await _telemetryRepo.CheckDataAvailabilityAsync(
                    prevParams.AssetId.Value, period.Value.From, period.Value.To, ct);

                if (!hasData)
                {
                    string rangeMsg = $"{period.Value.From:yyyy-MM-dd} to {period.Value.To:yyyy-MM-dd}";
                    string availableNotice = (minRecorded.HasValue && maxRecorded.HasValue)
                        ? $" Recorded telemetry is available between {minRecorded.Value:yyyy-MM-dd} and {maxRecorded.Value:yyyy-MM-dd}."
                        : "";

                    prevParams.TimeRange = null;
                    prevParams.FromDate = null;
                    prevParams.ToDate = null;

                    var fastNoData = new FastTurnResult(
                        Reply: $"Cannot generate report: No telemetry data was found for **{prevParams.AssetName}** between {rangeMsg}.{availableNotice} Please select a date range within the recorded period.",
                        IsOnTopic: true,
                        IsComplete: false,
                        SuggestedAction: SuggestedActions.SelectTimeframe,
                        SuggestedOptions: DefaultTimeframeOptions,
                        Parameters: prevParams
                    );
                    return new LayaAnalysis(fastNoData, "confirm_queue", true, null, null);
                }
            }

            var fast = new FastTurnResult(
                Reply: $"Understood. I am queueing the telemetry report generation for {prevParams.AssetName} ({prevParams.TimeRange ?? $"{prevParams.FromDate} to {prevParams.ToDate}"}).",
                IsOnTopic: true,
                IsComplete: true,
                SuggestedAction: SuggestedActions.ConfirmQueue,
                SuggestedOptions: new[] { "Yes", "Change Options" },
                Parameters: prevParams
            );
            return new LayaAnalysis(fast, "confirm_queue", true, null, null);
        }

        // ── 3. Asset & Timeframe Matching via C# Engine ──
        var assetMatch = AssetMatcher.Match(userMsg, assets);
        var matchedAsset = assetMatch.Asset;
        var timeResult = TimeframeParser.Parse(userMsg, allowBareNumber: true, utcNow: DateTime.UtcNow);
        bool hasConversationalNuance = IsConversationalNuance(userMsg);

        // Clean direct match for both asset and timeframe (no ambiguity, no conversational nuance)
        if (matchedAsset != null && timeResult.Found && !hasConversationalNuance)
        {
            var targetParams = new ExtractedReportParametersDto
            {
                AssetId = matchedAsset.AssetId,
                AssetName = matchedAsset.Name,
                TimeRange = timeResult.TimeRange,
                FromDate = timeResult.FromDate,
                ToDate = timeResult.ToDate,
                Mode = "raw"
            };

            var period = ResolveTimeframeToUtc(timeResult, targetParams);
            if (period.HasValue)
            {
                var (hasData, pointCount, minRecorded, maxRecorded) = await _telemetryRepo.CheckDataAvailabilityAsync(
                    matchedAsset.AssetId, period.Value.From, period.Value.To, ct);

                if (!hasData)
                {
                    string rangeMsg = $"{period.Value.From:yyyy-MM-dd} to {period.Value.To:yyyy-MM-dd}";
                    string availableNotice = (minRecorded.HasValue && maxRecorded.HasValue)
                        ? $" Recorded telemetry is available between {minRecorded.Value:yyyy-MM-dd} and {maxRecorded.Value:yyyy-MM-dd}."
                        : "";

                    var emptyParams = new ExtractedReportParametersDto
                    {
                        AssetId = matchedAsset.AssetId,
                        AssetName = matchedAsset.Name,
                        Mode = "raw"
                    };

                    var fastNoData = new FastTurnResult(
                        Reply: $"No telemetry data was found for **{matchedAsset.Name}** between {rangeMsg}.{availableNotice} Please select a date range within the recorded period.",
                        IsOnTopic: true,
                        IsComplete: false,
                        SuggestedAction: SuggestedActions.SelectTimeframe,
                        SuggestedOptions: DefaultTimeframeOptions,
                        Parameters: emptyParams
                    );
                    return new LayaAnalysis(fastNoData, "configure_report", true, matchedAsset, timeResult);
                }
            }

            string timeDesc = DescribeTimeframe(targetParams);
            var fast = new FastTurnResult(
                Reply: $"I have configured the telemetry report for {targetParams.AssetName} covering {timeDesc}. Would you like me to generate this report?",
                IsOnTopic: true,
                IsComplete: true,
                SuggestedAction: SuggestedActions.ConfirmQueue,
                SuggestedOptions: new[] { "Yes", "Change Options" },
                Parameters: targetParams
            );
            return new LayaAnalysis(fast, "configure_report", true, matchedAsset, timeResult);
        }

        // Clean direct asset match without timeframe (and no conversational questions)
        if (matchedAsset != null && !timeResult.Found && !hasConversationalNuance && !prevParams.HasTime)
        {
            var targetParams = new ExtractedReportParametersDto
            {
                AssetId = matchedAsset.AssetId,
                AssetName = matchedAsset.Name,
                Mode = "raw"
            };

            var fast = new FastTurnResult(
                Reply: $"Great! I can help you configure a report for the {targetParams.AssetName}. Could you please specify the timeframe you're interested in?",
                IsOnTopic: true,
                IsComplete: false,
                SuggestedAction: SuggestedActions.SelectTimeframe,
                SuggestedOptions: DefaultTimeframeOptions,
                Parameters: targetParams
            );
            return new LayaAnalysis(fast, "configure_report", true, matchedAsset, null);
        }

        // Clean direct timeframe answer when asset is already selected (allow updates/changes)
        if (prevParams.HasAsset && timeResult.Found && !hasConversationalNuance)
        {
            var testParams = prevParams.Clone();
            testParams.TimeRange = timeResult.TimeRange;
            testParams.FromDate = timeResult.FromDate;
            testParams.ToDate = timeResult.ToDate;

            var period = ResolveTimeframeToUtc(timeResult, testParams);
            if (period.HasValue && prevParams.AssetId.HasValue)
            {
                var (hasData, pointCount, minRecorded, maxRecorded) = await _telemetryRepo.CheckDataAvailabilityAsync(
                    prevParams.AssetId.Value, period.Value.From, period.Value.To, ct);

                if (!hasData)
                {
                    string rangeMsg = $"{period.Value.From:yyyy-MM-dd} to {period.Value.To:yyyy-MM-dd}";
                    string availableNotice = (minRecorded.HasValue && maxRecorded.HasValue)
                        ? $" Recorded telemetry is available between {minRecorded.Value:yyyy-MM-dd} and {maxRecorded.Value:yyyy-MM-dd}."
                        : "";

                    prevParams.TimeRange = null;
                    prevParams.FromDate = null;
                    prevParams.ToDate = null;

                    var fastNoData = new FastTurnResult(
                        Reply: $"No telemetry data was found for **{prevParams.AssetName}** between {rangeMsg}.{availableNotice} Please select a date range within the recorded period.",
                        IsOnTopic: true,
                        IsComplete: false,
                        SuggestedAction: SuggestedActions.SelectTimeframe,
                        SuggestedOptions: DefaultTimeframeOptions,
                        Parameters: prevParams
                    );
                    return new LayaAnalysis(fastNoData, "configure_report", true, null, timeResult);
                }
            }

            prevParams.TimeRange = timeResult.TimeRange;
            prevParams.FromDate = timeResult.FromDate;
            prevParams.ToDate = timeResult.ToDate;

            string timeDesc = DescribeTimeframe(prevParams);
            var fast = new FastTurnResult(
                Reply: $"I have configured the telemetry report for {prevParams.AssetName} covering {timeDesc}. Would you like me to generate this report?",
                IsOnTopic: true,
                IsComplete: true,
                SuggestedAction: SuggestedActions.ConfirmQueue,
                SuggestedOptions: new[] { "Yes", "Change Options" },
                Parameters: prevParams
            );
            return new LayaAnalysis(fast, "configure_report", true, null, timeResult);
        }

        // ── 4. Laya System-1 Fast Classification (for hints) ──
        var intentRes = await _layaClient.AskAsync(
            userMsg,
            "What is the user trying to do?",
            "choice",
            choices: new[] { "greeting", "reset_options", "confirm_queue", "configure_report", "general_question" },
            ct);

        string intent = intentRes?.AsString?.ToLowerInvariant() ?? "general_question";

        // Clean greeting fast-path (only when no asset is selected yet)
        if (intent == "greeting" && !prevParams.HasAsset && !hasConversationalNuance)
        {
            var fast = new FastTurnResult(
                Reply: "Hello! I am SensorBot, your industrial telemetry and EcoStruxure report assistant. Which machine or equipment would you like to examine?",
                IsOnTopic: true,
                IsComplete: false,
                SuggestedAction: SuggestedActions.SelectAsset,
                SuggestedOptions: assets.Select(a => a.Name).ToList(),
                Parameters: prevParams
            );
            return new LayaAnalysis(fast, intent, true, null, null);
        }

        // ── 5. Relevance Guardrail ──
        // CRITICAL FIX: If an asset is already selected (in-progress workflow), NEVER flag timeframe or follow-up answers as off-topic!
        bool isRelevant = true;
        if (!prevParams.HasAsset && !prevParams.HasTime)
        {
            var topicRes = await _layaClient.AskAsync(
                userMsg,
                "Is this message relevant to industrial plant machinery, sensors, telemetry, equipment monitoring, report generation, or asking about available assets/catalog?",
                "noul",
                choices: null,
                ct);

            isRelevant = topicRes?.AsBoolean ?? true;
        }

        if (!isRelevant)
        {
            _logger.LogInformation("Laya filtered out off-topic message '{UserMsg}' - skipping System-2 LLM execution.", userMsg);
            var fast = new FastTurnResult(
                Reply: "I am SensorBot, dedicated exclusively to industrial plant equipment, sensor telemetry, and report generation. Please ask about plant machinery or select an asset from the catalog.",
                IsOnTopic: false,
                IsComplete: prevParams.HasAsset && prevParams.HasTime,
                SuggestedAction: !prevParams.HasAsset ? SuggestedActions.SelectAsset : (prevParams.HasTime ? SuggestedActions.ConfirmQueue : SuggestedActions.SelectTimeframe),
                SuggestedOptions: !prevParams.HasAsset ? assets.Select(a => a.Name).ToList() : DefaultTimeframeOptions,
                Parameters: prevParams
            );
            return new LayaAnalysis(fast, intent, false, matchedAsset, timeResult);
        }

        // ── 6. Pass Insights to Qwen (System-2 Deep Reasoning) ──
        // For conversational inputs, typos ("3.5 dddaysss"), slang, or multi-intent questions:
        // Forward Laya's extracted signals to Qwen so Qwen resolves it with 100% precision.
        _logger.LogInformation("Laya passing request to full LLM reasoning (intent: {Intent}, asset: {Asset}, time: {Time}, isRelevant: {IsRelevant}).",
            intent, matchedAsset?.Name, timeResult?.TimeRange, isRelevant);

        return new LayaAnalysis(null, intent, isRelevant, matchedAsset, timeResult);
    }

    private static bool IsResetRequest(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        string trimmed = message.Trim().TrimEnd('.', '!', '?');
        return ResetWords.Contains(trimmed);
    }

    private static bool IsConversationalNuance(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Contains('?')) return true;

        string lower = text.ToLowerInvariant();
        return lower.Contains("will it") || lower.Contains("is it") || lower.Contains("can we")
            || lower.Contains("can you") || lower.Contains("ok") || lower.Contains("okay")
            || lower.Contains("fine") || lower.Contains("possible") || lower.Contains("should")
            || lower.Contains("what about") || lower.Contains("how about") || lower.Contains("bhai")
            || lower.Contains("bro") || lower.Contains("please") || lower.Contains("just");
    }

    private static bool IsConfirmation(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        string trimmed = message.Trim().TrimEnd('.', '!', '?');
        return ConfirmWords.Contains(trimmed);
    }

    private static string DescribeTimeframe(ExtractedReportParametersDto p)
    {
        if (!string.IsNullOrWhiteSpace(p.TimeRange))
            return p.TimeRange;

        if (!string.IsNullOrWhiteSpace(p.FromDate))
        {
            if (DateTime.TryParse(p.FromDate, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var f))
            {
                if (!string.IsNullOrWhiteSpace(p.ToDate) && DateTime.TryParse(p.ToDate, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var t))
                {
                    return $"{f:MMM dd, yyyy} to {t:MMM dd, yyyy}";
                }
                return $"since {f:MMM dd, yyyy}";
            }
        }

        return "the selected period";
    }

    private static (DateTime From, DateTime To)? ResolveTimeframeToUtc(TimeframeResult? timeResult, ExtractedReportParametersDto? p)
    {
        string? fromStr = timeResult?.FromDate ?? p?.FromDate;
        string? toStr = timeResult?.ToDate ?? p?.ToDate;
        string? rangeStr = timeResult?.TimeRange ?? p?.TimeRange;

        if (!string.IsNullOrWhiteSpace(fromStr) &&
            DateTime.TryParse(fromStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var f))
        {
            DateTime t = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(toStr) &&
                DateTime.TryParse(toStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTo))
            {
                t = parsedTo;
            }
            if (t < f) (f, t) = (t, f);
            return (f, t);
        }

        if (!string.IsNullOrWhiteSpace(rangeStr))
        {
            string tr = rangeStr.Trim().ToLowerInvariant();
            var now = DateTime.UtcNow;
            if (tr.EndsWith("h") && double.TryParse(tr[..^1], NumberStyles.Any, CultureInfo.InvariantCulture, out var h))
            {
                return (now.AddHours(-h), now);
            }
            if (tr.EndsWith("d") && double.TryParse(tr[..^1], NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            {
                return (now.AddDays(-d), now);
            }
            if (tr.EndsWith("w") && double.TryParse(tr[..^1], NumberStyles.Any, CultureInfo.InvariantCulture, out var w))
            {
                return (now.AddDays(-w * 7), now);
            }
        }

        return null;
    }

    private async Task<(LlmChatReplyDto? Reply, SyncedTurn? Turn)> AskAsync(
        ChatRequestState state,
        string systemPrompt,
        IReadOnlyList<AssetDto> assets,
        string? note,
        CancellationToken ct)
    {
        string userPrompt = PromptContextBuilder.Build(
            state.UserMessage, state.PreviousParameters, state.History, DateTime.UtcNow, note);

        LlmChatReplyDto? reply;
        try
        {
            reply = await _llmService.GenerateJsonResponseAsync<LlmChatReplyDto>(
                systemPrompt, userPrompt, ct, ChatReplySchema.Value);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmUnavailableException("The language model endpoint is not reachable.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LlmUnavailableException("The language model timed out.", ex);
        }

        if (reply == null) return (null, null);
        return (reply, ChatResponseSynchronizer.Sync(reply, state.PreviousParameters, assets));
    }

    private async Task<string> BuildSystemPromptAsync(IReadOnlyList<AssetDto> assets, CancellationToken ct)
    {
        string template = await _promptProvider.GetPromptAsync("ExtractorPrompt", ct);
        string catalog = assets.Count == 0
            ? "(none)"
            : string.Join(" | ", assets.Select(a => $"{a.AssetId}: {a.Name}"));

        return template.Replace("{AssetCatalog}", catalog);
    }

    private sealed record FastTurnResult(
        string Reply,
        bool IsOnTopic,
        bool IsComplete,
        string SuggestedAction,
        IReadOnlyList<string> SuggestedOptions,
        ExtractedReportParametersDto Parameters);

    private sealed record LayaAnalysis(
        FastTurnResult? FastResult,
        string? LayaIntent,
        bool? IsRelevant,
        AssetDto? MatchedAsset,
        TimeframeResult? ParsedTimeframe);
}
