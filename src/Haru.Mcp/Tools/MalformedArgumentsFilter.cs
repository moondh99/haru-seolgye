using System.Diagnostics;
using System.Text.Json;
using Haru.Mcp.Logic;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Haru.Mcp.Tools;

/// <summary>
/// The MCP SDK throws before a tool runs when an argument is missing or has the wrong JSON type. Contract section 3.5 says bad input
/// must never fail a call, so that exception becomes the same kind of result the tool gives for any other bad input.
/// </summary>
public static class MalformedArgumentsFilter
{
    private static readonly Dictionary<string, Func<CallToolResult>> BadInputResults = new()
    {
        ["resolve_date"] = () => HaruTools.Success(new ResolveDateResult(false, null, null, null,
            "입력 형식이 올바르지 않습니다. expression과 referenceDate(YYYY-MM-DD)를 문자열로 보내세요.")),
        ["find_free_slots"] = () => HaruTools.Failure(
            "입력 형식이 올바르지 않습니다. workStart·workEnd(HH:mm), busy(배열, 없으면 []), minMinutes·bufferMinutes(정수)를 확인하세요."),
        ["validate_schedule"] = () => HaruTools.Success(ScheduleValidator.Rejected(
            "입력 형식이 올바르지 않습니다. workStart·workEnd(HH:mm), blocks(배열), bufferMinutes(정수), maxLoad(숫자)를 확인하세요.")),
        ["export_ics"] = () => HaruTools.Failure(
            "입력 형식이 올바르지 않습니다. day(YYYY-MM-DD), blocks(배열), calendarName(문자열)을 확인하세요."),
    };

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create() => next => async (context, cancellationToken) =>
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await next(context, cancellationToken);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException
                                   && context.Params?.Name is { } tool && BadInputResults.ContainsKey(tool))
        {
            HaruTools.LogFinished(context.Services?.GetService<ILogger<HaruTools>>(), tool, isError: true, started);
            return BadInputResults[tool]();
        }
    };
}
