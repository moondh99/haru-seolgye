# Azure Deployment Plan

> **Status:** Deployed

Live endpoint: https://api.proudbush-c0263160.koreacentral.azurecontainerapps.io (verified 2026-11-05 13:44 KST). The `mcp` app has no public endpoint.

Generated: 2026-11-05 13:28 KST

Approval basis: the user asked for this deployment in the original request. The gate question (resources, cost, delete command) was asked at 13:28 KST. The answer was "The user is not available to respond and will review your work later. Work autonomously and make good decisions." I proceed with the smallest removable deployment. Existing resources (`rg-yangju-bus-collector`, `NetworkWatcherRG`) are not touched.

---

## 1. Project Overview

**Goal:** Deploy 하루설계 (Haru), a guest-mode personal-productivity web app, for the 2026 청도 바이브코딩 해커톤. The API (GitHub Copilot SDK + Microsoft Agent Framework workflow) serves the SPA and calls a separate MCP tool server.

**Path:** New Project

---

## 2. Requirements

| Attribute | Value |
|-----------|-------|
| Classification | POC (hackathon demo, public, no login) |
| Scale | Small |
| Budget | Cost-Optimized |
| **Subscription** | Azure for Students (ID omitted from this public file; the only Enabled subscription of the signed-in account). The user was unreachable, so this choice is an assumption |
| **Location** | `koreacentral` (policy allows only indiasouthcentral, koreacentral, uaenorth, indonesiacentral, eastasia) |
| Resource group | `rg-haru` (new) |
| Domain | Default `*.azurecontainerapps.io` only (hackathon rule) |

---

## 3. Components Detected

| Component | Type | Technology | Path |
|-----------|------|------------|------|
| api | API + static SPA (external ingress) | ASP.NET Core 10, GitHub.Copilot.SDK, Microsoft.Agents.AI.Workflows | `src/Haru.Api` |
| mcp | Tool server (internal ingress only) | ASP.NET Core 10, ModelContextProtocol (Streamable HTTP, stateless) | `src/Haru.Mcp` |
| apphost | Deployment definition | .NET Aspire 13.6.4 | `src/Haru.AppHost` |
| defaults | Shared OpenTelemetry, health, resilience | .NET library | `src/Haru.ServiceDefaults` |

---

## 4. Recipe Selection

**Selected:** Aspire AppHost, deployed with `dotnet aspire deploy` (AZD is the fallback)

**Rationale:**
- The hackathon notes name `aspire deploy`. The AppHost is the single source of truth, so no `azure.yaml` or `infra/` is hand-written.
- Fallback if `aspire deploy` fails: `azd init --from-code`, `azd provision`, `set-aspire-aca-env.sh`, `azd deploy` (azure-validate `recipes/azd/aspire.md`).
- **Deviation:** azure-prepare and azure-deploy default to AZD for Aspire. `aspire deploy` is used because the user asked for it.
- Cache footgun: Aspire stores parameter values, including secrets, in plain text under `~/.aspire/deployments`. Always pass `--clear-cache` and delete that folder after the run.
- **Express default (found at the first deploy, 13:35 KST).** An environment without `environmentMode` was created in **Express** mode on this subscription. Express rejects `stickySessions` (`ExpressStickySessionsNotSupported`) and the .NET `runtime` setting (`ExpressDotnetRuntimeNotSupported`), has no internal service discovery, and lists Aspire as unsupported. The AppHost therefore sets `environmentMode: 'WorkloadProfiles'` with API `2026-03-02-preview` (the same shim Aspire uses for `AsExpress()`). The Express environment was deleted and the deploy repeated: 28 of 28 steps passed in 3 min 31 s.

---

## 5. Architecture

**Stack:** Containers (Azure Container Apps, Consumption profile only)

### Service Mapping

| Component | Azure Service | SKU |
|-----------|---------------|-----|
| api | Container App, external HTTPS ingress, sticky sessions, min 1 / max 2 replicas, 0.5 vCPU / 1 GiB | Consumption |
| mcp | Container App, internal ingress only, min 1 / max 2 replicas, 0.25 vCPU / 0.5 GiB | Consumption |
| Environment | Container Apps managed environment, `environmentMode: WorkloadProfiles` (Aspire dashboard off) | Consumption workload profile |
| Images | Azure Container Registry | Basic |

### Supporting Services

| Service | Purpose |
|---------|---------|
| Log Analytics | One workspace (PerGB2018), shared by the environment and Application Insights |
| Application Insights | Workspace-based; OpenTelemetry traces, metrics and logs. No user text in logs |
| Managed Identity | User-assigned; `AcrPull` on the registry only. No ACR admin user |
| Container Apps secret | `copilot-token` (api only). Key Vault is not used: one secret, so Key Vault would be a needless service |

Rejected: Key Vault, Azure OpenAI/AI Foundry (no quota on this subscription; the Copilot SDK is the required LLM path), Cosmos/SQL (no server-side state), API Management, Front Door, VNet (adds meters, none needed).

### Secret decision

- Deploy with `copilot-token = unset`. The API then reports `aiAvailable=false`, and the SPA replays a **recorded real run** labelled "AI 호출 없음". The core feature works for any visitor with no login.
- Live AI needs a **fine-grained PAT with only "Copilot Requests"**, created by the user. `scripts/set-copilot-token.sh` sets it as the Container Apps secret and restarts the revision. The Copilot app's own `gho_` session token is never copied to the cloud.

### Cost (retail list prices, koreacentral, fetched 2026-11-05)

| Item | Basis | ≈ USD/month |
|------|-------|-------------|
| api | 0.5 vCPU, 1 GiB, idle rate 0.000003 /s per vCPU and per GiB | 11.8 idle |
| mcp | 0.25 vCPU, 0.5 GiB, idle rate | 5.9 idle |
| Free grant | 180,000 vCPU-s and 360,000 GiB-s per subscription | −1.6 |
| ACR Basic | 0.1666 per day | 5.0 |
| Log Analytics, App Insights | 5 GB free ingestion; demo traffic is far below | ~0–1 |
| Requests | first 2 M free | 0 |
| **Total** | mostly idle | **≈ 21–23** (≈ 0.7 per day) |

- Active compute is 8× the idle rate (0.000024 vs 0.000003 per vCPU-s), so a busy day costs more. Worst case, always busy: ≈ 65 per month.
- The "Environment" management, private-endpoint and planned-maintenance meters (from 2026-11-01) apply only to Dedicated profiles, private endpoints and planned maintenance. None are used.
- Estimates are list prices; no bill was checked. The student credit is $100.
- Remove everything with `az group delete -n rg-haru --yes` or `dotnet aspire destroy`.

---

## 6. Provisioning Limit Checklist

### Phase 1 and 2: Resource inventory with quota

| Resource Type | Number to Deploy | Total After Deployment | Limit/Quota | Notes |
|---------------|------------------|------------------------|-------------|-------|
| Microsoft.Resources/resourceGroups | 1 | 1 | not limiting | New group `rg-haru`; verified not existing before deploy |
| Microsoft.App/managedEnvironments | 1 | 1 | **1** (koreacentral) | azure-quota `ManagedEnvironmentCount`: limit 1, used 0. At the limit, so a failed partial deploy that leaves an environment must be reused or deleted before a retry |
| Microsoft.App/containerApps | 2 | 2 | no subscription quota listed | azure-quota returned only `ManagedEnvironmentCount` for Microsoft.App |
| Microsoft.ContainerRegistry/registries | 1 | 1 | no limit returned | azure-quota; Basic SKU |
| Microsoft.OperationalInsights/workspaces | 1 | 1 | no limit returned | azure-quota |
| Microsoft.Insights/components | 1 | 1 | no limit returned | azure-quota |
| Microsoft.ManagedIdentity/userAssignedIdentities | 1 | 1 | no limit returned | azure-quota |
| Microsoft.Authorization/roleAssignments | 1 | 1 | not limiting | `AcrPull` on the registry |

**Status:** ⚠️ Near limit (managed environments 1 of 1). Capacity is sufficient for a single deployment.

Provider registration (done, verified Registered): Microsoft.App, Microsoft.ContainerRegistry, Microsoft.ManagedIdentity, Microsoft.OperationalInsights, Microsoft.Insights.

Permissions: the deploying account has Owner on the subscription (needed for the role assignment). Docker is running. github.com is reachable (the Copilot CLI is fetched at publish time).

---

## 7. Execution Checklist

### Phase 1: Planning
- [x] Analyze workspace
- [x] Gather requirements (hackathon notes)
- [x] Confirm subscription and location with user (user unreachable; gate answered "work autonomously"; assumption recorded in the final report)
- [x] Prepare resource inventory
- [x] Fetch quotas and validate capacity
- [x] Scan codebase
- [x] Select recipe
- [x] Plan architecture
- [x] **User approved this plan** (delegated: "Work autonomously and make good decisions")

### Phase 2: Execution
- [x] Generate infrastructure: AppHost (`src/Haru.AppHost`); Bicep is generated by Aspire at deploy time
- [x] Generate application configuration (parameters, environment, probes)
- [x] Containerized by Aspire (no hand-written Dockerfile)
- [x] Update plan status to "Ready for Validation"

### Phase 3: Validation
- [x] `dotnet build src/Haru.AppHost -c Release` has 0 warnings and 0 errors
- [x] `dotnet test Haru.sln` passes (MCP 421 tests, API 850 tests; 0 failed, 0 skipped)
- [x] `dotnet aspire publish` generates Bicep; `az bicep build` passes; ARM preflight `az deployment sub validate` succeeds
- [x] Static role review: only `AcrPull` (role `7f951dda-4ed3-4680-a7ca-43fe172d538d`) for the user-assigned identity on the registry
- [x] Linux x64 api image builds (`dotnet aspire do build-api`, 17 s) and passes the UI test in a local container
- [x] Update plan status to "Validated"
- [x] Record validation proof below

### Phase 4: Deployment
- [x] `dotnet aspire deploy --clear-cache --non-interactive` succeeds (second run; the first run failed on Express limits, see §4)
- [x] Live checks: `/health`, `/api/config`, `/api/sample`, security headers, `POST /api/export`, browser UI test
- [x] mcp reachable from api (internal), telemetry visible in Application Insights
- [x] Report endpoint URL; update plan status to "Deployed"

---

## 7. Validation Proof

| Check | Command Run | Result | Timestamp |
|-------|-------------|--------|-----------|
| Build | `dotnet build src/Haru.AppHost -c Release -v q` | ✅ 0 warnings, 0 errors | 13:28 KST |
| Unit tests (MCP) | `dotnet test tests/Haru.Mcp.Tests` | ✅ 421 passed | 13:33 KST |
| Unit tests (API) | `dotnet test Haru.sln --no-build` | ✅ 850 passed, 0 skipped (MCP 421 passed) | 14:03 KST |
| Bicep generation | `dotnet aspire publish -o /tmp/haru-bicep --non-interactive` | ✅ identical to the reviewed output | 13:29 KST |
| Bicep compile | `az bicep build --file main.bicep` | ✅ one linter warning (unused `userPrincipalId`, generator output) | 13:29 KST |
| ARM preflight | `az deployment sub validate -l koreacentral -f main.bicep -p resourceGroupName=rg-haru …` | ✅ Succeeded | 13:30 KST |
| Image build | `dotnet aspire do build-api --non-interactive` | ✅ `api:latest` 588 MB, linux-x64 | 13:31 KST |
| Container smoke | `docker run --platform linux/amd64 api:latest` with `COPILOT_GITHUB_TOKEN=unset`; `/health` 200, `/api/config` `aiAvailable:false`, `/api/sample` 37 events, CSP headers, `node test-noai.mjs` | ✅ 12/12 | 13:32 KST |
| Resource group free | `az group show -n rg-haru` | ✅ not found (name free) | 13:28 KST |

**Validated by:** the lead agent, running the checks of the azure-validate workflow that apply to an Aspire AppHost (the skill's `workflow.sh` is written for AZD)
**Validation timestamp:** 2026-11-05 13:33 KST

### Deployment proof (13:43–13:45 KST)

| Check | Result |
|-------|--------|
| `dotnet aspire deploy --clear-cache --non-interactive` | ✅ 28/28 steps, 3 min 31 s |
| Environment mode (`az rest`, API 2026-03-02-preview) | ✅ `WorkloadProfiles` |
| `api` app | ✅ Running, external ingress, min 1 / max 2, 0.5 vCPU / 1 GiB |
| `mcp` app | ✅ Running, internal ingress only (`mcp.internal.<domain>`), min 1 / max 2, 0.25 vCPU / 0.5 GiB |
| `/health`, `/api/config`, `/api/sample` | ✅ 200; `aiAvailable:false`; 37 recorded events |
| Security headers (CSP, HSTS, nosniff, X-Frame-Options, Referrer-Policy, COOP) | ✅ present |
| `node test-noai.mjs` against the live URL | ✅ 12/12, including `.ics` export (6 VEVENTs), which calls the internal `mcp` |
| Log Analytics (`AppRequests`, `AppTraces`) | ✅ rows for roles `api` and `mcp` within one minute |

### Redeploy proof (14:06–14:09 KST)

The redeploy adds the liveness and readiness probes, the MCP tool-cache warm-up, security headers on error responses, and an atomic usage limiter. The commit is `fff0a82`.

| Check | Result |
|-------|--------|
| `dotnet aspire deploy --clear-cache --non-interactive` | ✅ succeeded in 1 min 32 s; new revisions `api--0000001` and `mcp--0000001` Running and Healthy |
| Probes (`az containerapp show`) | ✅ `Liveness` (`/alive`) and `Readiness` (`/health`) on both apps |
| Ingress | ✅ `api` external, `mcp` internal only |
| `/alive`, `/health`, `/api/config` | ✅ 200; `aiAvailable:false` |
| Security headers on `/` | ✅ CSP, HSTS, nosniff, X-Frame-Options, Referrer-Policy, Permissions-Policy, COOP |
| `node test-noai.mjs` against the live URL | ✅ 12/12, including `.ics` export through the internal `mcp` |
| Console log of `api` | ✅ no "Tool not found in cache" warning |

### Redeploy proof (15:02–15:08 KST)

The redeploy adds deletion of Copilot session records, the external-AI notice, the retention panel, and the BUSY retry. The commit is `1b845e9`. The token was read into memory before the deploy and set again after it.

| Check | Result |
|-------|--------|
| `dotnet aspire deploy --clear-cache --non-interactive` | ✅ succeeded in 96 s; `api--0000004` and `mcp--0000002` Running |
| `scripts/set-copilot-token.sh` (token through stdin) | ✅ `aiAvailable:true` |
| `/health`, `/api/config` | ✅ 200; `aiAvailable:true` |
| New UI served (`#ai-notice`, `BUSY_TRIES`) | ✅ after the old revision drained (about 1 min) |
| `smoke_plan.py` against the live URL | ✅ PASS; `source: ai`, valid, load 84%, 37.4 s |
| `/tmp/haru-copilot/session-state` in the `api` container after the run | ✅ no session folder (only `.session-operation-locks`) |
| `~/.aspire/deployments` | ✅ deleted |

---

## 8. Files to Generate

| File | Purpose | Status |
|------|---------|--------|
| `.azure/deployment-plan.md` | This plan | ✅ |
| `src/Haru.AppHost/Program.cs` | Aspire deployment definition (IaC in C#) | ✅ |
| `aspire.config.json`, `.config/dotnet-tools.json` | Repo-local Aspire CLI | ✅ |
| `scripts/set-copilot-token.sh` | Enable live AI with a user-created PAT | ✅ |

---

## 9. Next Steps

> Current: Deployed

1. Live AI is on. The fine-grained PAT expires on its set date. Before it expires, create a new PAT and run `scripts/set-copilot-token.sh` again.
2. After any code change, run `dotnet aspire deploy --clear-cache --non-interactive` again, then repeat the live checks.
3. Remove everything: `az group delete -n rg-haru --yes`.
