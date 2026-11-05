using Haru.Api.Contracts;

namespace Haru.Api.Workflow;

public sealed record PlanInput(
    string Day, string WorkStart, string WorkEnd, string LunchStart, string LunchEnd, string Text);

/// <summary>Immutable message passed between executors. It holds structured data only, never chat history.</summary>
public sealed record PlanContext
{
    public required string Day { get; init; }
    public required string WorkStart { get; init; }
    public required string WorkEnd { get; init; }
    public string LunchStart { get; init; } = "";
    public string LunchEnd { get; init; } = "";
    public string RawText { get; init; } = "";
    public IReadOnlyList<IntakeWireItem> Items { get; init; } = [];

    public IReadOnlyList<TaskItem> Tasks { get; init; } = [];
    public IReadOnlyList<Block> FixedBlocks { get; init; } = [];
    public IReadOnlyList<Deferred> PreDeferred { get; init; } = [];
    public IReadOnlyList<Deferred> SchedDeferred { get; init; } = [];
    public IReadOnlyList<Block> Blocks { get; init; } = [];

    public int Iteration { get; init; }
    public ValidationReport? Report { get; init; }
    public bool OnlyFixedViolations { get; init; }
    public string? Critique { get; init; }
    public string Decision { get; init; } = "";
    public IReadOnlyList<Risk> Risks { get; init; } = [];
    public string Summary { get; init; } = "";
    public bool Repaired { get; init; }

    public IEnumerable<TaskItem> PlannableTasks => Tasks.Where(t => t.Kind == "task");
}

/// <summary>Result of one parallel branch (estimator or prioritizer). The context travels along so the barrier can merge.</summary>
public sealed record Part(
    PlanContext Context, IReadOnlyDictionary<string, int>? Minutes, IReadOnlyDictionary<string, int>? Priorities);

// Wire records: what the model returns. Every member is nullable because model output is never trusted.
public sealed record IntakeWire(List<IntakeWireItem>? Items);

public sealed record IntakeWireItem(
    string? Title, double? Minutes, string? DeadlineText, string? StartText, string? EndText, bool? Fixed, string? Note);

public sealed record EstimatorWire(List<EstimateWireItem>? Estimates);

public sealed record EstimateWireItem(string? Id, double? Minutes, string? Why);

public sealed record PrioritizerWire(List<PriorityWireItem>? Priorities);

public sealed record PriorityWireItem(string? Id, double? Priority, string? Why);

public sealed record SchedulerWire(List<SchedulerWireBlock>? Blocks, List<SchedulerWireDeferred>? Deferred);

public sealed record SchedulerWireBlock(
    string? Id, string? TaskId, string? Title, string? Start, string? End, string? Kind, string? Reason);

public sealed record SchedulerWireDeferred(string? TaskId, string? Title, string? Reason);

public sealed record CriticWire(string? Decision, List<CriticWireRisk>? Risks, string? Revision);

public sealed record CriticWireRisk(string? Title, string? Mitigation, string? Severity);

public sealed record CoachWire(string? Summary);
