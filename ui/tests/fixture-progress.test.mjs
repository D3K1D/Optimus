// node --test ui/tests/   — task progress: usage after every counted call/turn, running → complete for each tool call.
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

test('agent.task usage grows step by step; every tool call goes running → complete', async () => {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  const o = await be.call('onboarding.get');
  await be.call('onboarding.save', { expectedRevision: o.revision, patch: { inventoryConsent: 'skipped', step: 'complete' } });
  const fin = until(be, (e) => e.type === 'scan.finished');
  await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  const reportId = (await fin).payload.reportId;
  await be.call('shell.requestApiKey', { kind: 'anthropic_api' });
  const t = await be.call('agent.start', { idempotencyKey: key(), goal: 'x', recipientKind: 'anthropic_api', scope: { reportId },
    grant: { diagnostics: ['process.sample'] } });
  const seq = [];
  be.onEvent((e) => {
    if (e.stream !== 'task:' + t.taskId) return;
    if (e.type === 'agent.task') seq.push('used:' + e.payload.used.turns + '/' + e.payload.used.diagnosticCalls + '/' + (e.payload.used.bytesShared > 0 ? 'b' : '0'));
    if (e.type === 'agent.tool') seq.push(e.payload.tool + ':' + e.payload.status + (e.payload.arguments.window ? ':' + e.payload.arguments.window : ''));
  });
  const done = until(be, (e) => e.type === 'agent.turn' && e.payload.status === 'finished');
  await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'Is the CPU busy?' });
  await done;
  const at = (x) => seq.indexOf(x);
  for (const x of ['used:1/0/0', 'get_findings:running', 'get_findings:complete', 'used:1/0/b', 'run_diagnostic:running:10', 'used:1/1/b', 'run_diagnostic:complete:10']) assert.ok(at(x) >= 0, 'missing ' + x + ' in ' + seq.join(' '));
  assert.ok(at('used:1/0/0') < at('get_findings:running'));                     // the turn counts before its first call
  assert.ok(at('get_findings:complete') < at('run_diagnostic:running:10'));
  assert.ok(at('used:1/1/b') < at('run_diagnostic:complete:10'));               // the check counts when it starts
});
