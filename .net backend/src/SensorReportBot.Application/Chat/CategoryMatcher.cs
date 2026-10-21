namespace SensorReportBot.Application.Chat;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public sealed record CategoryMatchResult(string? Category, IReadOnlyList<string>? Candidates = null)
{
    public static CategoryMatchResult None { get; } = new(null, Array.Empty<string>());
    public bool IsMatch => !string.IsNullOrWhiteSpace(Category);
}

/// <summary>
/// Deterministic category matcher with typo and edit-distance tolerance.
/// Free of hardcoded category names — works strictly against the active asset's database categories.
/// Tolerates minor misspellings (e.g. 'lubricaion' -> 'Lubrication', 'vibraton' -> 'Bearing & Vibration').
/// </summary>
public static class CategoryMatcher
{
    private static readonly Regex TokenRegex = new(@"[a-zA-Z0-9]+", RegexOptions.Compiled);

    private static readonly HashSet<string> CategoryStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "&", "for", "the", "me", "give", "report", "please", "with",
        "show", "get", "covering", "of", "in", "to", "on", "a", "an", "all",
        "sensor", "sensors", "data", "signals", "signal", "select", "choose"
    };

    /// <summary>
    /// Matches one or more categories from the user's message.
    /// Handles multiple categories joined by commas, 'and', '&', as well as 'all categories'.
    /// </summary>
    public static IReadOnlyList<string> MatchMultiple(
        string? message,
        IReadOnlyList<string> availableCategories,
        IEnumerable<string>? excludeTokens = null)
    {
        if (string.IsNullOrWhiteSpace(message) || availableCategories == null || availableCategories.Count == 0)
            return Array.Empty<string>();

        string cleanMsg = message.Trim();
        string lowerMsg = cleanMsg.ToLowerInvariant();

        // 0. Shortcut for "all" / "all categories" / "all signals"
        if (lowerMsg == "all" || lowerMsg == "all categories" || lowerMsg == "all signals" || lowerMsg == "every category")
        {
            return availableCategories.ToList();
        }

        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Direct full category substring matches (longest names first to avoid partial overlap)
        foreach (var cat in availableCategories.OrderByDescending(c => c.Length))
        {
            if (cleanMsg.Contains(cat, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(cat);
            }
        }

        // 2. Comma or semicolon separated list (e.g. from UI multi-select or typed list)
        if (cleanMsg.Contains(',') || cleanMsg.Contains(';'))
        {
            var parts = cleanMsg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                var res = Match(part, availableCategories, excludeTokens);
                if (res.IsMatch && res.Category != null)
                {
                    matched.Add(res.Category);
                }
            }
        }

        // 3. Conjunction-based splitting (" and ", " & ")
        if (matched.Count < 2 && (cleanMsg.Contains(" and ", StringComparison.OrdinalIgnoreCase) || cleanMsg.Contains(" & ")))
        {
            var segments = Regex.Split(cleanMsg, @"\s+(?:and|\&)\s+", RegexOptions.IgnoreCase);
            if (segments.Length > 1)
            {
                foreach (var seg in segments)
                {
                    var segTrimmed = seg.Trim();
                    if (!string.IsNullOrWhiteSpace(segTrimmed))
                    {
                        var res = Match(segTrimmed, availableCategories, excludeTokens);
                        if (res.IsMatch && res.Category != null)
                        {
                            matched.Add(res.Category);
                        }
                    }
                }
            }
        }

        // 4. Fallback to single category match if nothing collected yet
        if (matched.Count == 0)
        {
            var single = Match(cleanMsg, availableCategories, excludeTokens);
            if (single.IsMatch && single.Category != null)
            {
                matched.Add(single.Category);
            }
        }

        // Preserve catalog order for consistency
        return availableCategories.Where(c => matched.Contains(c)).ToList();
    }

    public static CategoryMatchResult Match(
        string? message,
        IReadOnlyList<string> availableCategories,
        IEnumerable<string>? excludeTokens = null)
    {
        if (string.IsNullOrWhiteSpace(message) || availableCategories == null || availableCategories.Count == 0)
            return CategoryMatchResult.None;

        string cleanMsg = message.Trim();

        // 1. Exact case-insensitive match (e.g. from UI button clicks)
        var exact = availableCategories.FirstOrDefault(c => string.Equals(c, cleanMsg, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return new CategoryMatchResult(exact, new[] { exact });

        // 2. Direct full category substring match (e.g. "please give me Bearing & Vibration report")
        var substring = availableCategories.FirstOrDefault(c => cleanMsg.Contains(c, StringComparison.OrdinalIgnoreCase));
        if (substring != null)
            return new CategoryMatchResult(substring, new[] { substring });

        // 3. Token-based matching with edit-distance typo tolerance
        var excludeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (excludeTokens != null)
        {
            foreach (var tok in excludeTokens)
            {
                if (!string.IsNullOrWhiteSpace(tok))
                    excludeSet.Add(tok.Trim().ToLowerInvariant());
            }
        }

        var userTokens = TokenRegex.Matches(cleanMsg.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(t => t.Length >= 3 && !CategoryStopwords.Contains(t))
            .Where(t => !IsExcluded(t, excludeSet))
            .ToList();

        if (userTokens.Count == 0)
            return CategoryMatchResult.None;

        double bestScore = 0.0;
        string? bestCategory = null;
        var candidates = new List<string>();

        foreach (var cat in availableCategories)
        {
            var catTokens = TokenRegex.Matches(cat.ToLowerInvariant())
                .Select(m => m.Value)
                .Where(t => t.Length >= 3 && !CategoryStopwords.Contains(t))
                .ToList();

            double catScore = 0.0;

            foreach (var uTok in userTokens)
            {
                foreach (var cTok in catTokens)
                {
                    if (string.Equals(uTok, cTok, StringComparison.OrdinalIgnoreCase))
                    {
                        catScore += 1.0;
                    }
                    else if (IsTypoMatch(uTok, cTok))
                    {
                        catScore += 0.8;
                    }
                }
            }

            if (catScore > 0.7)
            {
                if (catScore > bestScore)
                {
                    bestScore = catScore;
                    bestCategory = cat;
                    candidates.Clear();
                    candidates.Add(cat);
                }
                else if (Math.Abs(catScore - bestScore) < 0.01)
                {
                    candidates.Add(cat);
                }
            }
        }

        if (candidates.Count == 1 && bestCategory != null)
            return new CategoryMatchResult(bestCategory, candidates);

        return candidates.Count > 1
            ? new CategoryMatchResult(null, candidates)
            : CategoryMatchResult.None;
    }

    private static bool IsExcluded(string token, HashSet<string> excludeSet)
    {
        if (excludeSet.Contains(token)) return true;
        foreach (var ex in excludeSet)
        {
            if (token.Length >= 4 && ex.Length >= 4)
            {
                if (token.StartsWith(ex, StringComparison.OrdinalIgnoreCase) ||
                    ex.StartsWith(token, StringComparison.OrdinalIgnoreCase) ||
                    EditDistance(token, ex) <= 1)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsTypoMatch(string uTok, string cTok)
    {
        if (uTok.Length < 4 || cTok.Length < 4) return false;
        if (Math.Abs(uTok.Length - cTok.Length) > 2) return false;
        if (uTok[0] != cTok[0]) return false; // First letter must match

        int maxAllowedEdits = (uTok.Length >= 8 && cTok.Length >= 8) ? 2 : 1;
        return EditDistance(uTok, cTok) <= maxAllowedEdits;
    }

    private static int EditDistance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            Array.Copy(cur, prev, cur.Length);
        }
        return cur[b.Length];
    }
}
