using System.Diagnostics;
using Haru.Api.Agents;
using Haru.Api.Config;
using Haru.Api.Contracts;
using Haru.Api.Infrastructure;
using Haru.Api.Mcp;
using Microsoft.Agents.AI.Workflows;

namespace Haru.Api.Workflow;

/// <summary>Runs one plan workflow and turns its events into the SSE stream described in docs/api-contract.md.</summary>
public sealed class PlanService(
    HaruOptions options, AgentCatalog catalog, AgentCaller caller, IMcpGateway mcp, ILogger<PlanService> log)
{
    public async Task RunAsync(PlanInput input, SseStream sse, CancellationToken requestAborted)
    {
        var runId = Guid.NewGuid().ToString("N")[..12];
        var state = new RunState(runId);
        using var activity = HaruTelemetry.Workflow.StartActivity("haru.plan");
        activity?.SetTag("haru.run_id", runId);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        budget.CancelAfter(TimeSpan.FromSeconds(options.PlanTimeoutSeconds));

        await sse.SendAsync("run_started", new { runId, day = input.Day, aiAvailable = true });

        PlanResult? result = null;
        Exception? error = null;
        try
        {
            var workflow = PlanWorkflow.Build(new RunServices(state, caller, mcp, catalog, options));
            await using var run = await InProcessExecution.RunStreamingAsync(workflow, input, cancellationToken: budget.Token);
            try
            {
                await foreach (var e in run.WatchStreamAsync(budget.Token))
                {
                    switch (e)
                    {
                        case HaruEvent h:
                            await sse.SendAsync(h.Name, h.Data);
                            break;
                        case WorkflowOutputEvent { Data: PlanResult plan }:
                            result = plan;
                            break;
                        case WorkflowErrorEvent { Exception: { } ex }:
                            error ??= ex;
                            break;
                        case ExecutorFailedEvent { Data: Exception ex }:
                            error ??= ex;
                            break;
                    }
                }
            }
            finally
            {
                if (result is null) await run.CancelRunAsync();
            }
        }
        catch (Exception e)
        {
            error ??= e;
        }

        var elapsed = state.Clock.ElapsedMilliseconds;
        if (requestAborted.IsCancellationRequested)
        {
            Finish(activity, state, "cancelled", elapsed);
            return;
        }

        if (result is not null)
        {
            await sse.SendAsync("plan", result);
            await sse.SendAsync("done", new { elapsedMs = elapsed, source = "ai", repaired = result.Meta.Repaired });
            Finish(activity, state, "ok", elapsed);
            return;
        }

        var failure = state.Failure ?? Describe(error, budget.IsCancellationRequested);
        log.LogWarning("Plan run {RunId} failed with {Code} after {ElapsedMs} ms ({ErrorType})",
            runId, failure.Code, elapsed, error?.GetType().Name ?? "none");
        await sse.SendAsync("error", new { code = failure.Code, message = failure.Message, recoverable = failure.Recoverable });
        Finish(activity, state, failure.Code == "TIMEOUT" ? "timeout" : "error", elapsed);
    }

    private static void Finish(Activity? activity, RunState state, string outcome, long elapsedMs)
    {
        HaruTelemetry.Plans.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        HaruTelemetry.PlanDuration.Record(elapsedMs, new KeyValuePair<string, object?>("outcome", outcome));
        activity?.SetTag("haru.outcome", outcome);
        activity?.SetTag("haru.scheduler_runs", state.SchedulerRuns);
        activity?.SetTag("haru.tokens.input", state.InputTokens);
        activity?.SetTag("haru.tokens.output", state.OutputTokens);
    }

    /// <summary>Maps any failure to a safe Korean message. Exception text never reaches the user.</summary>
    public static (string Code, string Message, bool Recoverable) Describe(Exception? error, bool timedOut)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is PlanException p) return (p.Code, p.Message, p.Recoverable);
            if (e is TimeoutException) return Timeout;
            if (e is OperationCanceledException && timedOut) return Timeout;
            if (e is AgentOutputException)
                return ("WORKFLOW_FAILED", "AI 응답을 이해하지 못했습니다. 다시 시도해 주세요.", true);
            if (e is McpToolException)
                return ("WORKFLOW_FAILED", "검증 도구에 연결하지 못했습니다. 잠시 후 다시 시도해 주세요.", true);
        }
        return timedOut
            ? Timeout
            : ("WORKFLOW_FAILED", "계획을 만들지 못했습니다. 다시 시도해 주세요.", true);
    }

    private static readonly (string, string, bool) Timeout =
        ("TIMEOUT", "AI 응답이 늦어져 중단했습니다. 잠시 후 다시 시도해 주세요.", true);
}
