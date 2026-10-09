namespace SensorReportBot.Application.Interfaces;

using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SensorReportBot.Domain.Entities;

public class ConversationalChatResultDto
{
    [JsonPropertyName("isOnTopic")]
    public bool IsOnTopic { get; set; }

    [JsonPropertyName("isComplete")]
    public bool IsComplete { get; set; }

    [JsonPropertyName("replyMessage")]
    public string ReplyMessage { get; set; } = string.Empty;

    [JsonPropertyName("suggestedAction")]
    public string? SuggestedAction { get; set; } // "select_asset" | "select_timeframe" | "none"

    [JsonPropertyName("suggestedOptions")]
    public List<string>? SuggestedOptions { get; set; }

    [JsonPropertyName("extractedParameters")]
    public ExtractedReportParametersDto? ExtractedParameters { get; set; }

    [JsonPropertyName("jobId")]
    public System.Guid? JobId { get; set; }

    [JsonPropertyName("inputTokens")]
    public int? InputTokens { get; set; }

    [JsonPropertyName("outputTokens")]
    public int? OutputTokens { get; set; }

    [JsonPropertyName("totalTokens")]
    public int? TotalTokens { get; set; }

    [JsonPropertyName("decisionModel")]
    public string? DecisionModel { get; set; }
}

public interface IChatService
{
    Task<ConversationalChatResultDto> ProcessChatMessageAsync(string sessionId, string userMessage, CancellationToken ct = default);
    Task<IReadOnlyList<ChatMessageEntity>> GetHistoryAsync(string sessionId, CancellationToken ct = default);
    Task ClearHistoryAsync(string sessionId, CancellationToken ct = default);
}
