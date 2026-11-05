using System.Globalization;
using System.Text;

namespace Haru.Mcp.Logic;

public static class IcsExporter
{
    private const string DefaultCalendarName = "하루설계";
    private const string AiNotice = "AI가 만든 제안입니다(하루설계)";
    private const string Crlf = "\r\n";

    public static Outcome<IcsResult> Export(string? day, IReadOnlyList<ExportBlock?>? blocks, string? calendarName)
    {
        if (!TimeUtil.TryParseDay(day, out var date))
        {
            return Outcome<IcsResult>.Fail("day는 YYYY-MM-DD 형식이어야 합니다.");
        }

        blocks ??= [];
        if (blocks.Count > TimeUtil.MaxItems)
        {
            return Outcome<IcsResult>.Fail($"blocks는 최대 {TimeUtil.MaxItems}개까지 보낼 수 있습니다.");
        }

        if (TimeUtil.TooLong(calendarName))
        {
            return Outcome<IcsResult>.Fail($"calendarName은 {TimeUtil.MaxText}자 이하여야 합니다.");
        }

        var events = new List<(string Uid, string Title, int Start, int End, string? Reason)>();
        var seenIds = new HashSet<string>();
        foreach (var block in blocks)
        {
            if (block is null
                || TimeUtil.TooLong(block.Id) || TimeUtil.TooLong(block.Title) || TimeUtil.TooLong(block.Reason)
                || string.IsNullOrWhiteSpace(block.Id)
                || !TimeUtil.TryParseHhmm(block.Start, out var start)
                || !TimeUtil.TryParseHhmm(block.End, out var end)
                || start >= end)
            {
                return Outcome<IcsResult>.Fail("blocks의 각 항목은 id, HH:mm 형식의 start·end(start < end)가 필요하고 문자열은 200자 이하여야 합니다.");
            }

            switch (block.Kind?.Trim().ToLowerInvariant())
            {
                case "break":
                    continue;
                case "task" or "fixed":
                    break;
                default:
                    return Outcome<IcsResult>.Fail("blocks의 kind는 task, fixed, break 중 하나여야 합니다.");
            }

            var uid = SafeUid(block.Id.Trim());
            if (!seenIds.Add(uid))
            {
                return Outcome<IcsResult>.Fail("blocks의 id가 중복되었습니다. 이벤트 UID가 겹칩니다.");
            }

            events.Add((uid, block.Title ?? "", start, end, block.Reason));
        }

        var compactDay = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var isoDay = TimeUtil.FormatDay(date);
        var sb = new StringBuilder();
        void Line(string line) => AppendFolded(sb, line);

        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//Haru//Haru 하루설계//KO");
        Line("CALSCALE:GREGORIAN");
        Line("METHOD:PUBLISH");
        Line("X-WR-CALNAME:" + EscapeText(string.IsNullOrWhiteSpace(calendarName) ? DefaultCalendarName : calendarName));
        Line("BEGIN:VTIMEZONE");
        Line("TZID:Asia/Seoul");
        Line("BEGIN:STANDARD");
        Line("DTSTART:19700101T000000");
        Line("TZOFFSETFROM:+0900");
        Line("TZOFFSETTO:+0900");
        Line("TZNAME:KST");
        Line("END:STANDARD");
        Line("END:VTIMEZONE");

        foreach (var (uid, title, start, end, reason) in events)
        {
            var summary = EscapeText(title);
            var description = EscapeText(reason);
            Line("BEGIN:VEVENT");
            Line($"UID:{uid}-{isoDay}@haru");
            Line($"DTSTAMP:{compactDay}T000000Z");
            Line($"DTSTART;TZID=Asia/Seoul:{compactDay}T{TimeUtil.Format(start).Replace(":", "")}00");
            Line($"DTEND;TZID=Asia/Seoul:{compactDay}T{TimeUtil.Format(end).Replace(":", "")}00");
            Line("SUMMARY:" + (summary.Length > 0 ? summary : "(제목 없음)"));
            Line("DESCRIPTION:" + (description.Length > 0 ? description + "\\n" : "") + AiNotice);
            Line("END:VEVENT");
        }

        Line("END:VCALENDAR");
        return Outcome<IcsResult>.Ok(new IcsResult($"haru-{isoDay}.ics", sb.ToString(), events.Count));
    }

    // Control characters (CR/LF included) are removed first so user text can never start a new property or event.
    private static string EscapeText(string? text) =>
        TimeUtil.SingleLine(text).Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,");

    private static string SafeUid(string id) =>
        string.Create(id.Length, id, (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                var c = source[i];
                span[i] = char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_';
            }
        });

    // RFC 5545 section 3.1: lines are at most 75 octets (UTF-8); continuation lines start with one space.
    private static void AppendFolded(StringBuilder sb, string line)
    {
        var budget = 75;
        var used = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (used + size > budget)
            {
                sb.Append(Crlf).Append(' ');
                used = 0;
                budget = 74;
            }

            sb.Append(rune.ToString());
            used += size;
        }

        sb.Append(Crlf);
    }
}
