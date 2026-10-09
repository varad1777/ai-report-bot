namespace SensorReportBot.Infrastructure.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SensorReportBot.Application.DTOs;
using SensorReportBot.Application.Interfaces;
using SensorReportBot.Application.Workflows;
using SensorReportBot.Domain.Entities;

public class ChatService : IChatService
{
    private readonly ChatWorkflow _chatWorkflow;
    private readonly IChatHistoryRepository _historyRepo;
    private readonly ILogger<ChatService> _logger;

    public ChatService(
        ChatWorkflow chatWorkflow,
        IChatHistoryRepository historyRepo,
        ILogger<ChatService> logger)
    {
        _chatWorkflow = chatWorkflow;
        _historyRepo = historyRepo;
        _logger = logger;
    }

    public async Task<ConversationalChatResultDto> ProcessChatMessageAsync(string sessionId, string userMessage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return new ConversationalChatResultDto
            {
                IsOnTopic = false,
                IsComplete = false,
                ReplyMessage = "Please provide a valid question or request regarding sensor reports."
            };
        }

        var state = new ChatRequestState
        {
            SessionId = sessionId,
            UserMessage = userMessage
        };

        try
        {
            // Execute full ChatWorkflow Pipeline (GuardStep -> SaveUserMessageStep -> LoadHistoryStep -> IntentExtractionStep -> PersistAssistantReplyStep)
            await _chatWorkflow.ExecuteAsync(state, ct);

            return new ConversationalChatResultDto
            {
                IsOnTopic = state.IsOnTopic,
                IsComplete = state.IsComplete,
                ReplyMessage = state.Reply ?? "I can help with your sensor report. Which asset and timeframe would you like?",
                ExtractedParameters = state.ExtractedParameters,
                SuggestedAction = state.SuggestedAction,
                SuggestedOptions = state.SuggestedOptions,
                JobId = state.JobId,
                InputTokens = state.InputTokens,
                OutputTokens = state.OutputTokens,
                TotalTokens = state.TotalTokens,
                DecisionModel = state.DecisionModel
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing ChatWorkflow pipeline for session {SessionId}", sessionId);
        }

        return new ConversationalChatResultDto
        {
            IsOnTopic = false,
            IsComplete = false,
            ReplyMessage = "I encountered an issue processing your prompt. Please ask about sensor reports or specify target asset parameters."
        };
    }

    public Task<IReadOnlyList<ChatMessageEntity>> GetHistoryAsync(string sessionId, CancellationToken ct = default)
    {
        return _historyRepo.GetRecentHistoryAsync(sessionId, limit: 50, ct);
    }

    public Task ClearHistoryAsync(string sessionId, CancellationToken ct = default)
    {
        return _historyRepo.ClearHistoryAsync(sessionId, ct);
    }
}
