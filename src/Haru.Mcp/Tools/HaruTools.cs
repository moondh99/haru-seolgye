using System.ComponentModel;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Haru.Mcp.Logic;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Haru.Mcp.Tools;

/// <summary>Thin MCP adapter over the pure logic classes. Logs tool name, outcome and duration only, never arguments.</summary>
[McpServerToolType]
public sealed class HaruTools(ILogger<HaruTools> logger)
{
    /// <summary>camelCase output with explicit nulls, so the JSON matches docs/api-contract.md exactly. Korean text stays readable (not \uXXXX).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [McpServerTool(Name = "resolve_date", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ResolveDateResult))]
    [Description("Resolve a Korean date and/or time phrase (for example 내일 오후 3시, 다음 주 월요일, 금요일까지 17:00, 11월 7일, 정오) " +
                 "relative to referenceDate. Returns {ok, date, time, normalized, error}; ok=false with a Korean error when the phrase cannot be read.")]
    public CallToolResult ResolveDate(
        [Description("Korean date/time phrase, at most 60 characters.")] string expression,
        [Description("Reference date (today) as YYYY-MM-DD, KST. Weeks start on Monday.")] string referenceDate) =>
        Run("resolve_date", () => Outcome<ResolveDateResult>.Ok(DateResolver.Resolve(expression, referenceDate)));

    [McpServerTool(Name = "find_free_slots", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(FreeSlotsResult))]
    [Description("List free time slots inside the work window. Busy intervals are clipped to the window and merged; each free gap is shortened by " +
                 "bufferMinutes on the sides that touch a busy interval (not at the window edges); gaps shorter than minMinutes are dropped. " +
                 "Returns {slots: [{start, end, minutes}], totalFreeMinutes}.")]
    public CallToolResult FindFreeSlots(
        [Description("Work window start, HH:mm (24-hour, KST).")] string workStart,
        [Description("Work window end, HH:mm, later than workStart.")] string workEnd,
        [Description("Busy intervals (at most 50), each {start, end} as HH:mm.")] TimeRange[] busy,
        [Description("Minimum slot length in minutes (0-1440). Default 5.")] int? minMinutes = 5,
        [Description("Buffer in minutes kept next to busy intervals (0-1440). Default 5.")] int? bufferMinutes = 5) =>
        Run("find_free_slots", () => FreeSlotFinder.Find(workStart, workEnd, busy, minMinutes ?? 5, bufferMinutes ?? 5));

    [McpServerTool(Name = "validate_schedule", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ValidationReport))]
    [Description("Check a day schedule. Violation codes: INVALID_RANGE, OUT_OF_HOURS (task/break blocks outside the work window), OVERLAP, " +
                 "NO_BUFFER (gap between neighbouring blocks smaller than bufferMinutes, ignored next to a break), OVERLOAD (loadRatio above maxLoad), " +
                 "DEADLINE_MISSED (task block ends after deadlineTime). Returns {valid, loadRatio, occupiedMinutes, availableMinutes, violations: [{code, message, blockIds}]}.")]
    public CallToolResult ValidateSchedule(
        [Description("Work window start, HH:mm (24-hour, KST).")] string workStart,
        [Description("Work window end, HH:mm, later than workStart.")] string workEnd,
        [Description("Schedule blocks (at most 50).")] ScheduleBlock[] blocks,
        [Description("Minimum gap in minutes between neighbouring blocks (0-1440). Default 5.")] int? bufferMinutes = 5,
        [Description("Maximum allowed loadRatio = occupied / available minutes. Default 0.85.")] double? maxLoad = 0.85) =>
        Run("validate_schedule", () => Outcome<ValidationReport>.Ok(
            ScheduleValidator.Validate(workStart, workEnd, blocks, bufferMinutes ?? 5, maxLoad ?? 0.85)));

    [McpServerTool(Name = "export_ics", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(IcsResult))]
    [Description("Export a confirmed day schedule as an RFC 5545 iCalendar file (Asia/Seoul). Break blocks are skipped. " +
                 "Returns {filename, ics, eventCount}. Only call after the user has confirmed the schedule.")]
    public CallToolResult ExportIcs(
        [Description("Schedule day as YYYY-MM-DD, KST.")] string day,
        [Description("Schedule blocks to export (at most 50).")] ExportBlock[] blocks,
        [Description("Calendar display name. Default 하루설계.")] string? calendarName = null) =>
        Run("export_ics", () => IcsExporter.Export(day, blocks, calendarName));

    private CallToolResult Run<T>(string tool, Func<Outcome<T>> work)
    {
        var started = Stopwatch.GetTimestamp();
        CallToolResult result;
        try
        {
            var outcome = work();
            result = outcome.IsOk ? Success(outcome.Value!) : Failure(outcome.Error!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only the exception type is logged: messages may quote user text.
            logger.LogError("Tool {Tool} failed unexpectedly ({ExceptionType})", tool, ex.GetType().Name);
            result = Failure("도구 실행 중 내부 오류가 발생했습니다. 입력을 확인한 뒤 다시 시도하세요.");
        }

        LogFinished(logger, tool, result.IsError == true, started);
        return result;
    }

    /// <summary>The only per-call log line: tool name, outcome and duration. Arguments are never logged.</summary>
    internal static void LogFinished(ILogger? logger, string tool, bool isError, long started) =>
        logger?.LogInformation(
            "Tool {Tool} finished: {Outcome} in {ElapsedMs:0.0} ms",
            tool,
            isError ? "error" : "ok",
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    internal static CallToolResult Success<T>(T value) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, JsonOptions) }],
        StructuredContent = JsonSerializer.SerializeToElement(value, JsonOptions),
    };

    internal static CallToolResult Failure(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
