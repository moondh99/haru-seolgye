using System.Text;
using Haru.Mcp.Logic;

namespace Haru.Mcp.Tests;

public class IcsExporterTests
{
    private const string Day = "2026-11-05";
    private const string AiNotice = "AI가 만든 제안입니다(하루설계)";

    private static ExportBlock Block(
        string id, string? title, string start, string end, string kind = "task", string? reason = null) =>
        new() { Id = id, Title = title, Start = start, End = end, Kind = kind, Reason = reason };

    private static IcsResult RunWith(string day, string? calendarName, params ExportBlock?[] blocks)
    {
        var outcome = IcsExporter.Export(day, blocks, calendarName);
        Assert.True(outcome.IsOk, outcome.Error);
        return outcome.Value!;
    }

    private static IcsResult Run(params ExportBlock?[] blocks) => RunWith(Day, null, blocks);

    private static IcsResult RunSample()
    {
        var sample = SamplePlan.Load();
        return RunWith(sample.Day, null, sample.ExportBlocks.ToArray());
    }

    private static string[] Physical(string ics) => ics.Split("\r\n");

    // RFC 5545 section 3.1 unfolding: a CRLF followed by one space is removed.
    private static string[] Logical(string ics) =>
        ics.Replace("\r\n ", "").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    private static void AssertFailed(Outcome<IcsResult> outcome)
    {
        Assert.False(outcome.IsOk);
        Assert.Null(outcome.Value);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Error));
        Assert.Matches("[가-힣]", outcome.Error);
    }

    // Walks the unfolded lines and proves the document has exactly the expected components and property names,
    // which is what injected text must never be able to change.
    private static void AssertWellFormed(IcsResult result)
    {
        var ics = result.Ics;
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
        Assert.All(ics, c => Assert.True(c is '\r' or '\n' || c >= ' ', $"control character U+{(int)c:X4}"));
        Assert.DoesNotContain('\u2028', ics);
        Assert.DoesNotContain('\u2029', ics);
        Assert.DoesNotContain('\u0085', ics);
        Assert.DoesNotContain('\uFFFD', ics);

        var physical = Physical(ics);
        Assert.Equal("", physical[^1]);
        Assert.All(physical.SkipLast(1), line =>
        {
            Assert.DoesNotContain('\r', line);
            Assert.DoesNotContain('\n', line);
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, $"line has {Encoding.UTF8.GetByteCount(line)} octets");
        });

        var components = new List<(string Name, List<string> Properties)>();
        var open = new Stack<(string Name, List<string> Properties)>();
        foreach (var line in Logical(ics))
        {
            if (line.StartsWith("BEGIN:", StringComparison.Ordinal))
            {
                var component = (Name: line[6..], Properties: new List<string>());
                open.Push(component);
                components.Add(component);
            }
            else if (line.StartsWith("END:", StringComparison.Ordinal))
            {
                Assert.Equal(line[4..], open.Pop().Name);
            }
            else
            {
                var colon = line.IndexOf(':');
                Assert.True(colon > 0, $"not a content line: {line}");
                open.Peek().Properties.Add(line[..colon]);
            }
        }

        Assert.Empty(open);
        Assert.Equal(
            new[] { "VCALENDAR", "VTIMEZONE", "STANDARD" }.Concat(Enumerable.Repeat("VEVENT", result.EventCount)),
            components.Select(c => c.Name));
        Assert.Equal(new[] { "VERSION", "PRODID", "CALSCALE", "METHOD", "X-WR-CALNAME" }, components[0].Properties);
        Assert.Equal(new[] { "TZID" }, components[1].Properties);
        Assert.Equal(new[] { "DTSTART", "TZOFFSETFROM", "TZOFFSETTO", "TZNAME" }, components[2].Properties);
        foreach (var vevent in components.Skip(3))
        {
            Assert.Equal(
                new[] { "UID", "DTSTAMP", "DTSTART;TZID=Asia/Seoul", "DTEND;TZID=Asia/Seoul", "SUMMARY", "DESCRIPTION" },
                vevent.Properties);
        }
    }

    [Fact]
    public void Sample_plan_exports_six_events_and_skips_the_lunch_break()
    {
        var result = RunSample();

        Assert.Equal("haru-2026-11-05.ics", result.Filename);
        Assert.Equal(6, result.EventCount);
        var lines = Logical(result.Ics);
        Assert.Equal(6, lines.Count(line => line == "BEGIN:VEVENT"));
        Assert.Equal(6, lines.Count(line => line == "END:VEVENT"));
        Assert.DoesNotContain(lines, line => line.StartsWith("UID:b3-", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line == "SUMMARY:점심");
        Assert.Contains("UID:b2-2026-11-05@haru", lines);
        Assert.Contains("SUMMARY:제안서 초안 작성", lines);
        Assert.Contains("DTSTART;TZID=Asia/Seoul:20261105T093500", lines);
        Assert.Contains("DTEND;TZID=Asia/Seoul:20261105T113500", lines);
        AssertWellFormed(result);
    }

    [Fact]
    public void Single_event_matches_the_expected_document_exactly()
    {
        var result = Run(Block("b2", "제안서 초안 작성", "09:35", "11:35", reason: "마감(17시)이 있는 가장 큰 일이라 집중이 잘 되는 오전에 둡니다."));

        Assert.Equal(1, result.EventCount);
        Assert.Equal(
            new[]
            {
                "BEGIN:VCALENDAR",
                "VERSION:2.0",
                "PRODID:-//Haru//Haru 하루설계//KO",
                "CALSCALE:GREGORIAN",
                "METHOD:PUBLISH",
                "X-WR-CALNAME:하루설계",
                "BEGIN:VTIMEZONE",
                "TZID:Asia/Seoul",
                "BEGIN:STANDARD",
                "DTSTART:19700101T000000",
                "TZOFFSETFROM:+0900",
                "TZOFFSETTO:+0900",
                "TZNAME:KST",
                "END:STANDARD",
                "END:VTIMEZONE",
                "BEGIN:VEVENT",
                "UID:b2-2026-11-05@haru",
                "DTSTAMP:20261105T000000Z",
                "DTSTART;TZID=Asia/Seoul:20261105T093500",
                "DTEND;TZID=Asia/Seoul:20261105T113500",
                "SUMMARY:제안서 초안 작성",
                @"DESCRIPTION:마감(17시)이 있는 가장 큰 일이라 집중이 잘 되는 오전에 둡니다.\n" + AiNotice,
                "END:VEVENT",
                "END:VCALENDAR",
            },
            Logical(result.Ics));
    }

    [Fact]
    public void Time_zone_is_Asia_Seoul_without_daylight_saving()
    {
        var lines = Logical(Run().Ics);

        Assert.Contains("BEGIN:VTIMEZONE", lines);
        Assert.Contains("TZID:Asia/Seoul", lines);
        Assert.Contains("TZOFFSETFROM:+0900", lines);
        Assert.Contains("TZOFFSETTO:+0900", lines);
        Assert.Contains("TZNAME:KST", lines);
        Assert.DoesNotContain("DAYLIGHT", Run(Block("a", "t", "09:00", "10:00")).Ics);
    }

    [Fact]
    public void Output_is_deterministic_and_never_reads_the_clock()
    {
        Assert.Equal(RunSample().Ics, RunSample().Ics);
        Assert.Contains("DTSTAMP:20261105T000000Z", Logical(RunSample().Ics));
        Assert.Contains("DTSTAMP:20270301T000000Z", Logical(RunWith("2027-03-01", null, Block("a", "t", "09:00", "10:00")).Ics));
    }

    [Fact]
    public void Every_line_ending_is_CRLF()
    {
        var ics = RunSample().Ics;

        Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
        var withoutCrlf = ics.Replace("\r\n", "");
        Assert.DoesNotContain('\r', withoutCrlf);
        Assert.DoesNotContain('\n', withoutCrlf);
    }

    [Fact]
    public void Long_korean_titles_fold_at_75_octets_and_never_split_a_character()
    {
        var title = string.Concat(Enumerable.Repeat("가나다라마바사", 9)); // 63 Hangul characters, 3 octets each

        var result = Run(Block("a", title, "09:00", "10:00"));

        var physical = Physical(result.Ics);
        var first = Array.FindIndex(physical, line => line.StartsWith("SUMMARY:", StringComparison.Ordinal));
        Assert.Equal("SUMMARY:" + title[..22], physical[first]);
        Assert.Equal(" " + title[22..46], physical[first + 1]);
        Assert.Equal(" " + title[46..], physical[first + 2]);
        Assert.Equal(74, Encoding.UTF8.GetByteCount(physical[first]));
        Assert.Contains("SUMMARY:" + title, Logical(result.Ics));
        AssertWellFormed(result);
    }

    [Theory]
    [InlineData(67, 1)]
    [InlineData(68, 2)]
    [InlineData(67 + 74, 2)]
    [InlineData(67 + 74 + 1, 3)]
    public void A_75_octet_line_is_kept_whole_and_a_76_octet_line_is_folded(int titleLength, int physicalLines)
    {
        var physical = Physical(Run(Block("a", new string('a', titleLength), "09:00", "10:00")).Ics);

        var summary = physical.SkipWhile(line => !line.StartsWith("SUMMARY:", StringComparison.Ordinal))
            .TakeWhile((line, index) => index == 0 || line.StartsWith(' '))
            .ToList();
        Assert.Equal(physicalLines, summary.Count);
        Assert.Equal(75, Encoding.UTF8.GetByteCount(summary[0]));
    }

    [Fact]
    public void Folding_keeps_emoji_and_mixed_width_text_intact_for_every_length()
    {
        const string unit = "가🙂a나🙂🙂다ab";
        var all = string.Concat(Enumerable.Repeat(unit, 30)).EnumerateRunes().Select(rune => rune.ToString()).ToArray();

        for (var length = 1; length <= 120; length++)
        {
            var title = string.Concat(all.Take(length));

            var result = Run(Block("a", title, "09:00", "10:00", reason: title));

            AssertWellFormed(result);
            Assert.Contains("SUMMARY:" + title, Logical(result.Ics));
            Assert.Contains("DESCRIPTION:" + title + @"\n" + AiNotice, Logical(result.Ics));
        }
    }

    [Fact]
    public void Backslash_semicolon_and_comma_are_escaped()
    {
        var lines = Logical(Run(Block("a", @"a,b;c\d", "09:00", "10:00", reason: @"x,y;z\w"), Block("b", @"\;", "11:00", "12:00")).Ics);

        Assert.Contains(@"SUMMARY:a\,b\;c\\d", lines);
        Assert.Contains(@"DESCRIPTION:x\,y\;z\\w\n" + AiNotice, lines);
        Assert.Contains(@"SUMMARY:\\\;", lines);
    }

    [Fact]
    public void The_calendar_name_is_escaped_and_defaults_to_하루설계()
    {
        Assert.Contains("X-WR-CALNAME:하루설계", Logical(Run().Ics));
        Assert.Contains("X-WR-CALNAME:하루설계", Logical(RunWith(Day, "  ").Ics));
        Assert.Contains(@"X-WR-CALNAME:내 일정\, 개인\;공유", Logical(RunWith(Day, "내 일정, 개인;공유").Ics));
    }

    [Fact]
    public void Description_always_carries_the_AI_notice()
    {
        var lines = Logical(Run(
            Block("a", "t", "09:00", "10:00"),
            Block("b", "t", "10:05", "11:00", reason: "   "),
            Block("c", "t", "11:05", "12:00", reason: "이유 하나")).Ics);

        Assert.Equal(2, lines.Count(line => line == "DESCRIPTION:" + AiNotice));
        Assert.Contains(@"DESCRIPTION:이유 하나\n" + AiNotice, lines);
    }

    [Fact]
    public void Title_line_breaks_cannot_inject_a_property()
    {
        var result = Run(Block("a", "x\r\nATTENDEE:evil", "09:00", "10:00"));

        Assert.DoesNotContain(Physical(result.Ics), line => line.StartsWith("ATTENDEE", StringComparison.Ordinal));
        Assert.DoesNotContain(Logical(result.Ics), line => line.StartsWith("ATTENDEE", StringComparison.Ordinal));
        Assert.Contains("SUMMARY:x ATTENDEE:evil", Logical(result.Ics));
        AssertWellFormed(result);
    }

    [Fact]
    public void Id_cannot_inject_through_the_UID()
    {
        var result = Run(Block("b1\r\nX-EVIL:1", "t", "09:00", "10:00"));

        Assert.Contains("UID:b1__X-EVIL_1-2026-11-05@haru", Logical(result.Ics));
        AssertWellFormed(result);
    }

    [Theory]
    [InlineData("x\r\nATTENDEE:evil")]
    [InlineData("x\nATTENDEE:evil")]
    [InlineData("x\rATTENDEE:evil")]
    [InlineData("x\u2028ATTENDEE:evil")]
    [InlineData("x\u2029ATTENDEE:evil")]
    [InlineData("x\u0085ATTENDEE:evil")]
    [InlineData("x\u001bATTENDEE:evil")]
    [InlineData("x\tATTENDEE:evil")]
    [InlineData("x\0ATTENDEE:evil")]
    [InlineData("ok\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:evil")]
    [InlineData("ok\r\nEND:VCALENDAR\r\nBEGIN:VCALENDAR\r\nMETHOD:CANCEL")]
    [InlineData("\r\nBEGIN:VEVENT")]
    [InlineData("x\r\n folded-continuation")]
    [InlineData("\\\r\nEND:VEVENT")]
    public void Injected_text_never_changes_the_structure_of_the_calendar(string payload)
    {
        var inTitle = Run(Block("a", payload, "09:00", "10:00"));
        var inReason = Run(Block("a", "t", "09:00", "10:00", reason: payload));
        var inId = Run(Block("a" + payload, "t", "09:00", "10:00"));
        var inCalendarName = RunWith(Day, payload, Block("a", "t", "09:00", "10:00"));

        foreach (var result in new[] { inTitle, inReason, inId, inCalendarName })
        {
            AssertWellFormed(result);
            Assert.Equal(1, result.EventCount);
            Assert.DoesNotContain(Physical(result.Ics), line => line.StartsWith("ATTENDEE", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Random_hostile_text_keeps_the_calendar_well_formed()
    {
        string[] tokens = ["a", "B", " ", ",", ";", "\\", ":", "=", "\r", "\n", "\r\n", "\u2028", "\u0085", "\t", "\0", "가", "나", "🙂", "BEGIN:VEVENT", "ATTENDEE:evil"];
        var random = new Random(20261105);
        string Hostile()
        {
            var text = new StringBuilder();
            for (var count = random.Next(0, 80); count > 0; count--)
            {
                var token = tokens[random.Next(tokens.Length)];
                if (text.Length + token.Length > 200)
                {
                    break;
                }

                text.Append(token);
            }

            return text.ToString();
        }

        for (var round = 0; round < 400; round++)
        {
            var outcome = IcsExporter.Export(
                Day,
                [Block($"id{round}", Hostile(), "09:00", "10:00", reason: Hostile()), Block($"other{round}", Hostile(), "11:00", "12:00")],
                Hostile());

            Assert.True(outcome.IsOk, outcome.Error);
            AssertWellFormed(outcome.Value!);
        }
    }

    [Fact]
    public void Break_blocks_are_not_exported()
    {
        var result = Run(
            Block("a", "집중", "09:00", "10:00"),
            Block("lunch", "점심", "12:00", "13:00", "break"),
            Block("f", "회의", "14:00", "15:00", "fixed"));

        Assert.Equal(2, result.EventCount);
        Assert.DoesNotContain("점심", result.Ics);
        Assert.DoesNotContain("UID:lunch", result.Ics);
        AssertWellFormed(result);
    }

    [Fact]
    public void No_exportable_blocks_gives_an_empty_but_valid_calendar()
    {
        var empty = Run();
        var onlyBreaks = Run(Block("lunch", "점심", "12:00", "13:00", "break"));

        foreach (var result in new[] { empty, onlyBreaks })
        {
            Assert.Equal(0, result.EventCount);
            Assert.Equal("haru-2026-11-05.ics", result.Filename);
            Assert.DoesNotContain("BEGIN:VEVENT", result.Ics);
            AssertWellFormed(result);
        }

        Assert.Equal(0, IcsExporter.Export(Day, null, null).Value!.EventCount);
    }

    [Theory]
    [InlineData("9:05", "23:59", "T090500", "T235900")]
    [InlineData("00:00", "00:01", "T000000", "T000100")]
    public void Times_are_written_in_local_Asia_Seoul_form(string start, string end, string expectedStart, string expectedEnd)
    {
        var lines = Logical(Run(Block("a", "t", start, end)).Ics);

        Assert.Contains($"DTSTART;TZID=Asia/Seoul:20261105{expectedStart}", lines);
        Assert.Contains($"DTEND;TZID=Asia/Seoul:20261105{expectedEnd}", lines);
    }

    [Fact]
    public void Filename_and_UID_use_the_day()
    {
        var result = RunWith("2027-01-09", null, Block(" b9 ", "t", "09:00", "10:00"));

        Assert.Equal("haru-2027-01-09.ics", result.Filename);
        Assert.Contains("UID:b9-2027-01-09@haru", Logical(result.Ics));
        Assert.Contains("DTSTART;TZID=Asia/Seoul:20270109T090000", Logical(result.Ics));
    }

    [Fact]
    public void Non_ascii_ids_are_made_safe_and_missing_titles_get_a_placeholder()
    {
        var lines = Logical(Run(Block("회의 1", null, "09:00", "10:00"), Block("b", "   ", "11:00", "12:00")).Ics);

        Assert.Contains("UID:___1-2026-11-05@haru", lines);
        Assert.Equal(2, lines.Count(line => line == "SUMMARY:(제목 없음)"));
    }

    [Fact]
    public void Kinds_are_matched_ignoring_case()
    {
        Assert.Equal(1, Run(Block("a", "t", "09:00", "10:00", "Fixed"), Block("b", "t", "11:00", "12:00", "BREAK")).EventCount);
    }

    [Fact]
    public void Limits_50_blocks_are_accepted_and_51_are_rejected()
    {
        ExportBlock[] Many(int count) => Enumerable.Range(0, count)
            .Select(i => Block($"b{i}", $"일 {i}", TimeUtil.Format(i * 15), TimeUtil.Format(i * 15 + 10)))
            .ToArray();

        var accepted = IcsExporter.Export(Day, Many(50), null);
        var rejected = IcsExporter.Export(Day, Many(51), null);

        Assert.Equal(50, accepted.Value!.EventCount);
        AssertWellFormed(accepted.Value);
        AssertFailed(rejected);
        Assert.Contains("50", rejected.Error);
    }

    [Fact]
    public void Limits_strings_of_200_characters_are_accepted_and_201_are_rejected()
    {
        var ok = new string('가', 200);
        var tooLong = new string('가', 201);

        AssertWellFormed(Run(Block("a", ok, "09:00", "10:00", reason: ok)));
        AssertWellFormed(RunWith(Day, ok, Block("a", "t", "09:00", "10:00")));
        AssertFailed(IcsExporter.Export(Day, [Block("a", tooLong, "09:00", "10:00")], null));
        AssertFailed(IcsExporter.Export(Day, [Block("a", "t", "09:00", "10:00", reason: tooLong)], null));
        AssertFailed(IcsExporter.Export(Day, [Block(tooLong, "t", "09:00", "10:00")], null));
        AssertFailed(IcsExporter.Export(Day, [], tooLong));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("2026-13-01")]
    [InlineData("2026-02-30")]
    [InlineData("20261105")]
    [InlineData("2026-11-5")]
    [InlineData("2026/11/05")]
    [InlineData("오늘")]
    [InlineData("2026-11-05T09:00")]
    public void A_bad_day_is_reported_as_a_korean_error(string? day)
    {
        AssertFailed(IcsExporter.Export(day, [Block("a", "t", "09:00", "10:00")], null));
    }

    [Fact]
    public void Bad_blocks_are_reported_as_a_korean_error_and_never_throw()
    {
        ExportBlock?[][] cases =
        [
            [null],
            [Block(" ", "t", "09:00", "10:00")],
            [Block("a", "t", "9am", "10:00")],
            [Block("a", "t", "09:00", "25:00")],
            [Block("a", "t", "10:00", "09:00")],
            [Block("a", "t", "10:00", "10:00")],
            [Block("a", "t", "09:00", "10:00", "meeting")],
            [Block("a", "t", "09:00", "10:00", "")],
            [Block("lunch", "점심", "13:00", "12:00", "break")],
            [Block("a", "t", "09:00", "10:00"), Block("a", "u", "11:00", "12:00")],
            [Block("a b", "t", "09:00", "10:00"), Block("a_b", "u", "11:00", "12:00")],
        ];

        foreach (var blocks in cases)
        {
            AssertFailed(IcsExporter.Export(Day, blocks, null));
        }
    }
}
