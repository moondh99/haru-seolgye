using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Haru.Mcp.Logic;

/// <summary>Resolves Korean date/time phrases against a reference date. Pure: nothing here reads the clock.</summary>
public static class DateResolver
{
    public const int MaxExpressionLength = 60;

    private const string Unrecognized = "날짜·시각 표현을 이해하지 못했습니다. 예: 내일, 다음 주 월요일 오전 10시, 11월 7일, 17:00.";
    private const string NoSuchDate = "존재하지 않는 날짜입니다.";
    private const string NoSuchTime = "존재하지 않는 시각입니다.";

    private const string Period = "오전|오후|아침|점심|저녁|밤|새벽";
    private const string WeekdayChars = "월화수목금토일";

    private static readonly Regex Spaces = Rx(@"\s+", anchored: false);
    private static readonly Regex TrailingPunctuation = Rx(@"[.!?~]+$", anchored: false);
    private static readonly Regex Suffixes = Rx(@"(?<=\S)(?:까지|쯤|경|에게|에)+(?=\s|$)", anchored: false);

    private static readonly Regex NamedTime = Rx("(?<word>정오|자정)");
    private static readonly Regex ColonTime = Rx($@"(?:(?<period>{Period})\s*)?(?<hour>[0-9]{{1,2}}):(?<minute>[0-9]{{2}})(?![0-9])");
    private static readonly Regex KoreanTime = Rx($@"(?:(?<period>{Period})\s*)?(?<hour>[0-9]{{1,2}})\s*시(?!간)(?:\s*(?:(?<half>반)|(?<minute>[0-9]{{1,2}})\s*분|정각))?");

    // Tried in order, so more specific shapes come before the shapes they contain ("3일 후" before "3일").
    private static readonly (Regex Pattern, Func<Match, DateOnly, DateOnly?> Resolve)[] DateRules =
    [
        (Rx("(?<word>오늘|내일|모레|글피)"), (m, today) => today.AddDays(m.Groups["word"].Value switch { "오늘" => 0, "내일" => 1, "모레" => 2, _ => 3 })),
        (Rx(@"(?<n>[0-9]{1,4})\s*일\s*(?:후|뒤)"), (m, today) => today.AddDays(Num(m, "n"))),
        (Rx($@"(?<week>다다음|이번|다음)\s*주\s*(?<dow>[{WeekdayChars}])\s*요일"), WeekRelativeWeekday),
        (Rx($@"(?<dow>[{WeekdayChars}])\s*요일"), NextWeekday),
        (Rx(@"(?<which>이번|다음)\s*달\s*말일?"), MonthEnd),
        (Rx(@"(?<which>이번|다음)\s*달\s*(?<day>[0-9]{1,2})\s*일"), MonthRelativeDay),
        (Rx(@"(?:(?<year>[0-9]{4})\s*년\s*)?(?<month>[0-9]{1,2})\s*월\s*(?<day>[0-9]{1,2})\s*일"), MonthDay),
        (Rx(@"(?<month>[0-9]{1,2})\s*/\s*(?<day>[0-9]{1,2})(?![0-9/])"), MonthDay),
        (Rx(@"(?<year>[0-9]{4})-(?<month>[0-9]{1,2})-(?<day>[0-9]{1,2})(?![0-9])"), MonthDay),
        (Rx(@"(?<day>[0-9]{1,2})\s*일"), DayOfMonth),
    ];

    public static ResolveDateResult Resolve(string? expression, string? referenceDate)
    {
        if (!TimeUtil.TryParseDay(referenceDate, out var today))
        {
            return Fail("referenceDate는 YYYY-MM-DD 형식이어야 합니다.");
        }

        if (string.IsNullOrWhiteSpace(expression))
        {
            return Fail("표현이 비어 있습니다. 예: 내일, 다음 주 월요일 오전 10시.");
        }

        if (expression.Length > MaxExpressionLength)
        {
            return Fail($"표현은 {MaxExpressionLength}자 이하여야 합니다.");
        }

        try
        {
            return Parse(Normalize(expression), today);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException or OverflowException or FormatException)
        {
            return Fail(Unrecognized);
        }
    }

    private static ResolveDateResult Parse(string text, DateOnly today)
    {
        DateOnly? date = null;
        int? minutes = null;
        var rest = text;
        while (rest.Length > 0)
        {
            var consumed = 0;
            if (date is null)
            {
                foreach (var (pattern, resolve) in DateRules)
                {
                    var match = pattern.Match(rest);
                    if (!match.Success)
                    {
                        continue;
                    }

                    date = resolve(match, today);
                    if (date is null)
                    {
                        return Fail(NoSuchDate);
                    }

                    consumed = match.Length;
                    break;
                }
            }

            if (consumed == 0 && minutes is null && ParseTime(rest) is { } time)
            {
                if (time.Minutes is null)
                {
                    return Fail(NoSuchTime);
                }

                minutes = time.Minutes;
                consumed = time.Length;
            }

            if (consumed == 0)
            {
                return Fail(Unrecognized);
            }

            rest = rest[consumed..].TrimStart();
        }

        var dateText = date is { } d ? TimeUtil.FormatDay(d) : null;
        var timeText = minutes is { } t ? TimeUtil.Format(t) : null;
        var normalized = dateText is not null && timeText is not null ? $"{dateText} {timeText}" : dateText ?? timeText;
        return new ResolveDateResult(true, dateText, timeText, normalized, null);
    }

    private static string Normalize(string expression)
    {
        var text = Spaces.Replace(expression.Normalize(NormalizationForm.FormKC).Replace(',', ' '), " ").Trim();
        text = TrailingPunctuation.Replace(text, "").TrimEnd();
        return Suffixes.Replace(text, "");
    }

    private static ResolveDateResult Fail(string error) => new(false, null, null, null, error);

    private static Regex Rx(string pattern, bool anchored = true) =>
        new(anchored ? $"^(?:{pattern})" : pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    private static int Num(Match match, string group) =>
        int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    // Null when no time expression starts the text; Minutes is null when it looks like a time but cannot exist (25시).
    private static (int Length, int? Minutes)? ParseTime(string rest)
    {
        var named = NamedTime.Match(rest);
        if (named.Success)
        {
            return (named.Length, named.Groups["word"].Value == "정오" ? 12 * 60 : 0);
        }

        var colon = ColonTime.Match(rest);
        var match = colon.Success ? colon : KoreanTime.Match(rest);
        if (!match.Success)
        {
            return null;
        }

        var minute = match.Groups["half"].Success ? 30 : match.Groups["minute"].Success ? Num(match, "minute") : 0;
        var hour = minute > 59 ? null : ResolveHour(match.Groups["period"].Value, Num(match, "hour"), colon.Success);
        return (match.Length, hour is { } h ? h * 60 + minute : null);
    }

    // Maps a spoken hour (plus optional period word) to a 0-23 hour. "HH:mm" without a period word is literal 24-hour.
    private static int? ResolveHour(string period, int hour, bool literalWhenUnmarked)
    {
        if (period.Length == 0)
        {
            if (hour > 23)
            {
                return null;
            }

            // Unmarked N시: 1-6 are afternoon, 7-11 morning, 12 noon, 0 and 13+ as written.
            return literalWhenUnmarked || hour is 0 or >= 7 ? hour : hour + 12;
        }

        if (hour > 12)
        {
            return null;
        }

        return period switch
        {
            "오전" or "아침" or "새벽" => (int?)(hour % 12),
            "오후" or "저녁" => hour == 0 ? null : hour % 12 + 12,
            "점심" => hour == 0 ? null : hour >= 10 ? hour : hour + 12,
            _ => hour switch { >= 6 and <= 11 => hour + 12, 12 => 0, >= 1 and <= 5 => hour, _ => null }, // 밤
        };
    }

    private static DateOnly? TryDate(int year, int month, int day) =>
        year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;

    private static int MondayIndex(DateOnly date) => ((int)date.DayOfWeek + 6) % 7;

    private static DateOnly? WeekRelativeWeekday(Match m, DateOnly today)
    {
        var weeks = m.Groups["week"].Value switch { "이번" => 0, "다음" => 1, _ => 2 };
        var weekday = WeekdayChars.IndexOf(m.Groups["dow"].Value[0]);
        return today.AddDays(-MondayIndex(today) + 7 * weeks + weekday);
    }

    private static DateOnly? NextWeekday(Match m, DateOnly today) =>
        today.AddDays((WeekdayChars.IndexOf(m.Groups["dow"].Value[0]) - MondayIndex(today) + 7) % 7);

    private static DateOnly FirstOfMonth(Match m, DateOnly today) =>
        new DateOnly(today.Year, today.Month, 1).AddMonths(m.Groups["which"].Value == "다음" ? 1 : 0);

    private static DateOnly? MonthEnd(Match m, DateOnly today)
    {
        var first = FirstOfMonth(m, today);
        return new DateOnly(first.Year, first.Month, DateTime.DaysInMonth(first.Year, first.Month));
    }

    private static DateOnly? MonthRelativeDay(Match m, DateOnly today)
    {
        var first = FirstOfMonth(m, today);
        return TryDate(first.Year, first.Month, Num(m, "day"));
    }

    // Without an explicit year: the next occurrence on or after the reference date, which may be in a later year.
    private static DateOnly? MonthDay(Match m, DateOnly today)
    {
        var month = Num(m, "month");
        var day = Num(m, "day");
        if (m.Groups["year"].Success)
        {
            return TryDate(Num(m, "year"), month, day);
        }

        for (var year = today.Year; year <= today.Year + 8; year++)
        {
            if (TryDate(year, month, day) is { } candidate && candidate >= today)
            {
                return candidate;
            }
        }

        return null;
    }

    // A bare "N일" is the next such day of the month on or after the reference date (skipping months that lack it).
    private static DateOnly? DayOfMonth(Match m, DateOnly today)
    {
        var day = Num(m, "day");
        var month = new DateOnly(today.Year, today.Month, 1);
        for (var i = 0; i < 13; i++, month = month.AddMonths(1))
        {
            if (TryDate(month.Year, month.Month, day) is { } candidate && candidate >= today)
            {
                return candidate;
            }
        }

        return null;
    }
}
