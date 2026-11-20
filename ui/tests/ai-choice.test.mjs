// node --test ui/tests/   — the chosen AI is used for everything (M9 fix): whichever AI is chosen in the picker is the
// one every question, first scan and summary goes to; switching stops the old AI's in-app task first. Being signed in
// to several AIs at once never matters.
// Exercised end to end: the real app-flow.js (AppFlow) runs in node on the fixture backend (adapter-fixture.js), with a
// small stateful React stub (as rebuild-v72/v73, plus effects; elements are shallow, so the picker's and the box's
// props are called the way the components call them). Every backend call is recorded; the tests assert on the
// recorded agent.* calls.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import fs from 'node:fs';
import vm from 'node:vm';
const require = createRequire(import.meta.url);
const { createFixtureAdapter } = require('../js/adapter-fixture.js');
const fixture = JSON.parse(fs.readFileSync(new URL('../fixtures/m1-simulated-audit.json', import.meta.url), 'utf8'));
const extras = JSON.parse(fs.readFileSync(new URL('../fixtures/m2-extras.json', import.meta.url), 'utf8'));
const src = (f) => fs.readFileSync(new URL('../js/' + f, import.meta.url), 'utf8');
const tick = (ms = 0) => new Promise((r) => setTimeout(r, ms));
const key = () => crypto.randomUUID();

function memStore() {
  const m = {};
  return { getItem: (k) => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); }, removeItem: (k) => { delete m[k]; } };
}

// A stateful React stub: hooks keep their values across renders in call order; effects run after a render when their
// deps changed; elements are shallow ({ type, props }), so components are never called.
function makeReact() {
  const R = { slots: [], i: 0, dirty: true, effects: [] };
  const same = (a, b) => !!a && !!b && a.length === b.length && a.every((x, i) => Object.is(x, b[i]));
  function Component(props) { this.props = props; }
  R.React = {
    createElement(type, props, ...children) { return { type, props: Object.assign({}, props, { children: children.length <= 1 ? children[0] : children }) }; },
    useState(v) {
      const i = R.i++;
      if (!(i in R.slots)) R.slots[i] = { v: typeof v === 'function' ? v() : v };
      const s = R.slots[i];
      if (!s.set) s.set = (n) => { const next = typeof n === 'function' ? n(s.v) : n; if (!Object.is(next, s.v)) { s.v = next; R.dirty = true; } };
      return [s.v, s.set];
    },
    useRef(v) { const i = R.i++; if (!(i in R.slots)) R.slots[i] = { current: v === undefined ? null : v }; return R.slots[i]; },
    useEffect(fn, deps) {
      const i = R.i++;
      const s = R.slots[i] || (R.slots[i] = { deps: null, cleanup: null, ran: false });
      if (s.ran && deps && same(deps, s.deps)) return;
      s.deps = deps; s.ran = true;
      R.effects.push(() => { if (typeof s.cleanup === 'function') s.cleanup(); s.cleanup = fn(); });
    },
    useLayoutEffect(fn, deps) { return R.React.useEffect(fn, deps); },
    useMemo(fn, deps) {
      const i = R.i++;
      const s = R.slots[i];
      if (s && deps && same(deps, s.deps)) return s.v;
      R.slots[i] = { v: fn(), deps };
      return R.slots[i].v;
    },
    useCallback: (f) => f, useId: () => 'id', Fragment: 'fragment', Component, forwardRef: (f) => f, memo: (f) => f
  };
  return R;
}

function loadApp(R, storage) {
  const listeners = {};
  const window = {
    React: R.React, OptTool: {}, localStorage: storage,
    matchMedia: () => ({ matches: true, addEventListener() {}, removeEventListener() {} }),   // reduced motion: no fades
    addEventListener(t, f) { (listeners[t] = listeners[t] || []).push(f); }, removeEventListener() {}
  };
  const document = { documentElement: { dataset: {}, style: {} }, querySelector: () => null, querySelectorAll: () => [], getElementById: () => null,
    addEventListener() {}, removeEventListener() {}, body: { appendChild() {} } };
  const ctx = vm.createContext({ window, document, navigator: {}, console, crypto, setTimeout, clearTimeout, setInterval, clearInterval,
    requestAnimationFrame: () => 0, cancelAnimationFrame() {} });
  window.window = window;
  for (const f of ['components.js', 'connection-state.js', 'bench.js', 'report-problem.js', 'fix-plan.js', 'app-flow.js']) vm.runInContext(src(f), ctx, { filename: f });
  return window.OptTool;
}

// The fixture as the host would be at start: a finished report, permissions set, Claude Code installed and signed in.
async function preparedBackend() {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  const o = await be.call('onboarding.get');
  await be.call('onboarding.save', { expectedRevision: o.revision, patch: { uses: ['Gaming'], inventoryConsent: 'skipped', provider: { value: 'local', label: 'Local only' }, step: 'complete' } });
  const fin = new Promise((r) => { const off = be.onEvent((e) => { if (e.type === 'scan.finished') { off(); r(e); } }); });
  await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  await fin;
  const p = await be.call('ai.getPermissions');
  const rec = JSON.parse(JSON.stringify(p.permissions)); delete rec.revision; rec.set = true;
  await be.call('ai.setPermissions', { idempotencyKey: key(), revision: p.permissions.revision, permissions: Object.assign(rec, { set: true }) }).catch(() => {});
  return be;
}

// The app on that backend, every call recorded. agent.start can be held (hold = true) until release().
async function startApp() {
  const inner = await preparedBackend();
  const calls = [], held = [], log = [], toasts = [];
  // hold: agent.start waits until release(). refuse: the next n agent.start calls are answered not_chosen (as the host
  // answers a start that reaches it before the choice's save).
  const gate = { hold: false, refuse: 0 };
  const backend = {
    kind: 'fixture', state: () => inner.state(), detail: () => (inner.detail ? inner.detail() : null),
    onEvent: (f) => inner.onEvent(f), onState: (f) => inner.onState(f),
    call(method, params) {
      const rec = { method, params: params || {} };
      calls.push(rec);
      log.push({ ev: 'call', rec });
      const run = () => inner.call(method, params).then((r) => { rec.result = r; log.push({ ev: 'done', rec }); return r; }, (e) => { rec.error = e; log.push({ ev: 'failed', rec }); throw e; });
      if (method === 'agent.start' && gate.refuse > 0) {
        gate.refuse--;
        rec.error = { code: -32013, message: 'That AI is not the one chosen.', data: { reason: 'not_chosen', chosen: null } };
        log.push({ ev: 'failed', rec });
        return Promise.reject(rec.error);
      }
      if (method === 'agent.start' && gate.hold) return new Promise((res, rej) => held.push(() => run().then(res, rej)));
      return run();
    }
  };
  const R = makeReact(), storage = memStore();
  const OT = loadApp(R, storage);
  let tree = null;
  function renderOnce() {
    R.i = 0; R.dirty = false;
    tree = OT.AppFlow({ backend });
    const t = all(tree).find((n) => n.props && typeof n.props.className === 'string' && /ot-toast/.test(n.props.className));
    if (t && toasts[toasts.length - 1] !== t.props.children) toasts.push(t.props.children);
    const fx = R.effects; R.effects = [];
    fx.forEach((f) => f());
  }
  async function settle(ms = 120) {
    const end = Date.now() + ms;
    do { if (R.dirty || !tree) renderOnce(); await tick(2); } while (Date.now() < end || R.dirty);
  }
  await settle(250);
  const app = {
    calls, gate, inner, settle, log, toasts,
    get tree() { return tree; },
    find(type) { return all(tree).find((n) => n.type === type) || null; },
    stage() { return R.slots.find((s) => s && s.v && typeof s.v === 'object' && 'leaving' in s.v && 'id' in s.v).v.id; },
    async release() { const h = held.splice(0); h.forEach((f) => f()); await settle(); },
    agent(method) { return calls.filter((c) => c.method === method); },
    kindOfTask(taskId) { const s = calls.find((c) => c.method === 'agent.start' && c.result && c.result.taskId === taskId); return s ? s.params.recipientKind : null; },
    // The picker, as the chooser opens it (the pill's Change).
    async choose(value, label, api) {
      if (app.stage() !== 'login') { app.openLogin(); await settle(); }
      app.find(OT.LoginPanel).props.onConnect(value, label, api);
      await settle();
    },
    // The first look's page: Skip back to the main page (the run goes on).
    async toMain() { if (app.stage() === 'aiscan') { app.find(OT.AiScanPanel).props.onSkip(); await settle(); } },
    // Connect an AI in the picker and come back to the main page.
    async connect(value, label, api) { await app.choose(value, label, api); await settle(300); await app.toMain(); },
    async ask(text) { app.find(OT.PromptBox).props.onSubmit(text); await settle(); },
    async activeKinds() { const r = await inner.call('agent.active'); return (r.tasks || []).map((t) => t.recipient.kind); },
    OT
  };
  // The pill's Change opens the chooser.
  app.openLogin = () => { all(tree).find((n) => n.type === OT.ConnectionPill).props.onChange(); };
  return app;
}
function all(n, out = []) {
  if (n == null || n === false || n === true) return out;
  if (Array.isArray(n)) { n.forEach((x) => all(x, out)); return out; }
  if (typeof n !== 'object') return out;
  out.push(n);
  if (n.props) all(n.props.children, out);
  return out;
}

test('harness: the app boots to the main page on the fixture', async () => {
  const app = await startApp();
  assert.equal(app.stage(), 'main');
});

const SCAN = /^Look over my latest report/;
const CLAUDE = 'claude_code_mcp', GPT = 'chatgpt_plan', API = 'anthropic_api';
const PICK = { [CLAUDE]: ['claude', 'Claude'], [GPT]: ['chatgpt', 'ChatGPT'], [API]: ['api-key', 'Claude (API)', 'anthropic'] };
// The agent.* calls from index i on, each with the AI it went to.
function agentCalls(app, i) {
  return app.calls.slice(i).filter((c) => /^agent\./.test(c.method)).map((c) => ({
    method: c.method, kind: c.method === 'agent.start' ? c.params.recipientKind : app.kindOfTask(c.params.taskId), taskId: c.params.taskId || (c.result && c.result.taskId), text: c.params.text
  }));
}
// Connect `from`, then switch to `to` in the picker; the agent.* calls since the switch.
async function switchCase(from, to) {
  const app = await startApp();
  await app.connect(...PICK[from]);
  const first = agentCalls(app, 0);
  assert.ok(first.some((c) => c.method === 'agent.start' && c.kind === from), 'the first AI started its task');
  const oldTask = first.find((c) => c.method === 'agent.start' && c.kind === from).taskId;
  const mark = app.calls.length, logMark = app.log.length;
  await app.choose(...PICK[to]);
  await app.settle(400);
  return { app, oldTask, after: agentCalls(app, mark), logMark };
}
// The choice's onboarding.save (with the new AI's kind) has come back before that AI's first agent.start is sent.
function assertSavedFirst(app, kind, logMark = 0) {
  const log = app.log.slice(logMark);
  const saved = log.findIndex((e) => e.ev === 'done' && e.rec.method === 'onboarding.save' && e.rec.params.patch.provider && e.rec.params.patch.provider.kind === kind);
  const start = log.findIndex((e) => e.ev === 'call' && e.rec.method === 'agent.start' && e.rec.params.recipientKind === kind);
  assert.ok(saved >= 0, 'the choice is saved with its kind');
  assert.ok(start >= 0 && saved < start, 'the save came back before the start was sent');
}
function assertSwitched({ app, oldTask, after, logMark }, from, to) {
  assertSavedFirst(app, to, logMark);
  const starts = after.filter((c) => c.method === 'agent.start');
  assert.deepEqual(starts.map((c) => c.kind), [to], 'exactly one task is started, for the new AI');
  const asks = after.filter((c) => c.method === 'agent.ask');
  assert.ok(asks.length >= 1 && asks.every((c) => c.kind === to && c.taskId === starts[0].taskId), 'every question goes to the new AI: ' + JSON.stringify(asks));
  assert.ok(asks.some((c) => SCAN.test(c.text)), 'the first scan is asked of the new AI');
  // Nothing of the old AI's after the switch but its stop.
  assert.deepEqual(after.filter((c) => c.kind === from).map((c) => c.method + ':' + c.taskId), ['agent.stop:' + oldTask]);
  // (b) the old task is stopped before the new AI's scan starts.
  const iStop = after.findIndex((c) => c.method === 'agent.stop' && c.taskId === oldTask);
  const iStart = after.findIndex((c) => c.method === 'agent.start');
  assert.ok(iStop >= 0 && iStop < iStart, 'agent.stop of the old task comes before agent.start of the new one');
  return app;
}

test('(a)(b) Claude, then ChatGPT signed in from the picker: the first scan starts and is asked on ChatGPT only; Claude is stopped first', async () => {
  const r = await switchCase(CLAUDE, GPT);
  const app = assertSwitched(r, CLAUDE, GPT);
  assert.deepEqual(await app.activeKinds(), [GPT]);
});

test('(c) ChatGPT, then Claude: the same', async () => {
  const r = await switchCase(GPT, CLAUDE);
  const app = assertSwitched(r, GPT, CLAUDE);
  assert.deepEqual(await app.activeKinds(), [CLAUDE]);
});

test('(c) API key, then Claude: the same', async () => {
  const r = await switchCase(API, CLAUDE);
  const app = assertSwitched(r, API, CLAUDE);
  assert.deepEqual(await app.activeKinds(), [CLAUDE]);
});

test('(d) a question typed right after the switch, while the first scan is still starting, never reaches the old AI and goes to the new one', async () => {
  const app = await startApp();
  await app.connect(...PICK[CLAUDE]);
  const oldTask = agentCalls(app, 0).find((c) => c.method === 'agent.start').taskId;
  const mark = app.calls.length, logMark = app.log.length;
  app.gate.hold = true;                      // ChatGPT's agent.start waits
  await app.choose(...PICK[GPT]);
  assert.equal(app.stage(), 'main');
  await app.ask('Why is my PC slow?');
  // Not lost: back in the box while the new AI is starting; nothing went to Claude.
  assert.equal(app.find(app.OT.PromptBox).props.restore.text, 'Why is my PC slow?');
  assert.deepEqual(agentCalls(app, mark).filter((c) => c.kind === CLAUDE).map((c) => c.method + ':' + c.taskId), ['agent.stop:' + oldTask]);
  app.gate.hold = false;
  await app.release();
  await app.settle(400);
  await app.toMain();
  // Asked again (the first look has answered): ChatGPT's task.
  const mark2 = app.calls.length;
  await app.ask('Why is my PC slow?');
  await app.settle(200);
  const asks = agentCalls(app, mark2).filter((c) => c.method === 'agent.ask');
  assert.deepEqual(asks.map((c) => [c.kind, c.text]), [[GPT, 'Why is my PC slow?']]);
  assert.equal(agentCalls(app, mark).filter((c) => c.kind === CLAUDE).length, 1);
  assert.deepEqual(agentCalls(app, mark).filter((c) => c.method === 'agent.start').map((c) => c.kind), [GPT]);
  assertSavedFirst(app, GPT, logMark);
});

test('(e) switching back and forth quickly ends with exactly one active task, for the last chosen AI', async () => {
  const app = await startApp();
  await app.connect(...PICK[CLAUDE]);
  const mark = app.calls.length;
  app.gate.hold = true;                      // starts come back only after all the switching
  await app.choose(...PICK[GPT]);
  await app.choose(...PICK[CLAUDE]);
  await app.choose(...PICK[GPT]);
  app.gate.hold = false;
  await app.release();
  await app.settle(400);
  assert.deepEqual(await app.activeKinds(), [GPT]);
  const after = agentCalls(app, mark);
  assert.deepEqual(after.filter((c) => c.method === 'agent.start').map((c) => c.kind), [GPT], 'one start for ChatGPT, none doubled');
  assert.ok(after.filter((c) => c.method === 'agent.ask').every((c) => c.kind === GPT));
  assertSavedFirst(app, GPT);
  assert.equal((await app.inner.call('onboarding.get')).profile.provider.kind, GPT, 'the host has the last choice saved');
});

test('(e) back and forth ending on Claude: nothing of ChatGPT stays running (its start waits for its save, then finds Claude chosen); the next question starts one Claude task', async () => {
  const app = await startApp();
  await app.connect(...PICK[CLAUDE]);
  const mark = app.calls.length;
  app.gate.hold = true;
  await app.choose(...PICK[GPT]);
  await app.choose(...PICK[CLAUDE]);
  app.gate.hold = false;
  await app.release();
  await app.settle(300);
  assert.deepEqual(await app.activeKinds(), [], 'ChatGPT’s late task is stopped at once, nothing asked of it');
  assert.equal(agentCalls(app, mark).filter((c) => c.method === 'agent.ask').length, 0);
  assert.equal((await app.inner.call('onboarding.get')).profile.provider.kind, CLAUDE, 'the host has the last choice saved');
  await app.ask('Why is my PC slow?');
  await app.settle(200);
  assert.deepEqual(await app.activeKinds(), [CLAUDE]);
  const asks = agentCalls(app, mark).filter((c) => c.method === 'agent.ask');
  assert.deepEqual(asks.map((c) => c.kind), [CLAUDE]);
});

/* ---------- a start the host refuses as not_chosen ---------- */
test('not_chosen: the app reads the saved choice again and starts once more; nothing is shown', async () => {
  const app = await startApp();
  await app.connect(...PICK[CLAUDE]);
  const mark = app.calls.length;
  app.gate.refuse = 1;                       // the first start reaches the host "before" its choice
  await app.choose(...PICK[GPT]);
  await app.settle(400);
  const seq = app.calls.slice(mark).filter((c) => c.method === 'agent.start' || c.method === 'onboarding.get')
    .map((c) => c.method === 'agent.start' ? 'start:' + c.params.recipientKind + ':' + (c.error ? c.error.data.reason : 'ok') : 'get');
  assert.deepEqual(seq, ['start:' + GPT + ':not_chosen', 'get', 'start:' + GPT + ':ok']);
  assert.deepEqual(await app.activeKinds(), [GPT]);
  assert.ok(agentCalls(app, mark).some((c) => c.method === 'agent.ask' && c.kind === GPT && SCAN.test(c.text)), 'the first look goes on');
  assert.deepEqual(app.toasts.filter((t) => t === 'Not started'), []);
});

test('not_chosen twice: one re-read and one retry only, then nothing more and nothing shown', async () => {
  const app = await startApp();
  await app.connect(...PICK[CLAUDE]);
  const mark = app.calls.length;
  app.gate.refuse = 2;
  await app.choose(...PICK[GPT]);
  await app.settle(400);
  const after = app.calls.slice(mark);
  assert.equal(after.filter((c) => c.method === 'agent.start').length, 2);
  assert.equal(after.filter((c) => c.method === 'onboarding.get').length, 1);
  assert.deepEqual(await app.activeKinds(), []);
  assert.deepEqual(app.toasts.filter((t) => t === 'Not started'), []);
});

/* ---------- the fixture mirrors the host ---------- */
test('fixture: provider.kind is stored and must match the value; starts and in-app reviews of another AI are refused not_chosen', async () => {
  const be = await preparedBackend();
  const save = async (provider) => be.call('onboarding.save', { expectedRevision: (await be.call('onboarding.get')).revision, patch: { provider } });
  for (const [value, kind] of [['chatgpt', 'openai_api'], ['api', 'chatgpt_plan'], ['claude-code', 'anthropic_api'], ['local', 'anthropic_api'], ['later', 'claude_code_mcp'], ['api', '']]) {
    const e = await save({ value, label: 'x', kind }).then(() => null, (err) => err);
    assert.deepEqual([e && e.code, e && e.data.field], [-32602, 'patch.provider.kind'], value + ' + ' + kind);
  }
  const reportId = (await be.call('onboarding.get')).resume.reportId;
  const start = (kind) => be.call('agent.start', { idempotencyKey: key(), goal: 'x', recipientKind: kind, scope: { reportId } });
  const reason = (p) => p.then(() => null, (e) => [e.code, e.data.reason, e.data.chosen]);
  await be.call('shell.clientStatus', { client: 'claude_code' });
  await be.call('shell.requestApiKey', { kind: 'anthropic_api' });
  await be.call('shell.requestApiKey', { kind: 'openai_api' });

  // Local only (the prepared profile): nothing in-app or headless starts.
  assert.deepEqual(await reason(start(API)), [-32013, 'not_chosen', null]);
  assert.deepEqual(await reason(start(CLAUDE)), [-32013, 'not_chosen', null]);

  // OpenAI chosen, both keys saved: OpenAI starts; Anthropic and Claude are refused; so is an Anthropic review.
  assert.equal((await save({ value: 'api', label: 'OpenAI (API)', kind: 'openai_api' })).profile.provider.kind, 'openai_api');
  const t = await start('openai_api');
  assert.deepEqual(await reason(start(API)), [-32013, 'not_chosen', 'openai_api']);
  assert.deepEqual(await reason(start(CLAUDE)), [-32013, 'not_chosen', 'openai_api']);
  assert.deepEqual(await reason(be.call('review.preview', { recipientKind: API, reportId })), [-32013, 'not_chosen', 'openai_api']);
  const pv = await be.call('review.preview', { recipientKind: 'openai_api', reportId });

  // Switching to Anthropic stops the OpenAI task; OpenAI's preview is no longer sent.
  await save({ value: 'api', label: 'Claude (API)', kind: API });
  assert.equal((await be.call('agent.get', { taskId: t.taskId })).status, 'stopped');
  assert.deepEqual(await reason(be.call('review.submit', { idempotencyKey: key(), previewId: pv.previewId })), [-32013, 'not_chosen', API]);

  // An older "api" profile (no kind) with both keys: either API kind.
  await save({ value: 'api', label: 'API key' });
  const a = await start(API), b = await start('openai_api');
  assert.deepEqual([a.status, b.status], ['active', 'active']);
  // Only OpenAI's key left: that profile now means OpenAI, and a new Anthropic task is refused.
  await be.call('provider.disconnect', { kind: API });
  assert.deepEqual(await reason(start(CLAUDE)), [-32013, 'not_chosen', 'openai_api']);
});

/* ---------- the decisions behind it (connection-state.js) ---------- */
const CS = require('../js/connection-state.js');
const tk = (kind, extra) => Object.assign({ taskId: 't-' + kind, status: 'active', recipient: { kind, mode: kind === CLAUDE ? 'headless' : undefined }, scope: { reportId: 'r1' } }, extra);
test('askTarget: a task handed in decides; otherwise the current task only when it is the chosen AI’s, else a new one for the chosen AI', () => {
  const claude = tk(CLAUDE);
  assert.deepEqual(CS.askTarget({ given: claude, current: claude, chosenKind: GPT, reportId: 'r1' }), { task: claude });
  assert.deepEqual(CS.askTarget({ current: claude, chosenKind: GPT, reportId: 'r1' }), { start: GPT });
  assert.deepEqual(CS.askTarget({ current: claude, chosenKind: CLAUDE, reportId: 'r1' }), { task: claude });
  assert.deepEqual(CS.askTarget({ current: tk(CLAUDE, { status: 'stopped' }), chosenKind: CLAUDE, reportId: 'r1' }), { start: CLAUDE });
  assert.deepEqual(CS.askTarget({ current: claude, chosenKind: CLAUDE, reportId: 'r2' }), { start: CLAUDE });
  // A paired outside window is asked in that window, never here.
  assert.deepEqual(CS.askTarget({ current: tk(CLAUDE, { recipient: { kind: CLAUDE } }), chosenKind: CLAUDE, reportId: 'r1' }), { start: CLAUDE });
  assert.deepEqual(CS.askTarget({ current: null, chosenKind: null, reportId: 'r1' }), { task: null });
});
test('switchStops: an active in-app task of another AI stops; an outside Claude Code window and the same AI are left', () => {
  assert.equal(CS.switchStops(tk(CLAUDE), GPT), true);
  assert.equal(CS.switchStops(tk(API), CLAUDE), true);
  assert.equal(CS.switchStops(tk(GPT), GPT), false);
  assert.equal(CS.switchStops(tk(GPT, { status: 'stopped' }), CLAUDE), false);
  assert.equal(CS.switchStops(tk(CLAUDE, { recipient: { kind: CLAUDE } }), GPT), false);
  assert.equal(CS.switchStops(null, GPT), false);
});
test('staleStart: an in-app start that comes back for an AI no longer chosen; a paired window’s start never is', () => {
  assert.equal(CS.staleStart({ kind: GPT }, tk(GPT), CLAUDE), true);
  assert.equal(CS.staleStart({ kind: GPT }, tk(GPT), GPT), false);
  assert.equal(CS.staleStart({ kind: CLAUDE, pairingId: 'p1', grant: {} }, tk(CLAUDE, { recipient: { kind: CLAUDE } }), GPT), false);
});
