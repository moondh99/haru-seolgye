# API 계약 (Contract)

이 문서는 UI, API, MCP 서버가 따르는 단일 계약이다. 코드와 문서가 다르면 이 문서를 먼저 고친다.
모든 JSON 키는 camelCase다. 모든 시각은 한국 표준시(KST, +09:00)다. 시각 문자열은 `HH:mm`(24시간)이다.

## 1. 공통 타입

```ts
type Day = string;            // "2026-11-05"
type Hhmm = string;           // "09:30"
type Priority = 1 | 2 | 3;    // 1 = 높음

interface TaskItem {
  id: string;                 // "t1", "t2", ...
  title: string;              // 최대 80자, 제어 문자 없음
  minutes: number;            // 5~240, 5의 배수
  priority: Priority;
  deadline: string | null;    // "2026-11-05T17:00:00+09:00" 또는 "2026-11-06"(날짜만)
  fixedStart: Hhmm | null;    // kind가 fixed일 때만 값이 있다
  fixedEnd: Hhmm | null;
  kind: "task" | "fixed";
  note: string | null;        // 최대 120자
}

interface Block {
  id: string;                 // "b1", "b2", ...
  taskId: string | null;      // 휴식(break)이면 null
  title: string;
  start: Hhmm;
  end: Hhmm;                  // start보다 늦다
  kind: "task" | "fixed" | "break";
  reason: string;             // 한국어 한 문장, 최대 120자
}

type ViolationCode =
  | "INVALID_RANGE" | "OUT_OF_HOURS" | "OVERLAP"
  | "NO_BUFFER" | "OVERLOAD" | "DEADLINE_MISSED";

interface Violation { code: ViolationCode; message: string; blockIds: string[]; }

interface ValidationReport {
  valid: boolean;
  loadRatio: number;          // 0~, 소수점 2자리
  occupiedMinutes: number;
  availableMinutes: number;
  violations: Violation[];
}

interface Deferred { taskId: string; title: string; reason: string; }
interface Risk { title: string; mitigation: string; severity: "low" | "medium" | "high"; }

interface PlanResult {
  day: Day;
  workStart: Hhmm;
  workEnd: Hhmm;
  tasks: TaskItem[];
  blocks: Block[];            // start 오름차순
  deferred: Deferred[];
  risks: Risk[];              // 최대 3개
  summary: string;            // 코치 요약, 한국어, 최대 300자
  validation: ValidationReport;
  meta: {
    source: "ai" | "offline-sample";
    aiGenerated: boolean;     // source가 ai이면 true
    iterations: number;       // 스케줄러 실행 횟수
    repaired: boolean;        // 결정적 보정을 적용했으면 true
    elapsedMs: number;
    models: { fast: string; smart: string } | null;
  };
}
```

## 2. HTTP API (`Haru.Api`, 외부 공개)

| 메서드·경로 | 설명 |
|---|---|
| `GET /api/config` | `{ aiAvailable, today, limits: { maxInputChars: 3000, plansPerWindow, windowMinutes }, defaults: { workStart: "09:00", workEnd: "18:00", lunchStart: "12:00", lunchEnd: "13:00" }, version }` |
| `POST /api/plan` | 요청 본문은 아래와 같다. 응답은 SSE(`text/event-stream`)다. |
| `GET /api/sample` | `{ inputText, plan: PlanResult, events }`. `plan`은 오프라인 샘플이고 `meta.source`는 `offline-sample`이다. `events`는 실제 실행을 기록한 SSE 이벤트 목록 `{ t: number, event: string, data: object }[]`이다(`t`는 ms, `agent_delta`는 없다). 기록 파일이 없으면 `events`는 `null`이다. UI는 `events`가 있으면 재생하고, 없으면 `plan`만 보인다. |
| `POST /api/export` | 요청 `{ confirmed: true, day, workStart?, workEnd?, blocks: Block[] }`. `blocks`는 1~50개다. `workStart`·`workEnd`를 생략하면 00:00~23:59로 검증한다. 응답은 `text/calendar` 첨부 파일 `haru-<day>.ics`다. `confirmed`가 `true`가 아니면 400이다. `validate_schedule`(`maxLoad` 1.0)로 다시 검증하고 실패하면 422다. 내보내기 도구에 연결하지 못하면 503 `AI_UNAVAILABLE`이다. |
| `GET /health`, `GET /alive` | 상태 점검. Container Apps 프로브가 쓴다. |

`POST /api/plan` 요청 본문:

```json
{ "text": "원문(1~3000자)", "day": "2026-11-05", "workStart": "09:00", "workEnd": "18:00",
  "lunchStart": "12:00", "lunchEnd": "13:00" }
```

`day`와 근무·점심 시간은 생략할 수 있다. 생략하면 서버가 기본값(KST 오늘)을 쓴다. `lunchStart`나 `lunchEnd`가 빈 문자열이면 점심 시간이 없다. `day`는 어제부터 1년 안이어야 하고, 근무 시간은 1시간 이상이어야 한다.

API는 `fixed` 블록끼리 이웃해서 생기는 `NO_BUFFER`를 위반으로 세지 않는다. 사용자가 정한 일정이기 때문이다. 이 보정은 계획 검증과 내보내기 검증에 모두 적용한다.

스트림이 시작되기 전의 오류는 JSON `{ "error": { "code", "message" } }`로 답한다.

| 상태 | code | 뜻 |
|---|---|---|
| 400 | `INPUT_EMPTY`, `INPUT_TOO_LONG`, `BAD_REQUEST`, `NOT_CONFIRMED` | 잘못된 입력 |
| 422 | `INVALID_SCHEDULE` | 내보낼 시간표가 검증을 통과하지 못함 |
| 429 | `RATE_LIMITED`, `DAILY_CAP` | 사용량 제한 |
| 500 | `INTERNAL` | 처리하지 못한 예외. 고정 문구만 보낸다. |
| 503 | `BUSY`, `AI_UNAVAILABLE` | 동시 실행 한도 초과, AI 미연결. 내보내기에서는 `AI_UNAVAILABLE`이 변환 도구 연결 실패를 뜻한다. |

### 2.1 SSE 이벤트

형식은 `event: <이름>` 줄과 `data: <JSON>` 줄이다. 15초마다 `: ping` 주석 줄을 보낸다.
에이전트 id는 `intake`, `estimator`, `prioritizer`, `scheduler`, `critic`, `coach`다.
코드 단계 id는 `normalize`, `validator`, `repair`다.

| event | data |
|---|---|
| `run_started` | `{ runId, day, aiAvailable }` |
| `agent_started` | `{ agent, role, model }` (`role`은 한국어 한 줄 설명) |
| `agent_delta` | `{ agent, text }` |
| `tool_call` | `{ agent, tool, args }` |
| `tool_result` | `{ agent, tool, ok, summary }` |
| `agent_completed` | `{ agent, summary, elapsedMs }` |
| `tasks` | `{ tasks: TaskItem[] }` |
| `schedule` | `{ iteration, blocks: Block[], deferred: Deferred[] }` |
| `validation` | `{ iteration, report: ValidationReport }` |
| `critique` | `{ iteration, decision: "approve" \| "revise", risks: Risk[] }` |
| `plan` | `PlanResult` |
| `done` | `{ elapsedMs, source: "ai" \| "offline-sample", repaired }` |
| `error` | `{ code, message, recoverable }`. `code`는 `NO_TASKS`(입력에서 할 일을 찾지 못함), `TIMEOUT`(시간 초과), `WORKFLOW_FAILED`(AI 응답 해석 실패, 검증 도구 연결 실패, 그 밖의 실패)다. `message`는 고정된 한국어 문장이다. |

`error` 뒤에는 `done`이 오지 않는다. 스트림은 `plan`과 `done`으로 끝나거나 `error`로 끝난다.
UI는 오류를 받으면(스트림 시작 전 오류 포함) 안내 배너를 보이고 `GET /api/sample`의 결과를 대신 보인다. 기록된 실행이 있으면 "기록된 실행 재생 · AI 호출 없음", 없으면 "오프라인 샘플 · AI 호출 없음"으로 표시한다. 입력 문제(`INPUT_EMPTY`, `INPUT_TOO_LONG`, `BAD_REQUEST`, `NO_TASKS`)는 샘플로 바꾸지 않고 오류만 보인다.

## 3. MCP 서버 (`Haru.Mcp`, 내부 전용)

전송 방식은 Streamable HTTP, 경로는 `/mcp`, 무상태(stateless)다. 도구 이름은 snake_case, 매개변수는 camelCase다.
모든 도구는 순수 함수다. 외부 호출과 저장소 접근이 없다. 결과는 JSON 객체 하나다.

| 도구 | 호출 주체 |
|---|---|
| `resolve_date` | `normalize` 코드 단계 |
| `find_free_slots` | scheduler 에이전트 |
| `validate_schedule` | scheduler 에이전트, `validator` 코드 단계, export 전 검증 |
| `export_ics` | API(사용자 확인 후에만). 어떤 에이전트에도 주지 않는다. |

### 3.1 `resolve_date`

- 입력: `expression: string`(최대 60자), `referenceDate: string`(`YYYY-MM-DD`)
- 출력: `{ ok: boolean, date: string|null, time: string|null, normalized: string|null, error: string|null }`
- 규칙:
  - 주는 월요일에 시작한다.
  - `오늘`, `내일`, `모레`, `글피`, `N일 후|뒤`를 읽는다.
  - `이번 주 금요일`은 기준일과 같은 주(월~일)의 금요일이다.
  - `다음 주 월요일`, `다다음 주 수요일`은 주 단위로 센다.
  - `금요일`, `금요일까지`는 기준일 이후(기준일 포함) 첫 금요일이다.
  - 이번 달 말, 이번 달 말일, 다음 달 5일, 11월 7일, 11/7, 7일을 읽는다. `7일`이 이미 지났으면 다음 달로 본다. `11월 7일`처럼 월·일이 기준일보다 앞서면 다음 해로 본다.
  - 시각은 `오전|오후|아침|점심|저녁|밤 N시`, `N시 반`, `N시 M분`, `HH:mm`, `N시`(24시간제), `정오`, `자정`을 읽는다.
  - 오전·오후 표시가 없는 `N시`는 1~6시를 오후, 7~11시를 오전으로 본다. 12시는 12:00, 0시는 00:00이다. 13시 이상은 24시간제다.
  - `아침`은 오전, `점심`은 오후(12시는 12:00), `저녁`과 `밤`은 오후로 읽는다.
  - 날짜와 시각이 함께 있으면 둘 다 채운다(예: `내일 오후 3시` → date, `15:00`).
  - 읽지 못하면 `ok: false`이고 `error`에 이유를 쓴다.
  - `normalized`는 `YYYY-MM-DD` 또는 `YYYY-MM-DD HH:mm` 또는 `HH:mm`이다.

### 3.2 `find_free_slots`

- 입력: `workStart`, `workEnd`, `busy: { start, end }[]`, `minMinutes: int`(기본 5), `bufferMinutes: int`(기본 5)
- 출력: `{ slots: { start, end, minutes }[], totalFreeMinutes: int }`
- 규칙: busy 구간을 근무 시간에 맞게 자르고 정렬해 합친다. 빈 구간은 busy와 맞닿은 쪽에서 `bufferMinutes`만큼 줄인다. 근무 시간 가장자리에는 여유를 두지 않는다. `minMinutes`보다 짧은 구간은 버린다.

### 3.3 `validate_schedule`

- 입력: `workStart`, `workEnd`, `blocks: { id, title, start, end, kind, deadlineTime?: Hhmm }[]`, `bufferMinutes`(기본 5), `maxLoad`(기본 0.85)
- 출력: `{ valid, loadRatio, occupiedMinutes, availableMinutes, violations: Violation[] }`
- 검사(코드):
  - `INVALID_RANGE`: 형식 오류이거나 `start`가 `end`보다 늦거나 같다.
  - `OUT_OF_HOURS`: `task`, `break` 블록이 근무 시간 밖이다. `fixed` 블록은 어디에 있어도 된다.
  - `OVERLAP`: 두 블록의 시간이 겹친다.
  - `NO_BUFFER`: 시작 시각순으로 이웃한 두 블록 사이 간격이 `bufferMinutes`보다 작다. 둘 중 하나가 `break`이면 검사하지 않는다.
  - `OVERLOAD`: `loadRatio`가 `maxLoad`보다 크다.
  - `DEADLINE_MISSED`: `task` 블록의 `end`가 `deadlineTime`보다 늦다.
- `occupiedMinutes`는 `task`와 `fixed` 블록 길이의 합이다. `availableMinutes`는 (근무 시간 − `break` 블록 길이의 합)이다. `loadRatio = occupied / available`을 소수점 2자리로 반올림한다.
- `valid`는 `violations`가 비어 있을 때만 `true`다.
- `message`는 한국어 한 문장이다. 블록 제목과 시각을 넣는다.

### 3.4 `export_ics`

- 입력: `day`, `blocks: { id, title, start, end, kind, reason? }[]`, `calendarName?: string`(기본 `하루설계`)
- 출력: `{ filename: "haru-<day>.ics", ics: string, eventCount: int }`
- 규칙: RFC 5545를 따른다. 줄 끝은 CRLF다. 75옥텟을 넘는 줄은 접는다. `,` `;` `\` 줄바꿈은 이스케이프한다. `VTIMEZONE`으로 `Asia/Seoul`(+0900, 일광절약시간 없음)을 정의한다. `DTSTART;TZID=Asia/Seoul`을 쓴다. `UID`는 `<id>-<day>@haru`다. `DTSTAMP`는 `<day>T000000Z`로 고정한다(같은 입력이면 같은 출력). `break` 블록은 내보내지 않는다. `DESCRIPTION`에는 이유와 `AI가 만든 제안입니다(하루설계)`를 넣는다. `PRODID`는 `-//Haru//Haru 하루설계//KO`다.

### 3.5 입력 한도 (모든 도구)

- 문자열은 200자 이하(`expression`은 60자 이하), `blocks`와 `busy`는 50개 이하다.
- 한도를 넘거나 형식이 틀리면 예외를 던지지 않는다. `resolve_date`는 `ok: false`로, `validate_schedule`은 `INVALID_RANGE` 위반으로 답한다. 다른 도구는 `isError`가 `true`인 MCP 오류 결과를 돌려준다.
- 스택 트레이스와 내부 경로를 응답에 넣지 않는다.
- `deadlineTime`은 시간표의 날짜와 같은 날 마감에만 쓴다. 호출하는 쪽이 계산해서 채운다.

### 3.6 구현 메모 (`Haru.Mcp` 기준)

위 규칙에서 모호했던 부분을 이렇게 구현했다. 호출하는 쪽은 이 동작을 전제로 해도 된다.

- 요청 본문은 512KB까지 받는다. 넘으면 HTTP 413이다.
- 인자를 빠뜨리거나 타입이 틀려도(예: `blocks`가 배열이 아님) 3.5와 같이 답한다. `resolve_date`는 `ok: false`, `validate_schedule`은 `blockIds: []`인 `INVALID_RANGE` 위반 하나, `find_free_slots`와 `export_ics`는 `isError`다. `busy`는 필수이므로 없으면 `[]`를 보낸다.
- `resolve_date`: `밤`은 6~11시를 오후(18~23시), 12시를 00:00, 1~5시를 그대로 읽는다. `점심`은 10~12시를 그대로, 1~9시를 오후로 읽는다. `새벽`은 `오전`과 같고 `오전 12시`는 00:00이다. 시각 없이 `내일 오전`만 쓰면 `ok: false`다.
- `validate_schedule`:
  - `OVERLOAD`는 반올림한 `loadRatio`가 `maxLoad`보다 클 때만 나온다. 0.85는 통과하고 0.86은 위반이다. 0.875는 0.88로 답한다.
  - `availableMinutes`가 음수가 되면 0으로 둔다.
  - `OVERLAP`은 겹치는 쌍마다 하나다. `NO_BUFFER`는 겹치지 않은 이웃 쌍에만 나온다.
  - `INVALID_RANGE`인 블록은 다른 검사에서 뺀다. 블록이 50개를 넘거나, 근무 시간이 형식·순서가 틀리거나, `bufferMinutes`가 0~1440 밖이거나, `maxLoad`가 0~1000 밖이면 전체를 거절한다. 이때 `valid: false`, 수치 0, `blockIds: []`인 `INVALID_RANGE` 하나로 답한다.
  - 위반은 3.3의 코드 순서로, 같은 코드끼리는 첫 블록의 시작 시각순으로 놓는다. `OVERLOAD`의 `blockIds`는 `[]`다.
- `find_free_slots`: `totalFreeMinutes`는 돌려준 `slots`의 `minutes` 합이다. `minMinutes`와 `bufferMinutes`는 0~1440이다.
- `export_ics`: `DTSTAMP`는 RFC 5545 형식인 `YYYYMMDDT000000Z`다(예: `20261105T000000Z`). 제목, 이유, `calendarName`의 제어 문자(CR, LF 등)는 이스케이프하기 전에 공백으로 바꾼다. `UID`의 `<id>`에서 `A-Za-z0-9._-` 밖의 문자는 `_`로 바꾼다. `break` 블록도 형식은 검사하고 내보내지만 않는다. 이벤트가 0개여도 응답한다.

## 4. 샘플 데이터

- `src/Haru.Api/Samples/sample-input.txt`: 샘플 원문
- `src/Haru.Api/Samples/sample-plan.json`: `PlanResult` 예시. `validate_schedule`을 통과한다.
