namespace SensorReportBot.Application.Chat;

using System.Collections.Generic;

/// <summary>
/// JSON Schema of <see cref="DTOs.LlmChatReplyDto"/>. Strict structured output schema
/// compliant with OpenAI and Groq grammar-constrained decoding.
/// </summary>
public static class ChatReplySchema
{
    private static Dictionary<string, object> NullableOf(string type) =>
        new()
        {
            ["anyOf"] = new object[]
            {
                new Dictionary<string, object> { ["type"] = type },
                new Dictionary<string, object> { ["type"] = "null" }
            }
        };

    public static readonly object Value = new Dictionary<string, object>
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new[] { "isOnTopic", "isComplete", "replyMessage", "suggestedAction", "extractedParameters" },
        ["properties"] = new Dictionary<string, object>
        {
            ["isOnTopic"] = new Dictionary<string, object> { ["type"] = "boolean" },
            ["isComplete"] = new Dictionary<string, object> { ["type"] = "boolean" },
            ["replyMessage"] = new Dictionary<string, object> { ["type"] = "string" },
            ["suggestedAction"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = new[] { "select_asset", "select_category", "select_timeframe", "confirm_queue", "none" }
            },
            ["extractedParameters"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new[] { "assetId", "assetName", "category", "signalIds", "timeRange", "fromDate", "toDate", "mode" },
                ["properties"] = new Dictionary<string, object>
                {
                    ["assetId"] = NullableOf("integer"),
                    ["assetName"] = NullableOf("string"),
                    ["category"] = NullableOf("string"),
                    ["signalIds"] = new Dictionary<string, object>
                    {
                        ["anyOf"] = new object[]
                        {
                            new Dictionary<string, object>
                            {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object> { ["type"] = "integer" }
                            },
                            new Dictionary<string, object> { ["type"] = "null" }
                        }
                    },
                    ["timeRange"] = NullableOf("string"),
                    ["fromDate"] = NullableOf("string"),
                    ["toDate"] = NullableOf("string"),
                    ["mode"] = NullableOf("string")
                }
            }
        }
    };
}
