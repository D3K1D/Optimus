// node --test ui/tests/   — design Version 61 (ReportsMore): page-less guided tasks. Once done the receipt is “You made the change.”
// (a task with a Windows page keeps “You changed it in Settings.”); asked for by an AI with no guide lines, the single step is
// “Make the change yourself” with “<AI>’s answer above says how.”; the host's ManualHandler `what` with and without a page;
// guide steps one line each, and the BIOS sample's texts.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import fs from 'node:fs';
const require = createRequire(import.meta.url);
const FIX = require('../js/fix-plan.js');
const { createFixtureAdapter } = require('../js/adapter-fixture.js');

const manual = (target) => ({ planId: 'plan-m', kind: 'manual', actionId: 'manual.open', label: 'Open settings', status: 'awaiting_user', findingId: 'fnd-1',
  target, rows: [], context: [], tradeoffs: [], flags: [], privilege: 'none', undo: { supported: false, conditions: null }, ineligible: null });
const SETTINGS = manual({ type: 'settings', id: 'windowsdefender://network', label: 'Public firewall profile is off', settingsPath: 'Windows Security › Firewall & network protection' });
const PAGELESS = manual({ type: 'manual', id: 'fnd-1', label: 'Fan curve is set to silent in the BIOS' });
const BIOS_STEPS = ['Restart and open the BIOS (press Delete or F2 while the PC starts)', 'Set the CPU fan to Standard (usually under Monitor or Hardware Monitor)', 'Save and exit'];
const byAi = (plan, by) => Object.assign({}, plan, { requestedBy: { kind: 'in_app', taskId: 't-1', invocationId: null }, why: by ? { text: 'Because.', by } : null });

test('1: the receipt once done: “You made the change.” without a Windows page, “You changed it in Settings.” with one', () => {
  assert.equal(FIX.guidedReceipt(true), 'You made the change.');
  assert.equal(FIX.guidedReceipt(false), 'You changed it in Settings.');
  const done = FIX.planView(PAGELESS, null, null, { guide: BIOS_STEPS, recheck: 'verified_by_rescan' });
  assert.equal(done.state, 'verified');
  assert.deepEqual(done.steps.map((s) => [s.state, s.receipt]), [['done', null], ['done', null], ['done', 'You made the change.']]);
  const one = FIX.planView(byAi(PAGELESS, 'ChatGPT'), null, null, { recheck: 'verified_by_rescan' });
  assert.deepEqual(one.steps.map((s) => s.receipt), ['You made the change.']);
  const page = FIX.planView(SETTINGS, null, null, { recheck: 'verified_by_rescan' });
  assert.deepEqual(page.steps.map((s) => [s.label, s.receipt]), [['Change it in Windows Settings', 'You changed it in Settings.']]);
  // An ineligible manual_only change (a Windows page) keeps the Settings receipt.
  const inel = { planId: 'p', kind: 'setting', actionId: 'firewall.enable_public', status: 'ineligible', target: {}, ineligible: { code: 'manual_only', message: 'x', manualUri: 'windowsdefender://network' } };
  assert.equal(FIX.planView(inel, null, null, { recheck: 'verified_by_rescan' }).steps[0].receipt, 'You changed it in Settings.');
});

test('2: no guide lines and no page: “Make the change yourself”, “<AI>’s answer above says how.” (the requester’s name); with a page as before', () => {
  assert.deepEqual(FIX.guidedSingleStep(true, 'Claude'), { label: 'Make the change yourself', does: 'Claude’s answer above says how.' });
  assert.deepEqual(FIX.guidedSingleStep(true, 'ChatGPT'), { label: 'Make the change yourself', does: 'ChatGPT’s answer above says how.' });
  assert.deepEqual(FIX.guidedSingleStep(true, null), { label: 'Make the change yourself', does: 'The answer above says how.' }, 'v63: no AI named');
  assert.deepEqual(FIX.guidedSingleStep(false, 'Claude'), { label: 'Change it in Windows Settings', does: null });
  // The name as on the card's head and its “From <AI>” line (requesterName): why.by, Claude Code, the task's recipient.
  const v = FIX.planView(byAi(PAGELESS, 'ChatGPT'), null, null, {});
  assert.deepEqual([v.state, v.planSettingsUri, v.title], ['guided', null, 'Fan curve is set to silent in the BIOS']);
  assert.deepEqual(v.steps.map((s) => [s.label, s.does]), [['Make the change yourself', 'ChatGPT’s answer above says how.']]);
  const task = { taskId: 't-1', recipient: { kind: 'openai_api' } };
  assert.equal(FIX.planView(byAi(PAGELESS, null), null, null, { task }).steps[0].does, 'ChatGPT’s answer above says how.');
  const cc = Object.assign({}, PAGELESS, { requestedBy: { kind: 'claude_code', taskId: null, invocationId: null } });
  assert.equal(FIX.planView(cc, null, null, {}).steps[0].does, 'Claude’s answer above says how.');
  // With guide lines: the lines, one each, no AI line.
  assert.deepEqual(FIX.planView(byAi(PAGELESS, 'Claude'), null, null, { guide: BIOS_STEPS }).steps.map((s) => [s.label, s.does]), BIOS_STEPS.map((s) => [s, null]));
  // With a page and no guide lines: the single step with the Settings path.
  assert.deepEqual(FIX.planView(byAi(SETTINGS, 'Claude'), null, null, {}).steps.map((s) => [s.label, s.does]),
    [['Change it in Windows Settings', 'Windows Security › Firewall & network protection']]);
  // The page passes the task (the requester's name).
  assert.match(fs.readFileSync(new URL('../js/app-flow.js', import.meta.url), 'utf8'), /settingsPath: extra\.settingsPath \|\| null, task: task \}\);/);
});

/* ---------- the fixture ---------- */
const fixture = JSON.parse(fs.readFileSync(new URL('../fixtures/m1-simulated-audit.json', import.meta.url), 'utf8'));
const extras = JSON.parse(fs.readFileSync(new URL('../fixtures/m2-extras.json', import.meta.url), 'utf8'));
function memStore() { const m = {}; return { getItem: (k) => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); } }; }
const key = () => crypto.randomUUID();
const until = (be, pred) => new Promise((r) => { const off = be.onEvent((e) => { if (pred(e)) { off(); r(e); } }); });
async function scanned() {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  const o = await be.call('onboarding.get');
  await be.call('onboarding.save', { expectedRevision: o.revision, patch: { inventoryConsent: 'skipped', step: 'complete', provider: { value: 'api', label: 'Claude (API)' } } });
  const fin = until(be, (e) => e.type === 'scan.finished');
  await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  return { be, report: await be.call('report.get', { reportId: (await fin).payload.reportId }) };
}
const WHAT_PAGE = 'Opens the right page in Windows Settings, where you make the change yourself. Optimus checks it afterwards.';
const WHAT_NONE = 'You make this change yourself, outside Windows Settings. Optimus checks it afterwards.';

test('3: the fixture’s manual plans carry the host’s ManualHandler `what`, with a Windows page and without one', async () => {
  const { be, report } = await scanned();
  const withPage = report.findings.find((f) => f.settingsUri);
  const bios = report.findings.find((f) => f.ruleId === 'cooling.bios_fan_silent');
  const a = await be.call('action.preview', { idempotencyKey: key(), actionId: 'manual.open', findingId: withPage.id });
  const b = await be.call('action.preview', { idempotencyKey: key(), actionId: 'manual.open', findingId: bios.id });
  assert.deepEqual([a.kind, a.target.type, a.what], ['manual', 'settings', WHAT_PAGE]);
  assert.deepEqual([b.kind, b.target.type, b.what], ['manual', 'manual', WHAT_NONE]);
  // An AI asking for it (the page passes no guide lines for an assistant's plan): the page-less single step.
  const c = await be.call('fixture.requestAction', { actionId: 'manual.open', findingId: bios.id });
  assert.equal(c.what, WHAT_NONE);
  assert.deepEqual(FIX.planView(c, null, null, {}).steps.map((s) => s.label), ['Make the change yourself']);
  // The catalog row is the handler's (with a page).
  const row = (await be.call('action.catalog')).actions.find((x) => x.actionId === 'manual.open');
  assert.equal(row.what, WHAT_PAGE);
});

test('4: the BIOS sample (c17): the designer’s title, claim, evidence and one-line guide steps', async () => {
  const rule = extras.rules.find((r) => r.ruleId === 'cooling.bios_fan_silent');
  assert.equal(rule.title, 'Fan curve is set to silent in the BIOS');
  assert.equal(rule.card.key, 'c17');
  assert.equal(rule.card.text, 'Your CPU fan stays slow while the CPU runs hot.');
  assert.deepEqual(rule.card.guide, BIOS_STEPS);
  assert.ok(rule.card.guide.every((g) => !/\n/.test(g)));
  const ev = extras.collectors.flatMap((c) => c.evidence).find((e) => e.key === rule.evidence[0]);
  assert.equal(ev.display, 'During the benchmark the CPU reached 88 °C while its fan stayed at 900 RPM.');
  const { report } = await scanned();
  const bios = report.findings.find((f) => f.ruleId === 'cooling.bios_fan_silent');
  assert.equal(bios.title, 'Fan curve is set to silent in the BIOS');
  // Nothing of the earlier invented sentences is left.
  const all = JSON.stringify(extras);
  assert.ok(!all.includes('BIOS’s Silent mode') && !all.includes('CPU fan mode in the BIOS: Silent.'));
});
