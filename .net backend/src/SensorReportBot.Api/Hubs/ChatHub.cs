namespace SensorReportBot.Api.Hubs;

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SensorReportBot.Application.Exceptions;
using SensorReportBot.Application.Interfaces;

public class ChatHub : Hub
{
    private readonly IChatService _chatService;
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(IChatService chatService, ILogger<ChatHub> logger)
    {
        _chatService = chatService;
        _logger = logger;
    }

    public async Task SendMessage(string sessionId, string userMessage)
    {
        try
        {
            _logger.LogInformation("WebSocket Chat Message Received from Session {SessionId}: '{Message}'", sessionId, userMessage);
            
            var result = await _chatService.ProcessChatMessageAsync(sessionId, userMessage);

            // Push the response back over WebSocket to the client who sent the message.
            // "ReceiveChatResponse" must match the connection.on(...) listener in React.
            await Clients.Caller.SendAsync("ReceiveChatResponse", result);
        }
        catch (LlmUnavailableException ex)
        {
            _logger.LogWarning(ex, "LLM unavailable for session {SessionId}", sessionId);
            await Clients.Caller.SendAsync("ReceiveError", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling WebSocket SendMessage in ChatHub");
            await Clients.Caller.SendAsync("ReceiveError", ex.Message);
        }
    }

    public async Task GetHistory(string sessionId)
    {
        try
        {
            var history = await _chatService.GetHistoryAsync(sessionId);
            await Clients.Caller.SendAsync("ReceiveHistory", history);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching history in ChatHub");
        }
    }

    public async Task ClearHistory(string sessionId)
    {
        try
        {
            await _chatService.ClearHistoryAsync(sessionId);
            await Clients.Caller.SendAsync("ReceiveHistoryCleared", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing history in ChatHub");
        }
    }
}
