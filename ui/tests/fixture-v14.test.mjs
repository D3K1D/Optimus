// node --test ui/tests/   — protocol v1.4 (§10): up to three cards, "Intended", report.list for welcome back.
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

async function setup(storage) {
  const be = createFixtureAdapter({ fixture, extras, storage: storage || memStore(), timeScale: 0.01 });
  const o = await be.call('onboarding.get');
  await be.call('onboarding.save', { expectedRevision: o.revision, patch: { uses: ['Gaming', 'Hosting / development'], inventoryConsent: 'skipped', step: 'complete' } });
  const fin = new Promise((r) => { const off = be.onEvent((e) => { if (e.type === 'scan.finished') { off(); r(e); } }); });
  await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  return { be, reportId: (await fin).payload.reportId };
}

test('followup.get carries up to two upcoming cards; any of the three can be acted on', async () => {
  const { be, reportId } = await setup();
  let fu = await be.call('followup.get', { reportId });
  assert.equal(fu.upcoming.length, 2);
  assert.notEqual(fu.upcoming[0].id, fu.card.id);
  const third = fu.upcoming[1];
  fu = await be.call('followup.respond', { idempotencyKey: key(), reportId, expectedRevision: fu.revision, scope: 'card', cardId: third.id, option: 'ignore' });
  assert.ok(![fu.card.id, ...fu.upcoming.map((c) => c.id)].includes(third.id));
  assert.equal(fu.upcoming.length, 2);          // the next queued card moved up
  assert.equal(fu.pendingCount, 8);
  const second = fu.upcoming[0];
  fu = await be.call('followup.respond', { idempotencyKey: key(), reportId, expectedRevision: fu.revision, scope: 'card', cardId: second.id, option: 'later' });
  assert.ok(!fu.upcoming.slice(0, 1).some((c) => c.id === second.id));
});

test('Intended: offered on the reviewed rules, stored like ignored, listed and restorable', async () => {
  const { be, reportId } = await setup();
  let fu = await be.call('followup.get', { reportId });
  const fw = [fu.card, ...fu.upcoming].find((c) => c.title === 'Public firewall profile is off');
  assert.ok(fw.options.includes('Intended'));
  const storage = [fu.card, ...fu.upcoming].find((c) => c.category === 'storage');
  if (storage) assert.ok(!storage.options.includes('Intended'));
  fu = await be.call('finding.setDisposition', { idempotencyKey: key(), reportId, findingId: fw.findingId, status: 'intended', scope: 'finding' });
  assert.deepEqual(fu.decisions.map((d) => d.status), ['intended']);
  await assert.rejects(be.call('finding.setDisposition', { idempotencyKey: key(), reportId, findingId: fw.findingId, status: 'intended', scope: 'rule' }),
    (e) => e.code === -32602);
  const r = await be.call('report.get', { reportId });
  assert.equal(r.findings.find((f) => f.id === fw.findingId).disposition.status, 'intended');
  fu = await be.call('finding.clearDisposition', { idempotencyKey: key(), reportId, findingId: fw.findingId });
  assert.equal(fu.card.findingId, fw.findingId);
  assert.equal(fu.decisions.length, 0);
});

test('welcome back data: report.list newest first, pending count from followup.get', async () => {
  const store = memStore();
  const { reportId } = await setup(store);
  const be2 = createFixtureAdapter({ fixture, extras, storage: store, timeScale: 0.01 });
  const o = await be2.call('onboarding.get');
  assert.equal(o.step, 'complete');
  assert.deepEqual(o.profile.uses, ['Gaming', 'Hosting / development']);
  const list = await be2.call('report.list', { limit: 1 });
  assert.equal(list.reports[0].reportId, reportId);
  assert.ok(list.reports[0].generatedAt);
  assert.equal((await be2.call('followup.get', { reportId })).pendingCount, 9);
});
