using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Haru.Api.Agents;
using Haru.Api.Config;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Haru.Api.Mcp;

public interface IMcpGateway
{
    /// <summary>Calls one MCP tool from code and returns its JSON result.</summary>
    Task<JsonElement> CallAsync(string tool, object? args, CancellationToken ct);

    /// <summary>Builds model-callable tools for an agent. Only the named tools are exposed.</summary>
    Task<IReadOnlyList<AIFunction>> CreateToolsAsync(
        IEnumerable<string> names, Func<AgentSignal, ValueTask> signal, CancellationToken ct);
}

public sealed class McpToolException(string tool, string message) : Exception($"MCP tool '{tool}' failed: {message}")
{
    public string Tool { get; } = tool;
}

/// <summary>Streamable-HTTP client for the separate Haru.Mcp service. One shared connection; it reconnects once on failure.</summary>
public sealed class McpGateway(HaruOptions options, ILoggerFactory loggerFactory, ILogger<McpGateway> log)
    : IMcpGateway, IAsyncDisposable
{
    private static readonly ActivitySource Source = new("Haru.Mcp.Client");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;
    private IList<McpClientTool>? _tools;

    private async Task<McpClient> GetClientAsync(CancellationToken ct)
    {
        if (_client is not null) return _client;
        await _gate.WaitAsync(ct);
        try
        {
            if (_client is not null) return _client;
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(options.McpUrl),
                TransportMode = HttpTransportMode.StreamableHttp,
                Name = "haru-mcp",
            }, loggerFactory);
            var client = await McpClient.CreateAsync(transport, loggerFactory: loggerFactory, cancellationToken: ct);
            try
            {
                // The SDK reads its tool cache when it calls a tool, so list the tools once per connection.
                _tools = await client.ListToolsAsync(cancellationToken: ct);
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
            _client = client;
            return client;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ResetAsync(McpClient failed)
    {
        await _gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_client, failed)) return;
            _client = null;
            _tools = null;
        }
        finally
        {
            _gate.Release();
        }
        try { await failed.DisposeAsync(); } catch { /* connection is already broken */ }
    }

    public async Task<JsonElement> CallAsync(string tool, object? args, CancellationToken ct)
    {
        using var activity = Source.StartActivity("mcp.call");
        activity?.SetTag("mcp.tool", tool);
        var arguments = ToDictionary(args);
        for (var attempt = 0; ; attempt++)
        {
            var client = await GetClientAsync(ct);
            CallToolResult result;
            try
            {
                result = await client.CallToolAsync(tool, arguments, cancellationToken: ct);
            }
            catch (Exception e) when (attempt == 0 && e is not OperationCanceledException)
            {
                log.LogWarning("MCP call failed once ({Tool}); reconnecting: {Reason}", tool, e.GetType().Name);
                await ResetAsync(client);
                continue;
            }
            return Parse(tool, result);
        }
    }

    public async Task<IReadOnlyList<AIFunction>> CreateToolsAsync(
        IEnumerable<string> names, Func<AgentSignal, ValueTask> signal, CancellationToken ct)
    {
        var wanted = names.ToHashSet();
        if (wanted.Count == 0) return [];

        var client = await GetClientAsync(ct);
        var all = _tools ??= await client.ListToolsAsync(cancellationToken: ct);
        var budget = new ToolBudget(8);
        var result = new List<AIFunction>();
        foreach (var name in wanted)
        {
            var source = all.FirstOrDefault(t => t.Name == name)
                ?? throw new McpToolException(name, "the MCP server does not offer this tool");
            result.Add(new GatewayTool(source, this, signal, budget));
        }
        return result;
    }

    private static Dictionary<string, object?> ToDictionary(object? args)
    {
        var dict = new Dictionary<string, object?>();
        if (args is null) return dict;
        var element = args is JsonElement je ? je : JsonSerializer.SerializeToElement(args, Contracts.HaruJson.Options);
        if (element.ValueKind != JsonValueKind.Object) return dict;
        foreach (var p in element.EnumerateObject()) dict[p.Name] = p.Value.Clone();
        return dict;
    }

    private static JsonElement Parse(string tool, CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().Select(c => c.Text).FirstOrDefault();
        if (result.IsError is true) throw new McpToolException(tool, Truncate(text ?? "tool error", 160));
        if (result.StructuredContent is { } structured)
            return structured.Clone();
        if (string.IsNullOrWhiteSpace(text)) throw new McpToolException(tool, "empty result");
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new McpToolException(tool, "result is not JSON");
        }
    }

    internal void LogToolFailure(string tool, Exception e) =>
        log.LogWarning("Tool {Tool} failed: {Reason}", tool, e.GetType().Name);

    internal static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        // Do not cut an emoji in half.
        var end = max > 0 && char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..end] + "…";
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        _gate.Dispose();
    }

    internal sealed class ToolBudget(int max)
    {
        private int _used;
        public bool TryTake() => Interlocked.Increment(ref _used) <= max;
    }

    /// <summary>Wraps an MCP tool for the model: reports progress to the UI, limits the call count and hides raw errors.</summary>
    private sealed class GatewayTool(
        McpClientTool source, McpGateway gateway, Func<AgentSignal, ValueTask> signal, ToolBudget budget) : AIFunction
    {
        public override string Name => source.Name;
        public override string Description => source.Description;
        public override JsonElement JsonSchema => source.JsonSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken ct)
        {
            if (!budget.TryTake())
                return JsonSerializer.SerializeToElement(new { error = "Tool call limit reached. Answer with what you have." });

            var args = new Dictionary<string, object?>(arguments);
            await signal(new ToolCallSignal(Name, ToolSummary.CompactArgs(args)));
            try
            {
                var json = await gateway.CallAsync(Name, args, ct);
                await signal(new ToolResultSignal(Name, true, ToolSummary.Of(Name, json)));
                return json;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                gateway.LogToolFailure(Name, e);
                await signal(new ToolResultSignal(Name, false, "도구 호출에 실패했습니다."));
                return JsonSerializer.SerializeToElement(new { error = "The tool failed. Try once more or answer with what you have." });
            }
        }
    }
}

public static class ToolSummary
{
    public static IReadOnlyDictionary<string, object?> CompactArgs(IReadOnlyDictionary<string, object?> args)
    {
        var result = new Dictionary<string, object?>();
        foreach (var (key, value) in args)
        {
            var e = value is JsonElement je ? je : JsonSerializer.SerializeToElement(value);
            result[key] = e.ValueKind switch
            {
                JsonValueKind.Array => $"{e.GetArrayLength()}개",
                JsonValueKind.String => McpGateway.Truncate(e.GetString() ?? "", 40),
                JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }
        return result;
    }

    public static string Of(string tool, JsonElement json)
    {
        try
        {
            switch (tool)
            {
                case "find_free_slots":
                {
                    var n = json.TryGetProperty("slots", out var s) && s.ValueKind == JsonValueKind.Array ? s.GetArrayLength() : 0;
                    var total = json.TryGetProperty("totalFreeMinutes", out var t) ? t.GetInt32() : 0;
                    return $"빈 시간 {n}곳, 합계 {total}분";
                }
                case "validate_schedule":
                {
                    var load = json.TryGetProperty("loadRatio", out var l) ? (int)Math.Round(l.GetDouble() * 100) : 0;
                    var count = json.TryGetProperty("violations", out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;
                    if (count == 0) return $"통과 · 부하 {load}%";
                    var codes = v.EnumerateArray()
                        .Select(x => x.TryGetProperty("code", out var c) ? c.GetString() : null)
                        .Where(c => c is not null).Distinct().Take(3);
                    return $"위반 {count}건({string.Join(", ", codes)}) · 부하 {load}%";
                }
                case "resolve_date":
                {
                    var ok = json.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                    var norm = json.TryGetProperty("normalized", out var n) ? n.GetString() : null;
                    return ok && norm is not null ? $"→ {norm}" : "읽지 못함";
                }
                default:
                    return "완료";
            }
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return "완료";
        }
    }
}
