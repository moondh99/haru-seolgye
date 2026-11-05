using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Haru.Mcp.Tests;

/// <summary>Starts the real MCP server in-process on a free loopback port and records every log line it writes.</summary>
public sealed class McpServerFixture : IAsyncLifetime
{
    private WebApplication? app;

    public ConcurrentQueue<string> Logs { get; } = new();

    public string BaseUrl { get; private set; } = "";

    public async Task InitializeAsync()
    {
        app = McpServerApp.Create(
            ["--urls=http://127.0.0.1:0", "--Logging:LogLevel:Default=Information"],
            builder => builder.Logging.AddProvider(new CapturingLoggerProvider(Logs)));
        await app.StartAsync();
        BaseUrl = app.Urls.Single().TrimEnd('/');
    }

    public async Task DisposeAsync()
    {
        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

internal sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Capture(categoryName, sink);

    public void Dispose()
    {
    }

    private sealed class Capture(string category, ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(", ", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                : "";
            sink.Enqueue($"{logLevel} {category}: {formatter(state, exception)} [{values}] {exception}");
        }
    }
}

[Collection("Live server")]
public sealed class McpServerLiveTests(McpServerFixture server) : IClassFixture<McpServerFixture>
{
    private static readonly string[] ToolNames = ["resolve_date", "find_free_slots", "validate_schedule", "export_ics"];

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private async Task<McpClient> ConnectAsync() =>
        await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(server.BaseUrl + "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }));

    private static Dictionary<string, object?> Args(object arguments) =>
        JsonSerializer.SerializeToElement(arguments, Web).EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());

    private static string[] Names(JsonElement element) => element.EnumerateObject().Select(property => property.Name).ToArray();

    private static string TextOf(CallToolResult result) => Assert.Single(result.Content.OfType<TextContentBlock>()).Text;

    // A successful result carries the same JSON as text and as structuredContent.
    private static JsonElement Json(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        var json = JsonDocument.Parse(TextOf(result)).RootElement.Clone();
        Assert.NotNull(result.StructuredContent);
        Assert.True(JsonElement.DeepEquals(json, result.StructuredContent!.Value));
        return json;
    }

    private static void AssertSafeErrorText(string text)
    {
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain(" at Haru.", text);
        Assert.DoesNotContain(".cs:line", text);
        Assert.DoesNotContain("/Users/", text);
        Assert.DoesNotContain("Exception", text);
        Assert.DoesNotContain("StackTrace", text);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task Default_endpoints_return_200(string path)
    {
        using var http = new HttpClient();

        var response = await http.GetAsync(server.BaseUrl + path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Tools_list_returns_exactly_the_four_tools()
    {
        await using var client = await ConnectAsync();

        var tools = await client.ListToolsAsync();

        Assert.Equal(ToolNames.Order(), tools.Select(tool => tool.Name).Order());
        Assert.All(tools, tool => Assert.False(string.IsNullOrWhiteSpace(tool.Description)));
    }

    [Fact]
    public async Task Tool_schemas_use_the_contract_parameter_names_and_describe_every_parameter()
    {
        await using var client = await ConnectAsync();
        var tools = (await client.ListToolsAsync()).ToDictionary(tool => tool.Name, tool => tool.ProtocolTool);
        var expected = new Dictionary<string, (string[] Properties, string[] Required)>
        {
            ["resolve_date"] = (["expression", "referenceDate"], ["expression", "referenceDate"]),
            ["find_free_slots"] = (["workStart", "workEnd", "busy", "minMinutes", "bufferMinutes"], ["workStart", "workEnd", "busy"]),
            ["validate_schedule"] = (["workStart", "workEnd", "blocks", "bufferMinutes", "maxLoad"], ["workStart", "workEnd", "blocks"]),
            ["export_ics"] = (["day", "blocks", "calendarName"], ["day", "blocks"]),
        };

        foreach (var (name, (properties, required)) in expected)
        {
            var schema = tools[name].InputSchema;
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.Equal(properties.Order(), Names(schema.GetProperty("properties")).Order());
            Assert.Equal(required.Order(), schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).Order());
            AssertDescribed(schema, name);
        }

        Assert.Equal(["start", "end"], Names(Item(tools["find_free_slots"], "busy")));
        Assert.Equal(["id", "title", "start", "end", "kind", "deadlineTime"], Names(Item(tools["validate_schedule"], "blocks")));
        Assert.Equal(["id", "title", "start", "end", "kind", "reason"], Names(Item(tools["export_ics"], "blocks")));

        static JsonElement Item(Tool tool, string parameter) =>
            tool.InputSchema.GetProperty("properties").GetProperty(parameter).GetProperty("items").GetProperty("properties");

        static void AssertDescribed(JsonElement schema, string path)
        {
            if (schema.TryGetProperty("properties", out var properties))
            {
                foreach (var property in properties.EnumerateObject())
                {
                    Assert.True(
                        property.Value.TryGetProperty("description", out var description) && !string.IsNullOrWhiteSpace(description.GetString()),
                        $"{path}.{property.Name} has no description");
                    AssertDescribed(property.Value, $"{path}.{property.Name}");
                }
            }

            if (schema.TryGetProperty("items", out var items))
            {
                AssertDescribed(items, path + "[]");
            }
        }
    }

    [Fact]
    public async Task Tool_output_schemas_use_the_contract_field_names()
    {
        await using var client = await ConnectAsync();
        var tools = (await client.ListToolsAsync()).ToDictionary(tool => tool.Name, tool => tool.ProtocolTool);

        Assert.Equal(["ok", "date", "time", "normalized", "error"], Names(tools["resolve_date"].OutputSchema!.Value.GetProperty("properties")));
        Assert.Equal(["slots", "totalFreeMinutes"], Names(tools["find_free_slots"].OutputSchema!.Value.GetProperty("properties")));
        Assert.Equal(
            ["valid", "loadRatio", "occupiedMinutes", "availableMinutes", "violations"],
            Names(tools["validate_schedule"].OutputSchema!.Value.GetProperty("properties")));
        Assert.Equal(["filename", "ics", "eventCount"], Names(tools["export_ics"].OutputSchema!.Value.GetProperty("properties")));
    }

    [Fact]
    public async Task Tools_are_annotated_as_read_only_and_idempotent()
    {
        await using var client = await ConnectAsync();

        foreach (var tool in await client.ListToolsAsync())
        {
            var annotations = tool.ProtocolTool.Annotations;
            Assert.NotNull(annotations);
            Assert.True(annotations.ReadOnlyHint);
            Assert.True(annotations.IdempotentHint);
            Assert.False(annotations.DestructiveHint);
            Assert.False(annotations.OpenWorldHint);
        }
    }

    [Theory]
    [InlineData("내일 오후 3시", "2026-11-06", "15:00", "2026-11-06 15:00")]
    [InlineData("다음 주 월요일 오전 10시 30분", "2026-11-09", "10:30", "2026-11-09 10:30")]
    [InlineData("금요일까지 17:00", "2026-11-06", "17:00", "2026-11-06 17:00")]
    [InlineData("이번 달 말", "2026-11-30", null, "2026-11-30")]
    [InlineData("3시 반", null, "15:30", "15:30")]
    public async Task Resolve_date_returns_the_contract_json(string expression, string? date, string? time, string normalized)
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("resolve_date", Args(new { expression, referenceDate = "2026-11-05" }));

        var json = Json(result);
        Assert.Equal(["ok", "date", "time", "normalized", "error"], Names(json));
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(date, json.GetProperty("date").GetString());
        Assert.Equal(time, json.GetProperty("time").GetString());
        Assert.Equal(normalized, json.GetProperty("normalized").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task Resolve_date_answers_ok_false_for_text_it_cannot_read_and_does_not_fail_the_call()
    {
        await using var client = await ConnectAsync();

        var garbage = Json(await client.CallToolAsync("resolve_date", Args(new { expression = "아무말", referenceDate = "2026-11-05" })));
        var tooLong = Json(await client.CallToolAsync("resolve_date", Args(new { expression = new string('가', 61), referenceDate = "2026-11-05" })));
        var badReference = Json(await client.CallToolAsync("resolve_date", Args(new { expression = "내일", referenceDate = "내일" })));

        foreach (var json in new[] { garbage, tooLong, badReference })
        {
            Assert.False(json.GetProperty("ok").GetBoolean());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("date").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("time").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("normalized").ValueKind);
            Assert.Matches("[가-힣]", json.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task Find_free_slots_returns_the_contract_json_and_applies_the_default_minutes_and_buffer()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "find_free_slots",
            Args(new { workStart = "09:00", workEnd = "18:00", busy = new[] { new { start = "12:00", end = "13:00" } } }));

        var json = Json(result);
        Assert.Equal(["slots", "totalFreeMinutes"], Names(json));
        Assert.Equal(470, json.GetProperty("totalFreeMinutes").GetInt32());
        var slots = json.GetProperty("slots").EnumerateArray().ToArray();
        Assert.Equal(2, slots.Length);
        Assert.Equal(["start", "end", "minutes"], Names(slots[0]));
        Assert.Equal(("09:00", "11:55", 175), (slots[0].GetProperty("start").GetString(), slots[0].GetProperty("end").GetString(), slots[0].GetProperty("minutes").GetInt32()));
        Assert.Equal(("13:05", "18:00", 295), (slots[1].GetProperty("start").GetString(), slots[1].GetProperty("end").GetString(), slots[1].GetProperty("minutes").GetInt32()));
    }

    [Fact]
    public async Task Find_free_slots_honours_explicit_minMinutes_and_bufferMinutes()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "find_free_slots",
            Args(new
            {
                workStart = "09:00",
                workEnd = "18:00",
                busy = new[] { new { start = "09:40", end = "12:00" }, new { start = "12:30", end = "18:00" } },
                minMinutes = 30,
                bufferMinutes = 0,
            }));

        var json = Json(result);
        var slots = json.GetProperty("slots").EnumerateArray().Select(slot => slot.GetProperty("start").GetString() + "-" + slot.GetProperty("end").GetString()).ToArray();
        Assert.Equal(["09:00-09:40", "12:00-12:30"], slots);
        Assert.Equal(70, json.GetProperty("totalFreeMinutes").GetInt32());
    }

    [Fact]
    public async Task Validate_schedule_passes_the_sample_plan_with_load_ratio_069()
    {
        var sample = SamplePlan.Load();
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "validate_schedule",
            Args(new { workStart = sample.WorkStart, workEnd = sample.WorkEnd, blocks = sample.Blocks, bufferMinutes = 5, maxLoad = 0.85 }));

        var json = Json(result);
        Assert.Equal(["valid", "loadRatio", "occupiedMinutes", "availableMinutes", "violations"], Names(json));
        Assert.True(json.GetProperty("valid").GetBoolean());
        Assert.Equal(0.69, json.GetProperty("loadRatio").GetDouble());
        Assert.Equal(330, json.GetProperty("occupiedMinutes").GetInt32());
        Assert.Equal(480, json.GetProperty("availableMinutes").GetInt32());
        Assert.Equal(0, json.GetProperty("violations").GetArrayLength());
    }

    [Fact]
    public async Task Validate_schedule_reports_violations_with_code_message_and_blockIds()
    {
        var sample = SamplePlan.Load();
        var blocks = sample.Blocks.Append(new ScheduleBlock { Id = "b8", Title = "다음 주 세미나 자료 조사", Start = "16:30", End = "18:00", Kind = "task" });
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("validate_schedule", Args(new { workStart = "09:00", workEnd = "18:00", blocks }));

        var json = Json(result);
        Assert.False(json.GetProperty("valid").GetBoolean());
        Assert.Equal(0.88, json.GetProperty("loadRatio").GetDouble());
        var violations = json.GetProperty("violations").EnumerateArray().ToArray();
        Assert.Equal(["NO_BUFFER", "OVERLOAD"], violations.Select(v => v.GetProperty("code").GetString()));
        Assert.All(violations, violation =>
        {
            Assert.Equal(["code", "message", "blockIds"], Names(violation));
            Assert.Matches("[가-힣]", violation.GetProperty("message").GetString());
        });
        Assert.Equal(["b7", "b8"], violations[0].GetProperty("blockIds").EnumerateArray().Select(id => id.GetString()));
    }

    [Fact]
    public async Task Validate_schedule_answers_INVALID_RANGE_for_bad_blocks_instead_of_failing_the_call()
    {
        await using var client = await ConnectAsync();
        var blocks = new[] { new { id = "x", title = "잘못된 블록", start = "25:00", end = "10:00", kind = "task" } };

        var result = await client.CallToolAsync("validate_schedule", Args(new { workStart = "09:00", workEnd = "18:00", blocks }));

        var json = Json(result);
        Assert.False(json.GetProperty("valid").GetBoolean());
        var violation = Assert.Single(json.GetProperty("violations").EnumerateArray());
        Assert.Equal("INVALID_RANGE", violation.GetProperty("code").GetString());
        Assert.Equal(["x"], violation.GetProperty("blockIds").EnumerateArray().Select(id => id.GetString()));
    }

    [Fact]
    public async Task Export_ics_returns_the_contract_json_for_the_sample_plan()
    {
        var sample = SamplePlan.Load();
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("export_ics", Args(new { day = sample.Day, blocks = sample.ExportBlocks, calendarName = "하루설계" }));

        var json = Json(result);
        Assert.Equal(["filename", "ics", "eventCount"], Names(json));
        Assert.Equal("haru-2026-11-05.ics", json.GetProperty("filename").GetString());
        Assert.Equal(6, json.GetProperty("eventCount").GetInt32());
        var ics = json.GetProperty("ics").GetString()!;
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
        Assert.Contains("TZID:Asia/Seoul\r\n", ics);
        Assert.Contains("UID:b2-2026-11-05@haru\r\n", ics);
        Assert.Contains("DTSTART;TZID=Asia/Seoul:20261105T093500\r\n", ics);
        Assert.DoesNotContain("점심", ics);
    }

    [Fact]
    public async Task Export_ics_defaults_the_calendar_name_when_it_is_omitted()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "export_ics",
            Args(new { day = "2026-11-05", blocks = new[] { new { id = "a", title = "회의", start = "10:00", end = "11:00", kind = "fixed" } } }));

        var ics = Json(result).GetProperty("ics").GetString()!;
        Assert.Contains("X-WR-CALNAME:하루설계\r\n", ics);
    }

    [Fact]
    public async Task Find_free_slots_and_export_ics_report_bad_input_as_isError_with_a_korean_message()
    {
        await using var client = await ConnectAsync();

        var badTime = await client.CallToolAsync("find_free_slots", Args(new { workStart = "9am", workEnd = "18:00", busy = Array.Empty<object>() }));
        var badRange = await client.CallToolAsync(
            "find_free_slots",
            Args(new { workStart = "09:00", workEnd = "18:00", busy = new[] { new { start = "11:00", end = "10:00" } } }));
        var badDay = await client.CallToolAsync("export_ics", Args(new { day = "내일", blocks = Array.Empty<object>() }));
        var tooMany = await client.CallToolAsync(
            "export_ics",
            Args(new { day = "2026-11-05", blocks = Enumerable.Range(0, 51).Select(i => new { id = $"b{i}", title = "t", start = "09:00", end = "10:00", kind = "task" }) }));

        foreach (var result in new[] { badTime, badRange, badDay, tooMany })
        {
            Assert.True(result.IsError);
            var text = TextOf(result);
            AssertSafeErrorText(text);
            Assert.Matches("[가-힣]", text);
            Assert.Null(result.StructuredContent);
        }
    }

    [Fact]
    public async Task Malformed_arguments_to_resolve_date_get_the_ok_false_result()
    {
        await using var client = await ConnectAsync();

        var results = new[]
        {
            await client.CallToolAsync("resolve_date", Args(new { referenceDate = "2026-11-05" })),
            await client.CallToolAsync("resolve_date", Args(new { expression = 5, referenceDate = "2026-11-05" })),
            await client.CallToolAsync("resolve_date", Args(new { expression = "내일" })),
            await client.CallToolAsync("resolve_date", Args(new { expression = new[] { "내일" }, referenceDate = "2026-11-05" })),
        };

        foreach (var result in results)
        {
            var json = Json(result);
            Assert.Equal(["ok", "date", "time", "normalized", "error"], Names(json));
            Assert.False(json.GetProperty("ok").GetBoolean());
            Assert.All(["date", "time", "normalized"], name => Assert.Equal(JsonValueKind.Null, json.GetProperty(name).ValueKind));
            var error = json.GetProperty("error").GetString()!;
            AssertSafeErrorText(error);
            Assert.Matches("[가-힣]", error);
            Assert.DoesNotContain("An error occurred", error);
        }
    }

    [Fact]
    public async Task Malformed_arguments_to_validate_schedule_get_one_INVALID_RANGE_violation()
    {
        await using var client = await ConnectAsync();

        var results = new[]
        {
            await client.CallToolAsync("validate_schedule", Args(new { workStart = "09:00", workEnd = "18:00", blocks = "nope" })),
            await client.CallToolAsync("validate_schedule", Args(new { workStart = "09:00", workEnd = "18:00" })),
            await client.CallToolAsync("validate_schedule", Args(new { workEnd = "18:00", blocks = Array.Empty<object>() })),
            await client.CallToolAsync(
                "validate_schedule",
                Args(new { workStart = "09:00", workEnd = "18:00", blocks = Array.Empty<object>(), maxLoad = "high" })),
        };

        foreach (var result in results)
        {
            var json = Json(result);
            Assert.False(json.GetProperty("valid").GetBoolean());
            Assert.Equal(0, json.GetProperty("loadRatio").GetDouble());
            Assert.Equal(0, json.GetProperty("occupiedMinutes").GetInt32());
            Assert.Equal(0, json.GetProperty("availableMinutes").GetInt32());
            var violation = Assert.Single(json.GetProperty("violations").EnumerateArray());
            Assert.Equal(["code", "message", "blockIds"], Names(violation));
            Assert.Equal("INVALID_RANGE", violation.GetProperty("code").GetString());
            Assert.Empty(violation.GetProperty("blockIds").EnumerateArray());
            var message = violation.GetProperty("message").GetString()!;
            AssertSafeErrorText(message);
            Assert.Matches("[가-힣]", message);
        }
    }

    [Fact]
    public async Task Malformed_arguments_to_find_free_slots_and_export_ics_become_isError_with_a_korean_hint()
    {
        await using var client = await ConnectAsync();
        var results = new[]
        {
            await client.CallToolAsync("find_free_slots", Args(new { workStart = "09:00", workEnd = "18:00" })),
            await client.CallToolAsync("find_free_slots", Args(new { workStart = "09:00", workEnd = "18:00", busy = "none" })),
            await client.CallToolAsync(
                "find_free_slots",
                Args(new { workStart = "09:00", workEnd = "18:00", busy = Array.Empty<object>(), minMinutes = "five" })),
            await client.CallToolAsync("export_ics", Args(new { blocks = Array.Empty<object>() })),
            await client.CallToolAsync("export_ics", Args(new { day = "2026-11-05", blocks = 7 })),
            await client.CallToolAsync("export_ics", Args(new { day = "2026-11-05", blocks = Array.Empty<object>(), calendarName = 12 })),
        };

        foreach (var result in results)
        {
            Assert.True(result.IsError);
            var text = TextOf(result);
            AssertSafeErrorText(text);
            Assert.Contains("입력 형식이 올바르지 않습니다", text);
            Assert.DoesNotContain("An error occurred", text);
            Assert.Null(result.StructuredContent);
        }
    }

    [Fact]
    public async Task Malformed_arguments_are_logged_as_a_plain_error_outcome_without_a_stack_trace()
    {
        await using var client = await ConnectAsync();
        var before = server.Logs.Count;

        await client.CallToolAsync("resolve_date", Args(new { expression = 5, referenceDate = "2026-11-05" }));
        await client.CallToolAsync("validate_schedule", Args(new { workStart = "09:00", workEnd = "18:00", blocks = "nope" }));
        await client.CallToolAsync("find_free_slots", Args(new { workStart = "09:00", workEnd = "18:00" }));
        await client.CallToolAsync("export_ics", Args(new { day = "2026-11-05", blocks = 7 }));

        var logs = server.Logs.Skip(before).ToArray();
        foreach (var tool in ToolNames)
        {
            Assert.Contains(logs, line => line.Contains($"Tool {tool} finished: error in "));
        }

        Assert.DoesNotContain(logs, line => line.Contains("unhandled exception") || line.Contains("Exception"));
    }

    [Fact]
    public async Task Unknown_tools_are_rejected_with_a_protocol_error()
    {
        await using var client = await ConnectAsync();

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("drop_database", Args(new { })).AsTask());

        AssertSafeErrorText(error.Message);
    }

    [Fact]
    public async Task Unknown_extra_arguments_are_ignored()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("resolve_date", Args(new { expression = "오늘", referenceDate = "2026-11-05", surprise = new { a = 1 } }));

        Assert.Equal("2026-11-05", Json(result).GetProperty("date").GetString());
    }

    [Fact]
    public async Task Concurrent_calls_on_one_connection_do_not_interfere()
    {
        await using var client = await ConnectAsync();
        var days = Enumerable.Range(0, 30).ToArray();

        var results = await Task.WhenAll(days.Select(async n =>
            Json(await client.CallToolAsync("resolve_date", Args(new { expression = $"{n}일 후", referenceDate = "2026-11-05" }))).GetProperty("date").GetString()));

        Assert.Equal(days.Select(n => new DateOnly(2026, 11, 5).AddDays(n).ToString("yyyy-MM-dd")), results);
    }

    [Fact]
    public async Task Separate_connections_work_independently()
    {
        await using var first = await ConnectAsync();
        await using var second = await ConnectAsync();

        var a = Json(await first.CallToolAsync("resolve_date", Args(new { expression = "내일", referenceDate = "2026-11-05" })));
        var b = Json(await second.CallToolAsync("resolve_date", Args(new { expression = "내일", referenceDate = "2027-01-31" })));

        Assert.Equal("2026-11-06", a.GetProperty("date").GetString());
        Assert.Equal("2027-02-01", b.GetProperty("date").GetString());
    }

    [Fact]
    public async Task Server_is_stateless_and_serves_calls_without_a_session()
    {
        using var http = new HttpClient();

        var initialize = await PostAsync(http, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"1"}}}""");
        var call = await PostAsync(http, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"resolve_date","arguments":{"expression":"모레","referenceDate":"2026-11-05"}}}""");

        Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);
        Assert.False(initialize.Headers.Contains("Mcp-Session-Id"));
        Assert.Contains("\"serverInfo\"", await initialize.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, call.StatusCode);
        Assert.Contains("2026-11-07", await call.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_and_delete_on_the_mcp_path_are_not_served_in_stateless_mode()
    {
        using var http = new HttpClient();

        var get = await http.GetAsync(server.BaseUrl + "/mcp");
        var delete = await http.DeleteAsync(server.BaseUrl + "/mcp");

        Assert.InRange((int)get.StatusCode, 400, 499);
        Assert.InRange((int)delete.StatusCode, 400, 499);
    }

    [Fact]
    public async Task Request_bodies_over_512_KB_are_refused()
    {
        using var http = new HttpClient();
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "resolve_date", arguments = new { expression = new string('a', 600 * 1024), referenceDate = "2026-11-05" } },
        });

        var response = await PostAsync(http, body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Logs_contain_tool_name_outcome_and_duration_but_never_the_arguments()
    {
        const string secret = "비밀제목XYZ";
        await using var client = await ConnectAsync();
        var block = new { id = "s1", title = secret, start = "09:00", end = "10:00", kind = "task", reason = secret };

        await client.CallToolAsync("resolve_date", Args(new { expression = secret, referenceDate = "2026-11-05" }));
        await client.CallToolAsync("find_free_slots", Args(new { workStart = secret, workEnd = "18:00", busy = Array.Empty<object>() }));
        await client.CallToolAsync("validate_schedule", Args(new { workStart = "09:00", workEnd = "18:00", blocks = new[] { block } }));
        await client.CallToolAsync("export_ics", Args(new { day = "2026-11-05", blocks = new[] { block }, calendarName = secret }));
        await client.CallToolAsync("export_ics", Args(new { day = secret, blocks = new[] { block } }));

        var logs = server.Logs.ToArray();
        Assert.DoesNotContain(logs, line => line.Contains("XYZ") || line.Contains("비밀"));
        Assert.Contains(logs, line => line.Contains("Information") && line.Contains("Tool resolve_date finished: ok in "));
        Assert.Contains(logs, line => line.Contains("Tool find_free_slots finished: error in "));
        Assert.Contains(logs, line => line.Contains("Tool validate_schedule finished: ok in "));
        Assert.Contains(logs, line => line.Contains("Tool export_ics finished: ok in "));
        Assert.Contains(logs, line => line.Contains("Tool export_ics finished: error in "));
        Assert.All(logs.Where(line => line.Contains("Tool ") && line.Contains(" finished: ")), line => Assert.Matches(@"in \d+\.\d ms", line));
    }

    private Task<HttpResponseMessage> PostAsync(HttpClient http, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, server.BaseUrl + "/mcp")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return http.SendAsync(request);
    }
}

[Collection("Live server")]
public sealed class McpServerPortTests
{
    private static readonly string[] PortVariables =
        ["ASPNETCORE_URLS", "DOTNET_URLS", "ASPNETCORE_HTTP_PORTS", "DOTNET_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS", "DOTNET_HTTPS_PORTS", "PORT"];

    [Fact]
    public async Task PORT_alone_selects_the_listen_port()
    {
        var port = FreePort();

        await using var host = await StartWithEnvironmentAsync(new() { ["PORT"] = port.ToString() });

        await AssertHealthyAsync($"http://127.0.0.1:{port}");
    }

    [Fact]
    public async Task ASPNETCORE_URLS_selects_the_listen_port_and_wins_over_PORT()
    {
        var urlsPort = FreePort();
        var ignoredPort = FreePort();

        await using var host = await StartWithEnvironmentAsync(new() { ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{urlsPort}", ["PORT"] = ignoredPort.ToString() });

        await AssertHealthyAsync($"http://127.0.0.1:{urlsPort}");
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => new HttpClient().GetAsync($"http://127.0.0.1:{ignoredPort}/health"));
    }

    private static async Task<Host> StartWithEnvironmentAsync(Dictionary<string, string> variables)
    {
        var saved = PortVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in PortVariables)
            {
                Environment.SetEnvironmentVariable(name, variables.GetValueOrDefault(name));
            }

            var app = McpServerApp.Create([]);
            await app.StartAsync();
            return new Host(app);
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private static async Task AssertHealthyAsync(string baseUrl)
    {
        using var http = new HttpClient();
        var response = await http.GetAsync(baseUrl + "/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class Host(WebApplication app) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
