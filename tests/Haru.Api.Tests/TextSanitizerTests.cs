using System.Text.Json;
using Haru.Api.Contracts;
using Haru.Api.Infrastructure;

namespace Haru.Api.Tests;

public class TextSanitizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Clean_NullOrEmpty_ReturnsEmpty(string? input) => Assert.Equal("", TextSanitizer.Clean(input, 80));

    [Theory]
    [InlineData('\u200B')] // zero width space
    [InlineData('\u200C')]
    [InlineData('\u200D')]
    [InlineData('\u200E')]
    [InlineData('\u200F')]
    [InlineData('\u2060')] // word joiner
    [InlineData('\uFEFF')] // BOM
    [InlineData('\u202A')]
    [InlineData('\u202B')]
    [InlineData('\u202C')]
    [InlineData('\u202D')]
    [InlineData('\u202E')] // right-to-left override
    [InlineData('\u2066')]
    [InlineData('\u2067')]
    [InlineData('\u2068')]
    [InlineData('\u2069')]
    public void Clean_HiddenOrBidiCharacter_IsRemoved(char hidden) =>
        Assert.Equal("보고서", TextSanitizer.Clean($"보{hidden}고{hidden}서{hidden}", 80));

    [Fact]
    public void Clean_ControlCharacters_AreRemoved() =>
        Assert.Equal("ab", TextSanitizer.Clean("a\u0000\u0007\u001B\u007F\u0085b", 80));

    [Fact]
    public void Clean_RepeatedSpaces_CollapseAndEdgesAreTrimmed() =>
        Assert.Equal("a b c", TextSanitizer.Clean("   a   b    c   ", 80));

    [Fact]
    public void Clean_NewlinesAndTabs_BecomeSingleSpacesByDefault() =>
        Assert.Equal("a b c", TextSanitizer.Clean("a\n\nb\t\tc", 80));

    [Fact]
    public void Clean_KeepNewlines_KeepsLineBreaksButNotTabs() =>
        Assert.Equal("a\nb c", TextSanitizer.Clean("a\nb\tc", 80, keepNewlines: true));

    [Fact]
    public void Clean_TextLongerThanLimit_IsCutAtTheLimit()
    {
        var cleaned = TextSanitizer.Clean(new string('가', 500), 80);

        Assert.Equal(80, cleaned.Length);
    }

    [Fact]
    public void Clean_TextAtLimit_IsKept() => Assert.Equal(new string('a', 80), TextSanitizer.Clean(new string('a', 80), 80));

    [Fact]
    public void Clean_HiddenCharactersDoNotCountTowardTheLimit() =>
        Assert.Equal(new string('a', 10), TextSanitizer.Clean(string.Concat(Enumerable.Repeat("a\u200B", 10)), 10));

    [Fact]
    public void Clean_Markup_IsKeptAsPlainText() =>
        Assert.Equal("<img src=x onerror=alert(1)>", TextSanitizer.Clean("<img src=x onerror=alert(1)>", 80));

    [Fact]
    public void Clean_EmojiCutByTheLimit_DoesNotLeaveALoneSurrogate()
    {
        var cleaned = TextSanitizer.Clean(new string('a', 79) + "😀 끝", 80);

        Assert.DoesNotContain(cleaned, char.IsSurrogate);
        Assert.Equal(cleaned, JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(cleaned, HaruJson.Options)));
    }
}
