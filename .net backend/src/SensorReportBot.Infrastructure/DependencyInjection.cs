namespace SensorReportBot.Infrastructure;

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SensorReportBot.Application.Interfaces;
using SensorReportBot.Infrastructure.Configuration;
using SensorReportBot.Infrastructure.Repositories;
using SensorReportBot.Infrastructure.Services;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Bind LLM configuration section
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));

        // Register HttpClient for LLM Service
        services.AddHttpClient<ILlmService, LlmService>((sp, client) =>
        {
            var llm = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LlmOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(Math.Max(5, llm.TimeoutSeconds));
        });

        // System prompt provider (reads system prompts from Prompts/ directory)
        services.AddSingleton<IPromptProvider, FilePromptProvider>();

        // TimescaleDB Telemetry Repository
        services.AddScoped<ITelemetryRepository, TelemetryRepository>();

        // PostgreSQL Chat History Repository & Conversational AI Chat Service
        services.AddScoped<IChatHistoryRepository, ChatHistoryRepository>();
        services.AddScoped<IChatService, ChatService>();

        // ── EcoStruxure Report Pipeline ──────────────────────────────────────
        // ReportEvidenceService: TimescaleDB SQL aggregation (no LLM)
        services.AddScoped<IReportEvidenceService, ReportEvidenceService>();

        // ReportNarrativeService: LLM call with strict JSON schema (text only)
        services.AddScoped<IReportNarrativeService, ReportNarrativeService>();

        // EcoReportHtmlBuilder: Schneider Electric HTML template merger
        services.AddSingleton<EcoReportHtmlBuilder>();

        // EcoReportService: full pipeline orchestrator (Phase 1→2→3→4)
        // Replaces QuestPdfReportService as the active PDF renderer.
        services.AddScoped<IPdfReportService, EcoReportService>();

        // PDF Generation Asynchronous In-Memory Job Queue & Background Service
        services.AddSingleton<IPdfJobQueue, InMemoryPdfJobQueue>();
        services.AddHostedService<PdfGenerationBackgroundService>();

        return services;
    }
}
