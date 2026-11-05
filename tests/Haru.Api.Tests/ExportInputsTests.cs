using Haru.Api.Contracts;
using Haru.Api.Endpoints;

namespace Haru.Api.Tests;

public class ExportInputsTests
{
    private static readonly DateOnly Today = new(2026, 11, 5);

    private static Block B(string id = "b1", string title = "회의", string start = "09:00", string end = "10:00",
        string kind = "task", string reason = "") => new(id, null, title, start, end, kind, reason);

    private static ExportRequest Req(Block?[]? blocks = null, bool? confirmed = true, string? day = "2026-11-05",
        string? workStart = null, string? workEnd = null) =>
        new(confirmed, day, (blocks ?? [B()])!, workStart, workEnd);

    private static ApiError Fail(ExportRequest? req, string code)
    {
        var result = ExportInputs.Validate(req, Today);
        Assert.NotNull(result.Error);
        Assert.Equal(400, result.Error.Status);
        Assert.Equal(code, result.Error.Code);
        return result.Error;
    }

    private static (string Day, string WorkStart, string WorkEnd, IReadOnlyList<Block> Blocks, ApiError? Error) Ok(ExportRequest req)
    {
        var result = ExportInputs.Validate(req, Today);
        Assert.Null(result.Error);
        return result;
    }

    [Fact]
    public void Validate_NullRequest_ReturnsBadRequest() => Fail(null, "BAD_REQUEST");

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void Validate_NotConfirmed_ReturnsNotConfirmed(bool? confirmed) => Fail(Req(confirmed: confirmed), "NOT_CONFIRMED");

    [Fact]
    public void Validate_NotConfirmedAndInvalidDay_ReportsNotConfirmedFirst() =>
        Fail(Req(confirmed: false, day: "garbage"), "NOT_CONFIRMED");

    [Fact]
    public void Validate_ConfirmedRequest_ReturnsDayDefaultWindowAndBlocks()
    {
        var result = Ok(Req());

        Assert.Equal(("2026-11-05", "00:00", "23:59"), (result.Day, result.WorkStart, result.WorkEnd));
        Assert.Single(result.Blocks);
    }

    // day

    [Theory]
    [InlineData("2026-11-04")]
    [InlineData("2027-11-05")]
    public void Validate_DayAtRangeEdge_IsAccepted(string day) => Assert.Equal(day, Ok(Req(day: day)).Day);

    [Theory]
    [InlineData("2026-11-03")]
    [InlineData("2027-11-06")]
    [InlineData("2026-13-01")]
    [InlineData("")]
    [InlineData(null)]
    public void Validate_DayOutsideRangeOrMissing_ReturnsBadRequest(string? day) => Fail(Req(day: day), "BAD_REQUEST");

    // work window

    [Fact]
    public void Validate_WorkWindowGiven_IsKept()
    {
        var result = Ok(Req(workStart: "08:00", workEnd: "19:30"));

        Assert.Equal(("08:00", "19:30"), (result.WorkStart, result.WorkEnd));
    }

    [Theory]
    [InlineData("9:00", "18:00")]
    [InlineData("09:00", "24:00")]
    [InlineData("18:00", "09:00")]
    [InlineData("09:00", "09:00")]
    public void Validate_BadWorkWindow_ReturnsBadRequest(string start, string end) =>
        Fail(Req(workStart: start, workEnd: end), "BAD_REQUEST");

    // block count

    [Fact]
    public void Validate_NoBlocksArray_ReturnsBadRequest() => Fail(new ExportRequest(true, "2026-11-05", null, null, null), "BAD_REQUEST");

    [Fact]
    public void Validate_EmptyBlocks_ReturnsBadRequest() => Fail(Req([]), "BAD_REQUEST");

    [Fact]
    public void Validate_FiftyBlocks_AreAccepted()
    {
        var blocks = Enumerable.Range(0, 50).Select(i => B($"b{i}", start: $"{i / 6:00}:{i % 6 * 10:00}", end: $"{i / 6:00}:{i % 6 * 10 + 5:00}")).ToArray();

        Assert.Equal(50, Ok(Req(blocks)).Blocks.Count);
    }

    [Fact]
    public void Validate_FiftyOneBlocks_ReturnsBadRequest()
    {
        var blocks = Enumerable.Range(0, 51).Select(i => B($"b{i}")).ToArray();

        Fail(Req(blocks), "BAD_REQUEST");
    }

    // block fields

    [Fact]
    public void Validate_NullBlockEntry_ReturnsBadRequest() => Fail(Req([B(), null]), "BAD_REQUEST");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u200B")]
    public void Validate_BlankId_ReturnsBadRequest(string id) => Fail(Req([B(id)]), "BAD_REQUEST");

    [Fact]
    public void Validate_DuplicateIds_ReturnsBadRequest() => Fail(Req([B("b1"), B("b1", start: "11:00", end: "12:00")]), "BAD_REQUEST");

    [Fact]
    public void Validate_LongId_IsCutToTwentyCharacters()
    {
        var block = Assert.Single(Ok(Req([B(new string('x', 40))])).Blocks);

        Assert.Equal(20, block.Id.Length);
    }

    [Fact]
    public void Validate_IdsThatCollideAfterCutting_AreDuplicates() =>
        Fail(Req([B(new string('x', 20) + "1"), B(new string('x', 20) + "2", start: "11:00", end: "12:00")]), "BAD_REQUEST");

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\u202E\u200B")]
    public void Validate_BlankTitle_ReturnsBadRequest(string title) => Fail(Req([B(title: title)]), "BAD_REQUEST");

    [Fact]
    public void Validate_LongTitle_IsCutToEightyCharacters()
    {
        var block = Assert.Single(Ok(Req([B(title: new string('가', 200))])).Blocks);

        Assert.Equal(80, block.Title.Length);
    }

    [Fact]
    public void Validate_TitleWithHiddenCharacters_IsCleaned()
    {
        var block = Assert.Single(Ok(Req([B(title: "회\u200B의\u202E \u0007자료")])).Blocks);

        Assert.Equal("회의 자료", block.Title);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("fixed")]
    [InlineData("break")]
    public void Validate_KnownKind_IsAccepted(string kind) => Assert.Equal(kind, Ok(Req([B(kind: kind)])).Blocks[0].Kind);

    [Theory]
    [InlineData("export_ics")]
    [InlineData("TASK")]
    [InlineData("")]
    [InlineData(null)]
    public void Validate_UnknownKind_ReturnsBadRequest(string? kind) => Fail(Req([B(kind: kind!)]), "BAD_REQUEST");

    [Theory]
    [InlineData("9:00", "10:00")]
    [InlineData("09:00", "25:00")]
    [InlineData("09:00", "09:00")]
    [InlineData("10:00", "09:00")]
    [InlineData("", "10:00")]
    [InlineData("09:00", null)]
    public void Validate_BadBlockTimes_ReturnsBadRequest(string start, string? end) =>
        Fail(Req([B(start: start, end: end!)]), "BAD_REQUEST");

    [Fact]
    public void Validate_Block_DropsTaskIdAndCleansReason()
    {
        var input = new Block("b1", "t9", "회의", "09:00", "10:00", "task", "이유\u200B  " + new string('가', 300));

        var block = Assert.Single(Ok(Req([input])).Blocks);

        Assert.Null(block.TaskId);
        Assert.Equal(120, block.Reason.Length);
        Assert.StartsWith("이유 ", block.Reason);
    }

    [Fact]
    public void Validate_BlocksKeepTheirOrder()
    {
        var result = Ok(Req([B("b2", start: "13:00", end: "14:00"), B("b1")]));

        Assert.Equal(new[] { "b2", "b1" }, result.Blocks.Select(b => b.Id));
    }
}
