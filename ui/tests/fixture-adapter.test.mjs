// node --test ui/tests/   — the fixture adapter's M1 path and its deterministic IDs.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { createFixtureAdapter } = require('../js/adapter-fixture.js');
const fixture = JSON.parse(readFileSync(new URL('../fixtures/m1-simulated-audit.json', import.meta.url), 'utf8'));
const extras = JSON.parse(readFileSync(new URL('../fixtures/m2-extras.json', import.meta.url), 'utf8'));
const H = (...p) => createHash('sha256').update(p.join('\u0000'), 'utf8').digest('hex').slice(0, 16);

function memStore() { const m = {}; return { getItem: (k) => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); } }; }
function adapter(storage) { return createFixtureAdapter({ fixture, extras, storage: storage || memStore(), timeScale: 0.01 }); }
function waitFor(be, type) { return new Promise((r) => { const off = be.onEvent((e) => { if (e.type === type) { off(); r(e); } }); }); }
const key = () => crypto.randomUUID();

async function onboard(be, uses = ['Gaming', 'Editing']) {
  let o = await be.call('onboarding.get');
  o = await be.call('onboarding.save', { expectedRevision: o.revision, patch: { provider: { value: 'local', label: 'Local only' }, step: 'use' } });
  o = await be.call('onboarding.save', { expectedRevision: o.revision, patch: { uses, step: 'goal' } });
  o = await be.call('onboarding.save', { expectedRevision: o.revision, patch: { goals: ['Improve PC performance'], step: 'specs' } });
  return be.call('onboarding.save', { expectedRevision: o.revision, patch: { inventoryConsent: 'skipped', step: 'complete' } });
}

test('Begin scan → report with deterministic IDs, gap, and the prototype queue', async () => {
  const be = adapter();
  await onboard(be);
  const s = await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  assert.match(s.scanId, /^scan-[0-9a-f]{16}$/);
  assert.equal(s.progress.total, 6);
  const fin = await waitFor(be, 'scan.finished');
  assert.equal(fin.payload.status, 'complete_with_gaps');
  const r = await be.call('report.get', { reportId: fin.payload.reportId });
  assert.equal(r.reportId, 'report-' + H(s.scanId));
  assert.deepEqual(r.counts, { findings: 9, gaps: 1 });   // c1–c8 + the Defender definitions card (m2-extras)
  const fw = r.evidence.find((e) => e.collectorId === 'fixture.firewall');
  assert.equal(fw.id, 'ev-' + H(s.scanId, 'fixture.firewall', 'firewall.profile.public'));
  const f1 = r.findings.find((f) => f.ruleId === 'fw.public_off');
  assert.equal(f1.fingerprint, H('fw.public_off', 'firewall.profile.public'));
  assert.equal(f1.id, 'fnd-' + H(s.scanId, 'fw.public_off', f1.fingerprint));
  assert.equal(r.gaps[0].id, 'gap-' + H(s.scanId, 'fixture.fan'));
  assert.equal(r.gaps[0].status, 'timed_out');
  const fu = await be.call('followup.get', { reportId: r.reportId });
  assert.equal(fu.card.id, 'card-' + H(r.reportId, f1.id));
  assert.equal(fu.total, 9);
  // Gaming → performance cards follow security.
  const fu2 = await be.call('followup.respond', { idempotencyKey: key(), reportId: r.reportId, expectedRevision: fu.revision, scope: 'card', cardId: fu.card.id, option: 'ignore' });
  assert.equal(fu2.card.title, 'High performance on battery');
  const fu3 = await be.call('followup.respond', { idempotencyKey: key(), reportId: r.reportId, expectedRevision: fu2.revision, scope: 'report', text: 'Focus on storage first.' });
  assert.equal(fu3.card.category, 'storage');
  assert.deepEqual(fu3.decisions.map((d) => d.status), ['ignored']);
  // Card question → template reply citing the card's evidence.
  const fu4 = await be.call('followup.respond', { idempotencyKey: key(), reportId: r.reportId, expectedRevision: fu3.revision, scope: 'card', cardId: fu3.card.id, text: 'How much?' });
  assert.equal(fu4.card.reply.text, 'From the report: The system volume has 11 GiB free out of 256 GiB.');
  assert.equal(fu4.card.reply.question, 'How much?');
  assert.deepEqual(fu4.card.reply.evidenceIds, fu3.card.evidenceIds);
  // Stale revision returns the current view.
  await assert.rejects(be.call('followup.respond', { idempotencyKey: key(), reportId: r.reportId, expectedRevision: fu.revision, scope: 'report', text: 'x' }),
    (e) => e.code === -32002 && e.data.current.revision === fu4.revision);
  // Same key + same params replays; different params is a mismatch.
  const k = key(), p = { idempotencyKey: k, reportId: r.reportId, expectedRevision: fu4.revision, scope: 'card', cardId: fu4.card.id, option: 'later' };
  const a = await be.call('followup.respond', p), b = await be.call('followup.respond', p);
  assert.deepEqual(a, b);
  await assert.rejects(be.call('followup.respond', { ...p, option: 'ignore' }), (e) => e.code === -32003);
  // Restore returns an ignored card to the front.
  const fr = await be.call('followup.respond', { idempotencyKey: key(), reportId: r.reportId, expectedRevision: a.revision, scope: 'card', cardId: fu.card.id, option: 'restore' });
  assert.equal(fr.card.id, fu.card.id);
  // The card put off with Later stays decided ("later", restorable) until restored or the next scan.
  assert.deepEqual(fr.decisions.map((d) => d.status), ['later']);
});

test('targeted scan plans only its categories; cancel gives a partial report', async () => {
  const be = adapter();
  await onboard(be);
  const s = await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.storage', text: 'Where could I free up disk space?' });
  assert.deepEqual(s.progress.categories.map((c) => c.id), ['storage']);
  await assert.rejects(be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' }), (e) => e.code === -32004);
  const fin = await waitFor(be, 'scan.finished');
  const r = await be.call('report.get', { reportId: fin.payload.reportId });
  assert.deepEqual(r.findings.map((f) => f.category), ['storage']);
  assert.equal(r.status, 'complete');

  const s2 = await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  await waitFor(be, 'scan.progress');
  const c = await be.call('scan.cancel', { scanId: s2.scanId });
  assert.equal(c.status, 'cancelled');
  const r2 = await be.call('report.get', { reportId: c.reportId });
  assert.equal(r2.status, 'partial');
  assert.ok(r2.findings.length >= 1 && r2.findings.length < 8);
  assert.ok(r2.coverage.some((x) => x.status === 'cancelled'));
});

test('inventory needs consent; facts stream; starters gain the two-display starter', async () => {
  const be = adapter();
  let o = await be.call('onboarding.get');
  await assert.rejects(be.call('inventory.start', { idempotencyKey: key() }), (e) => e.code === -32013);
  o = await be.call('onboarding.save', { expectedRevision: o.revision, patch: { uses: ['Work'], inventoryConsent: 'granted' } });
  const facts = [];
  be.onEvent((e) => { if (e.type === 'inventory.fact') facts.push(e.payload.label); });
  const inv = await be.call('inventory.start', { idempotencyKey: key() });
  assert.match(inv.inventoryId, /^inv-[0-9a-f]{16}$/);
  const done = await waitFor(be, 'inventory.finished');
  assert.equal(done.payload.status, 'partial');
  assert.equal(facts[0], '8-core CPU');
  const { starters } = await be.call('starter.list', { profileRevision: o.revision });
  assert.ok(starters.length >= 10);
  assert.ok(starters.some((s) => s.id === 's12' && s.basis === 'inventory'));
  assert.ok(!starters.some((s) => s.id === 's11'));
});

test('reload resumes at the report', async () => {
  const store = memStore();
  const be = adapter(store);
  await onboard(be, ['Work']);
  const s = await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  const fin = await waitFor(be, 'scan.finished');
  const be2 = adapter(store);
  const o = await be2.call('onboarding.get');
  assert.equal(o.step, 'complete');
  assert.equal(o.resume.reportId, fin.payload.reportId);
  assert.equal(o.resume.scanId, s.scanId);
  const r = await be2.call('report.get', { reportId: o.resume.reportId });
  assert.equal(r.findings.length, 9);
});
