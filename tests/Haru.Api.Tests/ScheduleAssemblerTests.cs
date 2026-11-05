using Haru.Api.Contracts;
using Haru.Api.Workflow;

namespace Haru.Api.Tests;

public class ScheduleAssemblerTests
{
    private static readonly TaskItem[] Tasks =
    [
        new("t1", "보고서", 60, 1, null, null, null, "task", null),
        new("t2", "메일", 30, 2, null, null, null, "task", null),
        new("t3", "회의", 60, 1, null, "14:00", "15:00", "fixed", null),
    ];

    private static readonly Block[] FixedBlocks =
    [
        new("f1", "t3", "회의", "14:00", "15:00", "fixed", Normalizer.FixedReason),
        new("lunch", null, "점심", "12:00", "13:00", "break", Normalizer.LunchReason),
    ];

    private static PlanContext Ctx() => new()
    {
        Day = "2026-11-05", WorkStart = "09:00", WorkEnd = "18:00", Tasks = Tasks, FixedBlocks = FixedBlocks,
    };

    private static SchedulerWireBlock Placed(string? taskId, string? start, string? kind = "task", string? reason = null, string? end = null) =>
        new(null, taskId, null, start, end, kind, reason);

    private static SchedulerWireDeferred Later(string? taskId, string? reason = null) => new(taskId, null, reason);

    private static (IReadOnlyList<Block> Blocks, IReadOnlyList<Deferred> Deferred) Assemble(
        List<SchedulerWireBlock>? blocks = null, List<SchedulerWireDeferred>? deferred = null) =>
        ScheduleAssembler.Assemble(Ctx(), new SchedulerWire(blocks, deferred));

    private static IEnumerable<Block> TaskBlocks(IEnumerable<Block> blocks) => blocks.Where(b => b.Kind == "task");

    [Fact]
    public void Assemble_ValidBlocks_UseTheTaskLengthAndIgnoreTheModelEnd()
    {
        var (blocks, deferred) = Assemble([Placed("t1", "09:00", end: "09:10"), Placed("t2", "10:00", end: "23:00")]);

        Assert.Equal(new[] { ("b1", "t1", "09:00", "10:00"), ("b2", "t2", "10:00", "10:30") },
            TaskBlocks(blocks).Select(b => (b.Id, b.TaskId!, b.Start, b.End)));
        Assert.Equal(new[] { "보고서", "메일" }, TaskBlocks(blocks).Select(b => b.Title));
        Assert.Empty(deferred);
    }

    [Fact]
    public void Assemble_FixedBlocks_AreAlwaysKeptAndNumberedInTimeOrder()
    {
        var (blocks, _) = Assemble([Placed("t1", "09:00"), Placed("t2", "15:30")]);

        Assert.Equal(new[] { ("b1", "09:00"), ("b2", "12:00"), ("b3", "14:00"), ("b4", "15:30") },
            blocks.Select(b => (b.Id, b.Start)));
        Assert.Equal(new[] { "task", "break", "fixed", "task" }, blocks.Select(b => b.Kind));
    }

    [Fact]
    public void Assemble_BlockWithoutReason_GetsTheDefaultReason()
    {
        var (blocks, _) = Assemble([Placed("t1", "09:00"), Placed("t2", "10:00", reason: "  \u200B ")]);

        Assert.All(TaskBlocks(blocks), b => Assert.Equal(ScheduleAssembler.DefaultReason, b.Reason));
    }

    [Fact]
    public void Assemble_LongReasonWithHiddenCharacters_IsCleanedAndCut()
    {
        var (blocks, _) = Assemble([Placed("t1", "09:00", reason: "\u202E집중\u200B" + new string('가', 200))]);

        var reason = Assert.Single(TaskBlocks(blocks)).Reason;
        Assert.Equal(120, reason.Length);
        Assert.StartsWith("집중가", reason);
    }

    [Fact]
    public void Assemble_UnknownDuplicateNullAndFixedTaskIds_AreIgnored()
    {
        var (blocks, deferred) = Assemble(
        [
            Placed("t9", "10:00"), Placed(null, "10:00"), Placed("t3", "10:00"), Placed("t1", "09:00"), Placed("t1", "11:00"),
        ]);

        var block = Assert.Single(TaskBlocks(blocks));
        Assert.Equal(("t1", "09:00"), (block.TaskId, block.Start));
        Assert.Equal("t2", Assert.Single(deferred).TaskId);
        Assert.Equal(3, blocks.Count); // the two fixed blocks plus the one task block
    }

    [Theory]
    [InlineData("break")]
    [InlineData("fixed")]
    [InlineData("export_ics")]
    [InlineData("")]
    public void Assemble_BlockOfAnotherKind_IsIgnoredAndTheTaskIsDeferred(string kind)
    {
        var (blocks, deferred) = Assemble([Placed("t1", "09:00", kind), Placed("t2", "10:00")]);

        Assert.Equal("t2", Assert.Single(TaskBlocks(blocks)).TaskId);
        Assert.Equal("t1", Assert.Single(deferred).TaskId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("task")]
    [InlineData("TASK")]
    [InlineData("Task")]
    public void Assemble_TaskKindInAnyCaseOrMissing_IsAccepted(string? kind)
    {
        var (blocks, deferred) = Assemble([Placed("t1", "09:00", kind)]);

        Assert.Equal("t1", Assert.Single(TaskBlocks(blocks)).TaskId);
        Assert.Equal("t2", Assert.Single(deferred).TaskId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("9:00")]
    [InlineData("25:00")]
    [InlineData("09:60")]
    [InlineData("09:00:00")]
    [InlineData("아침")]
    public void Assemble_StartThatIsNotAClockTime_DefersTheTask(string? start)
    {
        var (blocks, deferred) = Assemble([Placed("t1", start), Placed("t2", start)]);

        Assert.Empty(TaskBlocks(blocks));
        Assert.Equal(new[] { "t1", "t2" }, deferred.Select(d => d.TaskId).Order());
        Assert.All(deferred, d => Assert.Equal(ScheduleAssembler.DefaultDeferred, d.Reason));
    }

    [Fact]
    public void Assemble_TaskThatWouldRunPastMidnight_IsDeferred()
    {
        var (blocks, deferred) = Assemble([Placed("t1", "23:30")]);

        Assert.Empty(TaskBlocks(blocks));
        Assert.Contains(deferred, d => d.TaskId == "t1");
    }

    [Fact]
    public void Assemble_TaskEndingAt2359_IsPlaced()
    {
        var (blocks, _) = Assemble([Placed("t2", "23:29")]);

        var block = Assert.Single(TaskBlocks(blocks));
        Assert.Equal(("23:29", "23:59"), (block.Start, block.End));
    }

    [Fact]
    public void Assemble_StartOutsideTheWorkWindow_IsKeptSoTheValidatorCanFlagIt()
    {
        var (blocks, deferred) = Assemble([Placed("t1", "03:00")]);

        var block = Assert.Single(TaskBlocks(blocks));
        Assert.Equal(("03:00", "04:00"), (block.Start, block.End));
        Assert.DoesNotContain(deferred, d => d.TaskId == "t1");
    }

    [Fact]
    public void Assemble_DeferredList_KeepsModelReasonsOfUnplacedKnownTasks()
    {
        var (_, deferred) = Assemble(
            [Placed("t1", "09:00")],
            [Later("t2", "  내일 오전이 더 맞아요.  "), Later("t1", "이미 넣은 일"), Later("t9", "없는 일"), Later(null, "id 없음"), Later("t2", "두 번째 이유")]);

        var item = Assert.Single(deferred);
        Assert.Equal(("t2", "메일", "내일 오전이 더 맞아요."), (item.TaskId, item.Title, item.Reason));
    }

    [Fact]
    public void Assemble_DeferredWithoutReason_GetsTheDefaultReason()
    {
        var (_, deferred) = Assemble([Placed("t1", "09:00")], [Later("t2", " ")]);

        Assert.Equal(ScheduleAssembler.DefaultDeferred, Assert.Single(deferred).Reason);
    }

    [Fact]
    public void Assemble_DeferredReasonTooLong_IsCut()
    {
        var (_, deferred) = Assemble([Placed("t1", "09:00")], [Later("t2", new string('나', 500))]);

        Assert.Equal(120, Assert.Single(deferred).Reason.Length);
    }

    [Fact]
    public void Assemble_TaskNeitherPlacedNorDeferred_IsAppendedAsDeferred()
    {
        var (_, deferred) = Assemble([Placed("t1", "09:00")]);

        var item = Assert.Single(deferred);
        Assert.Equal(("t2", "메일", ScheduleAssembler.DefaultDeferred), (item.TaskId, item.Title, item.Reason));
    }

    [Fact]
    public void Assemble_NullLists_DeferEveryPlannableTaskAndKeepOnlyTheFixedBlocks()
    {
        var (blocks, deferred) = Assemble();

        Assert.Equal(new[] { ("b1", "break"), ("b2", "fixed") }, blocks.Select(b => (b.Id, b.Kind)));
        Assert.Equal(new[] { "t1", "t2" }, deferred.Select(d => d.TaskId).Order());
    }

    [Fact]
    public void Number_UnsortedBlocks_AreSortedByStartThenEndAndNumberedFromB1()
    {
        var input = new[]
        {
            new Block("x", "t1", "A", "10:00", "11:00", "task", "r"),
            new Block("x", "t2", "B", "09:00", "09:30", "task", "r"),
            new Block("x", "t3", "C", "10:00", "10:30", "task", "r"),
        };

        var result = ScheduleAssembler.Number(input);

        Assert.Equal(new[] { ("b1", "B"), ("b2", "C"), ("b3", "A") }, result.Select(b => (b.Id, b.Title)));
    }

    [Fact]
    public void Number_NoBlocks_ReturnsAnEmptyList() => Assert.Empty(ScheduleAssembler.Number([]));
}
