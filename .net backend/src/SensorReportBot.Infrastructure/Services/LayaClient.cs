namespace SensorReportBot.Infrastructure.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SensorReportBot.Application.DTOs;
using SensorReportBot.Application.Interfaces;
using SensorReportBot.Infrastructure.Configuration;

public class LayaClient : ILayaClient
{
    private readonly HttpClient _httpClient;
    private readonly LayaOptions _options;
    private readonly ILogger<LayaClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LayaClient(HttpClient httpClient, IOptions<LayaOptions> options, ILogger<LayaClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => _options.Enabled;

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled) return false;

        try
        {
            var res = await _httpClient.GetAsync("/health", ct);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Laya health check failed.");
            return false;
        }
    }

    public async Task<LayaAskResponse?> AskAsync(
        string context,
        string question,
        string type,
        IReadOnlyList<string>? choices = null,
        CancellationToken ct = default)
    {
        if (!_options.Enabled) return null;

        var sw = Stopwatch.StartNew();
        var payload = new LayaAskRequest
        {
            Context = context,
            Question = question,
            Type = type,
            Choices = choices
        };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/ask", payload, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Laya responded with {StatusCode}: {ErrorText}", response.StatusCode, errorText);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<LayaAskResponse>(JsonOptions, ct);
            sw.Stop();

            string banner =
                $"\n⚡ [LAYA SYSTEM-1 DECISION] ({sw.ElapsedMilliseconds} ms)\n" +
                $"   • Question: {question}\n" +
                $"   • Type:     {type}\n" +
                $"   • Decision: {result?.Answer ?? "null"} (probability: {result?.Probability:P1})\n";
            Console.WriteLine(banner);
            _logger.LogInformation("{Banner}", banner);

            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Laya HTTP request failed (container not reachable).");
            return null;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Laya request timed out after {Elapsed} ms.", sw.ElapsedMilliseconds);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error calling Laya service.");
            return null;
        }
    }
}
