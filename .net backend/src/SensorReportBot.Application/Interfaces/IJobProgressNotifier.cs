namespace SensorReportBot.Application.Interfaces;

using System;
using System.Threading.Tasks;

public interface IJobProgressNotifier
{
    Task NotifyProgressAsync(string? sessionId, Guid jobId, int progressPercentage, string statusMessage, string status);
}
