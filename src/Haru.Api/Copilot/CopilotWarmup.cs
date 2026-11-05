namespace Haru.Api.Copilot;

/// <summary>Starts the Copilot runtime at boot so the first user does not wait for it.</summary>
public sealed class CopilotWarmup(CopilotHost host, ILogger<CopilotWarmup> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var ready = await host.IsAvailableAsync(stoppingToken);
            log.LogInformation("Copilot runtime warm-up finished. AI available: {Available}", ready);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Copilot runtime warm-up failed");
        }
    }
}
