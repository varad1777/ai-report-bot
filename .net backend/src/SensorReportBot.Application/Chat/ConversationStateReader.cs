namespace SensorReportBot.Application.Chat;

using System.Text.Json;
using SensorReportBot.Domain.Entities;

/// <summary>
/// Restores the report configuration (asset + timeframe) from the metadata that
/// PersistAssistantReplyStep stores with every assistant message. The database, not the LLM's
/// context window, is the source of truth for "what was already chosen" - this is what lets the
/// prompt carry only the last 2 turns.
/// </summary>
public static class ConversationStateReader
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private sealed class Meta
    {
        public ExtractedReportParametersDto? ExtractedParameters { get; set; }
    }

    public static ExtractedReportParametersDto? Read(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<Meta>(metadataJson, Options)?.ExtractedParameters;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
