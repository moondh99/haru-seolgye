using System.Text.Json;
using Haru.Mcp.Logic;

namespace Haru.Mcp.Tests;

public class FreeSlotFinderTests
{
    private static TimeRange Busy(string? start, string? end) => new() { Start = start, End = end };

    private static FreeSlotsResult SlotsWith(int minMinutes, int bufferMinutes, params TimeRange?[] busy)
    {
        var outcome = FreeSlotFinder.Find("09:00", "18:00", busy, minMinutes, bufferMinutes);
        Assert.True(outcome.IsOk, outcome.Error);
        Assert.Null(outcome.Error);
        return outcome.Value!;
    }

    private static FreeSlotsResult Slots(params TimeRange?[] busy) => SlotsWith(5, 5, busy);

    private static string[] Show(FreeSlotsResult result) =>
        result.Slots.Select(slot => $"{slot.Start}-{slot.End}/{slot.Minutes}").ToArray();

    [Fact]
    public void Empty_busy_list_gives_the_whole_window()
    {
        var empty = Slots();
        var missing = FreeSlotFinder.Find("09:00", "18:00", null, 5, 5).Value!;

        Assert.Equal(new[] { "09:00-18:00/540" }, Show(empty));
        Assert.Equal(540, empty.TotalFreeMinutes);
        Assert.Equal(Show(empty), Show(missing));
    }

    [Fact]
    public void Busy_at_both_edges_leaves_the_middle_with_a_buffer_on_each_side()
    {
        var result = Slots(Busy("09:00", "10:00"), Busy("17:00", "18:00"));

        Assert.Equal(new[] { "10:05-16:55/410" }, Show(result));
        Assert.Equal(410, result.TotalFreeMinutes);
    }

    [Fact]
    public void Busy_in_the_middle_splits_the_day_and_edges_get_no_buffer()
    {
        var result = Slots(Busy("12:00", "13:00"));

        Assert.Equal(new[] { "09:00-11:55/175", "13:05-18:00/295" }, Show(result));
        Assert.Equal(470, result.TotalFreeMinutes);
    }

    [Fact]
    public void Unsorted_and_overlapping_busy_intervals_are_sorted_and_merged()
    {
        var result = Slots(Busy("14:00", "15:00"), Busy("09:30", "10:30"), Busy("10:00", "11:00"), Busy("14:30", "16:00"));

        Assert.Equal(new[] { "09:00-09:25/25", "11:05-13:55/170", "16:05-18:00/115" }, Show(result));
        Assert.Equal(310, result.TotalFreeMinutes);
    }

    [Fact]
    public void Touching_busy_intervals_merge()
    {
        var result = Slots(Busy("11:00", "12:00"), Busy("10:00", "11:00"));

        Assert.Equal(new[] { "09:00-09:55/55", "12:05-18:00/355" }, Show(result));
        Assert.Equal(410, result.TotalFreeMinutes);
    }

    [Theory]
    [InlineData("07:00", "08:30", "09:00-18:00/540")]
    [InlineData("07:00", "09:00", "09:00-18:00/540")]
    [InlineData("17:30", "19:00", "09:00-17:25/505")]
    [InlineData("08:00", "10:00", "10:05-18:00/475")]
    [InlineData("18:00", "20:00", "09:00-18:00/540")]
    public void Busy_intervals_are_clipped_to_the_work_window(string start, string end, string expected)
    {
        Assert.Equal(new[] { expected }, Show(Slots(Busy(start, end))));
    }

    [Theory]
    [InlineData("09:00", "18:00")]
    [InlineData("08:00", "19:00")]
    public void Busy_covering_the_whole_window_leaves_nothing(string start, string end)
    {
        var result = Slots(Busy(start, end));

        Assert.Empty(result.Slots);
        Assert.Equal(0, result.TotalFreeMinutes);
    }

    [Theory]
    [InlineData(0, "09:00-12:00/180,13:00-18:00/300")]
    [InlineData(5, "09:00-11:55/175,13:05-18:00/295")]
    [InlineData(60, "09:00-11:00/120,14:00-18:00/240")]
    public void Buffer_is_applied_next_to_busy_intervals(int buffer, string expected)
    {
        Assert.Equal(expected.Split(','), Show(SlotsWith(5, buffer, Busy("12:00", "13:00"))));
    }

    [Fact]
    public void A_buffer_can_swallow_a_gap_completely()
    {
        var result = SlotsWith(5, 30, Busy("09:30", "17:30"));

        Assert.Empty(result.Slots);
        Assert.Equal(0, result.TotalFreeMinutes);
    }

    [Theory]
    [InlineData(0, "09:00-09:35/35,12:05-12:25/20")]
    [InlineData(5, "09:00-09:35/35,12:05-12:25/20")]
    [InlineData(20, "09:00-09:35/35,12:05-12:25/20")]
    [InlineData(21, "09:00-09:35/35")]
    [InlineData(35, "09:00-09:35/35")]
    [InlineData(36, "")]
    public void Slots_shorter_than_minMinutes_are_dropped_and_exactly_minMinutes_is_kept(int min, string expected)
    {
        var result = SlotsWith(min, 5, Busy("09:40", "12:00"), Busy("12:30", "18:00"));

        Assert.Equal(expected.Length == 0 ? Array.Empty<string>() : expected.Split(','), Show(result));
        Assert.Equal(result.Slots.Sum(slot => slot.Minutes), result.TotalFreeMinutes);
    }

    [Fact]
    public void One_digit_hours_are_accepted_and_output_is_always_HH_mm()
    {
        var result = FreeSlotFinder.Find("9:00", "18:00", [Busy("9:30", "10:00")], 5, 5).Value!;

        Assert.Equal(new[] { "09:00-09:25/25", "10:05-18:00/475" }, Show(result));
    }

    [Fact]
    public void Works_for_other_work_windows()
    {
        var result = FreeSlotFinder.Find("13:00", "17:30", [Busy("12:00", "14:00"), Busy("16:00", "17:00")], 5, 10).Value!;

        Assert.Equal(new[] { "14:10-15:50/100", "17:10-17:30/20" }, Show(result));
    }

    [Fact]
    public void Limits_50_busy_intervals_are_accepted_and_51_are_rejected()
    {
        TimeRange[] Many(int count) => Enumerable.Range(0, count)
            .Select(i => Busy(TimeUtil.Format(i * 15), TimeUtil.Format(i * 15 + 10)))
            .ToArray();

        var accepted = FreeSlotFinder.Find("00:00", "23:59", Many(50), 5, 5);
        var rejected = FreeSlotFinder.Find("00:00", "23:59", Many(51), 5, 5);

        Assert.True(accepted.IsOk);
        Assert.False(rejected.IsOk);
        Assert.Contains("50", rejected.Error);
    }

    [Theory]
    [InlineData("9am", "18:00", 5, 5)]
    [InlineData("09:00", "", 5, 5)]
    [InlineData(null, "18:00", 5, 5)]
    [InlineData("09:00", "24:00", 5, 5)]
    [InlineData("18:00", "09:00", 5, 5)]
    [InlineData("09:00", "09:00", 5, 5)]
    [InlineData("09:00", "18:00", -1, 5)]
    [InlineData("09:00", "18:00", 5, -1)]
    [InlineData("09:00", "18:00", 1441, 5)]
    [InlineData("09:00", "18:00", 5, 1441)]
    public void Bad_window_or_parameters_are_reported_as_a_korean_error(string? workStart, string? workEnd, int min, int buffer)
    {
        var outcome = FreeSlotFinder.Find(workStart, workEnd, [], min, buffer);

        AssertFailed(outcome);
    }

    [Theory]
    [InlineData("10:00", "09:00")]
    [InlineData("10:00", "10:00")]
    [InlineData("10:00", "abc")]
    [InlineData("", "10:00")]
    [InlineData(null, "10:00")]
    public void Bad_busy_intervals_are_reported_as_a_korean_error(string? start, string? end)
    {
        AssertFailed(FreeSlotFinder.Find("09:00", "18:00", [Busy(start, end)], 5, 5));
    }

    [Fact]
    public void A_null_busy_item_is_reported_as_a_korean_error()
    {
        AssertFailed(FreeSlotFinder.Find("09:00", "18:00", [null], 5, 5));
    }

    [Fact]
    public void The_callers_busy_list_is_not_reordered()
    {
        var late = Busy("15:00", "16:00");
        var early = Busy("10:00", "11:00");
        var list = new List<TimeRange?> { late, early };

        FreeSlotFinder.Find("09:00", "18:00", list, 5, 5);

        Assert.Same(late, list[0]);
        Assert.Same(early, list[1]);
    }

    [Fact]
    public void Result_serializes_to_the_contract_field_names()
    {
        var json = JsonSerializer.SerializeToElement(Slots(Busy("12:00", "13:00")), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(new[] { "slots", "totalFreeMinutes" }, json.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "start", "end", "minutes" }, json.GetProperty("slots")[0].EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Random_busy_sets_match_a_minute_by_minute_oracle_whatever_their_order()
    {
        var random = new Random(20261105);
        for (var round = 0; round < 300; round++)
        {
            var busy = Enumerable.Range(0, random.Next(0, 9)).Select(_ =>
            {
                var start = random.Next(7 * 60, 19 * 60);
                return (Start: start, End: Math.Min(start + random.Next(1, 240), 23 * 60 + 59));
            }).ToList();
            var min = random.Next(0, 61);
            var buffer = random.Next(0, 21);

            var ranges = busy.Select(b => Busy(TimeUtil.Format(b.Start), TimeUtil.Format(b.End))).ToArray();
            var result = FreeSlotFinder.Find("09:00", "18:00", ranges, min, buffer).Value!;
            var shuffled = FreeSlotFinder.Find("09:00", "18:00", ranges.OrderBy(_ => random.Next()).ToArray(), min, buffer).Value!;

            Assert.Equal(Oracle(busy, min, buffer), Show(result));
            Assert.Equal(Show(result), Show(shuffled));
            Assert.Equal(result.Slots.Sum(slot => slot.Minutes), result.TotalFreeMinutes);
        }
    }

    // A minute is usable when it lies in the window and, for every (clipped) busy interval, at least `buffer` minutes away from it.
    private static string[] Oracle(List<(int Start, int End)> busy, int min, int buffer)
    {
        const int windowStart = 9 * 60, windowEnd = 18 * 60;
        var clipped = busy
            .Select(b => (Start: Math.Max(b.Start, windowStart), End: Math.Min(b.End, windowEnd)))
            .Where(b => b.Start < b.End)
            .ToList();
        bool Usable(int minute) => clipped.All(b => minute >= b.End + buffer || minute + 1 <= b.Start - buffer);

        var runs = new List<string>();
        int? runStart = null;
        for (var minute = windowStart; minute <= windowEnd; minute++)
        {
            if (minute < windowEnd && Usable(minute))
            {
                runStart ??= minute;
            }
            else if (runStart is { } start)
            {
                if (minute - start >= min)
                {
                    runs.Add($"{TimeUtil.Format(start)}-{TimeUtil.Format(minute)}/{minute - start}");
                }

                runStart = null;
            }
        }

        return runs.ToArray();
    }

    private static void AssertFailed(Outcome<FreeSlotsResult> outcome)
    {
        Assert.False(outcome.IsOk);
        Assert.Null(outcome.Value);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Error));
        Assert.Matches("[가-힣]", outcome.Error);
    }
}
