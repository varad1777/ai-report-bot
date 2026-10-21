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

        // ── 1. Deterministic Entity Extraction (Asset -> Category -> Timeframe) ──
        var analysis = await ExtractTurnEntitiesAsync(state, assets, ct);
        var availableCategories = analysis.AvailableCategories;

        // Compute pre-grounded state parameters to pass in prompt's STATE: line
        var promptStateParams = state.PreviousParameters?.Clone() ?? new ExtractedReportParametersDto();
        if (analysis.ActiveAsset != null)
        {
            promptStateParams.AssetId = analysis.ActiveAsset.AssetId;
            promptStateParams.AssetName = analysis.ActiveAsset.Name;
            promptStateParams.Category = analysis.TargetCategory;
            promptStateParams.TimeRange = analysis.ParsedTimeframe?.TimeRange;
            promptStateParams.FromDate = analysis.ParsedTimeframe?.FromDate;
            promptStateParams.ToDate = analysis.ParsedTimeframe?.ToDate;
        }

        string? categoryNote = availableCategories.Count > 0
            ? $"AVAILABLE_CATEGORIES: [{string.Join(", ", availableCategories.Select(c => $"\"{c}\""))}]"
            : null;

        // ── 2. Deep-Path: Ollama Qwen for Conversational Dialogue & JSON Contract (1-2s) ──
        string systemPrompt = await BuildSystemPromptAsync(assets, ct);

        var (reply, turn) = await AskAsync(state, systemPrompt, assets, note: categoryNote, availableCategories, ct, promptStateParams);

        // One corrective retry when reply is empty or invented invalid schema parameters.
        string? problem = string.IsNullOrWhiteSpace(reply?.ReplyMessage)
            ? "replyMessage was empty. Write a helpful reply."
            : turn?.Problem;

        if (problem != null)
        {
            _logger.LogInformation("Retrying LLM call once. Problem: {Problem}", problem);
            string retryNote = string.IsNullOrWhiteSpace(categoryNote) ? problem : $"{categoryNote} | RETRY_PROBLEM: {problem}";
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
                            turn.Parameters.FromDate = anchoredPeriod.Value.From.ToString("yyyy-MM-ddTHH:mm:ssZ");
                            turn.Parameters.ToDate = anchoredPeriod.Value.To.ToString("yyyy-MM-ddTHH:mm:ssZ");
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

        state.Reply = (turn.HarmonizedReply ?? reply.ReplyMessage).Trim();
        state.IsOnTopic = turn.IsOnTopic;
        state.IsComplete = turn.IsComplete;
        state.SuggestedAction = turn.SuggestedAction;
        state.SuggestedOptions = turn.SuggestedOptions.ToList();
        state.ExtractedParameters = turn.Parameters;
    }

    private async Task<PreProcessedEntities> ExtractTurnEntitiesAsync(
        ChatRequestState state,
        IReadOnlyList<AssetDto> assets,
        CancellationToken ct)
    {
        string userMsg = state.UserMessage?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userMsg))
            return new PreProcessedEntities();

        var prevParams = state.PreviousParameters?.Clone() ?? new ExtractedReportParametersDto();

        // 1. Deterministic Reset / Clear Request (C# Exact Match)
        if (IsResetRequest(userMsg))
        {
            return new PreProcessedEntities(
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

        bool isChangeAsset = IsChangeAssetRequest(userMsg);
        bool isChangeCategory = IsChangeCategoryRequest(userMsg);

        var activeAsset = matchedAsset ?? ((isChangeAsset && matchedAsset == null) ? null : (prevParams.HasAsset ? assets.FirstOrDefault(a => a.AssetId == prevParams.AssetId) : null));
        var availableCategories = activeAsset != null
            ? await _telemetryRepo.GetSignalCategoriesByAssetAsync(activeAsset.AssetId, ct)
            : (IReadOnlyList<string>)Array.Empty<string>();

        // 3. Category matching (no regex, direct name / UI chip match with typo tolerance)
        // If an asset was matched in this turn, exclude its words so asset tokens never falsely match category
        IEnumerable<string>? excludeTokens = matchedAsset != null
            ? AssetMatcher.Tokenize(matchedAsset.Name)
            : null;

        string? matchedCategory = null;
        if (availableCategories.Count > 0)
        {
            var catMatch = CategoryMatcher.Match(userMsg, availableCategories, excludeTokens);
            if (catMatch.IsMatch)
            {
                matchedCategory = catMatch.Category;
            }
        }

        string? targetCategory;
        if (assetChanged || (assetSelectedInThisTurn && matchedCategory == null) || (isChangeCategory && matchedCategory == null))
        {
            // When an asset is changed or newly selected without an explicit category, or user asked to change category, reset category
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
            : (assetChanged || assetSelectedInThisTurn || isChangeCategory ? null : (prevParams.HasTime ? new TimeframeResult(prevParams.TimeRange, prevParams.FromDate, prevParams.ToDate, null) : null));

        // 5. Deduce Action To Take
        string actionToTake =
            activeAsset == null ? SuggestedActions.SelectAsset :
            string.IsNullOrWhiteSpace(targetCategory) ? SuggestedActions.SelectCategory :
            (targetTime == null || !targetTime.Found) ? SuggestedActions.SelectTimeframe :
            SuggestedActions.ConfirmQueue;

        _logger.LogInformation(
            "Deterministic pre-processor: asset={Asset}, category={Category}, time={Time}, action={Action}",
            activeAsset?.Name, targetCategory, targetTime?.TimeRange, actionToTake);

        return new PreProcessedEntities(
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

    private static bool IsChangeAssetRequest(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var words = message.ToLowerInvariant().Split(new[] { ' ', ',', '.', '!', '?', '\'', '"' }, StringSplitOptions.RemoveEmptyEntries);
        bool hasChangeWord = words.Any(w => w is "change" or "switch" or "different" or "another" or "other" or "pick" or "select" or "choose" or "new");
        bool hasAssetWord = words.Any(w => w is "asset" or "machine" or "motor" or "equipment"
            || (w.Length >= 4 && (EditDistance(w, "asset") <= 2 || EditDistance(w, "machine") <= 2 || EditDistance(w, "motor") <= 1)));
        return hasChangeWord && hasAssetWord;
    }

    private static bool IsChangeCategoryRequest(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var words = message.ToLowerInvariant().Split(new[] { ' ', ',', '.', '!', '?', '\'', '"' }, StringSplitOptions.RemoveEmptyEntries);
        bool hasChangeWord = words.Any(w => w is "change" or "switch" or "different" or "another" or "other" or "pick" or "select" or "choose" or "new");
        bool hasCategoryWord = words.Any(w => w is "category" or "categories" or "signal" or "signals" or "metric" or "metrics" or "sensor" or "sensors"
            || (w.Length >= 5 && (EditDistance(w, "category") <= 2 || EditDistance(w, "signal") <= 1)));
        return hasChangeWord && hasCategoryWord;
    }

    private static int EditDistance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            Array.Copy(cur, prev, cur.Length);
        }
        return cur[b.Length];
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

    private sealed record PreProcessedEntities(
        AssetDto? ActiveAsset = null,
        string? TargetCategory = null,
        TimeframeResult? ParsedTimeframe = null,
        IReadOnlyList<string>? AvailableCategories = null,
        string? ActionToTake = null)
    {
        public IReadOnlyList<string> AvailableCategories { get; init; } = AvailableCategories ?? Array.Empty<string>();
    }
}
