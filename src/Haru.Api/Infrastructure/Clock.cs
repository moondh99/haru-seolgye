using System.Globalization;
using System.Text;

namespace Haru.Api.Infrastructure;

public static class Clock
{
    public static readonly TimeSpan KstOffset = TimeSpan.FromHours(9);

    public static DateTimeOffset NowKst() => DateTimeOffset.UtcNow.ToOffset(KstOffset);

    public static string TodayKst() => NowKst().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool TryParseDay(string? s, out DateOnly day) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    public static string Weekday(string day)
    {
        if (!TryParseDay(day, out var d)) return "";
        return d.DayOfWeek switch
        {
            DayOfWeek.Monday => "월",
            DayOfWeek.Tuesday => "화",
            DayOfWeek.Wednesday => "수",
            DayOfWeek.Thursday => "목",
            DayOfWeek.Friday => "금",
            DayOfWeek.Saturday => "토",
            _ => "일",
        };
    }

    public static bool TryMinutes(string? hhmm, out int minutes)
    {
        minutes = 0;
        if (hhmm is not { Length: 5 } || hhmm[2] != ':') return false;
        if (!int.TryParse(hhmm.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var h)) return false;
        if (!int.TryParse(hhmm.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m)) return false;
        if (h > 23 || m > 59) return false;
        minutes = h * 60 + m;
        return true;
    }

    public static string Hhmm(int minutes) =>
        string.Create(CultureInfo.InvariantCulture, $"{minutes / 60:00}:{minutes % 60:00}");
}

public static class TextSanitizer
{
    // Zero-width and bidi control characters are a common way to hide instructions in text.
    private static bool IsHidden(char c) =>
        c is '\u200B' or '\u200C' or '\u200D' or '\u200E' or '\u200F' or '\u2060' or '\uFEFF'
          or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');

    public static string Clean(string? input, int maxChars, bool keepNewlines = false)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var sb = new StringBuilder(Math.Min(input.Length, maxChars));
        var lastSpace = false;
        foreach (var c in input)
        {
            if (IsHidden(c)) continue;
            if (c is '\n' or '\r' or '\t')
            {
                if (keepNewlines && c != '\t') { sb.Append('\n'); lastSpace = false; }
                else if (!lastSpace && sb.Length > 0) { sb.Append(' '); lastSpace = true; }
                continue;
            }
            if (char.IsControl(c)) continue;
            if (c == ' ')
            {
                if (lastSpace || sb.Length == 0) continue;
                lastSpace = true;
            }
            else lastSpace = false;
            sb.Append(c);
            if (sb.Length >= maxChars) break;
        }
        // Cutting at the limit must not leave half of an emoji.
        if (sb.Length > 0 && char.IsHighSurrogate(sb[^1])) sb.Length--;
        return sb.ToString().Trim();
    }
}
