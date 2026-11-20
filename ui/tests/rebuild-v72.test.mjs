// node --test ui/tests/   — design Version 72 (PillAndStates): the seven ChatGPT states in the picker and the bottom
// button, the AI pill's panel (AIPillPanel) with Disconnect asking first in place, the finished run's × and its
// headlines (“First look at your PC” with no goal; a typed question as the headline, with no answer in the panel).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import fs from 'node:fs';
import vm from 'node:vm';
const require = createRequire(import.meta.url);
const FIX = require('../js/fix-plan.js');
const CS = require('../js/connection-state.js');

// A React stub with state: function components are called inline; hooks keep their values across render() calls
// (in call order, as React does), so a click followed by render() shows the next state.
const hooks = { slots: [], i: 0 };
function render(fn) { hooks.i = 0; return fn(); }
function loadComponents() {
  const React = {
    createElement(type, props, ...children) {
      props = Object.assign({}, props, { children: children.length <= 1 ? children[0] : children });
      return typeof type === 'function' ? type(props) : { type, props };
    },
    useState(v) {
      const i = hooks.i++;
      if (!(i in hooks.slots)) hooks.slots[i] = typeof v === 'function' ? v() : v;
      return [hooks.slots[i], (n) => { hooks.slots[i] = typeof n === 'function' ? n(hooks.slots[i]) : n; }];
    },
    useRef(v) {
      const i = hooks.i++;
      if (!(i in hooks.slots)) hooks.slots[i] = { current: v === undefined ? null : v };
      return hooks.slots[i];
    },
    useEffect() {}, useLayoutEffect() {}, useMemo: (f) => f(), useCallback: (f) => f, useId: () => 'id', Fragment: 'fragment'
  };
  const window = { React, OptTool: Object.assign({}, CS, { fixPlan: FIX }), matchMedia: () => ({ matches: false, addEventListener() {} }) };
  const ctx = vm.createContext({ window, document: { documentElement: {} }, navigator: {}, console, setTimeout, clearTimeout, requestAnimationFrame: () => 0, cancelAnimationFrame() {} });
  vm.runInContext(fs.readFileSync(new URL('../js/components.js', import.meta.url), 'utf8'), ctx);
  return window.OptTool;
}
const OT = loadComponents();
function fresh() { hooks.slots = []; hooks.i = 0; }
function nodes(n, out = []) {
  if (n == null || n === false || n === true) return out;
  if (Array.isArray(n)) { n.forEach((x) => nodes(x, out)); return out; }
  if (typeof n !== 'object') return out;
  out.push(n); nodes(n.props && n.props.children, out); return out;
}
function text(n) {
  if (n == null || n === false || n === true) return '';
  if (Array.isArray(n)) return n.map(text).join('');
  if (typeof n !== 'object') return String(n);
  return text(n.props && n.props.children);
}
const has = (n, c) => String((n.props && n.props.className) || '').split(/\s+/).includes(c);
const btns = (tree) => nodes(tree).filter((n) => n.type === 'button');
const buttons = (tree) => btns(tree).map((b) => text(b).trim());
const btn = (tree, label) => btns(tree).find((b) => text(b).trim() === label);
const noop = () => {};
const plan = (status, detail) => CS.connectionState({ kind: 'chatgpt_plan', entry: { kind: 'chatgpt_plan', status, detail } });

const CASES = [
  // [row source, signing, state, title, button, note]
  [plan('not_configured'), false, 'new', 'Sign in with ChatGPT', 'Sign in', 'Opens your browser.'],
  [plan('checking'), false, 'signingin', 'Sign in with ChatGPT', 'Signing in…', 'Finish signing in in your browser.'],
  [plan('error', 'callback_timeout'), false, 'failed', 'Sign-in didn’t finish', 'Try again', 'Opens your browser.'],
  [plan('error', 'auth'), false, 'signedout', 'Signed out of ChatGPT', 'Sign in', 'Sign in again to keep using it.'],
  [plan('error', 'quota'), false, 'limit', 'ChatGPT plan limit reached', null, 'Try again later, or choose another AI.'],
  [plan('error', 'not_eligible'), false, 'notavailable', 'Not available for this account', null, 'Choose another AI.'],
  [plan('connected'), false, 'signedin', 'Signed in with ChatGPT', null, null]
];

test('chatgptState: the seven states from the connection row', () => {
  assert.equal(CS.chatgptState(plan('connected')), 'signedin');
  assert.equal(CS.chatgptState(plan('connected', 'not_saved')), 'signedin');
  assert.equal(CS.chatgptState(plan('connected'), true), 'signedin');
  // Signing in (the browser is open, or the row is checking), whatever the row said before.
  assert.equal(CS.chatgptState(plan('checking')), 'signingin');
  for (const d of ['auth', 'quota', 'not_eligible', 'cancelled']) assert.equal(CS.chatgptState(plan('error', d), true), 'signingin', d);
  assert.equal(CS.chatgptState(plan('not_configured'), true), 'signingin');
  assert.equal(CS.chatgptState(plan('error', 'auth')), 'signedout');
  assert.equal(CS.chatgptState(plan('error', 'quota')), 'limit');
  assert.equal(CS.chatgptState(plan('error', 'not_eligible')), 'notavailable');
  // Any other cause: one “Sign-in didn’t finish”.
  for (const d of ['callback_timeout', 'cancelled', 'storage', 'unavailable', 'network']) assert.equal(CS.chatgptState(plan('error', d)), 'failed', d);
  assert.equal(CS.chatgptState(plan('not_configured')), 'new');
  assert.equal(CS.chatgptState(CS.connectionState({ kind: 'chatgpt_plan', entry: null })), 'new');
  assert.equal(CS.chatgptState(null), 'new');
});

// v73: limit and notavailable have no bottom button, signing in waits for the browser (rebuild-v73.test.mjs).
test('the bottom button: Continue only when signed in; Connect otherwise', () => {
  const want = { new: ['Connect', false], failed: ['Connect', false], signedout: ['Connect', false], signedin: ['Continue', false] };
  for (const [st, , state] of CASES) {
    if (!want[state]) continue;
    assert.equal(CS.connectLabel('chatgpt', st), want[state][0], state);
    fresh();
    const tree = render(() => OT.LoginPanel({ initialProvider: 'chatgpt', tileStates: { chatgpt_plan: st }, clientViews: {} }));
    const last = btns(tree).pop();
    assert.equal(text(last).trim(), want[state][0], state);
    assert.equal(!!last.props.disabled, want[state][1], state);
  }
});

test('the ChatGPT line: one row and its note per state; no reason text from the app', () => {
  for (const [st, , state, title, action, note] of CASES) {
    const signing = state === 'signingin';
    fresh();
    const tree = render(() => OT.ProviderDetails({ provider: { value: 'chatgpt' }, tileState: st, connect: signing ? { tile: 'chatgpt' } : null, onSignIn: noop }));
    const li = nodes(tree).filter((n) => n.type === 'li');
    assert.equal(li.length, 1, state);
    assert.equal(text(nodes(li[0]).find((n) => has(n, 'ot-connect-title')).props.children[0]), title, state);
    assert.deepEqual(buttons(tree), action ? [action] : [], state);
    if (signing) {
      const b = btns(tree)[0];
      assert.equal(b.props.disabled, true);
      assert.ok(nodes(b).some((n) => n.type === 'svg' || has(n, 'ot-logo')), 'turning gears');
    } else if (action) assert.ok(!btns(tree)[0].props.disabled, state);
    const status = nodes(tree).filter((n) => n.props && n.props.role === 'status');
    if (note) { assert.equal(status.length, 1, state); assert.equal(text(status[0]), note); }
    else assert.equal(status.length, 0, state);
    assert.equal(has(li[0], 'is-warn'), ['failed', 'signedout', 'limit', 'notavailable'].includes(state), state);
    // The connection's own hint (e.g. “This ChatGPT account can’t be used here…”) is not shown.
    if (st.hint) assert.ok(!text(tree).includes(st.hint), state);
  }
  // notavailable never offers Try again, even when signing in failed before.
  fresh();
  assert.ok(!buttons(render(() => OT.ProviderDetails({ provider: { value: 'chatgpt' }, tileState: plan('error', 'not_eligible'), onSignIn: noop }))).includes('Try again'));
});

test('AIPillPanel: head with the dot, name and status; Change, Permissions, then Disconnect pushed right', () => {
  fresh();
  const t = render(() => OT.AIPillPanel({ provider: 'Claude', onChange: noop, onPermissions: noop, onDisconnect: noop }));
  assert.ok(has(t, 'ot-pillpanel'));
  assert.equal(t.props.role, 'dialog'); assert.equal(t.props['aria-label'], 'Claude');
  const head = nodes(t).find((n) => has(n, 'ot-pillpanel-head'));
  assert.ok(nodes(head).some((n) => has(n, 'ot-pill-dot')));
  assert.equal(text(head), 'ClaudeConnected');
  assert.deepEqual(buttons(t), ['Change', 'Permissions', 'Disconnect']);
  assert.ok(has(btn(t, 'Disconnect'), 'ot-pillpanel-off'));   // v73: a small text button on its own row
  assert.ok(has(btn(t, 'Change'), 'ot-btn-neu') && has(btn(t, 'Permissions'), 'ot-btn-neu'));
  // The design's start on the confirm (demo card).
  fresh();
  const c = render(() => OT.AIPillPanel({ provider: 'Claude', confirming: true, onDisconnect: noop }));
  assert.ok(text(c).includes('Sign Claude out of Optimus? You can connect it again any time.'));
  assert.deepEqual(buttons(c), ['Cancel', 'Disconnect']);
});

test('Disconnect asks first: Cancel leaves it connected; Disconnect calls provider.disconnect exactly once', async () => {
  // The app's panel (ConnectionDetail → AIPillPanel), wired as app-flow wires it: onDisconnect → provider.disconnect.
  const calls = [];
  const backend = { call: (m, a) => { calls.push([m, a]); return new Promise(() => {}); } };
  const props = { state: CS.connectionState({ kind: 'claude_code_mcp', entry: { kind: 'claude_code_mcp', status: 'connected' } }),
    onChange: noop, onPermissions: noop, onDisconnect: () => backend.call('provider.disconnect', { kind: 'claude_code_mcp' }) };
  fresh();
  const draw = () => render(() => OT.ConnectionDetail(props));
  let t = draw();
  btn(t, 'Disconnect').props.onClick();
  t = draw();
  assert.ok(text(t).includes('Sign Claude out of Optimus? You can connect it again any time.'));
  assert.equal(nodes(t).find((n) => has(n, 'ot-pillpanel-confirm')).props.role, 'alert');
  assert.deepEqual(buttons(t), ['Cancel', 'Disconnect']);
  assert.equal(calls.length, 0);
  // Cancel: back to the row, still connected, nothing sent.
  btn(t, 'Cancel').props.onClick();
  t = draw();
  assert.deepEqual(buttons(t), ['Change', 'Permissions', 'Disconnect']);
  assert.ok(text(t).includes('Connected'));
  assert.equal(calls.length, 0);
  // Ask again and confirm (twice, fast): one call.
  btn(t, 'Disconnect').props.onClick();
  t = draw();
  const go = btn(t, 'Disconnect');
  go.props.onClick(); go.props.onClick();
  t = draw();
  btn(t, 'Disconnect').props.onClick();
  assert.deepEqual(calls, [['provider.disconnect', { kind: 'claude_code_mcp' }]]);
  assert.equal(btn(t, 'Disconnect').props.disabled, true);
  // A failed disconnect gives the button back (the app says “Not saved”).
  let fail;
  const p2 = Object.assign({}, props, { onDisconnect: () => new Promise((res, rej) => { fail = rej; }) });
  fresh();
  t = render(() => OT.ConnectionDetail(p2));
  btn(t, 'Disconnect').props.onClick();
  t = render(() => OT.ConnectionDetail(p2));
  btn(t, 'Disconnect').props.onClick();
  fail(new Error('x')); await new Promise((r) => setTimeout(r, 0));
  t = render(() => OT.ConnectionDetail(p2));
  assert.ok(!btn(t, 'Disconnect').props.disabled);
});

// v73: each state's buttons come from the design's state table (rebuild-v73.test.mjs); a state the design has none for
// keeps its own word, hint and action.
test('the pill panel keeps a state’s own word, hint and action where the design has none', () => {
  const panel = CS.pillPanelState({ kind: 'chatgpt_plan', entry: { kind: 'chatgpt_plan', status: 'error', detail: 'not_eligible' } });
  const s2 = plan('error', 'not_eligible');
  fresh();
  const t2 = render(() => OT.ConnectionDetail({ panel, label: s2.label, onChange: noop, onAction: noop }));
  assert.deepEqual(buttons(t2), ['Change']);
  assert.ok(text(t2).includes(s2.hint) && text(t2).includes('Not available'));
});

const STEPS = [{ origin: 'client', tool: 'read_report', title: 'Read the report', state: 'done' },
  { origin: 'optimizer', tool: 'startup.list', title: 'Checked startup apps', state: 'done' }];
const ITEMS = [{ id: 'a', title: 'Read the report', tool: 'get_findings', origin: 'client', originLabel: 'Claude', status: 'complete' },
  { id: 'b', title: 'Checked startup apps', tool: 'startup.list', origin: 'in_app', originLabel: 'Optimizer', status: 'complete' }];
const closeX = (t) => nodes(t).filter((n) => n.type === 'button' && n.props['aria-label'] === 'Close');

test('finished run: the × at the right of the AI-name line; no goal → “First look at your PC”', () => {
  fresh();
  const d = render(() => OT.AgentActivity({ finished: true, agent: 'Claude', onClose: noop, answer: 'Your PC is in good shape.', steps: STEPS }));
  const head = nodes(d).find((n) => has(n, 'ot-answer-head'));
  assert.equal(text(nodes(head).find((n) => has(n, 'ot-answer-from'))), 'Claude');
  const x = closeX(head);
  assert.equal(x.length, 1); assert.ok(has(x[0], 'ot-rp-x')); assert.equal(text(x[0]), '');
  assert.equal(head.props.children[1], x[0]);                      // last in the line: at its right
  assert.equal(text(nodes(d).find((n) => n.type === 'h2')), 'First look at your PC');
  // The app's Activity panel and first look: the same head; no “Close” text button.
  fresh();
  const a = render(() => OT.ActivityPanel({ title: 'Claude', items: ITEMS, finished: true, agent: 'Claude', answer: OT.runAnswer('Three things.', [], noop), onClose: noop }));
  assert.equal(closeX(nodes(a).find((n) => has(n, 'ot-answer-head'))).length, 1);
  assert.ok(!buttons(a).includes('Close'));
  assert.equal(nodes(a).filter((n) => has(n, 'ot-answer-fromrow')).length, 0);
  assert.equal(text(nodes(a).find((n) => n.type === 'h2')), 'First look at your PC');
  fresh();
  const s = render(() => OT.AiScanPanel({ title: 'Claude', items: ITEMS, done: true, answer: { id: 's', text: 'Three things.', cites: [] }, onContinue: noop, onSkip: noop }));
  assert.equal(text(nodes(s).find((n) => n.type === 'h2')), 'First look at your PC');
  // Without onClose, no ×.
  fresh();
  assert.equal(closeX(render(() => OT.AgentActivity({ finished: true, agent: 'Claude', answer: 'x', steps: STEPS }))).length, 0);
});

test('a typed question: no answer in the Activity panel; the question is its headline, the steps folded', () => {
  fresh();
  const t = render(() => OT.ActivityPanel({ title: 'Claude', items: ITEMS, finished: true, agent: 'Claude', goal: 'Why is my PC slow?', answer: null, onClose: noop }));
  assert.ok(has(t, 'is-finished'));
  assert.equal(text(nodes(t).find((n) => n.type === 'h2')), 'Why is my PC slow?');
  assert.equal(nodes(t).filter((n) => n.type === 'p').length, 0);
  assert.equal(text(nodes(t).find((n) => has(n, 'ot-steps-toggle'))), 'Steps Claude took2');
  assert.equal(nodes(t).filter((n) => has(n, 'ot-steps')).length, 0);
  assert.equal(closeX(t).length, 1);
  // app-flow passes the answer only for the first look (a scan answer), never for a typed question.
  const flow = fs.readFileSync(new URL('../js/app-flow.js', import.meta.url), 'utf8');
  assert.match(flow, /answer: lastDone && lastDone\.scan && !runInProgress \? OT\.runAnswer\(/);
  assert.match(flow, /goal: lastDone && !lastDone\.scan \? lastDone\.q : null/);
});
