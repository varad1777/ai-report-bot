namespace SensorReportBot.Domain.Entities;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

public class ChatMessageEntity
{
    public long Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty; // "user" or "assistant"
    public string Content { get; set; } = string.Empty;
    public string? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ExtractedReportParametersDto
{
    public int? AssetId { get; set; }
    public string? AssetName { get; set; }
    public List<int>? SignalIds { get; set; }

    /// <summary>Relative duration code e.g. "5d", "24h". Use when user says "last 5 days".</summary>
    public string? TimeRange { get; set; }

    /// <summary>Absolute start datetime ISO-8601 UTC. Use when user specifies a concrete date like "24 sept".</summary>
    public string? FromDate { get; set; }

    /// <summary>Absolute end datetime ISO-8601 UTC. Defaults to current time when only FromDate is given.</summary>
    public string? ToDate { get; set; }

    public string? Mode { get; set; }

    [JsonIgnore]
    public bool HasAsset => AssetId is > 0;
    [JsonIgnore]
    public bool HasTime => !string.IsNullOrWhiteSpace(TimeRange) || !string.IsNullOrWhiteSpace(FromDate);

    public ExtractedReportParametersDto Clone() => new()
    {
        AssetId = AssetId,
        AssetName = AssetName,
        SignalIds = SignalIds == null ? null : new List<int>(SignalIds),
        TimeRange = TimeRange,
        FromDate = FromDate,
        ToDate = ToDate,
        Mode = Mode
    };
}

/// <summary>Values of the <c>suggestedAction</c> field shared by the LLM contract and the React UI.</summary>
public static class SuggestedActions
{
    public const string SelectAsset = "select_asset";
    public const string SelectTimeframe = "select_timeframe";
    public const string ConfirmQueue = "confirm_queue";
    public const string None = "none";
}

public class ChatRequestState
{
    public string UserMessage { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public bool IsOnTopic { get; set; } = true;
    public bool IsComplete { get; set; } = false;

    /// <summary>The assistant reply. Always authored by the LLM - never a canned string.</summary>
    public string? Reply { get; set; }

    /// <summary>"select_asset" | "select_timeframe" | "confirm_queue" | "none"</summary>
    public string? SuggestedAction { get; set; }

    /// <summary>Chip labels for the UI. Filled by the server from the database, never by the LLM.</summary>
    public List<string>? SuggestedOptions { get; set; }

    // Pipeline step state

    /// <summary>Only the last 2 conversation turns (4 messages), loaded before the current message is saved.</summary>
    public IReadOnlyList<ChatMessageEntity>? History { get; set; }

    /// <summary>Accumulated report configuration restored from the last assistant message's saved metadata.</summary>
    public ExtractedReportParametersDto? PreviousParameters { get; set; }

    /// <summary>Report configuration after this turn (LLM output validated and grounded by the server).</summary>
    public ExtractedReportParametersDto? ExtractedParameters { get; set; }
}
