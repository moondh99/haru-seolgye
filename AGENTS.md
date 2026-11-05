# AGENTS.md

## Purpose
This file tells coding agents how to work in Haru (하루설계). Haru turns rough notes into a conflict-free day schedule.
Read `PRD.md`, `TRD.md`, and `TDD.md` before you change the design.

## Repository map
| Path | Content |
|---|---|
| `src/Haru.Api` | HTTP API, workflow, agents, Copilot SDK link, MCP client, `wwwroot` UI |
| `src/Haru.Mcp` | MCP server. Tools: `Tools/HaruTools.cs`. Logic: `Logic/` |
| `src/Haru.AppHost` | Aspire AppHost. It defines the Azure resources |
| `src/Haru.ServiceDefaults` | OpenTelemetry, health checks, service discovery |
| `prompts/*.md` | One instruction file for each agent |
| `tests/` | `Haru.Mcp.Tests`, `Haru.Api.Tests`, `scripts/` (offline tests for `scripts/`), `eval/` (scripts that you run by hand) |
| `docs/` | `api-contract.md`, `diagrams/*.mmd` |

## Commands (run from the repository root)
| Task | Command |
|---|---|
| Restore tools | `dotnet tool restore` |
| Build | `dotnet build` |
| Test | `dotnet test` |
| Test the scripts | `python3 -m unittest discover -s tests/scripts` |
| Run all services | `dotnet run --project src/Haru.AppHost` |
| Run one service | `dotnet run --project src/Haru.Mcp`, then `dotnet run --project src/Haru.Api` |
| Smoke test | `python3 tests/eval/smoke_plan.py` |
| Injection test | `python3 tests/eval/injection_eval.py` |
| Build the api image | `dotnet aspire do build-api` |
| Deploy | See Azure rules. Ask the user first |

- Set `COPILOT_GITHUB_TOKEN` for live AI. Without it, `POST /api/plan` returns 503 `AI_UNAVAILABLE`.
- For the injection test, start the api with `HARU_PLANS_PER_WINDOW=100 HARU_MAX_CONCURRENT=3`.

## Architecture rules
1. Keep four layers apart: model, agents, instructions, tools.
2. Define agents in `src/Haru.Api/Agents/AgentCatalog.cs`. Each agent has an id, a model tier, and a tool allow-list.
3. Write instructions only in `prompts/<agent>.md`. Do not put prompt text in C# code.
4. Write tools only in `src/Haru.Mcp`. A tool is a pure function. It has no file, network, or secret access.
5. Define the workflow graph in `src/Haru.Api/Workflow/PlanWorkflow.cs`. Code does time math and rule checks. The model does not.
6. Give each agent only the tools it needs. No allow-list contains `export_ics`.
7. Only the `intake` agent reads raw user text. Later agents get only sanitized structured fields.
8. Create a new Copilot session for each agent call. Keep `CopilotClientMode.Empty`.
9. Call MCP tools from code with `McpGateway`. Do not open a second MCP connection.

## Coding rules
- Target .NET 10. Keep package versions exact in `*.csproj`.
- Treat model output as untrusted. Validate each field. Give each agent step a fallback or a clear error code.
- Put each limit (length, count, time) in `HaruOptions` or in the tool code.
- Write a test for each new rule. Put it in `tests/Haru.Mcp.Tests` (tool logic) or `tests/Haru.Api.Tests` (api and workflow).
- Log tool names, outcomes, and durations. Do not log user text.
- Read settings from environment variables (`HARU_*`). Do not hard-code them.

## Security and privacy rules
- Never write a token, key, or personal data in a file, log, test, or document.
- Keep the Copilot token in `COPILOT_GITHUB_TOKEN`. In Azure, use a Container Apps secret.
- Do not add a database. Do not write user text to disk in the api code.
- Keep telemetry content capture off (`EnableSensitiveData=false`).
- Keep the CSP and the security headers in `src/Haru.Api/Program.cs`. In the UI, use `textContent`, never `innerHTML`.
- After a change to agents, instructions, or the workflow, run the injection test. All cases must pass.

## Prohibitions
- Do not call `export_ics` from an agent. Only `POST /api/export` calls it, after user confirmation.
- Do not use `RuntimeConnection.ForInProcess()` in the Linux container. It can hang `CreateSessionAsync`.
- Do not add an Azure service, a database, or a cache without a decision record in `TDD.md` and user approval.
- Do not claim a result that you did not measure.

## Done criteria (all must be true)
1. `dotnet build` has 0 errors and `dotnet test` passes.
2. For a workflow change, `smoke_plan.py` passes (valid plan, 120 seconds or less).
3. For an agent, instruction, or workflow change, `injection_eval.py` passes all cases.
4. `docs/api-contract.md` matches the API.
5. `PRD.md`, `TRD.md`, and `TDD.md` match the code.
6. The diff has no secret.

## Documentation rules
- Change `PRD.md`, `TRD.md`, and `TDD.md` together when the design changes.
- Keep the TRD chapter numbers. `PRD.md` section 9 cites chapters 4, 5, 7, 8, 9, and 10.
- Keep `docs/diagrams/*.mmd` identical to the Mermaid blocks in `TRD.md`.
- Korean documents: plain style ("~한다"). One idea in each sentence. One term for one thing.
- English documents: ASD-STE100. Steps have 20 words or fewer. Use simple present and active voice.
- Use tables and short lists. Put the most important fact first. Do not use marketing words.
- Check each fact in the code. Mark an unverified fact "예정" or "estimate".

## Azure rules
- Ask the user before you create, change, or delete an Azure resource.
- Ask the user before any action that costs money, such as `dotnet aspire deploy`.
- After approval, run `dotnet tool restore`. Then run `env Azure__SubscriptionId=<ID> Azure__Location=koreacentral Azure__ResourceGroup=rg-haru dotnet aspire deploy --clear-cache --non-interactive`.
- Keep `--clear-cache`. Without it, Aspire stores the parameters in plain text under `~/.aspire`.
- Do not pass `--arch`. Docker must run.
- Keep `environmentMode: WorkloadProfiles` in `src/Haru.AppHost/Program.cs`. Without it, the service can create an Express environment. Express rejects sticky sessions and the .NET runtime setting (`TDD.md` 14).
- The deploy sets the secret `copilot-github-token` to `unset`. The api then replays the recorded run. For live AI, the user runs `scripts/set-copilot-token.sh` with a fine-grained token that has only "Copilot Requests".
- Never copy a personal login token to Azure. Never write a secret in a file or a command line.
- After a deploy, check `/health`, `/api/config`, and both container apps. Then delete `~/.aspire/deployments`.
- Delete everything with `az group delete -n rg-haru --yes`.
- Use the region `koreacentral`. The subscription policy allows five regions only.
