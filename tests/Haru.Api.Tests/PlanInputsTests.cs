using Haru.Api.Config;
using Haru.Api.Contracts;
using Haru.Api.Endpoints;

namespace Haru.Api.Tests;

public class PlanInputsTests
{
    private static readonly DateOnly Today = new(2026, 11, 5);

    private static (Workflow.PlanInput? Input, ApiError? Error) Validate(PlanRequest? req, HaruOptions? options = null) =>
        PlanInputs.Validate(req, options ?? new HaruOptions(), Today);

    private static PlanRequest Req(string? text = "보고서 쓰기", string? day = null, string? workStart = null,
        string? workEnd = null, string? lunchStart = null, string? lunchEnd = null) =>
        new(text, day, workStart, workEnd, lunchStart, lunchEnd);

    private static void AssertError(PlanRequest? req, string code, HaruOptions? options = null)
    {
        var (input, error) = Validate(req, options);
        Assert.Null(input);
        Assert.NotNull(error);
        Assert.Equal(400, error.Status);
        Assert.Equal(code, error.Code);
    }

    private static Workflow.PlanInput AssertOk(PlanRequest req)
    {
        var (input, error) = Validate(req);
        Assert.Null(error);
        return input!;
    }

    [Fact]
    public void Validate_NullRequest_ReturnsBadRequest() => AssertError(null, "BAD_REQUEST");

    [Fact]
    public void Validate_MinimalRequest_UsesTodayAndDefaultHours()
    {
        var input = AssertOk(Req());

        Assert.Equal(("2026-11-05", "09:00", "18:00", "12:00", "13:00"),
            (input.Day, input.WorkStart, input.WorkEnd, input.LunchStart, input.LunchEnd));
        Assert.Equal("보고서 쓰기", input.Text);
    }

    // text

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t \r\n")]
    [InlineData("\u200B\u200D\u202E\u2066")]
    public void Validate_BlankOrInvisibleText_ReturnsInputEmpty(string? text) => AssertError(Req(text), "INPUT_EMPTY");

    [Fact]
    public void Validate_TextAtLimit_IsAccepted()
    {
        var input = AssertOk(Req(new string('가', 3000)));

        Assert.Equal(3000, input.Text.Length);
    }

    [Fact]
    public void Validate_TextOneOverLimit_ReturnsInputTooLong() => AssertError(Req(new string('가', 3001)), "INPUT_TOO_LONG");

    [Fact]
    public void Validate_LimitIsCheckedOnRawLength_EvenWhenCleanedTextWouldFit() =>
        AssertError(Req(new string('a', 3000) + "\u200B"), "INPUT_TOO_LONG");

    [Fact]
    public void Validate_TooLongAndBlank_ReportsTooLongFirst() =>
        AssertError(Req(new string(' ', 3001)), "INPUT_TOO_LONG");

    [Fact]
    public void Validate_ConfiguredLimit_ReplacesTheDefault()
    {
        var options = new HaruOptions { MaxInputChars = 10 };

        AssertError(Req("12345678901"), "INPUT_TOO_LONG", options);
        Assert.Null(Validate(Req("1234567890"), options).Error);
    }

    [Fact]
    public void Validate_TextWithHiddenAndControlCharacters_ReturnsCleanedText()
    {
        var input = AssertOk(Req("할\u200B 일\u0000  하나\u202E\n둘\u0007"));

        Assert.Equal("할 일 하나\n둘", input.Text);
    }

    // day

    [Theory]
    [InlineData("2026-11-04")]
    [InlineData("2026-11-05")]
    [InlineData("2027-11-05")]
    [InlineData(" 2026-12-25 ")]
    public void Validate_DayInsideRange_IsAccepted(string day) =>
        Assert.Equal(day.Trim(), AssertOk(Req(day: day)).Day);

    [Theory]
    [InlineData("2026-11-03")]
    [InlineData("2027-11-06")]
    [InlineData("2026-02-30")]
    [InlineData("2026/11/05")]
    [InlineData("26-11-05")]
    [InlineData("tomorrow")]
    [InlineData("2026-11-05T09:00:00")]
    public void Validate_DayOutsideRangeOrBadFormat_ReturnsBadRequest(string day) => AssertError(Req(day: day), "BAD_REQUEST");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Validate_BlankDay_MeansToday(string? day) => Assert.Equal("2026-11-05", AssertOk(Req(day: day)).Day);

    // work window

    [Theory]
    [InlineData("9:00")]
    [InlineData("09:00:00")]
    [InlineData("24:00")]
    [InlineData("09:60")]
    [InlineData("0900")]
    [InlineData("오전 9시")]
    public void Validate_BadWorkStartFormat_ReturnsBadRequest(string value) =>
        AssertError(Req(workStart: value), "BAD_REQUEST");

    [Theory]
    [InlineData("6pm")]
    [InlineData("18.00")]
    [InlineData("99:99")]
    public void Validate_BadWorkEndFormat_ReturnsBadRequest(string value) =>
        AssertError(Req(workEnd: value), "BAD_REQUEST");

    [Theory]
    [InlineData("09:00", "09:59", false)]
    [InlineData("09:00", "10:00", true)]
    [InlineData("09:00", "09:00", false)]
    [InlineData("18:00", "09:00", false)]
    [InlineData("00:00", "23:59", true)]
    public void Validate_WorkWindow_NeedsAtLeast60Minutes(string start, string end, bool accepted)
    {
        var (input, error) = Validate(Req(workStart: start, workEnd: end, lunchStart: "", lunchEnd: ""));

        Assert.Equal(accepted, error is null);
        if (!accepted) Assert.Equal("BAD_REQUEST", error!.Code);
        else Assert.Equal((start, end), (input!.WorkStart, input.WorkEnd));
    }

    [Fact]
    public void Validate_WorkTimesWithSpaces_AreTrimmed()
    {
        var input = AssertOk(Req(workStart: " 08:30 ", workEnd: " 17:30"));

        Assert.Equal(("08:30", "17:30"), (input.WorkStart, input.WorkEnd));
    }

    [Fact]
    public void Validate_BlankWorkTimes_UseDefaults()
    {
        var input = AssertOk(Req(workStart: " ", workEnd: ""));

        Assert.Equal(("09:00", "18:00"), (input.WorkStart, input.WorkEnd));
    }

    // lunch

    [Fact]
    public void Validate_NoLunchFields_UsesDefaultLunch()
    {
        var input = AssertOk(Req());

        Assert.Equal(("12:00", "13:00"), (input.LunchStart, input.LunchEnd));
    }

    [Fact]
    public void Validate_OneLunchFieldMissing_FillsOnlyThatFieldFromTheDefault()
    {
        var input = AssertOk(Req(lunchEnd: "13:30"));

        Assert.Equal(("12:00", "13:30"), (input.LunchStart, input.LunchEnd));
    }

    [Fact]
    public void Validate_WhitespaceLunchField_IsTreatedAsMissing()
    {
        var input = AssertOk(Req(lunchStart: " ", lunchEnd: "13:30"));

        Assert.Equal(("12:00", "13:30"), (input.LunchStart, input.LunchEnd));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("", null)]
    [InlineData(null, "")]
    [InlineData("", "13:00")]
    [InlineData("12:00", "")]
    public void Validate_EmptyLunchString_MeansNoLunchAtAll(string? start, string? end)
    {
        var input = AssertOk(Req(lunchStart: start, lunchEnd: end));

        Assert.Equal(("", ""), (input.LunchStart, input.LunchEnd));
    }

    [Theory]
    [InlineData("12:00", "12:00")]
    [InlineData("13:00", "12:00")]
    [InlineData("12:0", "13:00")]
    [InlineData("12:00", "25:00")]
    [InlineData("noon", "13:00")]
    public void Validate_BadLunch_ReturnsBadRequest(string start, string end) =>
        AssertError(Req(lunchStart: start, lunchEnd: end), "BAD_REQUEST");

    [Fact]
    public void Validate_CustomLunch_IsKept()
    {
        var input = AssertOk(Req(lunchStart: "11:30", lunchEnd: "12:30"));

        Assert.Equal(("11:30", "12:30"), (input.LunchStart, input.LunchEnd));
    }
}
