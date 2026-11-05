using System.Text.Json;
using Haru.Mcp.Logic;

namespace Haru.Mcp.Tests;

public class ScheduleValidatorTests
{
    private static ScheduleBlock Block(
        string id, string? start, string? end, string kind = "task", string? title = null, string? deadline = null) =>
        new() { Id = id, Title = title ?? $"일 {id}", Start = start, End = end, Kind = kind, DeadlineTime = deadline };

    private static ValidationReport Check(params ScheduleBlock?[] blocks) => CheckWith(5, 0.85, blocks);

    private static ValidationReport CheckWith(int bufferMinutes, double maxLoad, params ScheduleBlock?[] blocks) =>
        ScheduleValidator.Validate("09:00", "18:00", blocks, bufferMinutes, maxLoad);

    private static string[] Codes(ValidationReport report) => report.Violations.Select(v => v.Code).ToArray();

    private static string[] Summary(ValidationReport report) =>
        report.Violations.Select(v => $"{v.Code}:{string.Join(",", v.BlockIds)}").ToArray();

    // The sample plan of docs/api-contract.md section 4 (day 2026-11-05, 09:00-18:00).
    private static ScheduleBlock[] SampleBlocks() =>
    [
        Block("b1", "09:00", "09:30", title: "메일 답장 정리"),
        Block("b2", "09:35", "11:35", title: "제안서 초안 작성", deadline: "17:00"),
        Block("b3", "12:00", "13:00", "break", "점심"),
        Block("b4", "13:05", "13:50", title: "내일 발표 슬라이드 다듬기"),
        Block("b5", "14:00", "15:00", "fixed", "팀 주간 회의"),
        Block("b6", "15:05", "15:50", title: "주간 보고서 구조 잡기"),
        Block("b7", "16:00", "16:30", title: "운동"),
    ];

    // A one-hour break leaves 480 available minutes, so the ratio is occupiedMinutes / 480.
    private static ValidationReport WithOccupied(int occupiedMinutes) => Check(
        Block("a", "09:00", "12:00"),
        Block("lunch", "12:00", "13:00", "break", "점심"),
        Block("b", "13:05", TimeUtil.Format(13 * 60 + 5 + occupiedMinutes - 180)));

    [Fact]
    public void Sample_plan_blocks_are_valid_with_load_ratio_0_69()
    {
        var report = Check(SampleBlocks());

        Assert.True(report.Valid);
        Assert.Equal(0.69, report.LoadRatio);
        Assert.Equal(330, report.OccupiedMinutes);
        Assert.Equal(480, report.AvailableMinutes);
        Assert.Empty(report.Violations);
    }

    [Fact]
    public void Sample_plan_file_from_Haru_Api_is_valid_and_matches_its_own_validation_block()
    {
        var sample = SamplePlan.Load();

        var report = ScheduleValidator.Validate(sample.WorkStart, sample.WorkEnd, sample.Blocks, 5, 0.85);

        Assert.Equal(7, sample.Blocks.Count);
        Assert.True(report.Valid);
        Assert.Equal(0.69, report.LoadRatio);
        Assert.Equal(sample.Valid, report.Valid);
        Assert.Equal(sample.LoadRatio, report.LoadRatio);
        Assert.Equal(sample.OccupiedMinutes, report.OccupiedMinutes);
        Assert.Equal(sample.AvailableMinutes, report.AvailableMinutes);
        Assert.Empty(report.Violations);
    }

    [Fact]
    public void Adding_the_90_minute_seminar_task_overloads_the_day()
    {
        var report = Check(SampleBlocks().Append(Block("b8", "16:30", "18:00", title: "다음 주 세미나 자료 조사")).ToArray());

        Assert.False(report.Valid);
        Assert.Equal(420, report.OccupiedMinutes);
        Assert.Equal(480, report.AvailableMinutes);
        Assert.Equal(0.88, report.LoadRatio); // 420 / 480 = 0.875, rounded half away from zero to 2 decimals
        Assert.Equal(new[] { "NO_BUFFER", "OVERLOAD" }, Codes(report));
    }

    [Fact]
    public void Seminar_task_that_keeps_the_buffer_gives_OVERLOAD_alone()
    {
        var report = Check(SampleBlocks().Append(Block("b8", "16:35", "18:00", title: "다음 주 세미나 자료 조사")).ToArray());

        Assert.Equal(415, report.OccupiedMinutes);
        Assert.Equal(0.86, report.LoadRatio);
        var violation = Assert.Single(report.Violations);
        Assert.Equal("OVERLOAD", violation.Code);
        Assert.Empty(violation.BlockIds);
        Assert.Contains("0.86", violation.Message);
        Assert.Contains("0.85", violation.Message);
    }

    [Theory]
    [InlineData("10:00", "09:00")]
    [InlineData("10:00", "10:00")]
    [InlineData("9am", "10:00")]
    [InlineData("09:00", "25:00")]
    [InlineData("09:00", "09:60")]
    [InlineData("09:00", "10:00:00")]
    [InlineData("0900", "1000")]
    [InlineData("", "10:00")]
    [InlineData(null, "10:00")]
    [InlineData("09:00", null)]
    public void INVALID_RANGE_for_malformed_empty_or_reversed_times(string? start, string? end)
    {
        var report = Check(Block("x", start, end, title: "문제 블록"));

        Assert.False(report.Valid);
        var violation = Assert.Single(report.Violations);
        Assert.Equal("INVALID_RANGE", violation.Code);
        Assert.Equal(new[] { "x" }, violation.BlockIds);
        Assert.Contains("문제 블록", violation.Message);
        Assert.Equal(0, report.OccupiedMinutes);
    }

    [Theory]
    [InlineData("meeting")]
    [InlineData("")]
    [InlineData(null)]
    public void INVALID_RANGE_for_an_unknown_kind(string? kind)
    {
        var report = Check(new ScheduleBlock { Id = "x", Title = "일", Start = "10:00", End = "11:00", Kind = kind });

        Assert.Equal(new[] { "INVALID_RANGE:x" }, Summary(report));
    }

    [Fact]
    public void INVALID_RANGE_for_a_bad_deadline_a_missing_id_and_a_null_block()
    {
        var report = Check(
            Block("d", "10:00", "11:00", deadline: "soon"),
            new ScheduleBlock { Id = " ", Title = "이름 없음", Start = "12:00", End = "13:00", Kind = "task" },
            null);

        Assert.Equal(new[] { "INVALID_RANGE:d", "INVALID_RANGE:", "INVALID_RANGE:" }, Summary(report));
        Assert.False(report.Valid);
    }

    [Fact]
    public void Invalid_blocks_are_left_out_of_every_other_check()
    {
        var report = Check(Block("ok", "10:00", "11:00"), Block("bad", "10:30", "10:00"));

        Assert.Equal(new[] { "INVALID_RANGE:bad" }, Summary(report));
        Assert.Equal(60, report.OccupiedMinutes);
    }

    [Theory]
    [InlineData("08:00", "09:30", "task")]
    [InlineData("17:30", "18:30", "task")]
    [InlineData("06:00", "07:00", "task")]
    [InlineData("17:30", "18:30", "break")]
    public void OUT_OF_HOURS_for_task_and_break_blocks_outside_the_window(string start, string end, string kind)
    {
        var report = Check(Block("x", start, end, kind, "야근"));

        var violation = Assert.Single(report.Violations);
        Assert.Equal("OUT_OF_HOURS", violation.Code);
        Assert.Equal(new[] { "x" }, violation.BlockIds);
        Assert.Contains("야근", violation.Message);
        Assert.Contains($"{start}~{end}", violation.Message);
        Assert.Contains("09:00~18:00", violation.Message);
    }

    [Theory]
    [InlineData("09:00", "10:00")]
    [InlineData("17:00", "18:00")]
    public void Blocks_touching_the_window_edges_are_inside_it(string start, string end)
    {
        Assert.True(Check(Block("x", start, end)).Valid);
    }

    [Theory]
    [InlineData("07:00", "08:00", 60)]
    [InlineData("17:30", "19:00", 90)]
    [InlineData("20:00", "21:00", 60)]
    public void Fixed_blocks_outside_the_window_are_not_flagged_but_still_count_as_load(string start, string end, int minutes)
    {
        var report = Check(Block("f", start, end, "fixed", "외부 행사"));

        Assert.True(report.Valid);
        Assert.Equal(minutes, report.OccupiedMinutes);
    }

    [Fact]
    public void OUT_OF_HOURS_uses_the_window_that_was_passed_in()
    {
        var blocks = new[] { Block("a", "09:30", "10:30"), Block("b", "15:30", "16:30"), Block("c", "11:00", "12:00") };

        var report = ScheduleValidator.Validate("10:00", "16:00", blocks, 5, 0.85);

        Assert.Equal(new[] { "OUT_OF_HOURS:a", "OUT_OF_HOURS:b" }, Summary(report));
    }

    [Fact]
    public void OVERLAP_names_both_blocks_with_titles_and_times()
    {
        var report = Check(Block("a", "10:00", "11:00", title: "기획서"), Block("b", "10:30", "11:30", title: "리뷰"));

        var violation = Assert.Single(report.Violations);
        Assert.Equal("OVERLAP", violation.Code);
        Assert.Equal(new[] { "a", "b" }, violation.BlockIds);
        Assert.Contains("기획서", violation.Message);
        Assert.Contains("10:00~11:00", violation.Message);
        Assert.Contains("리뷰", violation.Message);
        Assert.Contains("10:30~11:30", violation.Message);
    }

    [Fact]
    public void OVERLAP_also_covers_nested_blocks_and_a_break_inside_a_task()
    {
        var nested = Check(Block("a", "10:00", "12:00"), Block("b", "10:30", "11:00"));
        var withBreak = Check(Block("t", "10:00", "11:00"), Block("r", "10:30", "11:30", "break", "휴식"));

        Assert.Equal(new[] { "OVERLAP:a,b" }, Summary(nested));
        Assert.Equal(new[] { "OVERLAP:t,r" }, Summary(withBreak));
    }

    [Fact]
    public void OVERLAP_is_reported_per_overlapping_pair()
    {
        var report = Check(Block("a", "10:00", "12:00"), Block("b", "10:30", "11:00"), Block("c", "11:30", "12:30"));

        Assert.Equal(new[] { "OVERLAP:a,b", "OVERLAP:a,c" }, Summary(report));
    }

    [Fact]
    public void Touching_blocks_are_not_an_overlap()
    {
        var report = Check(Block("a", "10:00", "11:00"), Block("b", "11:00", "12:00"));

        Assert.Equal(new[] { "NO_BUFFER:a,b" }, Summary(report));
    }

    [Theory]
    [InlineData("10:00", 0, true)]
    [InlineData("10:01", 1, true)]
    [InlineData("10:04", 4, true)]
    [InlineData("10:05", 5, false)]
    [InlineData("10:06", 6, false)]
    public void NO_BUFFER_only_when_the_gap_is_smaller_than_the_buffer(string nextStart, int gap, bool flagged)
    {
        var report = Check(Block("a", "09:00", "10:00", title: "앞 일정"), Block("b", nextStart, "11:00", title: "뒤 일정"));

        if (flagged)
        {
            var violation = Assert.Single(report.Violations);
            Assert.Equal("NO_BUFFER", violation.Code);
            Assert.Equal(new[] { "a", "b" }, violation.BlockIds);
            Assert.Contains("앞 일정", violation.Message);
            Assert.Contains("뒤 일정", violation.Message);
            Assert.Contains($"{gap}분", violation.Message);
            Assert.Contains("5분", violation.Message);
        }
        else
        {
            Assert.True(report.Valid);
        }
    }

    [Fact]
    public void NO_BUFFER_follows_bufferMinutes()
    {
        var adjacent = new[] { Block("a", "09:00", "10:00"), Block("b", "10:00", "11:00") };
        var tenMinutesApart = new[] { Block("a", "09:00", "10:00"), Block("b", "10:10", "11:00") };

        Assert.True(CheckWith(0, 0.85, adjacent).Valid);
        Assert.Equal(new[] { "NO_BUFFER:a,b" }, Summary(CheckWith(15, 0.85, tenMinutesApart)));
        Assert.True(CheckWith(10, 0.85, tenMinutesApart).Valid);
    }

    [Fact]
    public void NO_BUFFER_is_not_checked_next_to_a_break()
    {
        var report = Check(
            Block("a", "09:00", "10:00"),
            Block("r", "10:00", "10:30", "break", "휴식"),
            Block("b", "10:30", "11:00"),
            Block("lunch", "11:00", "12:00", "break", "점심"));

        Assert.True(report.Valid);
        Assert.Equal(90, report.OccupiedMinutes);
        Assert.Equal(450, report.AvailableMinutes);
    }

    [Fact]
    public void NO_BUFFER_compares_start_order_neighbours_only()
    {
        // a and c are only 2 minutes apart, but b (inside a) sits between them in start order.
        var report = Check(Block("a", "09:00", "12:00"), Block("b", "09:30", "10:00"), Block("c", "12:02", "13:00"));

        Assert.Equal(new[] { "OVERLAP:a,b" }, Summary(report));
    }

    [Theory]
    [InlineData(408, 0.85, true)]
    [InlineData(410, 0.85, true)]
    [InlineData(411, 0.86, false)]
    [InlineData(412, 0.86, false)]
    public void OVERLOAD_boundary_around_85_percent(int occupied, double expectedRatio, bool valid)
    {
        var report = WithOccupied(occupied);

        Assert.Equal(occupied, report.OccupiedMinutes);
        Assert.Equal(480, report.AvailableMinutes);
        Assert.Equal(expectedRatio, report.LoadRatio);
        Assert.Equal(valid, report.Valid);
        Assert.Equal(valid ? 0 : 1, report.Violations.Count(v => v.Code == "OVERLOAD"));
    }

    [Fact]
    public void OVERLOAD_follows_maxLoad()
    {
        var wholeDay = new[] { Block("a", "09:00", "18:00") };
        var half = new[] { Block("a", "09:00", "11:30"), Block("b", "11:35", "13:35") };

        Assert.Equal(new[] { "OVERLOAD:" }, Summary(CheckWith(5, 0.85, wholeDay)));
        Assert.True(CheckWith(5, 1.0, wholeDay).Valid);
        Assert.Equal(1.0, CheckWith(5, 1.0, wholeDay).LoadRatio);
        Assert.True(CheckWith(5, 0.5, half).Valid);
        Assert.Equal(new[] { "OVERLOAD:" }, Summary(CheckWith(5, 0.49, half)));
    }

    [Fact]
    public void Break_blocks_shrink_the_available_time_but_are_not_load()
    {
        var report = Check(Block("a", "09:00", "10:00"), Block("r", "12:00", "13:00", "break", "점심"));

        Assert.Equal(60, report.OccupiedMinutes);
        Assert.Equal(480, report.AvailableMinutes);
        Assert.Equal(0.13, report.LoadRatio);
    }

    [Fact]
    public void A_day_that_is_all_break_does_not_divide_by_zero()
    {
        var onlyBreak = Check(Block("r", "09:00", "18:00", "break", "쉬는 날"));
        var plusFixed = Check(Block("r", "09:00", "18:00", "break", "쉬는 날"), Block("f", "10:00", "11:00", "fixed", "약속"));

        Assert.True(onlyBreak.Valid);
        Assert.Equal(0, onlyBreak.LoadRatio);
        Assert.Equal(0, onlyBreak.AvailableMinutes);
        Assert.Contains("OVERLOAD", Codes(plusFixed));
        Assert.Equal(60.0, plusFixed.LoadRatio);
    }

    [Theory]
    [InlineData("10:00", "11:00", "10:30", true)]
    [InlineData("10:00", "11:01", "11:00", true)]
    [InlineData("10:00", "11:00", "11:00", false)]
    [InlineData("10:00", "11:00", "11:01", false)]
    [InlineData("10:00", "11:00", "9:59", true)]
    public void DEADLINE_MISSED_when_a_task_ends_after_its_deadline(string start, string end, string deadline, bool missed)
    {
        var report = Check(Block("t", start, end, "task", "제출", deadline));

        if (missed)
        {
            var violation = Assert.Single(report.Violations);
            Assert.Equal("DEADLINE_MISSED", violation.Code);
            Assert.Equal(new[] { "t" }, violation.BlockIds);
            Assert.Contains("제출", violation.Message);
            Assert.Contains(end, violation.Message);
        }
        else
        {
            Assert.True(report.Valid);
        }
    }

    [Fact]
    public void Deadlines_only_apply_to_task_blocks_and_blank_deadlines_are_ignored()
    {
        var report = Check(
            Block("f", "10:00", "11:00", "fixed", deadline: "10:30"),
            Block("r", "12:00", "13:00", "break", deadline: "12:30"),
            Block("t", "14:00", "15:00", deadline: ""));

        Assert.True(report.Valid);
    }

    [Fact]
    public void Kinds_are_matched_ignoring_case_and_surrounding_spaces()
    {
        Assert.True(Check(Block("a", "10:00", "11:00", " Task ")).Valid);
    }

    [Fact]
    public void Violations_come_in_contract_code_order_then_by_block_start_whatever_the_input_order()
    {
        var k1 = Block("k1", "09:00", "10:00", title: "보고서", deadline: "09:30");
        var k2 = Block("k2", "09:30", "10:30");
        var k3 = Block("k3", "10:32", "12:00");
        var k4 = Block("k4", "12:00", "17:00");
        var k5 = Block("k5", "07:00", "08:00");
        var k6 = Block("k6", "11:00", "10:00");

        var report = Check(k4, k6, k2, k5, k1, k3);

        Assert.Equal(
            new[]
            {
                "INVALID_RANGE:k6",
                "OUT_OF_HOURS:k5",
                "OVERLAP:k1,k2",
                "NO_BUFFER:k2,k3",
                "NO_BUFFER:k3,k4",
                "OVERLOAD:",
                "DEADLINE_MISSED:k1",
            },
            Summary(report));
        Assert.False(report.Valid);
        Assert.Equal(568, report.OccupiedMinutes);
        Assert.Equal(540, report.AvailableMinutes);
        Assert.Equal(1.05, report.LoadRatio);
    }

    [Fact]
    public void Violations_of_one_code_are_ordered_by_block_start()
    {
        var late = Block("late", "17:30", "18:30");
        var early = Block("early", "07:00", "08:00");

        Assert.Equal(new[] { "OUT_OF_HOURS:early", "OUT_OF_HOURS:late" }, Summary(Check(late, early)));
    }

    [Fact]
    public void The_callers_blocks_are_neither_reordered_nor_changed()
    {
        var z = Block("z", "15:00", "16:00");
        var a = Block("a", "09:00", "10:00");
        var list = new List<ScheduleBlock?> { z, null, a };

        ScheduleValidator.Validate("09:00", "18:00", list, 5, 0.85);

        Assert.Equal(3, list.Count);
        Assert.Same(z, list[0]);
        Assert.Null(list[1]);
        Assert.Same(a, list[2]);
        Assert.Equal(("15:00", "16:00"), (z.Start, z.End));
    }

    [Fact]
    public void Messages_are_single_line_korean_sentences()
    {
        var report = Check(
            Block("a", "08:00", "09:30", title: "첫줄\n둘째줄\r\n셋째줄"),
            Block("b", "09:20", "10:00", title: "회의"));

        Assert.Equal(new[] { "OUT_OF_HOURS:a", "OVERLAP:a,b" }, Summary(report));
        foreach (var violation in report.Violations)
        {
            Assert.Matches("[가-힣]", violation.Message);
            Assert.EndsWith(".", violation.Message);
            Assert.DoesNotContain('\n', violation.Message);
            Assert.DoesNotContain('\r', violation.Message);
            Assert.Contains("첫줄 둘째줄 셋째줄", violation.Message);
        }
    }

    [Fact]
    public void Long_titles_are_shortened_inside_messages()
    {
        var title = new string('가', 40);

        var report = Check(Block("a", "07:00", "08:00", title: title));

        var message = Assert.Single(report.Violations).Message;
        Assert.Contains(new string('가', 30) + "…", message);
        Assert.DoesNotContain(new string('가', 31), message);
    }

    [Fact]
    public void Empty_input_is_a_valid_empty_day()
    {
        var report = ScheduleValidator.Validate("09:00", "18:00", null, 5, 0.85);

        Assert.True(report.Valid);
        Assert.Equal(0, report.LoadRatio);
        Assert.Equal(0, report.OccupiedMinutes);
        Assert.Equal(540, report.AvailableMinutes);
        Assert.Empty(report.Violations);
    }

    [Theory]
    [InlineData("25:00", "18:00", 5, 0.85)]
    [InlineData("09:00", "09:00", 5, 0.85)]
    [InlineData("18:00", "09:00", 5, 0.85)]
    [InlineData(null, "18:00", 5, 0.85)]
    [InlineData("09:00", "", 5, 0.85)]
    [InlineData("09:00", "18:00", -1, 0.85)]
    [InlineData("09:00", "18:00", 1441, 0.85)]
    [InlineData("09:00", "18:00", 5, double.NaN)]
    [InlineData("09:00", "18:00", 5, -0.1)]
    [InlineData("09:00", "18:00", 5, 1001.0)]
    [InlineData("09:00", "18:00", 5, double.PositiveInfinity)]
    public void Bad_window_or_parameters_give_one_INVALID_RANGE_without_block_ids(
        string? workStart, string? workEnd, int buffer, double maxLoad)
    {
        var report = ScheduleValidator.Validate(workStart, workEnd, [Block("a", "10:00", "11:00")], buffer, maxLoad);

        Assert.False(report.Valid);
        Assert.Equal(0, report.LoadRatio);
        Assert.Equal(0, report.OccupiedMinutes);
        Assert.Equal(0, report.AvailableMinutes);
        var violation = Assert.Single(report.Violations);
        Assert.Equal("INVALID_RANGE", violation.Code);
        Assert.Empty(violation.BlockIds);
    }

    [Fact]
    public void Limits_50_blocks_are_accepted_and_51_are_rejected()
    {
        // 50 blocks of 10 minutes with 5-minute gaps fit between 00:00 and 23:59.
        ScheduleBlock[] Many(int count) => Enumerable.Range(0, count)
            .Select(i => Block($"b{i}", TimeUtil.Format(i * 15), TimeUtil.Format(i * 15 + 10)))
            .ToArray();

        var accepted = ScheduleValidator.Validate("00:00", "23:59", Many(50), 5, 0.85);
        var rejected = ScheduleValidator.Validate("00:00", "23:59", Many(51), 5, 0.85);

        Assert.True(accepted.Valid);
        Assert.False(rejected.Valid);
        var violation = Assert.Single(rejected.Violations);
        Assert.Equal("INVALID_RANGE", violation.Code);
        Assert.Empty(violation.BlockIds);
        Assert.Contains("50", violation.Message);
    }

    [Fact]
    public void Limits_strings_over_200_characters_are_invalid_and_200_is_fine()
    {
        var tooLong = Check(Block("a", "10:00", "11:00", title: new string('가', 201)));
        var longestAllowed = Check(Block("a", "10:00", "11:00", title: new string('가', 200)));
        var longId = Check(Block(new string('i', 201), "10:00", "11:00"));

        Assert.Equal(new[] { "INVALID_RANGE:a" }, Summary(tooLong));
        Assert.True(tooLong.Violations[0].Message.Length < 100);
        Assert.True(longestAllowed.Valid);
        Assert.Equal(new[] { "INVALID_RANGE:" }, Summary(longId));
    }

    [Fact]
    public void Result_serializes_to_the_contract_field_names()
    {
        var report = Check(SampleBlocks());

        var json = JsonSerializer.SerializeToElement(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(
            new[] { "valid", "loadRatio", "occupiedMinutes", "availableMinutes", "violations" },
            json.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Random_valid_blocks_agree_with_an_independent_oracle()
    {
        var random = new Random(20261105);
        for (var round = 0; round < 300; round++)
        {
            var blocks = new List<(string Id, int Start, int End, string Kind, int? Deadline)>();
            var count = random.Next(0, 9);
            for (var i = 0; i < count; i++)
            {
                var start = random.Next(7 * 60, 19 * 60);
                var end = Math.Min(start + random.Next(1, 180), 23 * 60 + 59);
                var kind = new[] { "task", "task", "fixed", "break" }[random.Next(4)];
                blocks.Add(($"b{i}", start, end, kind, random.Next(3) == 0 ? random.Next(9 * 60, 20 * 60) : null));
            }

            var report = ScheduleValidator.Validate(
                "09:00",
                "18:00",
                blocks.Select(b => Block(b.Id, TimeUtil.Format(b.Start), TimeUtil.Format(b.End), b.Kind,
                    deadline: b.Deadline is { } d ? TimeUtil.Format(d) : null)).ToArray(),
                5,
                0.85);

            var occupied = blocks.Where(b => b.Kind != "break").Sum(b => b.End - b.Start);
            var available = Math.Max(0, 540 - blocks.Where(b => b.Kind == "break").Sum(b => b.End - b.Start));
            Assert.Equal(occupied, report.OccupiedMinutes);
            Assert.Equal(available, report.AvailableMinutes);
            Assert.Equal((double)Math.Round((decimal)occupied / Math.Max(available, 1), 2, MidpointRounding.AwayFromZero), report.LoadRatio);

            var expectedOverlaps = new HashSet<string>(
                from a in blocks
                from b in blocks
                where string.CompareOrdinal(a.Id, b.Id) < 0 && a.Start < b.End && b.Start < a.End
                select $"{a.Id}|{b.Id}");
            var actualOverlaps = report.Violations.Where(v => v.Code == "OVERLAP")
                .Select(v => string.Join("|", v.BlockIds.OrderBy(id => id, StringComparer.Ordinal))).ToHashSet();
            Assert.Equal(expectedOverlaps, actualOverlaps);

            var expectedOutside = blocks.Where(b => b.Kind != "fixed" && (b.Start < 540 || b.End > 1080)).Select(b => b.Id).Order().ToArray();
            var actualOutside = report.Violations.Where(v => v.Code == "OUT_OF_HOURS").SelectMany(v => v.BlockIds).Order().ToArray();
            Assert.Equal(expectedOutside, actualOutside);

            var expectedLate = blocks.Where(b => b.Kind == "task" && b.Deadline is { } d && b.End > d).Select(b => b.Id).Order().ToArray();
            var actualLate = report.Violations.Where(v => v.Code == "DEADLINE_MISSED").SelectMany(v => v.BlockIds).Order().ToArray();
            Assert.Equal(expectedLate, actualLate);

            Assert.Equal(report.Violations.Count == 0, report.Valid);
            Assert.Equal(Codes(report).OrderBy(code => Array.IndexOf(ExpectedOrder, code)), Codes(report));
        }
    }

    private static readonly string[] ExpectedOrder =
        ["INVALID_RANGE", "OUT_OF_HOURS", "OVERLAP", "NO_BUFFER", "OVERLOAD", "DEADLINE_MISSED"];

    [Fact]
    public void Garbage_input_never_throws_and_valid_always_means_no_violations()
    {
        string?[] texts = [null, "", " ", "9:5", "09:00", "10:30", "17:59", "18:00", "23:59", "24:00", "-1:00", "ab:cd", "\r\n", "\0", new string('x', 500), "🙂"];
        string?[] kinds = [null, "", "task", "fixed", "break", "TASK", "nap"];
        var random = new Random(7);
        string? Pick(string?[] pool) => pool[random.Next(pool.Length)];

        for (var round = 0; round < 500; round++)
        {
            var blocks = Enumerable.Range(0, random.Next(0, 9))
                .Select(_ => random.Next(10) == 0
                    ? null
                    : new ScheduleBlock { Id = Pick(texts), Title = Pick(texts), Start = Pick(texts), End = Pick(texts), Kind = Pick(kinds), DeadlineTime = Pick(texts) })
                .ToArray();

            var report = ScheduleValidator.Validate("09:00", "18:00", blocks, 5, 0.85);

            Assert.Equal(report.Violations.Count == 0, report.Valid);
            Assert.All(report.Violations, v =>
            {
                Assert.False(string.IsNullOrWhiteSpace(v.Message));
                Assert.DoesNotContain('\n', v.Message);
                Assert.DoesNotContain('\r', v.Message);
                Assert.True(v.Message.Length < 400);
            });
        }
    }
}
