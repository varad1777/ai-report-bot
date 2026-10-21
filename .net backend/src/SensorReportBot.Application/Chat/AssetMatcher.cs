namespace SensorReportBot.Application.Chat;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SensorReportBot.Application.DTOs;

public sealed record AssetMatchResult(AssetDto? Asset, IReadOnlyList<AssetDto> Candidates)
{
    public static AssetMatchResult None { get; } = new(null, Array.Empty<AssetDto>());
    public bool IsMatch => Asset is not null;
    public bool IsAmbiguous => Asset is null && Candidates.Count > 1;
}

/// <summary>
/// Deterministic asset matcher. Works from the catalog itself (no hard-coded machine names):
/// tokens shared by every asset name (e.g. "motor") are ignored, typos are tolerated,
/// and very short words ("air", "fan") only count when the message is short or backed by another word.
/// </summary>
public static class AssetMatcher
{
    private const double ExactScore = 1.0;
    private const double FuzzyScore = 0.6;
    private const double WeakScore = 0.5;

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "of", "and", "for", "in", "to", "a", "an"
    };

    private static readonly Regex TokenRegex = new(@"[a-z0-9]+", RegexOptions.Compiled);

    private static readonly Regex IdRegex = new(
        @"(?:\b(?:asset|machine|equipment|id)\s*(?:no\.?|number)?\s*#?\s*|#)(\d{1,4})\b",
        RegexOptions.Compiled);

    public static AssetMatchResult Match(string message, IReadOnlyList<AssetDto> assets)
    {
        if (string.IsNullOrWhiteSpace(message) || assets.Count == 0) return AssetMatchResult.None;

        string lower = message.ToLowerInvariant();

        // 1. Explicit catalog id: "asset 3", "machine #2"
        var idMatch = IdRegex.Match(lower);
        if (idMatch.Success && int.TryParse(idMatch.Groups[1].Value, out int id))
        {
            var byId = assets.FirstOrDefault(a => a.AssetId == id);
            if (byId != null) return new AssetMatchResult(byId, new[] { byId });
        }

        // 2. Full catalog name present in the message (longest name wins)
        var byName = assets
            .Where(a => lower.Contains(a.Name.ToLowerInvariant()))
            .OrderByDescending(a => a.Name.Length)
            .FirstOrDefault();
        if (byName != null) return new AssetMatchResult(byName, new[] { byName });

        // 3. Token scoring with typo tolerance
        var userTokens = TokenRegex.Matches(lower).Select(m => m.Value).ToList();
        int wordCount = userTokens.Count;

        var tokenLists = assets.Select(a => Tokenize(a.Name)).ToList();
        var docFreq = new Dictionary<string, int>();
        foreach (var list in tokenLists)
            foreach (var t in list.Distinct())
                docFreq[t] = docFreq.GetValueOrDefault(t) + 1;

        bool dropGeneric = assets.Count > 1;
        var scores = new double[assets.Count];
        var strong = new bool[assets.Count];
        for (int i = 0; i < assets.Count; i++)
        {
            foreach (var token in tokenLists[i].Distinct())
            {
                if (dropGeneric && docFreq[token] >= assets.Count) continue; // e.g. "motor"
                var (score, isStrong) = BestTokenScore(token, userTokens, wordCount);
                scores[i] += score;
                strong[i] |= isStrong;
            }
        }

        double best = scores.Max();
        double threshold = wordCount == 1 ? FuzzyScore : ExactScore;
        if (best < threshold) return AssetMatchResult.None;

        // Require at least one strong word (a real name word, not just "fan"/"air" in a sentence
        // or a loose typo). A lone message word may be a typo, e.g. "boilr".
        var candidates = assets
            .Where((_, i) => scores[i] >= best - 1e-9 && (strong[i] || wordCount == 1))
            .ToList();
        if (candidates.Count == 0) return AssetMatchResult.None;
        return candidates.Count == 1
            ? new AssetMatchResult(candidates[0], candidates)
            : new AssetMatchResult(null, candidates);
    }

    public static List<string> Tokenize(string name) =>
        TokenRegex.Matches(name.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(t => t.Length >= 2 && !Stopwords.Contains(t))
            .ToList();

    private static (double Score, bool Strong) BestTokenScore(string assetToken, List<string> userTokens, int wordCount)
    {
        double best = 0;
        foreach (var raw in userTokens)
        {
            string u = Singular(raw);
            bool exact = u == assetToken || raw == assetToken ||
                         (u.Length >= 4 && assetToken.StartsWith(u, StringComparison.Ordinal));
            if (exact)
            {
                // "air" / "fan" inside a long sentence is weak evidence ("I'm a fan of cricket")
                bool weak = assetToken.Length <= 3 && wordCount > 3;
                return weak ? (WeakScore, false) : (ExactScore, true);
            }

            if (IsFuzzy(u, assetToken))
            {
                // one typo in a long word ("compresor") is near-certain; loose matches are not
                bool longWord = assetToken.Length >= 8 && EditDistance(u, assetToken) == 1;
                if (longWord) return (ExactScore, true);
                best = Math.Max(best, FuzzyScore);
            }
        }
        return (best, false);
    }

    /// <summary>All words used in catalog asset names (including generic ones like "motor").</summary>
    public static HashSet<string> CatalogWords(IReadOnlyList<AssetDto> assets) =>
        assets.SelectMany(a => Tokenize(a.Name)).ToHashSet();

    /// <summary>True when a word is (a typo of / prefix of / plural of) a catalog name word.</summary>
    public static bool IsAssetWord(string token, HashSet<string> catalogWords)
    {
        string u = Singular(token);
        if (catalogWords.Contains(token) || catalogWords.Contains(u)) return true;
        foreach (var w in catalogWords)
        {
            if (u.Length >= 4 && w.StartsWith(u, StringComparison.Ordinal)) return true;
            if (IsFuzzy(u, w)) return true;
        }
        return false;
    }

    private static string Singular(string t) => t.Length > 4 && t.EndsWith('s') ? t[..^1] : t;

    private static bool IsFuzzy(string u, string t)
    {
        if (u.Length < 5 || t.Length < 5) return false;
        if (u[0] != t[0] || Math.Abs(u.Length - t.Length) > 2) return false;
        int max = t.Length >= 8 ? 2 : 1;
        return EditDistance(u, t) <= max;
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
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
