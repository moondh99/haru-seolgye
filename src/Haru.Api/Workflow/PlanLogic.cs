using Haru.Api.Contracts;
using Haru.Api.Infrastructure;

namespace Haru.Api.Workflow;

public sealed record ResolvedDate(bool Ok, string? Date, string? Time);

public sealed record NormalizeResult(
    IReadOnlyList<TaskItem> Tasks, IReadOnlyList<Block> FixedBlocks, IReadOnlyList<Deferred> PreDeferred);

/// <summary>
/// Pure code that turns untrusted model output into checked data: clean text, clamped numbers, real dates.
/// Nothing here calls a model, so every rule is unit-testable.
/// </summary>
public static class Normalizer
{
    public const int MaxItems = 30;
    public const string FixedReason = "정해진 일정이라 시간을 바꾸지 않습니다.";
    public const string LunchReason = "기본 점심 시간입니다.";

    public static string Key(string? expression) => TextSanitizer.Clean(expression, 60);

    public static IReadOnlyList<string> Expressions(IEnumerable<IntakeWireItem> items) =>
        items.Take(MaxItems)
            .SelectMany(i => new[] { i.DeadlineText, i.StartText, i.EndText })
            .Select(Key)
            .Where(k => k.Length > 0)
            .Distinct()
            .ToList();

    public static int RoundMinutes(double value) =>
        Math.Clamp((int)(Math.Round(value / 5.0, MidpointRounding.AwayFromZero) * 5), 5, 240);

    public static NormalizeResult Build(PlanContext c, IReadOnlyDictionary<string, ResolvedDate> resolved)
    {
        _ = Clock.TryMinutes(c.WorkStart, out var windowStart);
        _ = Clock.TryMinutes(c.WorkEnd, out var windowEnd);

        ResolvedDate? Lookup(string? text)
        {
            var key = Key(text);
            return key.Length > 0 && resolved.TryGetValue(key, out var r) ? r : null;
        }

        var tasks = new List<TaskItem>();
        var fixedBlocks = new List<Block>();
        var deferred = new List<Deferred>();
        var taskNumber = 0;
        var fixedNumber = 0;

        foreach (var item in c.Items.Take(MaxItems))
        {
            var title = TextSanitizer.Clean(item.Title, 80);
            if (title.Length == 0) continue;

            var id = $"t{++taskNumber}";
            int? minutes = item.Minutes is > 0 and < 100_000 ? RoundMinutes(item.Minutes.Value) : null;
            var note = TextSanitizer.Clean(item.Note, 120);
            if (note.Length == 0) note = null;

            if (item.Fixed == true && TryBuildFixed(item, minutes) is { } fixedEvent)
            {
                var (date, start, end, source) = fixedEvent;
                var length = RoundMinutes(end - start);
                tasks.Add(new TaskItem(id, title, length, 1, null, Clock.Hhmm(start), Clock.Hhmm(end), "fixed", note, source));

                if (date != c.Day)
                    deferred.Add(new Deferred(id, title, "다른 날 일정이라 오늘 시간표에는 넣지 않습니다."));
                else if (end <= windowStart || start >= windowEnd)
                    deferred.Add(new Deferred(id, title, "근무 시간 밖 일정이라 시간표에는 넣지 않습니다."));
                else
                    fixedBlocks.Add(new Block($"f{++fixedNumber}", id, title,
                        Clock.Hhmm(Math.Max(start, windowStart)), Clock.Hhmm(Math.Min(end, windowEnd)), "fixed", FixedReason));
                continue;
            }

            tasks.Add(new TaskItem(id, title, minutes ?? 0, 2, Deadline(Lookup(item.DeadlineText), c.Day), null, null, "task", note,
                minutes is not null ? "input" : null));
        }

        if (LunchBlock(c, windowStart, windowEnd, fixedBlocks) is { } lunch) fixedBlocks.Add(lunch);
        return new NormalizeResult(tasks, fixedBlocks, deferred);

        (string Date, int Start, int End, string Source)? TryBuildFixed(IntakeWireItem item, int? minutes)
        {
            var start = Lookup(item.StartText);
            if (start is not { Ok: true, Time: not null } || !Clock.TryMinutes(start.Time, out var startMinutes)) return null;

            var date = start.Date ?? c.Day;
            var end = Lookup(item.EndText);
            var endMinutes = startMinutes + (minutes ?? 60);
            var source = minutes is not null ? "input" : "default";
            if (end is { Ok: true, Time: not null } && (end.Date ?? date) == date
                && Clock.TryMinutes(end.Time, out var stated) && stated > startMinutes)
            {
                endMinutes = stated;
                source = "fixed";
            }

            endMinutes = Math.Min(endMinutes, 24 * 60 - 1);
            return endMinutes > startMinutes ? (date, startMinutes, endMinutes, source) : null;
        }
    }

    public static string? Deadline(ResolvedDate? r, string day)
    {
        if (r is not { Ok: true }) return null;
        var date = r.Date ?? (r.Time is not null ? day : null);
        if (date is null) return null;
        return r.Time is null ? date : $"{date}T{r.Time}:00+09:00";
    }

    /// <summary>The latest end time of a task block today, or null when the deadline does not limit today.</summary>
    public static string? DeadlineTime(TaskItem task, string day, string workEnd)
    {
        if (task.Deadline is not { Length: >= 10 } deadline) return null;
        var order = string.CompareOrdinal(deadline[..10], day);
        if (order > 0) return null;
        if (order == 0 && deadline.Length >= 16) return deadline.Substring(11, 5);
        return workEnd;
    }

    private static Block? LunchBlock(PlanContext c, int windowStart, int windowEnd, List<Block> fixedBlocks)
    {
        if (!Clock.TryMinutes(c.LunchStart, out var start) || !Clock.TryMinutes(c.LunchEnd, out var end)) return null;
        if (start >= end || start < windowStart || end > windowEnd) return null;

        foreach (var b in fixedBlocks)
        {
            if (Clock.TryMinutes(b.Start, out var bs) && Clock.TryMinutes(b.End, out var be) && bs < end && start < be)
                return null;
        }
        return new Block("lunch", null, "점심", Clock.Hhmm(start), Clock.Hhmm(end), "break", LunchReason);
    }

    public static IReadOnlyDictionary<string, int> ReadEstimates(EstimatorWire wire, IReadOnlySet<string> wanted)
    {
        var result = new Dictionary<string, int>();
        foreach (var e in wire.Estimates ?? [])
        {
            if (e.Id is { } id && wanted.Contains(id) && e.Minutes is > 0 and < 100_000 && !result.ContainsKey(id))
                result[id] = RoundMinutes(e.Minutes.Value);
        }
        return result;
    }

    public static IReadOnlyDictionary<string, int> ReadPriorities(PrioritizerWire wire, IReadOnlySet<string> wanted)
    {
        var result = new Dictionary<string, int>();
        foreach (var p in wire.Priorities ?? [])
        {
            if (p.Id is { } id && wanted.Contains(id) && p.Priority is >= 1 and <= 3 && !result.ContainsKey(id))
                result[id] = (int)Math.Round(p.Priority.Value);
        }
        return result;
    }

    public static IReadOnlyList<TaskItem> Apply(
        IReadOnlyList<TaskItem> tasks, IReadOnlyDictionary<string, int>? minutes, IReadOnlyDictionary<string, int>? priorities)
    {
        return tasks.Select(t =>
        {
            if (t.Kind != "task") return t;
            var estimated = minutes is not null && minutes.ContainsKey(t.Id);
            var m = t.Minutes > 0 ? t.Minutes : estimated ? minutes![t.Id] : 30;
            var p = priorities is not null && priorities.TryGetValue(t.Id, out var ep) ? ep : t.Priority;
            return t with { Minutes = m, Priority = p, DurationSource = t.Minutes > 0 ? t.DurationSource ?? "input" : estimated ? "estimated" : "default" };
        }).ToList();
    }
}

/// <summary>Builds a checked block list from scheduler output. The model proposes start times; code decides everything else.</summary>
public static class ScheduleAssembler
{
    public const string DefaultReason = "우선순위와 마감을 고려해 정한 시간입니다.";
    public const string DefaultDeferred = "오늘 시간 안에 넣지 못해 다른 날로 옮깁니다.";

    public static (IReadOnlyList<Block> Blocks, IReadOnlyList<Deferred> Deferred) Assemble(PlanContext c, SchedulerWire wire)
    {
        var tasks = c.PlannableTasks.ToDictionary(t => t.Id);
        var placed = new HashSet<string>();
        var blocks = new List<Block>();

        foreach (var b in wire.Blocks ?? [])
        {
            if (b.TaskId is null || !tasks.TryGetValue(b.TaskId, out var task) || placed.Contains(b.TaskId)) continue;
            if (!string.Equals(b.Kind ?? "task", "task", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Clock.TryMinutes(b.Start, out var start)) continue;

            var end = start + task.Minutes;
            if (end > 24 * 60 - 1) continue;

            var reason = TextSanitizer.Clean(b.Reason, 120);
            placed.Add(task.Id);
            blocks.Add(new Block("", task.Id, task.Title, Clock.Hhmm(start), Clock.Hhmm(end), "task",
                reason.Length > 0 ? reason : DefaultReason));
        }

        var deferred = new List<Deferred>();
        foreach (var d in wire.Deferred ?? [])
        {
            if (d.TaskId is null || !tasks.TryGetValue(d.TaskId, out var task) || placed.Contains(task.Id)) continue;
            if (deferred.Any(x => x.TaskId == task.Id)) continue;
            var reason = TextSanitizer.Clean(d.Reason, 120);
            deferred.Add(new Deferred(task.Id, task.Title, reason.Length > 0 ? reason : DefaultDeferred));
        }

        foreach (var task in tasks.Values)
        {
            if (!placed.Contains(task.Id) && deferred.All(x => x.TaskId != task.Id))
                deferred.Add(new Deferred(task.Id, task.Title, DefaultDeferred));
        }

        return (Number(blocks.Concat(c.FixedBlocks)), deferred);
    }

    /// <summary>Sorts by time and gives the ids b1, b2, ... in that order.</summary>
    public static IReadOnlyList<Block> Number(IEnumerable<Block> blocks) =>
        blocks.OrderBy(b => b.Start, StringComparer.Ordinal).ThenBy(b => b.End, StringComparer.Ordinal)
            .Select((b, i) => b with { Id = $"b{i + 1}" })
            .ToList();
}

/// <summary>Rules for the deterministic repair step.</summary>
public static class Repair
{
    public const string Reason = "시간 충돌을 없애기 위해 다른 날로 옮깁니다.";

    public static Block? PickVictim(
        IReadOnlyList<Block> blocks, ValidationReport report, IReadOnlyDictionary<string, TaskItem> tasks)
    {
        var implicated = report.Violations.SelectMany(v => v.BlockIds).ToHashSet();
        var taskBlocks = blocks.Where(b => b.Kind == "task" && b.TaskId is not null && tasks.ContainsKey(b.TaskId)).ToList();
        var candidates = taskBlocks.Where(b => implicated.Contains(b.Id)).ToList();
        if (candidates.Count == 0) candidates = taskBlocks;

        return candidates
            .OrderByDescending(b => tasks[b.TaskId!].Priority)
            .ThenByDescending(b => b.End, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Violations that only involve user-fixed or break blocks cannot be solved by moving tasks.</summary>
    public static bool OnlyFixed(ValidationReport report, IReadOnlyList<Block> blocks)
    {
        if (report.Violations.Length == 0) return false;
        var nonTask = blocks.Where(b => b.Kind != "task").Select(b => b.Id).ToHashSet();
        return report.Violations.All(v => v.BlockIds.Length > 0 && v.BlockIds.All(nonTask.Contains));
    }

    /// <summary>Back-to-back fixed events are the user's choice, so the missing buffer between them is not a plan error.</summary>
    public static ValidationReport IgnoreUserChoices(ValidationReport report, IReadOnlyList<Block> blocks)
    {
        var nonTask = blocks.Where(b => b.Kind != "task").Select(b => b.Id).ToHashSet();
        var kept = report.Violations
            .Where(v => !(v.Code == "NO_BUFFER" && v.BlockIds.Length > 0 && v.BlockIds.All(nonTask.Contains)))
            .ToArray();
        return report with { Valid = kept.Length == 0 && (report.Valid || report.Violations.Length > 0), Violations = kept };
    }
}
