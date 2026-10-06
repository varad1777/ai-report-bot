namespace SensorReportBot.Application.Steps;

using System;
using System.Collections.Generic;
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
/// The "brain" of the pipeline: ONE LLM call per user message. The model authors the reply and
/// extracts the report parameters; the server then syncs those parameters with the database
/// (<see cref="ChatResponseSynchronizer"/>) and fills the UI chips. No regex fast-paths, no canned
/// replies: if the model cannot answer, an <see cref="LlmUnavailableException"/> is raised.
/// </summary>
public class IntentExtractionStep : IChatStep
{
    private readonly ILlmService _llmService;
    private readonly IPromptProvider _promptProvider;
    private readonly ITelemetryRepository _telemetryRepo;
    private readonly ILogger<IntentExtractionStep> _logger;

    public IntentExtractionStep(
        ILlmService llmService,
        IPromptProvider promptProvider,
        ITelemetryRepository telemetryRepo,
        ILogger<IntentExtractionStep> logger)
    {
        _llmService = llmService;
        _promptProvider = promptProvider;
        _telemetryRepo = telemetryRepo;
        _logger = logger;
    }

    public async Task RunAsync(ChatRequestState state, CancellationToken ct)
    {
        var assets = await _telemetryRepo.GetAssetsAsync(ct);
        string systemPrompt = await BuildSystemPromptAsync(assets, ct);

        var (reply, turn) = await AskAsync(state, systemPrompt, assets, note: null, ct);

        // One corrective retry when the model invented an asset / gave an unusable timeframe /
        // returned no text. The retry reply is again written by the model.
        string? problem = string.IsNullOrWhiteSpace(reply?.ReplyMessage)
            ? "replyMessage was empty. Write a helpful reply."
            : turn?.Problem;

        if (problem != null)
        {
            _logger.LogInformation("Retrying LLM call once. Problem: {Problem}", problem);
            var (retryReply, retryTurn) = await AskAsync(state, systemPrompt, assets, problem, ct);
            if (!string.IsNullOrWhiteSpace(retryReply?.ReplyMessage))
            {
                reply = retryReply;
                turn = retryTurn;
            }
        }

        if (reply == null || turn == null || string.IsNullOrWhiteSpace(reply.ReplyMessage))
            throw new LlmUnavailableException("The language model did not return a usable reply.");

        state.Reply = reply.ReplyMessage.Trim();
        state.IsOnTopic = turn.IsOnTopic;
        state.IsComplete = turn.IsComplete;
        state.SuggestedAction = turn.SuggestedAction;
        state.SuggestedOptions = turn.SuggestedOptions.ToList();
        state.ExtractedParameters = turn.Parameters;
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

    /// <summary>
    /// Static rules + asset catalog. Kept identical between requests so Ollama / llama.cpp can reuse
    /// the cached KV prefix instead of re-processing it on every message.
    /// </summary>
    private async Task<string> BuildSystemPromptAsync(IReadOnlyList<AssetDto> assets, CancellationToken ct)
    {
        string template = await _promptProvider.GetPromptAsync("ExtractorPrompt", ct);
        string catalog = assets.Count == 0
            ? "(none)"
            : string.Join(" | ", assets.Select(a => $"{a.AssetId}: {a.Name}"));

        return template.Replace("{AssetCatalog}", catalog);
    }
}
