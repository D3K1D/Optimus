// node --test ui/tests/   — Report a problem (docs/error-logging.md §2, design ReportProblem / ReportStates): the logo's menu,
// the preview's wording per state, Copy and Open GitHub, the crash prompt on start, and which endings offer Report it.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import vm from 'node:vm';
const require = createRequire(import.meta.url);
const RP = require('../js/report-problem.js');
const FIX = require('../js/fix-plan.js');
const CS = require('../js/connection-state.js');
const { createFixtureAdapter } = require('../js/adapter-fixture.js');
const fixture = JSON.parse(readFileSync(new URL('../fixtures/m1-simulated-audit.json', import.meta.url), 'utf8'));
const extras = JSON.parse(readFileSync(new URL('../fixtures/m2-extras.json', import.meta.url), 'utf8'));
function memStore() { const m = {}; return { getItem: (k) => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); } }; }
const key = () => crypto.randomUUID();
const until = (be, pred) => new Promise((r) => { const off = be.onEvent((e) => { if (pred(e)) { off(); r(e); } }); });

// The components, shallow-rendered with a stand-in React (as rebuild-v65.test.mjs).
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
  vm.runInContext(readFileSync(new URL('../js/components.js', import.meta.url), 'utf8'), ctx);
  vm.runInContext(readFileSync(new URL('../js/report-problem.js', import.meta.url), 'utf8'), ctx);
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
const buttons = (tree) => nodes(tree).filter((n) => n.type === 'button');
const button = (tree, label) => buttons(tree).find((b) => text(b).trim() === label || b.props['aria-label'] === label);
const status = (tree) => text(nodes(tree).find((n) => has(n, 'ot-rp-status')));
const noop = () => {};

// A backend that records calls and answers from a table (or the fixture).
function spy(answers, inner) {
  const calls = [];
  return { calls, call(method, params) {
    calls.push({ method, params });
    if (answers && method in answers) { const a = answers[method]; return typeof a === 'function' ? a(params) : Promise.resolve(a); }
    return inner ? inner.call(method, params) : Promise.reject(Object.assign(new Error('nope'), { code: -32601 }));
  } };
}
const tick = () => new Promise((r) => setTimeout(r, 0));

test('the logo menu: one item, Report a problem, with the version under it; it opens the preview', () => {
  let opened = 0;
  const tree = OT.AppMenu({ open: true, version: '0.9.0', onReport: () => { opened++; } });
  const toggle = buttons(tree).find((b) => b.props.id === 'ot-appmenu-btn');
  assert.equal(toggle.props['aria-label'], 'Optimus menu');
  assert.equal(toggle.props['aria-expanded'], true);
  const items = nodes(tree).filter((n) => n.props && n.props['data-menu-item']);
  assert.equal(items.length, 1);
  assert.equal(text(items[0]), 'Report a problemTell the developers what went wrong');
  assert.equal(text(nodes(tree).find((n) => has(n, 'ot-appmenu-ver'))), 'Optimus 0.9.0');
  items[0].props.onClick();
  assert.equal(opened, 1);
  // Closed: only the logo button.
  assert.equal(nodes(OT.AppMenu({ onReport: noop })).filter((n) => n.props && n.props['data-menu-item']).length, 0);
});

test('the preview: title, line, footer and each state’s exact wording', () => {
  const base = { text: 'Optimus 0.9.0 · Windows', onClose: noop, onCopy: noop, onOpen: noop };
  const ready = OT.ReportProblemPanel(Object.assign({ state: 'ready' }, base));
  const all = text(ready);
  assert.ok(all.startsWith('Report a problem'));
  assert.ok(all.includes('This is exactly what would be sent. It has no names, files or findings.'));
  assert.ok(all.includes('Sending needs a free GitHub account.'));
  assert.equal(text(nodes(ready).find((n) => n.type === 'pre')), 'Optimus 0.9.0 · Windows');
  assert.deepEqual(buttons(ready).map((b) => b.props['aria-label'] || text(b).trim()), ['Close', 'Close', 'Copy', 'Open GitHub']);
  assert.equal(status(ready), '');

  const building = OT.ReportProblemPanel(Object.assign({ state: 'building' }, base));
  assert.ok(text(building).includes('Building the report…'));
  assert.equal(nodes(building).find((n) => n.type === 'pre'), undefined);
  assert.equal(button(building, 'Copy').props.disabled, true);
  assert.equal(button(building, 'Open GitHub').props.disabled, true);

  const copied = OT.ReportProblemPanel(Object.assign({ state: 'copied' }, base));
  assert.ok(button(copied, 'Copied'));
  assert.equal(status(copied), 'Copied. You can paste it anywhere.');
  assert.equal(status(OT.ReportProblemPanel(Object.assign({ state: 'opened' }, base))), 'Opened in your browser. Say what you were doing, then press Submit there.');
  const failed = OT.ReportProblemPanel(Object.assign({ state: 'browserFailed' }, base));
  assert.equal(status(failed), 'Couldn’t open your browser. Copy the report and paste it at github.com/D3K1D/Optimus/issues/new.');
  assert.ok(has(nodes(failed).find((n) => has(n, 'ot-rp-status')), 'is-warn'));
});

test('trimmed: the shorter-link line with any state, never while building', () => {
  const line = 'Too long for GitHub’s link, so the page gets a shorter version. Copy gives you all of it.';
  for (const state of ['ready', 'copied', 'opened', 'browserFailed']) {
    assert.ok(text(OT.ReportProblemPanel({ state, text: 'x', trimmed: true, onCopy: noop, onOpen: noop })).includes(line), state);
    assert.ok(!text(OT.ReportProblemPanel({ state, text: 'x', trimmed: false, onCopy: noop, onOpen: noop })).includes(line), state);
  }
  assert.ok(!text(OT.ReportProblemPanel({ state: 'building', trimmed: true, onCopy: noop, onOpen: noop })).includes(line));
});

test('the prompt: each cause’s wording, the AI by name, Report it and Not now; it never takes focus', () => {
  const words = { crash: 'Optimus closed unexpectedly last time.', scan: 'The scan stopped with an error.', fix: 'The change stopped with an error.',
    benchmark: 'The benchmark stopped with an error.', ai: 'Claude’s task stopped with an error.' };
  for (const [cause, w] of Object.entries(words)) {
    const tree = OT.ProblemPrompt({ cause, onReport: noop, onDismiss: noop });
    assert.equal(text(nodes(tree).find((n) => has(n, 'ot-problem-text'))), w, cause);
    assert.deepEqual(buttons(tree).map((b) => text(b).trim()), ['Report it', 'Not now']);
    assert.equal(nodes(tree).some((n) => n.props && (n.props.autoFocus || n.props.tabIndex != null)), false);
  }
  assert.equal(text(nodes(OT.ProblemPrompt({ cause: 'ai', ai: 'ChatGPT' })).find((n) => has(n, 'ot-problem-text'))), 'ChatGPT’s task stopped with an error.');
});

test('the preview from the menu: building, then the fixture’s report; Copy copies the full text', async () => {
  const be = spy(null, createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 }));
  const copiedText = [];
  const c = RP.createReportController(be, { clipboard: { writeText: (t) => { copiedText.push(t); return Promise.resolve(); } } });
  const opening = c.open();
  assert.equal(c.get().panel.state, 'building');
  await opening;
  const pn = c.get().panel;
  assert.equal(pn.state, 'ready');
  assert.deepEqual(be.calls.find((x) => x.method === 'shell.problemReport').params, {});   // from the menu: no operation
  assert.ok(pn.text.length > 0 && pn.reportId);
  await c.copy();
  assert.deepEqual(copiedText, [pn.text]);
  assert.equal(c.get().panel.state, 'copied');
  c.close();
  assert.equal(c.get().panel, null);
});

test('Copy copies the full text even when the link is trimmed; a refused clipboard flashes Copy failed', async () => {
  const long = 'line\n'.repeat(3000);
  const be = spy({ 'shell.problemReport': { reportId: 'r1', text: long, title: 'Problem', trimmed: true } });
  const got = [], flashes = [];
  const c = RP.createReportController(be, { clipboard: { writeText: (t) => { got.push(t); return Promise.resolve(); } }, flash: (w) => flashes.push(w) });
  await c.open('scan');
  assert.equal(c.get().panel.trimmed, true);
  await c.copy();
  assert.equal(got[0], long);
  const c2 = RP.createReportController(be, { clipboard: { writeText: () => Promise.reject(new Error('denied')) }, flash: (w) => flashes.push(w) });
  await c2.open();
  await c2.copy();
  assert.deepEqual(flashes, ['Copy failed']);
  assert.equal(c2.get().panel.state, 'ready');
});

test('Open GitHub sends only the reportId: opened, failed or unknown → browserFailed; too soon changes nothing', async () => {
  let answer = { status: 'opened' };
  const be = spy({ 'shell.problemReport': { reportId: 'rpt-1', text: 'T', title: 'Problem', trimmed: false }, 'shell.openIssue': () => answer instanceof Error ? Promise.reject(answer) : Promise.resolve(answer) });
  const c = RP.createReportController(be, {});
  await c.open('fix');
  assert.deepEqual(be.calls[0].params, { operation: 'fix' });
  await c.openIssue();
  assert.deepEqual(be.calls.find((x) => x.method === 'shell.openIssue').params, { reportId: 'rpt-1' });
  assert.equal(c.get().panel.state, 'opened');
  answer = Object.assign(new Error('rate'), { code: -32013, data: { reason: 'rate_limited' } });
  await c.openIssue();
  assert.equal(c.get().panel.state, 'opened');
  answer = { status: 'failed' };
  await c.openIssue();
  assert.equal(c.get().panel.state, 'browserFailed');
  await c.open();
  answer = { status: 'unknown' };
  await c.openIssue();
  assert.equal(c.get().panel.state, 'browserFailed');
});

test('a report that can’t be built: the panel closes with Not available', async () => {
  const flashes = [];
  const c = RP.createReportController(spy({ 'shell.problemReport': () => Promise.reject(new Error('x')) }), { flash: (w) => flashes.push(w) });
  await c.open();
  assert.equal(c.get().panel, null);
  assert.deepEqual(flashes, ['Not available']);
});

test('crash on start: the prompt; Not now clears the markers and it stays gone', async () => {
  const be = spy({ 'shell.crashStatus': { crashed: true, process: 'host', at: '2026-11-18T10:00:00.000Z' }, 'shell.crashDismiss': { status: 'cleared' } });
  const c = RP.createReportController(be, {});
  await c.checkCrash();
  assert.deepEqual(c.get().prompt, { cause: 'crash', key: 'crash:2026-11-18T10:00:00.000Z', ai: null });
  c.dismiss();
  await tick();
  assert.equal(c.get().prompt, null);
  assert.ok(be.calls.some((x) => x.method === 'shell.crashDismiss'));
  await c.checkCrash();   // the same crash again (another window, or before the markers were cleared): not shown again
  assert.equal(c.get().prompt, null);
  // No crash: no prompt; an older host without the method: nothing.
  const quiet = RP.createReportController(spy({ 'shell.crashStatus': { crashed: false } }), {});
  await quiet.checkCrash();
  assert.equal(quiet.get().prompt, null);
  const old = RP.createReportController(spy({}), {});
  await old.checkCrash();
  assert.equal(old.get().prompt, null);
});

test('crash Report it: the preview with operation crash, and the markers cleared once the report was built', async () => {
  const order = [];
  const be = spy({ 'shell.crashStatus': { crashed: true, process: 'desktop', at: 'a' },
    'shell.problemReport': (p) => { order.push('report'); return Promise.resolve({ reportId: 'r', text: 'T', title: 'P', trimmed: false }); },
    'shell.crashDismiss': () => { order.push('dismiss'); return Promise.resolve({ status: 'cleared' }); } });
  const c = RP.createReportController(be, {});
  await c.checkCrash();
  await c.report();
  assert.equal(c.get().prompt, null);
  assert.equal(c.get().panel.state, 'ready');
  assert.deepEqual(be.calls.find((x) => x.method === 'shell.problemReport').params, { operation: 'crash' });
  assert.deepEqual(order, ['report', 'dismiss']);
});

test('one prompt at a time; Not now keeps that problem’s prompt gone, a new problem still shows', () => {
  const c = RP.createReportController(spy({}), {});
  assert.equal(c.raise('scan', 'scan:1'), true);
  assert.equal(c.raise('fix', 'fix:op1'), true);
  assert.equal(c.get().prompt.cause, 'fix');        // the newest replaces the one shown
  c.dismiss();
  assert.equal(c.raise('fix', 'fix:op1'), false);
  assert.equal(c.get().prompt, null);
  assert.equal(c.raise('scan', 'scan:2'), true);
  assert.equal(c.get().prompt.key, 'scan:2');
  assert.equal(c.raise('ai', 'ai:t1', 'Claude'), true);
  assert.equal(c.get().prompt.ai, 'Claude');
});

test('which endings are errors: scan, fix, benchmark, AI', () => {
  const F = RP.failed;
  assert.equal(F.scan({ status: 'failed' }), true);
  assert.equal(F.scan({ status: 'cancelled' }), false);
  assert.equal(F.scan({ status: 'completed', reportId: 'r' }), false);
  assert.equal(F.scanStart({ code: -32603 }), true);
  assert.equal(F.scanStart({ code: -32004 }), false);                                      // a scan is running
  assert.equal(F.scanStart({ code: -32013, data: { reason: 'bench_active' } }), false);    // busy
  assert.equal(F.scanStart({ code: -32013, data: { reason: 'rate_limited' } }), false);
  assert.equal(F.fix({ state: 'failed', error: { code: 'step_failed' } }, 'fix'), true);
  assert.equal(F.fix({ state: 'indeterminate' }, 'setting'), true);
  assert.equal(F.fix({ state: 'failed', error: { code: 'cancelled' } }, 'setting'), false);
  assert.equal(F.fix({ state: 'failed', error: { code: 'stopped' } }, 'fix'), false);
  assert.equal(F.fix({ state: 'cancelled' }, 'fix'), false);
  assert.equal(F.fix({ state: 'verified' }, 'fix'), false);
  assert.equal(F.fix({ state: 'failed' }, 'script'), false);
  assert.equal(F.benchmark({ state: 'failed' }), true);
  assert.equal(F.benchmark({ state: 'interrupted' }), true);
  assert.equal(F.benchmark({ state: 'cancelled' }), false);
  assert.equal(F.benchmark({ state: 'stopped_on_battery' }), false);
  assert.equal(F.aiTurn({ status: 'failed', code: 'internal' }), true);
  assert.equal(F.aiTurn({ status: 'failed', code: 'cancelled' }), false);
  assert.equal(F.aiTurn({ status: 'failed', code: 'tool_blocked' }), false);
  assert.equal(F.aiTurn({ status: 'finished' }), false);
  assert.equal(F.aiTask({ status: 'failed' }), true);
  assert.equal(F.aiTask({ status: 'stopped' }), false);
});

test('fixture: a stopped scan offers no prompt; a failed one does', async () => {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  const o = await be.call('onboarding.get');
  await be.call('onboarding.save', { expectedRevision: o.revision, patch: { uses: ['Gaming'], inventoryConsent: 'skipped', step: 'complete', provider: { value: 'api', label: 'Claude (API)' } } });
  const s = await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  const fin = until(be, (e) => e.type === 'scan.finished');
  const snap = await be.call('scan.cancel', { scanId: s.scanId });
  const c = RP.createReportController(be, {});
  const payload = (await fin).payload;
  for (const p of [payload, { status: snap.status, reportId: snap.reportId }]) if (RP.failed.scan(p)) c.raise('scan', 'scan:' + s.scanId);
  assert.equal(c.get().prompt, null);
  if (RP.failed.scan({ status: 'failed', reportId: null })) c.raise('scan', 'scan:x');
  assert.equal(c.get().prompt.cause, 'scan');
  assert.equal(text(nodes(OT.ProblemPrompt({ cause: c.get().prompt.cause })).find((n) => has(n, 'ot-problem-text'))), 'The scan stopped with an error.');
});

test('the app version for the menu comes from shell.crashStatus', async () => {
  const c = RP.createReportController(spy({ 'shell.crashStatus': { crashed: false, version: '0.9.0' } }), {});
  await c.checkCrash();
  assert.equal(c.get().version, '0.9.0');
  assert.equal(c.get().prompt, null);
  const menu = nodes(OT.AppMenu({ open: true, version: '0.9.0', onReport() {} }));
  assert.ok(JSON.stringify(menu.map((n) => n.props && n.props.children)).includes('Optimus 0.9.0'));
});
