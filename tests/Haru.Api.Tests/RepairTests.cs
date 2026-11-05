using Haru.Api.Contracts;
using Haru.Api.Tests.Support;
using Haru.Api.Workflow;

namespace Haru.Api.Tests;

public class RepairTests
{
    private static TaskItem Task(string id, int priority, string kind = "task") =>
        new(id, $"할 일 {id}", 30, priority, null, null, null, kind, null);

    private static readonly Dictionary<string, TaskItem> Tasks = new[]
    {
        Task("t1", 1), Task("t2", 2), Task("t3", 3), Task("t4", 3), Task("tf", 1, "fixed"),
    }.ToDictionary(t => t.Id);

    private static Block Task(string id, string taskId, string start, string end) =>
        new(id, taskId, $"할 일 {taskId}", start, end, "task", "r");

    private static readonly Block Meeting = new("f1", "tf", "회의", "14:00", "15:00", "fixed", "r");
    private static readonly Block Lunch = new("lunch", null, "점심", "12:00", "13:00", "break", "r");

    private static Violation V(string code, params string[] ids) => new(code, "m", ids);

    private static ValidationReport Report(params Violation[] violations) =>
        new(violations.Length == 0, 0.5, 240, 480, violations);

    // PickVictim

    [Fact]
    public void PickVictim_OverlappingTasks_PicksTheLeastImportantOne()
    {
        var blocks = new[] { Task("b1", "t1", "09:00", "10:00"), Task("b2", "t2", "09:30", "10:00") };

        var victim = Repair.PickVictim(blocks, Report(V("OVERLAP", "b1", "b2")), Tasks);

        Assert.Equal("b2", victim?.Id);
    }

    [Fact]
    public void PickVictim_SamePriority_PicksTheOneThatEndsLast()
    {
        var blocks = new[] { Task("b1", "t3", "09:00", "10:00"), Task("b2", "t4", "09:30", "11:00") };

        var victim = Repair.PickVictim(blocks, Report(V("OVERLAP", "b1", "b2")), Tasks);

        Assert.Equal("b2", victim?.Id);
    }

    [Fact]
    public void PickVictim_OnlyImplicatedBlocksAreCandidates()
    {
        var blocks = new[] { Task("b1", "t1", "09:00", "10:00"), Task("b2", "t2", "09:30", "10:00"), Task("b3", "t3", "16:00", "17:00") };

        var victim = Repair.PickVictim(blocks, Report(V("OVERLAP", "b1", "b2")), Tasks);

        Assert.Equal("b2", victim?.Id);
    }

    [Fact]
    public void PickVictim_ViolationWithoutATaskBlock_FallsBackToAllTaskBlocks()
    {
        var blocks = new[] { Task("b1", "t1", "09:00", "10:00"), Task("b2", "t3", "16:00", "17:00"), Meeting, Lunch };

        var victim = Repair.PickVictim(blocks, Report(V("OVERLOAD")), Tasks);

        Assert.Equal("b2", victim?.Id);
    }

    [Fact]
    public void PickVictim_FixedAndBreakBlocks_AreNeverPickedEvenWhenImplicated()
    {
        var blocks = new[] { Task("b1", "t1", "13:30", "14:30"), Meeting, Lunch };

        var victim = Repair.PickVictim(blocks, Report(V("OVERLAP", "f1", "b1"), V("OVERLAP", "lunch", "f1")), Tasks);

        Assert.Equal("b1", victim?.Id);
    }

    [Fact]
    public void PickVictim_BlocksOfUnknownTasks_AreNeverPicked()
    {
        var blocks = new[]
        {
            Task("b1", "t9", "09:00", "10:00"),
            new Block("b2", null, "id 없음", "09:00", "10:00", "task", "r"),
            Task("b3", "t1", "09:30", "10:30"),
        };

        var victim = Repair.PickVictim(blocks, Report(V("OVERLAP", "b1", "b2", "b3")), Tasks);

        Assert.Equal("b3", victim?.Id);
    }

    [Fact]
    public void PickVictim_NoTaskBlocksAtAll_ReturnsNull() =>
        Assert.Null(Repair.PickVictim([Meeting, Lunch], Report(V("OVERLAP", "f1", "lunch")), Tasks));

    // OnlyFixed

    [Fact]
    public void OnlyFixed_NoViolations_IsFalse() =>
        Assert.False(Repair.OnlyFixed(Report(), [Meeting, Lunch]));

    [Fact]
    public void OnlyFixed_EveryViolationInvolvesOnlyFixedOrBreakBlocks_IsTrue() =>
        Assert.True(Repair.OnlyFixed(Report(V("OVERLAP", "f1", "lunch"), V("OUT_OF_HOURS", "f1")), [Meeting, Lunch]));

    [Fact]
    public void OnlyFixed_AViolationInvolvesATaskBlock_IsFalse() =>
        Assert.False(Repair.OnlyFixed(Report(V("OVERLAP", "f1", "b1")), [Meeting, Task("b1", "t1", "14:30", "15:30")]));

    [Fact]
    public void OnlyFixed_MixOfFixedOnlyAndTaskViolations_IsFalse() =>
        Assert.False(Repair.OnlyFixed(
            Report(V("OVERLAP", "f1", "lunch"), V("DEADLINE_MISSED", "b1")), [Meeting, Lunch, Task("b1", "t1", "16:00", "17:00")]));

    [Fact]
    public void OnlyFixed_ViolationWithoutBlockIds_IsFalse() =>
        Assert.False(Repair.OnlyFixed(Report(V("OVERLOAD")), [Meeting, Lunch]));

    [Fact]
    public void OnlyFixed_ViolationOfAnUnknownBlock_IsFalse() =>
        Assert.False(Repair.OnlyFixed(Report(V("INVALID_RANGE", "zzz")), [Meeting, Lunch]));

    // IgnoreUserChoices

    [Fact]
    public void IgnoreUserChoices_NoBufferBetweenFixedBlocks_IsDroppedAndTheReportBecomesValid()
    {
        var report = Report(V("NO_BUFFER", "f1", "lunch"));

        var result = Repair.IgnoreUserChoices(report, [Meeting, Lunch]);

        Assert.True(result.Valid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void IgnoreUserChoices_NoBufferInvolvingATaskBlock_IsKept()
    {
        var report = Report(V("NO_BUFFER", "b1", "f1"));

        var result = Repair.IgnoreUserChoices(report, [Meeting, Task("b1", "t1", "13:00", "14:00")]);

        Assert.False(result.Valid);
        Assert.Single(result.Violations);
    }

    [Fact]
    public void IgnoreUserChoices_NoBufferWithoutBlockIds_IsKept() =>
        Assert.False(Repair.IgnoreUserChoices(Report(V("NO_BUFFER")), [Meeting, Lunch]).Valid);

    [Fact]
    public void IgnoreUserChoices_OtherCodesBetweenFixedBlocks_AreKept()
    {
        var report = Report(V("OVERLAP", "f1", "lunch"), V("NO_BUFFER", "f1", "lunch"));

        var result = Repair.IgnoreUserChoices(report, [Meeting, Lunch]);

        Assert.False(result.Valid);
        Assert.Equal(new[] { "OVERLAP" }, result.Violations.Select(v => v.Code));
    }

    [Fact]
    public void IgnoreUserChoices_KeepsTheOtherFieldsOfTheReport()
    {
        var result = Repair.IgnoreUserChoices(
            new ValidationReport(false, 0.62, 300, 480, [V("NO_BUFFER", "f1", "lunch")]), [Meeting, Lunch]);

        Assert.Equal((0.62, 300, 480), (result.LoadRatio, result.OccupiedMinutes, result.AvailableMinutes));
    }

    [Fact]
    public void IgnoreUserChoices_ValidReport_StaysValid() =>
        Assert.True(Repair.IgnoreUserChoices(Report(), [Meeting, Lunch]).Valid);

    // the repair step inside the workflow

    [Fact]
    public async Task RepairStep_ThreeTasksAtTheSameTime_MovesTheLeastImportantFirstUntilValid()
    {
        var h = Scenario.Happy();
        var stacked = Scenario.Schedule(("t2", "09:00", "09:30"), ("t1", "09:00", "11:00"), ("t4", "09:00", "09:30"));
        h.Runner.Reply("scheduler", stacked, stacked);

        var run = await h.RunAsync();

        var plan = run.Plan!;
        Assert.Equal(new[] { "t4", "t2" }, plan.Deferred.Select(d => d.TaskId));
        Assert.All(plan.Deferred, d => Assert.Equal(Repair.Reason, d.Reason));
        Assert.Equal(new[] { "t1" }, plan.Blocks.Where(b => b.Kind == "task").Select(b => b.TaskId));
        Assert.True(plan.Validation.Valid);
        Assert.True(plan.Meta.Repaired);
        Assert.Equal(2, plan.Meta.Iterations);
        Assert.Equal("done", run.Names[^1]);
    }

    [Fact]
    public async Task Plan_TwoFixedEventsOverlap_IsReportedWithoutRepair()
    {
        var h = Scenario.Happy();
        h.Runner.Reply("intake", Scenario.Intake(
            Scenario.Item("회의 A", start: "오늘 14시", end: "오늘 15시", fixedItem: true),
            Scenario.Item("회의 B", start: "오늘 14시 30분", end: "오늘 15시 30분", fixedItem: true),
            Scenario.Item("메일 정리", minutes: 30)));
        h.Runner.Reply("estimator", Scenario.Estimates(("t3", 30)));
        h.Runner.Reply("prioritizer", Scenario.Priorities(("t3", 2)));
        h.Runner.Reply("scheduler", Scenario.Schedule(("t3", "09:00", "09:30")));

        var run = await h.RunAsync();

        var plan = run.Plan!;
        Assert.False(plan.Validation.Valid);
        Assert.Contains(plan.Validation.Violations, v => v.Code == "OVERLAP");
        Assert.False(plan.Meta.Repaired);
        Assert.Equal(1, h.Runner.CountOf("scheduler"));
        Assert.Equal("done", run.Names[^1]);
    }
}
