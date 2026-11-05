using Haru.Api.Config;
using Haru.Api.Infrastructure;
using Microsoft.Extensions.Configuration;
using static Haru.Api.Infrastructure.UsageLimiter;

namespace Haru.Api.Tests;

public class UsageLimiterTests
{
    private sealed class FakeClock
    {
        // 12:00 in Korea, far from the midnight reset
        public DateTimeOffset Value { get; set; } = new(2026, 11, 5, 3, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => Value += by;
    }

    private static (UsageLimiter Limiter, FakeClock Clock) Make(int perWindow = 3, int concurrent = 10, int daily = 1000, int windowMinutes = 10)
    {
        var clock = new FakeClock();
        var options = new HaruOptions { PlansPerWindow = perWindow, MaxConcurrent = concurrent, DailyCap = daily, WindowMinutes = windowMinutes };
        return (new UsageLimiter(options) { Now = () => clock.Value }, clock);
    }

    private static Verdict Enter(UsageLimiter limiter, string key = "a")
    {
        var (verdict, lease) = limiter.TryEnter(key);
        lease?.Dispose();
        return verdict;
    }

    // per-client window

    [Fact]
    public void TryEnter_RequestsWithinTheLimit_AreAllowed()
    {
        var (limiter, _) = Make(perWindow: 3);

        Assert.All(Enumerable.Range(0, 3), _ => Assert.Equal(Verdict.Ok, Enter(limiter)));
    }

    [Fact]
    public void TryEnter_OneRequestOverTheLimit_IsRateLimitedWithoutALease()
    {
        var (limiter, _) = Make(perWindow: 3);
        for (var i = 0; i < 3; i++) Enter(limiter);

        var (verdict, lease) = limiter.TryEnter("a");

        Assert.Equal(Verdict.RateLimited, verdict);
        Assert.Null(lease);
    }

    [Fact]
    public void TryEnter_AnotherClient_HasItsOwnWindow()
    {
        var (limiter, _) = Make(perWindow: 1);
        Enter(limiter, "a");

        Assert.Equal(Verdict.RateLimited, Enter(limiter, "a"));
        Assert.Equal(Verdict.Ok, Enter(limiter, "b"));
    }

    [Fact]
    public async Task TryEnter_ParallelBurstFromOneClient_NeverPassesTheWindowLimit()
    {
        var (limiter, _) = Make(perWindow: 3, concurrent: 64);
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 48).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return limiter.TryEnter("a").Verdict;
        })).ToArray();
        start.Set();

        var verdicts = await Task.WhenAll(tasks);

        Assert.Equal(3, verdicts.Count(v => v == Verdict.Ok));
        Assert.Equal(45, verdicts.Count(v => v == Verdict.RateLimited));
    }

    [Fact]
    public async Task TryEnter_ParallelBurstFromManyClients_NeverPassesTheDailyCap()
    {
        var (limiter, _) = Make(perWindow: 100, concurrent: 64, daily: 5);
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 48).Select(i => Task.Run(() =>
        {
            start.Wait();
            return limiter.TryEnter($"client-{i}").Verdict;
        })).ToArray();
        start.Set();

        var verdicts = await Task.WhenAll(tasks);

        Assert.Equal(5, verdicts.Count(v => v == Verdict.Ok));
        Assert.Equal(43, verdicts.Count(v => v == Verdict.DailyCap));
    }

    [Fact]
    public void TryEnter_JustBeforeTheWindowEnds_IsStillRateLimited()
    {
        var (limiter, clock) = Make(perWindow: 1, windowMinutes: 10);
        Enter(limiter);

        clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));

        Assert.Equal(Verdict.RateLimited, Enter(limiter));
    }

    [Fact]
    public void TryEnter_AfterTheWindowEnds_AllowsTheClientAgain()
    {
        var (limiter, clock) = Make(perWindow: 1, windowMinutes: 10);
        Enter(limiter);

        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        Assert.Equal(Verdict.Ok, Enter(limiter));
        Assert.Equal(Verdict.RateLimited, Enter(limiter));
    }

    [Fact]
    public void TryEnter_Window_SlidesSoOnlyTheOldestRequestExpiresFirst()
    {
        var (limiter, clock) = Make(perWindow: 3, windowMinutes: 10);
        Enter(limiter); // minute 0
        clock.Advance(TimeSpan.FromMinutes(5));
        Enter(limiter); // minute 5
        clock.Advance(TimeSpan.FromMinutes(4));
        Enter(limiter); // minute 9
        Assert.Equal(Verdict.RateLimited, Enter(limiter));

        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1)); // minute 10:01, only the first one expired

        Assert.Equal(Verdict.Ok, Enter(limiter));
        Assert.Equal(Verdict.RateLimited, Enter(limiter));
    }

    [Fact]
    public void TryEnter_RateLimitedRequests_DoNotExtendTheWindow()
    {
        var (limiter, clock) = Make(perWindow: 1, windowMinutes: 10);
        Enter(limiter);
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(Verdict.RateLimited, Enter(limiter));

        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));

        Assert.Equal(Verdict.Ok, Enter(limiter));
    }

    // concurrency cap

    [Fact]
    public void TryEnter_ConcurrencyCapReached_ReturnsBusyUntilALeaseIsReleased()
    {
        var (limiter, _) = Make(concurrent: 2);
        var first = limiter.TryEnter("a").Lease;
        var second = limiter.TryEnter("b").Lease;

        Assert.Equal(Verdict.Busy, limiter.TryEnter("c").Verdict);

        first!.Dispose();
        Assert.Equal(Verdict.Ok, limiter.TryEnter("c").Verdict);
        second!.Dispose();
    }

    [Fact]
    public void TryEnter_BusyRejection_DoesNotUseUpTheClientsWindow()
    {
        var (limiter, _) = Make(perWindow: 2, concurrent: 1);
        var held = limiter.TryEnter("a").Lease;
        Assert.Equal(Verdict.Busy, limiter.TryEnter("a").Verdict);
        held!.Dispose();

        Assert.Equal(Verdict.Ok, Enter(limiter, "a"));
        Assert.Equal(Verdict.RateLimited, Enter(limiter, "a"));
    }

    [Fact]
    public void Lease_DisposedTwice_ReleasesOnlyOneSlot()
    {
        var (limiter, _) = Make(concurrent: 2);
        var a = limiter.TryEnter("a").Lease!;
        limiter.TryEnter("b");
        a.Dispose();
        a.Dispose();

        Assert.Equal(Verdict.Ok, limiter.TryEnter("c").Verdict);
        Assert.Equal(Verdict.Busy, limiter.TryEnter("d").Verdict);
    }

    [Fact]
    public void TryEnter_ManyParallelClients_NeverExceedTheConcurrencyCap()
    {
        var (limiter, _) = Make(concurrent: 3);
        var verdicts = new Verdict[60];

        Parallel.For(0, verdicts.Length, i => verdicts[i] = limiter.TryEnter($"client-{i}").Verdict);

        Assert.Equal(3, verdicts.Count(v => v == Verdict.Ok));
        Assert.Equal(57, verdicts.Count(v => v == Verdict.Busy));
    }

    // daily cap

    [Fact]
    public void TryEnter_DailyCapReached_RejectsEveryClient()
    {
        var (limiter, _) = Make(daily: 2);

        Assert.Equal(Verdict.Ok, Enter(limiter, "a"));
        Assert.Equal(Verdict.Ok, Enter(limiter, "b"));
        Assert.Equal(Verdict.DailyCap, Enter(limiter, "c"));
        Assert.Equal(Verdict.DailyCap, Enter(limiter, "a"));
    }

    [Fact]
    public void TryEnter_AfterKoreanMidnight_StartsANewDailyCount()
    {
        var (limiter, clock) = Make(daily: 1);
        Enter(limiter, "a");

        clock.Value = new DateTimeOffset(2026, 11, 5, 14, 59, 0, TimeSpan.Zero); // 23:59 in Korea
        Assert.Equal(Verdict.DailyCap, Enter(limiter, "b"));

        clock.Value = new DateTimeOffset(2026, 11, 5, 15, 0, 0, TimeSpan.Zero); // 00:00 in Korea
        Assert.Equal(Verdict.Ok, Enter(limiter, "c"));
        Assert.Equal(Verdict.DailyCap, Enter(limiter, "d"));
    }

    [Fact]
    public void TryEnter_RateLimitedRequest_DoesNotCountTowardsTheDailyCap()
    {
        var (limiter, _) = Make(perWindow: 1, daily: 2);
        Enter(limiter, "a");
        Assert.Equal(Verdict.RateLimited, Enter(limiter, "a"));

        Assert.Equal(Verdict.Ok, Enter(limiter, "b"));
        Assert.Equal(Verdict.DailyCap, Enter(limiter, "c"));
    }

    [Fact]
    public void TryEnter_BusyRequest_DoesNotCountTowardsTheDailyCap()
    {
        var (limiter, _) = Make(concurrent: 1, daily: 2);
        var held = limiter.TryEnter("a").Lease;
        Assert.Equal(Verdict.Busy, limiter.TryEnter("b").Verdict);
        held!.Dispose();

        Assert.Equal(Verdict.Ok, Enter(limiter, "b"));
        Assert.Equal(Verdict.DailyCap, Enter(limiter, "c"));
    }

    // order of the checks

    [Fact]
    public void TryEnter_RateLimitAndDailyCapBothHit_ReportsTheRateLimit()
    {
        var (limiter, _) = Make(perWindow: 1, daily: 1);
        Enter(limiter, "a");

        Assert.Equal(Verdict.RateLimited, Enter(limiter, "a"));
    }

    [Fact]
    public void TryEnter_DailyCapAndBusyBothHit_ReportsTheDailyCap()
    {
        var (limiter, _) = Make(concurrent: 1, daily: 1);
        using var held = limiter.TryEnter("a").Lease;

        Assert.Equal(Verdict.DailyCap, limiter.TryEnter("b").Verdict);
    }

    [Fact]
    public void TryEnter_RateLimitAndBusyBothHit_ReportsTheRateLimit()
    {
        var (limiter, _) = Make(perWindow: 1, concurrent: 1);
        using var held = limiter.TryEnter("a").Lease;

        Assert.Equal(Verdict.RateLimited, limiter.TryEnter("a").Verdict);
    }

    [Fact]
    public void Window_ReturnsTheConfiguredMinutes() => Assert.Equal(7, Make(windowMinutes: 7).Limiter.Window);
}

public class HaruOptionsTests
{
    private static HaruOptions From(params (string Key, string? Value)[] settings) =>
        HaruOptions.From(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
            .Build());

    [Fact]
    public void From_EmptyConfiguration_UsesTheDefaults() => Assert.Equal(new HaruOptions(), From());

    [Fact]
    public void Defaults_AreTheDocumentedLimits()
    {
        var o = new HaruOptions();

        Assert.Equal((8, 10, 3, 250), (o.PlansPerWindow, o.WindowMinutes, o.MaxConcurrent, o.DailyCap));
        Assert.Equal((120, 60, 5, 0.85), (o.PlanTimeoutSeconds, o.AgentTimeoutSeconds, o.BufferMinutes, o.MaxLoad));
        Assert.Equal(3000, o.MaxInputChars);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("true", true)]
    [InlineData(null, true)]
    public void From_AiEnabledSetting_OnlyFalseTurnsTheAiOff(string? value, bool expected) =>
        Assert.Equal(expected, From(("HARU_AI_ENABLED", value)).AiEnabled);

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("3.5")]
    [InlineData("")]
    [InlineData(null)]
    public void From_NumberThatIsNotAPositiveInteger_KeepsTheDefault(string? value)
    {
        var o = From(("HARU_PLANS_PER_WINDOW", value), ("HARU_DAILY_CAP", value), ("HARU_PLAN_TIMEOUT_SECONDS", value));

        Assert.Equal((8, 250, 120), (o.PlansPerWindow, o.DailyCap, o.PlanTimeoutSeconds));
    }

    [Fact]
    public void From_PositiveNumbers_AreUsed()
    {
        var o = From(("HARU_PLANS_PER_WINDOW", "2"), ("HARU_WINDOW_MINUTES", "1"), ("HARU_MAX_CONCURRENT", "7"),
            ("HARU_DAILY_CAP", "11"), ("HARU_PLAN_TIMEOUT_SECONDS", "30"), ("HARU_AGENT_TIMEOUT_SECONDS", "15"));

        Assert.Equal((2, 1, 7, 11, 30, 15),
            (o.PlansPerWindow, o.WindowMinutes, o.MaxConcurrent, o.DailyCap, o.PlanTimeoutSeconds, o.AgentTimeoutSeconds));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unset")]
    [InlineData("UNSET")]
    public void From_EmptyOrPlaceholderToken_MeansNoToken(string? value) =>
        Assert.Null(From(("COPILOT_GITHUB_TOKEN", value)).CopilotToken);

    [Fact]
    public void From_Token_IsTrimmed() => Assert.Equal("test-token", From(("COPILOT_GITHUB_TOKEN", "  test-token \n")).CopilotToken);

    [Theory]
    [InlineData("http://mcp:8080", "http://mcp:8080/mcp")]
    [InlineData("http://mcp:8080/", "http://mcp:8080/mcp")]
    [InlineData("http://mcp:8080/mcp", "http://mcp:8080/mcp")]
    [InlineData("http://mcp:8080/mcp/", "http://mcp:8080/mcp")]
    public void From_McpUrl_AlwaysEndsWithTheMcpPath(string url, string expected) =>
        Assert.Equal(expected, From(("MCP_URL", url)).McpUrl);

    [Fact]
    public void From_ServiceDiscoveryAddress_IsUsedWhenMcpUrlIsMissing() =>
        Assert.Equal("http://discovered:9000/mcp", From(("services:mcp:http:0", "http://discovered:9000")).McpUrl);

    [Fact]
    public void From_McpUrlAndServiceDiscovery_PrefersTheExplicitUrl() =>
        Assert.Equal("http://explicit/mcp", From(("MCP_URL", "http://explicit"), ("services:mcp:http:0", "http://discovered")).McpUrl);

    [Fact]
    public void From_ModelAndEffortNames_AreTrimmedAndBlankKeepsTheDefault()
    {
        var o = From(("HARU_MODEL_FAST", " fast-x "), ("HARU_MODEL_SMART", "  "), ("HARU_EFFORT_FAST", "high"));

        Assert.Equal(("fast-x", new HaruOptions().ModelSmart, "high"), (o.ModelFast, o.ModelSmart, o.EffortFast));
    }
}
