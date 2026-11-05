using System.Text.Json;
using Haru.Api.Contracts;

namespace Haru.Api.Agents;

public abstract record AgentSignal;
public sealed record DeltaSignal(string Text) : AgentSignal;
public sealed record ToolCallSignal(string Tool, IReadOnlyDictionary<string, object?> Args) : AgentSignal;
public sealed record ToolResultSignal(string Tool, bool Ok, string Summary) : AgentSignal;

public sealed record AgentRunRequest(string AgentId, string UserPrompt, Func<AgentSignal, ValueTask>? OnSignal = null);

public sealed record AgentRunResult(string Text, string? Model, long InputTokens, long OutputTokens, long ElapsedMs);

/// <summary>Runs one agent turn and returns its final text. The real runner uses the Copilot SDK; tests use a fake.</summary>
public interface IAgentRunner
{
    Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct);
}

public sealed class AgentOutputException(string agentId, string reason)
    : Exception($"Agent '{agentId}' returned unusable output: {reason}")
{
    public string AgentId { get; } = agentId;
}

/// <summary>Turns "JSON only" model output into typed records. It never trusts the shape: callers validate the result.</summary>
public static class AgentJson
{
    public static string Extract(string text)
    {
        var s = text.Trim();
        var first = s.IndexOf('{');
        var last = s.LastIndexOf('}');
        return first >= 0 && last > first ? s[first..(last + 1)] : s;
    }

    public static bool TryParse<T>(string text, out T? value, out string error) where T : class
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "empty reply"; return false; }
        try
        {
            value = JsonSerializer.Deserialize<T>(Extract(text), HaruJson.Lenient);
            if (value is null) { error = "reply was not a JSON object"; return false; }
            error = "";
            return true;
        }
        catch (JsonException e)
        {
            error = "invalid JSON (" + e.Message.Split('.')[0] + ")";
            return false;
        }
    }
}
