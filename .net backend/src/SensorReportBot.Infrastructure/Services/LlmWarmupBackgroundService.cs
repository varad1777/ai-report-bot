namespace SensorReportBot.Infrastructure.Services;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SensorReportBot.Application.Chat;
using SensorReportBot.Application.DTOs;
using SensorReportBot.Application.Interfaces;

/// <summary>
/// Pre-warms the local LLM (Ollama / Qwen) immediately upon application boot.
/// Solves the first-query cold-start latency by:
///   1. Loading model weights from disk into GPU VRAM / RAM in the background.
///   2. Evaluating and pre-populating the KV-cache for the System Prompt + Asset Catalog prefix.
///   3. Keeping the model permanently pinned in memory with keep_alive=-1.
/// When the user sends their first query, response time drops from ~10-15s to sub-second.
/// </summary>
public class LlmWarmupBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LlmWarmupBackgroundService> _logger;

    public LlmWarmupBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<LlmWarmupBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait 1.5 seconds for the web server and database connection pool to spin up
        await Task.Delay(1500, stoppingToken);

        try
        {
            _logger.LogInformation("🔥 [LLM Pre-Warm] Starting background warm-up to pre-cache System Prompt and model weights...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            using var scope = _scopeFactory.CreateScope();
            var telemetryRepo = scope.ServiceProvider.GetRequiredService<ITelemetryRepository>();
            var promptProvider = scope.ServiceProvider.GetRequiredService<IPromptProvider>();
            var llmService = scope.ServiceProvider.GetRequiredService<ILlmService>();

            var assets = await telemetryRepo.GetAssetsAsync(stoppingToken);
            string template = await promptProvider.GetPromptAsync("ExtractorPrompt", stoppingToken);
            string catalog = assets.Count == 0
                ? "(none)"
                : string.Join(" | ", assets.Select(a => $"{a.AssetId}: {a.Name}"));

            string systemPrompt = template.Replace("{AssetCatalog}", catalog);
            string warmupUserPrompt = "NOW: " + DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm'Z'") + "\nSTATE: asset=none time=none\nUSER: hi";

            await llmService.GenerateJsonResponseAsync<LlmChatReplyDto>(
                systemPrompt,
                warmupUserPrompt,
                stoppingToken,
                ChatReplySchema.Value);

            sw.Stop();
            _logger.LogInformation("✅ [LLM Pre-Warm] Completed in {ElapsedMs}ms. Model and System Prompt KV-Cache are now HOT in memory.", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Application is shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ [LLM Pre-Warm] Background pre-warm did not complete (LLM server might be starting up): {Message}", ex.Message);
        }
    }
}
