using System.Globalization;

namespace Haru.Mcp.Logic;

public static class ScheduleValidator
{
    public const string InvalidRange = "INVALID_RANGE";
    public const string OutOfHours = "OUT_OF_HOURS";
    public const string Overlap = "OVERLAP";
    public const string NoBuffer = "NO_BUFFER";
    public const string Overload = "OVERLOAD";
    public const string DeadlineMissed = "DEADLINE_MISSED";

    private const string KindTask = "task";
    private const string KindFixed = "fixed";
    private const string KindBreak = "break";

    private sealed record Item(string Id, string Title, int Start, int End, string Kind, int? Deadline);

    // Order is the sort key that gives violations their stable contract order (code order, then first block start).
    private sealed record Finding(int Order, int Start, Violation Violation);

    public static ValidationReport Validate(
        string? workStart, string? workEnd, IReadOnlyList<ScheduleBlock?>? blocks, int bufferMinutes, double maxLoad)
    {
        blocks ??= [];
        if (blocks.Count > TimeUtil.MaxItems)
        {
            return Rejected($"블록은 최대 {TimeUtil.MaxItems}개까지 검사할 수 있습니다.");
        }

        if (!TimeUtil.TryParseHhmm(workStart, out var windowStart)
            || !TimeUtil.TryParseHhmm(workEnd, out var windowEnd)
            || windowStart >= windowEnd)
        {
            return Rejected("근무 시간(workStart, workEnd)은 HH:mm 형식이고 workStart가 workEnd보다 빨라야 합니다.");
        }

        if (bufferMinutes is < 0 or > 1440 || !double.IsFinite(maxLoad) || maxLoad is < 0 or > 1000)
        {
            return Rejected("bufferMinutes는 0~1440, maxLoad는 0~1000 사이여야 합니다.");
        }

        var findings = new List<Finding>();
        var items = new List<Item>();
        foreach (var block in blocks)
        {
            var item = Parse(block, out var problem);
            if (item is not null)
            {
                items.Add(item);
            }
            else
            {
                var id = block?.Id?.Trim();
                string[] ids = string.IsNullOrEmpty(id) || id.Length > TimeUtil.MaxText ? [] : [id];
                findings.Add(new Finding(0, 0, new Violation(InvalidRange, problem!, ids)));
            }
        }

        // The caller's list is never touched; all checks run on a sorted copy.
        var sorted = items.OrderBy(item => item.Start).ThenBy(item => item.End).ToList();
        var window = $"{TimeUtil.Format(windowStart)}~{TimeUtil.Format(windowEnd)}";

        foreach (var item in sorted)
        {
            if (item.Kind != KindFixed && (item.Start < windowStart || item.End > windowEnd))
            {
                Add(findings, 1, item.Start, OutOfHours, $"{Label(item)} 블록({Span(item)})이 근무 시간({window})을 벗어납니다.", item);
            }
        }

        for (var i = 0; i < sorted.Count; i++)
        {
            for (var j = i + 1; j < sorted.Count && sorted[j].Start < sorted[i].End; j++)
            {
                Add(findings, 2, sorted[i].Start, Overlap,
                    $"{Label(sorted[i])} 블록({Span(sorted[i])}) 및 {Label(sorted[j])} 블록({Span(sorted[j])})의 시간이 겹칩니다.",
                    sorted[i], sorted[j]);
            }
        }

        for (var i = 0; i + 1 < sorted.Count; i++)
        {
            var (current, next) = (sorted[i], sorted[i + 1]);
            var gap = next.Start - current.End;
            if (gap >= 0 && gap < bufferMinutes && current.Kind != KindBreak && next.Kind != KindBreak)
            {
                Add(findings, 3, current.Start, NoBuffer,
                    $"{Label(current)} 블록의 종료({TimeUtil.Format(current.End)}) 후 {Label(next)} 블록 시작({TimeUtil.Format(next.Start)})까지 간격이 {gap}분으로, 최소 {bufferMinutes}분보다 짧습니다.",
                    current, next);
            }
        }

        var occupied = items.Where(item => item.Kind != KindBreak).Sum(item => item.End - item.Start);
        var breaks = items.Where(item => item.Kind == KindBreak).Sum(item => item.End - item.Start);
        var available = Math.Max(0, windowEnd - windowStart - breaks);
        // The reported (2-decimal) ratio is what gets compared, so exactly maxLoad is never an overload.
        var ratio = Math.Round((decimal)occupied / Math.Max(available, 1), 2, MidpointRounding.AwayFromZero);
        if (ratio > (decimal)maxLoad)
        {
            var text = $"하루 부하율이 허용 한도를 넘습니다(부하율 {ratio.ToString("0.##", CultureInfo.InvariantCulture)}, 허용 한도 {((decimal)maxLoad).ToString("0.##", CultureInfo.InvariantCulture)}).";
            Add(findings, 4, 0, Overload, text);
        }

        foreach (var item in sorted)
        {
            if (item.Kind == KindTask && item.Deadline is { } deadline && item.End > deadline)
            {
                Add(findings, 5, item.Start, DeadlineMissed,
                    $"{Label(item)} 블록의 종료 시각({TimeUtil.Format(item.End)})이 마감 시각({TimeUtil.Format(deadline)})보다 늦습니다.", item);
            }
        }

        var violations = findings.OrderBy(f => f.Order).ThenBy(f => f.Start).Select(f => f.Violation).ToList();
        return new ValidationReport(violations.Count == 0, (double)ratio, occupied, available, violations);
    }

    internal static ValidationReport Rejected(string message) =>
        new(false, 0, 0, 0, [new Violation(InvalidRange, message, [])]);

    private static void Add(List<Finding> findings, int order, int start, string code, string message, params Item[] blocks) =>
        findings.Add(new Finding(order, start, new Violation(code, message, blocks.Select(block => block.Id).ToArray())));

    private static string Label(Item item) => $"'{TimeUtil.ForMessage(item.Title.Length > 0 ? item.Title : item.Id)}'";

    private static string Span(Item item) => $"{TimeUtil.Format(item.Start)}~{TimeUtil.Format(item.End)}";

    private static Item? Parse(ScheduleBlock? block, out string? problem)
    {
        problem = null;
        if (block is null)
        {
            problem = "비어 있는 블록이 있어 검사할 수 없습니다.";
            return null;
        }

        var id = block.Id?.Trim() ?? "";
        var title = TimeUtil.SingleLine(block.Title);
        var name = $"'{TimeUtil.ForMessage(title.Length > 0 ? title : id)}'";
        if (id.Length == 0)
        {
            problem = $"id가 비어 있는 블록이 있습니다(제목: {name}).";
            return null;
        }

        if (TimeUtil.TooLong(id) || TimeUtil.TooLong(block.Title) || TimeUtil.TooLong(block.Kind))
        {
            problem = $"{name} 블록에 {TimeUtil.MaxText}자를 넘는 값이 있습니다.";
            return null;
        }

        var kind = block.Kind?.Trim().ToLowerInvariant();
        if (kind is not (KindTask or KindFixed or KindBreak))
        {
            problem = $"{name} 블록의 kind는 task, fixed, break 중 하나여야 합니다.";
            return null;
        }

        if (!TimeUtil.TryParseHhmm(block.Start, out var start) || !TimeUtil.TryParseHhmm(block.End, out var end))
        {
            problem = $"{name} 블록의 시작·종료 시각이 HH:mm 형식이 아닙니다.";
            return null;
        }

        if (start >= end)
        {
            problem = $"{name} 블록의 시작 시각({TimeUtil.Format(start)})이 종료 시각({TimeUtil.Format(end)})보다 늦거나 같습니다.";
            return null;
        }

        int? deadline = null;
        if (!string.IsNullOrWhiteSpace(block.DeadlineTime))
        {
            if (!TimeUtil.TryParseHhmm(block.DeadlineTime, out var parsed))
            {
                problem = $"{name} 블록의 마감 시각(deadlineTime)이 HH:mm 형식이 아닙니다.";
                return null;
            }

            deadline = parsed;
        }

        return new Item(id, title, start, end, kind, deadline);
    }
}
