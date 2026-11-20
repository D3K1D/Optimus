// node --test ui/tests/   — design Version 59 (ReportsMore): more report-only results (sfc / DISM unknown, Explorer), the
// failKey of a report-only failure, Check again on a Finished card (gone / still), a guided task that keeps its own steps
// once done, the card's "View steps" mode retired for the guided approval card, the Settings path as the guided step's
// `does` (settingsPath, English Windows only), and the card head that wraps at narrow widths.
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
const buttons = (tree) => nodes(tree).filter((n) => n.type === 'button').map((b) => text(b).trim());
const button = (tree, label) => nodes(tree).filter((n) => n.type === 'button' && text(n).trim() === label)[0];
const byClass = (tree, re) => nodes(tree).filter((n) => re.test((n.props && n.props.className) || ''));
const noop = () => {};
const src = (p) => fs.readFileSync(new URL(p, import.meta.url), 'utf8');

const plan = (actionId, label) => ({ planId: 'plan-r', kind: 'provider', actionId, label, status: 'running', rows: [], context: [], tradeoffs: [], flags: [],
  privilege: 'none', undo: { supported: false, conditions: null }, ineligible: null, findingId: 'fnd-1' });
const op = (state, result, more) => Object.assign({ operationId: 'op-r', planId: 'plan-r', state, result,
  receipt: state === 'completed' ? { receiptId: 'rcpt-1', completedAt: '2026-11-13T10:00:00Z', observed: { result }, text: 'x', benefit: 'unmeasured' } : null,
  undo: { available: false, reason: 'not_supported' }, error: state === 'failed' ? { code: 'tool_failed', message: 'The engine’s own sentence.' } : null }, more);
const SFC = plan('repair.sfc', 'Check and repair Windows files'), DISM = plan('repair.dism_restorehealth', 'Repair the Windows component store');
const DNS = plan('network.flush_dns', 'Clear the DNS cache'), EXP = plan('explorer.restart', 'Restart Windows Explorer');
const reported = (result, extra) => OT.ApprovalCard(Object.assign({ title: 'Check Windows files', state: 'reported', result, onClose: noop, onCheckAgain: noop,
  steps: [{ label: 'Check Windows files' }] }, extra));

test('1: result → key: sfc / DISM finished → sfcUnknown / dismUnknown; explorer.restart restarted | started | not_running', () => {
  for (const [p, r, key] of [[SFC, 'finished', 'sfcUnknown'], [DISM, 'finished', 'dismUnknown'], [EXP, 'restarted', 'explorerRestarted'],
    [EXP, 'started', 'explorerStarted'], [EXP, 'not_running', 'explorerDown']]) {
    assert.deepEqual(FIX.reportResult(p.actionId, op('completed', r)), { key }, r);
    const v = FIX.planView(p, op('completed', r));
    assert.deepEqual([v.state, v.result, v.steps], ['reported', { key }, [{ n: 1, label: p.label }]], p.actionId + ' ' + r);
  }
  // not_running is a completed result: an older host's indeterminate stays the plain unclear card.
  assert.notEqual(FIX.planView(EXP, op('indeterminate', 'not_running')).state, 'reported');
  assert.equal(FIX.reportResult('explorer.restart', op('completed', 'other')), null);
  assert.equal(FIX.reportResult('constructor', op('completed', 'x')), null);
});

test('1: the cards: unknown is a neutral grey note with “Finished”; explorerDown “Finished with problems”; the tone comes from the key', () => {
  const SAY = { sfcUnknown: ['Windows finished checking its files. Optimus couldn’t read what it found.', 'unknown'],
    dismUnknown: ['Windows finished repairing its image. Optimus couldn’t read the result.', 'unknown'],
    explorerRestarted: ['Windows Explorer restarted.', 'ok'], explorerStarted: ['Windows Explorer had to be started again. It’s running now.', 'ok'],
    explorerDown: ['Windows Explorer didn’t come back. Press Ctrl+Shift+Esc, choose Run new task, type explorer and press Enter.', 'problem'] };
  for (const [key, [sentence, tone]] of Object.entries(SAY)) {
    const c = reported({ key }), t = text(c);
    assert.ok(t.includes(sentence), key);
    assert.ok(!t.includes('Verified'), key);
    const badge = byClass(c, /ot-badge/)[0], note = byClass(c, /ot-plan-warn/)[0];
    assert.equal(text(badge), tone === 'problem' ? 'Finished with problems' : 'Finished', key);
    assert.match(badge.props.className, tone === 'problem' ? /ot-badge-exposure/ : tone === 'unknown' ? /ot-badge-gap/ : /ot-badge-verified/, key);
    assert.match(note.props.className, tone === 'problem' ? /is-result/ : tone === 'unknown' ? /is-neutral/ : /is-ok/, key);
  }
  // A tone the app passes still wins.
  assert.match(byClass(reported({ key: 'sfcUnknown', tone: 'problem' }), /ot-plan-warn/)[0].props.className, /is-result/);
});

test('2: a report-only failure: sfcFailed / dnsFailed on the Failed card in place of the engine’s sentence; Check again, no Retry', () => {
  assert.equal(FIX.failKey('repair.sfc', op('failed', 'failed')), 'sfcFailed');
  assert.equal(FIX.failKey('network.flush_dns', op('failed', 'failed', { error: { code: 'failed', message: 'x' } })), 'dnsFailed');
  assert.equal(FIX.failKey('repair.dism_restorehealth', op('failed', 'failed')), null);   // DISM: Finished with problems
  assert.equal(FIX.failKey('explorer.restart', op('failed', 'failed')), 'explorerFailed');   // design v60 (rebuild-v60.test.mjs)
  assert.equal(FIX.failKey('repair.sfc', op('failed', 'stopped', { error: { code: 'cancelled', message: 'Stopped.' } })), null);
  assert.equal(FIX.failKey('repair.sfc', op('completed', 'no_violations')), null);
  for (const [p, key, sentence] of [[SFC, 'sfcFailed', 'Windows couldn’t finish checking its files. Restart your PC and try again.'],
    [DNS, 'dnsFailed', 'Windows couldn’t clear the DNS cache.']]) {
    const v = FIX.planView(p, op('failed', 'failed'));
    assert.deepEqual([v.state, v.steps[0].state, v.steps[0].failKey, v.steps[0].note], ['failed', 'failed', key, null]);
    const c = OT.ApprovalCard({ title: p.label, state: v.state, steps: v.steps, onCheckStep: noop, onClose: noop });
    const t = text(c);
    assert.ok(t.includes(sentence) && !t.includes('The engine’s own sentence.'), key);
    assert.deepEqual(buttons(c), ['Check again', 'Close']);
  }
  // DISM's own failure stays the Finished-with-problems card; a stopped repair is Stopped.
  assert.deepEqual(FIX.planView(DISM, op('failed', 'failed')).result, { key: 'dismFailed' });
  assert.equal(FIX.planView(SFC, op('failed', 'stopped', { error: { code: 'cancelled', message: 'Stopped.' } })).state, 'stopped');
});

test('3: Check again on a Finished card: gone / still in the result note (a status line); Checking… while it runs; it stays Finished', () => {
  assert.deepEqual(['verified_by_rescan', 'awaiting_rescan', undefined].map((s) => FIX.recheckWord(s)), ['gone', 'still', 'still']);
  const line = (c) => text(byClass(c, /ot-plan-recheck/)[0]);
  assert.equal(line(reported({ key: 'sfcRepaired' })), '');
  for (const [w, say] of [['gone', 'Checked again: the problem is gone.'], ['still', 'Checked again: the problem is still there.']]) {
    const c = reported({ key: 'explorerStarted' }, { recheck: w });
    assert.equal(line(c), say);
    assert.equal(byClass(c, /ot-plan-recheck/)[0].props.role, 'status');
    assert.ok(text(byClass(c, /ot-plan-warn/)[0]).includes(say), 'inside the result note');
    assert.equal(text(byClass(c, /ot-badge/)[0]), 'Finished');
    assert.deepEqual(buttons(c), ['Close', 'Check again']);
  }
  const busy = reported({ key: 'sfcClean' }, { checking: true });
  assert.deepEqual(buttons(busy), ['Close', 'Checking…']);
  assert.equal(button(busy, 'Checking…').props.disabled, true);
  // The page clears the line while it checks again (app-flow planCardProps).
  assert.match(src('../js/app-flow.js'), /props\.recheck = !e\.checking && e\.reportRecheck \? e\.reportRecheck : null;/);
});

test('4: a Finished card with no finding behind it (a repair the AI asked for): Close alone', () => {
  const v = FIX.planView(Object.assign({}, SFC, { findingId: null }), op('completed', 'repaired'));
  assert.equal(v.state, 'reported');
  const c = OT.ApprovalCard({ title: 'Check Windows files', state: v.state, result: v.result, steps: v.steps, onClose: noop, onCheckAgain: null });
  assert.deepEqual(buttons(c), ['Close']);
  // The page offers Check again only with a finding.
  assert.match(src('../js/app-flow.js'), /props\.onCheckAgain = pl\.findingId \? function \(\) \{ recheckPlan\(e\); \} : null;/);
});

const manual = { planId: 'plan-m', kind: 'manual', actionId: 'manual.open', label: 'Open settings', status: 'awaiting_user', findingId: 'fnd-1',
  target: { type: 'settings', id: 'ms-settings:storagesense', label: 'Search indexing is slowing your PC' }, undo: { supported: false } };

test('5: a guided task with its own lines keeps them once done, each done, the receipt on the last; without lines the one step', () => {
  const guide = ['Open Indexing Options', 'Remove the Downloads folder from the list'];
  const path = 'Settings › Privacy & security › Searching Windows';
  const open = FIX.planView(manual, null, null, { guide, settingsPath: path });
  assert.deepEqual([open.state, open.steps.map((s) => [s.label, s.does, s.state])], ['guided', [[guide[0], path, null], [guide[1], null, null]]]);
  const done = FIX.planView(manual, null, null, { guide, settingsPath: path, recheck: 'verified_by_rescan' });
  assert.deepEqual([done.state, done.steps.map((s) => [s.label, s.does, s.state, s.receipt])],
    ['verified', [[guide[0], path, 'done', null], [guide[1], null, 'done', 'You changed it in Settings.']]]);
  // As the design's demo card: Verified, both steps marked done, the receipt once, Close.
  const c = OT.ApprovalCard({ title: done.title, state: done.state, steps: done.steps, onClose: noop });
  const t = text(c);
  assert.ok(t.includes(path) && t.split('You changed it in Settings.').length === 2 && t.includes('Verified'));
  assert.equal(byClass(c, /ot-plan-step is-done/).length, 2);
  assert.deepEqual(buttons(c), ['Close']);
  // A first line that already names the path doesn't repeat it.
  const named = FIX.planView(manual, null, null, { guide: ['Open ' + path + '.', 'Turn it off.'], settingsPath: path });
  assert.deepEqual(named.steps.map((s) => s.does), [null, null]);
  const one = FIX.planView(manual, null, null, { recheck: 'verified_by_rescan' });
  assert.deepEqual(one.steps.map((s) => [s.label, s.state, s.receipt]), [['Change it in Windows Settings', 'done', 'You changed it in Settings.']]);
});

test('6: the card’s “View steps” mode is retired: no steps list, words or CSS; View steps opens the guided approval card', () => {
  const card = OT.InsightCard({ category: 'security', text: 'Windows says its firewall for public networks is off.', options: ['View steps', 'Ask about this', 'Ignore'],
    guide: ['Open Windows Security', 'Turn on the firewall'], mode: 'guide', settingsUri: 'windowsdefender://network', checkAgain: true, recheck: 'Still the same',
    onOption: noop, onAsk: noop });
  const t = text(card);
  assert.ok(!t.includes('Open Windows Security') && !t.includes('Still the same') && !t.includes('Done'));
  assert.equal(byClass(card, /(^|\s)ot-guide(\s|$)/).length, 0);
  const idle = OT.InsightCard({ category: 'security', text: 'x', options: ['View steps', 'Ask about this', 'Ignore'], onOption: noop, onAsk: noop });
  assert.deepEqual(buttons(idle).filter((b) => b === 'View steps' || b === 'Ignore'), ['View steps', 'Ignore']);
  const comp = src('../js/components.js'), flow = src('../js/app-flow.js'), css = src('../css/components.css');
  assert.ok(!/mode === 'guide'/.test(comp) && !/'Still the same'/.test(comp + flow) && !/setMode\([^)]*'guide'/.test(flow) && !/recheckManual/.test(flow));
  assert.ok(!/^\.ot-guide \{/m.test(css));
  assert.match(flow, /if \(o === 'View steps'\) previewAction\(curCard, 'manual\.open'\);/);
});

test('7: settingsPath: the plan’s own (manual target, ineligible route), else the card’s; nothing when absent', () => {
  const p = 'Windows Security › Virus & threat protection';
  assert.equal(FIX.planSettingsPath(Object.assign({}, manual, { target: Object.assign({}, manual.target, { settingsPath: p }) })), p);
  assert.equal(FIX.planSettingsPath({ status: 'ineligible', ineligible: { code: 'manual_only', manualUri: 'windowsdefender://threat', settingsPath: p } }), p);
  assert.equal(FIX.planSettingsPath(manual), null);
  assert.equal(FIX.planSettingsPath({ ineligible: { settingsPath: '  ' } }), null);
  const withPath = FIX.planView(Object.assign({}, manual, { target: Object.assign({}, manual.target, { settingsPath: p }) }), null, null, { settingsPath: 'other' });
  assert.equal(withPath.steps[0].does, p);
  assert.equal(FIX.planView(manual, null, null, { settingsPath: 'Settings › Apps › Startup' }).steps[0].does, 'Settings › Apps › Startup');
  assert.equal(FIX.planView(manual, null).steps[0].does, null);
  const c = OT.ApprovalCard({ title: 'x', state: 'guided', steps: FIX.planView(manual, null).steps, onOpenPlanSettings: noop });
  assert.equal(byClass(c, /ot-plan-does/).length, 0);
});

test('7: the fixture’s path table is the design’s 13 pages, in one place', () => {
  const { createFixtureAdapter } = require('../js/adapter-fixture.js');
  const T = createFixtureAdapter.SETTINGS_PATHS;
  assert.equal(Object.keys(T).length, 13);
  assert.deepEqual([T['windowsdefender://threat'], T['ms-settings:startupapps'], T['ms-settings:display-advancedgraphics']],
    ['Windows Security › Virus & threat protection', 'Settings › Apps › Startup', 'Settings › System › Display › Graphics']);
  for (const u of createFixtureAdapter.SETTINGS_CATALOG) assert.ok(T[u], u);
  assert.ok(!/'Settings › /.test(src('../js/adapter-fixture-actions.js')) && !/'Settings › /.test(src('../js/app-flow.js')));
});

test('8: the card head (badge and AI name) wraps at narrow widths', () => {
  const css = src('../css/components.css');
  const rule = /\.ot-approval-head \{([^}]*)\}/.exec(css)[1];
  assert.match(rule, /flex-wrap: wrap/);
  assert.match(rule, /row-gap: 6px/);
  // The card's width (and under the ask box): design v60, rebuild-v60.test.mjs.
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

test('fixture: Explorer and the unknown repairs end as the designed Finished cards; sfc / DNS failures carry their failKey', async () => {
  const be = setup();
  const report = await scannedReport(be);
  const fid = report.findings[0].id;
  for (const [actionId, result, want] of [['explorer.restart', null, ['reported', 'explorerRestarted']], ['explorer.restart', 'started', ['reported', 'explorerStarted']],
    ['explorer.restart', 'not_running', ['reported', 'explorerDown']], ['repair.sfc', 'finished', ['reported', 'sfcUnknown']],
    ['repair.dism_restorehealth', 'finished', ['reported', 'dismUnknown']], ['repair.sfc', 'failed', ['failed', 'sfcFailed']], ['network.flush_dns', 'failed', ['failed', 'dnsFailed']]]) {
    if (result) await be.call('fixture.setReportResult', { result });
    const pl = await be.call('fixture.requestAction', { actionId, findingId: fid });
    const end = until(be, (e) => e.type === 'operation.state' && e.payload.planId === pl.planId && FIX.isTerminal(e.payload.state));
    const a = await be.call('shell.approvePlan', { planId: pl.planId });
    await end;
    const v = FIX.planView(pl, await be.call('operation.get', { operationId: a.operationId }));
    assert.deepEqual([v.state, v.state === 'reported' ? v.result && v.result.key : v.steps[0].failKey], want, actionId + ' ' + result);
  }
  // A guided recheck on the fixture: still, then gone.
  const g = report.findings.find((f) => f.settingsUri);
  assert.equal(FIX.recheckWord((await be.call('manual.recheck', { findingId: g.id })).status), 'still');
  assert.equal(FIX.recheckWord((await be.call('manual.recheck', { findingId: g.id })).status), 'gone');
});
