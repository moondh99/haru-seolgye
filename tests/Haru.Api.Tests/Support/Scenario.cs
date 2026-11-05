using System.Text.Json;
using Haru.Api.Contracts;
using Haru.Api.Workflow;

namespace Haru.Api.Tests.Support;

/// <summary>Canned model replies and inputs. The standard day has 4 items: 2 plain tasks, 1 fixed meeting and lunch.</summary>
public static class Scenario
{
    public const string Day = "2026-11-05"; // Thursday

    public const string Text = "제안서 초안 오늘 17시까지\n메일 답장 정리 30분\n14시부터 15시까지 팀 주간 회의\n운동";

    public static PlanInput Input(string? text = null, string workStart = "09:00", string workEnd = "18:00",
        string lunchStart = "12:00", string lunchEnd = "13:00") =>
        new(Day, workStart, workEnd, lunchStart, lunchEnd, text ?? Text);

    public static string Json(object? value) => JsonSerializer.Serialize(value, HaruJson.Options);

    public static object Item(string title, double? minutes = null, string? deadline = null,
        string? start = null, string? end = null, bool? fixedItem = null, string? note = null) =>
        new { title, minutes, deadlineText = deadline, startText = start, endText = end, @fixed = fixedItem, note };

    public static string Intake(params object[] items) => Json(new { items });

    /// <summary>t1 proposal (deadline 17:00), t2 mail (30 min), t3 fixed meeting 14-15, t4 workout.</summary>
    public static string StandardIntake() => Intake(
        Item("제안서 초안 작성", deadline: "오늘 17시까지"),
        Item("메일 답장 정리", minutes: 30),
        Item("팀 주간 회의", start: "오늘 14시", end: "오늘 15시", fixedItem: true),
        Item("운동"));

    public static string Estimates(params (string Id, double Minutes)[] items) =>
        Json(new { estimates = items.Select(i => new { id = i.Id, minutes = i.Minutes, why = "비슷한 일을 기준으로 잡았습니다." }) });

    public static string Priorities(params (string Id, double Priority)[] items) =>
        Json(new { priorities = items.Select(i => new { id = i.Id, priority = i.Priority, why = "마감이 가깝습니다." }) });

    public static string Schedule(params (string TaskId, string Start, string End)[] blocks) =>
        Json(new
        {
            blocks = blocks.Select(b => new { id = "x", taskId = b.TaskId, title = "-", start = b.Start, end = b.End, kind = "task", reason = "집중하기 좋은 시간입니다." }),
            deferred = Array.Empty<object>(),
        });

    /// <summary>t2 09:00-09:30, t1 09:35-11:35, t4 13:05-13:35 around lunch and the 14:00 meeting: no rule violation.</summary>
    public static string ValidSchedule() =>
        Schedule(("t2", "09:00", "09:30"), ("t1", "09:35", "11:35"), ("t4", "13:05", "13:35"));

    /// <summary>t2 and t1 both start at 09:00: one OVERLAP.</summary>
    public static string OverlapSchedule() =>
        Schedule(("t2", "09:00", "09:30"), ("t1", "09:00", "11:00"), ("t4", "13:05", "13:35"));

    public static string Critic(string decision = "approve", string? revision = null,
        params (string Title, string Mitigation, string Severity)[] risks) =>
        Json(new
        {
            decision,
            risks = risks.Select(r => new { title = r.Title, mitigation = r.Mitigation, severity = r.Severity }),
            revision,
        });

    public static string Approve() => Critic("approve", null, ("회의가 길어질 수 있음", "회의 뒤에 여유를 둡니다.", "medium"));

    public static string Revise() => Critic("revise", "Move the proposal to the afternoon so it has more focus time.");

    public static string Coach(string summary = "오늘은 제안서에 먼저 집중하세요.") => Json(new { summary });

    /// <summary>Scripts all six agents with the happy path. Script one agent again afterwards to override it.</summary>
    public static FakeAgentRunner ScriptHappy(this FakeAgentRunner runner) => runner
        .Reply("intake", StandardIntake())
        .Reply("estimator", Estimates(("t1", 120), ("t4", 30)))
        .Reply("prioritizer", Priorities(("t1", 1), ("t2", 2), ("t4", 3)))
        .Reply("scheduler", ValidSchedule())
        .Reply("critic", Approve())
        .Reply("coach", Coach());

    public static PlanHarness Happy(Func<Haru.Api.Config.HaruOptions, Haru.Api.Config.HaruOptions>? configure = null)
    {
        var harness = new PlanHarness(configure);
        harness.Runner.ScriptHappy();
        return harness;
    }
}
