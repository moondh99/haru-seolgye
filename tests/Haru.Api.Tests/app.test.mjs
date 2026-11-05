import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

class Element {
  constructor() {
    this.children = [];
    this.dataset = {};
    this.className = '';
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
  querySelector(selector) { return this.querySelectorAll(selector)[0] ?? null; }
  querySelectorAll(selector) {
    return this.children.filter(child => child instanceof Element).flatMap(child => [
      ...(selector === `.${child.className}` ? [child] : []),
      ...child.querySelectorAll(selector),
    ]);
  }
}

function setup() {
  const banner = new Element();
  const step = new Element();
  const tools = new Element();
  tools.className = 'tools';
  step.append(tools);
  const context = vm.createContext({
    document: {
      createElement: () => new Element(),
      querySelector: selector => selector === '#banner' ? banner : step,
    },
    matchMedia: () => ({ matches: true }),
  });
  const source = readFileSync(new URL('../../src/Haru.Api/wwwroot/app.js', import.meta.url), 'utf8');
  vm.runInContext(source.replace(/\ninit\(\);\s*$/, ''), context);
  const call = (name, data) => vm.runInContext(`${name}(${JSON.stringify(data)})`, context);
  return { banner, tools, call, context };
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
