// node --test ui/tests/   — design Version 57 (OneStepCards: Defender progress): a running step with no percent shows the
// elapsed time and an indeterminate bar (a sliding segment, static under reduced motion); a percent keeps the determinate
// bar. The fixture words the Defender steps as the host will ("Quick scan · just started" → "… · running for 4 min").
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
const bars = (tree) => nodes(tree).filter((n) => n.props.role === 'progressbar');
const cls = (n) => String(n.props.className || '').split(/\s+/);
const noop = () => {};
const card = (progress) => OT.ApprovalCard({ title: 'Scan with Microsoft Defender', state: 'applying', onStop: noop,
  steps: [{ label: 'Run a quick scan', progress, state: 'running' }] });

test('1: a running step with no percent: its text (live) and the indeterminate bar, on the step and on the card', () => {
  for (const t of ['Quick scan · just started', 'Quick scan · running for 4 min', 'Full scan · running for 12 min', 'Updating virus definitions',
    'Updating virus definitions · running for 2 min']) {
    const progress = FIX.stepProgress(t);
    assert.deepEqual(progress, { text: t });
    const c = card(progress);
    assert.ok(nodes(c).some((n) => n.props['aria-live'] === 'polite' && text(n) === t), t);
    const [top, step] = bars(c);
    // The step's thin bar: indeterminate, named by the step, its text as the value; no valuenow.
    assert.deepEqual(cls(step), ['ot-track', 'ot-track-thin', 'is-indeterminate']);
    assert.deepEqual([step.props['aria-label'], step.props['aria-valuetext'], 'aria-valuenow' in step.props], ['Run a quick scan', t, false]);
    assert.equal(nodes(step).filter((n) => n.type === 'span').length, 1);
    // The card's bar follows: indeterminate too (a single step with no percent).
    assert.ok(cls(top).includes('is-indeterminate') && cls(top).includes('ot-track'));
    assert.equal(top.props['aria-label'], 'Steps done');
    assert.ok(text(c).includes('Stop ends it now. Anything already done stays.'));
  }
});

test('1: a percent keeps the determinate bars (the repairs)', () => {
  const c = card(FIX.stepProgress('Checking Windows files · 40%'));
  assert.ok(nodes(c).some((n) => n.props['aria-live'] === 'polite' && text(n) === 'Checking Windows files · 40%'));
  const b = bars(c);
  assert.equal(b.length, 2);
  for (const x of b) {
    assert.ok(!cls(x).includes('is-indeterminate'));
    assert.ok(nodes(x).some((n) => n.props.style && n.props.style.width === '40%'));
  }
  assert.equal(b[1].props['aria-valuenow'], 40);
  // A fix of several steps: the card's bar counts steps, never indeterminate.
  const many = OT.ApprovalCard({ title: 'Fix', state: 'applying', onStop: noop, steps: [{ label: 'A', state: 'running', progress: { text: 'Quick scan · just started' } }, { label: 'B' }] });
  assert.ok(!cls(bars(many)[0]).includes('is-indeterminate'));
});

test('1: the indeterminate bar’s CSS as designed, with reduced motion static', () => {
  const css = fs.readFileSync(new URL('../css/components.css', import.meta.url), 'utf8');
  assert.ok(css.includes('.ot-track.is-indeterminate { position: relative; overflow: hidden; }'));
  assert.ok(css.includes('.ot-track.is-indeterminate > span { position: absolute; top: 0; bottom: 0; left: 0; width: 30% !important; animation: ot-indet 1.6s ease-in-out infinite; }'));
  assert.ok(css.includes('@keyframes ot-indet { 0% { transform: translateX(-100%); } 100% { transform: translateX(340%); } }'));
  assert.ok(css.includes('@media (prefers-reduced-motion: reduce) { .ot-track.is-indeterminate > span { animation: none; width: 100% !important; opacity: .35; } }'));
});

/* ---------- the fixture ---------- */
const { createFixtureAdapter } = require('../js/adapter-fixture.js');
const fixture = JSON.parse(fs.readFileSync(new URL('../fixtures/m1-simulated-audit.json', import.meta.url), 'utf8'));
const extras = JSON.parse(fs.readFileSync(new URL('../fixtures/m2-extras.json', import.meta.url), 'utf8'));
function memStore() { const m = {}; return { getItem: (k) => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); } }; }
const key = () => crypto.randomUUID();
const until = (be, pred) => new Promise((r) => { const off = be.onEvent((e) => { if (pred(e)) { off(); r(e); } }); });
const setup = () => createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
async function scannedReport(be) {
  const o = await be.call('onboarding.get');
  await be.call('onboarding.save', { expectedRevision: o.revision, patch: { inventoryConsent: 'skipped', step: 'complete', provider: { value: 'api', label: 'Claude (API)' } } });
  const fin = until(be, (e) => e.type === 'scan.finished');
  await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  return be.call('report.get', { reportId: (await fin).payload.reportId });
}

test('fixture: the Defender steps report running minutes, no percent; the full scan’s step says how long it can take', async () => {
  const be = setup();
  const report = await scannedReport(be);
  const fid = report.findings.find((f) => f.ruleId === 'av.signatures_stale').id;
  const seen = {};
  be.onEvent((e) => { if (e.type === 'operation.state' && e.payload.progress) (seen[e.payload.planId] = seen[e.payload.planId] || []).push(e.payload.progress); });
  await be.call('fixture.setLongScan', {});
  const want = { 'defender.quick_scan': ['Quick scan · just started', 'Quick scan · running for 1 min', 'Quick scan · running for 2 min', 'Quick scan · running for 3 min', 'Quick scan · running for 4 min'],
    'defender.full_scan': ['Full scan · just started', 'Full scan · running for 1 min'],
    'defender.update_signatures': ['Updating virus definitions', 'Updating virus definitions · running for 1 min'] };
  for (const actionId of Object.keys(want)) {
    const pl = await be.call('action.preview', { idempotencyKey: key(), actionId, findingId: fid });
    if (actionId === 'defender.full_scan') {
      assert.equal(pl.what, 'A full scan can take an hour or more.');
      assert.equal(FIX.planView(pl, null).steps[0].does, 'A full scan can take an hour or more.');
    }
    const end = until(be, (e) => e.type === 'operation.state' && e.payload.planId === pl.planId && FIX.isTerminal(e.payload.state));
    await be.call('shell.approvePlan', { planId: pl.planId });
    await end;
    assert.deepEqual(seen[pl.planId], want[actionId], actionId);
    assert.ok(seen[pl.planId].every((t) => FIX.stepProgress(t).pct === undefined));
    await be.call('fixture.setLongScan', { long: false });   // the long scan only for the quick one
  }
});
