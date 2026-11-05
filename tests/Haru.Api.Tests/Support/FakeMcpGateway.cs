extern alias HaruMcp;

using System.Collections.Concurrent;
using System.Text.Json;
using Haru.Api.Agents;
using Haru.Api.Contracts;
using Haru.Api.Mcp;
using Microsoft.Extensions.AI;
using HaruMcp::Haru.Mcp;
using HaruMcp::Haru.Mcp.Logic;
using HaruMcp::Haru.Mcp.Tools;

namespace Haru.Api.Tests.Support;

/// <summary>In-process MCP gateway. It calls the real Haru.Mcp.Logic classes, so tool results match the real server.</summary>
public sealed class FakeMcpGateway : IMcpGateway
{
    private static readonly JsonSerializerOptions ArgOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentQueue<(string Tool, JsonElement Args)> _calls = new();
    private readonly ConcurrentDictionary<string, string> _failing = new();
    private readonly ConcurrentDictionary<string, Exception> _crashing = new();

    public IReadOnlyList<(string Tool, JsonElement Args)> Calls => _calls.ToArray();

    public IEnumerable<string> Expressions =>
        Calls.Where(c => c.Tool == "resolve_date").Select(c => c.Args.GetProperty("expression").GetString()!);

    public int CountOf(string tool) => Calls.Count(c => c.Tool == tool);

    /// <summary>Makes every later call to the tool throw McpToolException.</summary>
    public FakeMcpGateway Fail(string tool, string message = "tool is down")
    {
        _failing[tool] = message;
        return this;
    }

    /// <summary>Makes every later call to the tool throw this exact exception, for failures the app does not expect.</summary>
    public FakeMcpGateway Throw(string tool, Exception error)
    {
        _crashing[tool] = error;
        return this;
    }

    public Task<JsonElement> CallAsync(string tool, object? args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var input = JsonSerializer.SerializeToElement(args, HaruJson.Options);
        _calls.Enqueue((tool, input));
        if (_failing.TryGetValue(tool, out var message)) throw new McpToolException(tool, message);
        if (_crashing.TryGetValue(tool, out var crash)) throw crash;

        object result = tool switch
        {
            "resolve_date" => DateResolver.Resolve(Text(input, "expression"), Text(input, "referenceDate")),
            "validate_schedule" => ScheduleValidator.Validate(
                Text(input, "workStart"), Text(input, "workEnd"), List<ScheduleBlock?>(input, "blocks"),
                Int(input, "bufferMinutes", 5), Number(input, "maxLoad", 0.85)),
            "find_free_slots" => Unwrap(tool, FreeSlotFinder.Find(
                Text(input, "workStart"), Text(input, "workEnd"),
                List<TimeRange?>(input, "busy"), Int(input, "minMinutes", 5), Int(input, "bufferMinutes", 5))),
            "export_ics" => Unwrap(tool, IcsExporter.Export(
                Text(input, "day"), List<ExportBlock?>(input, "blocks"), Text(input, "calendarName"))),
            _ => throw new McpToolException(tool, "unknown tool"),
        };
        return Task.FromResult(JsonSerializer.SerializeToElement(result, HaruTools.JsonOptions));
    }

    public Task<IReadOnlyList<AIFunction>> CreateToolsAsync(
        IEnumerable<string> names, Func<AgentSignal, ValueTask> signal, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AIFunction>>([]);

    private static T Unwrap<T>(string tool, Outcome<T> outcome) =>
        outcome.IsOk ? outcome.Value! : throw new McpToolException(tool, outcome.Error!);

    private static string? Text(JsonElement input, string name) =>
        input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement input, string name, int fallback) =>
        input.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : fallback;

    private static double Number(JsonElement input, string name, double fallback) =>
        input.TryGetProperty(name, out var v) && v.TryGetDouble(out var n) ? n : fallback;

    private static List<T>? List<T>(JsonElement input, string name) =>
        input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.Deserialize<List<T>>(ArgOptions)
            : null;
}
