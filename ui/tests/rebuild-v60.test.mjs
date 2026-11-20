// node --test ui/tests/   — design Version 60 (ReportsMore): the guided step's where line (only when the guide lines don't name
// the place; always on the single step), the card fills its column (the step list is the size container) and is capped at
// 680px under the ask box, explorer.restart's failure (explorerFailed), a guided task with no Windows page (the BIOS), and
// Settings › System › Display › Graphics in the UI's settings catalog.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import fs from 'node:fs';
import vm from 'node:vm';
const require = createRequire(import.meta.url);
const FIX = require('../js/fix-plan.js');
const CS = require('../js/connection-state.js');
const { createFixtureAdapter } = require('../js/adapter-fixture.js');

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

const manual = (target) => ({ planId: 'plan-m', kind: 'manual', actionId: 'manual.open', label: 'Open settings', status: 'awaiting_user', findingId: 'fnd-1',
  target, rows: [], context: [], tradeoffs: [], flags: [], privilege: 'none', undo: { supported: false, conditions: null }, ineligible: null });
const SETTINGS = manual({ type: 'settings', id: 'windowsdefender://network', label: 'Public firewall profile is off', settingsPath: 'Windows Security › Firewall & network protection' });
const BIOS = manual({ type: 'manual', id: 'fnd-1', label: 'Fan curve is set to silent in the BIOS' });
// The BIOS sample's guide lines (design v61: one line each, a hint in brackets).
const BIOS_STEPS = ['Restart and open the BIOS (press Delete or F2 while the PC starts)', 'Set the CPU fan to Standard (usually under Monitor or Hardware Monitor)', 'Save and exit'];

test('1: the where line: only when the guide lines don’t already name the place; always on the single step', () => {
  const path = 'Windows Security › Firewall & network protection';
  // Lines that name the place: no where line.
  const named = FIX.planView(SETTINGS, null, null, { guide: ['Open Windows Security › Firewall & network protection.', 'Choose Public network.'] });
  assert.deepEqual(named.steps.map((s) => s.does), [null, null]);
  // Lines that don't: the path on the first step only.
  const other = FIX.planView(SETTINGS, null, null, { guide: ['Choose Public network.', 'Turn Microsoft Defender Firewall on.'] });
  assert.deepEqual(other.steps.map((s) => s.does), [path, null]);
  // No lines: the single step “Change it in Windows Settings”, always with the path.
  const single = FIX.planView(SETTINGS, null, null, {});
  assert.deepEqual(single.steps.map((s) => [s.label, s.does]), [['Change it in Windows Settings', path]]);
  // The path from the card when the plan carries none (opts.settingsPath), and none on Windows in another language.
  const bare = manual({ type: 'settings', id: 'windowsdefender://network', label: 'x' });
  assert.equal(FIX.planView(bare, null, null, { settingsPath: path }).steps[0].does, path);
  assert.equal(FIX.planView(bare, null, null, {}).steps[0].does, null);
});

test('2: the card fills its column (the step list is the size container); capped at 680px and centred under the ask box', () => {
  const css = src('../css/components.css'), add = src('../css/additions.css');
  assert.match(css, /\.ot-plan \{ container: card \/ inline-size; \}/);
  assert.doesNotMatch(css + add, /\.ot-approval \{ container:/);
  assert.match(css, /\.ot-approval \{ width: 100%; box-sizing: border-box; \}/);
  // The app's wrapper for the design's .ot-ask-result: the main column under the ask box.
  assert.match(add, /\.ot-flow-main > \.ot-approval \{ max-width: 680px; margin-inline: auto; \}/);
  assert.doesNotMatch(add, /max-width: 640px; \}\s*$/);
  assert.doesNotMatch(add, /\.ot-flow-main > \.ot-approval \{ width: 100%; max-width: 640px; \}/);
});

test('3: explorer.restart’s failure is explorerFailed on the Failed card: the designed sentence, Check again, no Retry', () => {
  const EXP = { planId: 'plan-r', kind: 'provider', actionId: 'explorer.restart', label: 'Restart Windows Explorer', status: 'failed', rows: [], context: [], tradeoffs: [], flags: [],
    privilege: 'none', undo: { supported: false, conditions: null }, ineligible: null, findingId: 'fnd-1' };
  const op = { operationId: 'op-r', planId: 'plan-r', state: 'failed', result: 'failed', receipt: null, undo: { available: false, reason: 'not_supported' },
    error: { code: 'not_applied', message: 'The engine’s own sentence.' } };
  assert.equal(FIX.failKey('explorer.restart', op), 'explorerFailed');
  assert.equal(FIX.failKey('explorer.restart', Object.assign({}, op, { state: 'completed', result: 'restarted' })), null);
  const v = FIX.planView(EXP, op);
  assert.deepEqual([v.state, v.steps[0].state, v.steps[0].failKey, v.steps[0].note], ['failed', 'failed', 'explorerFailed', null]);
  const c = OT.ApprovalCard({ title: EXP.label, state: v.state, steps: v.steps, onCheckStep: noop, onClose: noop });
  const t = text(c);
  assert.ok(t.includes('Windows Explorer couldn’t be restarted. If the taskbar is missing, restart your PC.'));
  assert.ok(!t.includes('The engine’s own sentence.'));
  assert.deepEqual(buttons(c), ['Check again', 'Close']);
});

test('4: a guided task with no Windows page: “You do this one yourself.”, Back / Close and Check again (primary), no Open settings', () => {
  const v = FIX.planView(BIOS, null, null, { guide: BIOS_STEPS });
  assert.deepEqual([v.state, v.planSettingsUri, v.title], ['guided', null, 'Fan curve is set to silent in the BIOS']);
  assert.deepEqual(v.steps.map((s) => [s.label, s.does]), BIOS_STEPS.map((s) => [s, null]));
  const card = (extra) => OT.ApprovalCard(Object.assign({ title: v.title, state: v.state, steps: v.steps, onBack: noop, onClose: noop, onCheckAgain: noop }, extra));
  const c = card({});
  const note = byClass(c, /ot-plan-warn/)[0];
  assert.equal(text(note).trim(), 'You do this one yourself. Follow the steps, then come back and press Check again.');
  assert.equal(text(nodes(note).filter((n) => n.type === 'strong')[0]), 'You do this one yourself.');
  assert.equal(text(byClass(c, /ot-badge/)[0]).trim(), 'You do this one');
  assert.deepEqual(buttons(c), ['Back', 'Close', 'Check again']);
  assert.match(button(c, 'Check again').props.className, /ot-btn-primary/);
  assert.ok(!text(c).includes('Not available') && !text(c).includes('Open settings'));
  // guideText still replaces the second sentence.
  assert.equal(text(byClass(card({ guideText: 'Other words.' }), /ot-plan-warn/)[0]).trim(), 'You do this one yourself. Other words.');
  // Nothing changed yet; Checking… while it runs.
  assert.ok(text(card({ stillSame: true })).includes('Checked again. Nothing has changed yet.'));
  assert.deepEqual(buttons(card({ checking: true })), ['Back', 'Close', 'Checking…']);
  // With a Windows page: as before (Check again neu, Open settings primary).
  const s = card({ onOpenPlanSettings: noop });
  assert.ok(text(byClass(s, /ot-plan-warn/)[0]).startsWith('You do this one in Windows Settings. Open settings takes you to the right page.'));
  assert.deepEqual(buttons(s), ['Back', 'Close', 'Check again', 'Open settings']);
  assert.doesNotMatch(button(s, 'Check again').props.className, /ot-btn-primary/);
  assert.match(button(s, 'Open settings').props.className, /ot-btn-primary/);
  // The page gives the card Open settings only with a page to open.
  assert.match(src('../js/app-flow.js'), /props\.onOpenPlanSettings = v\.planSettingsUri \? function \(\) \{ openSettings\(v\.planSettingsUri\); \} : null;/);
});

test('5: Settings › System › Display › Graphics is in the UI’s settings catalog (and the fixture’s, with its path)', () => {
  const m = /var SETTINGS_CATALOG = \[([^\]]*)\]/.exec(src('../js/app-flow.js'));
  const ui = m[1].match(/'[^']+'/g).map((s) => s.slice(1, -1));
  assert.ok(ui.includes('ms-settings:display-advancedgraphics'));
  assert.deepEqual(ui, createFixtureAdapter.SETTINGS_CATALOG);
  assert.equal(createFixtureAdapter.SETTINGS_PATHS['ms-settings:display-advancedgraphics'], 'Settings › System › Display › Graphics');
});

/* ---------- the fixture ---------- */
const fixture = JSON.parse(fs.readFileSync(new URL('../fixtures/m1-simulated-audit.json', import.meta.url), 'utf8'));
const extras = JSON.parse(fs.readFileSync(new URL('../fixtures/m2-extras.json', import.meta.url), 'utf8'));
function memStore() { const m = {}; return { getItem: (k) => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); } }; }
const key = () => crypto.randomUUID();
const until = (be, pred) => new Promise((r) => { const off = be.onEvent((e) => { if (pred(e)) { off(); r(e); } }); });

test('fixture: the BIOS card is a guided task with no Windows page; explorer.restart can end failed (explorerFailed); Graphics opens', async () => {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  const o = await be.call('onboarding.get');
  await be.call('onboarding.save', { expectedRevision: o.revision, patch: { inventoryConsent: 'skipped', step: 'complete', provider: { value: 'api', label: 'Claude (API)' } } });
  const fin = until(be, (e) => e.type === 'scan.finished');
  await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  const report = await be.call('report.get', { reportId: (await fin).payload.reportId });
  const bios = report.findings.find((f) => f.ruleId === 'cooling.bios_fan_silent');
  assert.equal(bios.title, 'Fan curve is set to silent in the BIOS');
  const fu = await be.call('followup.get', { reportId: report.reportId });
  const cards = [fu.card].concat(fu.upcoming);
  // The card itself may be further down the queue; its guide comes from the rule.
  const rule = extras.rules.find((r) => r.ruleId === 'cooling.bios_fan_silent');
  assert.deepEqual([rule.card.guide, extras.settingsUri[rule.card.key]], [BIOS_STEPS, undefined]);
  assert.ok(cards.every((c) => c.findingId !== bios.id || c.settingsUri === null));
  const plan = await be.call('action.preview', { idempotencyKey: key(), actionId: 'manual.open', findingId: bios.id });
  const v = FIX.planView(plan, null, null, { guide: rule.card.guide });
  assert.deepEqual([v.state, v.planSettingsUri, v.steps.length], ['guided', null, 3]);
  // explorer.restart ends failed → explorerFailed.
  await be.call('fixture.setReportResult', { result: 'failed' });
  const pl = await be.call('fixture.requestAction', { actionId: 'explorer.restart', findingId: bios.id });
  const end = until(be, (e) => e.type === 'operation.state' && e.payload.planId === pl.planId && FIX.isTerminal(e.payload.state));
  const a = await be.call('shell.approvePlan', { planId: pl.planId });
  await end;
  const ev = FIX.planView(pl, await be.call('operation.get', { operationId: a.operationId }));
  assert.deepEqual([ev.state, ev.steps[0].failKey], ['failed', 'explorerFailed']);
  // The fixture's shell opens the Graphics page.
  await be.call('shell.openSettings', { uri: 'ms-settings:display-advancedgraphics' });
});
