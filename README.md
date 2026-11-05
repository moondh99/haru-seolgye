# 하루설계 (Haru)

흩어진 할 일을 붙여 넣으면, AI 에이전트 팀이 충돌 없는 하루 시간표를 1분 안에 만든다.

- 데모: https://api.proudbush-c0263160.koreacentral.azurecontainerapps.io (로그인 불필요, 첫 화면의 "샘플로 체험"을 누른다)
- 대회: 2026 청도 바이브코딩 해커톤 / 주제: 개인 생산성 향상 웹앱
- 문서: [PRD](PRD.md) (무엇을) · [TRD](TRD.md) (어떻게) · [TDD](TDD.md) (왜) · [AGENTS](AGENTS.md) (작업 규칙)

서버에 Copilot 토큰이 없으면 AI를 호출하지 않는다. 이때 화면은 기록해 둔 실제 실행을 "기록된 실행 재생 · AI 호출 없음"으로 보여 준다. 토큰을 넣는 방법은 "라이브 AI 켜기"에 있다.

## 문제

할 일이 메신저, 메모, 메일에 흩어져 있다. 아침마다 계획에 시간이 든다. 고정 일정과 마감이 겹친다. 하루 계획은 쉽게 과다해진다.

## 해결 방법

1. 할 일 원문을 붙여 넣는다.
2. 에이전트 6개가 협업한다. 정리 → (추정 ‖ 우선순위) → 배치 → 위험 점검 → 코칭.
3. MCP 도구가 날짜 해석, 빈 시간 계산, 충돌 검증을 결정적으로 수행한다. LLM은 시간 계산을 추측하지 않는다.
4. 근거가 있는 시간표와 위험 경고를 받는다. 확인한 뒤에만 `.ics`로 내보낸다.

## 기술 구성

| 계층 | 선택 |
|---|---|
| LLM | GitHub Copilot SDK (`GitHub.Copilot.SDK`) |
| 에이전트·워크플로우 | Microsoft Agent Framework (`Microsoft.Agents.AI.GitHub.Copilot`, `Microsoft.Agents.AI.Workflows`) |
| Instructions | `prompts/*.md` (에이전트별 1개) |
| Tools | MCP 서버 `Haru.Mcp` (별도 컨테이너) |
| 인프라 | .NET Aspire → Azure Container Apps, Container Registry, Log Analytics, Application Insights |

## 실행 방법

필요한 것: .NET 10 SDK, Python 3(평가 스크립트), Docker(이미지를 만들 때만), Copilot 권한이 있는 GitHub 로그인(`copilot` CLI) 또는 토큰.

```bash
dotnet tool restore
dotnet test                              # 단위·통합 테스트
dotnet run --project src/Haru.AppHost    # 로컬 실행 (Aspire 대시보드 포함)
python3 tests/eval/smoke_plan.py         # 실행 중인 서버로 계획 1회를 만든다 (--url로 주소를 바꾼다)
python3 tests/eval/injection_eval.py     # 프롬프트 주입 11건을 보낸다
```

로컬에서 Copilot 로그인이 없으면 환경 변수 `COPILOT_GITHUB_TOKEN`에 토큰을 넣는다. 토큰을 저장소에 커밋하지 않는다.

## 배포 방법

```bash
dotnet tool restore
env Azure__SubscriptionId=<구독 ID> Azure__Location=koreacentral Azure__ResourceGroup=rg-haru \
  dotnet aspire deploy --clear-cache --non-interactive
```

- `az login`과 Docker가 필요하다. 배포는 약 4분 걸린다.
- `--clear-cache`는 필수이다. 없으면 Aspire가 매개변수를 `~/.aspire`에 평문으로 저장한다.
- 환경 모드는 `WorkloadProfiles`로 고정했다. 서비스 기본값(Express)은 고정 세션과 .NET 런타임 설정을 거부한다 ([TDD](TDD.md) 14).
- 삭제: `az group delete -n rg-haru --yes`

## 라이브 AI 켜기

1. GitHub에서 세분화된 개인 액세스 토큰(PAT)을 만든다. 계정 권한 "Copilot Requests"만 준다.
2. `scripts/set-copilot-token.sh`를 실행하고 토큰을 입력한다. 입력은 화면에 보이지 않는다. 스크립트는 토큰을 표준 입력으로 `az`에 넘긴다. 토큰은 명령줄 인수에 나타나지 않는다. 스크립트가 Container Apps secret `copilot-github-token`을 바꾸고 리비전을 다시 시작한다.
3. `GET /api/config`가 `aiAvailable: true`를 돌려주면 입력마다 에이전트가 실제로 실행된다.

다시 배포하면 secret이 `unset`으로 돌아간다. 배포 때 `Parameters__copilot-token`을 함께 넘기거나, 배포 뒤에 스크립트를 다시 실행한다. 토큰을 지우려면 `scripts/set-copilot-token.sh --clear`를 실행한다.

## 저장소 구조

```text
src/Haru.AppHost/         Aspire 구성 (api, mcp, 로그, 모니터링)
src/Haru.ServiceDefaults/ OpenTelemetry, 상태 점검, 복원력
src/Haru.Api/             API, 정적 UI, 에이전트, 워크플로우
src/Haru.Mcp/             MCP 서버 (도구 4종)
prompts/                  에이전트별 instructions
tests/                    단위·통합 테스트, 스크립트 테스트(scripts/), 평가 스크립트(eval/)
scripts/                  라이브 AI 켜기
docs/                     API 계약, 다이어그램
```
