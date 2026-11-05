using System.Globalization;
using System.Text.RegularExpressions;

namespace Haru.Mcp.Logic;

/// <summary>Shared limits and parsing helpers. All times are KST wall-clock values.</summary>
public static partial class TimeUtil
{
    public const int MaxText = 200;
    public const int MaxItems = 50;

    [GeneratedRegex(@"^([01]?[0-9]|2[0-3]):([0-5][0-9])$")]
    private static partial Regex HhmmRegex();

    [GeneratedRegex(@"[\p{Cc}\u2028\u2029]+")]
    private static partial Regex ControlRunRegex();

    /// <summary>Parses "HH:mm" (a one-digit hour such as "9:30" is tolerated) into minutes after midnight.</summary>
    public static bool TryParseHhmm(string? text, out int minutes)
    {
        minutes = 0;
        var match = text is null || text.Length > 8 ? null : HhmmRegex().Match(text.Trim());
        if (match is not { Success: true })
        {
            return false;
        }

        minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                  + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }

    public static string Format(int minutes) =>
        string.Create(CultureInfo.InvariantCulture, $"{minutes / 60:00}:{minutes % 60:00}");

    public static bool TryParseDay(string? text, out DateOnly day)
    {
        day = default;
        return text is { Length: <= 10 } &&
               DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    public static string FormatDay(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool TooLong(string? text, int max = MaxText) => text is not null && text.Length > max;

    /// <summary>Replaces control characters (including CR/LF) with a space and trims; the result is single-line text.</summary>
    public static string SingleLine(string? text) => ControlRunRegex().Replace(text ?? "", " ").Trim();

    /// <summary>Single-line, shortened text that is safe to echo inside a user-facing message.</summary>
    public static string ForMessage(string? text, int max = 30)
    {
        var clean = SingleLine(text);
        return clean.Length <= max ? clean : string.Concat(clean.AsSpan(0, max), "…");
    }
}
