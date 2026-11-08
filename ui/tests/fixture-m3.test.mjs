// node --test ui/tests/   — the fixture adapter's M3 agent gateway simulation (contracts/m3-agent.md §4).
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
const key = () => crypto.randomUUID();
const ALL = ['power.inventory', 'process.sample', 'startup.list', 'events.recent', 'storage.volumes', 'network.listeners', 'display.modes', 'devices.problems', 'security.status'];

async function withReport(be) {
  let o = await be.call('onboarding.get');
  await be.call('onboarding.save', { expectedRevision: o.revision, patch: { uses: ['Gaming'], inventoryConsent: 'skipped', step: 'complete' } });
  const fin = waitFor(be, (e) => e.type === 'scan.finished');
  await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  return (await fin).payload.reportId;
}

test('providers: list, fake key prompt, never connected before the backend says so', async () => {
  const be = adapter();
  const { providers } = await be.call('provider.list');
  assert.deepEqual(providers.map((p) => p.kind), ['local', 'anthropic_api', 'openai_api', 'chatgpt_plan', 'claude_code_mcp', 'codex_mcp']);
  assert.equal(providers.find((p) => p.kind === 'anthropic_api').status, 'not_configured');
  assert.equal((await be.call('provider.connect', { kind: 'anthropic_api' })).status, 'not_configured');
  assert.deepEqual(await be.call('shell.requestApiKey', { kind: 'anthropic_api' }), { status: 'connected' });
  assert.equal((await be.call('provider.connect', { kind: 'anthropic_api' })).status, 'connected');
  assert.equal((await be.call('provider.disconnect', { kind: 'anthropic_api' })).status, 'not_configured');
});

test('in-app task: grant, two-step investigation with events, citations from real evidence, budgets, stop', async () => {
  const be = adapter();
  const reportId = await withReport(be);
  await be.call('shell.requestApiKey', { kind: 'anthropic_api' });
  const report = await be.call('report.get', { reportId });
  const t = await be.call('agent.start', { idempotencyKey: key(), goal: 'Why is it loud?', recipientKind: 'anthropic_api',
    scope: { reportId, findingIds: null }, grant: { diagnostics: ALL, share: { names: false } } });
  assert.match(t.taskId, /^task-[0-9a-f]{16}$/);
  assert.equal(t.status, 'active');
  const events = [];
  be.onEvent((e) => { if (e.stream === 'task:' + t.taskId) events.push(e); });
  const msg = waitFor(be, (e) => e.type === 'agent.message');
  const fu = await be.call('followup.get', { reportId });
  const r = await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'Why is my PC loud?', cardId: fu.card.id });
  assert.equal(r.taskId, t.taskId);
  const m = (await msg).payload;
  await new Promise((res) => setTimeout(res, 20));
  const tools = events.filter((e) => e.type === 'agent.tool' && e.payload.status === 'complete').map((e) => e.payload);
  assert.deepEqual(tools.map((x) => x.tool), ['get_findings', 'run_diagnostic']);
  assert.ok(tools.every((x) => x.origin === 'in_app' && x.bytes > 0));
  assert.equal(tools[1].arguments.diagnosticId, 'process.sample');
  assert.ok(report.evidence.some((e) => e.id === m.citations[0]), 'first citation is report evidence');
  assert.ok(m.citations[1].startsWith('ev-') && m.text.includes('[' + m.citations[1] + ']'));
  assert.deepEqual(events.filter((e) => e.type === 'agent.turn').map((e) => e.payload.status), ['started', 'finished']);
  const got = await be.call('agent.get', { taskId: t.taskId });
  assert.equal(got.used.diagnosticCalls, 1);
  assert.equal(got.used.turns, 1);
  assert.ok(got.used.bytesShared > 0);
  // History: the task stream's events as the host keeps them ({seq, type, at, payload}): two tool calls, the turn, the answer.
  assert.ok(got.activity.every((e) => typeof e.seq === 'number' && e.type && e.payload));
  assert.equal(new Set(got.activity.filter((e) => e.type === 'agent.tool').map((e) => e.payload.invocationId)).size, 2);
  assert.deepEqual(got.activity.filter((e) => e.type === 'agent.turn').map((e) => e.payload.status), ['started', 'finished']);
  assert.equal(got.activity.filter((e) => e.type === 'agent.message').length, 1);
  // A diagnostic the grant does not include is denied, not run.
  const t2 = await be.call('agent.start', { idempotencyKey: key(), goal: 'x', recipientKind: 'anthropic_api', scope: { reportId }, grant: { diagnostics: [] } });
  assert.equal((await be.call('agent.get', { taskId: t.taskId })).status, 'stopped');   // one active task per recipient kind
  const denied = waitFor(be, (e) => e.type === 'agent.tool' && e.payload.status === 'denied');
  await be.call('agent.ask', { idempotencyKey: key(), taskId: t2.taskId, text: 'Is the CPU busy?' });
  assert.equal((await denied).payload.arguments.diagnosticId, 'process.sample');
  await waitFor(be, (e) => e.type === 'agent.message');
  const stopped = await be.call('agent.stop', { taskId: t2.taskId });
  assert.equal(stopped.status, 'stopped');
  await assert.rejects(be.call('agent.ask', { idempotencyKey: key(), taskId: t2.taskId, text: 'again' }), (e) => e.code === -32013);
  await assert.rejects(be.call('agent.start', { idempotencyKey: key(), goal: 'x', recipientKind: 'anthropic_api', scope: { reportId }, grant: { diagnostics: ['format.disk'] } }),
    (e) => e.code === -32602);
});

test('native pairing: registration, pending code, approve, task with client-origin tool calls; no in-app ask', async () => {
  const be = adapter();
  const reportId = await withReport(be);
  const pending = waitFor(be, (e) => e.type === 'agent.pairing' && e.payload.status === 'pending');
  const c = await be.call('provider.connect', { kind: 'claude_code_mcp' });
  assert.equal(c.status, 'waiting');
  assert.match(c.registration.claude, /^claude mcp add --scope user optimization-tool -- /);
  const p = (await pending).payload;
  assert.equal(p.stream, undefined);
  assert.match(p.code, /^\d{4}$/);
  // As the host: a native client's task needs its pairing ID, and the pairing must be approved.
  await assert.rejects(be.call('agent.start', { idempotencyKey: key(), goal: 'x', recipientKind: 'claude_code_mcp', scope: { reportId }, grant: { diagnostics: ALL } }),
    (e) => e.code === -32602 && e.data.field === 'pairingId');
  await assert.rejects(be.call('agent.start', { idempotencyKey: key(), goal: 'x', recipientKind: 'claude_code_mcp', pairingId: p.pairingId, scope: { reportId }, grant: { diagnostics: ALL } }),
    (e) => e.code === -32013);
  assert.equal((await be.call('provider.list')).providers.find((x) => x.kind === 'claude_code_mcp').pairingId, p.pairingId, 'pending entry carries its pairingId');
  assert.deepEqual(await be.call('agent.approvePairing', { pairingId: p.pairingId, approve: true }), { pairingId: p.pairingId, status: 'approved' });
  const paired = (await be.call('provider.list')).providers.find((x) => x.kind === 'claude_code_mcp');
  assert.deepEqual([paired.status, paired.pairingId], ['paired', p.pairingId]);
  const t = await be.call('agent.start', { idempotencyKey: key(), goal: 'Review', recipientKind: 'claude_code_mcp', pairingId: p.pairingId, scope: { reportId }, grant: { diagnostics: ALL } });
  assert.equal(t.pairing.client, 'claude_code');
  const tool = await waitFor(be, (e) => e.type === 'agent.tool' && e.payload.status === 'complete' && e.payload.tool === 'run_diagnostic');
  assert.equal(tool.payload.origin, 'claude_code');
  await assert.rejects(be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'hi' }), (e) => e.code === -32013);
});

test('report review: preview shows field classes and bytes, submit consumes it once with validated citations', async () => {
  const be = adapter();
  const reportId = await withReport(be);
  await assert.rejects(be.call('review.preview', { recipientKind: 'anthropic_api', reportId }), (e) => e.code === -32013);
  await be.call('shell.requestApiKey', { kind: 'anthropic_api' });
  const pv = await be.call('review.preview', { recipientKind: 'anthropic_api', reportId });
  assert.ok(pv.bytes > 0 && pv.digest.length === 16);
  assert.equal(pv.fieldClasses.find((f) => f.id === 'findings').count, 9);
  assert.ok(!JSON.stringify(pv.payload).includes('14 apps start'), 'local evidence is not shared');
  const k = key();
  const out = await be.call('review.submit', { idempotencyKey: k, previewId: pv.previewId });
  assert.ok(out.lines.length >= 3);
  const shared = new Set(pv.payload.findings.flatMap((f) => f.evidence.map((e) => e.id)));
  assert.ok(out.lines.every((l) => l.evidenceIds.every((id) => shared.has(id))));
  assert.deepEqual(await be.call('review.submit', { idempotencyKey: k, previewId: pv.previewId }), out);   // replay
  await assert.rejects(be.call('review.submit', { idempotencyKey: key(), previewId: pv.previewId }), (e) => e.code === -32013);
});

test('grant sheet inputs as the host: provider.list diagnostics, capabilities.tools, explain-only refuses tools', async () => {
  const be = adapter();
  const reportId = await withReport(be);
  const list = await be.call('provider.list');
  assert.deepEqual(list.diagnostics.map((d) => d.id), ALL);
  await be.call('shell.requestApiKey', { kind: 'anthropic_api' });
  assert.equal(list.providers.find((p) => p.kind === 'anthropic_api').capabilities.tools, true);
  assert.equal((await be.call('fixture.setTools', { kind: 'anthropic_api', tools: false })).capabilities.tools, false);
  await assert.rejects(be.call('agent.start', { idempotencyKey: key(), goal: 'x', recipientKind: 'anthropic_api', scope: { reportId }, grant: { diagnostics: ['process.sample'] } }),
    (e) => e.code === -32013 && e.data.reason === 'tools_unavailable');
  const t = await be.call('agent.start', { idempotencyKey: key(), goal: 'x', recipientKind: 'anthropic_api', scope: { reportId }, grant: { diagnostics: [] } });
  assert.equal(t.status, 'active');
});

test('disconnecting a native client denies its open pairing and says so (agent.pairing)', async () => {
  const be = adapter();
  await be.call('fixture.setProviderState', { kind: 'claude_code_mcp', status: 'pending' });
  const p = (await waitFor(be, (e) => e.type === 'agent.pairing' && e.payload.status === 'pending')).payload;
  const denied = waitFor(be, (e) => e.type === 'agent.pairing' && e.payload.status === 'denied');
  await be.call('provider.disconnect', { kind: 'claude_code_mcp' });
  assert.equal((await denied).payload.pairingId, p.pairingId);
  const entry = (await be.call('provider.list')).providers.find((x) => x.kind === 'claude_code_mcp');
  assert.equal(entry.pairingId, undefined);
});
