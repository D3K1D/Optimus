// node --test ui/tests/   — design Versions 75–80 (FixForMe, FixStates, PlanInCard, AdminRights): the pure mappings from the
// host's state onto the design's props (fix-plan.js: fixPanelState, checksView, fixAvailability, narrowKeep, blockedReason,
// leftOutView), and the components as the app renders them: FixButton, FixPanel, AdminChecks, the plan in a card
// (ApprovalCard inCard / stepToggles / leftOut / removed / doneLeftOut), the Fix for me button's place in a card's row,
// and the administrator row on the permissions page.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import fs from 'node:fs';
import vm from 'node:vm';
const require = createRequire(import.meta.url);
const FIX = require('../js/fix-plan.js');
const CS = require('../js/connection-state.js');

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
const cls = (tree, c) => nodes(tree).filter((n) => has(n, c));
const noop = () => {};

/* ---------- pure: card.fix → FixPanel ---------- */
const FIXMAP = { working: 'working', waiting_checks: 'checks', checking: 'checksRunning', plan_ready: 'plan', running: 'applying', fixed: 'verified',
  done_reduced: 'doneLeftOut', partly_fixed: 'partial', finished: 'finished', failed: 'failed', unclear: 'unclear', stopped: 'scriptOnly', blocked: 'blocked',
  needs_you: 'needsYou', nothing_to_fix: 'nothing', no_fix: 'noFix' };
test('fixPanelState: every card.fix state of fix.schema.json maps onto a FixPanel state; null is the card’s normal state', () => {
  const schema = JSON.parse(fs.readFileSync(new URL('../../contracts/schemas/fix.schema.json', import.meta.url), 'utf8'));
  for (const st of schema.$defs.state.enum) assert.ok(st in FIXMAP, st);   // stopped is new on the host (design v82)
  // scriptOnly (stopped): no design state of its own, the script pointer or no panel.
  const designed = ['working', 'checks', 'checksRunning', 'plan', 'blocked', 'finished', 'doneLeftOut', 'unclear', 'scriptOnly', 'script', 'applying', 'verified', 'partial', 'failed', 'needsYou', 'nothing', 'noFix'];
  for (const [host, want] of Object.entries(FIXMAP)) {
    const v = FIX.fixPanelState({ state: host });
    assert.equal(v.state, want, host);
    assert.ok(designed.includes(v.state));
    assert.equal(v.withPlan, ['plan', 'applying', 'verified', 'doneLeftOut', 'partial', 'finished', 'failed', 'unclear', 'scriptOnly'].includes(want), host);
  }
  assert.equal(FIX.fixPanelState(null), null);
  assert.equal(FIX.fixPanelState({ state: 'something_new' }), null);
});
test('blockedCause (design v82): every ineligible code onto a cause; anything else is other', () => {
  const want = { admin_not_allowed: 'adminOff', admin_unavailable: 'adminUnavailable', changed_since: 'changed', stale_plan: 'changed', managed: 'managed', protected: 'protected',
    conflict: 'conflict', too_large: 'tooLarge', unsupported: 'unsupported', not_supported: 'unsupported', hardware_limit: 'unsupported', already_set: 'other', invalid_arguments: 'other' };
  for (const [code, cause] of Object.entries(want)) assert.equal(FIX.blockedCause(code), cause, code);
  assert.equal(FIX.blockedCause(null), 'other');
});
test('leftOutView / removedLabels: the plan’s leftOut and removed as the design takes them', () => {
  const plan = { leftOut: [{ actionId: 'a', label: 'Remove an old exclusion folder', what: 'x', code: 'admin_not_allowed' }, { actionId: 'b', label: 'Stop SysMain', what: 'y', code: 'admin_unavailable' }],
    removed: [{ label: 'Stop Steam at startup' }] };
  assert.deepEqual(FIX.leftOutView(plan), [{ label: 'Remove an old exclusion folder', reason: 'off' }, { label: 'Stop SysMain', reason: 'unavailable' }]);
  assert.deepEqual(FIX.removedLabels(plan), ['Stop Steam at startup']);
  assert.deepEqual(FIX.leftOutView({}), []);
  assert.deepEqual(FIX.removedLabels(null), []);
});
test('narrowKeep: every step kept → null (the normal approval); some off → the plan’s step numbers, ascending', () => {
  const steps = [{ n: 1 }, { n: 2 }, { n: 3 }];
  assert.equal(FIX.narrowKeep([0, 1, 2], steps), null);
  assert.deepEqual(FIX.narrowKeep([2, 0], steps), [1, 3]);
  assert.deepEqual(FIX.narrowKeep([1], steps), [2]);
  assert.equal(FIX.narrowKeep([], steps), null);
  assert.deepEqual(FIX.narrowKeep([0, 9], steps), [1]);
});
test('fixAvailability: unset without tools or on a closed card; nochanges; busy; yes', () => {
  assert.equal(FIX.fixAvailability({ tools: false, open: true, changes: 'fix' }), null);
  assert.equal(FIX.fixAvailability({ tools: true, open: false, changes: 'fix' }), null);
  assert.equal(FIX.fixAvailability({ tools: true, open: true, changes: 'off', busy: true }), 'nochanges');
  assert.equal(FIX.fixAvailability({ tools: true, open: true, changes: 'ask', busy: true }), 'busy');
  assert.equal(FIX.fixAvailability({ tools: true, open: true, changes: 'fix', busy: false }), 'yes');
});

/* ---------- pure: an administrator-checks request → AdminChecks ---------- */
const NOW = Date.parse('2026-11-20T10:00:00Z');
const req = (state, code, extra) => Object.assign({ requestId: 'chk-0123456789abcdef', revision: 1, taskId: 'task-1', cardId: null, state, code: code || null, retryAt: null,
  expiresAt: '2026-11-20T10:10:00Z', checks: [{ operationId: 'op-1', id: 'security.defender_history', label: 'Defender detection history · last 30 days', state }] }, extra);
const CHECK_ROWS = [
  ['awaiting_user', null, {}, ['ask', null, null, false]],
  ['awaiting_user (cooldown 42 s ahead)', null, { retryAt: new Date(NOW + 41200).toISOString() }, ['wait', null, 42, false]],
  ['awaiting_user (cooldown passed)', null, { retryAt: new Date(NOW - 10).toISOString() }, ['ask', null, null, false]],
  ['running', null, {}, ['running', null, null, false]],
  ['complete', null, {}, ['done', null, null, false]],
  ['declined', 'user_declined', {}, ['declined', null, null, false]],
  ['declined', 'uac_cancelled', {}, ['declined', null, null, false]],
  ['declined', 'broker_cancelled', {}, ['declined', null, null, false]],
  ['failed', 'timeout', {}, ['failed', 'timeout', null, false]],
  ['failed', 'malformed', {}, ['failed', 'failed', null, false]],
  ['failed', 'interrupted', {}, ['failed', 'failed', null, false]],
  ['failed', 'broker_missing', {}, ['failed', 'failed', null, false]],
  ['failed', 'different_admin', {}, ['failed', 'failed', null, false]],
  ['cancelled', 'permissions', {}, ['cancelled', 'permissions', null, false]],
  ['cancelled', 'ai', {}, ['cancelled', 'ai', null, false]],
  ['cancelled', 'task_ended', { switchedAi: true }, ['cancelled', 'ai', null, false]],
  ['expired', null, {}, ['expired', null, null, false]],
  ['failed', 'admin_unavailable', {}, ['unavailable', null, null, false]],
  ['cancelled', 'task_ended', {}, ['cancelled', 'ended', null, false]],
  ['cancelled', 'admin_unavailable', {}, ['unavailable', null, null, false]],
  ['cancelled', 'exhausted', {}, ['cancelled', 'limit', null, false]],
  ['cancelled', 'stopped', {}, ['cancelled', 'stopped', null, false]],
  ['cancelled', 'something_new', {}, ['failed', 'failed', null, true]]
];
for (const [row, code, extra, [state, cause, secs, unfit]] of CHECK_ROWS) {
  test('checksView: ' + row + (code ? ' / ' + code : ''), () => {
    const v = FIX.checksView(req(row.split(' ')[0], code, extra), NOW);
    assert.deepEqual([v.state, v.cause, v.secondsLeft, v.unfit], [state, cause, secs, unfit]);
    assert.deepEqual(v.checks, ['Defender detection history · last 30 days'], 'the host’s labels');
  });
}
test('checksView / checksResult (design v83): all read, some unread, none read; how a card’s checks ended', () => {
  const three = (states) => req('complete', null, { checks: states.map((st, i) => ({ operationId: 'op-' + i, id: 'storage.health', label: 'Check ' + i, state: st })) });
  assert.deepEqual(FIX.checksView(three(['complete', 'complete', 'complete']), NOW).unread, []);
  assert.deepEqual(FIX.checksView(three(['complete', 'failed', 'complete']), NOW).unread, [1]);
  assert.deepEqual(FIX.checksView(three(['failed', 'failed', 'failed']), NOW).unread, [0, 1, 2]);
  assert.equal(FIX.checksView(three(['failed', 'failed', 'failed']), NOW).state, 'done');
  assert.deepEqual(FIX.checksResult(three(['complete', 'complete', 'complete'])), { result: 'done', cause: null });
  assert.deepEqual(FIX.checksResult(three(['complete', 'failed', 'complete'])), { result: 'partial', cause: null });
  assert.deepEqual(FIX.checksResult(three(['failed', 'failed', 'failed'])), { result: 'noneRead', cause: null });
  assert.deepEqual(FIX.checksResult(req('declined', 'user_declined')), { result: 'declined', cause: null });
  assert.deepEqual(FIX.checksResult(req('expired')), { result: 'expired', cause: null });
  assert.deepEqual(FIX.checksResult(req('failed', 'timeout')), { result: 'timeout', cause: 'timeout' });
  assert.deepEqual(FIX.checksResult(req('failed', 'malformed')), { result: 'failed', cause: 'failed' });
  assert.deepEqual(FIX.checksResult(req('cancelled', 'stopped')), { result: 'cancelled', cause: 'stopped' });
  assert.deepEqual(FIX.checksResult(req('cancelled', 'admin_unavailable')), { result: 'unavailable', cause: null });
  assert.equal(FIX.checksResult(req('running')), null);
  assert.equal(FIX.checksResult(req('awaiting_user')), null);
});
test('checksView: the schema’s states and codes are all covered', () => {
  const schema = JSON.parse(fs.readFileSync(new URL('../../contracts/schemas/checks.schema.json', import.meta.url), 'utf8'));
  const states = new Set(CHECK_ROWS.map((r) => r[0].split(' ')[0]));
  assert.deepEqual([...states].sort(), schema.$defs.state.enum.slice().sort());
  assert.equal(FIX.checksOpen(req('running')), true);
  assert.equal(FIX.checksOpen(req('complete')), false);
});

/* ---------- FixButton ---------- */
test('FixButton: yes (primary or secondary), busy (disabled with its line), nochanges (the why and Permissions on click)', () => {
  fresh();
  let t = render(() => OT.FixButton({ availability: 'yes', ai: 'Claude', primary: true, onFix: noop }));
  assert.ok(has(btn(t, 'Fix for me'), 'ot-btn-primary'));
  fresh();
  t = render(() => OT.FixButton({ availability: 'busy', ai: 'ChatGPT' }));
  assert.equal(btn(t, 'Fix for me').props.disabled, true);
  assert.equal(text(t).includes('ChatGPT is working on another fix.'), true);
  fresh();
  let perm = 0;
  t = render(() => OT.FixButton({ availability: 'nochanges', ai: 'Claude', onPermissions: () => perm++ }));
  assert.ok(!text(t).includes('can’t suggest'));
  btn(t, 'Fix for me').props.onClick();
  t = render(() => OT.FixButton({ availability: 'nochanges', ai: 'Claude', onPermissions: () => perm++ }));
  assert.ok(text(t).includes('Claude can’t suggest changes yet.'));
  btn(t, 'Permissions').props.onClick();
  assert.equal(perm, 1);
});

/* ---------- the button in a card's row ---------- */
const card = (extra) => Object.assign({ category: 'security', text: 'Real-time protection is off.', options: ['Ask about this', 'Ignore'], onOption: noop, onAsk: noop, ai: 'Claude' }, extra);
const actionRow = (t) => cls(t, 'ot-insight-actions')[0];
test('InsightCard: Fix for me is the primary when the card has no action of its own, else right after its primary', () => {
  fresh();
  let t = render(() => OT.InsightCard(card({ fixAvailable: 'yes' })));
  assert.deepEqual(buttons(actionRow(t)), ['Fix for me']);
  assert.ok(has(btn(actionRow(t), 'Fix for me'), 'ot-btn-primary'));
  fresh();
  t = render(() => OT.InsightCard(card({ fixAvailable: 'yes', options: ['Preview change', 'Update definitions', 'Ask about this', 'Ignore'] })));
  assert.deepEqual(buttons(actionRow(t)), ['Preview change', 'Fix for me', 'Update definitions']);
  assert.ok(has(btn(t, 'Preview change'), 'ot-btn-primary'));
  assert.ok(!has(btn(t, 'Fix for me'), 'ot-btn-primary'));
  fresh();
  t = render(() => OT.InsightCard(card({ options: ['Preview change', 'Ask about this', 'Ignore'] })));
  assert.deepEqual(buttons(actionRow(t)), ['Preview change'], 'no button without fixAvailable');
});
test('InsightCard: the fix panel replaces the rows; an opinion sits above them (the rows stay)', () => {
  fresh();
  let t = render(() => OT.InsightCard(card({ fix: { state: 'working', ai: 'Claude', onStop: noop } })));
  assert.equal(cls(t, 'ot-insight-actions').length, 0);
  assert.equal(cls(t, 'ot-insight-quiet').length, 0);
  assert.ok(text(cls(t, 'ot-fixpanel')[0]).includes('Claude is looking into it…'));
  fresh();
  t = render(() => OT.InsightCard(card({ fixAvailable: 'yes', fix: { state: 'needsYou', ai: 'Claude', note: 'Turn it on in Windows Security.', onClose: noop } })));
  const all = nodes(t), panel = all.indexOf(cls(t, 'ot-fixpanel')[0]), row = all.indexOf(actionRow(t));
  assert.ok(panel >= 0 && row > panel, 'the opinion above the card’s row');
  assert.ok(btn(actionRow(t), 'Fix for me'));
  assert.ok(btn(cls(t, 'ot-insight-quiet')[0], 'Ignore'));
});
test('CheckupCard: Fix for me first and primary; the panel and rows as on other cards', () => {
  fresh();
  const ck = { checkup: { kind: 'Bug', title: 'A known driver bug', text: 'x', sources: [] }, options: ['Ask about this', 'Hide'] };
  let t = render(() => OT.InsightCard(card(Object.assign({ fixAvailable: 'yes' }, ck))));
  assert.deepEqual(buttons(actionRow(t)), ['Fix for me', 'Ask about this', 'Hide']);
  assert.ok(has(btn(t, 'Fix for me'), 'ot-btn-primary'));
  assert.ok(!has(btn(t, 'Ask about this'), 'ot-btn-neu'));
  fresh();
  t = render(() => OT.InsightCard(card(Object.assign({ fix: { state: 'noFix', onRetry: noop } }, ck))));
  assert.equal(cls(t, 'ot-insight-actions').length, 0);
  assert.ok(text(t).includes('Claude didn’t find a fix.'));
});

/* ---------- FixPanel states ---------- */
const panel = (p) => { fresh(); return render(() => OT.FixPanel(Object.assign({ ai: 'Claude' }, p))); };
const STEPS = [{ n: 1, label: 'Turn on real-time protection', does: 'Defender checks files as they open.', rows: [['Real-time protection', 'Off', 'On']] },
  { n: 2, label: 'Update virus definitions', does: 'Gets the newest list of threats.', nothingToUndo: true, badges: ['slow'] }];
test('FixPanel working: Stop; checksResult lines (design v82/v83)', () => {
  let stop = 0;
  let t = panel({ state: 'working', onStop: () => stop++ });
  assert.deepEqual(buttons(t), ['Stop']);
  btn(t, 'Stop').props.onClick(); assert.equal(stop, 1);
  assert.ok(!text(t).includes('Checks skipped'));
  const lines = { done: 'Checks done. Claude is reading the results.', partial: 'Some checks couldn’t be read. Claude uses the rest.',
    noneRead: 'None of the checks could be read. Claude goes on without them.', declined: 'Checks skipped. Claude goes on without them.',
    expired: 'Nobody answered, so the checks were skipped. Claude goes on without them.', unavailable: 'Administrator checks aren’t available in this version yet. Claude goes on without them.',
    failed: 'The checks couldn’t run. Claude goes on without them.', timeout: 'The checks took too long and were stopped. Claude goes on without them.' };
  for (const [res, line] of Object.entries(lines)) assert.ok(text(panel({ state: 'working', checksResult: res })).includes(line), res);
  assert.ok(text(panel({ state: 'working', checksResult: 'cancelled', checksCause: 'stopped' })).includes('Checks cancelled because you stopped Claude.'));
});
test('FixPanel checks: ask, wait (Run checks off, seconds), running', () => {
  let t = panel({ state: 'checks', checks: ['Defender detection history · last 30 days'], checksState: 'ask', onRunChecks: noop, onDeclineChecks: noop });
  assert.ok(text(t).includes('Claude wants to read these. They need administrator rights.'));
  assert.ok(text(t).includes('Defender detection history · last 30 days'));
  assert.ok(text(t).includes('Nothing is changed. Windows asks first.'));
  assert.deepEqual(buttons(t), ['Not now', 'Run checks']);
  t = panel({ state: 'checks', checks: ['Disk health'], checksState: 'wait', secondsLeft: 42 });
  assert.ok(text(t).includes('You said no a moment ago. You can run these in 42 seconds.'));
  assert.equal(btn(t, 'Run checks').props.disabled, true);
  assert.equal(!!btn(t, 'Not now').props.disabled, false);
  t = panel({ state: 'checks', checks: ['Disk health'], checksState: 'wait', secondsLeft: 1 });
  assert.ok(text(t).includes('in 1 second.'));
  t = panel({ state: 'checksRunning', checks: ['Disk health'] });
  assert.ok(text(t).includes('Running checks…'));
  assert.deepEqual(buttons(t), []);
});
test('AdminChecks end states: done, declined, failed (failed | timeout), cancelled (permissions | ai)', () => {
  const ac = (p) => { fresh(); return text(render(() => OT.AdminChecks(Object.assign({ ai: 'Claude', checks: ['Disk health'] }, p)))); };
  assert.ok(ac({ state: 'done' }).startsWith('Checks done. Claude can use the results.'));
  assert.equal(ac({ state: 'declined' }), 'Checks skipped. Claude goes on without them.');
  assert.equal(ac({ state: 'failed', cause: 'failed' }), 'The checks couldn’t run. Claude goes on without them.');
  assert.equal(ac({ state: 'failed', cause: 'timeout' }), 'The checks took too long and were stopped. Claude goes on without them.');
  assert.equal(ac({ state: 'cancelled', cause: 'permissions' }), 'Checks cancelled because permissions changed.');
  assert.equal(ac({ state: 'cancelled', cause: 'ai' }), 'Checks cancelled because you changed the AI.');
  assert.equal(ac({ state: 'cancelled', cause: 'stopped' }), 'Checks cancelled because you stopped Claude.');
  assert.equal(ac({ state: 'cancelled', cause: 'ended' }), 'Checks cancelled because the conversation ended.');
  assert.equal(ac({ state: 'cancelled', cause: 'limit' }), 'Checks cancelled because Claude reached its step limit.');
  assert.equal(ac({ state: 'expired' }), 'Nobody answered, so the checks were skipped. Claude goes on without them.');
  assert.equal(ac({ state: 'unavailable' }), 'Administrator checks aren’t available in this version yet. Claude goes on without them.');
  // Design v83: read and unread checks.
  const two = ['Disk health', 'Drive encryption status'];
  assert.equal(ac({ state: 'done', checks: two, unread: [1] }), 'Some checks couldn’t be read. Claude uses the rest.Disk healthDrive encryption status · couldn’t be read');
  assert.equal(ac({ state: 'done', checks: two, unread: [0, 1] }), 'None of the checks could be read. Claude goes on without them.Disk health · couldn’t be readDrive encryption status · couldn’t be read');
  assert.equal(ac({ state: 'done', checks: two, unread: [] }), 'Checks done. Claude can use the results.Disk healthDrive encryption status');
});
test('FixPanel blocked, needsYou (Check again or Close), nothing, noFix', () => {
  let t = panel({ state: 'blocked', cause: 'adminOff', onPermissions: noop, onRetry: noop });
  assert.ok(text(t).includes('Claude’s plan can’t be done. Everything in it needs administrator rights, which are off.'));
  assert.deepEqual(buttons(t), ['Permissions', 'Fix for me']);
  const why = { adminUnavailable: 'It needs administrator rights, which aren’t available in this version yet.', changed: 'The setting changed since Claude looked.',
    managed: 'Windows or your organisation manages this setting.', protected: 'It would change a protected part of Windows.', conflict: 'Two of its steps change the same thing.',
    tooLarge: 'It has too many steps.', unsupported: 'This PC doesn’t support it.', other: 'Optimus can’t run it.' };
  for (const [cause, w] of Object.entries(why)) {
    t = panel({ state: 'blocked', cause, onPermissions: noop, onRetry: noop });
    assert.ok(text(t).includes('Claude’s plan can’t be done. ' + w), cause);
    assert.deepEqual(buttons(t), ['Fix for me'], cause + ': only adminOff shows Permissions');
  }
  t = panel({ state: 'needsYou', note: 'Only you can sign in to the vendor’s site.', onCheckAgain: noop, onClose: noop });
  assert.ok(text(t).includes('You need to do this one yourself.'));
  assert.ok(text(t).includes('Only you can sign in to the vendor’s site.'));
  assert.deepEqual(buttons(t), ['Check again']);
  t = panel({ state: 'needsYou', note: 'x', onClose: noop });
  assert.deepEqual(buttons(t), ['Close']);
  t = panel({ state: 'nothing', note: 'It is set that way on purpose.', onClose: noop });
  assert.ok(text(t).includes('Claude thinks this doesn’t need fixing.'));
  assert.ok(text(t).includes('Optimus keeps this card open until a scan shows it’s fine.'));
  assert.deepEqual(buttons(t), ['Close']);
  t = panel({ state: 'noFix', onRetry: noop });
  assert.deepEqual(buttons(t), ['Try again']);
});

/* ---------- the plan in the card ---------- */
test('plan in a card: no requester, Not now / Approve…, step switches (one stays on), Approve… gets the kept indexes, Change something…', () => {
  const got = [];
  const props = { state: 'plan', plan: { title: 'Turn Defender’s real-time protection back on', reason: 'It was turned off two days ago.', steps: STEPS, state: 'awaiting',
    leftOut: [{ label: 'Remove an old exclusion folder', reason: 'off' }], onPermissions: noop, onApprove: (k) => got.push(k), onDecline: noop, onChangeRequest: noop } };
  fresh();
  let t = render(() => OT.FixPanel(Object.assign({ ai: 'Claude' }, props)));
  const card = cls(t, 'ot-approval')[0];
  assert.ok(has(card, 'is-incard') && has(card, 'is-awaiting'), 'the head (Approve? badge) is hidden by is-incard.is-awaiting');
  assert.ok(text(t).includes('From Claude: '));
  assert.deepEqual(buttons(t).filter((b) => b !== 'Permissions' && b !== ''), ['Not now', 'Approve…']);
  assert.ok(text(t).includes('Remove an old exclusion folder · Left out: needs administrator rights, which are off.'));
  assert.ok(btn(t, 'Permissions'));
  assert.ok(nodes(t).some((n) => n.type === 'input' && n.props.placeholder === 'Change something…'));
  const switches = nodes(t).filter((n) => n.type === 'input' && n.props.role === 'switch');
  assert.equal(switches.length, 2);
  switches[1].props.onChange({ target: { checked: false } });
  t = render(() => OT.FixPanel(Object.assign({ ai: 'Claude' }, props)));
  const sw2 = nodes(t).filter((n) => n.type === 'input' && n.props.role === 'switch');
  assert.equal(sw2[0].props.disabled, true, 'the last step on stays on');
  assert.ok(cls(t, 'is-left-out').length === 1);
  btn(t, 'Approve…').props.onClick();
  assert.deepEqual(JSON.parse(JSON.stringify(got)), [[0]]);
});
test('plan in a card: a single step has no switch; unavailable rights have no Permissions', () => {
  fresh();
  const t = render(() => OT.FixPanel({ ai: 'Claude', state: 'plan', plan: { title: 'x', steps: [STEPS[0]], state: 'awaiting', leftOut: [{ label: 'Stop SysMain', reason: 'unavailable' }], onApprove: noop, onDecline: noop } }));
  assert.equal(nodes(t).filter((n) => n.type === 'input' && n.props.role === 'switch').length, 0);
  assert.ok(text(t).includes('Stop SysMain · Left out: needs administrator rights, not available in this version yet.'));
  assert.ok(!btn(t, 'Permissions'));
});
test('doneLeftOut: badge, banner, count and what was left out (by you, and for administrator rights)', () => {
  fresh();
  const t = render(() => OT.FixPanel({ ai: 'Claude', state: 'doneLeftOut', plan: { title: 'x', steps: [Object.assign({}, STEPS[0], { state: 'done' })],
    removed: ['Update virus definitions'], leftOut: [{ label: 'Remove an old exclusion folder', reason: 'off' }], onClose: noop } }));
  const s = text(t);
  assert.ok(s.includes('Done, steps left out'));
  assert.ok(s.includes('Done, with steps left out. The steps that ran are verified. The next scan shows whether the problem is gone.'));
  assert.ok(s.includes('1 done · 2 left out'));
  assert.ok(s.includes('Update virus definitions · Left out by you'));
  assert.ok(s.includes('Remove an old exclusion folder · Left out: needs administrator rights'));
  assert.ok(btn(t, 'Close'));
});
test('finished: the reported state in the card (Finished badge and the result), never verified', () => {
  fresh();
  const t = render(() => OT.FixPanel({ ai: 'Claude', state: 'finished', plan: { title: 'Clear the DNS cache', steps: [{ n: 1, label: 'Clear the DNS cache' }], result: { key: 'dnsCleared' }, onClose: noop } }));
  assert.ok(has(cls(t, 'ot-approval')[0], 'is-reported'));
  assert.ok(text(t).includes('Finished'));
  assert.ok(text(t).includes('The DNS cache is cleared.'));
  assert.ok(!text(t).includes('Verified'));
});
test('run states in a card keep their badge (applying, verified, partial, failed)', () => {
  for (const [st, word] of [['applying', 'Applying'], ['verified', 'Verified'], ['partial', 'Partly done'], ['failed', 'Failed']]) {
    fresh();
    const steps = STEPS.map((s, i) => Object.assign({}, s, { state: st === 'applying' ? (i ? 'queued' : 'running') : st === 'verified' ? 'done' : i ? 'failed' : 'done' }));
    const t = render(() => OT.FixPanel({ ai: 'Claude', state: st, plan: { title: 'x', steps, onClose: noop } }));
    assert.ok(text(cls(t, 'ot-approval-head')[0]).includes(word), st);
  }
});

/* ---------- permissions ---------- */
test('permissions: Use administrator rights after Run approved scripts; off by default; disabled with its grey line while not available; the summary', () => {
  fresh();
  let t = render(() => OT.PermissionsPanel({ agent: 'Claude', showScripts: true, skipNote: true, onContinue: noop }));
  const titles = cls(t, 'ot-perm-title').map(text);
  assert.equal(titles.indexOf('Use administrator rights'), titles.indexOf('Run approved scripts') + 1);
  const row = cls(t, 'ot-perm-row').find((r) => text(r).includes('Use administrator rights'));
  assert.equal(nodes(row).find((n) => n.type === 'input').props.checked, false);
  fresh();
  t = render(() => OT.PermissionsPanel({ agent: 'Claude', adminUnavailable: true, initial: Object.assign(CS.permToggles(), { admin: true }), onContinue: noop }));
  const off = cls(t, 'ot-perm-row').find((r) => text(r).includes('Use administrator rights'));
  assert.ok(has(off, 'is-off'));
  assert.ok(text(off).includes('Not available in this version yet'));
  assert.equal(nodes(off).find((n) => n.type === 'input').props.disabled, true);
  fresh();
  t = render(() => OT.PermissionsPanel({ agent: 'Claude', initial: Object.assign(CS.permToggles(), { admin: true }), onContinue: noop }));
  assert.ok(text(cls(t, 'ot-perm-row').find((r) => text(r).includes('Use administrator rights'))).includes('Windows asks each time.'));
  // The compact pairing panel has it too.
  fresh();
  t = render(() => OT.PermissionGrant({ client: 'Claude Code', adminUnavailable: true, onContinue: noop, onCancel: noop }));
  assert.ok(text(t).includes('Use administrator rights'));
  assert.ok(text(t).includes('Not available in this version yet'));
  // The summary line.
  const rec = (admin) => ({ version: 1, revision: 1, set: true, see: { specs: true, history: true, names: false }, do: { checks: true, changes: 'fix', scripts: false, web: false, admin } });
  assert.equal(CS.permissionLine(rec(true), { tools: true, admin: true }), 'Reads your report · runs checks · asks before changes · can ask for administrator rights');
  assert.equal(CS.permissionLine(rec(true), { tools: true, admin: false }), 'Reads your report · runs checks · asks before changes');
  assert.equal(CS.permissionLine(rec(false), { tools: true, admin: true }), 'Reads your report · runs checks · asks before changes');
  assert.equal(CS.permRecord(Object.assign(CS.permToggles(rec(true)), {})).do.admin, true);
  assert.equal(CS.grantFromToggles(CS.permToggles(rec(true)), []).admin, true);
});

test('design v84: the script pointer, waiting / running / ended, Show script in all three', () => {
  const want = { waiting: 'Claude wrote a script for this. Read it beside the chat.', running: 'Claude’s script is running. Follow it beside the chat.', ended: 'Claude’s script has ended. See what happened beside the chat.' };
  for (const [st, line] of Object.entries(want)) {
    fresh();
    const t = render(() => OT.FixPanel({ ai: 'Claude', state: 'script', scriptState: st === 'waiting' ? undefined : st, onShowScript: noop }));
    assert.equal(text(nodes(t).find((n) => has(n, 'ot-fix-line'))), line, st);
    assert.deepEqual(buttons(t), ['Show script'], st);
    const spin = nodes(t).some((n) => has(n, 'ot-logo') && has(n, 'is-spinning'));
    assert.equal(spin, st === 'running', st + ': the turning gears only while it runs');
  }
  assert.equal(FIX.scriptPointer({ status: 'awaiting_user' }, null), 'waiting');
  assert.equal(FIX.scriptPointer({ status: 'approved' }, null), 'running');
  assert.equal(FIX.scriptPointer({ status: 'running' }, { state: 'running' }), 'running');
  for (const s of ['completed', 'failed', 'stopped', 'timed_out', 'not_run', 'indeterminate']) assert.equal(FIX.scriptPointer({ status: 'running' }, { state: s }), 'ended', s);
});
