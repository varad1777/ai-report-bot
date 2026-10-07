namespace SensorReportBot.Infrastructure.Configuration;

public class LlmOptions
{
    public const string SectionName = "Llm";

    public string Endpoint { get; set; } = "http://localhost:11434/v1/chat/completions";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "qwen2.5:1.5b";
    public double Temperature { get; set; } = 0.1;

    /// <summary>Output budget. The contract JSON needs roughly 250-800 tokens.</summary>
    public int MaxTokens { get; set; } = 1024;

    /// <summary>Context window size (tokens) sent to Ollama / local LLM server.</summary>
    public int NumCtx { get; set; } = 8192;

    /// <summary>
    /// Ask for grammar-constrained decoding with the contract's JSON Schema (Ollama / llama.cpp).
    /// Automatically falls back to plain json_object mode if the endpoint rejects it.
    /// </summary>
    public bool UseJsonSchema { get; set; } = true;

    /// <summary>HTTP timeout for one completion. Edge CPUs can be slow on the first (cold) request.</summary>
    public int TimeoutSeconds { get; set; } = 90;
}
