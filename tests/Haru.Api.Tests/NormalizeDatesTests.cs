using System.Text.Json;
using Haru.Api.Tests.Support;

namespace Haru.Api.Tests;

/// <summary>NormalizeExecutor with the real date resolver behind a fake gateway: phrase trimming and failure handling.</summary>
public class NormalizeDatesTests
{
    private static async Task<(PlanHarness H, PlanRun Run)> RunAsync(Action<PlanHarness>? setup = null, params object[] items)
    {
        var h = Scenario.Happy();
        h.Runner.Reply("intake", Scenario.Intake(items)).Reply("scheduler", Scenario.Schedule(("t1", "09:00", "10:00")));
        setup?.Invoke(h);
        return (h, await h.RunAsync());
    }

    private static Task<(PlanHarness H, PlanRun Run)> RunDeadlineAsync(string deadlineText, Action<PlanHarness>? setup = null) =>
        RunAsync(setup, Scenario.Item("보고서", minutes: 60, deadline: deadlineText));

    private static IEnumerable<SseEvent> NormalizeEvents(PlanRun run, string name) =>
        run.Of(name).Where(e => e.Data.GetProperty("agent").GetString() == "normalize");

    [Fact]
    public async Task Run_FullDeadlinePhrase_ResolvesWithOneCall()
    {
        var (h, run) = await RunDeadlineAsync("내일 10시");

        Assert.Equal(new[] { "내일 10시" }, h.Mcp.Expressions);
        Assert.Equal("2026-11-06T10:00:00+09:00", run.Plan!.Tasks[0].Deadline);
    }

    [Fact]
    public async Task Run_OneTrailingWord_IsTrimmedAndResolvesOnTheSecondCall()
    {
        var (h, run) = await RunDeadlineAsync("오늘 17시까지 제출");

        Assert.Equal(new[] { "오늘 17시까지 제출", "오늘 17시까지" }, h.Mcp.Expressions);
        Assert.Equal("2026-11-05T17:00:00+09:00", run.Plan!.Tasks[0].Deadline);
    }

    [Fact]
    public async Task Run_TwoTrailingWords_AreTrimmedAndResolveOnTheThirdCall()
    {
        var (h, run) = await RunDeadlineAsync("오늘 17시까지 제출 부탁");

        Assert.Equal(new[] { "오늘 17시까지 제출 부탁", "오늘 17시까지 제출", "오늘 17시까지" }, h.Mcp.Expressions);
        Assert.Equal("2026-11-05T17:00:00+09:00", run.Plan!.Tasks[0].Deadline);
    }

    [Fact]
    public async Task Run_FourTrailingWords_StopAfterThreeCallsAndKeepTheDeadlineNull()
    {
        var (h, run) = await RunDeadlineAsync("오늘 17시까지 아주 급한 일 제출");

        Assert.Equal(3, h.Mcp.CountOf("resolve_date"));
        Assert.Null(run.Plan!.Tasks[0].Deadline);
        Assert.True(run.Has("done"));
    }

    [Fact]
    public async Task Run_PhraseTheResolverDoesNotKnow_TriesOnceAndKeepsTheDeadlineNull()
    {
        var (h, run) = await RunDeadlineAsync("언젠가");

        Assert.Equal(1, h.Mcp.CountOf("resolve_date"));
        Assert.Null(run.Plan!.Tasks[0].Deadline);
    }

    [Fact]
    public async Task Run_ShortPhraseWithTwoTrailingWords_TrimsDownToTheFirstWord()
    {
        var (h, run) = await RunDeadlineAsync("내일 아주 급한");

        Assert.Equal(new[] { "내일 아주 급한", "내일 아주", "내일" }, h.Mcp.Expressions);
        Assert.Equal("2026-11-06", run.Plan!.Tasks[0].Deadline);
    }

    [Fact]
    public async Task Run_DateWithoutATime_StoresTheDateOnly()
    {
        var (_, run) = await RunDeadlineAsync("오늘까지");

        Assert.Equal("2026-11-05", run.Plan!.Tasks[0].Deadline);
    }

    [Fact]
    public async Task Run_TimeWithoutADate_UsesThePlanDay()
    {
        var (_, run) = await RunDeadlineAsync("17시까지");

        Assert.Equal("2026-11-05T17:00:00+09:00", run.Plan!.Tasks[0].Deadline);
    }

    [Fact]
    public async Task Run_SamePhraseOnTwoItems_IsResolvedOnceAndAppliedToBoth()
    {
        var (h, run) = await RunAsync(null,
            Scenario.Item("보고서", minutes: 60, deadline: "내일 10시"),
            Scenario.Item("메일", minutes: 30, deadline: "내일 10시"));

        Assert.Equal(1, h.Mcp.CountOf("resolve_date"));
        Assert.All(run.Plan!.Tasks, t => Assert.Equal("2026-11-06T10:00:00+09:00", t.Deadline));
    }

    [Fact]
    public async Task Run_Resolving_SendsThePlanDayAsTheReferenceDate()
    {
        var (h, _) = await RunDeadlineAsync("모레");

        var call = Assert.Single(h.Mcp.Calls, c => c.Tool == "resolve_date");
        Assert.Equal("2026-11-05", call.Args.GetProperty("referenceDate").GetString());
    }

    [Fact]
    public async Task Run_ResolverFails_KeepsTheDeadlineNullWithoutRetryingAndStillCompletes()
    {
        var (h, run) = await RunDeadlineAsync("오늘 17시까지 제출", h => h.Mcp.Fail("resolve_date"));

        Assert.Equal(1, h.Mcp.CountOf("resolve_date"));
        Assert.Null(run.Plan!.Tasks[0].Deadline);
        Assert.False(NormalizeEvents(run, "tool_result").Single().Data.GetProperty("ok").GetBoolean());
        Assert.Equal("done", run.Names[^1]);
    }

    [Fact]
    public async Task Run_ToolCallEvent_NamesTheNormalizeAgentAndTheExpression()
    {
        var (_, run) = await RunDeadlineAsync("내일 10시");

        var call = NormalizeEvents(run, "tool_call").Single().Data;
        Assert.Equal("resolve_date", call.GetProperty("tool").GetString());
        Assert.Equal("내일 10시", call.GetProperty("args").GetProperty("expression").GetString());
        Assert.True(NormalizeEvents(run, "tool_result").Single().Data.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Run_TenDistinctDeadlines_ResolvesAllButShowsOnlySixToolCalls()
    {
        var items = Enumerable.Range(1, 10).Select(n => Scenario.Item($"할 일 {n}", minutes: 30, deadline: $"{n}일 후")).ToArray();

        var (h, run) = await RunAsync(null, items);

        Assert.Equal(10, h.Mcp.CountOf("resolve_date"));
        Assert.Equal(6, NormalizeEvents(run, "tool_call").Count());
        Assert.Equal(6, NormalizeEvents(run, "tool_result").Count());
        Assert.Equal(Enumerable.Range(1, 10).Select(n => $"2026-11-{5 + n:00}"), run.Plan!.Tasks.Select(t => t.Deadline!));
    }

    [Fact]
    public async Task Run_NoDeadlinesAtAll_NeverCallsTheResolver()
    {
        var (h, _) = await RunAsync(null, Scenario.Item("보고서", minutes: 60), Scenario.Item("메일", minutes: 30));

        Assert.Equal(0, h.Mcp.CountOf("resolve_date"));
    }

    [Fact]
    public async Task Run_NormalizeEvents_ReportTheNumberOfExpressions()
    {
        var (_, run) = await RunAsync(null,
            Scenario.Item("보고서", minutes: 60, deadline: "내일"),
            Scenario.Item("회의", start: "오늘 14시", end: "오늘 15시", fixedItem: true));

        var summary = run.Of("agent_completed").Single(e => e.Data.GetProperty("agent").GetString() == "normalize")
            .Data.GetProperty("summary").GetString();
        Assert.Contains("3개", summary);
        Assert.Contains("고정 일정 1개", summary);
    }
}
