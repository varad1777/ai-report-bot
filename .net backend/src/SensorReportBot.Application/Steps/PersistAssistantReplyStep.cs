namespace SensorReportBot.Application.Steps;

using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SensorReportBot.Application.Interfaces;
using SensorReportBot.Domain.Entities;

public class PersistAssistantReplyStep : IChatStep
{
    private readonly IChatHistoryRepository _historyRepo;

    public PersistAssistantReplyStep(IChatHistoryRepository historyRepo)
    {
        _historyRepo = historyRepo;
    }

    public async Task RunAsync(ChatRequestState state, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(state.Reply) && !string.IsNullOrWhiteSpace(state.SessionId))
        {
            string? metadataJson = null;
            try
            {
                var metaObj = new
                {
                    isOnTopic = state.IsOnTopic,
                    isComplete = state.IsComplete,
                    suggestedAction = state.SuggestedAction,
                    suggestedOptions = state.SuggestedOptions,
                    extractedParameters = state.ExtractedParameters,
                    jobId = state.JobId,
                    inputTokens = state.InputTokens,
                    outputTokens = state.OutputTokens,
                    totalTokens = state.TotalTokens,
                    decisionModel = state.DecisionModel
                };
                metadataJson = JsonSerializer.Serialize(metaObj);
            }
            catch
            {
                // Fallback to null metadata if serialization fails
            }

            await _historyRepo.SaveMessageAsync(state.SessionId, "assistant", state.Reply, metadataJson, ct);
        }
    }
}
