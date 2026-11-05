using Haru.Api.Agents;
using Haru.Api.Contracts;
using Haru.Api.Tests.Support;
using static Haru.Api.Tests.Support.FakeAgentRunner;

namespace Haru.Api.Tests;

public class WorkflowIntegrationTests
{
    private static string[] Codes(SseEvent validation) =>
        validation.Data.GetProperty("report").GetProperty("violations").EnumerateArray()
            .Select(v => v.GetProperty("code").GetString()!).ToArray();

    // (a) happy path

    [Fact]
    public async Task Run_HappyPath_StreamsRunStartedFirstAndPlanThenDoneLast()
    {
        var run = await Scenario.Happy().RunAsync();

        Assert.Equal("run_started", run.Names[0]);
        Assert.Equal(new[] { "plan", "done" }, run.Names.TakeLast(2));
        Assert.DoesNotContain("error", run.Names);
        Assert.Single(run.Of("plan"));
        Assert.Single(run.Of("done"));
    }

    [Fact]
    public async Task Run_HappyPath_EmitsMilestonesInWorkflowOrder()
    {
        var run = await Scenario.Happy().RunAsync();

        var names = run.Names.ToList();
        var order = new[] { "tasks", "schedule", "validation", "critique", "plan", "done" }
            .Select(n => names.IndexOf(n)).ToArray();
        Assert.All(order, i => Assert.True(i > 0));
        Assert.Equal(order.OrderBy(i => i), order);
    }

    [Fact]
    public async Task Run_HappyPath_RunsEstimatorAndPrioritizerBetweenNormalizeAndScheduler()
    {
        var h = Scenario.Happy();

        var run = await h.RunAsync();

        Assert.Equal(1, h.Runner.CountOf("estimator"));
        Assert.Equal(1, h.Runner.CountOf("prioritizer"));
        var agents = run.StartedAgents.ToList();
        Assert.Equal(new[] { "intake", "normalize" }, agents.Take(2));
        Assert.Equal(new[] { "estimator", "prioritizer" }, agents.Skip(2).Take(2).Order());
        Assert.Equal(new[] { "scheduler", "validator", "critic", "coach" }, agents.Skip(4));
    }

    [Fact]
    public async Task Run_HappyPath_MarksPlanAsAiGenerated()
    {
        var plan = (await Scenario.Happy().RunAsync()).Plan!;

        Assert.Equal("ai", plan.Meta.Source);
        Assert.True(plan.Meta.AiGenerated);
        Assert.Equal(1, plan.Meta.Iterations);
        Assert.False(plan.Meta.Repaired);
        Assert.Equal(new ModelPair("fake-fast", "fake-smart"), plan.Meta.Models);
    }

    [Fact]
    public async Task Run_HappyPath_ReportsAiSourceInDoneEvent()
    {
        var done = (await Scenario.Happy().RunAsync()).Of("done").Single().Data;

        Assert.Equal("ai", done.GetProperty("source").GetString());
        Assert.False(done.GetProperty("repaired").GetBoolean());
        Assert.True(done.GetProperty("elapsedMs").GetInt64() >= 0);
    }

    [Fact]
    public async Task Run_HappyPath_ProducesValidPlanWithBlocksSortedAndNumbered()
    {
        var plan = (await Scenario.Happy().RunAsync()).Plan!;

        Assert.True(plan.Validation.Valid);
        Assert.Equal(new[] { "b1", "b2", "b3", "b4", "b5" }, plan.Blocks.Select(b => b.Id));
        Assert.Equal(new[] { "09:00", "09:35", "12:00", "13:05", "14:00" }, plan.Blocks.Select(b => b.Start));
        Assert.Equal(new[] { "task", "task", "break", "task", "fixed" }, plan.Blocks.Select(b => b.Kind));
        Assert.Equal(0.5, plan.Validation.LoadRatio);
        Assert.Empty(plan.Deferred);
    }

    [Fact]
    public async Task Run_HappyPath_AppliesEstimatesPrioritiesAndDeadlines()
    {
        var plan = (await Scenario.Happy().RunAsync()).Plan!;

        var proposal = plan.Tasks.Single(t => t.Id == "t1");
        Assert.Equal((120, 1, "2026-11-05T17:00:00+09:00"), (proposal.Minutes, proposal.Priority, proposal.Deadline));
        Assert.Equal(30, plan.Tasks.Single(t => t.Id == "t2").Minutes);
        Assert.Equal("estimated", proposal.DurationSource);
        Assert.Equal("input", plan.Tasks.Single(t => t.Id == "t2").DurationSource);
        var meeting = plan.Tasks.Single(t => t.Kind == "fixed");
        Assert.Equal(("14:00", "15:00", 60), (meeting.FixedStart, meeting.FixedEnd, meeting.Minutes));
        Assert.Equal("fixed", meeting.DurationSource);
    }

    [Fact]
    public async Task Run_HappyPath_KeepsCoachSummaryAndCriticRisks()
    {
        var plan = (await Scenario.Happy().RunAsync()).Plan!;

        Assert.Equal("오늘은 제안서에 먼저 집중하세요.", plan.Summary);
        var risk = Assert.Single(plan.Risks);
        Assert.Equal("medium", risk.Severity);
    }

    [Fact]
    public async Task Run_AllTasksHaveMinutes_SkipsEstimator()
    {
        var h = Scenario.Happy();
        h.Runner.Reply("intake", Scenario.Intake(Scenario.Item("보고서", minutes: 60), Scenario.Item("메일", minutes: 30)));
        h.Runner.Reply("prioritizer", Scenario.Priorities(("t1", 1), ("t2", 2)));
        h.Runner.Reply("scheduler", Scenario.Schedule(("t1", "09:00", "10:00"), ("t2", "10:05", "10:35")));

        var run = await h.RunAsync();

        Assert.Equal(0, h.Runner.CountOf("estimator"));
        Assert.Equal(2, run.Plan!.Tasks.Count(t => t.Kind == "task"));
    }

    [Fact]
    public async Task Run_AgentEmitsSignals_ForwardsThemAsSseEvents()
    {
        var h = Scenario.Happy();
        h.Runner.Script("scheduler", WithSignals(Scenario.ValidSchedule(),
            new DeltaSignal("생각 중"),
            new ToolCallSignal("find_free_slots", new Dictionary<string, object?> { ["minMinutes"] = 30 }, "scheduler-call"),
            new ToolResultSignal("find_free_slots", true, "빈 시간 2곳", "scheduler-call")));

        var run = await h.RunAsync();

        Assert.Contains(run.Of("agent_delta"), e => e.Data.GetProperty("agent").GetString() == "scheduler");
        Assert.Contains(run.Of("tool_call"), e => e.Data.GetProperty("tool").GetString() == "find_free_slots");
        Assert.Contains(run.Of("tool_result"), e => e.Data.GetProperty("summary").GetString() == "빈 시간 2곳");
        Assert.Equal("scheduler-call", run.Of("tool_call").Single(e => e.Data.GetProperty("agent").GetString() == "scheduler").Data.GetProperty("callId").GetString());
        Assert.Equal("scheduler-call", run.Of("tool_result").Single(e => e.Data.GetProperty("agent").GetString() == "scheduler").Data.GetProperty("callId").GetString());
    }

    // (b) overlap once, then valid

    [Fact]
    public async Task Run_SchedulerOverlapsOnce_LoopsBackAndSecondAnswerIsValid()
    {
        var h = Scenario.Happy();
        h.Runner.Reply("scheduler", Scenario.OverlapSchedule(), Scenario.ValidSchedule());

        var run = await h.RunAsync();

        var plan = run.Plan!;
        Assert.Equal(2, plan.Meta.Iterations);
        Assert.False(plan.Meta.Repaired);
        Assert.True(plan.Validation.Valid);
        Assert.Equal(2, h.Runner.CountOf("scheduler"));
        var validations = run.Of("validation").ToList();
        Assert.Equal(2, validations.Count);
        Assert.Contains("OVERLAP", Codes(validations[0]));
        Assert.Empty(Codes(validations[1]));
    }

    [Fact]
    public async Task Run_SchedulerOverlapsOnce_SecondPromptCarriesViolations()
    {
        var h = Scenario.Happy();
        h.Runner.Reply("scheduler", Scenario.OverlapSchedule(), Scenario.ValidSchedule());

        await h.RunAsync();

        var second = h.Runner.PromptsOf("scheduler").Last();
        Assert.Contains("OVERLAP", second);
        Assert.Contains("previousBlocks", second);
    }

    // (c) invalid twice, repair

    [Fact]
    public async Task Run_SchedulerInvalidTwice_RepairMovesLeastImportantTaskAndPlanIsValid()
    {
        var h = Scenario.Happy();
        h.Runner.Reply("scheduler", Scenario.OverlapSchedule());

        var run = await h.RunAsync();

        var plan = run.Plan!;
        Assert.Equal(2, plan.Meta.Iterations);
        Assert.True(plan.Meta.Repaired);
        Assert.True(plan.Validation.Valid);
        Assert.Contains("repair", run.StartedAgents);
        var moved = Assert.Single(plan.Deferred);
        Assert.Equal(("t2", "시간 충돌을 없애기 위해 다른 날로 옮깁니다."), (moved.TaskId, moved.Reason));
        Assert.DoesNotContain(plan.Blocks, b => b.TaskId == "t2");
        Assert.Contains(plan.Blocks, b => b.TaskId == "t1");
        Assert.True(run.Of("done").Single().Data.GetProperty("repaired").GetBoolean());
    }

    // (d) critic revise once, then accept

    [Fact]
    public async Task Run_CriticRevisesOnce_SchedulerRunsAgainWithCritiqueAndSecondCriticAccepts()
    {
        var h = Scenario.Happy();
        h.Runner.Reply("critic", Scenario.Revise(), Scenario.Approve());

        var run = await h.RunAsync();

        var plan = run.Plan!;
        Assert.Equal(2, plan.Meta.Iterations);
        Assert.Equal(2, h.Runner.CountOf("scheduler"));
        Assert.Equal(2, h.Runner.CountOf("critic"));
        Assert.Equal(new[] { "revise", "approve" },
            run.Of("critique").Select(e => e.Data.GetProperty("decision").GetString()));
        Assert.Contains("Move the proposal to the afternoon", h.Runner.PromptsOf("scheduler").Last());
    }

    [Fact]
    public async Task Run_CriticAlwaysRevises_StopsAtIterationLimitAndAccepts()
    {
        var h = Scenario.Happy();
        h.Runner.Reply("critic", Scenario.Revise());

        var run = await h.RunAsync();

        Assert.Equal(2, run.Plan!.Meta.Iterations);
        Assert.Equal(2, h.Runner.CountOf("critic"));
        Assert.Equal("approve", run.Of("critique").Last().Data.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Run_CriticRevisesWithoutRevisionText_IsTreatedAsApprove()
    {
        var h = Scenario.Happy();
        h.Runner.Reply("critic", Scenario.Critic("revise", null));

        var run = await h.RunAsync();

        Assert.Equal(1, run.Plan!.Meta.Iterations);
        Assert.Equal(1, h.Runner.CountOf("critic"));
    }

    // (e)-(h) a failing helper agent never stops the plan

    [Fact]
    public async Task Run_EstimatorFails_UsesDefault30MinutesAndCompletes()
    {
        var h = Scenario.Happy();
        h.Runner.Script("estimator", Fail());

        var run = await h.RunAsync();

        var plan = run.Plan!;
        Assert.True(run.Has("done"));
        Assert.Equal(30, plan.Tasks.Single(t => t.Id == "t1").Minutes);
        Assert.Equal(30, plan.Tasks.Single(t => t.Id == "t4").Minutes);
        Assert.Contains(run.Of("agent_completed"), e =>
            e.Data.GetProperty("agent").GetString() == "estimator"
            && e.Data.GetProperty("summary").GetString() == "시간을 추정하지 못해 기본값 30분을 씁니다.");
    }

    [Fact]
    public async Task Run_EstimatorReturnsGarbage_RetriesOnceThenUsesDefault()
    {
        var h = Scenario.Happy();
        h.Runner.Reply("estimator", "sorry, I cannot do that");

        var run = await h.RunAsync();

        Assert.Equal(2, h.Runner.CountOf("estimator"));
        Assert.Contains("rejected", h.Runner.PromptsOf("estimator").Last());
        Assert.Equal(30, run.Plan!.Tasks.Single(t => t.Id == "t1").Minutes);
    }

    [Fact]
    public async Task Run_PrioritizerFails_UsesPriority2AndCompletes()
    {
        var h = Scenario.Happy();
        h.Runner.Script("prioritizer", Fail());

        var run = await h.RunAsync();

        var plan = run.Plan!;
        Assert.True(run.Has("done"));
        Assert.All(plan.Tasks.Where(t => t.Kind == "task"), t => Assert.Equal(2, t.Priority));
        Assert.Equal(1, plan.Tasks.Single(t => t.Kind == "fixed").Priority);
    }

    [Fact]
    public async Task Run_CriticFails_ApprovesWithNoRisks()
    {
        var h = Scenario.Happy();
        h.Runner.Script("critic", Fail());

        var run = await h.RunAsync();

        Assert.True(run.Has("done"));
        Assert.Empty(run.Plan!.Risks);
        var critique = run.Of("critique").Single().Data;
        Assert.Equal("approve", critique.GetProperty("decision").GetString());
        Assert.Equal(0, critique.GetProperty("risks").GetArrayLength());
    }

    [Fact]
    public async Task Run_CoachFails_UsesDeterministicSummary()
    {
        var h = Scenario.Happy();
        h.Runner.Script("coach", Fail());

        var run = await h.RunAsync();

        Assert.True(run.Has("done"));
        Assert.Equal("오늘은 할 일 3개를 시간표에 넣었고, 부하는 50%입니다.", run.Plan!.Summary);
    }

    [Fact]
    public async Task Run_CoachFailsWithDeferredTask_FallbackMentionsMovedTasks()
    {
        var h = Scenario.Happy();
        h.Runner.Reply("scheduler", Scenario.Schedule(("t2", "09:00", "09:30"), ("t1", "09:35", "11:35")));
        h.Runner.Script("coach", Fail());

        var plan = (await h.RunAsync()).Plan!;

        Assert.Equal("오늘은 할 일 2개를 시간표에 넣었고, 부하는 44%입니다. 1개는 다른 날로 옮겼습니다.", plan.Summary);
    }

    // (i)-(k) failures that end the run

    [Fact]
    public async Task Run_IntakeFindsNoTasks_SendsNoTasksErrorAndNoDone()
    {
        var h = Scenario.Happy();
        h.Runner.Reply("intake", Scenario.Intake());

        var run = await h.RunAsync();

        Assert.Equal("NO_TASKS", run.ErrorCode);
        Assert.DoesNotContain("done", run.Names);
        Assert.DoesNotContain("plan", run.Names);
        Assert.Equal("error", run.Names[^1]);
        Assert.True(run.Of("error").Single().Data.GetProperty("recoverable").GetBoolean());
        Assert.Equal(0, h.Runner.CountOf("scheduler"));
    }

    [Fact]
    public async Task Run_SchedulerFails_SendsWorkflowFailedWithoutLeakingTheCause()
    {
        var h = Scenario.Happy();
        h.Runner.Script("scheduler", Fail(new InvalidOperationException("secret stack detail")));

        var run = await h.RunAsync();

        Assert.Equal("WORKFLOW_FAILED", run.ErrorCode);
        Assert.DoesNotContain("done", run.Names);
        Assert.DoesNotContain("secret stack detail", run.Of("error").Single().Data.ToString());
    }

    [Fact]
    public async Task Run_SchedulerReturnsGarbageTwice_SendsWorkflowFailed()
    {
        var h = Scenario.Happy();
        h.Runner.Reply("scheduler", "not json at all");

        var run = await h.RunAsync();

        Assert.Equal("WORKFLOW_FAILED", run.ErrorCode);
        Assert.Equal(2, h.Runner.CountOf("scheduler"));
    }

    [Fact]
    public async Task Run_ValidationToolFails_SendsWorkflowFailed()
    {
        var h = Scenario.Happy();
        h.Mcp.Fail("validate_schedule");

        var run = await h.RunAsync();

        Assert.Equal("WORKFLOW_FAILED", run.ErrorCode);
        Assert.DoesNotContain("done", run.Names);
    }

    [Fact]
    public async Task Run_AgentIsSlowerThanPlanTimeout_SendsTimeoutError()
    {
        var h = Scenario.Happy(o => o with { PlanTimeoutSeconds = 1 });
        h.Runner.Script("scheduler", Slow(TimeSpan.FromSeconds(30), Scenario.ValidSchedule()));

        var run = await h.RunAsync();

        Assert.Equal("TIMEOUT", run.ErrorCode);
        Assert.DoesNotContain("done", run.Names);
        Assert.True(run.Of("error").Single().Data.GetProperty("recoverable").GetBoolean());
    }

    [Fact]
    public async Task Run_ClientDisconnects_SendsNeitherPlanNorError()
    {
        var h = Scenario.Happy();
        h.Runner.Script("intake", Slow(TimeSpan.FromSeconds(30), Scenario.StandardIntake()));
        using var abort = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var run = await h.RunAsync(ct: abort.Token);

        Assert.Equal(new[] { "run_started" }, run.Names.Where(n => n is "run_started" or "plan" or "done" or "error"));
    }
}
