using System.Diagnostics;
using System.Text;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Haru.Api.Agents;
using Haru.Api.Config;
using Haru.Api.Mcp;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace Haru.Api.Copilot;

/// <summary>
/// Runs one agent turn through the GitHub Copilot SDK, wrapped as a Microsoft Agent Framework agent.
/// Each call gets a fresh, lean session: its own instructions, an allow-list of tools and no shared chat history.
/// </summary>
public sealed class CopilotAgentRunner(
    CopilotHost host,
    IMcpGateway mcp,
    AgentCatalog catalog,
    PromptLoader prompts,
    HaruOptions options,
    ILogger<CopilotAgentRunner> log) : IAgentRunner
{
    private static readonly TimeSpan DeltaInterval = TimeSpan.FromMilliseconds(120);

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct)
    {
        var spec = catalog.Get(request.AgentId);
        var client = await host.GetClientAsync(ct);
        var signal = request.OnSignal ?? (static _ => ValueTask.CompletedTask);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(options.AgentTimeoutSeconds));
        var token = limit.Token;
        var clock = Stopwatch.StartNew();
        AgentSession? session = null;

        try
        {
            var gate = new SemaphoreSlim(1, 1);
            async ValueTask Guarded(AgentSignal s)
            {
                await gate.WaitAsync(CancellationToken.None);
                try { await signal(s); }
                finally { gate.Release(); }
            }

            var tools = await mcp.CreateToolsAsync(spec.Tools, Guarded, token);
            var allowed = spec.Tools.ToHashSet();

            var config = new SessionConfig
            {
                Model = catalog.ModelFor(spec),
                ReasoningEffort = catalog.EffortFor(spec),
                Streaming = true,
                AvailableTools = [.. spec.Tools],
                Tools = [.. tools],
                OnPermissionRequest = (req, _) =>
                {
                    if (req is PermissionRequestCustomTool custom && allowed.Contains(custom.ToolName))
                        return Task.FromResult(PermissionDecision.ApproveOnce());
                    log.LogWarning("Denied a permission request from agent {Agent}: {Kind}", spec.Id, req.GetType().Name);
                    return Task.FromResult(PermissionDecision.Reject("This agent may only call its listed tools."));
                },
                SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Replace,
                    Content = prompts.Get(spec.PromptFile),
                },
                InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            };

            AIAgent agent = client
                .AsAIAgent(config, ownsClient: false, id: spec.Id, name: spec.Id, description: spec.Role)
                .AsBuilder()
                .UseOpenTelemetry("Haru.Agents", o => o.EnableSensitiveData = false)
                .Build();

            session = await agent.CreateSessionAsync(token);

            var messages = new List<(string? Id, StringBuilder Text)>();
            var pending = new StringBuilder();
            var lastFlush = Stopwatch.StartNew();
            long input = 0, output = 0;
            string? model = null;

            await foreach (var update in agent.RunStreamingAsync(request.UserPrompt, session, cancellationToken: token))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent { Text.Length: > 0 } text:
                            if (messages.Count == 0 || messages[^1].Id != update.MessageId)
                                messages.Add((update.MessageId, new StringBuilder()));
                            messages[^1].Text.Append(text.Text);
                            pending.Append(text.Text);
                            if (lastFlush.Elapsed >= DeltaInterval)
                            {
                                await Guarded(new DeltaSignal(pending.ToString()));
                                pending.Clear();
                                lastFlush.Restart();
                            }
                            break;

                        case UsageContent usage:
                            input += usage.Details.InputTokenCount ?? 0;
                            output += usage.Details.OutputTokenCount ?? 0;
                            if (usage.RawRepresentation is AssistantUsageEvent { Data.Model: { Length: > 0 } used })
                                model = used;
                            break;
                    }
                }
            }

            if (pending.Length > 0) await Guarded(new DeltaSignal(pending.ToString()));

            // The final answer is the last assistant message; earlier ones are commentary before tool calls.
            var answer = messages.LastOrDefault(m => m.Text.ToString().Trim().Length > 0).Text?.ToString()
                         ?? string.Empty;
            return new AgentRunResult(answer, model, input, output, clock.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Agent '{spec.Id}' did not finish within {options.AgentTimeoutSeconds} s.");
        }
        finally
        {
            // Session events hold the prompt text. Delete them, so no user text stays on disk.
            if (session is GitHubCopilotAgentSession { SessionId: { Length: > 0 } id })
                await host.DeleteSessionAsync(id);
        }
    }
}
