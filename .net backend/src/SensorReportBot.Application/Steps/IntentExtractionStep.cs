namespace SensorReportBot.Application.Steps;



using System.Globalization;

using Microsoft.Extensions.Logging;

using SensorReportBot.Application.DTOs;

using SensorReportBot.Application.Interfaces;

using SensorReportBot.Domain.Entities;



/// <summary>

/// Uses Jev as the primary semantic decision engine. The backend owns validated state,

/// report queueing, and response text. Qwen is used only for unbounded period values

/// that cannot be represented by Jev's bounded probability questions.

/// </summary>

public sealed class IntentExtractionStep(

    IJevDecisionService jev,

    ILlmService llm,

    ITelemetryRepository telemetry,

    IPdfJobQueue pdfJobs,

    ILogger<IntentExtractionStep> logger) : IChatStep

{

    private const double DecisionThreshold = 0.55;

    private const double StrongSelectionThreshold = 0.45;

    private const double MinimumSelectionThreshold = 0.25;

    private const double SelectionMargin = 0.05;



    private static readonly IReadOnlyDictionary<string, string> StandardPeriods =

        new Dictionary<string, string>

        {

            ["period_24h"] = "24h",

            ["period_5d"] = "5d",

            ["period_7d"] = "7d",

            ["period_14d"] = "14d",

            ["period_30d"] = "30d",

        };



    public async Task RunAsync(ChatRequestState state, CancellationToken ct)

    {

        var assets = await telemetry.GetAssetsAsync(ct);

        var current = state.PreviousParameters?.Clone() ?? new ExtractedReportParametersDto { Mode = "raw" };

        var questions = CreateQuestions(assets);



        JevDecisionResult decision;

        try

        {

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

            deadline.CancelAfter(TimeSpan.FromSeconds(6));

            decision = await jev.DecideAsync(new

            {

                message = state.UserMessage,

                conversationStage = CurrentStage(current),

                supportedTask = "Configure and queue industrial asset telemetry PDF reports."

            }, questions, deadline.Token);

            state.InputTokens = decision.InputTokens;

            state.OutputTokens = 0;

            state.TotalTokens = decision.InputTokens;

            state.DecisionModel = "jevos-v4/openvino-int8";

        }

        catch (Exception ex) when (!ct.IsCancellationRequested)

        {

            logger.LogWarning(ex, "Jev decision failed; using compact Qwen fallback.");

            await ResolveWithQwenAsync(state, current, assets, ct);

            return;

        }



        double P(string key) => decision.Probabilities.TryGetValue(key, out var value) ? value : 0;



        if (P("off_topic") >= DecisionThreshold && P("off_topic") > P("report"))
        {
            state.IsOnTopic = false;
            state.IsComplete = false;
            state.ExtractedParameters = state.PreviousParameters?.Clone();
            state.SuggestedAction = "none";
            state.SuggestedOptions = new();
            state.Reply = "I can help with industrial asset telemetry reports.";
            return;
        }

        if (P("cancel") >= DecisionThreshold)

        {

            SetSelectionRequest(state, new ExtractedReportParametersDto { Mode = "raw" }, assets,

                "Report selection cleared. Which machine or asset would you like a report for?");

            return;

        }



        var assetChoices = assets
            .Select(asset => new { Asset = asset, Probability = P($"asset_{asset.AssetId}") })
            .OrderByDescending(item => item.Probability)
            .ToList();
        var assetChoice = assetChoices.FirstOrDefault();
        double assetMargin = assetChoice is null ? 0 :
            assetChoice.Probability - (assetChoices.Count > 1 ? assetChoices[1].Probability : 0);
        bool assetConfident = assetChoice is not null && IsConfident(assetChoice.Probability, assetMargin);

        if (assetConfident)

        {

            current.AssetId = assetChoice!.Asset.AssetId;

            current.AssetName = assetChoice.Asset.Name;

        }



        var periodChoices = StandardPeriods
            .Select(item => new { item.Key, Value = item.Value, Probability = P(item.Key) })
            .OrderByDescending(item => item.Probability)
            .ToList();
        var periodChoice = periodChoices[0];
        double periodMargin = periodChoice.Probability - periodChoices[1].Probability;
        bool periodConfident = IsConfident(periodChoice.Probability, periodMargin);

        if (periodConfident)

        {

            current.TimeRange = periodChoice.Value;

            current.FromDate = null;

            current.ToDate = null;

        }



        bool reportRelated = P("report") >= DecisionThreshold ||

            P("confirm") >= DecisionThreshold ||

            P("change_period") >= 0.30 || P("change_asset") >= 0.15 || assetConfident ||

            periodConfident || P("report") >= 0.30;



        if (!reportRelated)

        {

            state.IsOnTopic = false;

            state.IsComplete = false;

            state.ExtractedParameters = state.PreviousParameters?.Clone();

            state.SuggestedAction = "none";

            state.SuggestedOptions = new();

            state.Reply = "I can help with industrial asset telemetry reports.";

            return;

        }



        bool needsUnboundedPeriod = P("change_period") >= DecisionThreshold &&
            !periodConfident;
        bool unresolvedAsset = current.AssetId is null && P("report") >= 0.30 && !assetConfident;
        bool ambiguousAssetChange = P("change_asset") >= 0.15 &&
            !assetConfident;

        bool ambiguousContinuation = current.AssetId is > 0 && HasPeriod(current) &&
            P("report") >= DecisionThreshold && P("confirm") < DecisionThreshold &&
            !assetConfident && !periodConfident &&
            P("change_asset") >= 0.15;

        if (needsUnboundedPeriod || unresolvedAsset || ambiguousAssetChange || ambiguousContinuation)

        {

            await ResolveWithQwenAsync(state, current, assets, ct);

            return;

        }



        if (P("confirm") >= DecisionThreshold && P("change_period") < DecisionThreshold &&
            !periodConfident && current.AssetId is > 0 && HasPeriod(current))

        {

            await QueueAsync(state, current, ct);

            return;

        }



        if (current.AssetId is null || current.AssetId <= 0)

        {

            SetSelectionRequest(state, current, assets,

                "Which machine or asset would you like a report for?");

            return;

        }



        if (!HasPeriod(current) || P("change_period") >= DecisionThreshold && !periodConfident)

        {

            state.IsOnTopic = true;

            state.IsComplete = false;

            state.ExtractedParameters = current;

            state.SuggestedAction = "select_timeframe";

            state.SuggestedOptions = StandardPeriods.Values.ToList();

            state.Reply = $"What reporting period should I use for {current.AssetName}?";

            return;

        }



        state.IsOnTopic = true;

        state.IsComplete = true;

        state.ExtractedParameters = current;

        state.SuggestedAction = "confirm_queue";

        state.SuggestedOptions = new() { "Yes, Queue PDF Report", "Change Options" };

        state.Reply = $"Report configured for {current.AssetName} covering {PeriodLabel(current)}. Generate the PDF now?";

    }



    private static Dictionary<string, string> CreateQuestions(IEnumerable<AssetDto> assets)

    {

        var questions = new Dictionary<string, string>

        {

            ["report"] = "Is the user asking for, configuring, changing, or discussing an industrial telemetry PDF report?",
            ["off_topic"] = "Is the message unrelated to industrial assets, telemetry, maintenance, reports, or the current report conversation?",


            ["confirm"] = "Is the user confirming that the configured PDF report should be generated or queued now?",

            ["cancel"] = "Is the user cancelling, clearing, resetting, or abandoning the current report selection?",
            ["change_asset"] = "Is the user selecting a different machine or changing the current asset?",


            ["change_period"] = "Is the user selecting or changing the reporting duration or date range?",

            ["period_24h"] = "Does the latest user message request a one-day, daily, yesterday, today, or 24-hour period? Ignore currentState when answering.",

            ["period_5d"] = "Does the latest user message request a five-day period? Ignore currentState when answering.",

            ["period_7d"] = "Does the latest user message request a seven-day, one-week, or weekly period? Ignore currentState when answering.",

            ["period_14d"] = "Does the latest user message request a fourteen-day, two-week, or fortnight period? Ignore currentState when answering.",

            ["period_30d"] = "Does the latest user message request a thirty-day, one-month, or monthly period? Ignore currentState when answering.",

        };

        foreach (var asset in assets)

            questions[$"asset_{asset.AssetId}"] = $"Does the latest user message name the monitored asset '{asset.Name}', including abbreviations or spelling mistakes? Ignore currentState when answering.";

        return questions;

    }

    private static bool IsConfident(double probability, double margin) =>
        probability >= StrongSelectionThreshold ||
        probability >= MinimumSelectionThreshold && margin >= SelectionMargin;



    private async Task ResolveWithQwenAsync(

        ChatRequestState state,

        ExtractedReportParametersDto current,

        IReadOnlyList<AssetDto> assets,

        CancellationToken ct)

    {

        const string systemPrompt = """
            Extract a telemetry report request and correct spelling mistakes.
            Return exactly one JSON object with these keys:
            {"isOnTopic":true,"assetId":null,"assetName":null,"timeRange":null,"fromDate":null,"toDate":null}
            isOnTopic MUST be true when the message requests a report, names or changes an
            available asset, selects a time period, confirms generation, or asks which assets
            are available. It is false only for unrelated subjects. Replace the null asset
            fields only when a listed asset is identified, and copy the exact assetId and
            assetName from availableAssets. Preserve currentState values unless the message
            changes them. Normalize durations to Nh or Nd; for example, five days is 5d,
            48 hours is 48h, two weeks is 14d, and monthly is 30d. Keep fromDate and toDate
            null unless the user provides explicit calendar dates. When the user asks which
            assets are available without naming one, keep assetId and assetName null; never
            choose the first available asset as a default.
            """;

        string context = System.Text.Json.JsonSerializer.Serialize(new

        {

            message = state.UserMessage,

            currentState = current,

            availableAssets = assets.Select(asset => new { asset.AssetId, asset.Name }),

            utcDate = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),

        });

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

        deadline.CancelAfter(TimeSpan.FromSeconds(25));

        var result = await llm.GenerateJsonResponseAsync<FallbackExtraction>(systemPrompt, context, deadline.Token);

        state.DecisionModel = "qwen2.5:1.5b-fallback";

        if (result is null)

        {

            state.IsOnTopic = true;

            state.IsComplete = false;

            state.ExtractedParameters = current;

            state.SuggestedAction = current.AssetId is > 0 ? "select_timeframe" : "select_asset";

            state.SuggestedOptions = current.AssetId is > 0

                ? StandardPeriods.Values.ToList()

                : assets.Select(asset => asset.Name).ToList();

            state.Reply = current.AssetId is > 0

                ? $"What reporting period should I use for {current.AssetName}?"

                : "Which machine or asset would you like a report for?";

            return;

        }



        if (!result.IsOnTopic)
        {
            state.IsOnTopic = false;
            state.IsComplete = false;
            state.ExtractedParameters = state.PreviousParameters?.Clone();
            state.SuggestedAction = "none";
            state.SuggestedOptions = new();
            state.Reply = "I can help with industrial asset telemetry reports.";
            return;
        }

        var extracted = result;

        var selected = assets.FirstOrDefault(asset => asset.AssetId == extracted.AssetId) ??

            assets.FirstOrDefault(asset => string.Equals(asset.Name, extracted.AssetName, StringComparison.OrdinalIgnoreCase));

        if (selected is not null)

        {

            current.AssetId = selected.AssetId;

            current.AssetName = selected.Name;

        }

        if (!string.IsNullOrWhiteSpace(extracted.TimeRange))

        {

            current.TimeRange = extracted.TimeRange;

            current.FromDate = current.ToDate = null;

        }

        if (!string.IsNullOrWhiteSpace(extracted.FromDate)) current.FromDate = extracted.FromDate;

        if (!string.IsNullOrWhiteSpace(extracted.ToDate)) current.ToDate = extracted.ToDate;



        state.IsOnTopic = true;

        state.IsComplete = current.AssetId is > 0 && HasPeriod(current);

        state.ExtractedParameters = current;

        state.SuggestedAction = state.IsComplete ? "confirm_queue" : current.AssetId is > 0 ? "select_timeframe" : "select_asset";

        state.SuggestedOptions = state.IsComplete

            ? new() { "Yes, Queue PDF Report", "Change Options" }

            : current.AssetId is > 0 ? StandardPeriods.Values.ToList() : assets.Select(asset => asset.Name).ToList();

        state.Reply = state.IsComplete

            ? $"Report configured for {current.AssetName} covering {PeriodLabel(current)}. Generate the PDF now?"

            : current.AssetId is > 0
                ? $"What reporting period should I use for {current.AssetName}?"
                : "Which machine or asset would you like a report for?";

    }



    private async Task QueueAsync(ChatRequestState state, ExtractedReportParametersDto current, CancellationToken ct)

    {

        DateTime to = DateTime.UtcNow;

        DateTime from;

        if (DateTimeOffset.TryParse(current.FromDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedFrom))

        {

            from = parsedFrom.UtcDateTime;

            if (DateTimeOffset.TryParse(current.ToDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedTo))

                to = parsedTo.UtcDateTime;

        }

        else if (!TryResolveDuration(current.TimeRange, to, out from))

        {

            state.IsOnTopic = true;

            state.IsComplete = false;

            state.ExtractedParameters = current;

            state.SuggestedAction = "select_timeframe";

            state.SuggestedOptions = StandardPeriods.Values.ToList();

            state.Reply = "Please provide a valid reporting period.";

            return;

        }



        var signals = await telemetry.GetSignalsByAssetAsync(current.AssetId!.Value, ct);

        var request = new ReportRequestDto

        {

            AssetId = current.AssetId.Value,

            SignalIds = current.SignalIds is { Count: > 0 } ? current.SignalIds : signals.Take(6).Select(item => item.SignalId).ToList(),

            From = from,

            To = to,

            IncludeEvents = true,

            IncludeAlerts = true,

            IncludeInsights = true,

            IncludeCharts = true,

            IncludeFullRawData = true,

        };

        var job = await pdfJobs.EnqueueAsync(request, current.AssetName ?? $"Asset-{current.AssetId}", ct);

        state.IsOnTopic = true;

        state.IsComplete = true;

        state.JobId = job.Id;

        state.ExtractedParameters = current;

        state.SuggestedAction = "confirm_queue";

        state.SuggestedOptions = new() { "Change Options" };

        state.Reply = $"Report for {current.AssetName} covering {PeriodLabel(current)} was queued as REP-{job.Id.ToString()[..8].ToUpperInvariant()}.";

    }



    private static bool TryResolveDuration(string? value, DateTime to, out DateTime from)

    {

        from = default;

        if (string.IsNullOrWhiteSpace(value) || value.Length < 2) return false;

        string number = value[..^1];

        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count <= 0) return false;

        char unit = char.ToLowerInvariant(value[^1]);

        if (unit == 'h') { from = to.AddHours(-count); return true; }

        if (unit == 'd') { from = to.AddDays(-count); return true; }

        return false;

    }



    private static bool HasPeriod(ExtractedReportParametersDto state) =>

        !string.IsNullOrWhiteSpace(state.TimeRange) || !string.IsNullOrWhiteSpace(state.FromDate);



    private static string CurrentStage(ExtractedReportParametersDto state) =>

        state.AssetId is null ? "select_asset" : !HasPeriod(state) ? "select_timeframe" : "confirm_queue";



    private static string PeriodLabel(ExtractedReportParametersDto state) =>

        !string.IsNullOrWhiteSpace(state.TimeRange) ? state.TimeRange :

        !string.IsNullOrWhiteSpace(state.ToDate) ? $"{state.FromDate} to {state.ToDate}" : state.FromDate ?? "the selected period";



    private static void SetSelectionRequest(

        ChatRequestState state,

        ExtractedReportParametersDto parameters,

        IEnumerable<AssetDto> assets,

        string reply)

    {

        state.IsOnTopic = true;

        state.IsComplete = false;

        state.ExtractedParameters = parameters;

        state.SuggestedAction = "select_asset";

        state.SuggestedOptions = assets.Select(asset => asset.Name).ToList();

        state.Reply = reply;

    }

    private sealed class FallbackExtraction
    {
        public bool IsOnTopic { get; set; }
        public int? AssetId { get; set; }
        public string? AssetName { get; set; }
        public string? TimeRange { get; set; }
        public string? FromDate { get; set; }
        public string? ToDate { get; set; }
    }

}


