namespace SensorReportBot.Application.Chat;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

public sealed record TimeframeResult(string? TimeRange, string? FromDate, string? ToDate, string? Problem)
{
    public static TimeframeResult None { get; } = new(null, null, null, null);
    public bool Found => TimeRange != null || FromDate != null;
    public bool HasProblem => Problem != null;
}

/// <summary>
/// Deterministic timeframe parser: "48 h", "last 2 weeks", "a couple of days", "yesterday",
/// "2026-09-01 to 2026-09-05". Produces "&lt;N&gt;h" / "&lt;N&gt;d" or absolute ISO-8601 UTC dates.
/// </summary>
public static class TimeframeParser
{
    public const int MaxDays = 90;

    private static readonly Dictionary<string, double> NumberWords = new()
    {
        ["a"] = 1, ["an"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["twelve"] = 12,
        ["fourteen"] = 14, ["fifteen"] = 15, ["twenty"] = 20, ["thirty"] = 30
    };

    private const string WordNumbers = "a|an|one|two|three|four|five|six|seven|eight|nine|ten|twelve|fourteen|fifteen|twenty|thirty";

    private static readonly Regex IsoRange = new(
        @"(\d{4}-\d{2}-\d{2})\s*(?:to|until|till|through|and|-|–)\s*(\d{4}-\d{2}-\d{2})", RegexOptions.Compiled);
    private static readonly Regex IsoDate = new(@"\b(\d{4}-\d{2}-\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex Yesterday = new(@"\byesterday\b", RegexOptions.Compiled);
    private static readonly Regex DigitUnit = new(
        @"(?<![\w.:-])(\d+(?:\.\d+)?)\s*(hours?|hrs?|h|days?|d|weeks?|wks?|w|months?|mos?|mon|m)(?![\w])",
        RegexOptions.Compiled);
    private static readonly Regex WordUnit = new(
        @"\b(" + WordNumbers + @")\s+(hours?|hrs?|days?|weeks?|months?|mos?|mon|m)\b", RegexOptions.Compiled);
    private static readonly Regex CoupleOf = new(@"\bcouple of (hours?|days?|weeks?)\b", RegexOptions.Compiled);
    private static readonly Regex FewDays = new(@"\b(?:a )?few days\b", RegexOptions.Compiled);
    private static readonly Regex HalfMonth = new(@"\bhalf (?:a |of a )?month\b", RegexOptions.Compiled);
    private static readonly Regex WeekPhrase = new(@"\b(?:past|last|this) week\b", RegexOptions.Compiled);
    private static readonly Regex MonthPhrase = new(@"\b(?:past|last|this) month\b", RegexOptions.Compiled);
    private static readonly Regex DayPhrase = new(@"\b(?:today|past day|last day|last 24)\b", RegexOptions.Compiled);
    private static readonly Regex BareNumber = new(
        @"^(?:(?:make|set|give|use|for|it|to|me|the|last|past|only|just|about|around|please|pls)\s+)*" +
        @"(\d{1,3}|" + WordNumbers + @")(?:\s+(?:please|pls))?\s*[.!]?$", RegexOptions.Compiled);

    public static TimeframeResult Parse(string message, bool allowBareNumber, DateTime? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return TimeframeResult.None;
        var now = utcNow ?? DateTime.UtcNow;
        string text = message.Trim().ToLowerInvariant();

        // 1. Absolute dates
        var range = IsoRange.Match(text);
        if (range.Success &&
            DateTime.TryParseExact(range.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var from) &&
            DateTime.TryParseExact(range.Groups[2].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var to))
        {
            if (to < from) (from, to) = (to, from);
            return Absolute(from, to.AddDays(1).AddSeconds(-1));
        }

        var single = IsoDate.Match(text);
        if (single.Success &&
            DateTime.TryParseExact(single.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var only))
        {
            return new TimeframeResult(null, Iso(only), null, null);
        }

        if (Yesterday.IsMatch(text))
        {
            var start = now.Date.AddDays(-1);
            return Absolute(start, start.AddDays(1).AddSeconds(-1));
        }

        // 2. "<number><unit>"
        var m = DigitUnit.Match(text);
        if (m.Success)
            return FromAmount(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), UnitOf(m.Groups[2].Value));

        m = WordUnit.Match(text);
        if (m.Success) return FromAmount(NumberWords[m.Groups[1].Value], UnitOf(m.Groups[2].Value));

        // 3. Natural phrases
        m = CoupleOf.Match(text);
        if (m.Success) return FromAmount(2, UnitOf(m.Groups[1].Value));
        if (FewDays.IsMatch(text)) return FromAmount(5, 'd');
        if (HalfMonth.IsMatch(text)) return FromAmount(14, 'd');
        if (WeekPhrase.IsMatch(text)) return FromAmount(7, 'd');
        if (MonthPhrase.IsMatch(text)) return FromAmount(30, 'd');
        if (DayPhrase.IsMatch(text)) return FromAmount(24, 'h');

        // 4. Bare number ("2", "make it 7") only when we are waiting for a timeframe
        if (allowBareNumber)
        {
            var bare = BareNumber.Match(text);
            if (bare.Success)
            {
                string token = bare.Groups[1].Value;
                double value = NumberWords.TryGetValue(token, out var w)
                    ? w
                    : double.Parse(token, CultureInfo.InvariantCulture);
                return FromAmount(value, 'd');
            }
        }

        return TimeframeResult.None;
    }

    private static char UnitOf(string unit) => unit[0] switch
    {
        'h' => 'h',
        'd' => 'd',
        'w' => 'w',
        _ => 'm'
    };

    private static TimeframeResult FromAmount(double value, char unit)
    {
        if (value <= 0)
            return new TimeframeResult(null, null, null, "Please give me a timeframe longer than zero, for example 24h, 5 days or 2 weeks.");

        double hours = unit switch
        {
            'h' => value,
            'd' => value * 24,
            'w' => value * 24 * 7,
            _ => value * 24 * 30
        };

        if (hours > MaxDays * 24)
            return new TimeframeResult(null, null, null,
                $"I can cover up to {MaxDays} days in one report. Please pick a shorter timeframe or specific dates.");

        if (unit == 'h')
            return new TimeframeResult($"{Math.Max(1, (int)Math.Round(hours))}h", null, null, null);

        double days = hours / 24;
        if (Math.Abs(days - Math.Round(days)) < 1e-9)
        {
            int d = (int)Math.Round(days);
            return new TimeframeResult(d == 1 ? "24h" : $"{d}d", null, null, null);
        }

        // Fractional days ("1.5 days") -> hours
        return new TimeframeResult($"{(int)Math.Round(hours)}h", null, null, null);
    }

    private static TimeframeResult Absolute(DateTime from, DateTime to) => new(null, Iso(from), Iso(to), null);

    private static string Iso(DateTime dt) => dt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
