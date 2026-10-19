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
        IReadOnlyList<string> availableCategories = Array.Empty<string>();
        LayaAnalysis? analysis = null;

        // ── 1. Cognitive Pre-Processing via Jev (System-1 ONNX ~25ms) ──
        if (_layaClient.IsEnabled)
        {
            try
            {
                analysis = await AnalyzeWithLayaAsync(state, assets, ct);
                availableCategories = analysis.AvailableCategories;

                // Pass Jev's System-1 pre-analysis hints directly to Qwen System-2 to guide reasoning
                layaHintNote = BuildLayaHintNote(analysis);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Jev cognitive pre-processor encountered an issue; falling back to deep LLM.");
            }
        }

        // If not retrieved via Laya, query available categories for active asset
        if (availableCategories.Count == 0)
        {
            var activeAssetMatch = AssetMatcher.Match(state.UserMessage, assets).Asset
                ?? (state.PreviousParameters?.AssetId != null ? assets.FirstOrDefault(a => a.AssetId == state.PreviousParameters.AssetId) : null);
            if (activeAssetMatch != null)
            {
                availableCategories = await _telemetryRepo.GetSignalCategoriesByAssetAsync(activeAssetMatch.AssetId, ct);
            }
        }

        // Compute pre-grounded state parameters to pass in prompt's STATE: line
        var promptStateParams = state.PreviousParameters?.Clone();
        if (analysis?.ActiveAsset != null)
        {
            promptStateParams ??= new ExtractedReportParametersDto();
            promptStateParams.AssetId = analysis.ActiveAsset.AssetId;
            promptStateParams.AssetName = analysis.ActiveAsset.Name;
            promptStateParams.Category = analysis.TargetCategory;
            promptStateParams.TimeRange = analysis.ParsedTimeframe?.TimeRange;
            promptStateParams.FromDate = analysis.ParsedTimeframe?.FromDate;
            promptStateParams.ToDate = analysis.ParsedTimeframe?.ToDate;
        }

        // ── 2. Deep-Path: Ollama Qwen with Laya hints for reasoning (2-3s) ──
        string systemPrompt = await BuildSystemPromptAsync(assets, ct);

        var (reply, turn) = await AskAsync(state, systemPrompt, assets, note: layaHintNote, availableCategories, ct, promptStateParams);

        // One corrective retry when reply is empty or invented invalid schema parameters.
        string? problem = string.IsNullOrWhiteSpace(reply?.ReplyMessage)
            ? "replyMessage was empty. Write a helpful reply."
            : turn?.Problem;

        if (problem != null)
        {
            _logger.LogInformation("Retrying LLM call once. Problem: {Problem}", problem);
            string retryNote = string.IsNullOrWhiteSpace(layaHintNote) ? problem : $"{layaHintNote} | RETRY_PROBLEM: {problem}";
            var (retryReply, retryTurn) = await AskAsync(state, systemPrompt, assets, retryNote, availableCategories, ct, promptStateParams);
            if (!string.IsNullOrWhiteSpace(retryReply?.ReplyMessage))
            {
                reply = retryReply;
                turn = retryTurn;
            }
        }

        if (reply == null || turn == null || string.IsNullOrWhiteSpace(reply.ReplyMessage))
            throw new LlmUnavailableException("The language model did not return a usable reply.");

        // If turn has an asset but availableCategories wasn't queried yet (e.g. user had typo like "primary crushert")
        if (turn.Parameters.AssetId.HasValue && (availableCategories == null || availableCategories.Count == 0))
        {
            availableCategories = await _telemetryRepo.GetSignalCategoriesByAssetAsync(turn.Parameters.AssetId.Value, ct);
            if (turn.SuggestedAction == SuggestedActions.SelectCategory)
            {
                turn = turn with { SuggestedOptions = availableCategories };
            }
        }

        // Guard against empty telemetry dates from LLM deep-path
        if (turn.Parameters.HasAsset && turn.Parameters.HasTime && turn.Parameters.AssetId.HasValue)
        {
            var period = ResolveTimeframeToUtc(null, turn.Parameters);
            if (period.HasValue)
            {
                var (hasData, pointCount, minRecorded, maxRecorded) = await _telemetryRepo.CheckDataAvailabilityAsync(
                    turn.Parameters.AssetId.Value, period.Value.From, period.Value.To, ct);

                // Auto-anchor relative timeframes (e.g. 24h, 2d, 16d) to latest recorded data if today is past the recorded dataset
                if (!hasData && !string.IsNullOrWhiteSpace(turn.Parameters.TimeRange) && maxRecorded.HasValue)
                {
                    var anchoredPeriod = ResolveTimeframeToUtc(null, turn.Parameters, maxAnchorTime: maxRecorded.Value);
                    if (anchoredPeriod.HasValue)
                    {
                        var (anchoredHasData, _, _, _) = await _telemetryRepo.CheckDataAvailabilityAsync(
                            turn.Parameters.AssetId.Value, anchoredPeriod.Value.From, anchoredPeriod.Value.To, ct);
                        if (anchoredHasData)
                        {
                            hasData = true;
                            period = anchoredPeriod;
                        }
                    }
                }

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
        if (analysis.ActiveAsset != null)
            parts.Add($"asset=\"{analysis.ActiveAsset.Name}\" (id={analysis.ActiveAsset.AssetId})");
        if (!string.IsNullOrWhiteSpace(analysis.TargetCategory))
            parts.Add($"category=\"{analysis.TargetCategory}\"");
        else
            parts.Add("category=none");

        if (analysis.AvailableCategories.Count > 0)
            parts.Add($"available_categories=[{string.Join(", ", analysis.AvailableCategories.Select(c => $"\"{c}\""))}]");

        if (analysis.ParsedTimeframe is { Found: true })
            parts.Add($"timeframe=\"{analysis.ParsedTimeframe.TimeRange ?? $"{analysis.ParsedTimeframe.FromDate}..{analysis.ParsedTimeframe.ToDate}"}\"");

        if (!string.IsNullOrWhiteSpace(analysis.ActionToTake))
            parts.Add($"action_to_take=\"{analysis.ActionToTake}\"");

        if (analysis.IsRelevant.HasValue)
            parts.Add($"relevance={(analysis.IsRelevant.Value ? "on_topic" : "off_topic")}");

        return parts.Count > 0 ? "JEV_COGNITIVE_GUIDE: " + string.Join(" | ", parts) : null;
    }

    private async Task<LayaAnalysis> AnalyzeWithLayaAsync(
        ChatRequestState state,
        IReadOnlyList<AssetDto> assets,
        CancellationToken ct)
    {
        string userMsg = state.UserMessage?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userMsg))
            return new LayaAnalysis();

        var prevParams = state.PreviousParameters?.Clone() ?? new ExtractedReportParametersDto();

        // 1. Deterministic Reset / Clear Request (C# Exact Match)
        if (IsResetRequest(userMsg))
        {
            return new LayaAnalysis(
                LayaIntent: "reset_options",
                IsRelevant: true,
                ActiveAsset: null,
                TargetCategory: null,
                ParsedTimeframe: null,
                AvailableCategories: Array.Empty<string>(),
                ActionToTake: SuggestedActions.SelectAsset);
        }

        // 2. Asset Matching & Asset Change Detection
        var assetMatch = AssetMatcher.Match(userMsg, assets);
        var matchedAsset = assetMatch.Asset;
        bool assetChanged = matchedAsset != null && prevParams.HasAsset && matchedAsset.AssetId != prevParams.AssetId;
        bool assetSelectedInThisTurn = matchedAsset != null;

        var activeAsset = matchedAsset ?? (prevParams.HasAsset ? assets.FirstOrDefault(a => a.AssetId == prevParams.AssetId) : null);
        var availableCategories = activeAsset != null
            ? await _telemetryRepo.GetSignalCategoriesByAssetAsync(activeAsset.AssetId, ct)
            : (IReadOnlyList<string>)Array.Empty<string>();

        // 3. Category matching (no regex, direct name / UI chip match)
        // If an asset was matched in the message, remove asset name to see what remains
        string remainingQuery = userMsg;
        if (matchedAsset != null)
        {
            int idx = remainingQuery.IndexOf(matchedAsset.Name, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                remainingQuery = remainingQuery.Remove(idx, matchedAsset.Name.Length).Trim();
            }
        }

        string? matchedCategory = null;
        if (!string.IsNullOrWhiteSpace(remainingQuery) && availableCategories.Count > 0)
        {
            var catMatch = CategoryMatcher.Match(remainingQuery, availableCategories);
            if (catMatch.IsMatch)
            {
                matchedCategory = catMatch.Category;
            }
            else if (_layaClient.IsEnabled && remainingQuery.Length >= 3 && !TimeframeParser.Parse(remainingQuery, allowBareNumber: false).Found)
            {
                try
                {
                    var jevCat = await _layaClient.AskAsync(
                        remainingQuery,
                        "Which equipment monitoring category is the user asking about?",
                        "choice",
                        choices: availableCategories.ToArray(),
                        ct);

                    if (jevCat != null && jevCat.Probability >= 0.70 && availableCategories.Contains(jevCat.AsString))
                    {
                        matchedCategory = jevCat.AsString;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Laya category classification skipped.");
                }
            }
        }

        string? targetCategory;
        if (assetChanged || (assetSelectedInThisTurn && matchedCategory == null))
        {
            targetCategory = matchedCategory;
        }
        else
        {
            targetCategory = matchedCategory ?? prevParams.Category;
        }

        // 4. Timeframe Parsing
        var timeResult = TimeframeParser.Parse(userMsg, allowBareNumber: true, utcNow: DateTime.UtcNow);
        var targetTime = timeResult.Found
            ? timeResult
            : (assetChanged || assetSelectedInThisTurn ? null : (prevParams.HasTime ? new TimeframeResult(prevParams.TimeRange, prevParams.FromDate, prevParams.ToDate, null) : null));

        // 5. Intent Classification via Jev (~25ms)
        string intent = "configure_report";
        if (matchedCategory == null && !timeResult.Found && !assetSelectedInThisTurn)
        {
            try
            {
                var intentRes = await _layaClient.AskAsync(
                    userMsg,
                    "What is the user trying to do?",
                    "choice",
                    choices: new[] { "greeting", "reset_options", "confirm_queue", "configure_report", "general_question" },
                    ct);
                if (intentRes?.AsString != null)
                {
                    intent = intentRes.AsString.ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Laya intent classification skipped.");
            }
        }

        // 6. Relevance Guardrail via Jev
        bool isRelevant = true;
        if (activeAsset == null && targetCategory == null && (targetTime == null || !targetTime.Found))
        {
            try
            {
                var topicRes = await _layaClient.AskAsync(
                    userMsg,
                    "Is this message relevant to industrial plant machinery, sensors, telemetry, equipment monitoring, report generation, or asking about available assets/catalog?",
                    "noul",
                    choices: null,
                    ct);
                isRelevant = topicRes?.AsBoolean ?? true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Laya relevance check skipped.");
            }
        }

        // 7. Deduce Action To Take
        string actionToTake =
            activeAsset == null ? SuggestedActions.SelectAsset :
            string.IsNullOrWhiteSpace(targetCategory) ? SuggestedActions.SelectCategory :
            (targetTime == null || !targetTime.Found) ? SuggestedActions.SelectTimeframe :
            SuggestedActions.ConfirmQueue;

        _logger.LogInformation(
            "Jev cognitive pre-processor: intent={Intent}, asset={Asset}, category={Category}, time={Time}, action={Action}, isRelevant={IsRelevant}",
            intent, activeAsset?.Name, targetCategory, targetTime?.TimeRange, actionToTake, isRelevant);

        return new LayaAnalysis(
            LayaIntent: intent,
            IsRelevant: isRelevant,
            ActiveAsset: activeAsset,
            TargetCategory: targetCategory,
            ParsedTimeframe: targetTime,
            AvailableCategories: availableCategories,
            ActionToTake: actionToTake);
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

    private static (DateTime From, DateTime To)? ResolveTimeframeToUtc(
        TimeframeResult? timeResult, 
        ExtractedReportParametersDto? p, 
        DateTime? maxAnchorTime = null)
    {
        string? fromStr = timeResult?.FromDate ?? p?.FromDate;
        string? toStr = timeResult?.ToDate ?? p?.ToDate;
        string? rangeStr = timeResult?.TimeRange ?? p?.TimeRange;

        if (!string.IsNullOrWhiteSpace(fromStr) &&
            DateTime.TryParse(fromStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var f))
        {
            DateTime t = maxAnchorTime ?? DateTime.UtcNow;
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
            var now = maxAnchorTime ?? DateTime.UtcNow;
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
        IReadOnlyList<string>? availableCategories,
        CancellationToken ct,
        ExtractedReportParametersDto? promptStateParams = null)
    {
        string userPrompt = PromptContextBuilder.Build(
            state.UserMessage, promptStateParams ?? state.PreviousParameters, state.History, DateTime.UtcNow, note);

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
        return (reply, ChatResponseSynchronizer.Sync(reply, state.PreviousParameters, assets, state.UserMessage, availableCategories));
    }

    private async Task<string> BuildSystemPromptAsync(IReadOnlyList<AssetDto> assets, CancellationToken ct)
    {
        string template = await _promptProvider.GetPromptAsync("ExtractorPrompt", ct);
        string catalog = assets.Count == 0
            ? "(none)"
            : string.Join(" | ", assets.Select(a => $"{a.AssetId}: {a.Name}"));

        return template.Replace("{AssetCatalog}", catalog);
    }

    private sealed record LayaAnalysis(
        string? LayaIntent = null,
        bool? IsRelevant = null,
        AssetDto? ActiveAsset = null,
        string? TargetCategory = null,
        TimeframeResult? ParsedTimeframe = null,
        IReadOnlyList<string>? AvailableCategories = null,
        string? ActionToTake = null)
    {
        public IReadOnlyList<string> AvailableCategories { get; init; } = AvailableCategories ?? Array.Empty<string>();
    }
}
