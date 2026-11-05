using Haru.Mcp.Logic;

namespace Haru.Mcp.Tests;

public class DateResolverTests
{
    // 2026-11-05 is a Thursday; its week (Monday first) runs 11-02 .. 11-08.
    private const string Thu = "2026-11-05";

    [Theory]
    // Relative days
    [InlineData(Thu, "오늘", "2026-11-05", null)]
    [InlineData(Thu, "내일", "2026-11-06", null)]
    [InlineData(Thu, "모레", "2026-11-07", null)]
    [InlineData(Thu, "글피", "2026-11-08", null)]
    [InlineData(Thu, "3일 후", "2026-11-08", null)]
    [InlineData(Thu, "3일 뒤", "2026-11-08", null)]
    [InlineData(Thu, "3일후", "2026-11-08", null)]
    [InlineData(Thu, "10일 후", "2026-11-15", null)]
    [InlineData(Thu, "30일 뒤", "2026-12-05", null)]
    // Spacing, suffixes and punctuation
    [InlineData(Thu, "  내일  ", "2026-11-06", null)]
    [InlineData(Thu, "내일까지", "2026-11-06", null)]
    [InlineData(Thu, "내일쯤", "2026-11-06", null)]
    [InlineData(Thu, "모레에", "2026-11-07", null)]
    [InlineData(Thu, "오늘!", "2026-11-05", null)]
    [InlineData(Thu, "내일.", "2026-11-06", null)]
    // Weekdays, week starts on Monday
    [InlineData(Thu, "이번 주 금요일", "2026-11-06", null)]
    [InlineData(Thu, "이번 주 월요일", "2026-11-02", null)]
    [InlineData(Thu, "이번 주 일요일", "2026-11-08", null)]
    [InlineData(Thu, "이번주 금요일", "2026-11-06", null)]
    [InlineData(Thu, "다음 주 월요일", "2026-11-09", null)]
    [InlineData(Thu, "다음 주 금요일", "2026-11-13", null)]
    [InlineData(Thu, "다음 주 일요일", "2026-11-15", null)]
    [InlineData(Thu, "다음주 월요일", "2026-11-09", null)]
    [InlineData(Thu, "다음  주   월요일", "2026-11-09", null)]
    [InlineData(Thu, "다음 주 월요일에", "2026-11-09", null)]
    [InlineData(Thu, "다다음 주 수요일", "2026-11-18", null)]
    [InlineData(Thu, "다다음 주 월요일", "2026-11-16", null)]
    [InlineData(Thu, "금요일", "2026-11-06", null)]
    [InlineData(Thu, "금요일까지", "2026-11-06", null)]
    [InlineData(Thu, "목요일", "2026-11-05", null)]
    [InlineData(Thu, "토요일", "2026-11-07", null)]
    [InlineData(Thu, "일요일", "2026-11-08", null)]
    [InlineData(Thu, "월요일", "2026-11-09", null)]
    [InlineData(Thu, "수요일", "2026-11-11", null)]
    // Month based
    [InlineData(Thu, "이번 달 말", "2026-11-30", null)]
    [InlineData(Thu, "이번 달 말일", "2026-11-30", null)]
    [InlineData(Thu, "이번달 말", "2026-11-30", null)]
    [InlineData(Thu, "다음 달 말", "2026-12-31", null)]
    [InlineData(Thu, "다음 달 5일", "2026-12-05", null)]
    [InlineData(Thu, "다음 달 5일까지", "2026-12-05", null)]
    [InlineData(Thu, "다음 달 31일", "2026-12-31", null)]
    [InlineData(Thu, "이번 달 20일", "2026-11-20", null)]
    [InlineData(Thu, "이번 달 3일", "2026-11-03", null)]
    [InlineData(Thu, "11월 7일", "2026-11-07", null)]
    [InlineData(Thu, "11월7일", "2026-11-07", null)]
    [InlineData(Thu, "11/7", "2026-11-07", null)]
    [InlineData(Thu, "11월 5일", "2026-11-05", null)]
    [InlineData(Thu, "11월 4일", "2027-11-04", null)]
    [InlineData(Thu, "12월 25일", "2026-12-25", null)]
    [InlineData(Thu, "1월 3일", "2027-01-03", null)]
    [InlineData(Thu, "2026-11-20", "2026-11-20", null)]
    [InlineData(Thu, "2026년 12월 1일", "2026-12-01", null)]
    [InlineData(Thu, "2월 29일", "2028-02-29", null)]
    [InlineData(Thu, "１１월 ７일", "2026-11-07", null)]
    [InlineData(Thu, "3일", "2026-12-03", null)]
    [InlineData(Thu, "5일", "2026-11-05", null)]
    [InlineData(Thu, "7일", "2026-11-07", null)]
    [InlineData(Thu, "7일까지", "2026-11-07", null)]
    [InlineData(Thu, "31일", "2026-12-31", null)]
    // Times only: date is null
    [InlineData(Thu, "3시 반", null, "15:30")]
    [InlineData(Thu, "3시반", null, "15:30")]
    [InlineData(Thu, "3시 30분", null, "15:30")]
    [InlineData(Thu, "3시쯤", null, "15:00")]
    [InlineData(Thu, "오전 9시 30분", null, "09:30")]
    [InlineData(Thu, "오전 9시 정각", null, "09:00")]
    [InlineData(Thu, "오전 11시 59분", null, "11:59")]
    [InlineData(Thu, "오전 12시", null, "00:00")]
    [InlineData(Thu, "오후 3시", null, "15:00")]
    [InlineData(Thu, "오후3시", null, "15:00")]
    [InlineData(Thu, "오후 3시경", null, "15:00")]
    [InlineData(Thu, "오후 3시 15분", null, "15:15")]
    [InlineData(Thu, "오후 12시", null, "12:00")]
    [InlineData(Thu, "오후 12시 30분", null, "12:30")]
    [InlineData(Thu, "오후 3:30", null, "15:30")]
    [InlineData(Thu, "아침 8시", null, "08:00")]
    [InlineData(Thu, "아침 7시 반", null, "07:30")]
    [InlineData(Thu, "새벽 2시", null, "02:00")]
    [InlineData(Thu, "점심 12시", null, "12:00")]
    [InlineData(Thu, "점심 1시", null, "13:00")]
    [InlineData(Thu, "점심 11시", null, "11:00")]
    [InlineData(Thu, "저녁 7시", null, "19:00")]
    [InlineData(Thu, "저녁 6시 45분", null, "18:45")]
    [InlineData(Thu, "밤 6시", null, "18:00")]
    [InlineData(Thu, "밤 10시", null, "22:00")]
    [InlineData(Thu, "밤 11시", null, "23:00")]
    [InlineData(Thu, "밤 12시", null, "00:00")]
    [InlineData(Thu, "정오", null, "12:00")]
    [InlineData(Thu, "정오쯤", null, "12:00")]
    [InlineData(Thu, "자정", null, "00:00")]
    [InlineData(Thu, "17:00", null, "17:00")]
    [InlineData(Thu, "9:30", null, "09:30")]
    [InlineData(Thu, "09:05", null, "09:05")]
    [InlineData(Thu, "23:59", null, "23:59")]
    [InlineData(Thu, "00:00", null, "00:00")]
    // Unmarked N시: 1-6 afternoon, 7-11 morning, 12 noon, 0 midnight, 13+ as written
    [InlineData(Thu, "0시", null, "00:00")]
    [InlineData(Thu, "1시", null, "13:00")]
    [InlineData(Thu, "6시", null, "18:00")]
    [InlineData(Thu, "7시", null, "07:00")]
    [InlineData(Thu, "11시", null, "11:00")]
    [InlineData(Thu, "12시", null, "12:00")]
    [InlineData(Thu, "13시", null, "13:00")]
    [InlineData(Thu, "23시 30분", null, "23:30")]
    // Date and time together, in either order
    [InlineData(Thu, "내일 오후 3시", "2026-11-06", "15:00")]
    [InlineData(Thu, "내일오후3시", "2026-11-06", "15:00")]
    [InlineData(Thu, "내일, 오후 3시", "2026-11-06", "15:00")]
    [InlineData(Thu, "내일 오후 3시에", "2026-11-06", "15:00")]
    [InlineData(Thu, "내일 3시 반", "2026-11-06", "15:30")]
    [InlineData(Thu, "내일 오전 9시 30분", "2026-11-06", "09:30")]
    [InlineData(Thu, "오늘 저녁 7시", "2026-11-05", "19:00")]
    [InlineData(Thu, "오늘 17:00", "2026-11-05", "17:00")]
    [InlineData(Thu, "모레 아침 8시", "2026-11-07", "08:00")]
    [InlineData(Thu, "모레 밤 10시", "2026-11-07", "22:00")]
    [InlineData(Thu, "다음 주 월요일 오전 10시 30분", "2026-11-09", "10:30")]
    [InlineData(Thu, "다음 주 월요일 정오", "2026-11-09", "12:00")]
    [InlineData(Thu, "다다음 주 수요일 자정", "2026-11-18", "00:00")]
    [InlineData(Thu, "금요일까지 17:00", "2026-11-06", "17:00")]
    [InlineData(Thu, "금요일까지 17:00까지", "2026-11-06", "17:00")]
    [InlineData(Thu, "11월 7일 오후 2시", "2026-11-07", "14:00")]
    [InlineData(Thu, "11월 7일 17:00", "2026-11-07", "17:00")]
    [InlineData(Thu, "11/7 9:00", "2026-11-07", "09:00")]
    [InlineData(Thu, "3일 후 정오", "2026-11-08", "12:00")]
    [InlineData(Thu, "다음 달 5일 오후 2시 반", "2026-12-05", "14:30")]
    [InlineData(Thu, "이번 달 말 17:00", "2026-11-30", "17:00")]
    [InlineData(Thu, "2026-11-20 14:00", "2026-11-20", "14:00")]
    [InlineData(Thu, "17:00 내일", "2026-11-06", "17:00")]
    [InlineData(Thu, "오후 3시 내일", "2026-11-06", "15:00")]
    // Sunday reference: the week that contains it started on Monday 11-02
    [InlineData("2026-11-08", "이번 주 금요일", "2026-11-06", null)]
    [InlineData("2026-11-08", "이번 주 일요일", "2026-11-08", null)]
    [InlineData("2026-11-08", "다음 주 월요일", "2026-11-09", null)]
    [InlineData("2026-11-08", "다음 주 일요일", "2026-11-15", null)]
    [InlineData("2026-11-08", "다다음 주 월요일", "2026-11-16", null)]
    [InlineData("2026-11-08", "금요일", "2026-11-13", null)]
    [InlineData("2026-11-08", "일요일", "2026-11-08", null)]
    [InlineData("2026-11-08", "월요일", "2026-11-09", null)]
    // Monday reference
    [InlineData("2026-11-09", "이번 주 월요일", "2026-11-09", null)]
    [InlineData("2026-11-09", "이번 주 일요일", "2026-11-15", null)]
    [InlineData("2026-11-09", "다음 주 월요일", "2026-11-16", null)]
    [InlineData("2026-11-09", "다다음 주 월요일", "2026-11-23", null)]
    [InlineData("2026-11-09", "월요일", "2026-11-09", null)]
    [InlineData("2026-11-09", "일요일", "2026-11-15", null)]
    [InlineData("2026-11-09", "화요일", "2026-11-10", null)]
    // Year rollover (2026-12-30 is a Wednesday)
    [InlineData("2026-12-30", "이번 달 말", "2026-12-31", null)]
    [InlineData("2026-12-30", "다음 달 말", "2027-01-31", null)]
    [InlineData("2026-12-30", "다음 달 5일", "2027-01-05", null)]
    [InlineData("2026-12-30", "1월 3일", "2027-01-03", null)]
    [InlineData("2026-12-30", "3일", "2027-01-03", null)]
    [InlineData("2026-12-30", "11월 7일", "2027-11-07", null)]
    [InlineData("2026-12-30", "12월 30일", "2026-12-30", null)]
    [InlineData("2026-12-30", "모레", "2027-01-01", null)]
    [InlineData("2026-12-30", "글피", "2027-01-02", null)]
    [InlineData("2026-12-30", "3일 후", "2027-01-02", null)]
    [InlineData("2026-12-30", "이번 주 금요일", "2027-01-01", null)]
    [InlineData("2026-12-30", "다음 주 월요일", "2027-01-04", null)]
    [InlineData("2026-12-30", "다음 주 일요일", "2027-01-10", null)]
    // Leap years (2028-02-28 is a Monday)
    [InlineData("2028-02-28", "이번 달 말", "2028-02-29", null)]
    [InlineData("2028-02-28", "내일", "2028-02-29", null)]
    [InlineData("2028-02-28", "모레", "2028-03-01", null)]
    [InlineData("2028-02-28", "다음 달 말", "2028-03-31", null)]
    [InlineData("2028-02-28", "2월 29일", "2028-02-29", null)]
    [InlineData("2028-02-28", "29일", "2028-02-29", null)]
    [InlineData("2028-02-28", "30일", "2028-03-30", null)]
    [InlineData("2028-02-28", "이번 주 일요일", "2028-03-05", null)]
    [InlineData("2028-03-01", "2월 29일", "2032-02-29", null)]
    [InlineData("2027-02-28", "이번 달 말", "2027-02-28", null)]
    [InlineData("2027-02-28", "내일", "2027-03-01", null)]
    [InlineData("2027-02-28", "이번 주 금요일", "2027-02-26", null)]
    [InlineData("2027-02-28", "다음 주 월요일", "2027-03-01", null)]
    [InlineData("2027-02-28", "2월 29일", "2028-02-29", null)]
    // Months without the requested day are skipped for a bare "N일"
    [InlineData("2026-02-15", "31일", "2026-03-31", null)]
    [InlineData("2026-01-31", "31일", "2026-01-31", null)]
    [InlineData("2026-01-31", "30일", "2026-03-30", null)]
    [InlineData("2026-01-31", "다음 달 말", "2026-02-28", null)]
    public void Resolves_golden_cases(string referenceDate, string expression, string? date, string? time)
    {
        var result = DateResolver.Resolve(expression, referenceDate);

        Assert.True(result.Ok, $"'{expression}' failed: {result.Error}");
        Assert.Equal(date, result.Date);
        Assert.Equal(time, result.Time);
        Assert.Equal(date is null ? time : time is null ? date : $"{date} {time}", result.Normalized);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("아무말")]
    [InlineData("13월 40일")]
    [InlineData("2월 30일")]
    [InlineData("11월 31일")]
    [InlineData("0일")]
    [InlineData("32일")]
    [InlineData("다음 달 32일")]
    [InlineData("25시")]
    [InlineData("24시")]
    [InlineData("24:00")]
    [InlineData("오후 13시")]
    [InlineData("오후 0시")]
    [InlineData("3시 70분")]
    [InlineData("12:60")]
    [InlineData("내일모레")]
    [InlineData("3시간")]
    [InlineData("내일 내일")]
    [InlineData("3시 4시")]
    [InlineData("정오 자정")]
    [InlineData("월요일 화요일")]
    [InlineData("이번 주 금요일 다음 주 월요일")]
    [InlineData("내일 아무말")]
    [InlineData("오전")]
    [InlineData("내일 오전")]
    [InlineData("다음 주 월요일 저녁")]
    [InlineData("시")]
    [InlineData("다음 주")]
    [InlineData("99999일 후")]
    [InlineData("٣일 후")]
    [InlineData("<script>alert(1)</script>")]
    public void Rejects_invalid_input_without_throwing(string expression)
    {
        var result = DateResolver.Resolve(expression, Thu);

        AssertFailed(result);
    }

    [Fact]
    public void Rejects_null_expression()
    {
        AssertFailed(DateResolver.Resolve(null, Thu));
    }

    [Fact]
    public void Enforces_the_60_character_limit()
    {
        var atLimit = "내일" + new string(' ', DateResolver.MaxExpressionLength - 2);
        var overLimit = atLimit + " ";

        Assert.Equal(DateResolver.MaxExpressionLength, atLimit.Length);
        Assert.True(DateResolver.Resolve(atLimit, Thu).Ok);
        AssertFailed(DateResolver.Resolve(overLimit, Thu));
        AssertFailed(DateResolver.Resolve(new string('가', 61), Thu));
        AssertFailed(DateResolver.Resolve(new string('9', 5000), Thu));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-13-45")]
    [InlineData("2026/11/05")]
    [InlineData("20261105")]
    [InlineData("2026-11-5")]
    [InlineData("내일")]
    public void Rejects_a_malformed_reference_date(string? referenceDate)
    {
        var result = DateResolver.Resolve("내일", referenceDate);

        AssertFailed(result);
        Assert.Contains("referenceDate", result.Error);
    }

    [Fact]
    public void Does_not_throw_when_the_result_would_leave_the_calendar()
    {
        AssertFailed(DateResolver.Resolve("내일", "9999-12-31"));
        AssertFailed(DateResolver.Resolve("3일 후", "9999-12-30"));
        Assert.Equal("9999-12-31", DateResolver.Resolve("오늘", "9999-12-31").Date);
    }

    [Fact]
    public void Error_messages_are_korean_and_do_not_echo_the_input()
    {
        var result = DateResolver.Resolve("비밀스러운-입력-9f3a", Thu);

        AssertFailed(result);
        Assert.DoesNotContain("비밀스러운", result.Error);
        Assert.Matches("[가-힣]", result.Error);
    }

    [Fact]
    public void Results_are_deterministic()
    {
        Assert.Equal(DateResolver.Resolve("다음 주 월요일 오전 10시 30분", Thu), DateResolver.Resolve("다음 주 월요일 오전 10시 30분", Thu));
    }

    private static void AssertFailed(ResolveDateResult result)
    {
        Assert.False(result.Ok);
        Assert.Null(result.Date);
        Assert.Null(result.Time);
        Assert.Null(result.Normalized);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Matches("[가-힣]", result.Error);
    }
}
