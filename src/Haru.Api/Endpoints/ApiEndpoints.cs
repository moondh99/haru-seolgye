using System.Text;
using System.Text.Json;
using Haru.Api.Config;
using Haru.Api.Contracts;
using Haru.Api.Copilot;
using Haru.Api.Infrastructure;
using Haru.Api.Mcp;
using Haru.Api.Workflow;

namespace Haru.Api.Endpoints;

public static class ApiEndpoints
{
    public static void MapHaruApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/config", GetConfig);
        api.MapGet("/sample", (SampleStore store) => Results.Json(store.Sample, HaruJson.Options));
        api.MapPost("/plan", PostPlan);
        api.MapPost("/export", PostExport);
    }

    private static async Task<IResult> GetConfig(HaruOptions o, CopilotHost host, CancellationToken ct) =>
        Results.Json(new
        {
            aiAvailable = await host.IsAvailableAsync(ct),
            today = Clock.TodayKst(),
            limits = new { maxInputChars = o.MaxInputChars, plansPerWindow = o.PlansPerWindow, windowMinutes = o.WindowMinutes },
            defaults = new
            {
                workStart = PlanInputs.DefaultWorkStart,
                workEnd = PlanInputs.DefaultWorkEnd,
                lunchStart = PlanInputs.DefaultLunchStart,
                lunchEnd = PlanInputs.DefaultLunchEnd,
            },
            version = o.Version,
        }, HaruJson.Options);

    private static async Task<IResult> PostPlan(
        HttpContext http, HaruOptions o, CopilotHost host, UsageLimiter limiter, PlanService service)
    {
        var ct = http.RequestAborted;
        var request = await ReadAsync<PlanRequest>(http);
        var today = DateOnly.ParseExact(Clock.TodayKst(), "yyyy-MM-dd");
        var (input, problem) = PlanInputs.Validate(request, o, today);
        if (problem is not null) return Error(problem);

        if (!await host.IsAvailableAsync(ct))
            return Error(new ApiError(503, "AI_UNAVAILABLE", "지금은 AI를 쓸 수 없습니다. '샘플로 체험'으로 화면을 먼저 확인해 보세요."));

        var (verdict, lease) = limiter.TryEnter(ClientKey(http));
        switch (verdict)
        {
            case UsageLimiter.Verdict.RateLimited:
                http.Response.Headers.RetryAfter = "60";
                return Error(new ApiError(429, "RATE_LIMITED", "요청이 너무 많습니다. 잠시 후 다시 시도해 주세요."));
            case UsageLimiter.Verdict.DailyCap:
                return Error(new ApiError(429, "DAILY_CAP", "오늘 사용 한도에 도달했습니다. 내일 다시 이용해 주세요."));
            case UsageLimiter.Verdict.Busy:
                http.Response.Headers.RetryAfter = "10";
                return Error(new ApiError(503, "BUSY", "다른 계획을 만드는 중입니다. 잠시 후 다시 시도해 주세요."));
        }

        using (lease)
        {
            await using var sse = new SseStream(http.Response);
            await sse.StartAsync(ct);
            await service.RunAsync(input!, sse, ct);
            await sse.CompleteAsync();
        }
        return Results.Empty;
    }

    private static async Task<IResult> PostExport(HttpContext http, IMcpGateway mcp, HaruOptions o)
    {
        var ct = http.RequestAborted;
        var request = await ReadAsync<ExportRequest>(http);
        var today = DateOnly.ParseExact(Clock.TodayKst(), "yyyy-MM-dd");
        var (day, workStart, workEnd, blocks, problem) = ExportInputs.Validate(request, today);
        if (problem is not null) return Error(problem);

        try
        {
            var check = await mcp.CallAsync("validate_schedule", new
            {
                workStart,
                workEnd,
                blocks = blocks.Select(b => new { id = b.Id, title = b.Title, start = b.Start, end = b.End, kind = b.Kind }),
                bufferMinutes = o.BufferMinutes,
                maxLoad = 1.0,
            }, ct);

            var report = check.Deserialize<ValidationReport>(HaruJson.Lenient)
                         ?? throw new McpToolException("validate_schedule", "empty result");
            report = Repair.IgnoreUserChoices(report with { Violations = report.Violations ?? [] }, blocks);
            if (!report.Valid)
                return Error(new ApiError(422, "INVALID_SCHEDULE", "시간표에 규칙 위반이 있어 내보낼 수 없습니다. 다시 계획을 만들어 주세요."));

            var ics = await mcp.CallAsync("export_ics", new
            {
                day,
                blocks = blocks.Select(b => new { id = b.Id, title = b.Title, start = b.Start, end = b.End, kind = b.Kind, reason = b.Reason }),
                calendarName = "하루설계",
            }, ct);

            var content = ics.TryGetProperty("ics", out var p) ? p.GetString() : null;
            if (string.IsNullOrEmpty(content)) throw new McpToolException("export_ics", "empty result");

            http.Response.Headers.CacheControl = "no-store";
            return Results.File(Encoding.UTF8.GetBytes(content), "text/calendar; charset=utf-8", $"haru-{day}.ics");
        }
        catch (Exception e) when (e is McpToolException or HttpRequestException or JsonException or IOException)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Export")
                .LogWarning("Export failed: {Type}", e.GetType().Name);
            return Error(new ApiError(503, "AI_UNAVAILABLE", "캘린더 변환 도구에 연결하지 못했습니다. 잠시 후 다시 시도해 주세요."));
        }
    }

    private static IResult Error(ApiError e) =>
        Results.Json(new { error = new { code = e.Code, message = e.Message } }, HaruJson.Options, statusCode: e.Status);

    private static string ClientKey(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static async Task<T?> ReadAsync<T>(HttpContext http) where T : class
    {
        try
        {
            return await http.Request.ReadFromJsonAsync<T>(HaruJson.Options, http.RequestAborted);
        }
        catch (Exception e) when (e is JsonException or BadHttpRequestException or InvalidOperationException)
        {
            return null;
        }
    }
}
