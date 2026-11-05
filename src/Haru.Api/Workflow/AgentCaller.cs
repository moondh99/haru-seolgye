using System.Text.Json;
using Haru.Api.Agents;
using Haru.Api.Contracts;
using Haru.Api.Mcp;
using Microsoft.Agents.AI.Workflows;

namespace Haru.Api.Workflow;

/// <summary>A progress event that the workflow stream carries to the SSE writer unchanged.</summary>
public sealed class HaruEvent(string name, object? payload = null) : WorkflowEvent(payload)
{
    public string Name { get; } = name;
}

/// <summary>What every executor needs for one run. The workflow is built per request, so this object is per request too.</summary>
public sealed class RunServices(
    RunState state, AgentCaller caller, IMcpGateway mcp, AgentCatalog catalog, Config.HaruOptions options)
{
    public RunState State { get; } = state;
    public AgentCaller Caller { get; } = caller;
    public IMcpGateway Mcp { get; } = mcp;
    public AgentCatalog Catalog { get; } = catalog;
    public Config.HaruOptions Options { get; } = options;

    public static ValueTask Emit(IWorkflowContext ctx, string name, object? payload, CancellationToken ct) =>
        ctx.AddEventAsync(new HaruEvent(name, payload), ct);

    public ValueTask AgentStarted(IWorkflowContext ctx, string agent, string role, string? model, CancellationToken ct) =>
        Emit(ctx, "agent_started", new { agent, role, model }, ct);

    public ValueTask AgentCompleted(IWorkflowContext ctx, string agent, string summary, long elapsedMs, CancellationToken ct) =>
        Emit(ctx, "agent_completed", new { agent, summary, elapsedMs }, ct);
}

/// <summary>Calls one agent, streams its progress as events, parses the JSON reply and retries once on a bad reply.</summary>
public sealed class AgentCaller(IAgentRunner runner, AgentCatalog catalog, ILogger<AgentCaller> log)
{
    public async Task<T> CallAsync<T>(
        RunState state, IWorkflowContext ctx, string agentId, string prompt, Func<T, string> summarize,
        CancellationToken ct, Func<T, string?>? validate = null) where T : class
    {
        var spec = catalog.Get(agentId);
        await RunServices.Emit(ctx, "agent_started", new { agent = agentId, role = spec.Role, model = catalog.ModelFor(spec) }, ct);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var attemptPrompt = prompt;
        string reason = "";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await runner.RunAsync(new AgentRunRequest(agentId, attemptPrompt, signal => Forward(ctx, agentId, signal, ct)), ct);
            state.Record(spec, result);

            if (AgentJson.TryParse<T>(result.Text, out var value, out reason))
            {
                reason = validate?.Invoke(value!) ?? "";
                if (reason.Length == 0)
                {
                    await RunServices.Emit(ctx, "agent_completed",
                        new { agent = agentId, summary = summarize(value!), elapsedMs = clock.ElapsedMilliseconds }, ct);
                    return value!;
                }
            }

            log.LogWarning("Agent {Agent} gave an unusable reply (attempt {Attempt}): {Reason}", agentId, attempt + 1, reason);
            attemptPrompt = prompt + $"\n\nYour last reply was rejected: {reason}. Reply again with the JSON object only.";
        }

        throw new AgentOutputException(agentId, reason);
    }

    /// <summary>Like <see cref="CallAsync{T}"/> but returns null after a failure and tells the UI that a default was used.</summary>
    public async Task<T?> TryCallAsync<T>(
        RunState state, IWorkflowContext ctx, string agentId, string prompt, Func<T, string> summarize,
        string fallbackSummary, CancellationToken ct, Func<T, string?>? validate = null) where T : class
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return await CallAsync(state, ctx, agentId, prompt, summarize, ct, validate);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Agent {Agent} failed and a default is used: {Type}", agentId, e.GetType().Name);
            await RunServices.Emit(ctx, "agent_completed",
                new { agent = agentId, summary = fallbackSummary, elapsedMs = clock.ElapsedMilliseconds }, ct);
            return null;
        }
    }

    private static ValueTask Forward(IWorkflowContext ctx, string agent, AgentSignal signal, CancellationToken ct) => signal switch
    {
        DeltaSignal d => RunServices.Emit(ctx, "agent_delta", new { agent, text = d.Text }, ct),
        ToolCallSignal c => RunServices.Emit(ctx, "tool_call", new { agent, tool = c.Tool, args = c.Args }, ct),
        ToolResultSignal r => RunServices.Emit(ctx, "tool_result", new { agent, tool = r.Tool, ok = r.Ok, summary = r.Summary }, ct),
        _ => ValueTask.CompletedTask,
    };

    public static string ToJson(object value) => JsonSerializer.Serialize(value, HaruJson.Options);
}
