namespace SensorReportBot.Application.Chat;

using System;
using System.Collections.Generic;
using System.Linq;

public sealed record CategoryMatchResult(string? Category, IReadOnlyList<string>? Candidates = null)
{
    public static CategoryMatchResult None { get; } = new(null, Array.Empty<string>());
    public bool IsMatch => !string.IsNullOrWhiteSpace(Category);
}

/// <summary>
/// Direct string matching for category selection (e.g. from UI button clicks or exact name mentions).
/// Free of regex and hardcoded keyword dictionaries — semantic classification is handled dynamically by Jev and LLM.
/// </summary>
public static class CategoryMatcher
{
    public static CategoryMatchResult Match(string? message, IReadOnlyList<string> availableCategories)
    {
        if (string.IsNullOrWhiteSpace(message) || availableCategories == null || availableCategories.Count == 0)
            return CategoryMatchResult.None;

        string cleanMsg = message.Trim();

        // 1. Exact case-insensitive match (e.g. user clicked category chip button)
        var exact = availableCategories.FirstOrDefault(c => string.Equals(c, cleanMsg, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return new CategoryMatchResult(exact, new[] { exact });

        // 2. Direct substring match (e.g. "please give me Bearing & Vibration report")
        var substring = availableCategories.FirstOrDefault(c => cleanMsg.Contains(c, StringComparison.OrdinalIgnoreCase));
        if (substring != null)
            return new CategoryMatchResult(substring, new[] { substring });

        return CategoryMatchResult.None;
    }
}
