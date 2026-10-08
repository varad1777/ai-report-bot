namespace SensorReportBot.Api.Services;

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SensorReportBot.Api.Hubs;
using SensorReportBot.Application.Interfaces;

public class SignalRJobProgressNotifier : IJobProgressNotifier
{
    private readonly IHubContext<ChatHub> _hubContext;
    private readonly ILogger<SignalRJobProgressNotifier> _logger;

    public SignalRJobProgressNotifier(
        IHubContext<ChatHub> hubContext,
        ILogger<SignalRJobProgressNotifier> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task NotifyProgressAsync(
        string? sessionId,
        Guid jobId,
        int progressPercentage,
        string statusMessage,
        string status)
    {
        try
        {
            var updatePayload = new
            {
                jobId,
                progressPercentage,
                statusMessage,
                status
            };

            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                _logger.LogDebug(
                    "Pushing progress ({Percentage}%, '{Message}') to Session {SessionId} for Job {JobId}",
                    progressPercentage, statusMessage, sessionId, jobId);

                // Push specifically to the originating chat session group
                await _hubContext.Clients.Group(sessionId).SendAsync("ReceiveJobProgress", updatePayload);
            }
            else
            {
                _logger.LogDebug(
                    "Pushing broadcast progress ({Percentage}%, '{Message}') for Job {JobId}",
                    progressPercentage, statusMessage, jobId);

                // If no specific session was supplied, broadcast to all connected clients
                await _hubContext.Clients.All.SendAsync("ReceiveJobProgress", updatePayload);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push SignalR job progress update for Job {JobId}", jobId);
        }
    }
}
