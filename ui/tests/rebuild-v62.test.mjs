// node --test ui/tests/   — design Versions 62 and 63 (OneStepCards, ReportsMore, brand README "Windows dialogs"): a step with
// nothing to undo (no undo line, no Undo, not part of Undo all, never the red line or the tick); who asked ("the assistant"
// when no AI is named, no line when the user started it); "Trade-off" after the before → after rows and before the badges;
// the page-less single step with no AI named; the engine sentences of a guided task.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import fs from 'node:fs';
import vm from 'node:vm';
const require = createRequire(import.meta.url);
const FIX = require('../js/fix-plan.js');
const CS = require('../js/connection-state.js');

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
const buttons = (tree) => nodes(tree).filter((n) => n.type === 'button').map((b) => text(b).trim());
const noop = () => {};
const NOTHING = { supported: false, conditions: null, nothingToUndo: true };

// A Defender quick scan as the host sends it (single provider plan).
const scan = (extra) => Object.assign({ planId: 'plan-s', kind: 'provider', actionId: 'defender.quick_scan', label: 'Quick scan', status: 'awaiting_user',
  what: 'Asks Microsoft Defender to scan the places malware usually hides; it takes a few minutes.', target: { type: 'defender', id: 'quick', label: 'Quick scan' },
  rows: [{ label: 'Quick scan', before: 'Last 9 days ago', after: 'Now' }], context: [['Provider', 'Microsoft Defender']], tradeoffs: ['A few minutes'],
  flags: [], privilege: 'none', undo: NOTHING, requestedBy: { kind: 'claude_code', taskId: null, invocationId: null }, ineligible: null }, extra);

test('1: nothingToUndo helper: only {supported: false, nothingToUndo: true}, never with irreversible or data_loss', () => {
  assert.equal(FIX.nothingToUndo(NOTHING, []), true);
  assert.equal(FIX.nothingToUndo(NOTHING, ['long', 'network']), true);
  assert.equal(FIX.nothingToUndo(NOTHING, ['irreversible']), false);
  assert.equal(FIX.nothingToUndo(NOTHING, ['data_loss']), false);
  assert.equal(FIX.nothingToUndo({ supported: true, conditions: null, nothingToUndo: true }, []), false, 'an undoable plan is not nothing to undo');
  assert.equal(FIX.nothingToUndo({ supported: false, conditions: null }, []), false);
  assert.equal(FIX.nothingToUndo(null, []), false);
});

test('1: a nothing-to-undo step: no Undo, no Settings route back, never counted by Undo all', () => {
  assert.equal(FIX.stepCanUndo({ nothingToUndo: true, undo: true, state: 'done' }), false);
  assert.equal(FIX.stepCanUndo({ nothingToUndo: true }), false, 'not even by the badge rule');
  assert.equal(FIX.stepCanUndo({ undo: true }), true);
  assert.equal(FIX.stepCanUndo({ settings: true, undo: true }), false);
  assert.equal(FIX.stepCanUndo({ badges: ['irreversible'] }), false);
  assert.equal(FIX.stepCanUndo({ badges: [] }), true);
  const steps = [{ state: 'done', undo: true }, { state: 'done', undo: true, nothingToUndo: true }, { state: 'done', undo: true }, { state: 'failed', undo: true }];
  assert.equal(FIX.undoAllCount(steps), 2);
  assert.equal(FIX.undoAllCount([{ state: 'done', undo: true }, { state: 'done', nothingToUndo: true }]), 1, 'one undoable step left: no Undo all');
});

test('1: planView marks the step; before approval no undo line and no Settings line; done: no Undo button', () => {
  const v = FIX.planView(scan(), null, 'windowsdefender://threat');
  assert.deepEqual([v.state, v.steps[0].nothingToUndo, v.steps[0].undo, v.steps[0].settings], ['awaiting', true, false, false]);
  const card = OT.ApprovalCard({ title: 'Quick scan', state: v.state, steps: v.steps, onApprove: noop, onDecline: noop });
  const t = text(card);
  assert.ok(!t.includes('You can undo this afterwards.'), t);
  assert.ok(!t.includes('Optimus can’t undo this one.'), t);
  assert.ok(!t.includes('can’t be undone'), 'never the red line');
  assert.ok(!t.includes('I understand this can’t be undone'), 'never the tick');
  // An ordinary single change keeps its undo line.
  const plain = FIX.planView(scan({ undo: { supported: true, conditions: null } }), null, null);
  assert.ok(text(OT.ApprovalCard({ title: 'T', state: plain.state, steps: plain.steps, onApprove: noop, onDecline: noop })).includes('You can undo this afterwards.'));
  // Done: the operation offers no undo, and the card shows no Undo.
  const op = { operationId: 'op-1', planId: 'plan-s', state: 'verified', receipt: { text: 'Scan finished.' }, undo: { available: true, reason: null }, error: null };
  const done = FIX.planView(Object.assign(scan(), { status: 'verified' }), op, null);
  assert.equal(done.steps[0].state, 'done');
  assert.equal(done.steps[0].undo, false, 'even if an operation said available');
  assert.ok(!buttons(OT.ApprovalCard({ title: 'T', state: done.state, steps: done.steps, onUndoStep: noop, onUndoAll: noop, onClose: noop })).includes('Undo'));
});

test('1: a fix: nothing-to-undo steps are not part of Undo all', () => {
  const step = (n, undo) => ({ n, actionId: 'setting.set', label: 'Step ' + n, what: 'x', rows: [], flags: [], privilege: 'none', undo });
  const plan = { planId: 'plan-f', kind: 'fix', status: 'verified', steps: [step(1, { supported: true, conditions: null }), step(2, NOTHING), step(3, { supported: true, conditions: null })] };
  const op = { state: 'verified', steps: [1, 2, 3].map((n) => ({ n, state: 'verified', undo: { available: true, reason: null }, receipt: null })) };
  const v = FIX.fixView(plan, op);
  assert.deepEqual(v.steps.map((s) => [s.undo, s.nothingToUndo]), [[true, false], [false, true], [true, false]]);
  const card = OT.ApprovalCard({ title: 'Fix', state: v.state, steps: v.steps, onUndoStep: noop, onUndoAll: noop, onClose: noop });
  assert.deepEqual(buttons(card).filter((b) => b.startsWith('Undo')), ['Undo', 'Undo', 'Undo all']);
  const two = FIX.fixView(Object.assign({}, plan, { steps: plan.steps.slice(0, 2) }), { state: 'verified', steps: op.steps.slice(0, 2) });
  assert.deepEqual(buttons(OT.ApprovalCard({ title: 'Fix', state: two.state, steps: two.steps, onUndoStep: noop, onUndoAll: noop, onClose: noop })).filter((b) => b.startsWith('Undo')), ['Undo'],
    'one undoable step: no Undo all');
});

test('2: who asked: "the assistant" when no AI is named, null (no line) when the user started it', () => {
  assert.equal(FIX.requesterName({ requestedBy: { kind: 'in_app', taskId: 't-9' }, why: null }), 'the assistant');
  assert.equal(FIX.requesterName({ requestedBy: {} }), 'the assistant');
  assert.equal(FIX.requesterName({ requestedBy: { kind: 'user', taskId: null, invocationId: null } }), null);
  assert.equal(FIX.requesterName({ requestedBy: { kind: 'in_app', taskId: 't-1' }, why: { text: 'x', by: 'ChatGPT' } }), 'ChatGPT');
  const head = (props) => nodes(OT.ApprovalCard(Object.assign({ title: 'T', onApprove: noop, onDecline: noop, steps: [{ label: 'A' }] }, props)))
    .filter((x) => x.props && x.props.className === 'ot-approval-head')[0];
  assert.ok(text(head({ requestedBy: 'the assistant' })).endsWith('the assistant'));
  assert.equal(text(head({ requestedBy: null })), 'Approve?', 'started from a card: no name');
  assert.ok(text(head({})).endsWith('Claude'), 'the design default when no requestedBy is passed');
  // No "Assistant" fallback is left in the UI code.
  for (const f of ['fix-plan.js', 'app-flow.js', 'components.js']) assert.ok(!/'Assistant'/.test(fs.readFileSync(new URL('../js/' + f, import.meta.url), 'utf8')), f);
});

test('2 (v63): the page-less single step with no AI named: “The answer above says how.”', () => {
  assert.deepEqual(FIX.guidedSingleStep(true, 'the assistant'), { label: 'Make the change yourself', does: 'The answer above says how.' });
  assert.deepEqual(FIX.guidedSingleStep(true, null), { label: 'Make the change yourself', does: 'The answer above says how.' });
  assert.deepEqual(FIX.guidedSingleStep(true, 'Claude'), { label: 'Make the change yourself', does: 'Claude’s answer above says how.' });
  const pageless = { planId: 'plan-m', kind: 'manual', actionId: 'manual.open', status: 'awaiting_user', findingId: 'fnd-1', target: { type: 'manual', id: 'fnd-1', label: 'BIOS' },
    rows: [], context: [], tradeoffs: [], flags: [], privilege: 'none', undo: { supported: false, conditions: null }, ineligible: null };
  const unnamed = FIX.planView(Object.assign({}, pageless, { requestedBy: { kind: 'in_app', taskId: 't-9', invocationId: null }, why: null }), null, null, {});
  assert.equal(unnamed.steps[0].does, 'The answer above says how.');
  const user = FIX.planView(Object.assign({}, pageless, { requestedBy: { kind: 'user', taskId: null, invocationId: null } }), null, null, {});
  assert.equal(user.steps[0].does, 'The answer above says how.');
});

test('3: “Trade-off” comes after the before → after rows and before the badge words', () => {
  const v = FIX.planView(scan({ flags: ['long'] }), null, null);
  // v65: the step's own Trade-off line, no longer a row.
  assert.ok(v.steps[0].rows.every((r) => r[0] !== 'Trade-off'));
  assert.equal(v.steps[0].tradeoff, 'A few minutes');
  const t = text(OT.ApprovalCard({ title: 'Quick scan', state: v.state, steps: v.steps, onApprove: noop, onDecline: noop }));
  assert.ok(t.indexOf('Now') < t.indexOf('Trade-off') && t.indexOf('Trade-off') < t.indexOf('A few minutes') && t.indexOf('A few minutes') < t.indexOf('Takes a while'), t);
});

test('v63: the fixture’s guided-task engine sentences are the ReportsMore ones', () => {
  const src = fs.readFileSync(new URL('../js/adapter-fixture-actions.js', import.meta.url), 'utf8');
  assert.ok(src.includes("'Opens the right page in Windows Settings, where you make the change yourself. Optimus checks it afterwards.'"));
  assert.ok(src.includes("'You make this change yourself, outside Windows Settings. Optimus checks it afterwards.'"));
});
