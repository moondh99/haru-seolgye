using Haru.Api.Agents;
using Haru.Api.Config;
using Haru.Api.Contracts;
using Haru.Api.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Haru.Api.Tests.Support;

/// <summary>The SSE events of one plan run, with shortcuts for the assertions.</summary>
public sealed class PlanRun(IReadOnlyList<SseEvent> events)
{
    public IReadOnlyList<SseEvent> Events => events;

    public IReadOnlyList<string> Names => events.Select(e => e.Name).ToList();

    public IEnumerable<SseEvent> Of(string name) => events.Where(e => e.Name == name);

    public bool Has(string name) => events.Any(e => e.Name == name);

    public PlanResult? Plan => Of("plan").Select(e => e.Data.Deserialize<PlanResult>(HaruJson.Options)).FirstOrDefault();

    public string? ErrorCode => Of("error").Select(e => e.Data.GetProperty("code").GetString()).FirstOrDefault();

    public IEnumerable<string> StartedAgents =>
        Of("agent_started").Select(e => e.Data.GetProperty("agent").GetString()!);

    public IEnumerable<string> ToolNames =>
        Of("tool_call").Select(e => e.Data.GetProperty("tool").GetString()!);
}

/// <summary>Runs PlanService against the scripted fakes and captures the SSE output.</summary>
public sealed class PlanHarness
{
    public PlanHarness(Func<HaruOptions, HaruOptions>? configure = null)
    {
        var options = new HaruOptions { PlanTimeoutSeconds = 20 };
        Options = configure?.Invoke(options) ?? options;
    }

    public HaruOptions Options { get; }

    public FakeAgentRunner Runner { get; } = new();

    public FakeMcpGateway Mcp { get; } = new();

    public async Task<PlanRun> RunAsync(PlanInput? input = null, CancellationToken ct = default)
    {
        var catalog = new AgentCatalog(Options);
        var caller = new AgentCaller(Runner, catalog, NullLogger<AgentCaller>.Instance);
        var service = new PlanService(Options, catalog, caller, Mcp, NullLogger<PlanService>.Instance);

        await using var capture = new SseCapture();
        await capture.StartAsync();
        await service.RunAsync(input ?? Scenario.Input(), capture.Stream, ct);
        return new PlanRun(await capture.FinishAsync());
    }
}
