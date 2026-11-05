// 하루설계 — 단일 페이지 UI. 빌드 단계 없이 모듈 하나로 동작한다.
// 서버 텍스트는 모두 textContent로 넣는다(innerHTML을 쓰지 않는다). CSP가 인라인 스크립트·스타일을 막는다.

const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];

function el(tag, props = {}, ...kids) {
  const n = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (v == null || v === false) continue;
    if (k === 'class') n.className = v;
    else if (k === 'text') n.textContent = v;
    else if (k === 'dataset') Object.assign(n.dataset, v);
    else if (k.startsWith('on')) n.addEventListener(k.slice(2), v);
    else n.setAttribute(k, v === true ? '' : String(v));
  }
  for (const kid of kids.flat()) if (kid != null && kid !== false) n.append(kid);
  return n;
}

const toMin = (t) => { const [h, m] = String(t).split(':').map(Number); return h * 60 + m; };
const fmtHM = (m) => `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`;
const fmtDur = (m) => { const h = Math.floor(m / 60), r = m % 60; return h && r ? `${h}시간 ${r}분` : h ? `${h}시간` : `${r}분`; };
const secs = (ms) => (ms < 100 ? '0.1초 미만' : `${(ms / 1000).toFixed(1)}초`);
const fmtDay = (d) => {
  const [y, m, dd] = String(d).split('-').map(Number);
  return new Date(y, m - 1, dd).toLocaleDateString('ko-KR', { year: 'numeric', month: 'long', day: 'numeric', weekday: 'short' });
};
const fmtDeadline = (s) => {
  const m = /^(\d{4})-(\d{2})-(\d{2})(?:T(\d{2}):(\d{2}))?/.exec(s || '');
  return m ? `${Number(m[2])}/${Number(m[3])}${m[4] ? ` ${m[4]}:${m[5]}` : ''}` : s;
};
const nowKst = () => {
  const [h, m] = new Date().toLocaleTimeString('en-GB', { timeZone: 'Asia/Seoul', hourCycle: 'h23', hour: '2-digit', minute: '2-digit' }).split(':').map(Number);
  return h * 60 + m;
};
const motion = () => (matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth');
const brief = (v) => {
  try { const s = typeof v === 'string' ? v : JSON.stringify(v); return s.length > 90 ? `${s.slice(0, 90)}…` : s; } catch { return ''; }
};

const KIND = { task: '작업', fixed: '고정 일정', break: '휴식' };
const PRI = { 1: '높음', 2: '보통', 3: '낮음' };
const SEV = { high: '높음', medium: '보통', low: '낮음' };
const PPM = 1.35; // 시간표 1분당 픽셀

const STEPS = [
  { id: 'intake', name: '입력 정리', tag: '에이전트 · 도구 없음(격리)', desc: '원문에서 할 일을 뽑아 표로 정리합니다.' },
  { id: 'normalize', name: '날짜 해석', tag: '코드 · MCP resolve_date', desc: '‘내일 오전’ 같은 말을 도구로 정확한 날짜로 바꿉니다.' },
  { id: 'estimator', name: '시간 추정', tag: '에이전트 · 병렬', desc: '작업마다 걸리는 시간을 추정합니다.' },
  { id: 'prioritizer', name: '우선순위', tag: '에이전트 · 병렬', desc: '마감과 중요도로 순서를 정합니다.' },
  { id: 'scheduler', name: '시간 배치', tag: '에이전트 · MCP 도구', desc: '빈 시간을 찾아 일을 배치합니다.' },
  { id: 'validator', name: '규칙 검사', tag: '코드 · MCP validate_schedule', desc: '겹침, 여유 시간, 부하를 코드로 검사합니다.' },
  { id: 'repair', name: '결정적 보정', tag: '코드', desc: '두 번 고쳐도 남은 위반을 코드로 고칩니다.', optional: true },
  { id: 'critic', name: '위험 검토', tag: '에이전트 · 사전 부검', desc: '계획이 실패할 이유를 미리 찾습니다.' },
  { id: 'coach', name: '하루 요약', tag: '에이전트', desc: '하루 계획을 한 단락으로 요약합니다.' },
];
const STEP_NAME = Object.fromEntries(STEPS.map((s) => [s.id, s.name]));

const ERR_TITLE = {
  AI_UNAVAILABLE: 'AI에 연결하지 못했습니다',
  BUSY: '다른 계획을 만드는 중입니다',
  RATE_LIMITED: '요청이 너무 많습니다',
  DAILY_CAP: '오늘 사용 한도에 도달했습니다',
  TIMEOUT: '계획을 만드는 데 시간이 너무 오래 걸렸습니다',
  WORKFLOW_FAILED: '계획을 끝까지 만들지 못했습니다',
  NO_TASKS: '할 일을 찾지 못했습니다',
  INPUT_EMPTY: '할 일을 입력해 주세요',
  INPUT_TOO_LONG: '입력이 너무 깁니다',
  INTERNAL: '서버에서 문제가 생겼습니다',
  NETWORK: '서버에 연결하지 못했습니다',
  STREAM_CLOSED: '연결이 중간에 끊겼습니다',
  INVALID_SCHEDULE: '일정을 조정해야 합니다',
};
const STORE = 'haru.v1';
const BUSY_WAIT_MS = 5000;
const BUSY_TRIES = 13;
const FALLBACK_INPUT = '오늘 할 일\n- 14시 팀 주간 회의 (1시간)\n- 제안서 초안 작성, 오늘 17시까지\n- 밀린 메일 답장 정리\n- 운동 30분';

const S = { config: null, running: false, mode: 'idle', ctrl: null, plan: null, cur: null, tasks: null, view: null, ag: {}, sig: '', tick: 0, t0: 0, error: null,
  saved: null, previous: null, input: null, history: [], revisionReason: '', measurement: null, exporting: false };
const validPlan = (p) => p?.validation?.valid === true && Array.isArray(p.validation.violations)
  && p.validation.violations.length === 0 && Array.isArray(p.blocks);

/* ---------- 공통 UI ---------- */

function live(msg) { const n = $('#live'); n.textContent = ''; setTimeout(() => { n.textContent = msg; }, 30); }

let toastTimer = 0;
function toast(msg) {
  const t = $('#toast'); t.textContent = msg; t.hidden = false;
  clearTimeout(toastTimer); toastTimer = setTimeout(() => { t.hidden = true; }, 4200);
}

function banner(kind, title, message, actions = []) {
  const b = $('#banner');
  b.className = `banner ${kind}`; b.hidden = false;
  b.replaceChildren(...[
    el('strong', { text: title }),
    message ? el('p', { text: message }) : null,
    actions.length ? el('div', { class: 'actions' }, ...actions.map((a) => el('button', { type: 'button', class: 'btn', text: a.label, onclick: a.run }))) : null,
  ].filter((n) => n !== null));
  b.scrollIntoView({ block: 'nearest', behavior: motion() });
}
const hideBanner = () => { $('#banner').hidden = true; };

function paintStatus() {
  const s = $('#status'), t = $('#status-text');
  if (!S.config) { s.dataset.state = 'bad'; t.textContent = '서버에 연결하지 못했습니다'; }
  else if (S.config.aiAvailable) { s.dataset.state = 'ok'; t.textContent = 'AI 연결됨'; }
  else { s.dataset.state = 'warn'; t.textContent = 'AI 미연결 · 기록 데모를 볼 수 있습니다'; }
}

function updateCount() {
  const n = $('#text').value.length, max = S.config?.limits?.maxInputChars ?? 3000;
  const c = $('#count');
  c.textContent = `${n.toLocaleString('ko-KR')} / ${max.toLocaleString('ko-KR')}자`;
  c.classList.toggle('warn', n > max * 0.9);
}

/* ---------- 에이전트 타임라인 ---------- */

function buildSteps() {
  $('#steps').replaceChildren(...STEPS.map((s) => el('li', { class: 'step', dataset: { agent: s.id, state: 'idle' }, hidden: s.optional },
    el('span', { class: 'mark', 'aria-hidden': 'true' }),
    el('div', { class: 'body' },
      el('div', { class: 'line' },
        el('span', { class: 'name', text: s.name }),
        el('span', { class: 'chip', text: s.tag }),
        el('span', { class: 'chip model', hidden: true }),
        el('span', { class: 'chip run', hidden: true }),
        el('span', { class: 'time' })),
      el('p', { class: 'desc', text: s.desc }),
      el('ul', { class: 'tools' }),
      el('p', { class: 'out' }),
      el('p', { class: 'out sub' })))));
}

function resetSteps() {
  S.ag = {};
  for (const li of $$('.step')) {
    li.dataset.state = 'idle';
    li.hidden = STEPS.find((s) => s.id === li.dataset.agent)?.optional ?? false;
    $('.model', li).hidden = true; $('.run', li).hidden = true;
    for (const sel of ['.time', '.out', '.sub']) $$(sel, li).forEach((n) => { n.textContent = ''; });
    $('.tools', li).replaceChildren();
  }
  $('#tasks-box').hidden = true;
}

const stepEl = (id) => $(`.step[data-agent="${id}"]`);

function onStarted(d) {
  if (d.agent === 'repair') S.revisionReason = '반복 배치 후 남은 규칙 위반을 코드로 보정했습니다.';
  const li = stepEl(d.agent);
  if (!li) return;
  const a = (S.ag[d.agent] ??= { runs: 0, chars: 0, t0: 0 });
  a.runs += 1; a.chars = 0; a.t0 = performance.now();
  li.hidden = false; li.dataset.state = 'running';
  const model = $('.model', li); model.hidden = !d.model; model.textContent = d.model ?? '';
  const run = $('.run', li); run.hidden = a.runs < 2; run.textContent = `${a.runs}회차`;
  $('.time', li).textContent = '';
  $('.out', li).textContent = ''; $('.sub', li).textContent = '';
  $('.tools', li).replaceChildren();
  live(`${STEP_NAME[d.agent]} 시작`);
}

function onDelta(d) {
  const li = stepEl(d.agent), a = S.ag[d.agent];
  if (!li || !a) return;
  a.chars += String(d.text ?? '').length;
  $('.out', li).textContent = `응답 수신 중 · ${a.chars.toLocaleString('ko-KR')}자`;
}

function onToolCall(d) {
  const li = stepEl(d.agent);
  if (!li) return;
  $('.tools', li).append(el('li', { class: 'tool', dataset: { state: 'running', tool: d.tool, callId: d.callId } },
    el('code', { text: d.tool }), el('span', { class: 'args', text: brief(d.args) }), el('span', { class: 'res' })));
}

function onToolResult(d) {
  const li = stepEl(d.agent);
  if (!li) return;
  const t = $$('.tool', li).find((n) =>
    n.dataset.callId === d.callId && n.dataset.tool === d.tool && n.dataset.state === 'running');
  if (!t) return;
  t.dataset.state = d.ok ? 'ok' : 'fail';
  $('.res', t).textContent = d.summary ?? '';
}

function onCompleted(d) {
  const li = stepEl(d.agent);
  if (!li) return;
  li.dataset.state = 'done';
  $('.time', li).textContent = secs(d.elapsedMs ?? 0);
  $('.out', li).textContent = d.summary ?? '';
  live(`${STEP_NAME[d.agent]} 완료`);
}

function renderTasks(tasks) {
  $('#tasks-box').hidden = !tasks?.length;
  $('#tasks').replaceChildren(...(tasks ?? []).map((t) => el('li', {},
    el('span', { class: 't', text: t.title }),
    t.kind === 'fixed'
      ? el('span', { class: 'pri fx', text: `고정 ${t.fixedStart}–${t.fixedEnd}` })
      : el('span', { class: `pri p${t.priority}`, text: `우선순위 ${PRI[t.priority] ?? t.priority}` }),
    el('span', { class: 'm', text: `${fmtDur(t.minutes)} · ${{
      input: '입력에서 추출 · 확인 필요', estimated: 'AI 추정', default: '기본값', fixed: '고정 시각 기준',
    }[t.durationSource] ?? '출처 기록 없음'}` }),
    t.deadline ? el('span', { class: 'm', text: `마감 ${fmtDeadline(t.deadline)}` }) : null)));
}

/* ---------- 결과 ---------- */

function paintPlan(p, draft = 0) {
  S.plan = !draft && validPlan(p) ? p : null;
  $('#result').hidden = false;
  $('#res-day').textContent = fmtDay(p.day);

  const mode = p.meta?.source === 'offline-sample' && S.mode !== 'replay' ? 'sample' : S.mode;
  const badge = $('#badge-source');
  const label = { live: 'AI가 만든 제안', sample: '오프라인 샘플 · AI 호출 없음', replay: '기록된 실행 재생 · AI 호출 없음', restored: '저장된 결과 · 이 브라우저에만 저장됨' }[mode] ?? 'AI가 만든 제안';
  badge.textContent = label; badge.className = `badge ${mode === 'live' ? 'ai' : 'sample'}`;
  $('#res-disclaimer').textContent = mode === 'sample' || mode === 'replay'
    ? '내 입력으로 만든 결과가 아닙니다. 미리 기록한 예시이므로 참고용으로만 보세요. 일정의 최종 판단은 직접 해 주세요.'
    : 'AI가 만든 제안입니다. 시간 계산과 충돌 검사는 도구가 했지만, 일정의 최종 판단은 직접 해 주세요.';
  const db = $('#badge-draft'); db.hidden = !draft; db.textContent = `초안 ${draft} · 검증 중`;

  const sum = $('#res-summary'); sum.hidden = !p.summary; sum.textContent = p.summary ?? '';
  paintVitals(p);
  paintBlocks(p);
  paintSide(p);
  paintActions();
}

function paintVitals(p) {
  const v = p.validation, box = $('#vitals');
  box.hidden = !v;
  const violations = v?.violations ?? [];
  $('#violations-box').hidden = !violations.length;
  $('#violations').replaceChildren(...violations.map(x => el('li', { text: x.message })));
  if (!v) return;
  const ok = $('#v-valid');
  ok.textContent = v.valid ? '통과 · 위반 0건' : `위반 ${v.violations.length}건`;
  ok.className = v.valid ? 'ok' : 'bad';
  const pct = Math.round(v.loadRatio * 100);
  $('#v-load').textContent = `${pct}%`;
  const bar = $('#v-bar');
  bar.style.width = `${Math.min(pct, 100)}%`;
  bar.className = pct > 100 ? 'over' : pct > 85 ? 'hot' : '';
  $('#v-occ').textContent = `${fmtDur(v.occupiedMinutes)} / ${fmtDur(v.availableMinutes)}`;
  const m = p.meta, run = $('#v-run');
  run.replaceChildren();
  if (!m) { run.textContent = '검증 중'; return; }
  run.append(`${secs(m.elapsedMs)} · 시간 배치 ${m.iterations}회${m.repaired ? ' · 자동 보정' : ''}`);
  if (m.models) run.append(el('small', { class: 'sub', text: `${m.models.fast} · ${m.models.smart}` }));
}

function paintActions() {
  for (const id of ['#btn-plan', '#btn-sample', '#btn-example', '#btn-fill', '#btn-clear']) $(id).disabled = S.running || S.exporting;
  const ready = !S.running && !S.exporting && validPlan(S.plan);
  $('#btn-export').disabled = !ready || !S.plan?.blocks?.some(b => b.kind !== 'break');
  $('#btn-copy').disabled = !ready;
  $('#btn-confirm').disabled = !ready || S.measurement?.ready == null || S.measurement.totalMs != null;
  $('#btn-restore').hidden = !restoreTarget();
  $('#btn-restore').disabled = S.running || S.exporting;
}

function scheduleChanges(before, after) {
  const prior = new Map((before.blocks ?? []).filter(b => b.taskId).map(b => [b.taskId, b]));
  const next = new Map((after.blocks ?? []).filter(b => b.taskId).map(b => [b.taskId, b]));
  const changes = [];
  for (const [id, b] of prior) {
    const n = next.get(id);
    if (!n) changes.push(`${b.title}: ${b.start}–${b.end} → 오늘 배치하지 않음`);
    else if (b.start !== n.start || b.end !== n.end) changes.push(`${b.title}: ${b.start}–${b.end} → ${n.start}–${n.end}`);
  }
  for (const [id, b] of next) if (!prior.has(id)) changes.push(`${b.title}: 미배치 → ${b.start}–${b.end}`);
  return changes;
}

function paintReview() {
  $('#review-box').hidden = !S.history.length;
  $('#review').replaceChildren(...S.history.map((round, i) => el('li', {},
    el('strong', { text: `${i + 1}번째 시간표 · ${round.report ? round.report.valid ? '규칙 통과' : `위반 ${round.report.violations.length}건` : '검사 미완료'}` }),
    el('p', { text: round.reason }),
    round.report ? el('p', { text: `부하 ${Math.round(round.report.loadRatio * 100)}%${round.report.violations.length ? ` · ${round.report.violations.map(v => v.message).join(' / ')}` : ''}` }) : null,
    ...(i ? (() => {
      const changes = scheduleChanges(S.history[i - 1], round);
      return changes.length ? changes : ['시간·배치 변경 없음. 남은 위험과 미룬 일을 확인해 주세요.'];
    })() : []).map(text => el('p', { text })),
    ...round.deferred.map(d => el('p', { text: `미룸: ${d.title} — ${d.reason}` })))));
}

function paintMeasurement() {
  const m = S.measurement;
  if (S.mode === 'live' && S.running && m?.requestMs == null) {
    $('#measurement').textContent = '실제 AI 실행 중입니다. 결과를 받은 뒤 검토·확정 시간을 측정합니다.';
    return;
  }
  $('#measurement').textContent = m?.requestMs != null
    ? `요청부터 결과까지 ${secs(m.requestMs)}${m.totalMs != null ? ` · 검토 ${secs(m.reviewMs)} · 확정까지 ${secs(m.totalMs)}` : ' · 검토 후 ‘이 계획 확정’을 누르면 검토 시간을 기록합니다.'} (입력 준비·파일 가져오기 제외, 절감 시간 아님)`
    : '기록·저장 결과는 이번 실제 실행 시간으로 측정하지 않습니다. 절감 효과는 사용자 비교 실험이 필요합니다.';
}

function confirmPlan() {
  if (S.running || !validPlan(S.plan) || S.measurement?.ready == null || S.measurement.totalMs != null) return;
  const now = performance.now();
  S.measurement.reviewMs = now - S.measurement.ready;
  S.measurement.totalMs = now - S.measurement.start;
  paintMeasurement(); paintActions();
  toast('이 계획을 확정했습니다. 캘린더 내보내기는 별도로 확인합니다.');
}

function paintBlocks(p) {
  const blocks = [...(p.blocks ?? [])].sort((a, b) => toMin(a.start) - toMin(b.start));
  const sig = JSON.stringify([p.workStart, p.workEnd, blocks, (p.tasks ?? []).map((t) => [t.id, t.priority])]);
  if (sig === S.sig) return;
  S.sig = sig;

  const box = $('#timeline'), reasons = $('#reasons');
  box.replaceChildren(); reasons.replaceChildren();
  if (!blocks.length) { box.style.height = '0px'; return; }

  const lo = Math.floor(Math.min(toMin(p.workStart), ...blocks.map((b) => toMin(b.start))) / 60) * 60;
  const hi = Math.ceil(Math.max(toMin(p.workEnd), ...blocks.map((b) => toMin(b.end))) / 60) * 60;
  box.style.height = `${(hi - lo) * PPM}px`;
  for (let m = lo; m <= hi; m += 60) {
    const h = el('div', { class: 'hour', 'aria-hidden': 'true' }, el('span', { text: fmtHM(m) }));
    h.style.top = `${(m - lo) * PPM}px`;
    box.append(h);
  }

  const pri = new Map((p.tasks ?? []).map((t) => [t.id, t.priority]));
  blocks.forEach((b, i) => {
    const cls = b.kind === 'task' ? `p${pri.get(b.taskId) ?? 2}` : b.kind;
    const h = Math.max((toMin(b.end) - toMin(b.start)) * PPM - 2, 22);
    const node = el('div', {
      class: `blk ${cls}${h < 40 ? ' short' : ''}`, role: 'listitem', tabindex: '0', title: b.reason || null,
      'aria-label': `${b.start}부터 ${b.end}까지, ${b.title}. ${KIND[b.kind] ?? ''}. ${b.reason ?? ''}`,
    },
      el('span', { class: 't', text: `${b.start}–${b.end}` }),
      el('span', { class: 'n', text: b.title }),
      h >= 64 && b.reason ? el('span', { class: 'r', text: b.reason }) : null);
    node.style.top = `${(toMin(b.start) - lo) * PPM}px`;
    node.style.height = `${h}px`;
    node.style.animationDelay = `${Math.min(i * 40, 400)}ms`;
    box.append(node);

    reasons.append(el('li', { class: cls },
      el('div', { class: 'head' }, el('time', { text: `${b.start}–${b.end}` }), el('strong', { text: b.title }), el('span', { class: 'chip', text: KIND[b.kind] ?? b.kind })),
      b.reason ? el('p', { text: b.reason }) : null));
  });

  if (p.day === S.config?.today) {
    const now = nowKst();
    if (now >= lo && now <= hi) {
      const n = el('div', { class: 'now', 'aria-hidden': 'true' });
      n.style.top = `${(now - lo) * PPM}px`;
      box.append(n);
    }
  }
}

function paintSide(p) {
  const def = p.deferred ?? [], risks = p.risks ?? [];
  $('#deferred-box').hidden = !def.length;
  $('#deferred').replaceChildren(...def.map((d) => el('li', {}, el('strong', { text: d.title }), el('p', { text: d.reason }))));
  $('#risks-box').hidden = !risks.length;
  $('#risks').replaceChildren(...risks.map((r) => el('li', {},
    el('span', { class: `sev ${r.severity}`, text: SEV[r.severity] ?? r.severity }), el('strong', { text: r.title }),
    el('p', { text: `대응: ${r.mitigation}` }))));
}

function focusResult() {
  $('#h-result').focus({ preventScroll: true });
  $('#result').scrollIntoView({ behavior: motion(), block: 'start' });
}

/* ---------- 이벤트 처리 ---------- */

function handleEvent(name, d) {
  switch (name) {
    case 'run_started': if (d.day && S.view) S.view.day = d.day; break;
    case 'agent_started': onStarted(d); break;
    case 'agent_delta': onDelta(d); break;
    case 'tool_call': onToolCall(d); break;
    case 'tool_result': onToolResult(d); break;
    case 'agent_completed': onCompleted(d); break;
    case 'tasks': S.tasks = d.tasks; renderTasks(d.tasks); break;
    case 'schedule':
      S.history.push({ blocks: d.blocks ?? [], deferred: d.deferred ?? [], report: null,
        reason: S.revisionReason || (S.history.length ? '규칙 검사 결과에 따라 다시 배치했습니다.' : '첫 배치입니다.') });
      S.revisionReason = '';
      S.cur = { ...S.view, tasks: S.tasks ?? [], blocks: d.blocks ?? [], deferred: d.deferred ?? [], risks: [], summary: '', validation: null, meta: null };
      paintPlan(S.cur, d.iteration ?? 1);
      paintReview();
      break;
    case 'validation': {
      if (!S.cur) break;
      S.cur.validation = d.report; paintVitals(S.cur);
      if (S.history.length) S.history.at(-1).report = d.report;
      if (!d.report?.valid) S.revisionReason = '도구가 발견한 규칙 위반을 줄이도록 다시 배치했습니다.';
      paintReview();
      const li = stepEl('validator'), v = d.report?.violations ?? [];
      if (li) $('.sub', li).textContent = v.length ? `위반 ${v.length}건 · ${v.slice(0, 2).map((x) => x.message).join(' / ')}` : '';
      break;
    }
    case 'critique': {
      if (d.decision === 'revise') S.revisionReason = `위험 검토에서 수정을 요청했습니다.${d.risks?.length ? ` ${d.risks.map(r => r.title).join(' / ')}` : ''}`;
      const li = stepEl('critic');
      if (li) $('.sub', li).textContent = d.decision === 'revise' ? '판정: 수정 요청 → 시간 배치를 다시 실행합니다.' : '판정: 승인';
      if (S.cur) { S.cur.risks = d.risks ?? []; paintSide(S.cur); }
      break;
    }
    case 'plan':
      if (!validPlan(d)) {
        S.error = { code: 'INVALID_SCHEDULE', message: '유효하지 않은 결과라 확정하지 않았습니다.' };
        break;
      }
      S.cur = d; paintPlan(d);
      break;
    case 'done':
      if (S.error || !validPlan(S.plan)) {
        S.error ??= { code: 'STREAM_CLOSED', message: '검증된 최종 결과를 받지 못했습니다.' };
        break;
      }
      if (S.mode === 'live') {
        S.measurement.ready = performance.now();
        S.measurement.requestMs = S.measurement.ready - S.measurement.start;
        S.previous = S.saved;
        S.saved = { plan: S.plan, input: S.input, history: S.history };
        save();
      }
      finish(S.mode === 'replay' ? '기록된 실행 · AI 호출 없음' : `완료 · ${secs(d.elapsedMs)}`);
      paintMeasurement(); paintActions(); focusResult(); live('검증된 시간표가 완성되었습니다');
      break;
    case 'error':
      S.error = d;
      $$('.step[data-state="running"]').forEach((li) => { li.dataset.state = 'failed'; });
      break;
    default: break;
  }
}

async function* parseSse(res) {
  const reader = res.body.getReader(), dec = new TextDecoder();
  let buf = '';
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      buf += dec.decode(value, { stream: true }).replace(/\r\n/g, '\n');
      let i;
      while ((i = buf.indexOf('\n\n')) >= 0) {
        const frame = buf.slice(0, i); buf = buf.slice(i + 2);
        let event = 'message'; const data = [];
        for (const line of frame.split('\n')) {
          if (line.startsWith(':')) continue;
          if (line.startsWith('event:')) event = line.slice(6).trim();
          else if (line.startsWith('data:')) data.push(line.slice(5).replace(/^ /, ''));
        }
        if (!data.length) continue;
        try { yield { event, data: JSON.parse(data.join('\n')) }; } catch (e) { if (e instanceof SyntaxError) continue; throw e; }
      }
    }
  } finally {
    try { await reader.cancel(); } catch { /* 이미 닫힘 */ }
  }
}

/* ---------- 실행 흐름 ---------- */

function begin(mode, keepSteps = false) {
  S.mode = mode; S.running = true; S.error = null; S.plan = null; S.cur = null; S.sig = '';
  S.history = []; S.revisionReason = '';
  S.measurement = mode === 'live' ? { start: performance.now(), ready: null, requestMs: null, totalMs: null } : null;
  paintReview(); paintMeasurement();
  hideBanner();
  if (!keepSteps) { resetSteps(); S.tasks = null; }
  $('#result').hidden = true;
  for (const id of ['#btn-plan', '#btn-sample', '#btn-example', '#btn-fill', '#btn-clear']) $(id).disabled = true;
  paintActions();
  $('#btn-stop').hidden = false;
  $('#steps').setAttribute('aria-busy', 'true');
  $('#agents-state').textContent = mode === 'live' ? '실행 중' : '재생 중';
  S.t0 = performance.now();
  clearInterval(S.tick);
  S.tick = setInterval(() => {
    if (S.mode === 'live') $('#btn-plan').textContent = `만드는 중… ${Math.floor((performance.now() - S.t0) / 1000)}초`;
    for (const li of $$('.step[data-state="running"]')) {
      const a = S.ag[li.dataset.agent];
      if (a) $('.time', li).textContent = `${((performance.now() - a.t0) / 1000).toFixed(1)}초`;
    }
  }, 250);
  if (matchMedia('(max-width: 900px)').matches) $('#agents-panel').scrollIntoView({ behavior: motion(), block: 'start' });
}

function finish(label) {
  if (!S.running) return;
  S.running = false; S.ctrl = null;
  clearInterval(S.tick);
  $('#btn-plan').textContent = '하루 계획 만들기';
  for (const id of ['#btn-plan', '#btn-sample', '#btn-example', '#btn-fill', '#btn-clear']) $(id).disabled = false;
  $('#btn-stop').hidden = true;
  $('#steps').setAttribute('aria-busy', 'false');
  $('#agents-state').textContent = label ?? '완료';
  paintActions();
}

function readSettings() {
  const v = (id) => $(`#${id}`).value;
  const ws = v('workStart'), we = v('workEnd'), ls = v('lunchStart'), le = v('lunchEnd');
  const bad = (msg) => { $('.settings').open = true; banner('warn', '시간 설정을 확인해 주세요', msg); return null; };
  if (!ws || !we) return bad('근무 시작과 종료 시간을 입력해 주세요.');
  if (toMin(we) - toMin(ws) < 60) return bad('근무 시간은 1시간 이상이어야 합니다.');
  if (Boolean(ls) !== Boolean(le)) return bad('점심은 시작과 종료를 모두 채우거나 모두 비워 주세요.');
  if (ls && le && toMin(ls) >= toMin(le)) return bad('점심 시작은 종료보다 빨라야 합니다.');
  return { workStart: ws, workEnd: we, lunchStart: ls, lunchEnd: le };
}

async function readError(res) {
  try { const j = await res.json(); return { code: j.error?.code ?? 'INTERNAL', message: j.error?.message ?? '' }; } catch { return { code: 'INTERNAL', message: '' }; }
}

async function runPlan() {
  if (S.running || S.exporting) return;
  const text = $('#text').value.trim();
  if (!text) { banner('warn', '할 일을 입력해 주세요', '메모나 메신저 글을 붙여 넣거나 ‘예시 입력 넣기’를 눌러 보세요.'); $('#text').focus(); return; }
  const settings = readSettings();
  if (!settings) return;

  begin('live');
  await requestPlan(text, settings, S.ctrl = new AbortController());
}

async function runExample() {
  if (S.running || S.exporting) return;
  const settings = readSettings();
  if (!settings) return;
  begin('live');
  const ctrl = (S.ctrl = new AbortController());
  try {
    const res = await fetch('/api/sample', { signal: ctrl.signal });
    if (!res.ok) throw new Error('sample');
    const sample = await res.json();
    ctrl.signal.throwIfAborted();
    if (!sample.inputText?.trim()) throw new Error('empty sample');
    $('#text').value = sample.inputText; updateCount();
    await requestPlan(sample.inputText, settings, ctrl);
  } catch (e) {
    if (e?.name === 'AbortError') { finish('중지됨'); banner('info', '실제 AI 예시 실행을 중지했습니다.'); }
    else await fail('NETWORK', '예시 입력을 불러오지 못했습니다. 다시 시도해 주세요.');
  }
}

async function requestPlan(text, settings, ctrl) {
  S.input = { text, ...settings };
  S.view = { day: S.config?.today ?? '', workStart: settings.workStart, workEnd: settings.workEnd };
  let finished = false;
  try {
    let res;
    for (let attempt = 1; ; attempt++) {
      res = await fetch('/api/plan', {
        method: 'POST', signal: ctrl.signal,
        headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream' },
        body: JSON.stringify({ text, ...settings }),
      });
      if (res.ok) break;
      const e = await readError(res);
      // Other people can fill all run slots. Wait for a free slot instead of showing an error at once.
      if (e.code !== 'BUSY' || attempt >= BUSY_TRIES) { await fail(e.code, e.message); return; }
      $('#agents-state').textContent = '대기 중';
      banner('info', '다른 계획이 끝나기를 기다립니다', `빈자리가 생기면 자동으로 시작합니다. (${attempt}/${BUSY_TRIES - 1})`);
      await sleep(BUSY_WAIT_MS, ctrl.signal);
    }
    hideBanner();
    $('#agents-state').textContent = '실행 중';
    for await (const { event, data } of parseSse(res)) {
      handleEvent(event, data);
      if (event === 'done' || event === 'error') { finished = true; break; }
    }
    if (S.error) await fail(S.error.code, S.error.message);
    else if (!finished) await fail('STREAM_CLOSED', '잠시 후 다시 시도해 주세요.');
  } catch (e) {
    if (e?.name === 'AbortError') { S.plan = null; finish('중지됨'); banner('info', '계획 만들기를 중지했습니다.'); }
    else await fail('NETWORK', '네트워크를 확인한 뒤 다시 시도해 주세요.');
  } finally {
    finish('완료');
  }
}

async function fail(code, message) {
  const title = ERR_TITLE[code] ?? '문제가 생겼습니다';
  S.plan = null; S.measurement = null;
  finish('오류');
  if (S.cur) { $('#badge-draft').hidden = false; $('#badge-draft').textContent = '미완료 · 확정·내보내기 불가'; }
  paintActions(); paintMeasurement();
  banner('error', title, message, [
    { label: code === 'INVALID_SCHEDULE' ? '입력 수정' : '다시 시도', run: code === 'INVALID_SCHEDULE' ? () => $('#text').focus() : runPlan },
    { label: '기록 데모 보기 · 내 결과 아님', run: showSample },
  ]);
}

const sleep = (ms, signal) => new Promise((resolve, reject) => {
  if (signal?.aborted) { reject(new DOMException('Aborted', 'AbortError')); return; }
  const abort = () => { clearTimeout(id); reject(new DOMException('Aborted', 'AbortError')); };
  const id = setTimeout(() => { signal?.removeEventListener('abort', abort); resolve(); }, ms);
  signal?.addEventListener('abort', abort, { once: true });
});

async function replay(events, signal) {
  let prev = 0;
  for (const ev of events) {
    signal.throwIfAborted();
    const gap = Math.min(Math.max((ev.t ?? 0) - prev, 0) / 4, 450);
    prev = ev.t ?? prev;
    if (gap > 25) await sleep(gap, signal);
    handleEvent(ev.event, ev.data);
  }
}

async function showSample() {
  if (S.running || S.exporting) return;
  begin('replay');
  $('#agents-state').textContent = '기록 불러오는 중';
  const ctrl = (S.ctrl = new AbortController());
  try {
    const r = await fetch('/api/sample', { signal: ctrl.signal });
    if (!r.ok) throw new Error('sample');
    const sample = await r.json();
    ctrl.signal.throwIfAborted();
    const p = sample.plan;
    S.view = { day: p.day, workStart: p.workStart, workEnd: p.workEnd };
    S.tasks = p.tasks;
    if (sample.events?.length) {
      $('#agents-state').textContent = '재생 중';
      await replay(sample.events, ctrl.signal);
      if (S.error || !validPlan(S.plan)) throw new Error('invalid recording');
    } else {
      if (!validPlan(p)) throw new Error('invalid sample');
      S.mode = 'sample'; renderTasks(p.tasks); paintPlan(p);
      finish('샘플 · AI 호출 없음'); focusResult();
    }
  } catch (e) {
    S.plan = null;
    if (e?.name === 'AbortError') { finish('중지됨'); banner('info', '기록 불러오기·재생을 중지했습니다.'); }
    else { finish('오류'); banner('error', '기록을 불러오지 못했습니다', '서버 상태를 확인한 뒤 다시 시도해 주세요.'); }
  } finally {
    finish('기록 재생 종료');
    paintActions();
  }
}

/* ---------- 저장(브라우저 안에서만) ---------- */

function save() {
  try { localStorage.setItem(STORE, JSON.stringify({ text: $('#text').value, saved: S.saved, previous: S.previous })); }
  catch { toast('브라우저에 저장하지 못했습니다. 페이지를 닫기 전에 필요한 결과를 복사해 주세요.'); }
}

function restoreTarget() {
  if (S.saved && S.plan !== S.saved.plan) return S.saved;
  return S.previous;
}

function displaySaved(saved) {
  S.mode = 'restored'; S.measurement = null; S.history = saved.history ?? [];
  S.view = { day: saved.plan.day, workStart: saved.plan.workStart, workEnd: saved.plan.workEnd };
  if (saved.input) {
    $('#text').value = saved.input.text ?? '';
    for (const k of ['workStart', 'workEnd', 'lunchStart', 'lunchEnd']) if (saved.input[k] != null) $(`#${k}`).value = saved.input[k];
    updateCount();
  }
  renderTasks(saved.plan.tasks); paintPlan(saved.plan); paintReview(); paintMeasurement();
}

function restorePrevious() {
  if (S.running || S.exporting) return;
  const target = restoreTarget();
  if (!target || !validPlan(target.plan)) return;
  if (target !== S.saved) { S.previous = S.saved; S.saved = target; }
  resetSteps(); displaySaved(target); hideBanner(); save(); paintActions(); focusResult();
  $('#agents-state').textContent = '이전 결과 복원 · AI 호출 없음';
  toast('계획과 당시 입력·시간 설정을 복원했습니다.');
}

function restore() {
  try {
    const raw = JSON.parse(localStorage.getItem(STORE) ?? 'null');
    if (!raw) return;
    if (raw.text && !$('#text').value) { $('#text').value = raw.text; updateCount(); }
    const saved = raw.saved ?? (raw.plan ? { plan: raw.plan } : null);
    if (validPlan(saved?.plan) && saved.plan.day === S.config?.today) {
      S.saved = saved;
      if (validPlan(raw.previous?.plan) && raw.previous.plan.day === S.config.today) S.previous = raw.previous;
      const text = $('#text').value;
      displaySaved(saved);
      $('#text').value = text; updateCount();
    }
  } catch { toast('저장된 결과를 불러오지 못했습니다. 입력과 시간 설정을 확인해 주세요.'); }
}

function clearAll() {
  if (S.running || S.exporting) return;
  $('#text').value = ''; updateCount();
  S.saved = null; S.previous = null; S.plan = null; S.cur = null; S.sig = '';
  S.history = []; S.measurement = null; S.input = null;
  try { localStorage.removeItem(STORE); } catch { toast('브라우저 저장을 지우지 못했습니다. 브라우저의 사이트 데이터 삭제를 이용해 주세요.'); }
  $('#result').hidden = true; hideBanner(); resetSteps();
  $('#agents-state').textContent = '대기 중';
  paintActions(); paintReview(); paintMeasurement();
  $('#text').focus();
}

/* ---------- 내보내기·복사 ---------- */

function planText(p) {
  const lines = [`${fmtDay(p.day)} 하루 계획 (AI 제안 · 하루설계)`, ''];
  for (const b of p.blocks) lines.push(`${b.start}–${b.end} ${b.title}${b.kind === 'break' ? ' (휴식)' : ''}${b.reason ? ` — ${b.reason}` : ''}`);
  if (p.deferred?.length) { lines.push('', '오늘 하기 어려운 일'); for (const d of p.deferred) lines.push(`- ${d.title}: ${d.reason}`); }
  return lines.join('\n');
}

async function copyPlan() {
  if (S.running || !validPlan(S.plan)) return;
  try { await navigator.clipboard.writeText(planText(S.plan)); toast('시간표를 복사했습니다.'); }
  catch { toast('복사하지 못했습니다. 브라우저 권한을 확인해 주세요.'); }
}

function openExport() {
  const p = S.plan;
  if (!validPlan(p) || S.running || S.exporting) return;
  const n = p.blocks.filter((b) => b.kind !== 'break').length;
  $('#dlg-body').textContent = `${fmtDay(p.day)} 일정 ${n}개를 .ics 파일로 내려받습니다.`;
  $('#dlg-error').hidden = true;
  const dlg = $('#dlg');
  if (typeof dlg.showModal === 'function') dlg.showModal();
  else if (confirm(`${$('#dlg-body').textContent}\nAI가 만든 제안입니다. 계속할까요?`)) doExport();
}

async function doExport() {
  const p = S.plan;
  if (!validPlan(p) || S.running || S.exporting) return;
  S.exporting = true; paintActions();
  const ok = $('#dlg-ok'), err = $('#dlg-error');
  ok.disabled = true; err.hidden = true;
  try {
    const res = await fetch('/api/export', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ confirmed: true, day: p.day, workStart: p.workStart, workEnd: p.workEnd, blocks: p.blocks }),
    });
    if (!res.ok) { const e = await readError(res); throw new Error(e.message || '내보내기에 실패했습니다.'); }
    const name = `haru-${p.day}.ics`;
    const url = URL.createObjectURL(await res.blob());
    const a = el('a', { href: url, download: name });
    document.body.append(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 3000);
    if ($('#dlg').open) $('#dlg').close();
    toast(`${name} 다운로드를 요청했습니다. 저장된 파일을 확인하고 캘린더에서 가져오기를 눌러 주세요.`);
  } catch (e) {
    err.textContent = e.message || '내보내기에 실패했습니다.'; err.hidden = false;
  } finally {
    ok.disabled = false;
    S.exporting = false; paintActions();
  }
}

/* ---------- 시작 ---------- */

async function fillExample() {
  let text = FALLBACK_INPUT;
  try { const r = await fetch('/api/sample'); if (r.ok) text = (await r.json()).inputText || text; } catch { /* 기본 예시 사용 */ }
  $('#text').value = text; updateCount(); $('#text').focus();
}

async function init() {
  buildSteps();
  try { const r = await fetch('/api/config'); S.config = r.ok ? await r.json() : null; } catch { S.config = null; }
  paintStatus();
  if (S.config) {
    const d = S.config.defaults ?? {};
    for (const k of ['workStart', 'workEnd', 'lunchStart', 'lunchEnd']) if (d[k]) $(`#${k}`).value = d[k];
    $('#text').maxLength = S.config.limits?.maxInputChars ?? 3000;
    $('#h-input').append(el('small', { text: fmtDay(S.config.today) }));
  }
  updateCount();

  $('#text').addEventListener('input', () => { updateCount(); save(); });
  $('#text').addEventListener('keydown', (e) => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); runPlan(); } });
  $('#btn-plan').addEventListener('click', runPlan);
  $('#btn-sample').addEventListener('click', () => { showSample(); });
  $('#btn-example').addEventListener('click', runExample);
  $('#btn-restore').addEventListener('click', restorePrevious);
  $('#btn-confirm').addEventListener('click', confirmPlan);
  $('#btn-stop').addEventListener('click', () => S.ctrl?.abort());
  $('#btn-fill').addEventListener('click', fillExample);
  $('#btn-clear').addEventListener('click', clearAll);
  $('#btn-export').addEventListener('click', openExport);
  $('#btn-copy').addEventListener('click', copyPlan);
  $('#dlg-ok').addEventListener('click', doExport);
  restore();
}

init();
