// node --test ui/tests/   — design Version 70 (FirstLookRound, PermissionsNote, AgentRunDone): connected setup steps with no
// button or state word, Continue when the chosen AI is already connected, the one-line ChatGPT sign-in, “Share app and
// device names”, the permissions note, and the finished AI run (the answer first, the steps folded).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import fs from 'node:fs';
import vm from 'node:vm';
const require = createRequire(import.meta.url);
const FIX = require('../js/fix-plan.js');
const CS = require('../js/connection-state.js');

// A React stub: function components are called (hooks give their initial values), host elements stay as a tree.
function loadComponents() {
  const React = {
    createElement(type, props, ...children) {
      props = Object.assign({}, props, { children: children.length <= 1 ? children[0] : children });
      return typeof type === 'function' ? type(props) : { type, props };
    },
    useState: (v) => [typeof v === 'function' ? v() : v, () => {}], useRef: (v) => ({ current: v === undefined ? null : v }),
    useEffect() {}, useLayoutEffect() {}, useMemo: (f) => f(), useCallback: (f) => f, useId: () => 'id', Fragment: 'fragment'
  };
  const window = { React, OptTool: Object.assign({}, CS, { fixPlan: FIX }), matchMedia: () => ({ matches: false, addEventListener() {} }) };
  const ctx = vm.createContext({ window, document: { documentElement: {} }, navigator: {}, console, setTimeout, clearTimeout, requestAnimationFrame: () => 0, cancelAnimationFrame() {} });
  vm.runInContext(fs.readFileSync(new URL('../js/components.js', import.meta.url), 'utf8'), ctx);
  return window.OptTool;
}
const OT = loadComponents();
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
const buttons = (tree) => nodes(tree).filter((n) => n.type === 'button').map((b) => text(b).trim());
const noop = () => {};
const plan = (status, detail) => CS.connectionState({ kind: 'chatgpt_plan', entry: { kind: 'chatgpt_plan', status, detail } });
const claude = (status, detail) => CS.connectionState({ kind: 'claude_code_mcp', entry: { kind: 'claude_code_mcp', status, detail } });
const key = (status, detail) => CS.connectionState({ kind: 'anthropic_api', entry: { kind: 'anthropic_api', status, detail } });

// chatgptState’s mapping is pinned in rebuild-v72.test.mjs (design v72: seven states).

test('connectLabel: Continue when the chosen AI is already connected, else Connect; an API key always Connect', () => {
  assert.equal(CS.connectLabel('local', null, null), 'Continue');
  // Claude through Claude Code: ready rows and nothing to install, update or repair.
  const cv = (o) => Object.assign({ note: null, outdated: false }, o);
  for (const s of ['connected', 'paired', 'active', 'client_closed']) assert.equal(CS.connectLabel('claude', claude(s), cv()), 'Continue', s);
  assert.equal(CS.connectLabel('claude', claude('connected'), null), 'Continue');
  assert.equal(CS.connectLabel('claude', claude('connected'), cv({ note: 'unknown' })), 'Continue');
  assert.equal(CS.connectLabel('claude', claude('connected'), cv({ note: 'apikey' })), 'Continue');
  assert.equal(CS.connectLabel('claude', claude('connected'), cv({ outdated: true })), 'Connect');
  for (const n of ['repair', 'installing', 'updating']) assert.equal(CS.connectLabel('claude', claude('connected'), cv({ note: n })), 'Connect', n);
  assert.equal(CS.connectLabel('claude', claude('error', 'signed_out'), cv()), 'Connect');
  assert.equal(CS.connectLabel('claude', claude('waiting', 'never_seen'), cv({ note: 'desktoponly' })), 'Connect');
  assert.equal(CS.connectLabel('claude', claude('pending'), cv()), 'Connect');
  assert.equal(CS.connectLabel('claude', claude('error', 'unavailable'), cv()), 'Connect');
  assert.equal(CS.connectLabel('claude', null, undefined), 'Connect');
  // ChatGPT: only once signed in.
  assert.equal(CS.connectLabel('chatgpt', plan('connected')), 'Continue');
  for (const [s, d] of [['not_configured'], ['checking'], ['error', 'callback_timeout'], ['error', 'auth'], ['error', 'quota'], ['error', 'not_eligible']]) {
    assert.equal(CS.connectLabel('chatgpt', plan(s, d)), 'Connect', s + '/' + d);
  }
  // API key: the key is checked, even when it is already connected.
  for (const [s, d] of [['connected'], ['not_configured'], ['error', 'auth'], ['error', 'quota']]) assert.equal(CS.connectLabel('api-key', key(s, d)), 'Connect');
  assert.equal(CS.connectLabel(null, null), 'Connect');
});

test('permSkipNote: the note only the first time the page is finished', () => {
  assert.equal(CS.permSkipNote(CS.permDefaults()), false);              // set:false, onboarding or a first connect
  assert.equal(CS.permSkipNote(Object.assign(CS.permDefaults(), { set: true, revision: 3 })), true);   // reopened from the pill or Change
  assert.equal(CS.permSkipNote(null), true);                            // nothing to save (older host)
  assert.equal(CS.permSkipNote(undefined), true);
});

test('setup steps: a finished step has its check and title only; “Done” is a hidden label; no Disconnect in the box', () => {
  const tree = OT.ProviderDetails({ provider: { value: 'claude' }, tileState: claude('connected'),
    client: { note: null, known: true, installed: true, outdated: false, signedIn: true, registered: 'yes', onInstall: noop, onSignIn: noop, onCheck: noop },
    onDisconnect: noop });
  const steps = nodes(tree).filter((n) => n.type === 'li');
  assert.equal(steps.length, 3);
  steps.forEach((li) => {
    assert.equal(nodes(li).filter((n) => n.type === 'button').length, 0, text(li));
    const sr = nodes(li).filter((n) => has(n, 'ot-sr'));
    assert.equal(sr.length, 1); assert.equal(text(sr[0]), 'Done');
    assert.equal(nodes(li).filter((n) => has(n, 'ot-connect-state')).length, 0);
  });
  assert.deepEqual(steps.map((li) => text(nodes(li).find((n) => has(n, 'ot-connect-title')).props.children[0])), ['Installed', 'Signed in', 'Connected']);
  assert.ok(!buttons(tree).includes('Disconnect'), buttons(tree).join('|'));
  // A step still to do keeps its button and a hidden “To do”.
  const fresh = OT.ProviderDetails({ provider: { value: 'claude' }, tileState: claude('waiting', 'never_seen'),
    client: { note: null, known: true, installed: false, notInstalled: true, onInstall: noop, onSignIn: noop, onCheck: noop } });
  assert.ok(buttons(fresh).includes('Install'));
  assert.equal(text(nodes(fresh).filter((n) => n.type === 'li')[0].props.children[1].props.children[1]), 'To do');
  // The API key box has no Disconnect either.
  assert.ok(!buttons(OT.ProviderDetails({ provider: { value: 'api-key' }, api: 'anthropic', setApi: noop, tileState: key('connected'), onDisconnect: noop })).includes('Disconnect'));
});

test('ChatGPT: one line with the globe mark: new, signed in, failed (and in progress)', () => {
  const box = (st, connect) => OT.ProviderDetails({ provider: { value: 'chatgpt' }, tileState: st, connect, onSignIn: noop, onDisconnect: noop });
  const line = (tree) => { const li = nodes(tree).filter((n) => n.type === 'li'); assert.equal(li.length, 1); return li[0]; };
  const mark = (li) => nodes(li.props.children[0]).filter((n) => n.type === 'svg');
  // new
  let t = box(plan('not_configured'));
  let li = line(t);
  assert.ok(has(nodes(t).find((n) => n.type === 'ol'), 'is-one'));
  assert.equal(text(li.props.children[1].props.children[0]), 'Sign in with ChatGPT');
  assert.deepEqual(buttons(t), ['Sign in']);
  assert.ok(text(t).includes('Opens your browser.'));
  assert.equal(mark(li).length, 1);
  assert.ok(!text(li.props.children[0]).match(/\d/));
  // signed in: a check, no button, no “Opens your browser.”, no Disconnect
  t = box(plan('connected')); li = line(t);
  assert.equal(text(li.props.children[1].props.children[0]), 'Signed in with ChatGPT');
  assert.deepEqual(buttons(t), []);
  assert.ok(!text(t).includes('Opens your browser.'));
  assert.equal(text(li.props.children[1].props.children[1]), 'Done');
  // failed: Try again
  t = box(plan('error', 'callback_timeout')); li = line(t);
  assert.equal(text(li.props.children[1].props.children[0]), 'Sign-in didn’t finish');
  assert.deepEqual(buttons(t), ['Try again']);
  assert.ok(has(li, 'is-warn'));
  // in progress: the nearest (new) with Sign in disabled and the browser line
  t = box(plan('checking'), { tile: 'chatgpt' }); li = line(t);
  assert.equal(text(li.props.children[1].props.children[0]), 'Sign in with ChatGPT');
  const b = nodes(t).find((n) => n.type === 'button');
  assert.equal(b.props.disabled, true);
  assert.ok(text(t).includes('Finish signing in in your browser.'));
  assert.ok(!text(t).includes('To do') || nodes(t).filter((n) => has(n, 'ot-sr')).length === 1);
});

test('the picker’s bottom button: Continue for a connected AI and Local only, Connect otherwise', () => {
  const panel = (prov, states, views) => OT.LoginPanel({ initialProvider: prov, tileStates: Object.assign({ chatgpt_plan: plan('not_configured') }, states), clientViews: views || {} });
  const last = (tree) => buttons(tree).pop();
  assert.equal(last(panel('local')), 'Continue');
  assert.equal(last(panel('chatgpt', { chatgpt_plan: plan('connected') })), 'Continue');
  assert.equal(last(panel('chatgpt', { chatgpt_plan: plan('error', 'cancelled') })), 'Connect');
  assert.equal(last(panel('api-key', { anthropic_api: key('connected') })), 'Connect');
  const cv = { note: null, known: true, installed: true, outdated: false, signedIn: true, registered: 'yes', onInstall: noop, onSignIn: noop, onCheck: noop };
  assert.equal(last(panel('claude', { claude_code_mcp: claude('connected') }, { claude: cv })), 'Continue');
  assert.equal(last(panel('claude', { claude_code_mcp: claude('connected') }, { claude: Object.assign({}, cv, { outdated: true }) })), 'Connect');
  assert.equal(last(panel('claude', { claude_code_mcp: claude('error', 'signed_out') }, { claude: Object.assign({}, cv, { signedIn: false }) })), 'Connect');
});

test('Disconnect is in the AI pill’s panel, right after Permissions', () => {
  const t = OT.ConnectionDetail({ state: { label: 'Claude', word: 'Connected' }, onDisconnect: noop, onChange: noop, onPermissions: noop });
  assert.deepEqual(buttons(t), ['Change', 'Permissions', 'Disconnect']);
});

test('permissions: “Share app and device names” with no grey line; no footer hint; the compact panel too', () => {
  for (const tree of [OT.PermissionsPanel({ agent: 'Claude', onBack: noop, onContinue: noop }), OT.PermissionGrant({ client: 'Claude Code', onContinue: noop, onCancel: noop })]) {
    const t = text(tree);
    assert.ok(t.includes('Share app and device names'));
    assert.ok(!t.includes('App A') && !t.includes('As they are') && !t.includes('Change this any time'), t);
    const row = nodes(tree).filter((n) => has(n, 'ot-perm-row')).find((r) => text(r).includes('Share app and device names'));
    assert.equal(nodes(row).filter((n) => has(n, 'ot-perm-line')).length, 0);
    assert.equal(nodes(tree).filter((n) => has(n, 'ot-perm-hint')).length, 0);
    assert.equal(nodes(tree).filter((n) => has(n, 'ot-perm-note-layer')).length, 0);
  }
});

test('permissions note: Continue opens it; its Continue saves and goes on; skipNote goes straight on', () => {
  // noteOpen shows it (as after Continue): the line, its own Continue, a dialog over the page.
  const saved = [];
  const open = OT.PermissionsPanel({ agent: 'Claude', noteOpen: true, onBack: noop, onContinue: (v) => saved.push(v) });
  const layer = nodes(open).find((n) => has(n, 'ot-perm-note-layer'));
  assert.ok(layer);
  const card = nodes(layer).find((n) => n.props && n.props.role === 'dialog');
  assert.equal(card.props['aria-modal'], true);
  assert.equal(text(card).trim(), 'You can change these settings any time from the AI pill.Continue');
  const go = nodes(layer).find((n) => n.type === 'button');
  go.props.onClick();
  assert.equal(saved.length, 1);
  assert.deepEqual(Object.keys(saved[0]).sort(), ['checks', 'fix', 'ask', 'history', 'names', 'scripts', 'specs', 'web', 'admin'].sort());
  // The page's Continue opens the note and saves nothing (Esc leaves the page unsaved).
  const calls = [];
  const page = OT.PermissionsPanel({ agent: 'Claude', onBack: noop, onContinue: (v) => calls.push(v) });
  const pageGo = nodes(page).filter((n) => n.type === 'button').find((b) => text(b).trim() === 'Continue');
  pageGo.props.onClick();
  assert.equal(calls.length, 0);
  // skipNote: straight on.
  const skip = OT.PermissionsPanel({ agent: 'Claude', skipNote: true, onBack: noop, onContinue: (v) => calls.push(v) });
  nodes(skip).filter((n) => n.type === 'button').find((b) => text(b).trim() === 'Continue').props.onClick();
  assert.equal(calls.length, 1);
});

const STEPS = [{ origin: 'client', tool: 'read_report', title: 'Read the report', state: 'done' },
  { origin: 'optimizer', tool: 'process.sample', title: 'Measured CPU use', detail: '18% average', state: 'done' }];

test('AgentActivity finished: the answer first (name, goal, paragraphs), then “Steps Claude took · N”; live unchanged', () => {
  const t = OT.AgentActivity({ finished: true, agent: 'Claude', goal: 'Why is my PC slow?', answer: 'One.\n\nTwo.', steps: STEPS });
  assert.ok(has(t, 'is-finished'));
  const ans = nodes(t).find((n) => has(n, 'ot-activity-answer'));
  assert.equal(text(nodes(ans).find((n) => has(n, 'ot-answer-from'))), 'Claude');
  assert.equal(text(nodes(ans).find((n) => n.type === 'h2')), 'Why is my PC slow?');
  assert.deepEqual(nodes(ans).filter((n) => n.type === 'p').map(text), ['One.', 'Two.']);
  const tog = nodes(t).find((n) => has(n, 'ot-steps-toggle'));
  assert.equal(tog.props['aria-expanded'], false);
  assert.equal(text(tog), 'Steps Claude took2');
  assert.equal(nodes(t).filter((n) => has(n, 'ot-steps')).length, 0);   // folded
  // Live: the list, Stop and the meters, as before.
  const live = OT.AgentActivity({ agent: 'Claude', goal: 'Loud on battery', steps: STEPS, budget: { diagnostics: [1, 10] } });
  assert.ok(!has(live, 'is-finished'));
  assert.ok(buttons(live).includes('Stop'));
  assert.equal(nodes(live).filter((n) => has(n, 'ot-steps')).length, 1);
  // Embedded (the opened list): no head, no meters.
  const emb = OT.AgentActivityLive({ embedded: true, agent: 'Claude', goal: 'x', steps: STEPS, budget: { diagnostics: [1, 10] } });
  assert.ok(has(emb, 'is-embedded'));
  assert.equal(nodes(emb).filter((n) => has(n, 'ot-activity-head') || has(n, 'ot-meters')).length, 0);
});

test('the app’s Activity panel and first look: finished with an answer → the designed finished view; otherwise live', () => {
  const items = [{ id: 'a', title: 'Read the report', tool: 'get_findings', origin: 'client', originLabel: 'Claude', status: 'complete' },
    { id: 'b', title: 'Measured CPU use', tool: 'process.sample', origin: 'in_app', originLabel: 'Optimizer', status: 'complete' }];
  const fin = OT.ActivityPanel({ title: 'Claude', items, finished: true, agent: 'Claude', goal: 'Why is it slow?', answer: OT.runAnswer('First.\n\nSecond **bold**.', [], noop), onClose: noop });
  assert.ok(has(fin, 'is-finished'));
  assert.equal(text(nodes(fin).find((n) => n.type === 'h2')), 'Why is it slow?');
  assert.deepEqual(nodes(fin).filter((n) => has(n, 'ot-run-para')).map(text), ['First.', 'Second bold.']);
  assert.equal(text(nodes(fin).find((n) => has(n, 'ot-steps-toggle'))), 'Steps Claude took2');
  assert.ok(nodes(fin).some((n) => n.type === 'button' && n.props['aria-label'] === 'Close'));
  // Not finished (or no answer): the live panel.
  const live = OT.ActivityPanel({ title: 'Claude', items, active: true, onStop: noop, finished: false });
  assert.ok(!has(live, 'is-finished') && buttons(live).includes('Stop'));
  // First look: answered → finished, and its answer is not shown a second time below.
  const scan = OT.AiScanPanel({ title: 'Claude', items, done: true, answer: { id: 's', text: 'Three things.', cites: [] }, onContinue: noop, onSkip: noop });
  assert.equal(nodes(scan).filter((n) => has(n, 'is-finished')).length, 1);
  assert.equal(nodes(scan).filter((n) => has(n, 'ot-answers')).length, 0);
  assert.equal(text(scan).split('Three things.').length, 2);
  // A failed first look keeps the live list and the failure line.
  const failed = OT.AiScanPanel({ title: 'Claude', items, done: true, answer: { id: 's', failed: 'No answer' }, onContinue: noop, onSkip: noop });
  assert.equal(nodes(failed).filter((n) => has(n, 'is-finished')).length, 0);
  assert.ok(text(failed).includes('No answer'));
});
