namespace Haru.Mcp.Logic;

public static class FreeSlotFinder
{
    private const int MaxMinutesParam = 1440;

    public static Outcome<FreeSlotsResult> Find(
        string? workStart, string? workEnd, IReadOnlyList<TimeRange?>? busy, int minMinutes, int bufferMinutes)
    {
        if (!TimeUtil.TryParseHhmm(workStart, out var windowStart) || !TimeUtil.TryParseHhmm(workEnd, out var windowEnd))
        {
            return Outcome<FreeSlotsResult>.Fail("근무 시간(workStart, workEnd)은 HH:mm 형식이어야 합니다.");
        }

        if (windowStart >= windowEnd)
        {
            return Outcome<FreeSlotsResult>.Fail("workStart는 workEnd보다 빨라야 합니다.");
        }

        if (minMinutes is < 0 or > MaxMinutesParam || bufferMinutes is < 0 or > MaxMinutesParam)
        {
            return Outcome<FreeSlotsResult>.Fail("minMinutes와 bufferMinutes는 0~1440 사이여야 합니다.");
        }

        busy ??= [];
        if (busy.Count > TimeUtil.MaxItems)
        {
            return Outcome<FreeSlotsResult>.Fail($"busy는 최대 {TimeUtil.MaxItems}개까지 보낼 수 있습니다.");
        }

        var intervals = new List<(int Start, int End)>();
        foreach (var item in busy)
        {
            if (item is null
                || !TimeUtil.TryParseHhmm(item.Start, out var start)
                || !TimeUtil.TryParseHhmm(item.End, out var end)
                || start >= end)
            {
                return Outcome<FreeSlotsResult>.Fail("busy의 각 구간은 HH:mm 형식이고 start가 end보다 빨라야 합니다.");
            }

            var clippedStart = Math.Max(start, windowStart);
            var clippedEnd = Math.Min(end, windowEnd);
            if (clippedStart < clippedEnd)
            {
                intervals.Add((clippedStart, clippedEnd));
            }
        }

        intervals.Sort();
        var merged = new List<(int Start, int End)>();
        foreach (var interval in intervals)
        {
            if (merged.Count > 0 && interval.Start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, interval.End));
            }
            else
            {
                merged.Add(interval);
            }
        }

        var slots = new List<FreeSlot>();
        var cursor = windowStart;
        foreach (var block in merged)
        {
            AddSlot(slots, cursor, block.Start, windowStart, windowEnd, minMinutes, bufferMinutes);
            cursor = block.End;
        }

        AddSlot(slots, cursor, windowEnd, windowStart, windowEnd, minMinutes, bufferMinutes);
        return Outcome<FreeSlotsResult>.Ok(new FreeSlotsResult(slots, slots.Sum(slot => slot.Minutes)));
    }

    // A gap only loses its buffer on sides that touch a busy interval, never at the work-window edges.
    private static void AddSlot(
        List<FreeSlot> slots, int gapStart, int gapEnd, int windowStart, int windowEnd, int minMinutes, int bufferMinutes)
    {
        var start = gapStart == windowStart ? gapStart : gapStart + bufferMinutes;
        var end = gapEnd == windowEnd ? gapEnd : gapEnd - bufferMinutes;
        var minutes = end - start;
        if (minutes > 0 && minutes >= minMinutes)
        {
            slots.Add(new FreeSlot(TimeUtil.Format(start), TimeUtil.Format(end), minutes));
        }
    }
}
