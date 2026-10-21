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
        // Initial delay to let Kestrel server bind ports and services initialize
        await Task.Delay(2000, stoppingToken);

        const int maxAttempts = 15;
        for (int attempt = 1; attempt <= maxAttempts && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                _logger.LogInformation("🔥 [LLM Pre-Warm] Attempt {Attempt}/{MaxAttempts}: Loading model weights & pre-caching System Prompt...", attempt, maxAttempts);
                var sw = System.Diagnostics.Stopwatch.StartNew();

                using var scope = _scopeFactory.CreateScope();
                var telemetryRepo = scope.ServiceProvider.GetRequiredService<ITelemetryRepository>();
                var promptProvider = scope.ServiceProvider.GetRequiredService<IPromptProvider>();
                var llmService = scope.ServiceProvider.GetRequiredService<ILlmService>();

                // 1. Fetch real assets from database (wait until seeder has populated catalog)
                var assets = await telemetryRepo.GetAssetsAsync(stoppingToken);
                if (assets.Count == 0 && attempt < 8)
                {
                    _logger.LogInformation("⏳ [LLM Pre-Warm] Asset catalog is still empty (database seeder running). Waiting for catalog...");
                    await Task.Delay(3000, stoppingToken);
                    continue;
                }

                // 2. Build exact System Prompt matching production user requests
                string template = await promptProvider.GetPromptAsync("ExtractorPrompt", stoppingToken);
                string catalog = assets.Count == 0
                    ? "(none)"
                    : string.Join(" | ", assets.Select(a => $"{a.AssetId}: {a.Name}"));

                string systemPrompt = template.Replace("{AssetCatalog}", catalog);

                // 3. Build exact Context structure matching first user turn
                string warmupUserPrompt = PromptContextBuilder.Build(
                    "hi", 
                    null, 
                    null, 
                    DateTime.UtcNow);

                // 4. Dispatch warm-up inference (pinned with keep_alive: -1)
                var response = await llmService.GenerateJsonResponseAsync<LlmChatReplyDto>(
                    systemPrompt,
                    warmupUserPrompt,
                    stoppingToken,
                    ChatReplySchema.Value);

                if (response != null)
                {
                    sw.Stop();
                    _logger.LogInformation(
                        "✅ [LLM Pre-Warm SUCCESS] Model and System Prompt KV-Cache are HOT in memory ({ElapsedMs}ms, {AssetCount} assets). First user query will be sub-second!", 
                        sw.ElapsedMilliseconds, assets.Count);
                    return;
                }

                _logger.LogWarning("⚠️ [LLM Pre-Warm] LLM returned null reply on attempt {Attempt}/{MaxAttempts}. Retrying...", attempt, maxAttempts);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ [LLM Pre-Warm] Attempt {Attempt}/{MaxAttempts} failed ({Message}). Retrying in 4s...", attempt, maxAttempts, ex.Message);
            }

            await Task.Delay(4000, stoppingToken);
        }
    }
}
