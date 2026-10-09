namespace SensorReportBot.Infrastructure.Configuration;

public sealed class JevOptions
{
    public const string SectionName = "Jev";
    public string Endpoint { get; set; } = "http://jev:8017/v1/systemone";
    public string Model { get; set; } = "jev-latest";
}
