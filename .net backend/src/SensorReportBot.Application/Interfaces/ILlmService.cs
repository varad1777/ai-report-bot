namespace SensorReportBot.Application.Interfaces;

using System.Threading;
using System.Threading.Tasks;

public interface ILlmService
{
    /// <param name="jsonSchema">
    /// Optional JSON Schema for grammar-constrained decoding. Implementations that cannot honour it
    /// fall back to plain JSON mode.
    /// </param>
    Task<TResponse?> GenerateJsonResponseAsync<TResponse>(
        string systemPrompt,
        string userMessage,
        CancellationToken ct = default,
        object? jsonSchema = null);
}
