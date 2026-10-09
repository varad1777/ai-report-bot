namespace SensorReportBot.Application.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SensorReportBot.Application.DTOs;

public interface ILayaClient
{
    bool IsEnabled { get; }
    Task<bool> IsHealthyAsync(CancellationToken ct = default);
    Task<LayaAskResponse?> AskAsync(
        string context,
        string question,
        string type,
        IReadOnlyList<string>? choices = null,
        CancellationToken ct = default);
}
