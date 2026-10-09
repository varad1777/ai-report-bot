namespace SensorReportBot.Application.DTOs;

using System.Collections.Generic;
using System.Text.Json.Serialization;

public class LayaAskRequest
{
    [JsonPropertyName("context")]
    public string Context { get; set; } = string.Empty;

    [JsonPropertyName("question")]
    public string Question { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "choice"; // "choice" | "noul" | "score"

    [JsonPropertyName("choices")]
    public IReadOnlyList<string>? Choices { get; set; }
}

public class LayaAskResponse
{
    [JsonPropertyName("answer")]
    public object? Answer { get; set; }

    [JsonPropertyName("probability")]
    public double Probability { get; set; }

    [JsonPropertyName("raw")]
    public Dictionary<string, object>? Raw { get; set; }

    [JsonPropertyName("routing")]
    public Dictionary<string, object>? Routing { get; set; }

    [JsonIgnore]
    public bool AsBoolean
    {
        get
        {
            if (Answer is bool b) return b;
            if (Answer is System.Text.Json.JsonElement elem)
            {
                if (elem.ValueKind == System.Text.Json.JsonValueKind.True) return true;
                if (elem.ValueKind == System.Text.Json.JsonValueKind.False) return false;
                if (elem.ValueKind == System.Text.Json.JsonValueKind.String && bool.TryParse(elem.GetString(), out var pb)) return pb;
            }
            if (Answer is string s && bool.TryParse(s, out var parsed)) return parsed;
            return false;
        }
    }

    [JsonIgnore]
    public string? AsString
    {
        get
        {
            if (Answer is string s) return s;
            if (Answer is System.Text.Json.JsonElement elem)
            {
                return elem.ValueKind == System.Text.Json.JsonValueKind.String ? elem.GetString() : elem.ToString();
            }
            return Answer?.ToString();
        }
    }
}
