using System.Diagnostics;
using System.Text.Json;
using Haru.Api.Config;
using Haru.Api.Contracts;
using Haru.Api.Infrastructure;
using Haru.Api.Mcp;
using Microsoft.Agents.AI.Workflows;

namespace Haru.Api.Workflow;

/// <summary>Calls the deterministic rule checker (an MCP tool) and applies the user-choice filter to its report.</summary>
public static class ScheduleChecks
{
    /// <summary>The scheduler runs at most this many times in one plan.</summary>
    public const int MaxSchedulerRuns = 2;

    public static async Task<ValidationReport> ValidateAsync(
        IMcpGateway mcp, HaruOptions options, PlanContext c, IReadOnlyList<Block> blocks, CancellationToken ct)
    {
        var tasks = c.Tasks.ToDictionary(t => t.Id);
        var items = blocks.Select(b =>
        {
            var item = new Dictionary<string, object?>
            {
                ["id"] = b.Id, ["title"] = b.Title, ["start"] = b.Start, ["end"] = b.End, ["kind"] = b.Kind,
            };
            if (b.Kind == "task" && b.TaskId is not null && tasks.TryGetValue(b.TaskId, out var t)
                && Normalizer.DeadlineTime(t, c.Day, c.WorkEnd) is { } deadline)
                item["deadlineTime"] = deadline;
            return item;
        }).ToList();

        var json = await mcp.CallAsync("validate_schedule", new
        {
            workStart = c.WorkStart,
            workEnd = c.WorkEnd,
            blocks = items,
            bufferMinutes = options.BufferMinutes,
            maxLoad = options.MaxLoad,
        }, ct);

        var report = json.Deserialize<ValidationReport>(HaruJson.Lenient)
                     ?? throw new McpToolException("validate_schedule", "empty result");
        return Repair.IgnoreUserChoices(report with { Violations = report.Violations ?? [] }, blocks);
    }

    public static int LoadPercent(ValidationReport? report) => (int)Math.Round((report?.LoadRatio ?? 0) * 100);

    public static string Summary(ValidationReport report)
    {
        if (report.Valid) return $"규칙을 모두 통과했습니다. 부하 {LoadPercent(report)}%.";
        var codes = string.Join(", ", report.Violations.Select(v => v.Code).Distinct().Take(3));
        return $"위반 {report.Violations.Length}건({codes}). 부하 {LoadPercent(report)}%.";
    }
}

/// <summary>Step 5. Places tasks in free time. It may call two read-only tools and answers with JSON only.</summary>
public sealed class SchedulerExecutor : Executor<PlanContext, PlanContext>
{
    private readonly RunServices _s;

    public SchedulerExecutor(RunServices s) : base("scheduler") => _s = s;

    public override async ValueTask<PlanContext> HandleAsync(PlanContext c, IWorkflowContext ctx, CancellationToken ct = default)
    {
        var spec = _s.Catalog.Get("scheduler");
        var tasks = c.PlannableTasks.ToList();
        var iteration = c.Iteration + 1;

        IReadOnlyList<Block> blocks;
        IReadOnlyList<Deferred> deferred;

        if (tasks.Count == 0)
        {
            await _s.AgentStarted(ctx, "scheduler", spec.Role, null, ct);
            await _s.AgentCompleted(ctx, "scheduler", "배치할 일이 없어 건너뜁니다.", 0, ct);
            blocks = ScheduleAssembler.Number(c.FixedBlocks);
            deferred = [];
        }
        else
        {
            var wire = await _s.Caller.CallAsync<SchedulerWire>(
                _s.State, ctx, "scheduler", BuildPrompt(c, tasks),
                w => $"{w.Blocks?.Count ?? 0}개를 배치하고 {w.Deferred?.Count ?? 0}개를 미뤘습니다.", ct,
                validate: w => w.Blocks is null ? "the 'blocks' array is missing" : null);
            (blocks, deferred) = ScheduleAssembler.Assemble(c, wire);
        }

        _s.State.CountSchedulerRun();
        await RunServices.Emit(ctx, "schedule",
            new { iteration, blocks, deferred = c.PreDeferred.Concat(deferred).ToList() }, ct);

        return c with
        {
            Blocks = blocks,
            SchedDeferred = deferred,
            Iteration = iteration,
            Report = null,
            OnlyFixedViolations = false,
            Critique = null,
        };
    }

    private string BuildPrompt(PlanContext c, List<TaskItem> tasks)
    {
        object? revision = null;
        if (c.Iteration > 0)
        {
            revision = new
            {
                previousBlocks = c.Blocks.Where(b => b.Kind == "task").Select(b => new
                {
                    id = b.Id, taskId = b.TaskId, title = b.Title, start = b.Start, end = b.End, kind = "task",
                }),
                violations = c.Report is { Valid: false } r && !c.OnlyFixedViolations ? r.Violations : [],
                critique = c.Critique,
            };
        }

        return AgentCaller.ToJson(new
        {
            day = c.Day,
            workStart = c.WorkStart,
            workEnd = c.WorkEnd,
            bufferMinutes = _s.Options.BufferMinutes,
            maxLoad = _s.Options.MaxLoad,
            fixedBlocks = c.FixedBlocks.Select(b => new { id = b.Id, title = b.Title, start = b.Start, end = b.End, kind = b.Kind }),
            tasks = tasks.Select(t => new
            {
                id = t.Id, title = t.Title, minutes = t.Minutes, priority = t.Priority,
                deadlineTime = Normalizer.DeadlineTime(t, c.Day, c.WorkEnd), note = t.Note,
            }),
            revision,
        });
    }
}

/// <summary>Step 6 (code). Checks the schedule with the rule engine. The model's own check is never trusted.</summary>
public sealed class ValidatorExecutor : Executor<PlanContext, PlanContext>
{
    private const string Role = "검증: 규칙 검사 도구로 시간표를 확인합니다.";
    private readonly RunServices _s;

    public ValidatorExecutor(RunServices s) : base("validator") => _s = s;

    public override async ValueTask<PlanContext> HandleAsync(PlanContext c, IWorkflowContext ctx, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        await _s.AgentStarted(ctx, "validator", Role, null, ct);
        var callId = Guid.NewGuid().ToString("N");
        await RunServices.Emit(ctx, "tool_call",
            new { agent = "validator", tool = "validate_schedule", args = new { blocks = $"{c.Blocks.Count}개" }, callId }, ct);

        var report = await ScheduleChecks.ValidateAsync(_s.Mcp, _s.Options, c, c.Blocks, ct);

        await RunServices.Emit(ctx, "tool_result",
            new { agent = "validator", tool = "validate_schedule", ok = true, summary = ScheduleChecks.Summary(report), callId }, ct);
        await RunServices.Emit(ctx, "validation", new { iteration = c.Iteration, report }, ct);
        await _s.AgentCompleted(ctx, "validator", ScheduleChecks.Summary(report), clock.ElapsedMilliseconds, ct);

        return c with { Report = report, OnlyFixedViolations = Repair.OnlyFixed(report, c.Blocks) };
    }
}

/// <summary>Step 7 (code). Last resort after the scheduler has had its tries: move the least important blocking task to another day.</summary>
public sealed class RepairExecutor : Executor<PlanContext, PlanContext>
{
    private const string Role = "보정: 남은 규칙 위반을 코드로 고칩니다.";
    private const int MaxMoves = 10;
    private readonly RunServices _s;

    public RepairExecutor(RunServices s) : base("repair") => _s = s;

    public override async ValueTask<PlanContext> HandleAsync(PlanContext c, IWorkflowContext ctx, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        await _s.AgentStarted(ctx, "repair", Role, null, ct);

        var tasks = c.Tasks.ToDictionary(t => t.Id);
        var blocks = c.Blocks.ToList();
        var deferred = c.SchedDeferred.ToList();
        var report = c.Report ?? throw new PlanException("WORKFLOW_FAILED", "검증 결과가 없습니다.");
        var moved = 0;

        while (moved < MaxMoves && !report.Valid && !Repair.OnlyFixed(report, blocks))
        {
            var victim = Repair.PickVictim(blocks, report, tasks);
            if (victim is null) break;

            blocks.Remove(victim);
            deferred.Add(new Deferred(victim.TaskId!, victim.Title, Repair.Reason));
            moved++;
            report = await ScheduleChecks.ValidateAsync(_s.Mcp, _s.Options, c, blocks, ct);
        }

        var numbered = ScheduleAssembler.Number(blocks);
        if (moved > 0) report = await ScheduleChecks.ValidateAsync(_s.Mcp, _s.Options, c, numbered, ct);

        var next = c with
        {
            Blocks = numbered,
            SchedDeferred = deferred,
            Report = report,
            OnlyFixedViolations = Repair.OnlyFixed(report, numbered),
            Repaired = moved > 0,
        };

        if (moved > 0)
        {
            HaruTelemetry.Repairs.Add(1);
            await RunServices.Emit(ctx, "schedule",
                new { iteration = c.Iteration, blocks = numbered, deferred = c.PreDeferred.Concat(deferred).ToList() }, ct);
            await RunServices.Emit(ctx, "validation", new { iteration = c.Iteration, report }, ct);
        }

        await _s.AgentCompleted(ctx, "repair",
            moved > 0 ? $"할 일 {moved}개를 다른 날로 옮겨 위반을 줄였습니다." : "옮길 수 있는 할 일이 없어 그대로 둡니다.",
            clock.ElapsedMilliseconds, ct);
        return next;
    }
}

/// <summary>Step 8. Pre-mortem on a valid plan. It has no tools. It may ask for one more scheduler run, but only inside a time budget.</summary>
public sealed class CriticExecutor : Executor<PlanContext, PlanContext>
{
    private readonly RunServices _s;

    public CriticExecutor(RunServices s) : base("critic") => _s = s;

    public override async ValueTask<PlanContext> HandleAsync(PlanContext c, IWorkflowContext ctx, CancellationToken ct = default)
    {
        var prompt = AgentCaller.ToJson(new
        {
            day = c.Day,
            workStart = c.WorkStart,
            workEnd = c.WorkEnd,
            loadPercent = ScheduleChecks.LoadPercent(c.Report),
            blocks = c.Blocks.Select(b => new { id = b.Id, taskId = b.TaskId, title = b.Title, start = b.Start, end = b.End, kind = b.Kind }),
            deferred = c.PreDeferred.Concat(c.SchedDeferred).Select(d => new { taskId = d.TaskId, title = d.Title }),
            tasks = c.Tasks.Select(t => new { id = t.Id, title = t.Title, priority = t.Priority, deadline = t.Deadline }),
            iteration = c.Iteration,
        });

        var wire = await _s.Caller.TryCallAsync<CriticWire>(
            _s.State, ctx, "critic", prompt,
            w => string.Equals(w.Decision, "revise", StringComparison.OrdinalIgnoreCase)
                ? "고칠 점을 찾았습니다." : $"위험 {w.Risks?.Count ?? 0}개를 짚었습니다.",
            "비판 단계를 건너뛰고 계획을 그대로 씁니다.", ct,
            validate: w => w.Decision is null ? "the 'decision' field is missing" : null);

        var canRevise = c.Iteration < ScheduleChecks.MaxSchedulerRuns
                        && !c.OnlyFixedViolations
                        && _s.State.Clock.Elapsed.TotalSeconds <= _s.Options.CritiqueBudgetSeconds;
        var revision = TextSanitizer.Clean(wire?.Revision, 200);
        var revise = canRevise && revision.Length > 0
                     && string.Equals(wire?.Decision, "revise", StringComparison.OrdinalIgnoreCase);
        var risks = CleanRisks(wire?.Risks);

        await RunServices.Emit(ctx, "critique",
            new { iteration = c.Iteration, decision = revise ? "revise" : "approve", risks }, ct);

        return c with { Decision = revise ? "revise" : "approve", Risks = risks, Critique = revise ? revision : null };
    }

    private static IReadOnlyList<Risk> CleanRisks(List<CriticWireRisk>? wire)
    {
        var risks = new List<Risk>();
        foreach (var r in wire ?? [])
        {
            var title = TextSanitizer.Clean(r.Title, 60);
            if (title.Length == 0) continue;
            var severity = r.Severity?.Trim().ToLowerInvariant();
            risks.Add(new Risk(title, TextSanitizer.Clean(r.Mitigation, 60),
                severity is "low" or "medium" or "high" ? severity : "medium"));
            if (risks.Count == 3) break;
        }
        return risks;
    }
}

/// <summary>Step 9. Writes the short Korean summary. If the model fails, code writes a plain one.</summary>
public sealed class CoachExecutor : Executor<PlanContext, PlanContext>
{
    private readonly RunServices _s;

    public CoachExecutor(RunServices s) : base("coach") => _s = s;

    public override async ValueTask<PlanContext> HandleAsync(PlanContext c, IWorkflowContext ctx, CancellationToken ct = default)
    {
        var deferred = c.PreDeferred.Concat(c.SchedDeferred).ToList();
        var scheduled = c.Blocks.Count(b => b.Kind == "task");
        var load = ScheduleChecks.LoadPercent(c.Report);

        var prompt = AgentCaller.ToJson(new
        {
            day = c.Day,
            loadPercent = load,
            scheduledCount = scheduled,
            deferred = deferred.Select(d => new { title = d.Title, reason = d.Reason }),
            risks = c.Risks.Select(r => new { title = r.Title, severity = r.Severity }),
            repaired = c.Repaired,
        });

        var wire = await _s.Caller.TryCallAsync<CoachWire>(
            _s.State, ctx, "coach", prompt, _ => "하루 요약을 썼습니다.",
            "요약을 쓰지 못해 기본 문장을 씁니다.", ct,
            validate: w => string.IsNullOrWhiteSpace(w.Summary) ? "the 'summary' text is missing" : null);

        var summary = TextSanitizer.Clean(wire?.Summary, 300);
        if (summary.Length == 0) summary = Fallback(scheduled, deferred.Count, load);
        return c with { Summary = summary };
    }

    public static string Fallback(int scheduled, int deferred, int load)
    {
        var text = $"오늘은 할 일 {scheduled}개를 시간표에 넣었고, 부하는 {load}%입니다.";
        return deferred > 0 ? $"{text} {deferred}개는 다른 날로 옮겼습니다." : text;
    }
}

/// <summary>Step 10. Builds the final result and yields it as the workflow output.</summary>
[YieldsOutput(typeof(PlanResult))]
public sealed class FinalizeExecutor : Executor<PlanContext>
{
    private readonly RunServices _s;

    public FinalizeExecutor(RunServices s) : base("finalize") => _s = s;

    public override async ValueTask HandleAsync(PlanContext c, IWorkflowContext ctx, CancellationToken ct = default)
    {
        var report = c.Report ?? new ValidationReport(false, 0, 0, 0, []);
        var meta = new PlanMeta("ai", true, _s.State.SchedulerRuns, c.Repaired,
            _s.State.Clock.ElapsedMilliseconds, _s.State.Models(_s.Catalog, _s.Options));

        var result = new PlanResult(
            c.Day, c.WorkStart, c.WorkEnd,
            [.. c.Tasks],
            [.. ScheduleAssembler.Number(c.Blocks)],
            [.. c.PreDeferred, .. c.SchedDeferred],
            [.. c.Risks.Take(3)],
            c.Summary, report, meta);

        await ctx.YieldOutputAsync(result, ct);
    }
}
