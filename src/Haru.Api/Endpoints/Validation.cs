using System.Text.Json;
using Haru.Api.Config;
using Haru.Api.Contracts;
using Haru.Api.Infrastructure;

namespace Haru.Api.Endpoints;

public sealed record ApiError(int Status, string Code, string Message);

/// <summary>Checks a plan request before any AI call. Every limit here is also written in docs/api-contract.md.</summary>
public static class PlanInputs
{
    public const string DefaultWorkStart = "09:00";
    public const string DefaultWorkEnd = "18:00";
    public const string DefaultLunchStart = "12:00";
    public const string DefaultLunchEnd = "13:00";
    public const int MinWindowMinutes = 60;

    public static (Workflow.PlanInput? Input, ApiError? Error) Validate(PlanRequest? req, HaruOptions options, DateOnly today)
    {
        if (req is null) return (null, Bad("요청 형식이 올바르지 않습니다."));

        var raw = req.Text ?? "";
        if (raw.Length > options.MaxInputChars)
            return (null, new ApiError(400, "INPUT_TOO_LONG", $"입력은 {options.MaxInputChars}자까지 쓸 수 있습니다."));
        var text = TextSanitizer.Clean(raw, options.MaxInputChars, keepNewlines: true);
        if (text.Length == 0)
            return (null, new ApiError(400, "INPUT_EMPTY", "할 일을 한 줄 이상 입력해 주세요."));

        var dayText = string.IsNullOrWhiteSpace(req.Day) ? today.ToString("yyyy-MM-dd") : req.Day.Trim();
        if (!Clock.TryParseDay(dayText, out var day) || day < today.AddDays(-1) || day > today.AddDays(365))
            return (null, Bad("날짜는 YYYY-MM-DD 형식이며 오늘부터 1년 안이어야 합니다."));

        var workStart = Or(req.WorkStart, DefaultWorkStart);
        var workEnd = Or(req.WorkEnd, DefaultWorkEnd);
        if (!Clock.TryMinutes(workStart, out var ws) || !Clock.TryMinutes(workEnd, out var we))
            return (null, Bad("근무 시간은 HH:mm 형식이어야 합니다."));
        if (we - ws < MinWindowMinutes)
            return (null, Bad("근무 시간은 1시간 이상이어야 합니다."));

        // null means "use the default"; an empty string means "no lunch break".
        string lunchStart, lunchEnd;
        if (req.LunchStart is { Length: 0 } || req.LunchEnd is { Length: 0 })
        {
            lunchStart = "";
            lunchEnd = "";
        }
        else
        {
            lunchStart = Or(req.LunchStart, DefaultLunchStart);
            lunchEnd = Or(req.LunchEnd, DefaultLunchEnd);
            if (!Clock.TryMinutes(lunchStart, out var ls) || !Clock.TryMinutes(lunchEnd, out var le) || ls >= le)
                return (null, Bad("점심 시간은 HH:mm 형식이며 시작이 끝보다 빨라야 합니다."));
        }

        return (new Workflow.PlanInput(day.ToString("yyyy-MM-dd"), workStart, workEnd, lunchStart, lunchEnd, text), null);
    }

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static ApiError Bad(string message) => new(400, "BAD_REQUEST", message);
}

/// <summary>Checks an export request. The calendar file is the only output that leaves the app, so it is checked twice: here and by the rule engine.</summary>
public static class ExportInputs
{
    public const int MaxBlocks = 50;

    public static (string Day, string WorkStart, string WorkEnd, IReadOnlyList<Block> Blocks, ApiError? Error) Validate(
        ExportRequest? req, DateOnly today)
    {
        ApiError Fail(int status, string code, string message) => new(status, code, message);
        (string, string, string, IReadOnlyList<Block>, ApiError?) Error(ApiError e) => ("", "", "", [], e);

        if (req is null) return Error(Fail(400, "BAD_REQUEST", "요청 형식이 올바르지 않습니다."));
        if (req.Confirmed != true)
            return Error(Fail(400, "NOT_CONFIRMED", "캘린더로 내보내기 전에 확인이 필요합니다."));
        if (!Clock.TryParseDay(req.Day, out var day) || day < today.AddDays(-1) || day > today.AddDays(365))
            return Error(Fail(400, "BAD_REQUEST", "날짜는 YYYY-MM-DD 형식이며 오늘부터 1년 안이어야 합니다."));

        var workStart = string.IsNullOrWhiteSpace(req.WorkStart) ? "00:00" : req.WorkStart.Trim();
        var workEnd = string.IsNullOrWhiteSpace(req.WorkEnd) ? "23:59" : req.WorkEnd.Trim();
        if (!Clock.TryMinutes(workStart, out var ws) || !Clock.TryMinutes(workEnd, out var we) || ws >= we)
            return Error(Fail(400, "BAD_REQUEST", "근무 시간 형식이 올바르지 않습니다."));

        if (req.Blocks is not { Length: > 0 and <= MaxBlocks })
            return Error(Fail(400, "BAD_REQUEST", $"시간표 블록은 1개 이상 {MaxBlocks}개 이하여야 합니다."));

        var blocks = new List<Block>();
        var ids = new HashSet<string>();
        foreach (var b in req.Blocks)
        {
            var id = TextSanitizer.Clean(b?.Id, 20);
            var title = TextSanitizer.Clean(b?.Title, 80);
            var kind = b?.Kind;
            if (b is null || id.Length == 0 || title.Length == 0 || !ids.Add(id)
                || kind is not ("task" or "fixed" or "break")
                || !Clock.TryMinutes(b.Start, out var s) || !Clock.TryMinutes(b.End, out var e) || s >= e)
                return Error(Fail(400, "BAD_REQUEST", "시간표 블록 형식이 올바르지 않습니다."));

            blocks.Add(new Block(id, null, title, b.Start, b.End, kind, TextSanitizer.Clean(b.Reason, 120)));
        }

        return (day.ToString("yyyy-MM-dd"), workStart, workEnd, blocks, null);
    }
}

/// <summary>Reads the offline sample once at start-up. A malformed sample stops the start, so it cannot fail on stage.</summary>
public sealed class SampleStore
{
    public SampleResponse Sample { get; }

    public SampleStore()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Samples");
        var input = File.ReadAllText(Path.Combine(dir, "sample-input.txt")).Trim();
        var plan = JsonSerializer.Deserialize<PlanResult>(File.ReadAllText(Path.Combine(dir, "sample-plan.json")), HaruJson.Lenient)
                   ?? throw new InvalidDataException("sample-plan.json is empty");
        Sample = new SampleResponse(input, plan, LoadRecordedRun(Path.Combine(dir, "sample-run.json")));
    }

    /// <summary>The recorded run is a real SSE run saved by tests/eval/smoke_plan.py --save. The file is optional.</summary>
    private static List<SampleEvent>? LoadRecordedRun(string path)
    {
        if (!File.Exists(path)) return null;
        var run = JsonSerializer.Deserialize<SampleRun>(File.ReadAllText(path), HaruJson.Lenient)
                  ?? throw new InvalidDataException("sample-run.json is empty");
        var names = run.Events.Select(e => e.Event).ToHashSet();
        if (!names.Contains("plan") || !names.Contains("done") || names.Contains("error"))
            throw new InvalidDataException("sample-run.json must hold a finished run (plan and done, no error)");
        return run.Events;
    }
}
