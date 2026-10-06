namespace SensorReportBot.Application.Exceptions;

using System;

/// <summary>
/// Raised when the language model cannot produce a usable reply (endpoint down, timeout, empty or
/// unparseable output after one retry). The pipeline surfaces this as an error instead of faking a
/// chatbot answer.
/// </summary>
public class LlmUnavailableException : Exception
{
    public LlmUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
