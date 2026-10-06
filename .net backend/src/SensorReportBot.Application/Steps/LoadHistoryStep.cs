namespace SensorReportBot.Application.Steps;

using System.Threading;
using System.Threading.Tasks;
using SensorReportBot.Application.Chat;
using SensorReportBot.Application.Interfaces;
using SensorReportBot.Domain.Entities;

/// <summary>
/// Loads the two inputs of the state-augmented prompt:
///   1. the last 2 turns (4 messages) of transcript, and
///   2. the accumulated report state from the newest assistant message's metadata.
/// Must run BEFORE <see cref="SaveUserMessageStep"/> so the current message is not part of its own context.
/// </summary>
public class LoadHistoryStep : IChatStep
{
    public const int RecentMessageLimit = 4; // 2 turns = 2 user + 2 assistant messages

    private readonly IChatHistoryRepository _historyRepo;

    public LoadHistoryStep(IChatHistoryRepository historyRepo)
    {
        _historyRepo = historyRepo;
    }

    public async Task RunAsync(ChatRequestState state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(state.SessionId)) return;

        state.History = await _historyRepo.GetRecentHistoryAsync(state.SessionId, RecentMessageLimit, ct);

        string? metadata = await _historyRepo.GetLatestAssistantMetadataAsync(state.SessionId, ct);
        state.PreviousParameters = ConversationStateReader.Read(metadata);
    }
}
