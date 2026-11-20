// node --test ui/tests/   — design Version 73 (PillPanelStates): the AI pill panel's twelve states (AIPillPanel state),
// the mapping from every real connection state (contracts m3-agent.md §9, m5-chat.md §1) onto them (pillPanelState),
// and the picker's bottom button for ChatGPT (“Waiting for browser…”; no button for plan limit or not available).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import fs from 'node:fs';
import vm from 'node:vm';
const require = createRequire(import.meta.url);
const FIX = require('../js/fix-plan.js');
const CS = require('../js/connection-state.js');

// A React stub with state (as in rebuild-v72): function components are called inline; hooks keep their values across
// render() calls in call order.
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

/* ---------- pillPanelState: one test per row of the contracts' tables ---------- */
const api = (status, detail) => ({ kind: 'anthropic_api', entry: { kind: 'anthropic_api', status, detail, label: 'Claude (API)' } });
const gpt = (status, detail) => ({ kind: 'chatgpt_plan', entry: { kind: 'chatgpt_plan', status, detail } });
const cc = (status, detail, code) => ({ kind: 'claude_code_mcp', entry: { kind: 'claude_code_mcp', status, detail, code } });
const ACTIVE_TASK = { taskId: 't1', status: 'active', recipient: { kind: 'claude_code_mcp' } };
const OWN = (base, detail, hint, primary, tone) => ({ state: base, own: true, detail, hint, primary, tone });

const ROWS = [
  // [contract row, input, working, expected]
  ['local / later', { kind: null, local: true }, false, { state: 'local' }],
  ['API not_configured', api('not_configured'), false, { state: 'nokey' }],
  ['API checking', api('checking'), false, OWN('connected', 'Checking…', null, null, 'muted')],
  ['API connected', api('connected'), false, { state: 'connected' }],
  ['API error auth', api('error', 'auth'), false, { state: 'badkey' }],
  ['API error quota', api('error', 'quota'), false, OWN('connected', 'No credit', 'The account has no credit left.', 'Update key', 'warn')],
  ['API error network', api('error', 'network'), false, { state: 'offline' }],
  ['API error unavailable', api('error', 'unavailable'), false, { state: 'busy' }],
  ['API error not_eligible', api('error', 'not_eligible'), false, OWN('connected', 'Not available', 'This account can’t use it here.', 'Update key', 'warn')],
  ['API error storage', api('error', 'storage'), false, OWN('connected', 'Not saved', 'This PC didn’t let the app save the key. Try again.', 'Add key', 'warn')],
  ['API connected not_saved', api('connected', 'not_saved'), false, OWN('connected', 'Connected', 'Works now, but this PC didn’t save the key. You’ll add it again next time.', null, 'ok')],
  ['ChatGPT not_configured', gpt('not_configured'), false, { state: 'signedout' }],
  ['ChatGPT checking (signing in)', gpt('checking'), false, OWN('connected', 'Checking…', null, null, 'muted')],
  ['ChatGPT connected', gpt('connected'), false, { state: 'connected' }],
  ['ChatGPT error auth', gpt('error', 'auth'), false, { state: 'signedout' }],
  ['ChatGPT error quota', gpt('error', 'quota'), false, OWN('connected', 'Limit reached', 'Your ChatGPT plan’s limit is used up for now.', 'Retry', 'warn')],
  ['ChatGPT error network', gpt('error', 'network'), false, { state: 'offline' }],
  ['ChatGPT error unavailable', gpt('error', 'unavailable'), false, { state: 'busy' }],
  ['ChatGPT error not_eligible', gpt('error', 'not_eligible'), false, OWN('connected', 'Not available', 'This ChatGPT account can’t be used here. Try another account or pick another AI.', null, 'warn')],
  ['ChatGPT error storage', gpt('error', 'storage'), false, OWN('connected', 'Not saved', 'Signed in, but this PC didn’t let the app save it. Try again.', 'Sign in', 'warn')],
  ['ChatGPT error callback_timeout', gpt('error', 'callback_timeout'), false, OWN('connected', 'Not finished', 'The browser didn’t come back to the app. Try again, or pick another AI.', 'Sign in', 'warn')],
  ['ChatGPT error cancelled', gpt('error', 'cancelled'), false, { state: 'signedout' }],
  ['MCP waiting never_seen', cc('waiting', 'never_seen'), false, { state: 'setup' }],
  ['MCP waiting idle', cc('waiting', 'idle'), false, { state: 'manual' }],
  ['MCP pending + code', cc('pending', null, '2760'), false, { state: 'pairing', code: '2760' }],
  ['MCP paired', cc('paired'), false, { state: 'paired' }],
  ['MCP active', cc('active'), false, { state: 'live' }],
  ['MCP client_closed (task kept)', Object.assign(cc('client_closed'), { task: ACTIVE_TASK }), false, OWN('connected', 'Closed', 'Ask Claude Code again', 'Activity', 'warn')],
  ['MCP client_closed (no task)', cc('client_closed'), false, OWN('connected', 'Closed', 'Ask Claude Code again', null, 'warn')],
  ['MCP error bridge_rejected', cc('error', 'bridge_rejected'), false, OWN('manual', 'Setup issue', undefined, undefined, 'warn')],
  ['MCP error pairing_expired', cc('error', 'pairing_expired'), false, { state: 'manual' }],
  ['MCP error denied', cc('error', 'denied'), false, { state: 'manual' }],
  ['MCP connected (m5)', cc('connected'), false, { state: 'connected' }],
  ['MCP error signed_out (m5)', cc('error', 'signed_out'), false, { state: 'signedout' }],
  ['MCP error unavailable: outdated (m5 §2)', cc('error', 'unavailable'), false, OWN('connected', 'Can’t run', null, 'Set up', 'warn')],
  ['MCP error unavailable: blocked (m5 §2)', cc('error', 'unavailable'), false, OWN('connected', 'Can’t run', null, 'Set up', 'warn')],
  ['any: backend connection lost', Object.assign(api('connected'), { backend: 'reconnecting' }), false, OWN('connected', 'Reconnecting…', null, null, 'ok')],
  ['any: no entry yet', { kind: 'anthropic_api', entry: null, label: 'Claude (API)' }, false, OWN('connected', 'Checking…', null, null, 'muted')],
  ['a run going (headless Claude, in-app API)', cc('connected'), true, { state: 'live' }],
  ['a run going, but reconnecting', Object.assign(api('connected'), { backend: 'reconnecting' }), true, OWN('connected', 'Reconnecting…', null, null, 'ok')]
];
for (const [row, input, working, want] of ROWS) {
  test('pillPanelState: ' + row, () => {
    assert.deepEqual(CS.pillPanelState(input, working), want);
  });
}
test('pillPanelState: every result is one of the design’s twelve states', () => {
  const twelve = ['connected', 'live', 'signedout', 'setup', 'nokey', 'badkey', 'offline', 'busy', 'pairing', 'paired', 'manual', 'local'];
  for (const [row, input, working] of ROWS) assert.ok(twelve.includes(CS.pillPanelState(input, working).state), row);
});

/* ---------- AIPillPanel per state ---------- */
const CMD = 'claude mcp add optimus -- "C:\\Program Files\\Optimus\\optimus-mcp.exe"';
const STATES = [
  // [state, status word, hint, buttons, dot tone]
  ['connected', 'Connected', null, ['Change', 'Permissions', 'Disconnect'], 'ok'],
  ['live', 'Working…', null, ['Permissions', 'Stop', 'Activity'], 'live'],
  ['signedout', 'Signed out', null, ['Change', 'Permissions', 'Sign in', 'Disconnect'], 'warn'],
  ['setup', 'Not set up', 'Claude Code isn’t installed or can’t start on this PC.', ['Change', 'Permissions', 'Set up', 'Disconnect'], 'warn'],
  ['nokey', 'Needs a key', null, ['Change', 'Permissions', 'Add key', 'Disconnect'], 'warn'],
  ['badkey', 'Key rejected', 'The key was turned down. Check it and add it again.', ['Change', 'Permissions', 'Update key', 'Disconnect'], 'warn'],
  ['offline', 'Can’t reach it', 'Check your internet connection.', ['Change', 'Permissions', 'Retry', 'Disconnect'], 'warn'],
  ['busy', 'Busy right now', 'Try again in a minute.', ['Change', 'Permissions', 'Retry', 'Disconnect'], 'warn'],
  ['pairing', 'Wants to pair', 'Check this code matches the one in Claude Code’s window.', ['Change', 'Permissions', 'Deny', 'Approve', 'Disconnect'], 'live'],
  ['paired', 'Paired', 'Claude Code can now work in its own window.', ['Change', 'Permissions', 'Start', 'Disconnect'], 'ok'],
  ['manual', 'Not added yet', 'Run this in a terminal to add Optimus to Claude Code:', ['Copy', 'Change', 'Permissions', 'Check again', 'Disconnect'], 'warn'],
  ['local', 'No AI connected', null, ['Connect an AI'], 'muted']
];
const panelProps = (state, extra) => Object.assign({ state, provider: 'Claude', code: '2760', command: CMD, onChange: noop, onPermissions: noop, onDisconnect: noop, onAction: noop }, extra || {});
for (const [state, word, hint, want, tone] of STATES) {
  test('AIPillPanel ' + state + ': head, hint, code / command, buttons, Disconnect', () => {
    fresh();
    const t = render(() => OT.AIPillPanel(panelProps(state)));
    const name = state === 'local' ? 'Local only' : 'Claude';
    assert.equal(t.props['aria-label'], name);
    const head = nodes(t).find((n) => has(n, 'ot-pillpanel-head'));
    assert.equal(text(head), name + word);
    assert.ok(has(nodes(head).find((n) => has(n, 'ot-pill-dot')), 'is-' + tone));
    const hints = nodes(t).filter((n) => has(n, 'ot-pillpanel-hint'));
    if (state === 'manual') assert.deepEqual(hints.map(text), [hint, 'Then run claude mcp list to check.']);
    else assert.deepEqual(hints.map(text), hint ? [hint] : []);
    // The code only for pairing (with its “Code” label), the command field only for manual (shown at once).
    const code = nodes(t).find((n) => has(n, 'ot-pair-code'));
    assert.equal(!!code, state === 'pairing');
    if (code) { assert.ok(has(code, 'is-small')); assert.equal(text(code), 'Code2760'); }
    const cmd = nodes(t).find((n) => has(n, 'ot-pillpanel-cmd'));
    assert.equal(!!cmd, state === 'manual');
    if (cmd) assert.equal(text(nodes(cmd).find((n) => n.type === 'code')), CMD);
    assert.deepEqual(buttons(t), want);
    // The state's one primary sits at the right; Disconnect is a small text button on its own row.
    const prim = btns(t).filter((b) => has(b, 'ot-btn-primary'));
    assert.equal(prim.length, state === 'connected' ? 0 : 1);
    if (prim.length) { assert.ok(has(prim[0], 'ot-pillpanel-primary')); assert.equal(text(prim[0]).trim(), want.filter((x) => x !== 'Disconnect').pop()); }
    const off = btn(t, 'Disconnect');
    if (off) { assert.ok(has(off, 'ot-pillpanel-off')); assert.ok(!nodes(nodes(t).find((n) => has(n, 'ot-pillpanel-actions'))).includes(off)); }
    // busy (a scan, a run): no Disconnect, the rest stays.
    fresh();
    const b = render(() => OT.AIPillPanel(panelProps(state, { busy: true })));
    assert.deepEqual(buttons(b), want.filter((x) => x !== 'Disconnect'));
  });
}
test('AIPillPanel: onAction gets the button’s name; Connect an AI opens the picker (onChange)', () => {
  const got = [];
  for (const [state] of STATES) {
    fresh();
    const t = render(() => OT.AIPillPanel(panelProps(state, { onAction: (n) => got.push(n), onChange: () => got.push('picker') })));
    btns(t).filter((b) => !['Change', 'Permissions', 'Disconnect', 'Copy'].includes(text(b).trim())).forEach((b) => b.props.onClick());
  }
  assert.deepEqual(got, ['Stop', 'Activity', 'Sign in', 'Set up', 'Add key', 'Update key', 'Retry', 'Retry', 'Deny', 'Approve', 'Start', 'Check again', 'picker']);
});
test('AIPillPanel manual: Copy turns to Copied once the command is on the clipboard', async () => {
  let copied = 0;
  fresh();
  let t = render(() => OT.AIPillPanel(panelProps('manual', { onCopy: () => { copied++; return Promise.resolve(); } })));
  btn(t, 'Copy').props.onClick();
  await new Promise((r) => setTimeout(r, 0));
  t = render(() => OT.AIPillPanel(panelProps('manual')));
  assert.equal(copied, 1);
  assert.ok(btn(t, 'Copied'));
  // A failed copy stays on Copy.
  fresh();
  t = render(() => OT.AIPillPanel(panelProps('manual', { onCopy: () => Promise.reject(new Error('x')) })));
  btn(t, 'Copy').props.onClick();
  await new Promise((r) => setTimeout(r, 0));
  t = render(() => OT.AIPillPanel(panelProps('manual')));
  assert.ok(btn(t, 'Copy'));
});
test('AIPillPanel: a state the design has none for keeps its own word, hint and action in the layout', () => {
  fresh();
  const t = render(() => OT.AIPillPanel(panelProps('connected', { detail: 'No credit', hint: 'The account has no credit left.', primary: 'Update key', tone: 'warn' })));
  assert.equal(text(nodes(t).find((n) => has(n, 'ot-pillpanel-head'))), 'ClaudeNo credit');
  assert.ok(text(t).includes('The account has no credit left.'));
  assert.deepEqual(buttons(t), ['Change', 'Permissions', 'Update key', 'Disconnect']);
  // primary null: no primary and no extra (Reconnecting…, ChatGPT not available).
  fresh();
  const r = render(() => OT.AIPillPanel(panelProps('pairing', { detail: 'Reconnecting…', primary: null })));
  assert.deepEqual(buttons(r), ['Change', 'Permissions', 'Disconnect']);
  // hint undefined keeps the design's (bridge_rejected in manual's layout).
  fresh();
  const m = render(() => OT.AIPillPanel(panelProps('manual', { detail: 'Setup issue', hint: undefined, primary: undefined, tone: 'warn' })));
  assert.ok(text(m).includes('Run this in a terminal to add Optimus to Claude Code:'));
  assert.ok(btn(m, 'Check again'));
});

/* ---------- The confirm flow, through the app's adapter ---------- */
test('Disconnect asks first in the v73 layout: Cancel leaves it; Disconnect is sent once', () => {
  const calls = [];
  const props = { panel: CS.pillPanelState(cc('connected')), label: 'Claude', onChange: noop, onPermissions: noop, onAction: noop,
    onDisconnect: () => { calls.push('provider.disconnect'); return new Promise(() => {}); } };
  fresh();
  const draw = () => render(() => OT.ConnectionDetail(props));
  let t = draw();
  btn(t, 'Disconnect').props.onClick();
  t = draw();
  assert.ok(text(t).includes('Sign Claude out of Optimus? You can connect it again any time.'));
  assert.deepEqual(buttons(t), ['Cancel', 'Disconnect']);
  btn(t, 'Cancel').props.onClick();
  t = draw();
  assert.deepEqual(buttons(t), ['Change', 'Permissions', 'Disconnect']);
  assert.equal(calls.length, 0);
  btn(t, 'Disconnect').props.onClick();
  t = draw();
  btn(t, 'Disconnect').props.onClick(); btn(t, 'Disconnect').props.onClick();
  assert.deepEqual(calls, ['provider.disconnect']);
  // Busy: no Disconnect at all.
  fresh();
  assert.ok(!buttons(render(() => OT.ConnectionDetail(Object.assign({}, props, { busy: true })))).includes('Disconnect'));
});

/* ---------- The picker's bottom row for ChatGPT ---------- */
const plan = (status, detail) => CS.connectionState({ kind: 'chatgpt_plan', entry: { kind: 'chatgpt_plan', status, detail } });
const key = (status, detail) => CS.connectionState({ kind: 'anthropic_api', entry: { kind: 'anthropic_api', status, detail } });
const login = (prov, states, extra) => { fresh(); return render(() => OT.LoginPanel(Object.assign({ initialProvider: prov, tileStates: states, clientViews: {}, onBack: noop }, extra || {}))); };
const bottom = (t) => nodes(t).find((n) => has(n, 'ot-login-actions'));
test('picker: ChatGPT signing in → “Waiting for browser…”, disabled, with turning gears', () => {
  for (const t of [login('chatgpt', { chatgpt_plan: plan('checking') }),
    login('chatgpt', { chatgpt_plan: plan('error', 'auth') }, { connect: { tile: 'chatgpt', kind: 'chatgpt_plan', phase: 'key' } })]) {
    assert.deepEqual(buttons(bottom(t)), ['Back', 'Later', 'Waiting for browser…']);
    const b = btn(bottom(t), 'Waiting for browser…');
    assert.equal(b.props.disabled, true);
    assert.ok(has(b, 'ot-btn-primary'));
    assert.ok(nodes(b).some((n) => has(n, 'ot-logo') && has(n, 'is-spinning') && has(n, 'is-mono')), 'turning gears');
    assert.ok(!text(t).includes('Checking…') && !text(t).includes('Connecting…'));
  }
});
test('picker: ChatGPT plan limit / not available → no primary button, only Back and Later', () => {
  for (const st of [plan('error', 'quota'), plan('error', 'not_eligible')]) {
    assert.deepEqual(buttons(bottom(login('chatgpt', { chatgpt_plan: st }))), ['Back', 'Later']);
    assert.deepEqual(buttons(bottom(login('chatgpt', { chatgpt_plan: st }, { onBack: null }))), ['Later']);
  }
  assert.equal(CS.pickerPrimary('chatgpt', plan('error', 'quota')), 'none');
  assert.equal(CS.pickerPrimary('chatgpt', plan('error', 'not_eligible')), 'none');
  assert.equal(CS.pickerPrimary('chatgpt', plan('checking')), 'waiting');
  assert.equal(CS.pickerPrimary('chatgpt', plan('error', 'auth')), null);
  assert.equal(CS.pickerPrimary('api-key', key('error', 'quota')), null);
  assert.equal(CS.connectBlocked, undefined);
});
test('picker: the API key keeps Connecting… (its dialog open) and Checking… (the key checked)', () => {
  let b = bottom(login('api-key', { anthropic_api: key('error', 'auth') }, { connect: { tile: 'api-key', kind: 'anthropic_api', phase: 'key' } }));
  assert.equal(buttons(b).pop(), 'Connecting…');
  b = bottom(login('api-key', { anthropic_api: key('checking') }));
  assert.equal(buttons(b).pop(), 'Checking…');
});

/* ---------- app-flow wiring ---------- */
test('app-flow: the pill panel comes from pillPanelState; Local only opens it; busy during a scan or a run', () => {
  const flow = fs.readFileSync(new URL('../js/app-flow.js', import.meta.url), 'utf8');
  assert.match(flow, /panel: OT\.pillPanelState\(\{ kind: pillKind, local: !pillKind,/);
  assert.match(flow, /busy: id === 'scanning' \|\| working,/);
  assert.match(flow, /onOpen: function \(\) \{ setDetailOpen\(!detailOpen\); \}, open: detailOpen,/);
  assert.match(flow, /signedIn && detailOpen && h\('div', \{ className: 'ot-conn-wrap' \}/);
  assert.ok(!/rowActions/.test(flow));
});
