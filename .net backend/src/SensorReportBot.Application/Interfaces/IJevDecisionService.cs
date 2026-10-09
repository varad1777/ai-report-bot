namespace SensorReportBot.Application.Interfaces;

/// <summary>Runs bounded yes/no decisions without generating free-form model text.</summary>
public interface IJevDecisionService
{
    Task<JevDecisionResult> DecideAsync(
        object state,
        IReadOnlyDictionary<string, string> questions,
        CancellationToken ct = default);
}

public sealed record JevDecisionResult(
    IReadOnlyDictionary<string, double> Probabilities,
    int? InputTokens);
