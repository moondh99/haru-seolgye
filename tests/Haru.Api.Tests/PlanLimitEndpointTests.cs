using System.Net;
using Haru.Api.Tests.Support;
using static Haru.Api.Tests.Support.ApiHttp;

namespace Haru.Api.Tests;

public class AiUnavailableEndpointTests
{
    private static Dictionary<string, string?> AiOff => new() { ["HARU_AI_ENABLED"] = "false" };

    [Fact]
    public async Task PostPlan_HostReportsAiDown_Returns503AiUnavailableAndRunsNoAgent()
    {
        using var factory = new HaruApiFactory(aiAvailable: false);

        var response = await factory.ClientFrom("10.1.0.1").PostAsync("/api/plan", Json(new { text = "보고서 쓰기" }));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("AI_UNAVAILABLE", await response.ErrorCodeAsync());
        Assert.Empty(factory.Runner.Calls);
    }

    [Fact]
    public async Task PostPlan_AiDisabledByConfiguration_Returns503WithTheRealHost()
    {
        using var factory = new HaruApiFactory(aiAvailable: true, AiOff, fakeHost: false);

        var response = await factory.ClientFrom("10.1.0.2").PostAsync("/api/plan", Json(new { text = "보고서 쓰기" }));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("AI_UNAVAILABLE", await response.ErrorCodeAsync());
        Assert.Empty(factory.Runner.Calls);
    }

    [Fact]
    public async Task GetConfig_AiDisabledByConfiguration_ReportsAiUnavailable()
    {
        using var factory = new HaruApiFactory(aiAvailable: true, AiOff, fakeHost: false);

        var config = await (await factory.ClientFrom("10.1.0.3").GetAsync("/api/config")).ReadJsonAsync();

        Assert.False(config.GetProperty("aiAvailable").GetBoolean());
    }

    [Fact]
    public async Task GetSample_AiDisabled_StillWorksOffline()
    {
        using var factory = new HaruApiFactory(aiAvailable: false);

        var response = await factory.ClientFrom("10.1.0.4").GetAsync("/api/sample");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostPlan_InvalidRequestWhileAiIsDown_ReportsTheInputProblemFirst()
    {
        using var factory = new HaruApiFactory(aiAvailable: false);

        var response = await factory.ClientFrom("10.1.0.5").PostAsync("/api/plan", Json(new { text = "" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INPUT_EMPTY", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task PostPlan_AiBackAgain_ServesAfterAnOutage()
    {
        using var factory = new HaruApiFactory(aiAvailable: false);
        var client = factory.ClientFrom("10.1.0.6");
        var first = await client.PostAsync("/api/plan", Json(new { text = "보고서 쓰기" }));

        factory.Host.Available = true;
        var second = await client.PostAsync("/api/plan", Json(new { text = "보고서 쓰기" }));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }
}

public class RateLimitEndpointTests
{
    private static Dictionary<string, string?> Setting(string key, string value) => new() { [key] = value };

    private static Task<HttpResponseMessage> Plan(HttpClient client, string text = "보고서 쓰기") =>
        client.PostAsync("/api/plan", Json(new { text }));

    [Fact]
    public async Task GetConfig_WindowSettings_AreReportedToTheClient()
    {
        using var factory = new HaruApiFactory(true, new()
        {
            ["HARU_PLANS_PER_WINDOW"] = "2",
            ["HARU_WINDOW_MINUTES"] = "7",
        });

        var limits = (await (await factory.ClientFrom("10.2.0.1").GetAsync("/api/config")).ReadJsonAsync()).GetProperty("limits");

        Assert.Equal(2, limits.GetProperty("plansPerWindow").GetInt32());
        Assert.Equal(7, limits.GetProperty("windowMinutes").GetInt32());
    }

    [Fact]
    public async Task PostPlan_OverThePerClientWindow_Returns429RateLimited()
    {
        using var factory = new HaruApiFactory(true, Setting("HARU_PLANS_PER_WINDOW", "2"));
        var client = factory.ClientFrom("10.2.1.1");

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage last = null!;
        for (var i = 0; i < 3; i++)
        {
            last = await Plan(client);
            statuses.Add(last.StatusCode);
        }

        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests }, statuses);
        Assert.Equal("RATE_LIMITED", await last.ErrorCodeAsync());
        Assert.Equal("60", last.Header("Retry-After"));
    }

    [Fact]
    public async Task PostPlan_RateLimitedRequest_RunsNoAgent()
    {
        using var factory = new HaruApiFactory(true, Setting("HARU_PLANS_PER_WINDOW", "1"));
        var client = factory.ClientFrom("10.2.2.1");
        await (await Plan(client)).Content.ReadAsStringAsync();
        var before = factory.Runner.Calls.Count;

        var limited = await Plan(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(before, factory.Runner.Calls.Count);
    }

    [Fact]
    public async Task PostPlan_AnotherClientAddress_HasItsOwnWindow()
    {
        using var factory = new HaruApiFactory(true, Setting("HARU_PLANS_PER_WINDOW", "1"));
        await (await Plan(factory.ClientFrom("10.2.3.1"))).Content.ReadAsStringAsync();

        var limited = await Plan(factory.ClientFrom("10.2.3.1"));
        var other = await Plan(factory.ClientFrom("10.2.3.2"));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task PostPlan_InvalidRequests_DoNotUseUpTheWindow()
    {
        using var factory = new HaruApiFactory(true, Setting("HARU_PLANS_PER_WINDOW", "1"));
        var client = factory.ClientFrom("10.2.4.1");

        for (var i = 0; i < 5; i++) await Plan(client, text: "");
        var response = await Plan(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostPlan_AiDown_DoesNotUseUpTheWindow()
    {
        using var factory = new HaruApiFactory(false, Setting("HARU_PLANS_PER_WINDOW", "1"));
        var client = factory.ClientFrom("10.2.5.1");

        await Plan(client);
        await Plan(client);
        var response = await Plan(client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task PostPlan_DailyCapReached_Returns429DailyCapForEveryone()
    {
        using var factory = new HaruApiFactory(true, Setting("HARU_DAILY_CAP", "1"));
        await (await Plan(factory.ClientFrom("10.2.6.1"))).Content.ReadAsStringAsync();

        var response = await Plan(factory.ClientFrom("10.2.6.2"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("DAILY_CAP", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task GetSample_DailyCapReached_StillServes()
    {
        using var factory = new HaruApiFactory(true, Setting("HARU_DAILY_CAP", "1"));
        await (await Plan(factory.ClientFrom("10.2.7.1"))).Content.ReadAsStringAsync();

        var sample = await factory.ClientFrom("10.2.7.2").GetAsync("/api/sample");

        Assert.Equal(HttpStatusCode.OK, sample.StatusCode);
    }

    [Fact]
    public async Task PostPlan_AllSlotsBusy_Returns503BusyUntilARunFinishes()
    {
        using var factory = new HaruApiFactory(true, Setting("HARU_MAX_CONCURRENT", "1"));
        var gate = new TaskCompletionSource();
        var happy = Scenario.StandardIntake();
        factory.Runner.Script("intake", async (_, _) =>
        {
            await gate.Task;
            return happy;
        });

        var running = Plan(factory.ClientFrom("10.2.8.1"));
        await WaitUntilAsync(() => factory.Runner.CountOf("intake") == 1);
        var busy = await Plan(factory.ClientFrom("10.2.8.2"));
        gate.SetResult();
        var finished = await running;
        var afterwards = await Plan(factory.ClientFrom("10.2.8.2"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        Assert.Equal("BUSY", await busy.ErrorCodeAsync());
        Assert.Equal("10", busy.Header("Retry-After"));
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        Assert.Equal(HttpStatusCode.OK, afterwards.StatusCode);
    }

    [Fact]
    public async Task PostPlan_RunThatFails_ReleasesItsSlot()
    {
        using var factory = new HaruApiFactory(true, Setting("HARU_MAX_CONCURRENT", "1"));
        factory.Runner.Script("intake", FakeAgentRunner.Fail());
        var client = factory.ClientFrom("10.2.9.1");

        for (var i = 0; i < 3; i++)
        {
            var response = await Plan(client);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("error", (await response.ReadSseAsync())[^1].Name);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition was not met in time");
            await Task.Delay(10);
        }
    }
}
