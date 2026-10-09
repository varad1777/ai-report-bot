namespace SensorReportBot.Infrastructure.Configuration;

public class LayaOptions
{
    public const string SectionName = "Laya";

    public string BaseUrl { get; set; } = "http://localhost:8001";
    public int TimeoutSeconds { get; set; } = 10;
    public bool Enabled { get; set; } = true;
}
