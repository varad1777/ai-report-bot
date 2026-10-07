namespace SensorReportBot.Infrastructure.Services;

using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SensorReportBot.Application.Interfaces;
using SensorReportBot.Infrastructure.Configuration;

public class LlmService : ILlmService
{
    private readonly HttpClient _httpClient;
    private readonly LlmOptions _options;
    private readonly ILogger<LlmService> _logger;

    public LlmService(HttpClient httpClient, IOptions<LlmOptions> options, ILogger<LlmService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<TResponse?> GenerateJsonResponseAsync<TResponse>(
        string systemPrompt,
        string userMessage,
        CancellationToken ct = default,
        object? jsonSchema = null)
    {
        int sysChars = systemPrompt?.Length ?? 0;
        int sysTokens = EstimateTokens(systemPrompt ?? string.Empty);
        int userChars = userMessage?.Length ?? 0;
        int userTokens = EstimateTokens(userMessage ?? string.Empty);
        int totalInputTokens = sysTokens + userTokens;

        // Structured Console Logging for outgoing LLM request with token counts at each level
        string requestBanner =
            "\n╔══════════════════════════════════════════════════════════════════════════════════════\n" +
            $"║ 🤖 [LLM REQUEST DISPATCH]\n" +
            $"║    • Target Model:     {_options.Model}\n" +
            $"║    • Endpoint:         {_options.Endpoint}\n" +
            $"║    • Temperature:      {_options.Temperature}\n" +
            $"║    • Max Tokens:       {_options.MaxTokens}\n" +
            $"║ 📊 [INPUT TOKENS BREAKDOWN]:\n" +
            $"║    • System Prompt:    {sysChars:N0} characters | ~{sysTokens:N0} tokens\n" +
            $"║    • User / Payload:   {userChars:N0} characters | ~{userTokens:N0} tokens\n" +
            $"║    • Total Input:      {sysChars + userChars:N0} characters | ~{totalInputTokens:N0} tokens\n" +
            "╠══════════════════════════════════════════════════════════════════════════════════════\n" +
            $"║ 📜 [SYSTEM PROMPT ({sysTokens:N0} tokens)]:\n{systemPrompt}\n" +
            "╟──────────────────────────────────────────────────────────────────────────────────────\n" +
            $"║ 💬 [USER INPUT / PAYLOAD ({userTokens:N0} tokens)]:\n{userMessage}\n" +
            "╚══════════════════════════════════════════════════════════════════════════════════════\n";

        Console.WriteLine(requestBanner);
        _logger.LogInformation("{RequestBanner}", requestBanner);

        string endpointUrl = _options.Endpoint;
        if (!endpointUrl.EndsWith("/chat/completions", System.StringComparison.OrdinalIgnoreCase))
        {
            endpointUrl = endpointUrl.TrimEnd('/') + "/chat/completions";
        }

        bool useSchema = jsonSchema != null && _options.UseJsonSchema;
        var response = await SendAsync(endpointUrl, systemPrompt, userMessage, useSchema ? jsonSchema : null, ct);

        // Endpoint does not support json_schema: retry once in plain JSON mode.
        if (useSchema && (int)response.StatusCode == 400)
        {
            string schemaError = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Endpoint rejected json_schema response_format, falling back to json_object. Body: {Body}", schemaError);
            response.Dispose();
            response = await SendAsync(endpointUrl, systemPrompt, userMessage, null, ct);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(ct);

                // Some hosted endpoints (Groq) return HTTP 400 with code=json_validate_failed when the
                // model produces an empty completion. Return null so the caller can retry/report.
                if ((int)response.StatusCode == 400 && errorBody.Contains("json_validate_failed"))
                {
                    _logger.LogWarning("LLM returned json_validate_failed (empty generation). Body: {Body}", errorBody);
                    return default;
                }

                _logger.LogError("LLM request failed with status {StatusCode}: {ResponseBody}", response.StatusCode, errorBody);
                response.EnsureSuccessStatusCode(); // throws HttpRequestException
            }

            return await ParseAsync<TResponse>(response, systemPrompt, userMessage, ct);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        string endpointUrl, string systemPrompt, string userMessage, object? jsonSchema, CancellationToken ct)
    {
        object responseFormat = jsonSchema == null
            ? (object)new { type = "json_object" }
            : new { type = "json_schema", json_schema = new { name = "chat_reply", strict = true, schema = jsonSchema } };

        var payload = new
        {
            model = _options.Model,
            temperature = _options.Temperature,
            max_tokens = _options.MaxTokens,
            stream = false,
            response_format = responseFormat,
            options = new
            {
                num_ctx = _options.NumCtx > 0 ? _options.NumCtx : 8192
            },
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
        request.Content = JsonContent.Create(payload);

        return await _httpClient.SendAsync(request, ct);
    }

    private async Task<TResponse?> ParseAsync<TResponse>(
        HttpResponseMessage response,
        string systemPrompt,
        string userMessage,
        CancellationToken ct)
    {
        var chatResponse = await response.Content.ReadFromJsonAsync<ChatCompletionsResponse>(cancellationToken: ct);
        string? rawContent = chatResponse?.Choices?.FirstOrDefault()?.Message?.Content;

        // Token Usage calculation (from API usage or Ollama direct eval counts or fallback estimation)
        int promptTokens;
        int completionTokens;
        string tokenSource;

        if (chatResponse?.Usage != null && (chatResponse.Usage.PromptTokens > 0 || chatResponse.Usage.TotalTokens > 0))
        {
            promptTokens = chatResponse.Usage.PromptTokens;
            completionTokens = chatResponse.Usage.CompletionTokens;
            tokenSource = "API Reported Usage (Exact)";
        }
        else if (chatResponse?.PromptEvalCount.HasValue == true || chatResponse?.EvalCount.HasValue == true)
        {
            promptTokens = chatResponse.PromptEvalCount ?? (EstimateTokens(systemPrompt) + EstimateTokens(userMessage));
            completionTokens = chatResponse.EvalCount ?? EstimateTokens(rawContent ?? string.Empty);
            tokenSource = "Ollama Eval Metrics (Exact)";
        }
        else
        {
            promptTokens = EstimateTokens(systemPrompt) + EstimateTokens(userMessage);
            completionTokens = EstimateTokens(rawContent ?? string.Empty);
            tokenSource = "Estimated (~4 chars/token)";
        }
        int totalTokens = promptTokens + completionTokens;

        string responseBanner =
            "\n╔══════════════════════════════════════════════════════════════════════════════════════\n" +
            "║ 📥 [LLM RESPONSE RECEIVED]\n" +
            "╠══════════════════════════════════════════════════════════════════════════════════════\n" +
            $"║ 📊 [TOKEN USAGE BREAKDOWN ({tokenSource})]:\n" +
            $"║    • Prompt / Input Tokens:     {promptTokens:N0}\n" +
            $"║    • Completion / Output Tokens:{completionTokens:N0}\n" +
            $"║    • Total Round-Trip Tokens:   {totalTokens:N0}\n" +
            "╟──────────────────────────────────────────────────────────────────────────────────────\n" +
            $"║ 📝 [RAW LLM OUTPUT ({completionTokens:N0} tokens)]:\n{rawContent}\n" +
            "╚══════════════════════════════════════════════════════════════════════════════════════\n";

        Console.WriteLine(responseBanner);
        _logger.LogInformation("{ResponseBanner}", responseBanner);

        if (string.IsNullOrWhiteSpace(rawContent)) return default;

        string cleanJson = CleanJson(rawContent);
        try
        {
            var options = new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };
            return JsonSerializer.Deserialize<TResponse>(cleanJson, options);
        }
        catch (JsonException jsonEx)
        {
            _logger.LogWarning(jsonEx, "Failed to deserialize JSON response from LLM. Raw content: {RawContent}", rawContent);
            // Attempt emergency repair: extract object between first '{' and last '}'
            int firstBrace = rawContent.IndexOf('{');
            int lastBrace = rawContent.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                try
                {
                    string repairedJson = rawContent.Substring(firstBrace, lastBrace - firstBrace + 1);
                    return JsonSerializer.Deserialize<TResponse>(repairedJson, new JsonSerializerOptions 
                    { 
                        PropertyNameCaseInsensitive = true, 
                        AllowTrailingCommas = true 
                    });
                }
                catch
                {
                    return default;
                }
            }
            return default;
        }
    }

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return (int)System.Math.Ceiling(text.Length / 4.0);
    }

    private static string CleanJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "{}";
        string trimmed = raw.Trim();
        if (trimmed.StartsWith("```"))
        {
            int firstLineEnd = trimmed.IndexOf('\n');
            if (firstLineEnd >= 0) trimmed = trimmed[(firstLineEnd + 1)..];
            if (trimmed.EndsWith("```")) trimmed = trimmed[..^3];
        }
        
        int start = trimmed.IndexOf('{');
        int end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            trimmed = trimmed.Substring(start, end - start + 1);
        }

        return trimmed.Trim();
    }
}

public class ChatUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

public class ChatCompletionsResponse
{
    [JsonPropertyName("choices")]
    public ChatChoice[]? Choices { get; set; }

    [JsonPropertyName("usage")]
    public ChatUsage? Usage { get; set; }

    [JsonPropertyName("prompt_eval_count")]
    public int? PromptEvalCount { get; set; }

    [JsonPropertyName("eval_count")]
    public int? EvalCount { get; set; }
}

public class ChatChoice
{
    [JsonPropertyName("message")]
    public ChatMessageContent? Message { get; set; }
}

public class ChatMessageContent
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}
