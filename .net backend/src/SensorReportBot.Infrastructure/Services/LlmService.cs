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

    public async Task<TResponse?> GenerateJsonResponseAsync<TResponse>(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        _logger.LogDebug("\n==================== [LLM REQUEST LOG] ====================\n[SYSTEM PROMPT]:\n{SystemPrompt}\n-----------------------------------------------------------\n[USER PROMPT]:\n{UserMessage}\n===========================================================", systemPrompt, userMessage);

        var payload = new
        {
            model = _options.Model,
            temperature = _options.Temperature,
            max_tokens = _options.MaxTokens,
            keep_alive = "30m",
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            }
        };

        string endpointUrl = _options.Endpoint;
        if (!endpointUrl.EndsWith("/chat/completions", System.StringComparison.OrdinalIgnoreCase))
        {
            endpointUrl = endpointUrl.TrimEnd('/') + "/chat/completions";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
        request.Content = JsonContent.Create(payload);

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(ct);

            // Groq returns HTTP 400 with code=json_validate_failed when the model produces
            // an empty completion (e.g. for short casual messages like "how are you?").
            // In this case, return null so the caller can apply its own graceful fallback
            // instead of crashing the entire request pipeline.
            if ((int)response.StatusCode == 400 && errorBody.Contains("json_validate_failed"))
            {
                _logger.LogWarning("LLM returned json_validate_failed (empty generation). Returning null for graceful fallback. Body: {Body}", errorBody);
                return default;
            }

            _logger.LogError("LLM request failed with status {StatusCode}: {ResponseBody}", response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode(); // re-throws for all other non-200 errors
        }

        var chatResponse = await response.Content.ReadFromJsonAsync<ChatCompletionsResponse>(cancellationToken: ct);
        string? rawContent = chatResponse?.Choices?.FirstOrDefault()?.Message?.Content;

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

public class ChatCompletionsResponse
{
    [JsonPropertyName("choices")]
    public ChatChoice[]? Choices { get; set; }
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
