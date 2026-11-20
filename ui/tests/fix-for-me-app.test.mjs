// node --test ui/tests/   — m9 §17 in the real app (Design System v80): Fix for me, the plan in the card with step switches
// and "Change something…", administrator checks in the card and beside the chat, every card.fix state, and the
// administrator row on the permissions page. The real app-flow.js (AppFlow) runs in node on the fixture backend with the
// stateful React stub of ai-choice.test.mjs; elements are shallow, so the cards' props are read and called the way the
// components call them, and every backend call is recorded.
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
  const window = {
    React: R.React, OptTool: {}, localStorage: storage,
    matchMedia: () => ({ matches: true, addEventListener() {}, removeEventListener() {} }),
    addEventListener() {}, removeEventListener() {}
  };
  const document = { documentElement: { dataset: {}, style: {} }, querySelector: () => null, querySelectorAll: () => [], getElementById: () => null,
    addEventListener() {}, removeEventListener() {}, body: { appendChild() {} } };
  const ctx = vm.createContext({ window, document, navigator: {}, console, crypto, setTimeout, clearTimeout, setInterval, clearInterval,
    requestAnimationFrame: () => 0, cancelAnimationFrame() {} });
  window.window = window;
  for (const f of ['components.js', 'connection-state.js', 'bench.js', 'report-problem.js', 'fix-plan.js', 'app-flow.js']) vm.runInContext(src(f), ctx, { filename: f });
  return window.OptTool;
}
function all(n, out = []) {
  if (n == null || n === false || n === true) return out;
  if (Array.isArray(n)) { n.forEach((x) => all(x, out)); return out; }
  if (typeof n !== 'object') return out;
  out.push(n);
  if (n.props) all(n.props.children, out);
  return out;
}

// The fixture as the host would be at start: a finished report; permissions set (changes fix, administrator rights as
// asked); the administrator flag as asked.
async function preparedBackend(o) {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  const ob = await be.call('onboarding.get');
  await be.call('onboarding.save', { expectedRevision: ob.revision, patch: { uses: ['Gaming'], inventoryConsent: 'skipped', provider: { value: 'local', label: 'Local only' }, step: 'complete' } });
  const fin = new Promise((r) => { const off = be.onEvent((e) => { if (e.type === 'scan.finished') { off(); r(e); } }); });
  await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  await fin;
  if (o.flag) await be.call('fixture.setAdminAvailable', { available: true });
  const p = await be.call('ai.getPermissions');
  const rec = JSON.parse(JSON.stringify(p.permissions));
  rec.set = true; rec.do.changes = o.changes || 'fix'; rec.do.admin = !!o.admin;
  await be.call('ai.setPermissions', { idempotencyKey: key(), revision: p.permissions.revision, permissions: rec });
  return be;
}
async function startApp(o = {}) {
  const inner = await preparedBackend(o);
  const calls = [], toasts = [];
  const backend = {
    kind: 'fixture', state: () => inner.state(), detail: () => (inner.detail ? inner.detail() : null),
    onEvent: (f) => inner.onEvent(f), onState: (f) => inner.onState(f),
    call(method, params) {
      const rec = { method, params: params || {} };
      calls.push(rec);
      return inner.call(method, params).then((r) => { rec.result = r; return r; }, (e) => { rec.error = e; throw e; });
    }
  };
  const R = makeReact(), OT = loadApp(R, memStore());
  let tree = null;
  function renderOnce() {
    R.i = 0; R.dirty = false;
    tree = OT.AppFlow({ backend });
    const t = all(tree).find((n) => n.props && typeof n.props.className === 'string' && /ot-toast/.test(n.props.className));
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
    calls, inner, settle, toasts, OT,
    get tree() { return tree; },
    find(type) { return all(tree).find((n) => n.type === type) || null; },
    findAll(type) { return all(tree).filter((n) => n.type === type); },
    stage() { return R.slots.find((s) => s && s.v && typeof s.v === 'object' && 'leaving' in s.v && 'id' in s.v).v.id; },
    after(i, method) { return calls.slice(i).filter((c) => c.method === method); },
    // The deck's cards as InsightCard gets them.
    cards() { return app.findAll(OT.InsightCard).map((n) => n.props); },
    card(cid) { return app.cards().find((c) => c.cardKey === cid) || null; },
    reportCard() { return app.cards().find((c) => !c.aiTag && !c.checkup) || null; },
    taskId() { const s = calls.filter((c) => c.method === 'agent.start' && c.result).pop(); return s ? s.result.taskId : null; },
    // Waits until pred() holds (re-rendering), or fails with what is on the card.
    async until(pred, what, ms = 3000) {
      const end = Date.now() + ms;
      while (Date.now() < end) { await settle(20); if (pred()) return; }
      assert.fail('timed out waiting for ' + what);
    }
  };
  // Claude (headless in the app), and the first look done.
  all(tree).find((n) => n.type === OT.ConnectionPill).props.onChange();
  await settle();
  app.find(OT.LoginPanel).props.onConnect('claude', 'Claude');
  await settle(300);
  if (app.stage() === 'aiscan') { app.find(OT.AiScanPanel).props.onSkip(); await settle(); }
  await app.until(() => !!app.reportCard() && (app.reportCard().fixAvailable === 'yes' || app.reportCard().fixAvailable === 'nochanges'), 'the first look to finish');
  return app;
}
const fixOf = (app, cid) => (app.card(cid) || {}).fix || null;
const stateOf = (app, cid) => (fixOf(app, cid) || {}).state || null;
// Fix for me on the first report card, with the fixture's assistant answering as `reply` says.
async function fixFirstCard(app, reply) {
  await app.inner.call('fixture.setFixReply', { mode: reply });
  const card = app.reportCard();
  assert.equal(card.fixAvailable, 'yes', 'Fix for me is offered on an open card with an AI that has tools');
  const mark = app.calls.length;
  card.onFix();
  await app.settle();
  const ask = app.after(mark, 'agent.ask')[0];
  assert.ok(ask, 'Fix for me asks');
  assert.equal(ask.params.intent, 'fix');
  assert.equal(ask.params.cardId, card.cardKey);
  assert.ok(!('text' in ask.params), 'no text with Fix for me');
  return card.cardKey;
}
const STARTUP = { title: 'Quieter startup', why: 'Two apps open with Windows that you rarely use.', steps: [
  { actionId: 'startup.disable', targetRef: { evidenceId: 'ev-fx-startup-spotify' } },
  { actionId: 'startup.disable', targetRef: { evidenceId: 'ev-fx-startup-steam' } },
  { actionId: 'setting.set', parameters: { settingId: 'notifications.windows_tips', value: 'off' } }] };

/* ---------- the button ---------- */
test('Fix for me: offered on open cards (yes), busy while a turn runs, nochanges with changes off; never without an AI', async () => {
  const app = await startApp();
  assert.ok(app.cards().length > 0);
  assert.ok(app.cards().every((c) => c.fixAvailable === 'yes'));
  const cid = await fixFirstCard(app, 'hold');
  assert.equal(stateOf(app, cid), 'working');
  assert.equal(app.card(cid).fixAvailable, undefined, 'no button while the panel shows');
  assert.ok(app.cards().filter((c) => c.cardKey !== cid).every((c) => c.fixAvailable === 'busy'), 'the other cards: busy');
  // Stop is the stop of the running turn (agent.cancel).
  const mark = app.calls.length;
  fixOf(app, cid).onStop();
  await app.settle();
  assert.equal(app.after(mark, 'agent.cancel').length, 1);
  await app.until(() => stateOf(app, cid) === 'noFix', 'no fix after Stop');
  const off = await startApp({ changes: 'off' });
  assert.ok(off.cards().every((c) => c.fixAvailable === 'nochanges'));
  off.cards()[0].onPermissions();
  await off.settle(200);
  assert.equal(off.stage(), 'permissions', 'Permissions leads to the page');
});

/* ---------- the plan in the card ---------- */
test('plan in the card: approve with a step off → action.narrow → the native approval of the new plan → done, steps left out', async () => {
  const app = await startApp();
  const cid = await fixFirstCard(app, 'plan');
  await app.until(() => stateOf(app, cid) === 'plan', 'the plan in the card');
  const f = fixOf(app, cid);
  assert.equal(f.plan.steps.length, 3);
  assert.equal(f.plan.requestedBy, undefined, 'no requester line in a card (the card is the context)');
  assert.equal(typeof f.plan.onChangeRequest, 'function');
  const parent = app.calls.filter((c) => c.method === 'action.get' || c.method === 'action.narrow').map((c) => c.result && c.result.planId).filter(Boolean).pop();
  const mark = app.calls.length;
  f.plan.onApprove([0, 2]);                       // step 2 switched off
  await app.settle(200);
  const nar = app.after(mark, 'action.narrow');
  assert.equal(nar.length, 1);
  assert.deepEqual(nar[0].params.keep, [1, 3]);
  assert.equal(nar[0].params.planId, parent);
  const appr = app.after(mark, 'shell.approvePlan');
  assert.equal(appr.length, 1, 'one native approval');
  assert.equal(appr[0].params.planId, nar[0].result.planId, 'for the new plan');
  await app.until(() => stateOf(app, cid) === 'doneLeftOut', 'done, steps left out');
  const d = fixOf(app, cid);
  assert.equal(d.plan.removed.length, 1, 'the switched-off step is listed as left out by you');
  assert.equal(typeof d.plan.onClose, 'function');
  const m2 = app.calls.length;
  d.plan.onClose();
  await app.settle();
  assert.deepEqual(app.after(m2, 'fix.dismiss').map((c) => c.params.cardId), [cid]);
  await app.until(() => !fixOf(app, cid) && app.card(cid).fixAvailable === 'yes', 'the card back to normal');
});

test('plan in the card: every step kept is the normal approval (no narrow); verified; Change something… revises', async () => {
  const app = await startApp();
  const cid = await fixFirstCard(app, 'plan');
  await app.until(() => stateOf(app, cid) === 'plan', 'the plan');
  // Change something…: agent.ask intent fix with text and revise (the plan it replaces); the card goes back to working.
  await app.inner.call('fixture.setFixReply', { mode: 'plan' });
  const planId = (app.calls.filter((c) => c.method === 'action.get' && c.result).pop() || {}).result.planId;
  const m0 = app.calls.length;
  fixOf(app, cid).plan.onChangeRequest('Keep Steam');
  await app.settle();
  const rv = app.after(m0, 'agent.ask')[0];
  assert.deepEqual([rv.params.intent, rv.params.text, rv.params.revise, rv.params.cardId], ['fix', 'Keep Steam', planId, cid]);
  await app.until(() => stateOf(app, cid) === 'plan' && fixOf(app, cid).plan && app.calls.filter((c) => c.method === 'action.get').length >= 2, 'the new plan');
  const mark = app.calls.length;
  fixOf(app, cid).plan.onApprove([0, 1, 2]);
  await app.settle(200);
  assert.equal(app.after(mark, 'action.narrow').length, 0);
  assert.equal(app.after(mark, 'shell.approvePlan').length, 1);
  await app.until(() => stateOf(app, cid) === 'verified', 'verified');
});

test('plan in the card: Not now declines; the card is back to normal', async () => {
  const app = await startApp();
  const cid = await fixFirstCard(app, 'plan');
  await app.until(() => stateOf(app, cid) === 'plan', 'the plan');
  const mark = app.calls.length;
  fixOf(app, cid).plan.onDecline();
  await app.settle();
  assert.equal(app.after(mark, 'action.decline').length, 1);
  await app.until(() => !fixOf(app, cid), 'back to normal');
});

test('leftOut: an administrator step left out shows as such in the plan (not available in this version)', async () => {
  const app = await startApp();
  const cid = await fixFirstCard(app, 'hold');
  const args = { title: 'Quieter after sign-in', why: 'SysMain and tips keep the PC busy.', steps: [
    { actionId: 'service.disable', targetRef: { evidenceId: 'ev-fx-svc-sysmain' }, parameters: { stop: false } },
    { actionId: 'setting.set', parameters: { settingId: 'notifications.windows_tips', value: 'off' } }] };
  const r = await app.inner.call('fixture.agentTool', { taskId: app.taskId(), tool: 'propose_fix', args });
  assert.ok(r.planId, JSON.stringify(r));
  await app.inner.call('fixture.finishTurn', { taskId: app.taskId() });
  await app.until(() => stateOf(app, cid) === 'plan' && fixOf(app, cid).plan, 'the plan');
  const lo = fixOf(app, cid).plan.leftOut;
  assert.equal(lo.length, 1);
  assert.equal(lo[0].reason, 'unavailable');
  assert.ok(lo[0].label);
});

/* ---------- run states ---------- */
for (const [n, outcome, want] of [[2, 'failed', 'partial'], [1, 'failed', 'failed'], [1, 'indeterminate', 'unclear']]) {
  test(`run states: a fix that ${outcome === 'failed' ? 'fails' : 'goes unclear'} at step ${n} → ${want}`, async () => {
    const app = await startApp();
    const cid = await fixFirstCard(app, 'plan');
    await app.until(() => stateOf(app, cid) === 'plan', 'the plan');
    await app.inner.call('fixture.failStep', { n, outcome });
    fixOf(app, cid).plan.onApprove([0, 1, 2]);
    await app.until(() => stateOf(app, cid) === 'applying' || stateOf(app, cid) === want, 'applying');
    await app.until(() => stateOf(app, cid) === want, want);
    assert.equal(typeof fixOf(app, cid).plan.onClose, 'function');
  });
}

test('finished: a report-only change ends finished (the result in the card), never verified', async () => {
  const app = await startApp();
  const cid = await fixFirstCard(app, 'hold');
  const r = await app.inner.call('fixture.agentTool', { taskId: app.taskId(), tool: 'request_action', args: { actionId: 'network.flush_dns' } });
  assert.ok(r.planId, JSON.stringify(r));
  await app.inner.call('fixture.finishTurn', { taskId: app.taskId() });
  await app.until(() => stateOf(app, cid) === 'plan' && fixOf(app, cid).plan, 'the plan');
  fixOf(app, cid).plan.onApprove([0]);
  await app.until(() => stateOf(app, cid) === 'finished', 'finished');
  const f = fixOf(app, cid);
  assert.ok(f.plan.result && (f.plan.result.key || f.plan.result.text), 'the result');
  const mark = app.calls.length;
  f.plan.onClose();
  await app.settle();
  assert.equal(app.after(mark, 'fix.dismiss').length, 1);
});

test('blocked: a plan that needs only administrator rights, which are off → the reason, Permissions and Fix for me', async () => {
  const app = await startApp({ flag: true, admin: false });
  const cid = await fixFirstCard(app, 'hold');
  await app.inner.call('fixture.agentTool', { taskId: app.taskId(), tool: 'propose_fix', args: { title: 'Slow down SysMain', why: 'SysMain keeps the disk busy.',
    steps: [{ actionId: 'service.disable', targetRef: { evidenceId: 'ev-fx-svc-sysmain' }, parameters: { stop: false } }] } });
  await app.inner.call('fixture.finishTurn', { taskId: app.taskId() });
  await app.until(() => stateOf(app, cid) === 'blocked', 'blocked');
  const f = fixOf(app, cid);
  assert.equal(f.cause, 'adminOff');
  assert.equal(typeof f.onPermissions, 'function');
  const mark = app.calls.length;
  f.onRetry();
  await app.settle();
  assert.equal(app.after(mark, 'agent.ask')[0].params.intent, 'fix');
});

/* ---------- opinions and no fix ---------- */
test('needs you on a report card: the note in the panel, Check again → manual.recheck with the card’s finding; the action row stays', async () => {
  const app = await startApp();
  const cid = await fixFirstCard(app, 'needs_user');
  await app.until(() => stateOf(app, cid) === 'needsYou', 'needs you');
  const c = app.card(cid), f = c.fix;
  assert.ok(f.note, 'the AI’s note');
  assert.equal(c.note, null, 'shown once (in the panel)');
  assert.equal(typeof f.onCheckAgain, 'function');
  assert.equal(c.fixAvailable, 'yes', 'the card’s own row (with Fix for me) stays under the opinion');
  const fid = (await app.inner.call('followup.get', { reportId: app.calls.find((c2) => c2.method === 'agent.start').params.scope.reportId })).card;
  const mark = app.calls.length;
  f.onCheckAgain();
  await app.settle();
  const rc = app.after(mark, 'manual.recheck');
  assert.equal(rc.length, 1);
  assert.ok(rc[0].params.findingId);
  assert.ok(fid);
});

test('needs you on an AI card: no Check again, Close → fix.dismiss', async () => {
  const app = await startApp();
  const rid = app.calls.find((c) => c.method === 'agent.start').params.scope.reportId;
  // The first look's new card (add_card) is further down the queue: bring it forward, then let the deck re-read.
  const added = await app.inner.call('fixture.lastToolResult', { tool: 'add_card' });
  const cid = added && (added.cardId || (added.result && added.result.cardId));
  assert.ok(cid, 'the first look added a card: ' + JSON.stringify(added));
  await app.inner.call('followup.front', { reportId: rid, cardIds: [cid] });
  await app.inner.call('fixture.agentTool', { taskId: app.taskId(), tool: 'note_card', args: { cardId: cid, text: 'Seen.' } });
  await app.until(() => !!app.card(cid), 'the AI card in the deck');
  assert.equal(app.card(cid).aiTag, true);
  await app.inner.call('fixture.setFixReply', { mode: 'needs_user' });
  app.card(cid).onFix();
  await app.until(() => stateOf(app, cid) === 'needsYou', 'needs you');
  const f = fixOf(app, cid);
  assert.equal(f.onCheckAgain, null, 'nothing to re-check on an AI card');
  const mark = app.calls.length;
  f.onClose();
  await app.settle();
  assert.deepEqual(app.after(mark, 'fix.dismiss').map((c) => c.params.cardId), [cid]);
  await app.until(() => !fixOf(app, cid), 'back to normal');
});

test('nothing to fix: the note and Close → fix.dismiss; no fix: Try again asks again', async () => {
  const app = await startApp();
  const cid = await fixFirstCard(app, 'nothing_to_fix');
  await app.until(() => stateOf(app, cid) === 'nothing', 'nothing');
  assert.ok(fixOf(app, cid).note);
  let mark = app.calls.length;
  fixOf(app, cid).onClose();
  await app.settle();
  assert.equal(app.after(mark, 'fix.dismiss').length, 1);
  await app.until(() => !fixOf(app, cid), 'back to normal');
  await app.inner.call('fixture.setFixReply', { mode: 'none' });
  app.card(cid).onFix();
  await app.until(() => stateOf(app, cid) === 'noFix', 'no fix');
  mark = app.calls.length;
  fixOf(app, cid).onRetry();
  await app.settle();
  assert.equal(app.after(mark, 'agent.ask')[0].params.intent, 'fix');
});

/* ---------- administrator checks ---------- */
test('checks in a card: ask → Not now (declined, the AI goes on: working with Checks skipped) → a new request waits (cooldown) → ask at 0 → running → working', async () => {
  const app = await startApp({ flag: true, admin: true });
  const cid = await fixFirstCard(app, 'hold');
  const tid = app.taskId();
  await app.inner.call('fixture.agentTool', { taskId: tid, tool: 'run_diagnostic', args: { diagnosticId: 'security.defender_history' } });
  await app.inner.call('fixture.finishTurn', { taskId: tid });
  await app.until(() => stateOf(app, cid) === 'checks' && fixOf(app, cid).checksState === 'ask', 'the request in the card');
  let f = fixOf(app, cid);
  assert.deepEqual(f.checks, ['Defender detection history · last 30 days'], 'the host’s labels, with the time span');
  let mark = app.calls.length;
  f.onDeclineChecks();
  await app.settle();
  assert.equal(app.after(mark, 'checks.decline').length, 1);
  await app.until(() => stateOf(app, cid) === 'working' && fixOf(app, cid).checksResult === 'declined', 'working, checks skipped');
  // The held continuation ends; a new fix asks for another check within the minute.
  await app.inner.call('fixture.finishTurn', { taskId: tid });
  await app.until(() => stateOf(app, cid) === 'noFix', 'no fix');
  await app.inner.call('fixture.setFixReply', { mode: 'hold' });
  fixOf(app, cid).onRetry();
  await app.until(() => stateOf(app, cid) === 'working', 'working');
  await app.inner.call('fixture.agentTool', { taskId: tid, tool: 'run_diagnostic', args: { diagnosticId: 'storage.health' } });
  await app.inner.call('fixture.finishTurn', { taskId: tid });
  await app.until(() => stateOf(app, cid) === 'checks' && fixOf(app, cid).checksState === 'wait', 'wait');
  f = fixOf(app, cid);
  assert.ok(f.secondsLeft > 0 && f.secondsLeft <= 60, 'seconds left from retryAt: ' + f.secondsLeft);
  assert.deepEqual(f.checks, ['Disk health']);
  await app.inner.call('fixture.endCooldown', {});
  await app.until(() => fixOf(app, cid).checksState === 'ask', 'back to ask');
  await app.inner.call('fixture.setChecksResult', { mode: 'complete', hold: true });
  mark = app.calls.length;
  fixOf(app, cid).onRunChecks();
  await app.settle();
  const ap = app.after(mark, 'checks.approve');
  assert.equal(ap.length, 1);
  assert.ok(ap[0].params.idempotencyKey && ap[0].params.requestId && ap[0].params.revision === 1);
  await app.until(() => stateOf(app, cid) === 'checksRunning', 'running');
  await app.inner.call('fixture.finishChecks', { requestId: ap[0].params.requestId });
  await app.until(() => stateOf(app, cid) === 'working' && fixOf(app, cid).checksResult === 'done', 'the AI goes on with the results');
});

// A request beside the chat (no card): asked outside a fix turn.
async function sideRequest(app, id = 'security.encryption') {
  const r = await app.inner.call('fixture.agentTool', { taskId: app.taskId(), tool: 'run_diagnostic', args: { diagnosticId: id } });
  assert.equal(r.status, 'awaiting_user', JSON.stringify(r));
  await app.until(() => !!app.find(app.OT.AdminChecks), 'the request beside the chat');
  return app.find(app.OT.AdminChecks).props;
}
const side = (app) => (app.find(app.OT.AdminChecks) || {}).props || null;
for (const [mode, want] of [['complete', ['done', undefined]], ['failed', ['failed', 'failed']], ['timeout', ['failed', 'timeout']], ['declined', ['declined', undefined]]]) {
  test(`checks beside the chat: Run checks → running → ${want.join(' / ')}`, async () => {
    const app = await startApp({ flag: true, admin: true });
    const p = await sideRequest(app);
    assert.equal(p.state, 'ask');
    assert.deepEqual(p.checks, ['Drive encryption status']);
    await app.inner.call('fixture.setChecksResult', { mode, hold: true });
    p.onRun();
    await app.until(() => side(app).state === 'running', 'running');
    const rq = app.calls.filter((c) => c.method === 'checks.approve').pop().params.requestId;
    await app.inner.call('fixture.finishChecks', { requestId: rq });
    await app.until(() => side(app).state === want[0], want[0]);
    assert.equal(side(app).cause, want[1]);
  });
}
test('checks beside the chat: cancelled because permissions changed; cancelled because the AI changed', async () => {
  const app = await startApp({ flag: true, admin: true });
  await sideRequest(app);
  const p = await app.inner.call('ai.getPermissions');
  const rec = JSON.parse(JSON.stringify(p.permissions)); rec.do.admin = false;
  await app.inner.call('ai.setPermissions', { idempotencyKey: key(), revision: p.permissions.revision, permissions: rec });
  await app.until(() => side(app).state === 'cancelled', 'cancelled');
  assert.equal(side(app).cause, 'permissions');
  const app2 = await startApp({ flag: true, admin: true });
  await sideRequest(app2);
  all(app2.tree).find((n) => n.type === app2.OT.ConnectionPill).props.onChange();
  await app2.settle();
  app2.find(app2.OT.LoginPanel).props.onConnect('chatgpt', 'ChatGPT');
  await app2.settle(400);
  if (app2.stage() === 'aiscan') { app2.find(app2.OT.AiScanPanel).props.onSkip(); await app2.settle(); }
  await app2.until(() => side(app2) && side(app2).state === 'cancelled', 'cancelled');
  assert.equal(side(app2).cause, 'ai');
});
test('checks beside the chat: Not now declines; a new question puts the ended request aside', async () => {
  const app = await startApp({ flag: true, admin: true });
  const p = await sideRequest(app);
  p.onDecline();
  await app.until(() => side(app).state === 'declined', 'declined');
  app.find(app.OT.PromptBox).props.onSubmit('Why is my PC slow?');
  await app.settle();
  assert.equal(side(app), null);
});

/* ---------- permissions ---------- */
test('permissions: the administrator row is disabled while not available (flag off), on the page and the pairing panel; enabled with the flag', async () => {
  const app = await startApp();
  app.find(app.OT.PermissionsSummary).props.onChange();
  await app.settle(200);
  assert.equal(app.find(app.OT.PermissionsPanel).props.adminUnavailable, true);
  const on = await startApp({ flag: true, admin: true });
  on.find(on.OT.PermissionsSummary) && assert.match(on.find(on.OT.PermissionsSummary).props.text, /can ask for administrator rights/);
  on.find(on.OT.PermissionsSummary).props.onChange();
  await on.settle(200);
  const pp = on.find(on.OT.PermissionsPanel).props;
  assert.equal(pp.adminUnavailable, false);
  assert.equal(pp.initial.admin, true);
  // Continue sends admin as the page has it.
  const mark = on.calls.length;
  pp.onContinue(Object.assign({}, pp.initial, { admin: false }));
  await on.settle(200);
  assert.equal(on.after(mark, 'ai.setPermissions')[0].params.permissions.do.admin, false);
});
