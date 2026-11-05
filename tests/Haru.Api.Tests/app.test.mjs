import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

class Element {
  constructor() {
    this.children = [];
    this.dataset = {};
    this.className = '';
    this.value = '';
    this.style = {};
    this.classList = { toggle() {} };
  }
  append(...children) {
    this.children.push(...children.map(child => child instanceof Element ? child : String(child)));
  }
  replaceChildren(...children) {
    this.children = [];
    this.append(...children);
  }
  set textContent(value) { this.children = [String(value)]; }
  get textContent() { return this.children.map(child => child instanceof Element ? child.textContent : child).join(''); }
  setAttribute(name, value) { this[name] = value; }
  addEventListener() {}
  scrollIntoView() {}
  focus() {}
  querySelector(selector) { return this.querySelectorAll(selector)[0] ?? null; }
  querySelectorAll(selector) {
    return this.children.filter(child => child instanceof Element).flatMap(child => [
      ...(selector === `.${child.className}` ? [child] : []),
      ...child.querySelectorAll(selector),
    ]);
  }
}

function setup(overrides = {}) {
  const banner = new Element();
  const step = new Element();
  const tools = new Element();
  tools.className = 'tools';
  step.append(tools);
  for (const name of ['sub', 'model', 'run', 'time', 'out']) {
    const part = new Element(); part.className = name; step.append(part);
  }
  const ids = new Map([['#banner', banner]]);
  const node = selector => {
    if (!ids.has(selector)) ids.set(selector, new Element());
    return ids.get(selector);
  };
  const storage = new Map();
  const context = vm.createContext({
    document: {
      createElement: () => new Element(),
      querySelector: selector => selector.startsWith('.step[') ? step : node(selector),
      querySelectorAll: () => [],
    },
    setTimeout: (fn, ms) => setTimeout(fn, ms).unref(), clearTimeout,
    setInterval: (fn, ms) => setInterval(fn, ms).unref(), clearInterval,
    AbortController, DOMException, TextDecoder,
    performance: { now: () => 1000 },
    localStorage: { setItem: (k, v) => storage.set(k, v), getItem: k => storage.get(k) ?? null, removeItem: k => storage.delete(k) },
    matchMedia: () => ({ matches: true }),
    ...overrides,
  });
  const source = readFileSync(new URL('../../src/Haru.Api/wwwroot/app.js', import.meta.url), 'utf8');
  vm.runInContext(source.replace(/\ninit\(\);\s*$/, ''), context);
  const call = (name, data) => vm.runInContext(`${name}(${JSON.stringify(data)})`, context);
  const run = code => vm.runInContext(code, context);
  return { banner, tools, call, context, node, storage, run };
}

test('a banner without a message or actions never renders null text', () => {
  const { banner, context } = setup();
  vm.runInContext("banner('warn', 'Stopped', null)", context);
  assert.equal(banner.textContent, 'Stopped');
  assert.equal(banner.children.length, 1);
});

test('a banner retains its message and action buttons', () => {
  const { banner, context } = setup();
  vm.runInContext("banner('warn', 'Busy', 'Wait', [{label:'Retry', run:()=>{}}])", context);
  assert.equal(banner.textContent, 'BusyWaitRetry');
  assert.equal(banner.children.length, 3);
});

test('parallel calls of the same tool match results by call ID, not completion order', () => {
  const { tools, call } = setup();
  call('onToolCall', { agent: 'normalize', tool: 'resolve_date', callId: 'tomorrow', args: { expression: 'tomorrow' } });
  call('onToolCall', { agent: 'normalize', tool: 'resolve_date', callId: 'next-week', args: { expression: 'next week' } });
  call('onToolResult', { agent: 'normalize', tool: 'resolve_date', callId: 'next-week', ok: true, summary: '2026-11-09' });
  assert.equal(tools.children[0].querySelector('.res').textContent, '');
  assert.equal(tools.children[1].querySelector('.res').textContent, '2026-11-09');
  call('onToolResult', { agent: 'normalize', tool: 'resolve_date', callId: 'tomorrow', ok: false, summary: 'Failed' });
  assert.equal(tools.children[0].dataset.state, 'fail');
  assert.equal(tools.children[1].dataset.state, 'ok');
});

test('an unknown result cannot overwrite a pending call', () => {
  const { tools, call } = setup();
  call('onToolCall', { agent: 'normalize', tool: 'resolve_date', callId: 'known', args: {} });
  call('onToolResult', { agent: 'normalize', tool: 'resolve_date', callId: 'unknown', ok: true, summary: 'Wrong' });
  assert.equal(tools.children[0].dataset.state, 'running');
  assert.equal(tools.children[0].querySelector('.res').textContent, '');
});

test('the recorded sample pairs every result with the right call', () => {
  const { tools, call } = setup();
  const sample = JSON.parse(readFileSync(new URL('../../src/Haru.Api/Samples/sample-run.json', import.meta.url), 'utf8'));
  const calls = new Map();
  for (const { event, data } of sample.events) {
    if (event === 'tool_call') {
      assert.ok(data.callId);
      assert.equal(calls.has(data.callId), false);
      calls.set(data.callId, data);
      call('onToolCall', data);
    } else if (event === 'tool_result') {
      assert.equal(calls.get(data.callId)?.tool, data.tool);
      assert.equal(calls.get(data.callId)?.agent, data.agent);
      calls.delete(data.callId);
      call('onToolResult', data);
    }
  }
  assert.equal(calls.size, 0);
  const dates = Object.fromEntries(tools.children.filter(node => node.dataset.tool === 'resolve_date').map(node => [
    JSON.parse(node.querySelector('.args').textContent).expression, node.querySelector('.res').textContent,
  ]));
  assert.equal(dates['내일 오전'], '→ 2026-11-06');
  assert.equal(dates['다음 주 월요일까지'], '→ 2026-11-09');
  assert.equal(dates['오늘 17시까지'], '→ 2026-11-05 17:00');
});

const sample = JSON.parse(readFileSync(new URL('../../src/Haru.Api/Samples/sample-plan.json', import.meta.url), 'utf8'));
const plan = () => ({ ...structuredClone(sample), meta: { ...sample.meta, source: 'ai', aiGenerated: true } });

test('invalid results and a done without a plan cannot enable export or persist success', () => {
  const { call, run, node, storage } = setup();
  const invalid = plan();
  invalid.validation.valid = false;
  call('begin', 'live');
  run(`handleEvent('plan', ${JSON.stringify(invalid)}); handleEvent('done', {elapsedMs:30})`);
  assert.equal(run('S.plan'), null);
  assert.equal(run('S.error.code'), 'INVALID_SCHEDULE');
  assert.equal(node('#btn-export').disabled, true);
  assert.equal(storage.size, 0);
  run('finish("test")');
});

test('a valid plan is not saved until done and a broken stream leaves the previous plan intact', async () => {
  const { run, node, storage } = setup({ fetch: () => { throw new Error('no automatic fallback'); } });
  run(`S.saved = {plan:${JSON.stringify(plan())}, input:{text:'previous'}}; begin('live');`);
  run(`handleEvent('plan', ${JSON.stringify(plan())})`);
  assert.equal(storage.size, 0);
  assert.equal(node('#btn-export').disabled, true);
  await run("fail('STREAM_CLOSED', 'disconnected')");
  assert.equal(run('S.plan'), null);
  assert.equal(run('S.saved.input.text'), 'previous');
  assert.equal(node('#btn-restore').hidden, false);
  assert.equal(node('#btn-export').disabled, true);
});

test('fixed conflicts show violations without exporting or replacing input with a sample', async () => {
  const { run, node } = setup({ fetch: () => { throw new Error('unexpected fetch'); } });
  node('#text').value = 'my input';
  run('begin("live"); S.view = {day:"2026-11-05",workStart:"09:00",workEnd:"18:00"}');
  run("handleEvent('schedule', {iteration:1, blocks:[], deferred:[]}); handleEvent('validation', {report:{valid:false,violations:[{message:'회의가 겹칩니다'}],loadRatio:0,occupiedMinutes:0,availableMinutes:480}})");
  await run("fail('INVALID_SCHEDULE', 'change input')");
  assert.equal(node('#violations').textContent, '회의가 겹칩니다');
  assert.equal(node('#text').value, 'my input');
  assert.equal(node('#btn-export').disabled, true);
  assert.equal(node('#btn-copy').disabled, true);
});

test('schedule differences use stable task IDs, not renumbered block IDs', () => {
  const { run } = setup();
  const changes = run(`scheduleChanges(
    {blocks:[{id:'b1',taskId:'t1',title:'work',start:'09:00',end:'10:00'},{id:'b2',taskId:'t2',title:'mail',start:'10:00',end:'10:30'}]},
    {blocks:[{id:'b2',taskId:'t1',title:'work',start:'11:00',end:'12:00'}]})`);
  assert.deepEqual(Array.from(changes), ['work: 09:00–10:00 → 11:00–12:00', 'mail: 10:00–10:30 → 오늘 배치하지 않음']);
});

test('sample loading is immediately busy and cancellable, and never posts to the AI API', async () => {
  const calls = [];
  const { run, node } = setup({
    fetch: (url, options) => {
      calls.push(url);
      return new Promise((resolve, reject) => options.signal.addEventListener('abort', () => reject(new DOMException('stop', 'AbortError'))));
    },
  });
  const pending = run('showSample()');
  assert.equal(node('#btn-stop').hidden, false);
  assert.equal(node('#btn-sample').disabled, true);
  run('S.ctrl.abort()');
  await pending;
  assert.equal(run('S.running'), false);
  assert.deepEqual(calls, ['/api/sample']);
  assert.equal(node('#agents-state').textContent, '중지됨');
});

test('live example fetches input then calls the normal live API with current settings', async () => {
  const calls = [];
  const { run, node } = setup({
    fetch: async (url, options) => {
      calls.push([url, options?.body && JSON.parse(options.body)]);
      return url === '/api/sample'
        ? { ok: true, json: async () => ({ inputText: 'example text' }) }
        : { ok: false, json: async () => ({ error: { code: 'AI_UNAVAILABLE', message: 'offline' } }) };
    },
  });
  node('#workStart').value = '10:00'; node('#workEnd').value = '18:00';
  await run('runExample()');
  assert.equal(calls.length, 2);
  assert.equal(calls[1][0], '/api/plan');
  assert.equal(calls[1][1].text, 'example text');
  assert.equal(calls[1][1].workStart, '10:00');
  assert.equal(run('S.mode'), 'live');
  assert.equal(run('S.plan'), null);
});

test('restoring a previous valid plan restores its settings and clear removes both snapshots', () => {
  const { run, node, storage } = setup();
  const p = JSON.stringify(plan());
  run(`S.saved={plan:${p},input:{text:'old input',workStart:'10:00',workEnd:'18:00',lunchStart:'',lunchEnd:''},history:[]};
    S.previous={plan:${p}}; S.plan=null; restorePrevious();`);
  assert.equal(node('#text').value, 'old input');
  assert.equal(node('#workStart').value, '10:00');
  assert.equal(node('#lunchStart').value, '');
  assert.equal(node('#btn-export').disabled, false);
  assert.equal(run('S.mode'), 'restored');
  run('clearAll()');
  assert.equal(storage.size, 0);
  assert.equal(run('S.saved'), null);
  assert.equal(run('S.previous'), null);
  assert.equal(node('#btn-restore').hidden, true);
});

test('confirmation measures request plus review once and never measures replay as live AI', () => {
  let clock = 1000;
  const { run } = setup({ performance: { now: () => clock } });
  run("begin('live'); S.input={text:'work'}");
  clock = 31000;
  run(`handleEvent('plan',${JSON.stringify(plan())}); handleEvent('done',{elapsedMs:29000})`);
  clock = 51000;
  run('confirmPlan()');
  assert.equal(run('S.measurement.requestMs'), 30000);
  assert.equal(run('S.measurement.reviewMs'), 20000);
  assert.equal(run('S.measurement.totalMs'), 50000);
  clock = 61000; run('confirmPlan()');
  assert.equal(run('S.measurement.totalMs'), 50000);
  run(`begin('replay'); handleEvent('plan',${JSON.stringify(plan())}); handleEvent('done',{elapsedMs:29000}); confirmPlan()`);
  assert.equal(run('S.measurement'), null);
});

test('a stale invalid plan in browser storage is never restored', () => {
  const { run, storage } = setup();
  const p = plan(); p.validation.valid = false;
  storage.set('haru.v1', JSON.stringify({ plan: p, text: 'notes' }));
  run(`S.config = {today:${JSON.stringify(p.day)}}; restore()`);
  assert.equal(run('S.plan'), null);
  assert.equal(run('S.saved'), null);
});

test('repeated scheduling without time changes does not claim an improvement', () => {
  const { run, node } = setup();
  const p = JSON.stringify(plan());
  run(`const p=${p}; S.history=[{blocks:p.blocks,deferred:p.deferred,report:p.validation,reason:'first'},{blocks:p.blocks,deferred:p.deferred,report:p.validation,reason:'revision requested'}]; paintReview()`);
  assert.ok(node('#review').textContent.includes('시간·배치 변경 없음'));
});

test('a live draft is labelled as running, not as a replay measurement', () => {
  const { run, node } = setup();
  run("begin('live')");
  assert.ok(node('#measurement').textContent.includes('실제 AI 실행 중'));
  run('finish("test")');
});
