using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Haru.Api.Contracts;

namespace Haru.Api.Infrastructure;

/// <summary>
/// Writes Server-Sent Events. All writes go through one channel reader, so frames never interleave.
/// A ping comment goes out every 15 s so proxies keep the connection open.
/// </summary>
public sealed class SseStream : IAsyncDisposable
{
    private readonly HttpResponse _response;
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _stop = new();
    private Task? _pump;

    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(15);

    public SseStream(HttpResponse response) => _response = response;

    public static string Frame(string name, object? data) =>
        $"event: {name}\ndata: {JsonSerializer.Serialize(data, HaruJson.Options)}\n\n";

    public async Task StartAsync(CancellationToken ct)
    {
        _response.StatusCode = StatusCodes.Status200OK;
        _response.Headers.ContentType = "text/event-stream; charset=utf-8";
        _response.Headers.CacheControl = "no-cache, no-transform";
        _response.Headers["X-Accel-Buffering"] = "no";
        await _response.StartAsync(ct);
        _pump = Task.Run(() => PumpAsync(ct), CancellationToken.None);
    }

    public ValueTask SendAsync(string name, object? data)
    {
        _channel.Writer.TryWrite(Frame(name, data));
        return ValueTask.CompletedTask;
    }

    public async Task CompleteAsync()
    {
        _channel.Writer.TryComplete();
        if (_pump is not null) await _pump;
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var reader = _channel.Reader;
        try
        {
            while (true)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
                wait.CancelAfter(PingInterval);
                try
                {
                    if (!await reader.WaitToReadAsync(wait.Token)) break;
                    while (reader.TryRead(out var frame)) await WriteAsync(frame, ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_stop.IsCancellationRequested)
                {
                    await WriteAsync(": ping\n\n", ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task WriteAsync(string text, CancellationToken ct)
    {
        await _response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await _response.Body.FlushAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_pump is not null) { try { await _pump; } catch { } }
        _stop.Dispose();
    }
}
