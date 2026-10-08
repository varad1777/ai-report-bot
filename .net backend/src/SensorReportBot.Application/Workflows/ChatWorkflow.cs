namespace SensorReportBot.Application.Workflows;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SensorReportBot.Application.Interfaces;
using SensorReportBot.Domain.Entities;

public class ChatWorkflow
{
    private readonly IEnumerable<IChatStep> _steps;

    public ChatWorkflow(IEnumerable<IChatStep> steps)
    {
        _steps = steps;
    }

    /// <summary>
    /// Runs the steps in registration order. There is intentionally no fallback reply: every
    /// reply comes from the LLM, and a step that cannot get one throws.
    /// </summary>
    public async Task ExecuteAsync(ChatRequestState state, CancellationToken ct = default)
    {
        foreach (var step in _steps)
        {
            await step.RunAsync(state, ct);
                // LoadHistoryStep
                // IntentExtractionStep
                // SaveUserMessageStep
                // PersistAssistantReplyStep
        }
    }
}
