using System.Text.Json;
using Haru.Api.Config;
using Haru.Api.Contracts;
using Haru.Api.Mcp;
using Haru.Api.Tests.Support;
using Haru.Api.Workflow;

namespace Haru.Api.Tests;

public class ScheduleChecksTests
{
    private static PlanContext Ctx(params TaskItem[] tasks) =>
        new() { Day = "2026-11-05", WorkStart = "09:00", WorkEnd = "18:00", Tasks = tasks };

    private static TaskItem Task(string id, int minutes = 60, string? deadline = null) =>
        new(id, $"할 일 {id}", minutes, 2, deadline, null, null, "task", null);

    private static Block Task(string id, string taskId, string start, string end) =>
        new(id, taskId, $"할 일 {taskId}", start, end, "task", "r");

    private static Block Fixed(string id, string start, string end) => new(id, "tf", "회의", start, end, "fixed", "r");

    private static readonly Block Lunch = new("lunch", null, "점심", "12:00", "13:00", "break", "r");

    private static Task<ValidationReport> Validate(FakeMcpGateway mcp, PlanContext c, Block[] blocks, HaruOptions? options = null) =>
        ScheduleChecks.ValidateAsync(mcp, options ?? new HaruOptions(), c, blocks, CancellationToken.None);

    private static JsonElement SentBlock(FakeMcpGateway mcp, int index) =>
        mcp.Calls.Single(c => c.Tool == "validate_schedule").Args.GetProperty("blocks")[index];

    // checks against the real validator

    [Fact]
    public async Task ValidateAsync_ValidBlocks_ReturnsAValidReportWithTheMinutes()
    {
        var report = await Validate(new FakeMcpGateway(), Ctx(Task("t1")), [Task("b1", "t1", "09:00", "10:00"), Lunch]);

        Assert.True(report.Valid);
        Assert.Equal((60, 480), (report.OccupiedMinutes, report.AvailableMinutes));
        Assert.Empty(report.Violations);
    }

    [Fact]
    public async Task ValidateAsync_TaskWithTodayDeadline_SendsTheDeadlineTimeAndCatchesALateBlock()
    {
        var mcp = new FakeMcpGateway();
        var c = Ctx(Task("t1", 90, "2026-11-05T10:00:00+09:00"));

        var report = await Validate(mcp, c, [Task("b1", "t1", "09:00", "10:30")]);

        Assert.Equal("10:00", SentBlock(mcp, 0).GetProperty("deadlineTime").GetString());
        Assert.Contains(report.Violations, v => v.Code == "DEADLINE_MISSED");
    }

    [Fact]
    public async Task ValidateAsync_FixedAndBreakBlocksAndOpenTasks_SendNoDeadlineTime()
    {
        var mcp = new FakeMcpGateway();

        await Validate(mcp, Ctx(Task("t1")), [Task("b1", "t1", "09:00", "10:00"), Lunch, Fixed("f1", "14:00", "15:00")]);

        for (var i = 0; i < 3; i++) Assert.False(SentBlock(mcp, i).TryGetProperty("deadlineTime", out _));
    }

    [Fact]
    public async Task ValidateAsync_DeadlineOnAnotherDay_SendsNoDeadlineTime()
    {
        var mcp = new FakeMcpGateway();

        await Validate(mcp, Ctx(Task("t1", 60, "2026-11-06T10:00:00+09:00")), [Task("b1", "t1", "16:00", "17:00")]);

        Assert.False(SentBlock(mcp, 0).TryGetProperty("deadlineTime", out _));
    }

    [Fact]
    public async Task ValidateAsync_UsesTheBufferAndMaxLoadOfTheOptions()
    {
        var options = new HaruOptions { BufferMinutes = 20, MaxLoad = 0.1 };
        var c = Ctx(Task("t1"), Task("t2"));

        var report = await Validate(new FakeMcpGateway(), c, [Task("b1", "t1", "09:00", "10:00"), Task("b2", "t2", "10:10", "11:10")], options);

        Assert.Equal(new[] { "NO_BUFFER", "OVERLOAD" }, report.Violations.Select(v => v.Code).Order());
    }

    [Fact]
    public async Task ValidateAsync_NoBufferBetweenTwoFixedEvents_IsNotAViolation()
    {
        var report = await Validate(new FakeMcpGateway(), Ctx(), [Fixed("f1", "10:00", "11:00"), Fixed("f2", "11:02", "12:00")]);

        Assert.True(report.Valid);
        Assert.Empty(report.Violations);
    }

    [Fact]
    public async Task ValidateAsync_NoBufferBetweenATaskAndAFixedEvent_StaysAViolation()
    {
        var report = await Validate(new FakeMcpGateway(), Ctx(Task("t1")), [Task("b1", "t1", "13:00", "13:58"), Fixed("f1", "14:00", "15:00")]);

        Assert.False(report.Valid);
        Assert.Equal("NO_BUFFER", Assert.Single(report.Violations).Code);
    }

    [Fact]
    public async Task ValidateAsync_ToolFailure_Throws()
    {
        var mcp = new FakeMcpGateway().Fail("validate_schedule");

        await Assert.ThrowsAsync<McpToolException>(() => Validate(mcp, Ctx(Task("t1")), [Task("b1", "t1", "09:00", "10:00")]));
    }

    // summary helpers

    [Fact]
    public void MaxSchedulerRuns_IsTwo() => Assert.Equal(2, ScheduleChecks.MaxSchedulerRuns);

    [Theory]
    [InlineData(0.5, 50)]
    [InlineData(0.374, 37)]
    [InlineData(0.376, 38)]
    [InlineData(1.0, 100)]
    [InlineData(0.0, 0)]
    public void LoadPercent_Ratio_IsRoundedToAWholePercent(double ratio, int expected) =>
        Assert.Equal(expected, ScheduleChecks.LoadPercent(new ValidationReport(true, ratio, 0, 0, [])));

    [Fact]
    public void LoadPercent_NoReport_IsZero() => Assert.Equal(0, ScheduleChecks.LoadPercent(null));

    [Fact]
    public void Summary_ValidReport_SaysAllRulesPassed() =>
        Assert.Equal("규칙을 모두 통과했습니다. 부하 50%.", ScheduleChecks.Summary(new ValidationReport(true, 0.5, 240, 480, [])));

    [Fact]
    public void Summary_InvalidReport_CountsViolationsAndListsAtMostThreeDistinctCodes()
    {
        Violation V(string code) => new(code, "m", ["b1"]);
        var report = new ValidationReport(false, 0.9, 432, 480,
            [V("OVERLAP"), V("OVERLAP"), V("NO_BUFFER"), V("OVERLOAD"), V("DEADLINE_MISSED")]);

        Assert.Equal("위반 5건(OVERLAP, NO_BUFFER, OVERLOAD). 부하 90%.", ScheduleChecks.Summary(report));
    }
}

public class CoachFallbackTests
{
    [Fact]
    public void Fallback_NothingDeferred_OnlyStatesCountAndLoad() =>
        Assert.Equal("오늘은 할 일 3개를 시간표에 넣었고, 부하는 44%입니다.", CoachExecutor.Fallback(3, 0, 44));

    [Fact]
    public void Fallback_SomethingDeferred_AddsTheDeferredCount() =>
        Assert.Equal("오늘은 할 일 3개를 시간표에 넣었고, 부하는 44%입니다. 2개는 다른 날로 옮겼습니다.", CoachExecutor.Fallback(3, 2, 44));
}
