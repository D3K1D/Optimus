// node --test ui/tests/   — design Version 65/66 (OneStepCards "Trade-off", RebuildCases): step.tradeoff after the rows and before
// the badges, on every step that has one: a single change and each step of a multi-step fix. One → “Trade-off: <text>” (mono line);
// several (v66) → “Trade-offs:” and a short list (mono, muted).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import fs from 'node:fs';
import vm from 'node:vm';
const require = createRequire(import.meta.url);
const FIX = require('../js/fix-plan.js');
const CS = require('../js/connection-state.js');
const { createFixtureAdapter } = require('../js/adapter-fixture.js');

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
const has = (n, c) => typeof (n.props && n.props.className) === 'string' && n.props.className.split(' ').includes(c);
const noop = () => {};

const single = (o) => Object.assign({ planId: 'plan-1', kind: 'setting', actionId: 'power.select_scheme', label: 'Switch the power plan', what: 'Uses Balanced when idle.',
  status: 'awaiting_user', rows: [{ label: 'Plan', before: 'High performance', after: 'Balanced' }], context: [],
  tradeoffs: ['Slightly lower peak speed'], flags: [], privilege: 'none', undo: { supported: true, conditions: null }, ineligible: null }, o);
const step = (n, tradeoffs, flags) => ({ n, actionId: 'setting.set', label: 'Step ' + n, what: 'Does ' + n + '.', rows: [{ label: 'Row ' + n, before: 'On', after: 'Off' }],
  context: [], tradeoffs, flags: flags || [], privilege: 'none', undo: { supported: true, conditions: null } });
const fix = { planId: 'plan-f', kind: 'fix', status: 'awaiting_user', ineligible: null,
  steps: [step(1, ['Ads in apps are no longer personalised by this ID.']), step(2, []),
    step(3, ['It won\'t start by itself; open it when you need it.', 'Features that rely on it running in the background wait until you open it.'], ['restart'])] };
const STARTUP_BOTH = ['It won\'t start by itself; open it when you need it.', 'Features that rely on it running in the background wait until you open it.'];

test('stepTradeoff: none → null; one → a string; two or three → an array', () => {
  assert.equal(FIX.stepTradeoff([]), null);
  assert.equal(FIX.stepTradeoff(null), null);
  assert.equal(FIX.stepTradeoff(undefined), null);
  assert.equal(FIX.stepTradeoff(['  ', '']), null);
  assert.equal(FIX.stepTradeoff(['Slightly lower peak speed']), 'Slightly lower peak speed');
  assert.equal(FIX.stepTradeoff([' Slightly lower peak speed ', ' ']), 'Slightly lower peak speed');
  assert.deepEqual(FIX.stepTradeoff([' A ', 'B']), ['A', 'B']);
  assert.deepEqual(FIX.stepTradeoff(['A', '', 'B', 'C']), ['A', 'B', 'C']);
});

test('a single change: the trade-off is the step’s tradeoff, not a row', () => {
  const s = FIX.planView(single(), null).steps[0];
  assert.equal(s.tradeoff, 'Slightly lower peak speed');
  assert.deepEqual(s.rows, [['Plan', 'High performance', 'Balanced']]);
  assert.equal(FIX.planView(single({ tradeoffs: [] }), null).steps[0].tradeoff, null);
  assert.deepEqual(FIX.planView(single({ tradeoffs: ['A', 'B'] }), null).steps[0].tradeoff, ['A', 'B']);
});

test('a multi-step fix: each step maps its own trade-offs (one, none, several)', () => {
  const v = FIX.planView(fix, null);
  assert.deepEqual(v.steps.map((s) => s.tradeoff), ['Ads in apps are no longer personalised by this ID.', null, STARTUP_BOTH]);
  // The trade-off stays on the step while it runs and after.
  const op = { state: 'verified', steps: [1, 2, 3].map((n) => ({ n, state: 'verified', undo: { available: true }, receipt: null })) };
  assert.equal(FIX.planView(fix, op).steps[0].tradeoff, 'Ads in apps are no longer personalised by this ID.');
});

test('the fixture’s fix plans carry each step’s handler trade-offs, so the card shows them', async () => {
  const fixture = JSON.parse(fs.readFileSync(new URL('../fixtures/m1-simulated-audit.json', import.meta.url), 'utf8'));
  const extras = JSON.parse(fs.readFileSync(new URL('../fixtures/m2-extras.json', import.meta.url), 'utf8'));
  const m = {}, storage = { getItem: (k) => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); } };
  const be = createFixtureAdapter({ fixture, extras, storage, timeScale: 0.01 });
  const r = await be.call('fixture.proposeFix', { sample: 'startup' });
  const plan = await be.call('action.get', { planId: r.planId });
  const v = FIX.planView(plan, null);
  assert.equal(v.steps.length, 3);
  assert.ok(v.steps.filter((s) => s.tradeoff).length >= 1);
  plan.steps.forEach((s, i) => assert.deepEqual(v.steps[i].tradeoff, FIX.stepTradeoff(s.tradeoffs)));
  assert.equal(plan.steps[0].actionId, 'startup.disable');
  assert.deepEqual(v.steps[0].tradeoff, STARTUP_BOTH);
  // …and the card renders it as the list.
  const tree = OT.ApprovalCard({ title: 'Quieter startup', state: v.state, steps: v.steps, onApprove: noop, onDecline: noop });
  const first = nodes(tree).filter((n) => n.type === 'li' && has(n, 'ot-plan-step'))[0];
  const line = nodes(first).find((n) => has(n, 'ot-plan-tradeoff'));
  assert.equal(line.type, 'div');
  assert.deepEqual(nodes(line).filter((n) => n.type === 'li').map(text), STARTUP_BOTH);
});

test('rendering: one → “Trade-off: …” line; several → “Trade-offs:” and a list; mono and muted, after the rows, before the badges', () => {
  const v = FIX.planView(fix, null);
  const tree = OT.ApprovalCard({ title: 'Quieter startup', state: v.state, steps: v.steps, onApprove: noop, onDecline: noop });
  const lines = nodes(tree).filter((n) => has(n, 'ot-plan-tradeoff'));
  assert.equal(lines.length, 2);
  lines.forEach((n) => assert.ok(has(n, 'text-mono-sm') && has(n, 'ot-muted')));
  assert.equal(lines[0].type, 'p');
  assert.equal(text(lines[0]), 'Trade-off: Ads in apps are no longer personalised by this ID.');
  assert.equal(nodes(lines[0]).filter((n) => n.type === 'ul').length, 0);
  assert.equal(lines[1].type, 'div');
  const kids1 = lines[1].props.children;
  assert.equal(kids1[0], 'Trade-offs:');
  assert.equal(kids1[1].type, 'ul');
  assert.deepEqual(nodes(kids1[1]).filter((n) => n.type === 'li').map(text), STARTUP_BOTH);
  assert.equal(text(lines[1]), 'Trade-offs:' + STARTUP_BOTH.join(''));
  // Three render three items.
  const three = FIX.planView(single({ tradeoffs: ['A', 'B', 'C'] }), null);
  const t3 = OT.ApprovalCard({ title: 'X', state: three.state, steps: three.steps, onApprove: noop, onDecline: noop });
  const l3 = nodes(t3).find((n) => has(n, 'ot-plan-tradeoff'));
  assert.deepEqual(nodes(l3).filter((n) => n.type === 'li').map(text), ['A', 'B', 'C']);
  const items = nodes(tree).filter((n) => n.type === 'li' && has(n, 'ot-plan-step'));
  assert.equal(items.length, 3);
  assert.equal(nodes(items[1]).filter((n) => has(n, 'ot-plan-tradeoff')).length, 0);
  const body = nodes(items[2]).find((n) => has(n, 'ot-plan-body'));
  const kids = (Array.isArray(body.props.children) ? body.props.children : [body.props.children]).filter((k) => k && typeof k === 'object');
  const at = (c) => kids.findIndex((k) => has(k, c));
  assert.ok(at('ot-plan-rows') >= 0 && at('ot-plan-rows') < at('ot-plan-tradeoff') && at('ot-plan-tradeoff') < at('ot-plan-badges'));
  // A single change renders its line too.
  const one = FIX.planView(single(), null);
  assert.ok(text(OT.ApprovalCard({ title: 'Switch power plan', state: one.state, steps: one.steps, onApprove: noop, onDecline: noop })).includes('Trade-off: Slightly lower peak speed'));
});
