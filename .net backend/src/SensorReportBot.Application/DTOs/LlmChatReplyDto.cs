namespace SensorReportBot.Application.DTOs;

using System.Text.Json.Serialization;
using SensorReportBot.Domain.Entities;

/// <summary>
/// The exact JSON contract the LLM must output. Note there is deliberately NO suggestedOptions:
/// the server derives the chips from the database so the model never spends output tokens on lists.
/// </summary>
public class LlmChatReplyDto
{
    [JsonPropertyName("isOnTopic")]
    public bool IsOnTopic { get; set; } = true;

    [JsonPropertyName("isComplete")]
    public bool IsComplete { get; set; }

    [JsonPropertyName("replyMessage")]
    public string ReplyMessage { get; set; } = string.Empty;

    /// <summary>select_asset | select_timeframe | confirm_queue | none</summary>
    [JsonPropertyName("suggestedAction")]
    public string? SuggestedAction { get; set; }

    [JsonPropertyName("extractedParameters")]
    public ExtractedReportParametersDto? ExtractedParameters { get; set; }
}
