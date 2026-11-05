using Haru.Api.Contracts;
using Haru.Api.Infrastructure;
using Haru.Api.Workflow;

namespace Haru.Api.Tests;

public class NormalizerTests
{
    private const string Day = "2026-11-05";

    private static PlanContext Ctx(IEnumerable<IntakeWireItem> items, string workStart = "09:00", string workEnd = "18:00",
        string lunchStart = "12:00", string lunchEnd = "13:00") => new()
    {
        Day = Day, WorkStart = workStart, WorkEnd = workEnd, LunchStart = lunchStart, LunchEnd = lunchEnd, Items = items.ToList(),
    };

    private static IntakeWireItem Item(string? title, double? minutes = null, string? deadline = null,
        string? start = null, string? end = null, bool? fixedItem = null, string? note = null) =>
        new(title, minutes, deadline, start, end, fixedItem, note);

    private static ResolvedDate At(string? date, string? time = null) => new(true, date, time);

    private static Dictionary<string, ResolvedDate> Resolved(params (string Text, ResolvedDate Value)[] entries) =>
        entries.ToDictionary(e => Normalizer.Key(e.Text), e => e.Value);

    private static NormalizeResult Build(PlanContext c, params (string Text, ResolvedDate Value)[] resolved) =>
        Normalizer.Build(c, Resolved(resolved));

    private static NormalizeResult BuildFixed(string start, string? end, double? minutes = null,
        string workStart = "09:00", string workEnd = "18:00", string? startDate = Day)
    {
        var item = Item("회의", minutes, start: "시작", end: end is null ? null : "끝", fixedItem: true);
        var entries = new List<(string, ResolvedDate)> { ("시작", At(startDate, start)) };
        if (end is not null) entries.Add(("끝", At(startDate, end)));
        return Build(Ctx([item], workStart, workEnd), entries.ToArray());
    }

    // plain tasks

    [Fact]
    public void Build_PlainItems_GetSequentialIdsDefaultPriorityAndUnknownMinutes()
    {
        var result = Build(Ctx([Item("보고서"), Item("메일")]));

        Assert.Equal(new[] { "t1", "t2" }, result.Tasks.Select(t => t.Id));
        Assert.All(result.Tasks, t => Assert.Equal(("task", 2, 0, null), (t.Kind, t.Priority, t.Minutes, t.Deadline)));
        Assert.Null(result.Tasks[0].Note);
        Assert.Empty(result.PreDeferred);
    }

    [Fact]
    public void Build_BlankOrHiddenTitles_AreSkippedWithoutGapsInIds()
    {
        var result = Build(Ctx([Item(null), Item("  "), Item("\u200B\u202E"), Item("A"), Item("B")]));

        Assert.Equal(new[] { ("t1", "A"), ("t2", "B") }, result.Tasks.Select(t => (t.Id, t.Title)));
    }

    [Fact]
    public void Build_LongTitleAndNote_AreCut()
    {
        var result = Build(Ctx([Item(new string('가', 200), note: new string('나', 300)), Item("B", note: " \u200B ")]));

        Assert.Equal(80, result.Tasks[0].Title.Length);
        Assert.Equal(120, result.Tasks[0].Note!.Length);
        Assert.Null(result.Tasks[1].Note);
    }

    [Fact]
    public void Build_MoreThan30Items_KeepsTheFirst30()
    {
        var result = Build(Ctx(Enumerable.Range(1, 35).Select(i => Item($"할 일 {i}"))));

        Assert.Equal(30, result.Tasks.Count);
        Assert.Equal("할 일 30", result.Tasks[^1].Title);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2.4, 5)]
    [InlineData(12, 10)]
    [InlineData(12.5, 15)]
    [InlineData(47, 45)]
    [InlineData(240, 240)]
    [InlineData(500, 240)]
    [InlineData(99_999, 240)]
    public void Build_MinutesInRange_AreRoundedToFiveAndClamped(double minutes, int expected) =>
        Assert.Equal(expected, Build(Ctx([Item("A", minutes)])).Tasks[0].Minutes);

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(100_000)]
    [InlineData(double.NaN)]
    public void Build_MinutesOutOfRange_AreTreatedAsUnknown(double minutes) =>
        Assert.Equal(0, Build(Ctx([Item("A", minutes)])).Tasks[0].Minutes);

    [Fact]
    public void Build_DeadlineText_BecomesResolvedKstTimestamp()
    {
        var result = Build(Ctx([Item("A", deadline: "오늘 17시까지"), Item("B", deadline: "모름")]),
            ("오늘 17시까지", At(Day, "17:00")));

        Assert.Equal("2026-11-05T17:00:00+09:00", result.Tasks[0].Deadline);
        Assert.Null(result.Tasks[1].Deadline);
    }

    [Fact]
    public void Build_ResolvedEntryWithHiddenCharactersInText_StillMatches()
    {
        var result = Build(Ctx([Item("A", deadline: "오늘\u200B  17시까지")]), ("오늘 17시까지", At(Day, "17:00")));

        Assert.Equal("2026-11-05T17:00:00+09:00", result.Tasks[0].Deadline);
    }

    // fixed items

    [Fact]
    public void Build_FixedItem_BecomesFixedTaskAndBlock()
    {
        var result = BuildFixed("14:00", "15:00");

        var task = Assert.Single(result.Tasks);
        Assert.Equal(("fixed", 1, 60, "14:00", "15:00", null), (task.Kind, task.Priority, task.Minutes, task.FixedStart, task.FixedEnd, task.Deadline));
        var block = result.FixedBlocks.First();
        Assert.Equal(("f1", "t1", "회의", "14:00", "15:00", "fixed", Normalizer.FixedReason),
            (block.Id, block.TaskId, block.Title, block.Start, block.End, block.Kind, block.Reason));
    }

    [Theory]
    [InlineData(null, "15:00")]
    [InlineData(45.0, "14:45")]
    public void Build_FixedItemWithoutEnd_UsesItsMinutesOrOneHour(double? minutes, string expectedEnd) =>
        Assert.Equal(expectedEnd, BuildFixed("14:00", null, minutes).Tasks[0].FixedEnd);

    [Fact]
    public void Build_FixedItemEndingBeforeItStarts_FallsBackToOneHour() =>
        Assert.Equal("15:00", BuildFixed("14:00", "13:00").Tasks[0].FixedEnd);

    [Fact]
    public void Build_FixedItemEndingOnAnotherDate_FallsBackToOneHour()
    {
        var item = Item("회의", start: "시작", end: "끝", fixedItem: true);

        var result = Build(Ctx([item]), ("시작", At(Day, "14:00")), ("끝", At("2026-11-06", "15:00")));

        Assert.Equal("15:00", result.Tasks[0].FixedEnd);
    }

    [Fact]
    public void Build_FixedItemWithoutResolvedStart_IsAPlainTask()
    {
        var result = Build(Ctx([Item("회의", start: "모레쯤", fixedItem: true)]));

        Assert.Equal(("task", 2, null, null), (result.Tasks[0].Kind, result.Tasks[0].Priority, result.Tasks[0].FixedStart, result.Tasks[0].FixedEnd));
        Assert.DoesNotContain(result.FixedBlocks, b => b.Kind == "fixed");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void Build_ItemNotMarkedFixed_IsAPlainTaskEvenWithAStartTime(bool? fixedFlag)
    {
        var result = Build(Ctx([Item("회의", start: "시작", fixedItem: fixedFlag)]), ("시작", At(Day, "14:00")));

        Assert.Equal("task", result.Tasks[0].Kind);
        Assert.DoesNotContain(result.FixedBlocks, b => b.Kind == "fixed");
    }

    [Fact]
    public void Build_FixedItemOnAnotherDay_IsDeferredAndNotScheduled()
    {
        var result = BuildFixed("10:00", "11:00", startDate: "2026-11-06");

        var deferred = Assert.Single(result.PreDeferred);
        Assert.Equal(("t1", "다른 날 일정이라 오늘 시간표에는 넣지 않습니다."), (deferred.TaskId, deferred.Reason));
        Assert.DoesNotContain(result.FixedBlocks, b => b.Kind == "fixed");
        Assert.Equal("fixed", result.Tasks[0].Kind);
    }

    [Theory]
    [InlineData("19:00", "20:00")]
    [InlineData("07:00", "08:00")]
    [InlineData("08:00", "09:00")]
    [InlineData("18:00", "19:00")]
    public void Build_FixedItemOutsideTheWorkWindow_IsDeferred(string start, string end)
    {
        var result = BuildFixed(start, end);

        Assert.Equal("근무 시간 밖 일정이라 시간표에는 넣지 않습니다.", Assert.Single(result.PreDeferred).Reason);
        Assert.DoesNotContain(result.FixedBlocks, b => b.Kind == "fixed");
    }

    [Theory]
    [InlineData("08:00", "10:00", "09:00", "10:00")]
    [InlineData("17:00", "19:00", "17:00", "18:00")]
    public void Build_FixedItemPartlyOutsideTheWorkWindow_IsClippedToIt(string start, string end, string blockStart, string blockEnd)
    {
        var result = BuildFixed(start, end);

        var block = result.FixedBlocks.Single(b => b.Kind == "fixed");
        Assert.Equal((blockStart, blockEnd), (block.Start, block.End));
        Assert.Equal((start, end), (result.Tasks[0].FixedStart, result.Tasks[0].FixedEnd));
    }

    [Fact]
    public void Build_FixedItemRunningPastMidnight_EndsAt2359() =>
        Assert.Equal("23:59", BuildFixed("23:30", null, workEnd: "23:59").Tasks[0].FixedEnd);

    // lunch

    [Fact]
    public void Build_LunchInsideTheWorkWindow_AddsABreakBlock()
    {
        var lunch = Assert.Single(Build(Ctx([Item("A")])).FixedBlocks);

        Assert.Equal(("lunch", null, "점심", "12:00", "13:00", "break", Normalizer.LunchReason),
            (lunch.Id, lunch.TaskId, lunch.Title, lunch.Start, lunch.End, lunch.Kind, lunch.Reason));
    }

    [Fact]
    public void Build_EmptyLunch_AddsNoBreak() =>
        Assert.Empty(Build(Ctx([Item("A")], lunchStart: "", lunchEnd: "")).FixedBlocks);

    [Theory]
    [InlineData("13:00", "18:00")] // lunch starts before the window
    [InlineData("09:00", "12:30")] // lunch ends after the window
    public void Build_LunchNotInsideTheWorkWindow_IsDropped(string workStart, string workEnd) =>
        Assert.Empty(Build(Ctx([Item("A")], workStart, workEnd)).FixedBlocks);

    [Fact]
    public void Build_LunchOverlappingAFixedBlock_IsDropped()
    {
        var result = BuildFixed("12:30", "13:30");

        Assert.DoesNotContain(result.FixedBlocks, b => b.Kind == "break");
        Assert.Single(result.FixedBlocks);
    }

    [Fact]
    public void Build_LunchTouchingAFixedBlock_IsKeptAfterIt()
    {
        var result = BuildFixed("13:00", "14:00");

        Assert.Equal(new[] { "fixed", "break" }, result.FixedBlocks.Select(b => b.Kind));
    }

    // expressions

    [Fact]
    public void Expressions_CollectsDistinctCleanTextsOfDeadlineStartAndEnd()
    {
        var items = new[]
        {
            Item("A", deadline: "오늘 17시까지", start: "오늘 14시", end: "오늘 15시"),
            Item("B", deadline: "오늘\u200B 17시까지", start: "  ", end: null),
        };

        Assert.Equal(new[] { "오늘 17시까지", "오늘 14시", "오늘 15시" }, Normalizer.Expressions(items));
    }

    [Fact]
    public void Expressions_ItemsBeyondTheFirst30_AreIgnored()
    {
        var items = Enumerable.Range(1, 40).Select(i => Item("A", deadline: $"{i}일"));

        Assert.Equal(30, Normalizer.Expressions(items).Count);
    }

    [Fact]
    public void Key_LongExpression_IsCutAtSixtyCharacters() => Assert.Equal(60, Normalizer.Key(new string('가', 100)).Length);

    // rounding, deadlines

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-5, 5)]
    [InlineData(2, 5)]
    [InlineData(7, 5)]
    [InlineData(7.5, 10)]
    [InlineData(13, 15)]
    [InlineData(238, 240)]
    [InlineData(241, 240)]
    [InlineData(10_000, 240)]
    public void RoundMinutes_AnyValue_IsAMultipleOfFiveBetween5And240(double value, int expected) =>
        Assert.Equal(expected, Normalizer.RoundMinutes(value));

    [Fact]
    public void Deadline_DateOnly_ReturnsTheDate() => Assert.Equal("2026-11-06", Normalizer.Deadline(At("2026-11-06"), Day));

    [Fact]
    public void Deadline_DateAndTime_ReturnsKstTimestamp() =>
        Assert.Equal("2026-11-06T10:00:00+09:00", Normalizer.Deadline(At("2026-11-06", "10:00"), Day));

    [Fact]
    public void Deadline_TimeOnly_UsesThePlanDay() =>
        Assert.Equal("2026-11-05T10:00:00+09:00", Normalizer.Deadline(At(null, "10:00"), Day));

    [Fact]
    public void Deadline_NothingResolved_IsNull()
    {
        Assert.Null(Normalizer.Deadline(null, Day));
        Assert.Null(Normalizer.Deadline(At(null), Day));
        Assert.Null(Normalizer.Deadline(new ResolvedDate(false, "2026-11-06", "10:00"), Day));
    }

    private static TaskItem WithDeadline(string? deadline) => new("t1", "A", 30, 2, deadline, null, null, "task", null);

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("2026-11", null)]
    [InlineData("2026-11-06T10:00:00+09:00", null)]
    [InlineData("2026-11-06", null)]
    [InlineData("2026-11-05T17:00:00+09:00", "17:00")]
    [InlineData("2026-11-05", "18:00")]
    [InlineData("2026-11-04T10:00:00+09:00", "18:00")]
    public void DeadlineTime_Deadline_LimitsTodayOnlyWhenItIsNotInTheFuture(string? deadline, string? expected) =>
        Assert.Equal(expected, Normalizer.DeadlineTime(WithDeadline(deadline), Day, "18:00"));

    // reading and applying model estimates

    [Fact]
    public void ReadEstimates_KeepsOnlyWantedPositiveRoundedFirstAnswers()
    {
        var wire = new EstimatorWire(
        [
            new("t1", 47, null), new("t1", 90, null), new("t2", 0, null), new("t3", -5, null), new("t9", 30, null),
            new(null, 30, null), new("t4", 100_000, null), new("t5", 12.5, null), new("t6", null, null),
        ]);

        var result = Normalizer.ReadEstimates(wire, new HashSet<string> { "t1", "t2", "t3", "t4", "t5", "t6" });

        Assert.Equal(new Dictionary<string, int> { ["t1"] = 45, ["t5"] = 15 }, result);
    }

    [Fact]
    public void ReadEstimates_NoEstimatesArray_ReturnsNothing() =>
        Assert.Empty(Normalizer.ReadEstimates(new EstimatorWire(null), new HashSet<string> { "t1" }));

    [Fact]
    public void ReadPriorities_KeepsOnlyWantedValuesFromOneToThree()
    {
        var wire = new PrioritizerWire(
        [
            new("t1", 1, null), new("t1", 3, null), new("t2", 3, null), new("t3", 0, null), new("t4", 4, null),
            new("t5", 1.6, null), new("t9", 2, null), new(null, 2, null), new("t6", null, null),
        ]);

        var result = Normalizer.ReadPriorities(wire, new HashSet<string> { "t1", "t2", "t3", "t4", "t5", "t6" });

        Assert.Equal(new Dictionary<string, int> { ["t1"] = 1, ["t2"] = 3, ["t5"] = 2 }, result);
    }

    [Fact]
    public void ReadPriorities_NoPrioritiesArray_ReturnsNothing() =>
        Assert.Empty(Normalizer.ReadPriorities(new PrioritizerWire(null), new HashSet<string> { "t1" }));

    private static readonly TaskItem[] Tasks =
    [
        new("t1", "A", 0, 2, null, null, null, "task", null),
        new("t2", "B", 45, 2, null, null, null, "task", null),
        new("t3", "회의", 60, 1, null, "14:00", "15:00", "fixed", null),
        new("t4", "D", 0, 2, null, null, null, "task", null),
    ];

    [Fact]
    public void Apply_Estimates_FillOnlyUnknownMinutesOfPlainTasks()
    {
        var minutes = new Dictionary<string, int> { ["t1"] = 90, ["t2"] = 15, ["t3"] = 10 };

        var result = Normalizer.Apply(Tasks, minutes, null);

        Assert.Equal(new[] { 90, 45, 60, 30 }, result.Select(t => t.Minutes));
    }

    [Fact]
    public void Apply_Priorities_ChangePlainTasksButNeverFixedOnes()
    {
        var priorities = new Dictionary<string, int> { ["t1"] = 1, ["t3"] = 3, ["t4"] = 3 };

        var result = Normalizer.Apply(Tasks, null, priorities);

        Assert.Equal(new[] { 1, 2, 1, 3 }, result.Select(t => t.Priority));
    }

    [Fact]
    public void Apply_NoEstimatesAtAll_UsesThirtyMinutesAndKeepsPriorities()
    {
        var result = Normalizer.Apply(Tasks, null, null);

        Assert.Equal(new[] { 30, 45, 60, 30 }, result.Select(t => t.Minutes));
        Assert.All(result.Where(t => t.Kind == "task"), t => Assert.Equal(2, t.Priority));
    }
}
