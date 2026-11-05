using GitHub.Copilot;
using Haru.Api.Config;

namespace Haru.Api.Copilot;

/// <summary>Owns the single Copilot client (one runtime process) and reports whether AI is reachable.</summary>
public class CopilotHost(HaruOptions options, ILogger<CopilotHost> log) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CopilotClient? _client;
    private (bool Ok, DateTimeOffset At)? _auth;

    public async Task<CopilotClient> GetClientAsync(CancellationToken ct)
    {
        if (_client is not null) return _client;
        await _gate.WaitAsync(ct);
        try
        {
            if (_client is not null) return _client;
            Directory.CreateDirectory(options.CopilotHome);
            var clientOptions = new CopilotClientOptions
            {
                Mode = CopilotClientMode.Empty,
                BaseDirectory = options.CopilotHome,
                LogLevel = CopilotLogLevel.Error,
            };
            if (options.CopilotToken is { Length: > 0 } token) clientOptions.GitHubToken = token;

            var client = new CopilotClient(clientOptions);
            try
            {
                await client.StartAsync(ct);
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

    public virtual async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (!options.AiEnabled) return false;
        if (_auth is { } cached)
        {
            var ttl = cached.Ok ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(15);
            if (DateTimeOffset.UtcNow - cached.At < ttl) return cached.Ok;
        }

        bool ok;
        try
        {
            var client = await GetClientAsync(ct);
            var status = await client.GetAuthStatusAsync(ct);
            ok = status.IsAuthenticated;
            if (!ok) log.LogWarning("Copilot is not authenticated: {Message}", status.StatusMessage);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Copilot availability check failed");
            ok = false;
        }
        _auth = (ok, DateTimeOffset.UtcNow);
        return ok;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        _gate.Dispose();
    }
}
