using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Haru.Api.Agents;
using Haru.Api.Config;
using Haru.Api.Contracts;

namespace Haru.Api.Workflow;

public static class HaruTelemetry
{
    public const string MeterName = "Haru.Api";
    public static readonly Meter Meter = new(MeterName);
    public static readonly ActivitySource Workflow = new("Haru.Workflow");

    public static readonly Counter<long> Plans = Meter.CreateCounter<long>("haru.plans", description: "Plan runs by outcome");
    public static readonly Counter<long> Tokens = Meter.CreateCounter<long>("haru.tokens", description: "Model tokens by tier and direction");
    public static readonly Counter<long> Repairs = Meter.CreateCounter<long>("haru.repairs", description: "Plans changed by the deterministic repair step");
    public static readonly Histogram<double> PlanDuration = Meter.CreateHistogram<double>("haru.plan.duration", "ms", "Plan run time");
    public static readonly Histogram<double> AgentDuration = Meter.CreateHistogram<double>("haru.agent.duration", "ms", "Agent call time");
}

/// <summary>Facts about one plan run: clock, token use and the models that really answered.</summary>
public sealed class RunState(string runId)
{
    private readonly ConcurrentDictionary<string, string> _models = new();
    private long _input;
    private long _output;
    private int _schedulerRuns;

    public string RunId { get; } = runId;
    public Stopwatch Clock { get; } = Stopwatch.StartNew();
    public int SchedulerRuns => _schedulerRuns;
    public long InputTokens => Interlocked.Read(ref _input);
    public long OutputTokens => Interlocked.Read(ref _output);
    public (string Code, string Message, bool Recoverable)? Failure { get; set; }

    public int CountSchedulerRun() => Interlocked.Increment(ref _schedulerRuns);

    public void Record(AgentSpec spec, AgentRunResult result)
    {
        Interlocked.Add(ref _input, result.InputTokens);
        Interlocked.Add(ref _output, result.OutputTokens);
        var tier = spec.Tier.ToString().ToLowerInvariant();
        HaruTelemetry.Tokens.Add(result.InputTokens, new("tier", tier), new("direction", "input"));
        HaruTelemetry.Tokens.Add(result.OutputTokens, new("tier", tier), new("direction", "output"));
        HaruTelemetry.AgentDuration.Record(result.ElapsedMs, new KeyValuePair<string, object?>("agent", spec.Id));
        if (result.Model is { Length: > 0 } model) _models[spec.Id] = model;
    }

    public ModelPair Models(AgentCatalog catalog, HaruOptions options)
    {
        string Observed(ModelTier tier, string configured) =>
            catalog.All.Where(s => s.Tier == tier && _models.ContainsKey(s.Id))
                .Select(s => _models[s.Id]).LastOrDefault() ?? configured;
        return new ModelPair(Observed(ModelTier.Fast, options.ModelFast), Observed(ModelTier.Smart, options.ModelSmart));
    }
}

/// <summary>A workflow step failed in a way the user should see (for example, no tasks were found).</summary>
public sealed class PlanException(string code, string message, bool recoverable = true) : Exception(message)
{
    public string Code { get; } = code;
    public bool Recoverable { get; } = recoverable;
}
