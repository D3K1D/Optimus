// node --test ui/tests/   — design Version 73, updated to v86 (PillPanelStates): the AI pill panel's states (AIPillPanel state),
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

/* ---------- pillPanelState: one test per row of the contracts' tables (design v86: every row has a designed state) ---------- */
const api = (status, detail) => ({ kind: 'anthropic_api', entry: { kind: 'anthropic_api', status, detail, label: 'Claude (API)' } });
const gpt = (status, detail) => ({ kind: 'chatgpt_plan', entry: { kind: 'chatgpt_plan', status, detail } });
const cc = (status, detail, code) => ({ kind: 'claude_code_mcp', entry: { kind: 'claude_code_mcp', status, detail, code } });
const ACTIVE_TASK = { taskId: 't1', status: 'active', recipient: { kind: 'claude_code_mcp' } };
const A = (state, x) => Object.assign({ state, kind: 'api' }, x || {});
const G = (state, x) => Object.assign({ state, kind: 'chatgpt' }, x || {});
const C = (state, x) => Object.assign({ state, kind: 'claude' }, x || {});

const ROWS = [
  // [contract row, input, working, expected]
  ['local / later', { kind: null, local: true }, false, { state: 'local' }],
  ['local while reconnecting', { kind: null, local: true, backend: 'reconnecting' }, false, { state: 'local' }],
  ['API not_configured', api('not_configured'), false, A('nokey')],
  ['API checking', api('checking'), false, A('checking')],
  ['API connected', api('connected'), false, A('connected')],
  ['API error auth', api('error', 'auth'), false, A('badkey')],
  ['API error quota', api('error', 'quota'), false, A('nocredit')],
  ['API error network', api('error', 'network'), false, A('offline')],
  ['API error unavailable', api('error', 'unavailable'), false, A('busy')],
  ['API error not_eligible', api('error', 'not_eligible'), false, A('noteligible')],
  ['API error storage', api('error', 'storage'), false, A('notsaved')],
  ['API connected not_saved', api('connected', 'not_saved'), false, A('connected', { notSaved: true })],
  ['OpenAI API error quota', { kind: 'openai_api', entry: { kind: 'openai_api', status: 'error', detail: 'quota' } }, false, A('nocredit')],
  ['ChatGPT not_configured', gpt('not_configured'), false, G('signin')],
  ['ChatGPT checking (signing in)', gpt('checking'), false, G('checking')],
  ['ChatGPT connected', gpt('connected'), false, G('connected')],
  ['ChatGPT connected not_saved', gpt('connected', 'not_saved'), false, G('connected', { notSaved: true })],
  ['ChatGPT error auth', gpt('error', 'auth'), false, G('signedout')],
  ['ChatGPT error quota', gpt('error', 'quota'), false, G('limit')],
  ['ChatGPT error network', gpt('error', 'network'), false, G('offline')],
  ['ChatGPT error unavailable', gpt('error', 'unavailable'), false, G('busy')],
  ['ChatGPT error not_eligible', gpt('error', 'not_eligible'), false, G('noteligible')],
  ['ChatGPT error storage', gpt('error', 'storage'), false, G('notsaved')],
  ['ChatGPT error callback_timeout', gpt('error', 'callback_timeout'), false, G('notfinished')],
  ['ChatGPT error cancelled', gpt('error', 'cancelled'), false, G('signin')],
  ['MCP waiting never_seen', cc('waiting', 'never_seen'), false, C('setup')],
  ['MCP waiting idle', cc('waiting', 'idle'), false, C('manual')],
  ['MCP pending + code', cc('pending', null, '2760'), false, C('pairing', { code: '2760' })],
  ['MCP paired', cc('paired'), false, C('paired')],
  ['MCP active', cc('active'), false, C('live')],
  ['MCP client_closed (task kept)', Object.assign(cc('client_closed'), { task: ACTIVE_TASK }), false, C('closed', { activity: true })],
  ['MCP client_closed (no task)', cc('client_closed'), false, C('closed', { activity: false })],
  ['MCP error bridge_rejected', cc('error', 'bridge_rejected'), false, C('rejected')],
  ['MCP error pairing_expired', cc('error', 'pairing_expired'), false, C('manual')],
  ['MCP error denied', cc('error', 'denied'), false, C('manual')],
  ['MCP connected (m5)', cc('connected'), false, C('connected')],
  ['MCP error signed_out (m5)', cc('error', 'signed_out'), false, C('signedout')],
  ['MCP error unavailable: outdated (m5 §2)', cc('error', 'unavailable'), false, C('cantrun')],
  ['any: backend connection lost', Object.assign(api('connected'), { backend: 'reconnecting' }), false, A('reconnecting')],
  ['any: no entry yet', { kind: 'anthropic_api', entry: null, label: 'Claude (API)' }, false, A('checking')],
  ['any: no entry yet (Claude)', { kind: 'claude_code_mcp', entry: null }, false, C('checking')],
  ['a run going (headless Claude, in-app API)', cc('connected'), true, C('live')],
  ['a run going, but reconnecting', Object.assign(api('connected'), { backend: 'reconnecting' }), true, A('reconnecting')],
  // Outside the contracts: kept where they were.
  // design v87: an API / ChatGPT error with another reason, or none: unknown (Not working); network stays offline.
  ['API error with an unknown detail', api('error', 'weird'), false, A('unknown')],
  ['API error with no detail', api('error'), false, A('unknown')],
  ['ChatGPT error with an unknown detail', gpt('error', 'weird'), false, G('unknown')],
  ['ChatGPT error with no detail', gpt('error'), false, G('unknown')],
  ['MCP error with an unknown detail', cc('error', 'weird'), false, C('manual')]
];
for (const [row, input, working, want] of ROWS) {
  test('pillPanelState: ' + row, () => {
    assert.deepEqual(CS.pillPanelState(input, working), want);
  });
}
const DESIGN_STATES = ['connected', 'live', 'checking', 'reconnecting', 'signin', 'signedout', 'notfinished', 'notsaved', 'limit', 'nocredit', 'noteligible',
  'setup', 'cantrun', 'nokey', 'badkey', 'offline', 'busy', 'unknown', 'pairing', 'paired', 'manual', 'rejected', 'closed', 'local'];
test('pillPanelState: every result is one of the design’s states, and every design state is reached', () => {
  const seen = new Set();
  for (const [row, input, working] of ROWS) { const s = CS.pillPanelState(input, working).state; assert.ok(DESIGN_STATES.includes(s), row); seen.add(s); }
  assert.deepEqual(DESIGN_STATES.filter((s) => !seen.has(s)), []);
});

/* ---------- AIPillPanel per state (design v86 table) ---------- */
const CMD = 'claude mcp add optimus -- "C:\\Program Files\\Optimus\\optimus-mcp.exe"';
const AFTER = 'Then run claude mcp list to check.';
const STATES = [
  // [state, status word, hint, buttons, dot tone]
  ['connected', 'Connected', null, ['Change', 'Permissions', 'Disconnect'], 'ok'],
  ['live', 'Working…', null, ['Permissions', 'Stop', 'Activity'], 'live'],
  ['checking', 'Checking…', null, ['Change', 'Permissions'], 'muted'],
  ['reconnecting', 'Reconnecting…', 'Optimus is reconnecting to its background service.', [], 'muted'],
  ['signin', 'Not signed in', null, ['Change', 'Permissions', 'Sign in'], 'warn'],
  ['signedout', 'Signed out', null, ['Change', 'Permissions', 'Sign in', 'Disconnect'], 'warn'],
  ['notfinished', 'Sign-in didn’t finish', 'The browser didn’t come back to Optimus. Try again, or pick another AI.', ['Change', 'Permissions', 'Sign in'], 'warn'],
  ['notsaved', 'Not saved', 'This PC didn’t let Optimus save the key. Try again.', ['Change', 'Permissions', 'Add key'], 'warn'],
  ['limit', 'Limit reached', 'Your ChatGPT plan’s limit is used up for now. Try again later.', ['Change', 'Permissions', 'Retry', 'Disconnect'], 'warn'],
  ['nocredit', 'No credit', 'This account has no credit left. Add credit, or use another key.', ['Change', 'Permissions', 'Update key', 'Disconnect'], 'warn'],
  ['noteligible', 'Not available', 'This account can’t use it here. Use another key, or pick another AI.', ['Change', 'Permissions', 'Update key', 'Disconnect'], 'warn'],
  ['setup', 'Not set up', 'Claude Code isn’t installed or can’t start on this PC.', ['Change', 'Permissions', 'Set up'], 'warn'],
  ['cantrun', 'Can’t run', 'This Claude Code is too old or blocked on this PC. Update it, or pick another AI.', ['Change', 'Permissions', 'Set up'], 'warn'],
  ['nokey', 'Needs a key', null, ['Change', 'Permissions', 'Add key'], 'warn'],
  ['badkey', 'Key rejected', 'The key was turned down. Check it and add it again.', ['Change', 'Permissions', 'Update key', 'Disconnect'], 'warn'],
  ['offline', 'Can’t reach it', 'Check your internet connection.', ['Change', 'Permissions', 'Retry', 'Disconnect'], 'warn'],
  ['busy', 'Busy right now', 'Try again in a minute.', ['Change', 'Permissions', 'Retry', 'Disconnect'], 'warn'],
  ['unknown', 'Not working', 'Something went wrong, and Optimus doesn’t know why. Try again, or pick another AI.', ['Change', 'Permissions', 'Retry', 'Disconnect'], 'warn'],
  ['pairing', 'Wants to pair', 'Check this code matches the one in Claude Code’s window.', ['Change', 'Permissions', 'Deny', 'Pair', 'Disconnect'], 'live'],
  ['paired', 'Paired', 'Claude Code can now work in its own window.', ['Change', 'Permissions', 'Start', 'Disconnect'], 'ok'],
  ['manual', 'Not connected yet', 'To add Optimus to Claude Code, run this in a terminal:', ['Copy', 'Change', 'Permissions', 'Check again', 'Disconnect'], 'warn'],
  ['rejected', 'Couldn’t connect', 'Claude Code turned the connection down. Add Optimus again by running this in a terminal:', ['Copy', 'Change', 'Permissions', 'Check again', 'Disconnect'], 'warn'],
  ['closed', 'Window closed', 'The Claude Code window was closed. Open Claude Code and ask it again.', ['Change', 'Permissions', 'Disconnect'], 'warn'],
  ['local', 'No AI connected', null, ['Connect an AI'], 'muted']
];
const panelProps = (state, extra) => Object.assign({ state, provider: 'Claude', code: '2760', command: CMD, onChange: noop, onPermissions: noop, onDisconnect: noop, onAction: noop }, extra || {});
test('AIPillPanel: the table covers every design state', () => { assert.deepEqual(STATES.map((s) => s[0]).sort(), DESIGN_STATES.slice().sort()); });
for (const [state, word, hint, want, tone] of STATES) {
  test('AIPillPanel ' + state + ': head, hint, code / command, buttons, Disconnect', () => {
    fresh();
    const t = render(() => OT.AIPillPanel(panelProps(state)));
    const name = state === 'local' ? 'Local only' : 'Claude';
    assert.equal(t.props['aria-label'], name);
    const head = nodes(t).find((n) => has(n, 'ot-pillpanel-head'));
    assert.equal(text(head), name + word);
    assert.ok(has(nodes(head).find((n) => has(n, 'ot-pill-dot')), 'is-' + tone));
    // The toolbar pill shows the same word (Local only: none).
    assert.deepEqual({ ...OT.pillStatus(state) }, state === 'local' ? { status: 'local', detail: null }
      : { status: { ok: 'connected', live: 'live', muted: 'local', warn: 'expired' }[tone], detail: word });
    const hints = nodes(t).filter((n) => has(n, 'ot-pillpanel-hint'));
    if (state === 'manual' || state === 'rejected') assert.deepEqual(hints.map(text), [hint, AFTER]);
    else assert.deepEqual(hints.map(text), hint ? [hint] : []);
    // The code only for pairing (with its “Code” label), the command field only for manual and rejected (shown at once).
    const code = nodes(t).find((n) => has(n, 'ot-pair-code'));
    assert.equal(!!code, state === 'pairing');
    if (code) { assert.ok(has(code, 'is-small')); assert.equal(text(code), 'Code2760'); }
    const cmd = nodes(t).find((n) => has(n, 'ot-pillpanel-cmd'));
    assert.equal(!!cmd, state === 'manual' || state === 'rejected');
    if (cmd) assert.equal(text(nodes(cmd).find((n) => n.type === 'code')), CMD);
    assert.deepEqual(buttons(t), want);
    // The state's one primary sits at the right; Disconnect is a small text button on its own row.
    const prim = btns(t).filter((b) => has(b, 'ot-btn-primary'));
    const none = ['connected', 'checking', 'reconnecting', 'closed'].includes(state);
    assert.equal(prim.length, none ? 0 : 1);
    if (prim.length) { assert.ok(has(prim[0], 'ot-pillpanel-primary')); assert.equal(text(prim[0]).trim(), want.filter((x) => x !== 'Disconnect').pop()); }
    const off = btn(t, 'Disconnect');
    if (off) { assert.ok(has(off, 'ot-pillpanel-off')); assert.ok(!nodes(nodes(t).find((n) => has(n, 'ot-pillpanel-actions'))).includes(off)); }
    // busy (a scan, a run): no Disconnect, the rest stays.
    fresh();
    const b = render(() => OT.AIPillPanel(panelProps(state, { busy: true })));
    assert.deepEqual(buttons(b), want.filter((x) => x !== 'Disconnect'));
  });
}
test('AIPillPanel: per-kind words (ChatGPT, key) and connected + notSaved', () => {
  const words = (x) => { fresh(); const t = render(() => OT.AIPillPanel(panelProps(x.state, x))); return [nodes(t).filter((n) => has(n, 'ot-pillpanel-hint')).map(text), buttons(t)]; };
  assert.deepEqual(words({ state: 'notsaved', kind: 'chatgpt' }), [['You signed in, but this PC didn’t let Optimus save it. Try again.'], ['Change', 'Permissions', 'Sign in']]);
  assert.deepEqual(words({ state: 'notsaved', kind: 'api' }), [['This PC didn’t let Optimus save the key. Try again.'], ['Change', 'Permissions', 'Add key']]);
  assert.deepEqual(words({ state: 'noteligible', kind: 'chatgpt' }), [['This ChatGPT account can’t be used here. Sign in with another account, or pick another AI.'], ['Change', 'Permissions', 'Sign in', 'Disconnect']]);
  assert.deepEqual(words({ state: 'noteligible', kind: 'api' }), [['This account can’t use it here. Use another key, or pick another AI.'], ['Change', 'Permissions', 'Update key', 'Disconnect']]);
  assert.deepEqual(words({ state: 'connected', kind: 'chatgpt', notSaved: true }), [['Works now, but this PC didn’t save your sign-in. You’ll sign in again next time.'], ['Change', 'Permissions', 'Disconnect']]);
  assert.deepEqual(words({ state: 'connected', kind: 'api', notSaved: true }), [['Works now, but this PC didn’t save the key. You’ll add it again next time.'], ['Change', 'Permissions', 'Disconnect']]);
  assert.deepEqual(words({ state: 'connected', kind: 'claude' }), [[], ['Change', 'Permissions', 'Disconnect']]);
  // closed: Activity only while a task is open.
  assert.deepEqual(words({ state: 'closed', kind: 'claude', activity: true })[1], ['Change', 'Permissions', 'Activity', 'Disconnect']);
});
test('AIPillPanel pairing: pairBusy disables Deny and Pair, and Pair reads Pairing…', () => {
  fresh();
  const t = render(() => OT.AIPillPanel(panelProps('pairing', { pairBusy: true })));
  assert.deepEqual(buttons(t), ['Change', 'Permissions', 'Deny', 'Pairing…', 'Disconnect']);
  assert.equal(btn(t, 'Deny').props.disabled, true);
  assert.equal(btn(t, 'Pairing…').props.disabled, true);
  fresh();
  const u = render(() => OT.AIPillPanel(panelProps('pairing')));
  assert.ok(!btn(u, 'Deny').props.disabled && !btn(u, 'Pair').props.disabled);
});
test('AIPillPanel: onAction gets the button’s name (Pair sends Approve); Connect an AI opens the picker (onChange)', () => {
  const got = [];
  for (const [state] of STATES) {
    fresh();
    const t = render(() => OT.AIPillPanel(panelProps(state, { onAction: (n) => got.push(n), onChange: () => got.push('picker') })));
    btns(t).filter((b) => !['Change', 'Permissions', 'Disconnect', 'Copy'].includes(text(b).trim())).forEach((b) => b.props.onClick());
  }
  assert.deepEqual(got, ['Stop', 'Activity', 'Sign in', 'Sign in', 'Sign in', 'Add key', 'Retry', 'Update key', 'Update key', 'Set up', 'Set up', 'Add key',
    'Update key', 'Retry', 'Retry', 'Retry', 'Deny', 'Approve', 'Start', 'Check again', 'Check again', 'picker']);
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
  assert.match(flow, /var pillPanel = OT\.pillPanelState\(\{ kind: pillKind, local: !pillKind,/);
  assert.match(flow, /panel: pillPanel, pairBusy: pairBusy,/);
  assert.match(flow, /busy: id === 'scanning' \|\| working,/);
  assert.match(flow, /onOpen: function \(\) \{ setDetailOpen\(!detailOpen\); \}, open: detailOpen,/);
  assert.match(flow, /signedIn && detailOpen && h\('div', \{ className: 'ot-conn-wrap' \}/);
  assert.ok(!/rowActions/.test(flow));
});

/* ---------- designer (after v87): an error Optimus doesn't recognise says Not working everywhere ---------- */
test('Not working: picker tile word, the ChatGPT line, the chat answer word; a network error reads as before', () => {
  for (const d of ['something_new', null]) {
    assert.equal(CS.tileStatus('api-key', null, key('error', d)), 'Not working');
    assert.equal(CS.tileStatus('chatgpt', null, plan('error', d)), 'Not working');
    assert.equal(key('error', d).word, 'Not working');          // the failed chat answer's word (app-flow failWord)
    assert.equal(plan('error', d).word, 'Not working');
    assert.equal(key('error', d).tone, 'expired');
    assert.equal(CS.chatgptState(plan('error', d)), 'unknown');
    const t = login('chatgpt', { chatgpt_plan: plan('error', d) });
    assert.ok(text(t).includes('Not working') && text(t).includes('Try again') && !text(t).includes('Sign-in didn’t finish'));
  }
  assert.equal(CS.tileStatus('api-key', null, key('error', 'network')), 'Offline');
  assert.equal(CS.tileStatus('chatgpt', null, plan('error', 'network')), 'Offline');
  assert.equal(key('error', 'network').word, 'Offline');
  assert.equal(CS.chatgptState(plan('error', 'network')), 'failed');
  assert.ok(text(login('chatgpt', { chatgpt_plan: plan('error', 'network') })).includes('Sign-in didn’t finish'));
  // The picker tile marks it like the other warning words.
  fresh();
  const tile = render(() => OT.ProviderOption({ value: 'api-key', title: 'API key', status: 'Not working' }));
  const off = render(() => OT.ProviderOption({ value: 'api-key', title: 'API key', status: 'Offline' }));
  assert.equal(JSON.stringify(nodes(tile).map((n) => n.props && n.props.className)), JSON.stringify(nodes(off).map((n) => n.props && n.props.className)));
});
