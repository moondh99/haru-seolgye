using Haru.Api.Agents;
using Haru.Api.Infrastructure;
using Haru.Api.Workflow;

namespace Haru.Api.Tests;

public class AgentJsonTests
{
    [Theory]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("  {\"a\":1}\n", "{\"a\":1}")]
    [InlineData("```json\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("Here is the plan: {\"a\":{\"b\":2}} Hope it helps!", "{\"a\":{\"b\":2}}")]
    [InlineData("no json here", "no json here")]
    [InlineData("} reversed {", "} reversed {")]
    public void Extract_Reply_ReturnsTheOuterObjectOrTheTrimmedText(string reply, string expected) =>
        Assert.Equal(expected, AgentJson.Extract(reply));

    [Fact]
    public void TryParse_PlainJson_ReturnsTheValue()
    {
        var ok = AgentJson.TryParse<IntakeWire>("{\"items\":[{\"title\":\"A\",\"minutes\":45}]}", out var value, out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal(("A", 45d), (value!.Items![0].Title, value.Items[0].Minutes));
    }

    [Fact]
    public void TryParse_JsonInsideACodeFence_ReturnsTheValue() =>
        Assert.True(AgentJson.TryParse<IntakeWire>("```json\n{\"items\":[]}\n```", out var value, out _) && value!.Items!.Count == 0);

    [Fact]
    public void TryParse_NumberWrittenAsAString_IsAccepted()
    {
        AgentJson.TryParse<IntakeWire>("{\"items\":[{\"title\":\"A\",\"minutes\":\"45\"}]}", out var value, out _);

        Assert.Equal(45d, value!.Items![0].Minutes);
    }

    [Fact]
    public void TryParse_CommentsTrailingCommasAndAnyCase_AreAccepted()
    {
        var reply = "{ // note\n \"Items\": [ {\"Title\": \"A\",}, ], }";

        var ok = AgentJson.TryParse<IntakeWire>(reply, out var value, out _);

        Assert.True(ok);
        Assert.Equal("A", value!.Items![0].Title);
    }

    [Fact]
    public void TryParse_UnknownProperties_AreIgnored() =>
        Assert.True(AgentJson.TryParse<CoachWire>("{\"summary\":\"좋아요\",\"toolCalls\":[{\"name\":\"export_ics\"}]}", out var value, out _)
                    && value!.Summary == "좋아요");

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    public void TryParse_EmptyReply_FailsWithEmptyReply(string reply)
    {
        Assert.False(AgentJson.TryParse<CoachWire>(reply, out var value, out var error));
        Assert.Null(value);
        Assert.Equal("empty reply", error);
    }

    [Fact]
    public void TryParse_NullLiteral_FailsAsNotAnObject()
    {
        Assert.False(AgentJson.TryParse<CoachWire>("null", out _, out var error));
        Assert.Equal("reply was not a JSON object", error);
    }

    [Theory]
    [InlineData("{\"summary\": ")]
    [InlineData("just words")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"summary\": {\"a\": 1}}")]
    public void TryParse_BrokenOrWrongShape_FailsWithAReasonThatNamesInvalidJson(string reply)
    {
        Assert.False(AgentJson.TryParse<CoachWire>(reply, out var value, out var error));
        Assert.Null(value);
        Assert.StartsWith("invalid JSON (", error);
    }
}

public class ClockTests
{
    [Theory]
    [InlineData("00:00", 0)]
    [InlineData("09:05", 545)]
    [InlineData("23:59", 1439)]
    public void TryMinutes_ValidTime_ReturnsMinutesSinceMidnight(string text, int expected)
    {
        Assert.True(Clock.TryMinutes(text, out var minutes));
        Assert.Equal(expected, minutes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("9:00")]
    [InlineData("09:0")]
    [InlineData("24:00")]
    [InlineData("09:60")]
    [InlineData("09-00")]
    [InlineData("+9:00")]
    [InlineData("-1:00")]
    [InlineData("09:00:00")]
    [InlineData("ab:cd")]
    public void TryMinutes_InvalidTime_ReturnsFalse(string? text) => Assert.False(Clock.TryMinutes(text, out _));

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(545, "09:05")]
    [InlineData(1439, "23:59")]
    public void Hhmm_Minutes_FormatsWithTwoDigits(int minutes, string expected) => Assert.Equal(expected, Clock.Hhmm(minutes));

    [Theory]
    [InlineData("2026-11-05", true)]
    [InlineData("2026-02-29", false)]
    [InlineData("2026-2-5", false)]
    [InlineData("26-11-05", false)]
    [InlineData("2026/11/05", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryParseDay_Text_AcceptsOnlyARealIsoDate(string? text, bool expected) =>
        Assert.Equal(expected, Clock.TryParseDay(text, out _));

    [Theory]
    [InlineData("2026-11-02", "월")]
    [InlineData("2026-11-05", "목")]
    [InlineData("2026-11-07", "토")]
    [InlineData("2026-11-08", "일")]
    [InlineData("nonsense", "")]
    public void Weekday_Day_ReturnsTheKoreanWeekdayOrNothing(string day, string expected) =>
        Assert.Equal(expected, Clock.Weekday(day));

    [Fact]
    public void KstOffset_IsNineHours() => Assert.Equal(TimeSpan.FromHours(9), Clock.KstOffset);
}
