using System.Diagnostics;
using System.Text.Json;
using Haru.Api.Agents;
using Haru.Api.Infrastructure;
using Haru.Api.Mcp;
using Microsoft.Agents.AI.Workflows;

namespace Haru.Api.Workflow;

/// <summary>Serializes events that several parallel calls inside one executor want to send.</summary>
internal sealed class SerialEmitter(IWorkflowContext ctx, CancellationToken ct)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask EmitAsync(string name, object? payload)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try { await RunServices.Emit(ctx, name, payload, ct); }
        finally { _gate.Release(); }
    }
}

/// <summary>Step 1. The only agent that reads raw user text. It has no tools; its output is checked by the next step.</summary>
public sealed class IntakeExecutor : Executor<PlanInput, PlanContext>
{
    private readonly RunServices _s;

    public IntakeExecutor(RunServices s) : base("intake") => _s = s;

    public override async ValueTask<PlanContext> HandleAsync(PlanInput input, IWorkflowContext ctx, CancellationToken ct = default)
    {
        var text = TextSanitizer.Clean(input.Text, _s.Options.MaxInputChars, keepNewlines: true);
        var prompt = AgentCaller.ToJson(new { referenceDate = input.Day, weekday = Clock.Weekday(input.Day), text });

        var wire = await _s.Caller.CallAsync<IntakeWire>(
            _s.State, ctx, "intake", prompt,
            w => $"할 일 {w.Items?.Count ?? 0}개를 찾았습니다.", ct,
            validate: w => w.Items is null ? "the 'items' array is missing" : null);

        if (wire.Items is not { Count: > 0 })
        {
            _s.State.Failure = ("NO_TASKS", "입력에서 할 일을 찾지 못했습니다. 한 줄에 하나씩 적어 보세요.", true);
            throw new PlanException("NO_TASKS", _s.State.Failure.Value.Message);
        }

        return new PlanContext
        {
            Day = input.Day,
            WorkStart = input.WorkStart,
            WorkEnd = input.WorkEnd,
            LunchStart = input.LunchStart,
            LunchEnd = input.LunchEnd,
            Items = wire.Items.Take(Normalizer.MaxItems).ToList(),
        };
    }
}

/// <summary>Step 2 (code). Resolves date phrases with the MCP tool and turns raw items into validated tasks.</summary>
public sealed class NormalizeExecutor : Executor<PlanContext, PlanContext>
{
    private const string Role = "정리: 날짜 표현을 도구로 계산하고 값을 검사합니다.";
    private const int MaxShownCalls = 6;
    private const int MaxTrimmedWords = 2;
    private readonly RunServices _s;

    public NormalizeExecutor(RunServices s) : base("normalize") => _s = s;

    public override async ValueTask<PlanContext> HandleAsync(PlanContext c, IWorkflowContext ctx, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        await _s.AgentStarted(ctx, "normalize", Role, null, ct);

        var expressions = Normalizer.Expressions(c.Items);
        var resolved = await ResolveAsync(expressions, c.Day, ctx, ct);
        var result = Normalizer.Build(c, resolved);

        var fixedCount = result.Tasks.Count(t => t.Kind == "fixed");
        await _s.AgentCompleted(ctx, "normalize",
            $"날짜·시각 표현 {expressions.Count}개를 계산했고, 고정 일정 {fixedCount}개를 확인했습니다.", clock.ElapsedMilliseconds, ct);

        return c with
        {
            Items = [],
            Tasks = result.Tasks,
            FixedBlocks = result.FixedBlocks,
            PreDeferred = result.PreDeferred,
        };
    }

    private async Task<Dictionary<string, ResolvedDate>> ResolveAsync(
        IReadOnlyList<string> expressions, string day, IWorkflowContext ctx, CancellationToken ct)
    {
        var resolved = new Dictionary<string, ResolvedDate>();
        if (expressions.Count == 0) return resolved;

        var emitter = new SerialEmitter(ctx, ct);
        var limit = new SemaphoreSlim(4);
        var shown = 0;

        await Task.WhenAll(expressions.Select(async expression =>
        {
            await limit.WaitAsync(ct);
            try
            {
                var show = Interlocked.Increment(ref shown) <= MaxShownCalls;
                var callId = Guid.NewGuid().ToString("N");
                if (show)
                    await emitter.EmitAsync("tool_call", new { agent = "normalize", tool = "resolve_date", args = new { expression }, callId });

                ResolvedDate value;
                string summary;
                var ok = true;
                try
                {
                    (value, summary) = await ResolveOneAsync(expression, day, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    value = new ResolvedDate(false, null, null);
                    summary = "도구 호출에 실패했습니다.";
                    ok = false;
                }

                lock (resolved) resolved[expression] = value;
                if (show)
                    await emitter.EmitAsync("tool_result", new { agent = "normalize", tool = "resolve_date", ok, summary, callId });
            }
            finally
            {
                limit.Release();
            }
        }));

        return resolved;
    }

    /// <summary>
    /// The resolver is strict, so a phrase with trailing words ("오늘 17시까지 제출") fails.
    /// Drop trailing words, at most two, and try again. Nothing is ever added or guessed.
    /// </summary>
    private async Task<(ResolvedDate Value, string Summary)> ResolveOneAsync(string expression, string day, CancellationToken ct)
    {
        var words = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lastKeep = Math.Max(1, words.Length - MaxTrimmedWords);
        (ResolvedDate, string) last = (new ResolvedDate(false, null, null), "읽지 못함");
        for (var keep = words.Length; keep >= lastKeep; keep--)
        {
            var candidate = string.Join(' ', words.Take(keep));
            var json = await _s.Mcp.CallAsync("resolve_date", new { expression = candidate, referenceDate = day }, ct);
            var value = new ResolvedDate(
                json.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True,
                Text(json, "date"), Text(json, "time"));
            last = (value, ToolSummary.Of("resolve_date", json));
            if (value.Ok) break;
        }
        return last;
    }

    private static string? Text(JsonElement json, string name) =>
        json.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}

/// <summary>Step 3a (parallel). Estimates minutes for tasks that have none.</summary>
public sealed class EstimatorExecutor : Executor<PlanContext, Part>
{
    private readonly RunServices _s;

    public EstimatorExecutor(RunServices s) : base("estimator") => _s = s;

    public override async ValueTask<Part> HandleAsync(PlanContext c, IWorkflowContext ctx, CancellationToken ct = default)
    {
        var spec = _s.Catalog.Get("estimator");
        var unknown = c.PlannableTasks.Where(t => t.Minutes == 0).ToList();
        if (unknown.Count == 0)
        {
            await _s.AgentStarted(ctx, "estimator", spec.Role, null, ct);
            await _s.AgentCompleted(ctx, "estimator", "모든 할 일에 시간이 있어 건너뜁니다.", 0, ct);
            return new Part(c, null, null);
        }

        var wanted = unknown.Select(t => t.Id).ToHashSet();
        var prompt = AgentCaller.ToJson(new
        {
            tasks = unknown.Select(t => new { id = t.Id, title = t.Title, note = t.Note, minutes = (int?)null }),
        });

        var wire = await _s.Caller.TryCallAsync<EstimatorWire>(
            _s.State, ctx, "estimator", prompt,
            w => $"{unknown.Count}개 일의 소요 시간을 추정했습니다.",
            "시간을 추정하지 못해 기본값 30분을 씁니다.", ct,
            validate: w => w.Estimates is null ? "the 'estimates' array is missing" : null);

        return new Part(c, wire is null ? null : Normalizer.ReadEstimates(wire, wanted), null);
    }
}

/// <summary>Step 3b (parallel). Ranks tasks by urgency and impact.</summary>
public sealed class PrioritizerExecutor : Executor<PlanContext, Part>
{
    private readonly RunServices _s;

    public PrioritizerExecutor(RunServices s) : base("prioritizer") => _s = s;

    public override async ValueTask<Part> HandleAsync(PlanContext c, IWorkflowContext ctx, CancellationToken ct = default)
    {
        var spec = _s.Catalog.Get("prioritizer");
        var tasks = c.PlannableTasks.ToList();
        if (tasks.Count == 0)
        {
            await _s.AgentStarted(ctx, "prioritizer", spec.Role, null, ct);
            await _s.AgentCompleted(ctx, "prioritizer", "순서를 정할 일이 없어 건너뜁니다.", 0, ct);
            return new Part(c, null, null);
        }

        var wanted = tasks.Select(t => t.Id).ToHashSet();
        var prompt = AgentCaller.ToJson(new
        {
            today = c.Day,
            tasks = tasks.Select(t => new { id = t.Id, title = t.Title, note = t.Note, deadline = t.Deadline }),
        });

        var wire = await _s.Caller.TryCallAsync<PrioritizerWire>(
            _s.State, ctx, "prioritizer", prompt,
            w => $"{tasks.Count}개 일의 우선순위를 정했습니다.",
            "우선순위를 정하지 못해 기본값(보통)을 씁니다.", ct,
            validate: w => w.Priorities is null ? "the 'priorities' array is missing" : null);

        return new Part(c, null, wire is null ? null : Normalizer.ReadPriorities(wire, wanted));
    }
}

/// <summary>Step 4 (fan-in barrier, code). Waits for both branches and applies their results with safe defaults.</summary>
[SendsMessage(typeof(PlanContext))]
public sealed class MergeExecutor : Executor<Part>
{
    private readonly RunServices _s;
    private readonly List<Part> _parts = [];

    public MergeExecutor(RunServices s) : base("merge") => _s = s;

    public override ValueTask HandleAsync(Part part, IWorkflowContext ctx, CancellationToken ct = default)
    {
        lock (_parts) _parts.Add(part);
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask OnMessageDeliveryFinishedAsync(IWorkflowContext ctx, CancellationToken ct = default)
    {
        Part[] parts;
        lock (_parts)
        {
            parts = [.. _parts];
            _parts.Clear();
        }
        if (parts.Length == 0) return;

        var context = parts[0].Context;
        var minutes = parts.Select(p => p.Minutes).FirstOrDefault(m => m is not null);
        var priorities = parts.Select(p => p.Priorities).FirstOrDefault(m => m is not null);
        var tasks = Normalizer.Apply(context.Tasks, minutes, priorities);

        await RunServices.Emit(ctx, "tasks", new { tasks }, ct);
        await ctx.SendMessageAsync(context with { Tasks = tasks }, cancellationToken: ct);
    }
}
