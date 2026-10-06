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

        if (recentMessages is { Count: > 0 })
        {
            sb.AppendLine("RECENT:");
            foreach (var m in recentMessages.TakeLast(LoadLimit))
            {
                bool isUser = string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase);
                sb.Append(isUser ? "U: " : "A: ")
                  .AppendLine(Clip(m.Content, isUser ? MaxUserLineChars : MaxAssistantLineChars));
            }
        }

        if (!string.IsNullOrWhiteSpace(systemNote))
            sb.Append("NOTE: ").AppendLine(systemNote);

        sb.Append("USER: ").Append(Clip(userMessage, MaxCurrentMessageChars));
        return sb.ToString();
    }

    private const int LoadLimit = 4;

    public static string DescribeState(ExtractedReportParametersDto? p)
    {
        if (p == null || (!p.HasAsset && !p.HasTime)) return "asset=none time=none";

        string asset = p.HasAsset ? $"asset={p.AssetId} \"{p.AssetName}\"" : "asset=none";
        string time =
            !string.IsNullOrWhiteSpace(p.TimeRange) ? $"time={p.TimeRange}" :
            !string.IsNullOrWhiteSpace(p.FromDate) ? $"time={p.FromDate}..{(string.IsNullOrWhiteSpace(p.ToDate) ? "now" : p.ToDate)}" :
            "time=none";
        return $"{asset} {time}";
    }

    private static string Clip(string text, int max)
    {
        // collapse whitespace/newlines so each turn costs as few tokens as possible
        string flat = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
