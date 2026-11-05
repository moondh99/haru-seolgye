using System.Collections.Concurrent;
using Haru.Api.Agents;

namespace Haru.Api.Tests.Support;

/// <summary>Offline stand-in for the Copilot runner. Each agent gets a script: one step per call, and the last step repeats.</summary>
public sealed class FakeAgentRunner : IAgentRunner
{
    public delegate Task<string> Step(AgentRunRequest request, CancellationToken ct);

    private readonly ConcurrentDictionary<string, Step[]> _scripts = new();
    private readonly ConcurrentDictionary<string, int> _counts = new();
    private readonly ConcurrentQueue<AgentRunRequest> _calls = new();

    public IReadOnlyList<AgentRunRequest> Calls => _calls.ToArray();

    public int CountOf(string agentId) => _counts.GetValueOrDefault(agentId);

    public IEnumerable<string> PromptsOf(string agentId) => Calls.Where(c => c.AgentId == agentId).Select(c => c.UserPrompt);

    public FakeAgentRunner Script(string agentId, params Step[] steps)
    {
        _scripts[agentId] = steps;
        return this;
    }

    public FakeAgentRunner Reply(string agentId, params string[] replies) =>
        Script(agentId, replies.Select(Say).ToArray());

    public static Step Say(string text) => (_, _) => Task.FromResult(text);

    public static Step Fail(Exception? error = null) =>
        (_, _) => throw (error ?? new InvalidOperationException("agent is down"));

    public static Step Slow(TimeSpan delay, string text) => async (_, ct) =>
    {
        await Task.Delay(delay, ct);
        return text;
    };

    public static Step WithSignals(string text, params AgentSignal[] signals) => async (request, _) =>
    {
        if (request.OnSignal is { } emit)
            foreach (var signal in signals) await emit(signal);
        return text;
    };

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct)
    {
        _calls.Enqueue(request);
        if (!_scripts.TryGetValue(request.AgentId, out var steps))
            throw new InvalidOperationException($"No script for agent '{request.AgentId}'");

        var call = _counts.AddOrUpdate(request.AgentId, 1, (_, n) => n + 1);
        var text = await steps[Math.Min(call, steps.Length) - 1](request, ct);
        var model = request.AgentId is "scheduler" or "critic" ? "fake-smart" : "fake-fast";
        return new AgentRunResult(text, model, 100, 50, 1);
    }
}
