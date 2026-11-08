// node --test ui/tests/   — the fixture adapter's M4 simulation (contracts/m4-actions.md §4).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { createFixtureAdapter } = require('../js/adapter-fixture.js');
const fixture = JSON.parse(readFileSync(new URL('../fixtures/m1-simulated-audit.json', import.meta.url), 'utf8'));
const extras = JSON.parse(readFileSync(new URL('../fixtures/m2-extras.json', import.meta.url), 'utf8'));
function memStore() { const m = {}; return { getItem: (k) => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); } }; }
const key = () => crypto.randomUUID();
const until = (be, pred) => new Promise((r) => { const off = be.onEvent((e) => { if (pred(e)) { off(); r(e); } }); });

async function setup() {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  const o = await be.call('onboarding.get');
  await be.call('onboarding.save', { expectedRevision: o.revision, patch: { inventoryConsent: 'skipped', step: 'complete' } });
  const fin = until(be, (e) => e.type === 'scan.finished');
  await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  const reportId = (await fin).payload.reportId;
  const report = await be.call('report.get', { reportId });
  return { be, report, finding: (rule) => report.findings.find((f) => f.ruleId === rule) };
}

test('catalog, power preview → simulated approval → verified receipt → undo → drift refuses a second undo', async () => {
  const { be, finding } = await setup();
  const cat = (await be.call('action.catalog')).actions;
  assert.equal(cat.find((a) => a.actionId === 'power.select_scheme').enabled, true);
  assert.equal(cat.find((a) => a.actionId === 'firewall.enable_public').enabled, false);
  const plan = await be.call('action.preview', { idempotencyKey: key(), actionId: 'power.select_scheme', parameters: { personality: 'balanced' }, findingId: finding('power.high_perf_on_battery').id });
  assert.equal(plan.status, 'awaiting_user');
  assert.deepEqual(plan.rows, [{ label: 'Active power plan', before: 'High performance', after: 'Balanced' }]);
  assert.equal(plan.undo.supported, true);
  assert.match(plan.planHash, /^[0-9a-f]{16}$/);
  const states = [];
  be.onEvent((e) => { if (e.type === 'operation.state') states.push(e.payload.state); });
  const verified = until(be, (e) => e.type === 'operation.state' && e.payload.state === 'verified');
  const r = await be.call('shell.approvePlan', { planId: plan.planId });
  assert.equal(r.status, 'approved');
  const v = (await verified).payload;
  assert.deepEqual(states, ['prepared', 'attempted', 'verified']);
  assert.equal(v.receipt.text, 'Balanced is active, read back from Windows.');
  const op = await be.call('operation.get', { operationId: v.operationId });
  assert.equal(op.undo.available, true);
  assert.equal(r.operationId, v.operationId);
  assert.deepEqual(await be.call('shell.approvePlan', { planId: plan.planId }), { status: 'stale' });   // one approval = one plan
  // Undo is a new plan with its own approval.
  const u = await be.call('action.undo', { idempotencyKey: key(), operationId: v.operationId });
  assert.deepEqual(u.rows, [{ label: 'Active power plan', before: 'Balanced', after: 'High performance' }]);
  assert.equal(u.status, 'awaiting_user');
  await be.call('action.decline', { planId: u.planId });
  assert.equal((await be.call('action.get', { planId: u.planId })).status, 'declined');
  // Someone changed it since: undo is refused with the manual route.
  await be.call('fixture.drift');
  const u2 = await be.call('action.undo', { idempotencyKey: key(), operationId: v.operationId });
  assert.equal(u2.status, 'ineligible');
  assert.equal(u2.ineligible.code, 'changed_since');
  assert.ok(u2.ineligible.manualUri);
});

test('ineligible plans carry a reason and a manual route; Defender ops show progress; manual recheck', async () => {
  const { be, finding } = await setup();
  const fw = await be.call('action.preview', { idempotencyKey: key(), actionId: 'firewall.enable_public', findingId: finding('fw.public_off').id });
  assert.equal(fw.status, 'ineligible');
  assert.equal(fw.ineligible.manualUri, 'windowsdefender://network');
  const diag = await be.call('action.preview', { idempotencyKey: key(), actionId: 'manual.open', findingId: finding('privacy.optional_diag').id });
  assert.equal(diag.ineligible.manualUri, 'ms-settings:privacy-feedback');
  const scan = await be.call('action.preview', { idempotencyKey: key(), actionId: 'defender.quick_scan', findingId: finding('av.signatures_stale').id });
  const progress = [];
  be.onEvent((e) => { if (e.type === 'operation.state' && e.payload.progress) progress.push(e.payload.progress); });
  const done = until(be, (e) => e.type === 'operation.state' && e.payload.state === 'verified');
  await be.call('shell.approvePlan', { planId: scan.planId });
  const v = (await done).payload;
  assert.ok(progress.includes('Scanning · quick'));
  assert.match(v.receipt.text, /no threats found/);
  assert.equal((await be.call('operation.get', { operationId: v.operationId })).undo.available, false);
  const fid = finding('storage.system_low').id;
  const first = await be.call('manual.recheck', { findingId: fid });
  assert.equal(first.status, 'awaiting_rescan');
  assert.deepEqual(first.evidenceIds, finding('storage.system_low').evidenceIds);
  assert.equal((await be.call('manual.recheck', { findingId: fid })).status, 'verified_by_rescan');
});

test('agent-requested plan: only with canRequestChanges, requestedBy the assistant, user approves', async () => {
  const { be, report } = await setup();
  await be.call('shell.requestApiKey', { kind: 'anthropic_api' });
  const t = await be.call('agent.start', { idempotencyKey: key(), goal: 'loud', recipientKind: 'anthropic_api', scope: { reportId: report.reportId },
    grant: { diagnostics: ['process.sample'], canRequestChanges: true } });
  assert.equal(t.grant.canRequestChanges, true);
  const planEv = until(be, (e) => e.type === 'action.plan' && e.payload.requestedBy.kind === 'in_app');
  const tool = until(be, (e) => e.type === 'agent.tool' && e.payload.tool === 'request_action' && e.payload.status === 'complete');
  const msg = until(be, (e) => e.type === 'agent.message');
  await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'Why is it loud on battery?' });
  const pe = (await planEv).payload;
  assert.equal(pe.status, 'awaiting_user');
  assert.equal(pe.requestedBy.taskId, t.taskId);
  assert.equal((await tool).payload.planId, pe.planId);
  assert.match((await msg).payload.text, /approve it in the app/);
  const r = await be.call('shell.approvePlan', { planId: pe.planId });
  assert.equal(r.status, 'approved');
  // Without the grant, no request.
  const t2 = await be.call('agent.start', { idempotencyKey: key(), goal: 'x', recipientKind: 'anthropic_api', scope: { reportId: report.reportId }, grant: { diagnostics: [] } });
  const seen = [];
  be.onEvent((e) => { if (e.type === 'agent.tool' && e.stream === 'task:' + t2.taskId) seen.push(e.payload.tool); });
  const m2 = until(be, (e) => e.type === 'agent.message' && e.stream === 'task:' + t2.taskId);
  await be.call('agent.ask', { idempotencyKey: key(), taskId: t2.taskId, text: 'loud on battery' });
  await m2;
  assert.ok(!seen.includes('request_action'));
});

test('shell.approvePlan answers with the §8 shape: declined (UAC), unavailable, error, expired', async () => {
  const { be, finding } = await setup();
  const preview = () => be.call('action.preview', { idempotencyKey: key(), actionId: 'defender.update_signatures', findingId: finding('av.signatures_stale').id });
  let p = await preview();
  await be.call('fixture.setApproval', { status: 'declined', error: 'uac_cancelled' });
  assert.deepEqual(await be.call('shell.approvePlan', { planId: p.planId }), { status: 'declined', error: 'uac_cancelled' });
  assert.equal((await be.call('action.get', { planId: p.planId })).status, 'declined');
  p = await preview();
  await be.call('fixture.setApproval', { status: 'unavailable' });
  assert.deepEqual(await be.call('shell.approvePlan', { planId: p.planId }), { status: 'unavailable', error: null });
  await be.call('fixture.setApproval', { status: 'error' });
  assert.equal((await be.call('shell.approvePlan', { planId: p.planId })).status, 'error');
  const ok = await be.call('shell.approvePlan', { planId: p.planId });                    // Retry
  assert.equal(ok.status, 'approved');
  assert.match(ok.operationId, /^op-/);
});
