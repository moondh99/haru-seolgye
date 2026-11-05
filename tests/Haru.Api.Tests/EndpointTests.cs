using System.Net;
using System.Text.Json;
using Haru.Api.Infrastructure;
using Haru.Api.Tests.Support;
using static Haru.Api.Tests.Support.ApiHttp;

namespace Haru.Api.Tests;

public class ReadEndpointTests(HaruApiFactory factory) : IClassFixture<HaruApiFactory>
{
    private HttpClient Client => factory.ClientFrom("10.0.1.1");

    [Fact]
    public async Task GetConfig_Default_ReportsLimitsDefaultsAndAvailability()
    {
        var response = await Client.GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var config = await response.ReadJsonAsync();
        Assert.True(config.GetProperty("aiAvailable").GetBoolean());
        Assert.Equal(Clock.TodayKst(), config.GetProperty("today").GetString());
        Assert.Equal("1.0.0", config.GetProperty("version").GetString());

        var limits = config.GetProperty("limits");
        Assert.Equal(3000, limits.GetProperty("maxInputChars").GetInt32());
        Assert.Equal(8, limits.GetProperty("plansPerWindow").GetInt32());
        Assert.Equal(10, limits.GetProperty("windowMinutes").GetInt32());

        var defaults = config.GetProperty("defaults");
        Assert.Equal(new[] { "09:00", "18:00", "12:00", "13:00" },
            new[] { "workStart", "workEnd", "lunchStart", "lunchEnd" }.Select(n => defaults.GetProperty(n).GetString()));
    }

    [Fact]
    public async Task GetConfig_Fields_MatchTheContractNames()
    {
        var config = await (await Client.GetAsync("/api/config")).ReadJsonAsync();

        Assert.Equal(new[] { "aiAvailable", "defaults", "limits", "today", "version" },
            config.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task GetSample_Always_ReturnsTheOfflinePlan()
    {
        var response = await Client.GetAsync("/api/sample");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sample = await response.ReadJsonAsync();
        Assert.False(string.IsNullOrWhiteSpace(sample.GetProperty("inputText").GetString()));
        var plan = sample.GetProperty("plan");
        var meta = plan.GetProperty("meta");
        Assert.Equal("offline-sample", meta.GetProperty("source").GetString());
        Assert.False(meta.GetProperty("aiGenerated").GetBoolean());
        Assert.True(plan.GetProperty("validation").GetProperty("valid").GetBoolean());
        Assert.NotEmpty(plan.GetProperty("blocks").EnumerateArray());
    }

    [Fact]
    public async Task GetSample_RecordedRun_WhenShippedIsAFinishedRunWithoutErrors()
    {
        var sample = await (await Client.GetAsync("/api/sample")).ReadJsonAsync();
        if (!sample.TryGetProperty("events", out var recorded) || recorded.ValueKind != JsonValueKind.Array) return;

        var names = recorded.EnumerateArray().Select(e => e.GetProperty("event").GetString()).ToList();
        Assert.Equal("run_started", names[0]);
        Assert.Equal(new[] { "plan", "done" }, names.TakeLast(2));
        Assert.DoesNotContain("error", names);
        Assert.All(recorded.EnumerateArray(), e => Assert.True(e.GetProperty("t").GetInt32() >= 0));
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task GetHealth_Probe_ReturnsHealthy(string path)
    {
        var response = await Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetUnknownRoute_Returns404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/does-not-exist")).StatusCode);

    [Theory]
    [InlineData("/api/plan")]
    [InlineData("/api/export")]
    public async Task Get_OnAPostOnlyRoute_Returns405(string path) =>
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Client.GetAsync(path)).StatusCode);

    [Fact]
    public async Task Post_OnAReadOnlyRoute_Returns405() =>
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Client.PostAsync("/api/config", Json(new { }))).StatusCode);
}

public class PlanEndpointTests(HaruApiFactory factory) : IClassFixture<HaruApiFactory>
{
    private HttpClient Client => factory.ClientFrom($"10.0.2.{Interlocked.Increment(ref _next)}");

    private static int _next;

    // validation: contract error codes

    [Theory]
    [InlineData("{\"text\":\"\"}")]
    [InlineData("{\"text\":\"   \\n\\t  \"}")]
    [InlineData("{\"text\":\"\\u200b\\u202e\"}")]
    [InlineData("{\"text\":null}")]
    [InlineData("{}")]
    public async Task PostPlan_BlankText_Returns400InputEmpty(string body)
    {
        var response = await Client.PostAsync("/api/plan", Raw(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INPUT_EMPTY", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task PostPlan_TextOverTheLimit_Returns400InputTooLong()
    {
        var response = await Client.PostAsync("/api/plan", Json(new { text = new string('가', 3001) }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INPUT_TOO_LONG", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task PostPlan_TextAtTheLimit_IsAccepted()
    {
        var response = await Client.PostAsync("/api/plan", Json(new { text = new string('가', 3000) }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("{\"text\": 5}")]
    public async Task PostPlan_BodyThatIsNotAPlanRequest_Returns400BadRequest(string body)
    {
        var response = await Client.PostAsync("/api/plan", Raw(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("BAD_REQUEST", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task PostPlan_NotJsonContentType_Returns400BadRequest()
    {
        var response = await Client.PostAsync("/api/plan", Raw("{\"text\":\"a\"}", "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("BAD_REQUEST", await response.ErrorCodeAsync());
    }

    [Theory]
    [InlineData("2020-01-01", null, null)]
    [InlineData("2999-01-01", null, null)]
    [InlineData("11/05/2026", null, null)]
    [InlineData(null, "9:00", null)]
    [InlineData(null, null, "25:00")]
    [InlineData(null, "10:00", "10:30")]
    public async Task PostPlan_BadDayOrWorkWindow_Returns400BadRequest(string? day, string? workStart, string? workEnd)
    {
        var response = await Client.PostAsync("/api/plan", Json(new { text = "보고서 쓰기", day, workStart, workEnd }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("BAD_REQUEST", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task PostPlan_Error_HasTheContractShape()
    {
        var response = await Client.PostAsync("/api/plan", Json(new { text = "" }));

        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var error = (await response.ReadJsonAsync()).GetProperty("error");
        Assert.Equal(new[] { "code", "message" }, error.EnumerateObject().Select(p => p.Name).Order());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task PostPlan_RejectedRequests_NeverReachAnAgent()
    {
        var before = factory.Runner.Calls.Count;

        await Client.PostAsync("/api/plan", Json(new { text = "" }));
        await Client.PostAsync("/api/plan", Json(new { text = new string('a', 3001) }));
        await Client.PostAsync("/api/plan", Raw("{not json"));

        Assert.Equal(before, factory.Runner.Calls.Count);
    }

    // success: the SSE contract

    [Fact]
    public async Task PostPlan_ValidRequest_StreamsTheRunAsServerSentEvents()
    {
        var response = await Client.PostAsync("/api/plan", Json(new { text = Scenario.Text }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var cacheControl = response.Header("Cache-Control");
        Assert.Contains("no-cache", cacheControl);
        Assert.Contains("no-transform", cacheControl);
        var events = await response.ReadSseAsync();
        Assert.Equal("run_started", events[0].Name);
        Assert.Equal(new[] { "plan", "done" }, events.Select(e => e.Name).TakeLast(2));
        Assert.DoesNotContain(events, e => e.Name == "error");
    }

    [Fact]
    public async Task PostPlan_ValidRequest_ReturnsAnAiPlanForTheRequestedDay()
    {
        var day = DateOnly.ParseExact(Clock.TodayKst(), "yyyy-MM-dd").AddDays(1).ToString("yyyy-MM-dd");

        var events = await (await Client.PostAsync("/api/plan", Json(new { text = Scenario.Text, day }))).ReadSseAsync();

        var plan = events.Single(e => e.Name == "plan").Data;
        Assert.Equal(day, plan.GetProperty("day").GetString());
        Assert.Equal("ai", plan.GetProperty("meta").GetProperty("source").GetString());
        Assert.True(plan.GetProperty("meta").GetProperty("aiGenerated").GetBoolean());
        Assert.True(plan.GetProperty("validation").GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task PostPlan_WorkWindowAndLunch_ReachTheScheduler()
    {
        var response = await Client.PostAsync("/api/plan",
            Json(new { text = "보고서 쓰기", workStart = "08:30", workEnd = "17:30", lunchStart = "11:30", lunchEnd = "12:30" }));
        await response.Content.ReadAsStringAsync();

        var prompt = JsonDocument.Parse(factory.Runner.PromptsOf("scheduler").Last()).RootElement;
        Assert.Equal(("08:30", "17:30"), (prompt.GetProperty("workStart").GetString(), prompt.GetProperty("workEnd").GetString()));
    }

    [Fact]
    public async Task PostPlan_EmptyLunchStrings_MeanNoLunchBreak()
    {
        var response = await Client.PostAsync("/api/plan",
            Json(new { text = "보고서 쓰기", lunchStart = "", lunchEnd = "" }));
        var plan = (await response.ReadSseAsync()).Single(e => e.Name == "plan").Data;

        Assert.DoesNotContain(plan.GetProperty("blocks").EnumerateArray(), b => b.GetProperty("kind").GetString() == "break");
    }

    [Fact]
    public async Task PostPlan_DefaultLunch_AddsABreakBlock()
    {
        var response = await Client.PostAsync("/api/plan", Json(new { text = "보고서 쓰기" }));
        var plan = (await response.ReadSseAsync()).Single(e => e.Name == "plan").Data;

        Assert.Contains(plan.GetProperty("blocks").EnumerateArray(), b => b.GetProperty("kind").GetString() == "break");
    }

    [Fact]
    public async Task PostPlan_SuccessfulRun_NeverCallsTheExportTool()
    {
        await (await Client.PostAsync("/api/plan", Json(new { text = Scenario.Text }))).Content.ReadAsStringAsync();

        Assert.Equal(0, factory.Mcp.CountOf("export_ics"));
        Assert.True(factory.Mcp.CountOf("validate_schedule") > 0);
    }
}
