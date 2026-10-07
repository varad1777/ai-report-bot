# Sensor Report Chatbot Backend

A .NET 8 Web API built using Clean Architecture. The application processes user chat messages through an extensible workflow pipeline featuring an LLM-based Guard classification step to filter on-topic vs. off-topic requests using structured JSON outputs.

## Architecture

Pure-LLM, edge-optimized chat pipeline (built for 1.5B-3B models such as Qwen 2.5, Llama 3.2, Phi-3.5 on Ollama / llama.cpp):

- **One LLM call per message.** Every `replyMessage` is written by the model - there are no regex shortcuts or canned replies. If the model is unreachable or returns nothing usable (after one corrective retry), the hub sends `ReceiveError` instead of a fake answer.
- **State-augmented memory (~250 input tokens).** The prompt is `NOW` + `STATE` (report config restored from the last assistant message's metadata in PostgreSQL) + `RECENT` (last 2 turns) + `USER`. The static rules and the asset catalog live in the system prompt so the inference server can reuse its KV-cache prefix.
- **Server-side sync.** `ChatResponseSynchronizer` grounds `assetId`/`assetName` in the catalog, validates the timeframe (max 90 days), recomputes `isComplete`, keeps `suggestedAction` consistent with the real state and fills `suggestedOptions` (asset names from the DB, timeframe presets, confirm buttons). The model never emits option lists.
- **Structured output.** The contract JSON Schema is sent as `response_format: json_schema` (grammar-constrained decoding on Ollama/llama.cpp) and falls back to `json_object` automatically.

Layers:

- **`SensorReportBot.Domain`**: `ChatRequestState`, `ExtractedReportParametersDto`, `SuggestedActions`.
- **`SensorReportBot.Application`**: pipeline steps (`LoadHistoryStep` -> `IntentExtractionStep` -> `SaveUserMessageStep` -> `PersistAssistantReplyStep`), `PromptContextBuilder`, `ChatResponseSynchronizer`, `ChatReplySchema`, `LlmChatReplyDto` (the LLM contract).
- **`SensorReportBot.Infrastructure`**: `Prompts/ExtractorPrompt.txt`, `LlmService` (OpenAI-compatible HTTP client), repositories, PDF services.
- **`SensorReportBot.Api`**: SignalR `ChatHub` (`/hubs/chat`), controllers, Serilog, CORS, Swagger, health checks.

## LLM contract

```json
{
  "isOnTopic": true,
  "isComplete": false,
  "replyMessage": "...",
  "suggestedAction": "select_asset | select_timeframe | confirm_queue | none",
  "extractedParameters": { "assetId": 1, "assetName": "...", "signalIds": null, "timeRange": "5d", "fromDate": null, "toDate": null, "mode": "raw" }
}
```

The SignalR response to the UI additionally carries `suggestedOptions`, filled by the server.

## LLM Configuration (`appsettings.json`)

```json
"Llm": {
  "Endpoint": "http://localhost:11434/v1/chat/completions",
  "ApiKey": "",
  "Model": "qwen2.5:1.5b",
  "Temperature": 0.1,
  "MaxTokens": 256,
  "UseJsonSchema": true,
  "TimeoutSeconds": 90
}
```

## How to Run

### Build Solution
```bash
dotnet build SensorReportBot.sln
```

### Start Web API
```bash
dotnet run --project src/SensorReportBot.Api/SensorReportBot.Api.csproj
```

Swagger UI is enabled at `/swagger`.
Health check endpoint is available at `/health`.

## Chat over SignalR

Hub: `/hubs/chat`. Invoke `SendMessage(sessionId, userMessage)`; listen for `ReceiveChatResponse`, `ReceiveError`, `ReceiveHistory`, `ReceiveHistoryCleared`.

```json
{
  "isOnTopic": true,
  "isComplete": true,
  "replyMessage": "I have configured the report for Boiler Feed Pump Motor covering the last 5 days. Would you like me to queue the PDF report now?",
  "suggestedAction": "confirm_queue",
  "suggestedOptions": ["Yes", "Change Options"],
  "extractedParameters": { "assetId": 1, "assetName": "Boiler Feed Pump Motor", "signalIds": null, "timeRange": "5d", "fromDate": null, "toDate": null, "mode": "raw" }
}
```

Set `Serilog`/`Logging` level to `Debug` to see the full prompts sent to the model.
