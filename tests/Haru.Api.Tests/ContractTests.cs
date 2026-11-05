using System.Text.Json;
using Haru.Api.Contracts;
using Haru.Api.Tests.Support;

namespace Haru.Api.Tests;

/// <summary>Checks one PlanResult JSON against section 1 of docs/api-contract.md.</summary>
internal static class PlanShape
{
    private const string Hhmm = @"^([01]\d|2[0-3]):[0-5]\d$";

    private static readonly string[] ViolationCodes =
        ["INVALID_RANGE", "OUT_OF_HOURS", "OVERLAP", "NO_BUFFER", "OVERLOAD", "DEADLINE_MISSED"];

    public static string[] Names(JsonElement element) => element.EnumerateObject().Select(p => p.Name).Order().ToArray();

    public static IEnumerable<string> AllPropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                foreach (var name in AllPropertyNames(item)) yield return name;
        else if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var name in AllPropertyNames(property.Value)) yield return name;
            }
    }

    public static void AssertContract(JsonElement plan)
    {
        Assert.Equal(new[] { "blocks", "day", "deferred", "meta", "risks", "summary", "tasks", "validation", "workEnd", "workStart" }, Names(plan));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", plan.GetProperty("day").GetString()!);
        Assert.Matches(Hhmm, plan.GetProperty("workStart").GetString()!);
        Assert.Matches(Hhmm, plan.GetProperty("workEnd").GetString()!);
        Assert.InRange(plan.GetProperty("summary").GetString()!.Length, 1, 300);

        foreach (var task in plan.GetProperty("tasks").EnumerateArray()) AssertTask(task);
        AssertBlocks(plan.GetProperty("blocks"));
        foreach (var deferred in plan.GetProperty("deferred").EnumerateArray()) AssertDeferred(deferred);
        AssertRisks(plan.GetProperty("risks"));
        AssertValidation(plan.GetProperty("validation"));
        AssertMeta(plan.GetProperty("meta"));
    }

    private static void AssertTask(JsonElement task)
    {
        var names = Names(task);
        if (task.TryGetProperty("durationSource", out var durationSource))
        {
            Assert.Contains(durationSource.GetString(), new string?[] { null, "input", "fixed", "estimated", "default" });
            names = names.Where(n => n != "durationSource").ToArray();
        }
        Assert.Equal(new[] { "deadline", "fixedEnd", "fixedStart", "id", "kind", "minutes", "note", "priority", "title" }, names);
        Assert.Matches(@"^t\d+$", task.GetProperty("id").GetString()!);
        Assert.InRange(task.GetProperty("title").GetString()!.Length, 1, 80);
        var minutes = task.GetProperty("minutes").GetInt32();
        Assert.True(minutes is >= 5 and <= 240 && minutes % 5 == 0, $"minutes {minutes}");
        Assert.InRange(task.GetProperty("priority").GetInt32(), 1, 3);

        var kind = task.GetProperty("kind").GetString()!;
        Assert.Contains(kind, new[] { "task", "fixed" });
        foreach (var time in new[] { task.GetProperty("fixedStart"), task.GetProperty("fixedEnd") })
        {
            if (kind == "fixed") Assert.Matches(Hhmm, time.GetString()!);
            else Assert.Equal(JsonValueKind.Null, time.ValueKind);
        }

        var deadline = task.GetProperty("deadline");
        if (deadline.ValueKind != JsonValueKind.Null)
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}:\d{2}\+09:00)?$", deadline.GetString()!);
        var note = task.GetProperty("note");
        if (note.ValueKind != JsonValueKind.Null) Assert.InRange(note.GetString()!.Length, 1, 120);
    }

    private static void AssertBlocks(JsonElement blocks)
    {
        var starts = new List<string>();
        foreach (var block in blocks.EnumerateArray())
        {
            Assert.Equal(new[] { "end", "id", "kind", "reason", "start", "taskId", "title" }, Names(block));
            Assert.Matches(@"^b\d+$", block.GetProperty("id").GetString()!);
            var start = block.GetProperty("start").GetString()!;
            var end = block.GetProperty("end").GetString()!;
            Assert.Matches(Hhmm, start);
            Assert.Matches(Hhmm, end);
            Assert.True(string.CompareOrdinal(start, end) < 0, $"{start}-{end}");
            var kind = block.GetProperty("kind").GetString()!;
            Assert.Contains(kind, new[] { "task", "fixed", "break" });
            Assert.Equal(kind == "break", block.GetProperty("taskId").ValueKind == JsonValueKind.Null);
            Assert.InRange(block.GetProperty("reason").GetString()!.Length, 1, 120);
            starts.Add(start);
        }

        Assert.Equal(starts.Order(StringComparer.Ordinal), starts);
    }

    private static void AssertDeferred(JsonElement deferred)
    {
        Assert.Equal(new[] { "reason", "taskId", "title" }, Names(deferred));
        Assert.Matches(@"^t\d+$", deferred.GetProperty("taskId").GetString()!);
        Assert.False(string.IsNullOrWhiteSpace(deferred.GetProperty("reason").GetString()));
    }

    private static void AssertRisks(JsonElement risks)
    {
        Assert.InRange(risks.GetArrayLength(), 0, 3);
        foreach (var risk in risks.EnumerateArray())
        {
            Assert.Equal(new[] { "mitigation", "severity", "title" }, Names(risk));
            Assert.Contains(risk.GetProperty("severity").GetString()!, new[] { "low", "medium", "high" });
            Assert.InRange(risk.GetProperty("title").GetString()!.Length, 1, 60);
            Assert.InRange(risk.GetProperty("mitigation").GetString()!.Length, 1, 60);
        }
    }

    private static void AssertValidation(JsonElement validation)
    {
        Assert.Equal(new[] { "availableMinutes", "loadRatio", "occupiedMinutes", "valid", "violations" }, Names(validation));
        var ratio = validation.GetProperty("loadRatio").GetDouble();
        Assert.True(ratio >= 0 && Math.Round(ratio, 2) == ratio, $"loadRatio {ratio}");
        Assert.True(validation.GetProperty("occupiedMinutes").GetInt32() >= 0);
        Assert.True(validation.GetProperty("availableMinutes").GetInt32() >= 0);

        var violations = validation.GetProperty("violations");
        Assert.Equal(violations.GetArrayLength() == 0, validation.GetProperty("valid").GetBoolean());
        foreach (var violation in violations.EnumerateArray())
        {
            Assert.Equal(new[] { "blockIds", "code", "message" }, Names(violation));
            Assert.Contains(violation.GetProperty("code").GetString()!, ViolationCodes);
            Assert.Equal(JsonValueKind.Array, violation.GetProperty("blockIds").ValueKind);
        }
    }

    private static void AssertMeta(JsonElement meta)
    {
        Assert.Equal(new[] { "aiGenerated", "elapsedMs", "iterations", "models", "repaired", "source" }, Names(meta));
        var source = meta.GetProperty("source").GetString()!;
        Assert.Contains(source, new[] { "ai", "offline-sample" });
        Assert.Equal(source == "ai", meta.GetProperty("aiGenerated").GetBoolean());
        Assert.True(meta.GetProperty("iterations").GetInt32() >= 0);
        Assert.True(meta.GetProperty("elapsedMs").GetInt64() >= 0);
        Assert.Contains(meta.GetProperty("repaired").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });

        var models = meta.GetProperty("models");
        if (models.ValueKind != JsonValueKind.Null) Assert.Equal(new[] { "fast", "smart" }, Names(models));
    }
}

public class PlanContractTests
{
    [Fact]
    public async Task Plan_HappyPath_MatchesTheDocumentedShape()
    {
        var run = await Scenario.Happy().RunAsync();

        PlanShape.AssertContract(run.Of("plan").Single().Data);
    }

    [Fact]
    public async Task Plan_HappyPath_UsesCamelCaseEverywhere()
    {
        var run = await Scenario.Happy().RunAsync();

        var names = PlanShape.AllPropertyNames(run.Of("plan").Single().Data).ToList();
        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.True(char.IsLower(name[0]), name));
    }

    [Fact]
    public async Task Plan_PlainTaskAndBreakBlock_CarryExplicitNullsInsteadOfMissingFields()
    {
        var plan = (await Scenario.Happy().RunAsync()).Of("plan").Single().Data;

        var mail = plan.GetProperty("tasks")[1];
        Assert.Equal(JsonValueKind.Null, mail.GetProperty("deadline").ValueKind);
        Assert.Equal(JsonValueKind.Null, mail.GetProperty("fixedStart").ValueKind);
        var lunch = plan.GetProperty("blocks").EnumerateArray().Single(b => b.GetProperty("kind").GetString() == "break");
        Assert.Equal(JsonValueKind.Null, lunch.GetProperty("taskId").ValueKind);
    }

    [Fact]
    public async Task Plan_HappyPath_ReportsTheStandardDay()
    {
        var plan = (await Scenario.Happy().RunAsync()).Plan!;

        Assert.Equal((Scenario.Day, "09:00", "18:00"), (plan.Day, plan.WorkStart, plan.WorkEnd));
        Assert.Equal(new[] { "t1", "t2", "t3", "t4" }, plan.Tasks.Select(t => t.Id));
        Assert.Equal(new[] { "b1", "b2", "b3", "b4", "b5" }, plan.Blocks.Select(b => b.Id));
        Assert.Equal(new[] { "09:00", "09:35", "12:00", "13:05", "14:00" }, plan.Blocks.Select(b => b.Start));
        Assert.Equal(("ai", true, 1, false), (plan.Meta.Source, plan.Meta.AiGenerated, plan.Meta.Iterations, plan.Meta.Repaired));
        Assert.Equal(("fake-fast", "fake-smart"), (plan.Meta.Models!.Fast, plan.Meta.Models.Smart));
        Assert.True(plan.Validation.Valid);
    }

    [Fact]
    public async Task Plan_SchedulerAnswersOutOfOrder_BlocksComeSortedByStartWithFreshIds()
    {
        var h = Scenario.Happy();
        h.Runner.Reply("scheduler", Scenario.Schedule(("t4", "13:05", "13:35"), ("t1", "09:35", "11:35"), ("t2", "09:00", "09:30")));

        var plan = (await h.RunAsync()).Plan!;

        Assert.Equal(new[] { "09:00", "09:35", "12:00", "13:05", "14:00" }, plan.Blocks.Select(b => b.Start));
        Assert.Equal(new[] { "b1", "b2", "b3", "b4", "b5" }, plan.Blocks.Select(b => b.Id));
    }

    [Fact]
    public async Task Plan_WithDeferredTaskAndRisks_MatchesTheDocumentedShape()
    {
        var h = Scenario.Happy();
        h.Runner
            .Reply("scheduler", Scenario.Schedule(("t2", "09:00", "09:30"), ("t1", "09:35", "11:35")))
            .Reply("critic", Scenario.Critic("approve", null,
                ("마감 직전에 끝납니다", "오전에 먼저 끝냅니다.", "high"), ("점심 뒤 집중이 떨어집니다", "10분 걷습니다.", "low")));

        var plan = (await h.RunAsync()).Of("plan").Single().Data;

        PlanShape.AssertContract(plan);
        Assert.Equal(1, plan.GetProperty("deferred").GetArrayLength());
        Assert.Equal(2, plan.GetProperty("risks").GetArrayLength());
    }

    [Fact]
    public async Task Plan_RepairedRun_MatchesTheDocumentedShape()
    {
        var h = Scenario.Happy();
        h.Runner.Reply("scheduler", Scenario.OverlapSchedule());

        var run = await h.RunAsync();

        PlanShape.AssertContract(run.Of("plan").Single().Data);
        Assert.True(run.Plan!.Meta.Repaired);
    }

    [Fact]
    public async Task Plan_HappyPath_RoundTripsThroughThePlanResultRecordUnchanged()
    {
        var raw = (await Scenario.Happy().RunAsync()).Of("plan").Single().Data.GetRawText();

        var again = JsonSerializer.Serialize(JsonSerializer.Deserialize<PlanResult>(raw, HaruJson.Options), HaruJson.Options);

        Assert.Equal(raw, again);
    }
}

public class EventContractTests
{
    private static readonly string[] DocumentedEvents =
        ["run_started", "agent_started", "agent_delta", "tool_call", "tool_result", "agent_completed",
         "tasks", "schedule", "validation", "critique", "plan", "done", "error"];

    private static readonly string[] DocumentedAgents =
        ["intake", "estimator", "prioritizer", "scheduler", "critic", "coach", "normalize", "validator", "repair"];

    private static async Task<PlanRun> RepairedRunAsync()
    {
        var h = Scenario.Happy();
        h.Runner.Reply("scheduler", Scenario.OverlapSchedule());
        return await h.RunAsync();
    }

    [Theory]
    [InlineData("run_started", "aiAvailable,day,runId")]
    [InlineData("agent_started", "agent,model,role")]
    [InlineData("agent_completed", "agent,elapsedMs,summary")]
    [InlineData("tool_call", "agent,args,callId,tool")]
    [InlineData("tool_result", "agent,callId,ok,summary,tool")]
    [InlineData("tasks", "tasks")]
    [InlineData("schedule", "blocks,deferred,iteration")]
    [InlineData("validation", "iteration,report")]
    [InlineData("critique", "decision,iteration,risks")]
    [InlineData("done", "elapsedMs,repaired,source")]
    public async Task Event_HappyPath_CarriesTheDocumentedFields(string name, string fields)
    {
        var run = await Scenario.Happy().RunAsync();

        Assert.NotEmpty(run.Of(name));
        Assert.All(run.Of(name), e => Assert.Equal(fields.Split(','), PlanShape.Names(e.Data)));
    }

    [Fact]
    public async Task Event_ToolResults_MatchUniqueCallsById()
    {
        var run = await Scenario.Happy().RunAsync();
        var calls = run.Of("tool_call").ToDictionary(e => e.Data.GetProperty("callId").GetString()!);
        Assert.Equal(calls.Count, run.Of("tool_result").Count());
        foreach (var result in run.Of("tool_result"))
        {
            var id = result.Data.GetProperty("callId").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.True(calls.Remove(id, out var call));
            Assert.Equal(call.Data.GetProperty("agent").GetString(), result.Data.GetProperty("agent").GetString());
            Assert.Equal(call.Data.GetProperty("tool").GetString(), result.Data.GetProperty("tool").GetString());
        }
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Event_Done_ReportsTheSourceAndTheRepairFlagOfThePlan()
    {
        var run = await RepairedRunAsync();

        var done = run.Of("done").Single().Data;
        Assert.Equal("ai", done.GetProperty("source").GetString());
        Assert.True(done.GetProperty("repaired").GetBoolean());
        Assert.True(done.GetProperty("elapsedMs").GetInt64() >= 0);
    }

    [Fact]
    public async Task Event_Critique_CarriesTheDecisionOfTheCritic()
    {
        var h = Scenario.Happy();
        h.Runner.Reply("critic", Scenario.Revise(), Scenario.Approve());

        var run = await h.RunAsync();

        Assert.Equal(new[] { 1, 2 }, run.Of("critique").Select(e => e.Data.GetProperty("iteration").GetInt32()));
        Assert.Equal(new[] { "revise", "approve" }, run.Of("critique").Select(e => e.Data.GetProperty("decision").GetString()));
    }

    [Fact]
    public async Task Event_Error_CarriesCodeMessageAndRecoverableAndEndsTheStream()
    {
        var h = Scenario.Happy();
        h.Runner.Reply("intake", Scenario.Intake());

        var run = await h.RunAsync();

        Assert.Equal("error", run.Names[^1]);
        Assert.DoesNotContain("done", run.Names);
        var error = run.Of("error").Single().Data;
        Assert.Equal(new[] { "code", "message", "recoverable" }, PlanShape.Names(error));
        Assert.Equal("NO_TASKS", error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        Assert.Contains(error.GetProperty("recoverable").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
    }

    [Fact]
    public async Task Events_EveryNameOfAHappyRepairedAndFailedRun_IsDocumented()
    {
        var failing = Scenario.Happy();
        failing.Runner.Reply("intake", Scenario.Intake());
        var runs = new[] { await Scenario.Happy().RunAsync(), await RepairedRunAsync(), await failing.RunAsync() };

        var names = runs.SelectMany(r => r.Names).Distinct().ToList();

        Assert.All(names, name => Assert.Contains(name, DocumentedEvents));
    }

    [Fact]
    public async Task Events_EveryAgentIdOfARepairedRun_IsOneTheContractNames()
    {
        var run = await RepairedRunAsync();

        var ids = run.Events
            .Where(e => e.Data.ValueKind == JsonValueKind.Object && e.Data.TryGetProperty("agent", out _))
            .Select(e => e.Data.GetProperty("agent").GetString()!)
            .Distinct().ToList();

        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.Contains(id, DocumentedAgents));
    }
}

public class SampleContractTests(HaruApiFactory factory) : IClassFixture<HaruApiFactory>
{
    [Fact]
    public async Task GetSample_Plan_MatchesTheDocumentedShape()
    {
        var sample = await (await factory.ClientFrom("10.0.7.1").GetAsync("/api/sample")).ReadJsonAsync();

        var plan = sample.GetProperty("plan");
        PlanShape.AssertContract(plan);
        Assert.Equal("offline-sample", plan.GetProperty("meta").GetProperty("source").GetString());
    }

    [Fact]
    public async Task GetSample_Plan_DeserializesIntoThePlanResultRecord()
    {
        var sample = await (await factory.ClientFrom("10.0.7.2").GetAsync("/api/sample")).ReadJsonAsync();

        var plan = sample.GetProperty("plan").Deserialize<PlanResult>(HaruJson.Options);

        Assert.NotNull(plan);
        Assert.NotEmpty(plan.Blocks);
        Assert.False(plan.Meta.AiGenerated);
    }
}
