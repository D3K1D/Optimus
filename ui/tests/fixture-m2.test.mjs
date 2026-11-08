// node --test ui/tests/   — the fixture adapter's protocol v1.1 (§9) additions.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { createFixtureAdapter } = require('../js/adapter-fixture.js');
const fixture = JSON.parse(readFileSync(new URL('../fixtures/m1-simulated-audit.json', import.meta.url), 'utf8'));
const extras = JSON.parse(readFileSync(new URL('../fixtures/m2-extras.json', import.meta.url), 'utf8'));

function memStore() { const m = {}; return { getItem: (k) => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); } }; }
function adapter(storage) { return createFixtureAdapter({ fixture, extras, storage: storage || memStore(), timeScale: 0.01 }); }
function waitFor(be, pred) { return new Promise((r) => { const off = be.onEvent((e) => { if (pred(e)) { off(); r(e); } }); }); }
const finished = (be) => waitFor(be, (e) => e.type === 'scan.finished');
const key = () => crypto.randomUUID();

async function onboard(be) {
  let o = await be.call('onboarding.get');
  return be.call('onboarding.save', { expectedRevision: o.revision, patch: { uses: ['Gaming'], goals: ['Improve PC performance'], inventoryConsent: 'skipped', step: 'complete' } });
}
async function fullScan(be, extra = {}) {
  const s = await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan', ...extra });
  const fin = await finished(be);
  return { scan: s, report: await be.call('report.get', { reportId: fin.payload.reportId }) };
}

test('report categories: every planned category, with complete / partial coverage and informational evidence', async () => {
  const be = adapter();
  await onboard(be);
  const { report: r } = await fullScan(be);
  assert.deepEqual(r.categories.map((c) => c.id), ['security', 'performance', 'storage', 'privacy_telemetry', 'diagnostics', 'customization']);
  const diag = r.categories.find((c) => c.id === 'diagnostics');
  assert.equal(diag.status, 'partial');                       // the fan collector timed out
  assert.deepEqual([diag.checked, diag.planned], [1, 2]);
  assert.equal(r.categories.find((c) => c.id === 'security').status, 'complete');
  const cited = new Set(r.findings.flatMap((f) => f.evidenceIds));
  assert.ok(r.evidence.filter((e) => !cited.has(e.id)).length >= 4, 'informational evidence beyond findings');
  assert.ok(r.evidence.some((e) => e.sensitivity === 'local'));
  const fw = r.findings.find((f) => f.ruleId === 'fw.public_off');
  assert.equal(fw.subject, 'firewall.profile.public');
  assert.equal(fw.settingsUri, 'windowsdefender://network');
  assert.deepEqual(fw.counterevidence, []);
  assert.equal(fw.disposition, null);
  assert.equal(r.baseReportId, null);
  const fu = await be.call('followup.get', { reportId: r.reportId });
  assert.equal(fu.card.settingsUri, 'windowsdefender://network');
});

test('sampler: skippable countdown in progress.current, skip keeps committed samples', async () => {
  const be = adapter();
  await onboard(be);
  const s = await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.startup', text: 'Show me what starts when I sign in.' });
  const ev = await waitFor(be, (e) => e.type === 'scan.progress' && e.payload.current && e.payload.current.remainingSeconds <= 6);
  assert.equal(ev.payload.current.collectorId, 'fixture.sampler');
  assert.equal(ev.payload.current.skippable, true);
  const snap = await be.call('scan.get', { scanId: s.scanId });
  assert.equal(snap.progress.current.collectorId, 'fixture.sampler');
  await assert.rejects(be.call('scan.skipCollector', { scanId: s.scanId, collectorId: 'fixture.power' }), (e) => e.code === -32013);
  const fin = finished(be);
  await be.call('scan.skipCollector', { scanId: s.scanId, collectorId: 'fixture.sampler' });
  const r = await be.call('report.get', { reportId: (await fin).payload.reportId });
  const cov = r.coverage.find((c) => c.collectorId === 'fixture.sampler');
  assert.equal(cov.status, 'skipped');
  const sample = r.evidence.find((e) => e.collectorId === 'fixture.sampler');
  assert.ok(sample && sample.value.intervalSeconds < 8);
  assert.equal(r.categories.find((c) => c.id === 'performance').status, 'partial');
});

test('dispositions: this one / 30 days / all like this, carried into a rescan, cleared again', async () => {
  const store = memStore();
  const be = adapter(store);
  await onboard(be);
  const { report: r } = await fullScan(be);
  let fu = await be.call('followup.get', { reportId: r.reportId });
  const first = fu.card;                                       // the firewall card
  fu = await be.call('finding.setDisposition', { idempotencyKey: key(), reportId: r.reportId, findingId: first.findingId, status: 'ignored', scope: 'finding' });
  assert.notEqual(fu.card.id, first.id);
  assert.deepEqual(fu.decisions.map((d) => [d.status, d.findingId]), [['ignored', first.findingId]]);
  const second = fu.card;
  await assert.rejects(be.call('finding.setDisposition', { idempotencyKey: key(), reportId: r.reportId, findingId: second.findingId, status: 'snoozed', scope: 'finding' }),
    (e) => e.code === -32602);
  const until = new Date(Date.now() + 30 * 864e5).toISOString();
  fu = await be.call('finding.setDisposition', { idempotencyKey: key(), reportId: r.reportId, findingId: second.findingId, status: 'snoozed', scope: 'finding', expiresAt: until });
  const third = fu.card;
  fu = await be.call('finding.setDisposition', { idempotencyKey: key(), reportId: r.reportId, findingId: third.findingId, status: 'ignored', scope: 'rule' });
  assert.equal(fu.decisions.length, 3);
  const rr = await be.call('report.get', { reportId: r.reportId });
  assert.equal(rr.findings.find((f) => f.id === second.findingId).disposition.status, 'snoozed');

  // A rescan keeps them out of the queue (stored by fingerprint / rule), and compares against the base.
  const { report: r2 } = await fullScan(be, { intent: 'rescan', baseReportId: r.reportId });
  assert.equal(r2.baseReportId, r.reportId);
  assert.equal(r2.intent, 'audit.full');
  const f2 = await be.call('followup.get', { reportId: r2.reportId });
  assert.deepEqual(f2.decisions.map((d) => d.status).sort(), ['ignored', 'ignored', 'snoozed']);
  assert.equal(f2.total, f2.pendingCount);
  const titles = [first.title, second.title, third.title];
  assert.ok(!titles.includes(f2.card.title));
  // Clearing puts it back at the front, and the store forgets it for later scans.
  const back = f2.decisions.find((d) => d.title === first.title);
  const f3 = await be.call('finding.clearDisposition', { idempotencyKey: key(), reportId: r2.reportId, findingId: back.findingId });
  assert.equal(f3.card.title, first.title);
  assert.equal(f3.decisions.length, 2);
  // Persisted: a new adapter on the same storage still knows the remaining dispositions.
  const be2 = adapter(store);
  const f4 = await be2.call('followup.get', { reportId: r2.reportId });
  assert.equal(f4.decisions.length, 2);
});

test('rescan comparison: one new, one fixed; report.compare returns the findings', async () => {
  const be = adapter();
  await onboard(be);
  const { report: r } = await fullScan(be);
  const { report: r2 } = await fullScan(be, { intent: 'rescan', baseReportId: r.reportId });
  assert.equal(r2.comparison.new.length, 1);
  assert.equal(r2.comparison.resolved.length, 2);
  assert.equal(r2.comparison.unchanged, 7);
  const c = await be.call('report.compare', { reportId: r2.reportId, baseReportId: r.reportId });
  assert.equal(c.new[0].ruleId, 'storage.volume_low');
  assert.deepEqual(c.resolved.map((f) => f.ruleId).sort(), ['privacy.optional_diag', 'start.suggestions_on']);
  const priv = r2.categories.find((x) => x.id === 'privacy_telemetry');
  assert.deepEqual([priv.status, priv.findings], ['complete', 0]);
  assert.equal(c.unchanged.length, 7);
  // A plain scan of the same intent compares against the previous report too.
  const { report: r3 } = await fullScan(be);
  assert.equal(r3.baseReportId, r2.reportId);
});

test('finding.get: evidence, the rule inputs and their coverage; settings catalog', async () => {
  const be = adapter();
  await onboard(be);
  const { report: r } = await fullScan(be);
  const kp = r.findings.find((f) => f.ruleId === 'reliability.kernel_power');
  const d = await be.call('finding.get', { reportId: r.reportId, findingId: kp.id });
  assert.deepEqual(d.evidence.map((e) => e.id), kp.evidenceIds);
  assert.deepEqual(d.rule, { id: 'reliability.kernel_power', version: '1.0.0', inputs: ['fixture.kernelpower'] });
  assert.deepEqual(d.coverage.map((c) => [c.collectorId, c.status]), [['fixture.kernelpower', 'complete']]);
  await assert.rejects(be.call('finding.get', { reportId: r.reportId, findingId: 'fnd-0' }), (e) => e.code === -32001);
  assert.deepEqual(await be.call('shell.openSettings', { uri: 'ms-settings:storagesense' }), { opened: false });
  await assert.rejects(be.call('shell.openSettings', { uri: 'https://example.com' }), (e) => e.code === -32602);
});
