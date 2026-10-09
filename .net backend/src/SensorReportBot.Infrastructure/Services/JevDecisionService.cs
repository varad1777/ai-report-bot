namespace SensorReportBot.Infrastructure.Services;

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SensorReportBot.Application.Interfaces;
using SensorReportBot.Infrastructure.Configuration;

/// <summary>HTTP adapter for the local MIT-licensed jevos System One server.</summary>
public sealed class JevDecisionService(HttpClient http, IOptions<JevOptions> options) : IJevDecisionService
{
    public async Task<JevDecisionResult> DecideAsync(
        object state,
        IReadOnlyDictionary<string, string> questions,
        CancellationToken ct = default)
    {
        var request = new
        {
            model = options.Value.Model,
            state,
            questions = questions.ToDictionary(
                item => item.Key,
                item => new { type = "noul", instructions = item.Value })
        };
        using var response = await http.PostAsJsonAsync(options.Value.Endpoint, request, ct);
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var probabilities = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var answers = payload.RootElement.GetProperty("answers");
        foreach (var question in questions.Keys)
        {
            if (!answers.TryGetProperty(question, out var answer) ||
                !answer.TryGetProperty("noul", out var probability))
                throw new InvalidOperationException($"Jev response omitted '{question}'.");
            probabilities[question] = probability.GetDouble();
        }
        int? inputTokens = null;
        if (payload.RootElement.TryGetProperty("usage", out var usage) &&
            usage.TryGetProperty("input_tokens", out var tokens) && tokens.TryGetInt32(out var count))
            inputTokens = count;
        return new(probabilities, inputTokens);
    }
}
