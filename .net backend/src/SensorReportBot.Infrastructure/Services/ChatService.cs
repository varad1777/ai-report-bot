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
            throw new ArgumentException("Message must not be empty.", nameof(userMessage));

        var state = new ChatRequestState
        {
            SessionId = sessionId,
            UserMessage = userMessage
        };

        try
        {
            // LoadHistoryStep -> IntentExtractionStep (single LLM call) -> SaveUserMessageStep -> PersistAssistantReplyStep
            await _chatWorkflow.ExecuteAsync(state, ct);
        }
        catch (Exception ex)
        {
            // No canned chatbot answer: let the caller (SignalR hub) report the failure.
            _logger.LogError(ex, "Chat pipeline failed for session {SessionId}", sessionId);
            throw;
        }

        return new ConversationalChatResultDto
        {
            IsOnTopic = state.IsOnTopic,
            IsComplete = state.IsComplete,
            ReplyMessage = state.Reply!, // guaranteed by IntentExtractionStep (it throws otherwise)
            ExtractedParameters = state.ExtractedParameters,
            SuggestedAction = state.SuggestedAction,
            SuggestedOptions = state.SuggestedOptions
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
