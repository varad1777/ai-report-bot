namespace SensorReportBot.Infrastructure.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SensorReportBot.Application.DTOs;
using SensorReportBot.Application.Interfaces;
using ConfigLayaOptions = SensorReportBot.Infrastructure.Configuration.LayaOptions;
using Laya;

/// <summary>
/// In-process native .NET 9 System-1 Decision Engine.
/// Replaces external Python/Docker service with direct ONNX Runtime inference (~15-30ms).
/// Zero Python runtime, zero container overhead, zero network latency.
/// Implements ILayaClient directly in-process.
/// </summary>
public class OnnxDecisionEngine : ILayaClient, IDisposable
{
    private readonly ConfigLayaOptions _options;
    private readonly ILogger<OnnxDecisionEngine> _logger;
    private readonly LayaEngine? _engine;
    private readonly bool _initialized;
    private int _disposed;

    public OnnxDecisionEngine(
        IOptions<ConfigLayaOptions> options,
        ILogger<OnnxDecisionEngine> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (!_options.Enabled)
        {
            _logger.LogInformation("Native .NET decision engine is disabled by configuration.");
            return;
        }

        try
        {
            var resolvedPath = ResolveModelDirectory(_options.ModelDirectory);
            if (string.IsNullOrEmpty(resolvedPath) || !Directory.Exists(resolvedPath))
            {
                _logger.LogWarning("⚠️ Could not find ONNX model directory (configured: '{ConfiguredPath}'). In-process engine disabled.", _options.ModelDirectory);
                return;
            }

            _logger.LogInformation("🚀 Initializing native in-process .NET ONNX engine from '{Path}'...", resolvedPath);
            var sw = Stopwatch.StartNew();
            _engine = LayaEngine.FromDirectory(resolvedPath);
            sw.Stop();
            _initialized = true;

            _logger.LogInformation("✅ Native .NET Decision Engine initialized in {Elapsed} ms. Checkpoint: {Checkpoint}, MaxLen: {MaxLen}.",
                sw.ElapsedMilliseconds,
                _engine.Config.Encoder ?? "laya-multilingual",
                _engine.Config.MaxLen);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize native .NET ONNX engine from '{ModelDirectory}'.", _options.ModelDirectory);
        }
    }

    public bool IsEnabled => _options.Enabled && _initialized && _engine != null;

    public Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        return Task.FromResult(IsEnabled);
    }

    public Task<LayaAskResponse?> AskAsync(
        string context,
        string question,
        string type,
        IReadOnlyList<string>? choices = null,
        CancellationToken ct = default)
    {
        if (!IsEnabled || _engine == null)
        {
            return Task.FromResult<LayaAskResponse?>(null);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var questions = new QuestionSet();
            string normalizedType = (type ?? "noul").Trim().ToLowerInvariant();

            if (normalizedType == "choice" && choices != null && choices.Count > 0)
            {
                questions.Add("q1", Question.Choice(question, choices.ToArray()));
            }
            else if (normalizedType == "score" && choices != null && choices.Count > 0)
            {
                questions.Add("q1", Question.Score(question, choices.Cast<object?>().ToArray()));
            }
            else
            {
                questions.Add("q1", Question.Noul(question));
            }

            var result = _engine.Predict(context, questions);
            sw.Stop();

            var answer = result["q1"];
            object? answerValue = null;
            double probability = answer.Confidence;
            Dictionary<string, object>? rawData = new()
            {
                ["type"] = answer.Type.ToString(),
                ["confidence"] = answer.Confidence,
                ["answer_confidence"] = answer.AnswerConfidence,
                ["act_probability"] = answer.Action.ActProbability
            };

            if (answer is ChoiceAnswer choiceAnswer)
            {
                answerValue = choiceAnswer.Choice;
                probability = choiceAnswer[choiceAnswer.Choice];
                rawData["probabilities"] = choiceAnswer.Probabilities.ToDictionary(k => k.Key, v => (object)v.Value);
            }
            else if (answer is ScoreAnswer scoreAnswer)
            {
                answerValue = scoreAnswer.Score;
                probability = scoreAnswer.Confidence;
                rawData["most_likely_level"] = scoreAnswer.MostLikelyLevel;
            }
            else if (answer is NoulAnswer noulAnswer)
            {
                answerValue = noulAnswer.Value;
                probability = noulAnswer.Probability;
            }

            var response = new LayaAskResponse
            {
                Answer = answerValue,
                Probability = probability,
                Raw = rawData
            };

            string decisionStr = response.Answer?.ToString() ?? "null";
            string banner =
                $"\n⚡ [NATIVE .NET SYSTEM-1 INFERENCE] ({sw.ElapsedMilliseconds} ms)\n" +
                $"   • Question: {question}\n" +
                $"   • Type:     {type}\n" +
                $"   • Decision: {decisionStr} (probability: {response.Probability:P1}, act: {answer.Action.ActProbability:P1})\n";

            Console.WriteLine(banner);
            _logger.LogInformation("{Banner}", banner);

            return Task.FromResult<LayaAskResponse?>(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running native .NET inference for question '{Question}'.", question);
            return Task.FromResult<LayaAskResponse?>(null);
        }
    }

    private static string? ResolveModelDirectory(string configuredPath)
    {
        // 1. Check explicit environment variables first
        var envDir = Environment.GetEnvironmentVariable("LAYA_ONNX_DIR")
                     ?? Environment.GetEnvironmentVariable("LAYA_MODEL_PATH");
        if (!string.IsNullOrWhiteSpace(envDir) && Directory.Exists(envDir))
        {
            return Path.GetFullPath(envDir);
        }

        // 2. Candidate relative search paths
        var candidates = new List<string>
        {
            configuredPath,
            Path.Combine(AppContext.BaseDirectory, configuredPath),
            Path.Combine(Directory.GetCurrentDirectory(), configuredPath),
            Path.Combine(Directory.GetCurrentDirectory(), "..", configuredPath),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", configuredPath),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", configuredPath),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", configuredPath),
            // Standard edge / docker absolute locations
            "/app/models/laya-multilingual/multilingual",
            "C:\\models\\laya-multilingual\\multilingual"
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var full = Path.GetFullPath(candidate);
                if (Directory.Exists(full) && File.Exists(Path.Combine(full, "model.onnx")))
                {
                    return full;
                }
            }
            catch
            {
                // Ignore invalid path formats during probing
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _engine?.Dispose();
    }
}
