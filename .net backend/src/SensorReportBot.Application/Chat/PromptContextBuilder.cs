namespace SensorReportBot.Application.Chat;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SensorReportBot.Domain.Entities;

/// <summary>
/// State-augmented memory. Instead of replaying a growing transcript, the user prompt is
///   NOW    - current UTC time (for "yesterday" / "last 5 days" maths)
///   STATE  - the report configuration already agreed, read from the database metadata
///   RECENT - only the last 2 turns, each line clipped
///   USER   - the new message
/// which keeps a typical request under ~250 input tokens regardless of conversation length.
/// The static rules + asset catalog live in the system prompt so the inference server can keep
/// that prefix in its KV cache between requests.
/// </summary>
public static class PromptContextBuilder
{
    private const int MaxUserLineChars = 200;
    private const int MaxAssistantLineChars = 160;
    private const int MaxCurrentMessageChars = 1000;

    public static string Build(
        string userMessage,
        ExtractedReportParametersDto? state,
        IReadOnlyList<ChatMessageEntity>? recentMessages,
        DateTime utcNow,
        string? systemNote = null)
    {
        var sb = new StringBuilder();

        sb.Append("NOW: ").AppendLine(utcNow.ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture));
        sb.Append("STATE: ").AppendLine(DescribeState(state));
        sb.AppendLine(DetermineStageAndGoal(state));

        if (recentMessages is { Count: > 0 })
        {
            var filteredMessages = FilterOnTopicHistory(recentMessages);
            if (filteredMessages.Count > 0)
            {
                sb.AppendLine("RECENT:");
                foreach (var m in filteredMessages.TakeLast(LoadLimit))
                {
                    bool isUser = string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase);
                    sb.Append(isUser ? "U: " : "A: ")
                      .AppendLine(Clip(m.Content, isUser ? MaxUserLineChars : MaxAssistantLineChars));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(systemNote))
            sb.Append("NOTE: ").AppendLine(systemNote);

        sb.Append("USER: ").Append(Clip(userMessage, MaxCurrentMessageChars));
        return sb.ToString();
    }

    private const int LoadLimit = 4;

    public static string DetermineStageAndGoal(ExtractedReportParametersDto? p)
    {
        if (p == null || !p.HasAsset)
            return "STAGE: SELECT_ASSET\nTASK: Ask the user which machine or equipment from CATALOG they would like to examine.";

        if (!p.HasCategory)
            return $"STAGE: SELECT_CATEGORY\nTASK: Acknowledge that \"{p.AssetName}\" is selected. Ask the user which signal category they would like to examine for this machine.";

        if (!p.HasTime)
            return $"STAGE: SELECT_TIMEFRAME\nTASK: Acknowledge that category \"{p.Category}\" is configured for \"{p.AssetName}\". Ask the user which timeframe they would like to analyze (e.g. 24h, 5d, 2w, or dates).";

        string timeDesc = !string.IsNullOrWhiteSpace(p.TimeRange) ? p.TimeRange : $"{p.FromDate} to {p.ToDate}";
        return $"STAGE: CONFIRM_QUEUE\nTASK: State that the report for \"{p.AssetName}\" covering \"{p.Category}\" for {timeDesc} is configured. Ask the user to click 'Yes, Queue Report' below to generate.";
    }

    public static string DescribeState(ExtractedReportParametersDto? p)
    {
        if (p == null || (!p.HasAsset && !p.HasTime && !p.HasCategory)) return "asset=none category=none time=none";

        string asset = p.HasAsset ? $"asset={p.AssetId} \"{p.AssetName}\"" : "asset=none";
        string category = p.HasCategory ? $"category=\"{p.Category}\"" : "category=none";
        string time =
            !string.IsNullOrWhiteSpace(p.FromDate) ? $"time={p.FromDate}..{(string.IsNullOrWhiteSpace(p.ToDate) ? "now" : p.ToDate)}" :
            !string.IsNullOrWhiteSpace(p.TimeRange) ? $"time={p.TimeRange}" :
            "time=none";
        return $"{asset} {category} {time}";
    }

    private static List<ChatMessageEntity> FilterOnTopicHistory(IReadOnlyList<ChatMessageEntity> messages)
    {
        var result = new List<ChatMessageEntity>();
        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                if (!ConversationStateReader.IsOnTopic(msg.Metadata))
                {
                    if (result.Count > 0 && string.Equals(result[^1].Role, "user", StringComparison.OrdinalIgnoreCase))
                    {
                        result.RemoveAt(result.Count - 1);
                    }
                    continue;
                }
            }
            result.Add(msg);
        }
        return result;
    }

    private static string Clip(string text, int max)
    {
        // collapse whitespace/newlines so each turn costs as few tokens as possible
        string flat = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
