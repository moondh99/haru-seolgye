using Haru.Api.Mcp;

namespace Haru.Api.Tests;

public class McpGatewayTruncateTests
{
    [Fact]
    public void Truncate_TextWithinTheLimit_IsUnchanged() => Assert.Equal("abc", McpGateway.Truncate("abc", 3));

    [Fact]
    public void Truncate_LongerText_IsCutAndMarked() => Assert.Equal("ab…", McpGateway.Truncate("abcdef", 2));

    [Fact]
    public void Truncate_CutInsideAnEmoji_DropsTheWholeEmoji()
    {
        // The emoji uses two UTF-16 units at index 2 and 3, so a cut at 3 would leave half of it.
        var result = McpGateway.Truncate("ab😀cd", 3);

        Assert.Equal("ab…", result);
    }

    [Fact]
    public void Truncate_CutRightAfterAnEmoji_KeepsIt() => Assert.Equal("ab😀…", McpGateway.Truncate("ab😀cd", 4));
}
