// node --test ui/tests/   — design Version 56 (GuidedAndReports): a guided Check again with nothing changed, the guided step
// rule, report-only results, the host's undo and progress words in the fixture, and followup.front for the check-up chip.
// Pure helpers (fix-plan.js, connection-state.js) and the components rendered with a tiny React stub.
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
const button = (tree, label) => nodes(tree).filter((n) => n.type === 'button' && text(n).trim() === label)[0];
const byClass = (tree, re) => nodes(tree).filter((n) => re.test((n.props && n.props.className) || ''));
const noop = () => {};

test('1: guided Check again: “Checked again. Nothing has changed yet.” in the guided note (a status region); Checking… disabled', () => {
  const props = { title: 'Controlled folder access is off', state: 'guided', onBack: noop, onCheckAgain: noop, onOpenPlanSettings: noop,
    steps: [{ label: 'Change it in Windows Settings', does: 'Windows Security › Virus & threat protection › Ransomware protection' }] };
  const plain = OT.ApprovalCard(props);
  const region = byClass(plain, /ot-plan-recheck/)[0];
  assert.deepEqual([region.props.role, text(region)], ['status', '']);   // present and empty, so the line is announced when it fills
  const same = OT.ApprovalCard(Object.assign({}, props, { stillSame: true }));
  const r2 = byClass(same, /ot-plan-recheck/)[0];
  assert.equal(text(r2), 'Checked again. Nothing has changed yet.');
  assert.ok(byClass(same, /ot-plan-warn is-guide/)[0] && text(byClass(same, /is-guide/)[0]).includes('Checked again. Nothing has changed yet.'));
  const checking = OT.ApprovalCard(Object.assign({}, props, { checking: true }));
  assert.deepEqual(buttons(checking), ['Back', 'Checking…', 'Open settings']);
  assert.equal(button(checking, 'Checking…').props.disabled, true);
  assert.equal(button(plain, 'Check again').props.disabled, undefined);
});

test('2: the guided step rule: no guide lines → “Change it in Windows Settings”, the path as does only when known; done → one-step Verified', () => {
  const manual = { planId: 'plan-m', kind: 'manual', actionId: 'manual.open', label: 'Open settings', status: 'awaiting_user', findingId: 'fnd-1',
    target: { type: 'settings', id: 'windowsdefender://threat', label: 'Controlled folder access is off' }, undo: { supported: false } };
  const v = FIX.planView(manual, null);
  assert.deepEqual([v.title, v.steps.length, v.steps[0].label, v.steps[0].does], ['Controlled folder access is off', 1, 'Change it in Windows Settings', null]);
  assert.ok(v.steps.every((s) => s.label !== v.title), 'never the finding title as the step');
  const path = 'Windows Security › Virus & threat protection › Ransomware protection';
  assert.equal(FIX.planView(manual, null, null, { settingsPath: path }).steps[0].does, path);
  assert.deepEqual(FIX.planView(manual, null, null, { guide: ['Open Start settings.', 'Turn off recommendations.'] }).steps.map((s) => [s.label, s.does]),
    [['Open Start settings.', null], ['Turn off recommendations.', null]]);
  const done = FIX.planView(manual, null, null, { recheck: 'verified_by_rescan', guide: ['A', 'B'] });
  // Design v59: a card with its own lines keeps them once done (rebuild-v59 covers it); without lines, the one step.
  assert.deepEqual([done.state, done.steps.map((s) => [s.label, s.state, s.receipt])], ['verified', [['A', 'done', null], ['B', 'done', 'You changed it in Settings.']]]);
  const done1 = FIX.planView(manual, null, null, { recheck: 'verified_by_rescan' });
  assert.deepEqual(done1.steps.map((s) => [s.label, s.state, s.receipt]), [['Change it in Windows Settings', 'done', 'You changed it in Settings.']]);
  // As the design's demo card: the receipt shows in place of does, the step reads Verified.
  const card = OT.ApprovalCard({ title: v.title, state: done1.state, onClose: noop, steps: done1.steps });
  assert.ok(text(card).includes('You changed it in Settings.') && text(card).includes('Verified'));
  assert.deepEqual(buttons(card), ['Close']);
});

const plan = (actionId, label) => ({ planId: 'plan-r', kind: 'provider', actionId, label, status: 'running', rows: [{ label: 'Windows files', before: 'Not checked now', after: 'Checked, repaired where possible' }],
  context: [], tradeoffs: [], flags: ['restart', 'long'], privilege: 'admin', undo: { supported: false, conditions: null }, ineligible: null, findingId: 'fnd-1' });
const op = (state, result, more) => Object.assign({ operationId: 'op-r', planId: 'plan-r', state, result, receipt: state === 'completed' ? { receiptId: 'rcpt-1', completedAt: '2026-11-12T10:00:00Z', observed: { result }, text: 'x', benefit: 'unmeasured' } : null,
  undo: { available: false, reason: 'not_supported' }, error: state === 'failed' ? { code: 'tool_failed', message: 'The repair tool reported a failure.' } : null }, more);

test('5: report-only results map to the design’s keys; names without a key show the plain Finished state; other failures stay Failed', () => {
  const SFC = plan('repair.sfc', 'Check and repair Windows files'), DISM = plan('repair.dism_restorehealth', 'Repair the Windows component store'), DNS = plan('network.flush_dns', 'Clear the DNS cache');
  // Design v59: the key alone; the card takes the tone from it.
  const cases = [[SFC, op('completed', 'no_violations'), { key: 'sfcClean' }], [SFC, op('completed', 'repaired'), { key: 'sfcRepaired' }],
    [SFC, op('completed', 'could_not_repair'), { key: 'sfcPartial' }], [DISM, op('completed', 'healthy'), { key: 'dismHealthy' }],
    [DISM, op('completed', 'repaired'), { key: 'dismRepaired' }], [DISM, op('failed', 'failed'), { key: 'dismFailed' }],
    [DNS, op('completed', 'flushed'), { key: 'dnsCleared' }]];
  for (const [p, o, want] of cases) {
    const v = FIX.planView(p, o);
    assert.deepEqual([v.state, v.result, v.steps], ['reported', want, [{ n: 1, label: p.label }]], p.actionId + ' ' + o.result);
  }
  // The result read from the receipt when the event-built operation has no `result` field.
  assert.deepEqual(FIX.planView(SFC, op('completed', undefined, { receipt: { observed: { result: 'repaired' } } })).result, { key: 'sfcRepaired' });
  // No key from the designer: Finished, no sentence (v59 gave sfc / DISM finished and explorer.restart their keys).
  for (const [p, r] of [[SFC, 'something_new'], [plan('explorer.restart', 'Restart Explorer'), 'something_new'], [plan('defender.quick_scan', 'Quick scan'), 'no_threats']]) {
    const v = FIX.planView(p, op('completed', r));
    assert.deepEqual([v.state, v.result], ['reported', null], r);
  }
  // sfc's own failure and a failed DNS flush are the designed Failed card; a stopped repair is Stopped.
  assert.equal(FIX.planView(SFC, op('failed', 'failed')).state, 'failed');
  assert.equal(FIX.planView(DNS, op('failed', 'failed', { error: { code: 'failed', message: 'x' } })).state, 'failed');
  assert.equal(FIX.planView(DISM, op('failed', 'stopped', { error: { code: 'cancelled', message: 'Stopped.' } })).state, 'stopped');
  assert.equal(FIX.isTerminal('completed'), true);
});

test('5: the reported card: Finished / Finished with problems (never Verified), the tinted sentence, no state words, Close and Check again', () => {
  const card = (result, extra) => OT.ApprovalCard(Object.assign({ title: 'Check Windows files', state: 'reported', result, onClose: noop, onCheckAgain: noop, steps: [{ label: 'Check Windows files' }] }, extra));
  const SAY = { sfcClean: 'Windows checked its files and found nothing wrong.', sfcRepaired: 'Windows found damaged files and repaired them.',
    sfcPartial: 'Windows found damaged files but couldn’t repair some of them. Repairing the Windows image may help.',
    dismHealthy: 'The Windows image is healthy. Nothing needed repair.', dismRepaired: 'Windows repaired its image. Check Windows files again to finish.',
    dismFailed: 'Windows couldn’t repair its image.', dnsCleared: 'The DNS cache is cleared. Websites will look up their addresses again.' };
  for (const [k, sentence] of Object.entries(SAY)) {
    const tone = k === 'sfcPartial' || k === 'dismFailed' ? 'problem' : 'ok';
    const c = card({ key: k, tone }), t = text(c);
    assert.ok(t.includes(sentence), k);
    assert.ok(t.includes(tone === 'problem' ? 'Finished with problems' : 'Finished') && !t.includes('Verified'), k);
    const note = byClass(c, /ot-plan-warn/)[0];
    assert.match(note.props.className, tone === 'problem' ? /is-result/ : /is-ok/);
    assert.equal(note.props.role, 'status');
  }
  const c = card({ key: 'sfcClean', tone: 'ok' });
  assert.equal(byClass(c, /ot-plan-state/).length, 0);
  assert.deepEqual(buttons(c), ['Close', 'Check again']);
  assert.match(byClass(c, /ot-approval/)[0].props.className, /is-reported/);
  const busy = card({ key: 'sfcClean', tone: 'ok' }, { checking: true });
  assert.deepEqual(buttons(busy), ['Close', 'Checking…']);
  assert.equal(button(busy, 'Checking…').props.disabled, true);
  // No result (a name without a key): Finished, no note.
  const bare = card(null);
  assert.ok(text(bare).includes('Finished') && byClass(bare, /ot-plan-warn/).length === 0);
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

test('fixture 3/4: the undo conditions and progress lines are the host’s new sentences', async () => {
  const be = setup();
  const report = await scannedReport(be);
  const fid = report.findings.find((f) => f.ruleId === 'power.high_perf_on_battery').id;
  const p = await be.call('action.preview', { idempotencyKey: key(), actionId: 'power.select_scheme', parameters: { personality: 'balanced' }, findingId: fid });
  assert.equal(p.undo.conditions, 'You can undo this while Balanced is still the active plan and High performance is still installed.');
  const v = FIX.planView(p, null);
  assert.equal(v.steps[0].undoNote, p.undo.conditions);   // passed through as it is
  const t = text(OT.ApprovalCard({ title: 'x', state: 'awaiting', onApprove: noop, onDecline: noop, steps: v.steps }));
  assert.ok(t.includes(p.undo.conditions));
  // Progress (design v57): the Defender scans and the update in running minutes, no percent; the repairs' verbs in percent.
  const sfid = report.findings.find((f) => f.ruleId === 'av.signatures_stale').id;
  const seen = [];
  be.onEvent((e) => { if (e.type === 'operation.state' && e.payload.progress) seen.push(e.payload.progress); });
  for (const actionId of ['defender.quick_scan', 'defender.full_scan', 'defender.update_signatures', 'repair.sfc', 'repair.dism_restorehealth']) {
    const pl = await be.call('action.preview', { idempotencyKey: key(), actionId, findingId: sfid });
    const end = until(be, (e) => e.type === 'operation.state' && e.payload.planId === pl.planId && FIX.isTerminal(e.payload.state));
    await be.call('shell.approvePlan', { planId: pl.planId });
    await end;
  }
  for (const w of ['Quick scan · just started', 'Quick scan · running for 1 min', 'Full scan · just started', 'Full scan · running for 1 min', 'Updating virus definitions',
    'Updating virus definitions · running for 1 min', 'Checking Windows files · 40%', 'Repairing the Windows image · 40%'])
    assert.ok(seen.includes(w), w + ' in ' + JSON.stringify(seen));
  assert.ok(!seen.some((s) => /^Scanning/.test(s)));
  assert.deepEqual(FIX.stepProgress('Repairing the Windows image · 40%'), { text: 'Repairing the Windows image', pct: 40 });
});

test('fixture 5: a report-only action from the assistant ends completed; the card is reported with the result; setReportResult picks it', async () => {
  const be = setup();
  const report = await scannedReport(be);
  const fid = report.findings[0].id;
  for (const [actionId, result, want] of [['repair.sfc', null, 'sfcClean'], ['repair.sfc', 'could_not_repair', 'sfcPartial'], ['repair.dism_restorehealth', 'failed', 'dismFailed'],
    ['network.flush_dns', null, 'dnsCleared'], ['repair.dism_restorehealth', 'finished', 'dismUnknown']]) {
    if (result) await be.call('fixture.setReportResult', { result });
    const pl = await be.call('fixture.requestAction', { actionId, findingId: fid });
    assert.deepEqual([pl.requestedBy.kind, pl.status, pl.findingId], ['in_app', 'awaiting_user', fid]);
    const end = until(be, (e) => e.type === 'operation.state' && e.payload.planId === pl.planId && FIX.isTerminal(e.payload.state));
    const a = await be.call('shell.approvePlan', { planId: pl.planId });
    const ev = (await end).payload;
    // From the event alone (the page re-reads it afterwards): the state, and the result from the receipt or operation.get.
    const o = await be.call('operation.get', { operationId: a.operationId });
    const v = FIX.planView(pl, o);
    assert.deepEqual([v.state, v.result && v.result.key], ['reported', want], actionId + ' ' + result + ' (event ' + ev.state + ')');
  }
  // A guided recheck answers awaiting first, then verified (the card's Check again).
  assert.equal((await be.call('manual.recheck', { findingId: fid })).status, 'awaiting_rescan');
});

test('fixture 8: followup.front moves the named cards, in that order, to the front; unknown and decided ids are ignored', async () => {
  const be = setup();
  const report = await scannedReport(be);
  const rid = report.reportId;
  let fu = await be.call('followup.get', { reportId: rid });
  const order = (v) => [v.card.id].concat(v.upcoming.map((c) => c.id));
  // Decide the first card, so it is no longer in the queue.
  const decided = fu.card.id;
  fu = await be.call('followup.respond', { idempotencyKey: key(), reportId: rid, expectedRevision: fu.revision, scope: 'card', cardId: decided, option: 'later' });
  const before = fu;
  // The third and second cards on screen, plus an unknown id and the decided one: only the two move, in the order asked.
  assert.ok(before.pendingCount >= 4);
  const ids = order(before);
  const v = await be.call('followup.front', { reportId: rid, cardIds: ['card-nope', decided, ids[2], ids[1]] });
  assert.deepEqual(order(v).slice(0, 2), [ids[2], ids[1]]);
  assert.equal(v.pendingCount, before.pendingCount);
  assert.equal(v.revision, before.revision + 1);
  assert.deepEqual(Object.keys(v).sort(), Object.keys(before).sort());
  assert.deepEqual(v, await be.call('followup.get', { reportId: rid }));
  // Already in front: nothing changes, not even the revision.
  const again = await be.call('followup.front', { reportId: rid, cardIds: [ids[2], ids[1]] });
  assert.equal(again.revision, v.revision);
  // Bad params, as the host's Followups.Front checks them: reportId first, then cardIds 1 to 8 strings; an unknown report.
  for (const bad of [{ reportId: rid, cardIds: Array.from({ length: 9 }, (_, i) => 'c' + i) }, { reportId: rid, cardIds: [1] }, { reportId: rid }, { reportId: rid, cardIds: [] }])
    await assert.rejects(be.call('followup.front', bad), (e) => e.code === -32602 && e.data.field === 'cardIds');
  for (const bad of [{ cardIds: [ids[1]] }, { reportId: 7, cardIds: [ids[1]] }, { reportId: 'r'.repeat(65), cardIds: [ids[1]] }, { cardIds: [] }])
    await assert.rejects(be.call('followup.front', bad), (e) => e.code === -32602 && e.data.field === 'reportId');
  await assert.rejects(be.call('followup.front', { reportId: 'rep-nope', cardIds: ['c1'] }), (e) => e.code === -32001);
});

test('fixture 8: a card beyond the visible three comes to the front through followup.front', async () => {
  const be = setup();
  const report = await scannedReport(be);
  const rid = report.reportId;
  const fu = await be.call('followup.get', { reportId: rid });
  // Walk the queue with Later to find a card that is not among the three on screen, then put everything back.
  const visible = [fu.card.id].concat(fu.upcoming.map((c) => c.id));
  let cur = fu; const later = [];
  while (cur.card && later.length < 3) { later.push(cur.card.id); cur = await be.call('followup.respond', { idempotencyKey: key(), reportId: rid, expectedRevision: cur.revision, scope: 'card', cardId: cur.card.id, option: 'later' }); }
  const hidden = cur.card && visible.indexOf(cur.card.id) < 0 ? cur.card.id : null;
  assert.ok(hidden, 'a fourth card exists');
  for (const id of later) cur = await be.call('followup.respond', { idempotencyKey: key(), reportId: rid, expectedRevision: cur.revision, scope: 'card', cardId: id, option: 'restore' });
  const v = await be.call('followup.front', { reportId: rid, cardIds: [hidden] });
  assert.equal(v.card.id, hidden);
});

test('8: the chip’s Show sends followup.front; an older host (-32601) or any failure falls back to the local order (null)', async () => {
  const calls = [];
  const view = { reportId: 'rep-1', card: { id: 'c3' }, upcoming: [] };
  const ok = (m, p) => { calls.push([m, p]); return Promise.resolve(view); };
  assert.equal(await CS.frontCards(ok, 'rep-1', ['c3', 'c4', 'c3']), view);
  assert.deepEqual(calls, [['followup.front', { reportId: 'rep-1', cardIds: ['c3', 'c4'] }]]);
  // At most 8 ids.
  calls.length = 0;
  await CS.frontCards(ok, 'rep-1', Array.from({ length: 12 }, (_, i) => 'c' + i));
  assert.equal(calls[0][1].cardIds.length, 8);
  // -32601 (an older host), another error, or a thrown call: null, so the page keeps today's behaviour.
  const old = () => Promise.reject({ code: -32601, message: 'method not found' });
  assert.equal(await CS.frontCards(old, 'rep-1', ['c3']), null);
  assert.equal(await CS.frontCards(() => Promise.reject({ code: -32001 }), 'rep-1', ['c3']), null);
  assert.equal(await CS.frontCards(() => { throw new Error('x'); }, 'rep-1', ['c3']), null);
  // Nothing to send: no call.
  calls.length = 0;
  assert.equal(await CS.frontCards(ok, 'rep-1', []), null);
  assert.equal(await CS.frontCards(ok, null, ['c3']), null);
  assert.equal(calls.length, 0);
  // A view for another report is not used.
  assert.equal(await CS.frontCards(() => Promise.resolve({ reportId: 'rep-2' }), 'rep-1', ['c3']), null);
  // Against the fixture: the real method answers with the followup view.
  const be = setup();
  const report = await scannedReport(be);
  const fu = await be.call('followup.get', { reportId: report.reportId });
  const id = fu.upcoming[1].id;
  const v = await CS.frontCards((m, p) => be.call(m, p), report.reportId, [id]);
  assert.equal(v.card.id, id);
});
