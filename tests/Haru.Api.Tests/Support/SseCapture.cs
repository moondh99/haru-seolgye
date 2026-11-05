using System.Text;
using System.Text.Json;
using Haru.Api.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace Haru.Api.Tests.Support;

public sealed record SseEvent(string Name, JsonElement Data);

/// <summary>Collects what an SseStream writes, without a web server.</summary>
public sealed class SseCapture : IAsyncDisposable
{
    private readonly MemoryStream _body = new();

    public SseCapture()
    {
        var http = new DefaultHttpContext();
        http.Response.Body = _body;
        Stream = new SseStream(http.Response) { PingInterval = TimeSpan.FromMinutes(5) };
    }

    public SseStream Stream { get; }

    public Task StartAsync() => Stream.StartAsync(CancellationToken.None);

    public async Task<IReadOnlyList<SseEvent>> FinishAsync()
    {
        await Stream.CompleteAsync();
        return Parse(Encoding.UTF8.GetString(_body.ToArray()));
    }

    public static IReadOnlyList<SseEvent> Parse(string text)
    {
        var events = new List<SseEvent>();
        foreach (var frame in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? name = null, data = null;
            foreach (var line in frame.Split('\n'))
            {
                if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
            }

            if (name is null || data is null) continue; // ping comment
            events.Add(new SseEvent(name, JsonDocument.Parse(data).RootElement.Clone()));
        }

        return events;
    }

    public async ValueTask DisposeAsync() => await Stream.DisposeAsync();
}
