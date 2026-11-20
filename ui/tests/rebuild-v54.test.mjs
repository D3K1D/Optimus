// node --test ui/tests/   — design Versions 54 and 55 (OneStepCards, ClaudeSetupMore, check-up partial chip): what a long
// single change, the undo line, a failed step, Back, a guided task, a script that didn't start, the tile words and the
// check-up chip show. Pure helpers (fix-plan.js, connection-state.js) and the components rendered with a tiny React stub.
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
// Every node of a rendered tree, and its text.
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

const single = (o) => Object.assign({ planId: 'plan-1', kind: 'setting', actionId: 'power.select_scheme', label: 'Switch power plan', what: 'Uses Balanced when idle.',
  status: 'awaiting_user', rows: [{ label: 'Active power plan', before: 'High performance', after: 'Balanced' }], context: [], tradeoffs: [], flags: [], privilege: 'none',
  undo: { supported: true, conditions: 'Only while Balanced is still active and High performance is still installed' }, ineligible: null }, o);
const op = (state, more) => Object.assign({ operationId: 'op-1', planId: 'plan-1', state, receipt: null, undo: null, error: null }, more);

test('1: a long step’s progress from the host’s text (Defender scans, repairs); fixes and scripts keep their own lines', () => {
  // design v56: the host words them; the page only splits off a trailing percent.
  assert.deepEqual(FIX.stepProgress('Quick scan · 60%'), { text: 'Quick scan', pct: 60 });
  assert.deepEqual(FIX.stepProgress('Full scan · running for 12 min'), { text: 'Full scan · running for 12 min' });
  assert.deepEqual(FIX.stepProgress('Quick scan · just started'), { text: 'Quick scan · just started' });
  assert.deepEqual(FIX.stepProgress('Checking Windows files · 40%'), { text: 'Checking Windows files', pct: 40 });
  assert.deepEqual(FIX.stepProgress('Repairing the Windows image · 40%'), { text: 'Repairing the Windows image', pct: 40 });
  assert.deepEqual(FIX.stepProgress('Updating virus definitions'), { text: 'Updating virus definitions' });
  assert.deepEqual(FIX.stepProgress('Updating virus definitions · running for 2 min'), { text: 'Updating virus definitions · running for 2 min' });
  for (const t of [null, '', 'Step 2 of 3', 'Running · 0:42']) assert.equal(FIX.stepProgress(t), null, String(t));
  // Only the running step of a single change gets it.
  const scan = single({ kind: 'provider', actionId: 'defender.quick_scan', undo: { supported: false, conditions: null } });
  const v = FIX.planView(scan, op('attempted'), null, { progress: 'Quick scan · 60%' });
  assert.deepEqual([v.state, v.steps[0].state, v.steps[0].progress], ['applying', 'running', { text: 'Quick scan', pct: 60 }]);
  assert.equal(FIX.planView(scan, op('verified'), null, { progress: 'Quick scan · 60%' }).steps[0].progress, undefined);
  // Stopped: the host ends a stopped long tool cancelled.
  for (const o of [op('cancelled'), op('failed', { error: { code: 'cancelled', message: 'Stopped.' } })]) {
    const s = FIX.planView(scan, o);
    assert.deepEqual([s.state, s.steps[0].state], ['stopped', 'skipped'], o.state);
  }
  // The card: "Quick scan · 60%" (live), the thin bar, the card's bar at 60 %, Stop and its line.
  const card = OT.ApprovalCard({ title: 'Scan with Microsoft Defender', state: 'applying', onStop: noop, steps: [{ label: 'Run a quick scan', progress: { text: 'Quick scan', pct: 60 }, state: 'running' }] });
  const all = nodes(card);
  assert.ok(all.some((n) => n.props['aria-live'] === 'polite' && text(n) === 'Quick scan · 60%'));
  const bars = all.filter((n) => n.props.role === 'progressbar');
  assert.equal(bars.length, 2);
  assert.ok(bars.every((b) => nodes(b).some((x) => x.props.style && x.props.style.width === '60%')));
  assert.ok(text(card).includes('Stop ends it now. Anything already done stays.'));
  assert.deepEqual(buttons(card), ['Stop']);
  const stopped = OT.ApprovalCard({ title: 'Scan', state: 'stopped', onClose: noop, steps: [{ label: 'Run a quick scan', state: 'skipped' }] });
  assert.ok(text(stopped).includes('Stopped. Anything already done stays.'));
  assert.ok(!text(stopped).includes('Step 1'));
});

test('2: the undo line before approval: the plan’s own condition, the default, Settings, and Can’t be undone', () => {
  const v = FIX.planView(single(), null, 'ms-settings:powersleep');
  assert.deepEqual([v.steps[0].undo, v.steps[0].undoNote, v.steps[0].settings], [true, 'Only while Balanced is still active and High performance is still installed', false]);
  const plain = FIX.planView(single({ undo: { supported: true, conditions: null } }), null);
  assert.deepEqual([plain.steps[0].undo, plain.steps[0].undoNote], [true, null]);
  // Optimus can't undo it but a Settings page changes it back.
  const off = FIX.planView(single({ undo: { supported: false, conditions: null } }), null, 'ms-settings:powersleep');
  assert.deepEqual([off.steps[0].undo, off.steps[0].settings], [false, true]);
  assert.equal(FIX.planView(single({ undo: { supported: false, conditions: null } }), null).steps[0].settings, false);
  assert.equal(FIX.planView(single({ undo: { supported: false, conditions: null }, flags: ['irreversible'] }), null, 'ms-settings:').steps[0].settings, false);
  assert.equal(FIX.planView(single({ kind: 'provider', undo: { supported: false, conditions: null } }), null, 'ms-settings:').steps[0].settings, false);
  const say = (props) => text(OT.ApprovalCard(Object.assign({ title: 'T', onDecline: noop, onApprove: noop }, props)));
  assert.ok(say({ steps: [Object.assign({ label: 'A' }, v.steps[0])] }).includes('Only while Balanced is still active'));
  assert.ok(say({ steps: [{ label: 'A', undo: true }] }).includes('You can undo this afterwards.'));
  assert.ok(say({ steps: [{ label: 'A', undo: false, settings: true }] }).includes('Optimus can’t undo this one. You can change it back in Settings.'));
  const risky = say({ steps: [{ label: 'A', badges: ['irreversible'] }] });
  assert.ok(risky.includes('Can’t be undone') && risky.includes('This can’t be undone.') && risky.includes('I understand this can’t be undone'));
  assert.ok(!risky.includes('You can undo this'));
  assert.ok(say({ steps: [{ label: 'A', badges: ['unsaved'] }] }).includes('This may lose unsaved work.'));
  // A fix's step shows its line only with its own undoNote.
  const two = say({ steps: [{ label: 'A', undo: true }, { label: 'B', undo: true, undoNote: 'Only while the rule is unchanged' }] });
  assert.ok(two.includes('Only while the rule is unchanged') && !two.includes('You can undo this afterwards.'));
});

test('3: a failed step offers Check again, and Open settings when it has a Settings page; one-step wording', () => {
  const failed = FIX.planView(single(), op('failed', { error: { code: 'write_failed', message: 'Windows refused the change.' } }), 'ms-settings:powersleep');
  assert.deepEqual([failed.state, failed.steps[0].state, failed.steps[0].settings, failed.settingsUri], ['failed', 'failed', true, 'ms-settings:powersleep']);
  const card = OT.ApprovalCard({ title: 'Turn on Memory integrity', state: 'failed', onCheckStep: noop, onOpenSettings: noop, onClose: noop,
    steps: [{ label: 'Turn on Memory integrity', settings: true, state: 'failed', note: 'A driver on this PC blocks it.' }] });
  assert.deepEqual(buttons(card), ['Open settings', 'Check again', 'Close']);
  const t = text(card);
  assert.ok(t.includes('This change failed.') && t.includes('Nothing changed') && !t.includes('Step 1'));
  const ok = text(OT.ApprovalCard({ title: 'T', state: 'verified', onClose: noop, steps: [{ label: 'A', state: 'done' }] }));
  assert.ok(ok.includes('Done') && !ok.includes('1 of 1'));
  const cant = text(OT.ApprovalCard({ title: 'T', state: 'cantrun', onClose: noop, steps: [{ label: 'A', state: 'blocked', note: 'Protected.' }] }));
  assert.ok(cant.includes('This change can’t run. Nothing was changed.'));
  // A whole-plan Can't run: no state word on the steps; a step-blamed one marks only that step.
  const whole = nodes(OT.ApprovalCard({ title: 'T', state: 'cantrun', blockedWhy: 'Too many steps.', onClose: noop, steps: [{ label: 'A' }, { label: 'B' }] }));
  assert.equal(whole.filter((n) => /ot-plan-state/.test(n.props.className || '')).length, 0);
  const blamed = nodes(OT.ApprovalCard({ title: 'T', state: 'cantrun', onClose: noop, steps: [{ label: 'A' }, { label: 'B', state: 'blocked' }] }));
  assert.deepEqual(blamed.filter((n) => /ot-plan-state/.test(n.props.className || '')).map(text), ['Blocked']);
});

test('4: Back leaves the preview (a ghost button at the left of the foot) without deciding', () => {
  let back = 0, declined = 0;
  const card = OT.ApprovalCard({ title: 'Turn off Game Bar recording', onBack: () => back++, onDecline: () => declined++, onApprove: noop,
    steps: [{ label: 'Turn off background recording', does: 'Game Bar stops recording in the background.', rows: [['Background recording', 'On', 'Off']], undo: true }] });
  assert.deepEqual(buttons(card), ['Back', 'Decline', 'Approve']);
  const b = nodes(card).filter((n) => n.type === 'button' && text(n).trim() === 'Back')[0];
  assert.match(b.props.className, /ot-plan-back/);
  b.props.onClick();
  assert.deepEqual([back, declined], [1, 0]);
  assert.deepEqual(buttons(OT.ApprovalCard({ title: 'T', onDecline: noop, onApprove: noop, steps: [{ label: 'A' }] })), ['Decline', 'Approve']);
});

test('5: a guided task: the host’s manual.open plan or a manual_only change; Check again; verified by rescan', () => {
  const manual = { planId: 'plan-m', kind: 'manual', actionId: 'manual.open', label: 'Open settings', status: 'awaiting_user', findingId: 'fnd-1',
    target: { type: 'settings', id: 'ms-settings:privacy-general', label: 'Ads ID is on' }, rows: [{ label: 'Opens', before: '', after: 'ms-settings:privacy-general' }], undo: { supported: false } };
  assert.equal(FIX.isGuided(manual), true);
  const v = FIX.planView(manual, null);
  assert.deepEqual([v.state, v.planSettingsUri, v.steps.map((s) => s.label), v.steps[0].rows, v.steps[0].state, v.title], ['guided', 'ms-settings:privacy-general', ['Change it in Windows Settings'], [], null, 'Ads ID is on']);
  const withGuide = FIX.planView(manual, null, null, { guide: ['Open Privacy & security', 'Turn off “Let apps show me personalised ads”'] });
  assert.deepEqual(withGuide.steps.map((s) => s.label), ['Open Privacy & security', 'Turn off “Let apps show me personalised ads”']);
  const done = FIX.planView(manual, null, null, { recheck: 'verified_by_rescan' });
  assert.deepEqual([done.state, done.steps.length, done.steps[0].state, done.steps[0].receipt], ['verified', 1, 'done', 'You changed it in Settings.']);
  assert.equal(FIX.planView(manual, null, null, { recheck: 'awaiting_rescan' }).state, 'guided');
  // The fixture's (and an older host's) manual route: an ineligible manual_only plan is guided too, never Can't run.
  const mo = FIX.planView(single({ status: 'ineligible', ineligible: { code: 'manual_only', message: 'Change it in Settings', manualUri: 'ms-settings:display' } }), null);
  assert.deepEqual([mo.state, mo.planSettingsUri], ['guided', 'ms-settings:display']);
  assert.equal(FIX.planView(single({ status: 'ineligible', ineligible: { code: 'handler_off', message: 'Change it in Windows Security', manualUri: 'windowsdefender://network' } }), null).state, 'cantrun');
  // The card: badge, the accent note, no state words, Back / Close / Check again / Open settings (primary).
  let rechecked = 0;
  const card = OT.ApprovalCard({ title: 'Turn on Controlled folder access', state: 'guided', onBack: noop, onClose: noop, onCheckAgain: () => rechecked++, onOpenPlanSettings: noop,
    steps: [{ label: 'Turn on Controlled folder access', does: 'Windows Security › Virus & threat protection › Ransomware protection.' }] });
  const t = text(card);
  assert.ok(t.includes('You do this one') && t.includes('You do this one in Windows Settings. Open settings takes you to the right page. Come back and press Check again.'));
  assert.equal(nodes(card).filter((n) => /ot-plan-state/.test(n.props.className || '')).length, 0);
  assert.deepEqual(buttons(card), ['Back', 'Close', 'Check again', 'Open settings']);
  const prim = nodes(card).filter((n) => n.type === 'button' && text(n).trim() === 'Open settings')[0];
  assert.match(prim.props.className, /primary/);
  nodes(card).filter((n) => n.type === 'button' && text(n).trim() === 'Check again')[0].props.onClick();
  assert.equal(rechecked, 1);
  assert.ok(text(OT.ApprovalCard({ title: 'T', state: 'guided', guideText: 'Its own guide.', onOpenPlanSettings: noop, steps: [{ label: 'A' }] })).includes('You do this one in Windows Settings. Its own guide.'));
});

test('6: a script that didn’t start because the Windows prompt closed, or the helper refused it', () => {
  const say = (cause) => text(OT.ScriptApproval({ title: 'Clean temp', state: 'notstarted', notStartedCause: cause, script: 'Write-Output 1', onClose: noop }));
  assert.ok(say('declined').includes('You closed the Windows prompt, so it didn’t run.'));
  assert.ok(say('helper').includes('Windows didn’t allow it to run as administrator.'));
});

test('7: tile status words: warnings get the alert mark and bold; Not installed and Connected stay plain', () => {
  const tile = (status) => OT.ProviderOption({ value: 'claude', title: 'Claude', glyph: 'C', status });
  const status = (t) => nodes(t).filter((n) => /ot-provider-status/.test(n.props.className || ''))[0];
  for (const w of ['Couldn’t check', 'Update needed', 'Signed out', 'Blocked on this PC', 'Needs repair', 'Limit reached', 'No credit', 'Key rejected', 'Offline', 'Not available for this account']) {
    const s = status(tile(w));
    assert.match(s.props.className, /is-warn/, w);
    assert.equal(text(s), w);
  }
  for (const w of ['Not installed', 'Connected']) assert.doesNotMatch(status(tile(w)).props.className, /is-warn/, w);
  assert.match(status(tile({ text: 'Anything', warn: true })).props.className, /is-warn/);
  // Every word the app's helper gives is one of the design's.
  const designed = ['Not installed', 'Connected', 'Couldn’t check', 'Update needed', 'Signed out', 'Blocked on this PC', 'Needs repair', 'Limit reached', 'No credit', 'Key rejected', 'Offline', 'Not available for this account'];
  const st = (kind, status, detail) => CS.connectionState({ kind, entry: { kind, status, detail } });
  const words = [];
  for (const d of ['auth', 'quota', 'not_eligible', 'network', 'unavailable', 'storage', 'cancelled', 'callback_timeout']) {
    words.push(CS.tileStatus('chatgpt', null, st('chatgpt_plan', 'error', d)), CS.tileStatus('api-key', null, st('anthropic_api', 'error', d)));
  }
  for (const b of ['policy', 'access_denied', 'node_missing', 'unsupported_path', 'launch_failed']) words.push(CS.tileStatus('claude', { known: true, installed: true, blocked: true, blockedReason: b }, null));
  words.filter(Boolean).forEach((w) => assert.ok(designed.includes(w), w));
});

test('8: the deck shows new, unseen check-up cards first; the host’s order is kept for the rest; a card stays in front once seen', () => {
  const a = CS.deckOrder(['c1', 'c2', 'ck-new'], [], ['ck-new']);
  assert.deepEqual(a, { order: ['ck-new', 'c1', 'c2'], front: ['ck-new'] });
  // Seen now (no longer fresh): it stays at the front, nothing jumps back.
  assert.deepEqual(CS.deckOrder(['c1', 'c2', 'ck-new'], a.front, []).order, ['ck-new', 'c1', 'c2']);
  // A new card the deck doesn't show can't move (the host sends three and acts only on those).
  assert.deepEqual(CS.deckOrder(['c1', 'c2', 'c3'], [], ['ck-later']), { order: ['c1', 'c2', 'c3'], front: [] });
  assert.deepEqual(CS.deckOrder(['c1', 'ck-a', 'ck-b'], [], ['ck-b', 'ck-a']).order, ['ck-b', 'ck-a', 'c1']);
});

test('9: repair while signed in: step 2 reads “Sign in after the repair” with no button', () => {
  const tree = OT.ProviderDetails({ provider: { value: 'claude' }, tileState: { row: 'connected' },
    client: { note: 'repair', known: true, installed: true, outdated: false, signedIn: true, registered: 'yes', onInstall: noop, onSignIn: noop, onCheck: noop } });
  const t = text(tree);
  assert.ok(t.includes('Needs repair') && t.includes('Sign in after the repair'), t);
  assert.ok(!buttons(tree).includes('Sign in') && !buttons(tree).includes('Signed in'), buttons(tree).join('|'));
});

test('v55: the check-up partial chip names what was missed (1–2) or what was checked (3–4)', () => {
  const chip = (missed) => text(OT.ToolbarStatus({ checkupStatus: 'partial', missed }));
  assert.equal(chip(['hardware']), 'Checked online, except hardware. It will try those again later.');
  assert.equal(chip(['windows', 'background']), 'Checked online, except Windows and background items. It will try those again later.');
  assert.equal(chip(['hardware', 'drivers', 'windows']), 'Only checked programs and background items online. It will try the rest again later.');
  assert.equal(chip(['hardware', 'drivers', 'windows', 'programs']), 'Only checked background items online. It will try the rest again later.');
  assert.equal(chip(undefined), 'Checked online, except some areas. It will try those again later.');
  // From research.get's check-up: keys in the design's order; all five → the failed chip.
  const view = (missed) => CS.toolbarChip({ status: 'done', checkup: { status: 'partial', lastAt: null, cards: [], missed } });
  assert.equal(text(OT.ToolbarStatus(view(['programs', 'drivers']))), 'Checked online, except drivers and programs. It will try those again later.');
  assert.equal(text(OT.ToolbarStatus(view(['hardware', 'drivers', 'windows', 'programs', 'background']))), 'Couldn’t finish the online check-up. It will try again later.');
});

test('v55: the web permission line', () => {
  const src = fs.readFileSync(new URL('../js/components.js', import.meta.url), 'utf8');
  assert.ok(src.includes('Sends the names, makers and versions of what’s on this PC · uses your AI plan'));
  assert.ok(!src.includes('Sends the names and versions of your hardware, Windows and software'));
});

/* ---------- The fixture can show each new state (dev hooks: setLongScan, failSingle, setScriptRun declined|helper) ---------- */
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

test('fixture: a long Defender scan reports its running minutes (design v57) and Stop ends it (the card reads Stopped)', async () => {
  const be = setup();
  const report = await scannedReport(be);
  const fid = report.findings.find((f) => f.ruleId === 'av.signatures_stale').id;
  assert.deepEqual(await be.call('fixture.setLongScan', {}), { long: true });
  const plan = await be.call('action.preview', { idempotencyKey: key(), actionId: 'defender.quick_scan', findingId: fid });
  const at2 = until(be, (e) => e.type === 'operation.state' && e.payload.planId === plan.planId && e.payload.progress === 'Quick scan · running for 2 min');
  const a = await be.call('shell.approvePlan', { planId: plan.planId });
  const ev = (await at2).payload;
  const running = FIX.planView(plan, { operationId: a.operationId, state: ev.state }, null, { progress: ev.progress });
  assert.deepEqual(running.steps[0].progress, { text: 'Quick scan · running for 2 min' });
  const end = until(be, (e) => e.type === 'operation.state' && e.payload.planId === plan.planId && FIX.isTerminal(e.payload.state));
  await be.call('operation.stop', { operationId: a.operationId });
  await end;
  const op = await be.call('operation.get', { operationId: a.operationId });
  assert.deepEqual([op.state, op.error.code, op.progress], ['failed', 'cancelled', null]);
  assert.equal(FIX.planView(plan, op).state, 'stopped');
});

test('fixture: a failed single change (Check again, Open settings); a guided task (still the same, then verified)', async () => {
  const be = setup();
  const report = await scannedReport(be);
  const fid = report.findings.find((f) => f.ruleId === 'power.high_perf_on_battery').id;
  await be.call('fixture.failSingle');
  const plan = await be.call('action.preview', { idempotencyKey: key(), actionId: 'power.select_scheme', parameters: { personality: 'balanced' }, findingId: fid });
  const end = until(be, (e) => e.type === 'operation.state' && e.payload.planId === plan.planId && FIX.isTerminal(e.payload.state));
  const a = await be.call('shell.approvePlan', { planId: plan.planId });
  await end;
  const op = await be.call('operation.get', { operationId: a.operationId });
  const v = FIX.planView(plan, op, 'ms-settings:powersleep');
  assert.deepEqual([v.state, v.steps[0].state, v.steps[0].note, v.steps[0].settings], ['failed', 'failed', 'Windows refused the change.', true]);
  // Guided: a card action the catalog doesn't have is previewed as manual.open.
  const manual = report.findings.find((f) => f.settingsUri);
  const g = await be.call('action.preview', { idempotencyKey: key(), actionId: 'manual.open', findingId: manual.id });
  assert.equal(FIX.planView(g, null).state, 'guided');
  assert.equal((await be.call('manual.recheck', { findingId: manual.id })).status, 'awaiting_rescan');
  const r2 = await be.call('manual.recheck', { findingId: manual.id });
  assert.equal(FIX.planView(g, null, null, { recheck: r2.status }).state, 'verified');
});

test('fixture: a script not started because the Windows prompt closed, or the helper refused it', async () => {
  const be = setup();
  for (const [outcome, cause] of [['declined', 'declined'], ['helper', 'helper']]) {
    await be.call('fixture.setScriptRun', { outcome });
    const r = await be.call('fixture.proposeScript', { sample: 'plain' });
    const end = until(be, (e) => e.type === 'operation.state' && e.payload.planId === r.planId && FIX.scriptEnded(e.payload.state));
    const a = await be.call('shell.approvePlan', { planId: r.planId, options: { restorePoint: false, shareOutput: true }, ticked: true, readToEnd: true });
    assert.equal(a.status, 'approved');
    await end;
    const op = await be.call('operation.get', { operationId: a.operationId });
    const plan = await be.call('action.get', { planId: r.planId });
    const v = FIX.scriptView(plan, op);
    assert.deepEqual([v.state, v.notStartedCause], ['notstarted', cause], outcome);
  }
});
