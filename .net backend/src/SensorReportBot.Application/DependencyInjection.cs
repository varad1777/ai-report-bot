namespace SensorReportBot.Application;

using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SensorReportBot.Application.Interfaces;
using SensorReportBot.Application.Steps;
using SensorReportBot.Application.Workflows;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
        // Sequential chat pipeline (order matters - steps run in registration order):
        //  1. LoadHistoryStep         last 2 turns + accumulated report state (from DB metadata)
        //  2. IntentExtractionStep    single LLM call -> reply + parameters, synced with the database
        //  3. SaveUserMessageStep     persist the user message (only after the LLM answered, so a failed
        //                             call leaves no orphan message in the transcript)
        //  4. PersistAssistantReplyStep  store reply + state metadata for the next turn
        services.AddScoped<IChatStep, LoadHistoryStep>();
        services.AddScoped<IChatStep, IntentExtractionStep>();
        services.AddScoped<IChatStep, SaveUserMessageStep>();
        services.AddScoped<IChatStep, PersistAssistantReplyStep>();

        services.AddScoped<ChatWorkflow>();
        return services;
    }
}
