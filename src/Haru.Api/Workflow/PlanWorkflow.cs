using Microsoft.Agents.AI.Workflows;
using AgentWorkflow = Microsoft.Agents.AI.Workflows.Workflow;

namespace Haru.Api.Workflow;

/// <summary>
/// The plan workflow. Patterns: sequential, concurrent (fan-out and fan-in), loop (validate and revise) and conditional routing.
/// Agents reason; code steps (normalize, merge, validator, repair, finalize) decide and check.
/// </summary>
public static class PlanWorkflow
{
    public static AgentWorkflow Build(RunServices s)
    {
        var intake = new IntakeExecutor(s);
        var normalize = new NormalizeExecutor(s);
        var estimator = new EstimatorExecutor(s);
        var prioritizer = new PrioritizerExecutor(s);
        var merge = new MergeExecutor(s);
        var scheduler = new SchedulerExecutor(s);
        var validator = new ValidatorExecutor(s);
        var repair = new RepairExecutor(s);
        var critic = new CriticExecutor(s);
        var coach = new CoachExecutor(s);
        var finalize = new FinalizeExecutor(s);

        // The three validator edges and the two critic edges are mutually exclusive: a message that matches none would end the run silently.
        return new WorkflowBuilder(intake)
            .AddEdge(intake, normalize)
            .AddFanOutEdge(normalize, [estimator, prioritizer])
            .AddFanInBarrierEdge([estimator, prioritizer], merge)
            .AddEdge(merge, scheduler)
            .AddEdge(scheduler, validator)
            .AddEdge<PlanContext>(validator, critic, Routes.ToCritic)
            .AddEdge<PlanContext>(validator, scheduler, Routes.BackToScheduler)
            .AddEdge<PlanContext>(validator, repair, Routes.ToRepair)
            .AddEdge(repair, critic)
            .AddEdge<PlanContext>(critic, scheduler, Routes.Revise)
            .AddEdge<PlanContext>(critic, coach, Routes.Accept)
            .AddEdge(coach, finalize)
            .WithOutputFrom(finalize)
            .Build();
    }
}

/// <summary>Routing rules of the plan workflow. Each pair of rules is mutually exclusive and covers every case.</summary>
public static class Routes
{
    public static bool ToCritic(PlanContext? c) => c is { Report.Valid: true } or { OnlyFixedViolations: true };

    public static bool BackToScheduler(PlanContext? c) =>
        c is { Report.Valid: false, OnlyFixedViolations: false } && c.Iteration < ScheduleChecks.MaxSchedulerRuns;

    public static bool ToRepair(PlanContext? c) =>
        c is { Report.Valid: false, OnlyFixedViolations: false } && c.Iteration >= ScheduleChecks.MaxSchedulerRuns;

    public static bool Revise(PlanContext? c) => c is { Decision: "revise" };

    public static bool Accept(PlanContext? c) => c is not { Decision: "revise" };
}
