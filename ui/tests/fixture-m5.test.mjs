// node --test ui/tests/   — the fixture's M5 stand-ins (contracts/m5-chat.md): signed-in clients, headless asks, new tools.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { createFixtureAdapter } = require('../js/adapter-fixture.js');
const fixture = JSON.parse(readFileSync(new URL('../fixtures/m1-simulated-audit.json', import.meta.url), 'utf8'));
const extras = JSON.parse(readFileSync(new URL('../fixtures/m2-extras.json', import.meta.url), 'utf8'));

function memStore() { const m = {}; return { getItem: (k) => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); } }; }
function adapter() { return createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 }); }
function waitFor(be, pred) { return new Promise((r) => { const off = be.onEvent((e) => { if (pred(e)) { off(); r(e); } }); }); }
const key = () => crypto.randomUUID();
const entry = async (be, kind) => (await be.call('provider.list')).providers.find((p) => p.kind === kind);

async function withReport(be) {
  const o = await be.call('onboarding.get');
  await be.call('onboarding.save', { expectedRevision: o.revision, patch: { uses: ['Gaming'], inventoryConsent: 'skipped', step: 'complete' } });
  const fin = waitFor(be, (e) => e.type === 'scan.finished');
  await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  return (await fin).payload.reportId;
}

test('clientStatus reports signedIn and moves the kind: connected, signed_out, not set up', async () => {
  const be = adapter();
  assert.equal((await entry(be, 'claude_code_mcp')).status, 'waiting');            // nothing reported yet
  const st = await be.call('shell.clientStatus', { client: 'claude_code' });
  assert.equal(st.signedIn, true);
  assert.equal((await entry(be, 'claude_code_mcp')).status, 'connected');
  await be.call('fixture.setClient', { client: 'claude_code', signedIn: false });
  assert.deepEqual([(await entry(be, 'claude_code_mcp')).status, (await entry(be, 'claude_code_mcp')).detail], ['error', 'signed_out']);
  assert.deepEqual(await be.call('shell.signInClient', { client: 'claude_code' }), { status: 'started' });
  await new Promise((r) => setTimeout(r, 60));
  await be.call('shell.clientStatus', { client: 'claude_code' });                    // the UI re-checks
  assert.equal((await entry(be, 'claude_code_mcp')).status, 'connected');
  const cx = await be.call('shell.clientStatus', { client: 'codex' });
  assert.equal(cx.installed, false);
  assert.deepEqual([(await entry(be, 'codex_mcp')).status, (await entry(be, 'codex_mcp')).detail], ['waiting', 'never_seen']);
  assert.equal((await be.call('shell.signInClient', { client: 'codex' })).status, 'failed');
});

test('headless task: no pairing, same events as an in-app turn with the client origin; first look notes and adds cards', async () => {
  const be = adapter();
  const reportId = await withReport(be);
  await be.call('shell.clientStatus', { client: 'claude_code' });
  const t = await be.call('agent.start', { idempotencyKey: key(), goal: 'Look', recipientKind: 'claude_code_mcp', scope: { reportId }, grant: { diagnostics: ['process.sample'] } });
  assert.equal(t.recipient.mode, 'headless');
  const events = [];
  be.onEvent((e) => { if (e.stream === 'task:' + t.taskId) events.push(e); });
  let msg = waitFor(be, (e) => e.type === 'agent.message');
  await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'Why is it slow?' });
  await msg;
  const tools = events.filter((e) => e.type === 'agent.tool' && e.payload.status === 'complete').map((e) => e.payload);
  assert.ok(tools.length >= 2 && tools.every((x) => x.origin === 'claude_code'));
  msg = waitFor(be, (e) => e.type === 'agent.message');
  await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'Look over my latest report, PC specs and past reports. Tell me the 3 things that matter most, and improve the suggestion cards where you can.' });
  assert.match((await msg).payload.text, /^1\. /);
  const names = events.filter((e) => e.type === 'agent.tool' && e.payload.status === 'complete').map((e) => e.payload.tool);
  for (const n of ['get_cards', 'get_pc_specs', 'get_report_history', 'note_card', 'add_card']) assert.ok(names.includes(n), n);
  const fu = await be.call('followup.get', { reportId });
  assert.equal(typeof fu.card.note, 'string');
  const all = [fu.card].concat(fu.upcoming);
  const pending = fu.pendingCount;
  assert.ok(pending >= all.length);
  // The AI card goes to the end of the deck.
  assert.ok(fu.total >= pending);
});

test('agent.cancel ends only the running turn (failed, cancelled); the task stays active and answers the next ask', async () => {
  const be = adapter();
  const reportId = await withReport(be);
  await be.call('shell.clientStatus', { client: 'claude_code' });
  const t = await be.call('agent.start', { idempotencyKey: key(), goal: 'Look', recipientKind: 'claude_code_mcp', scope: { reportId }, grant: { diagnostics: ['process.sample'] } });
  assert.deepEqual(await be.call('agent.cancel', { taskId: t.taskId }), { taskId: t.taskId, cancelled: false });
  const events = [];
  be.onEvent((e) => { if (e.stream === 'task:' + t.taskId) events.push(e); });
  const started = waitFor(be, (e) => e.type === 'agent.turn' && e.payload.status === 'started');
  await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'Why is it slow?' });
  await started;
  const failed = waitFor(be, (e) => e.type === 'agent.turn' && e.payload.status === 'failed');
  assert.deepEqual(await be.call('agent.cancel', { taskId: t.taskId }), { taskId: t.taskId, cancelled: true });
  assert.equal((await failed).payload.code, 'cancelled');
  await new Promise((r) => setTimeout(r, 200));
  assert.ok(!events.some((e) => e.type === 'agent.message'));                     // the cancelled run never answers
  assert.equal((await be.call('agent.get', { taskId: t.taskId })).status, 'active');
  const msg = waitFor(be, (e) => e.type === 'agent.message');
  await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'Why is it slow?' });
  assert.ok((await msg).payload.text.length > 0);
});

test('agent.ask writes agent.question {turnId, text, cardId} before its turn; agent.get history keeps it; a replay adds none', async () => {
  const be = adapter();
  const reportId = await withReport(be);
  await be.call('shell.clientStatus', { client: 'claude_code' });
  const t = await be.call('agent.start', { idempotencyKey: key(), goal: 'Look', recipientKind: 'claude_code_mcp', scope: { reportId }, grant: { diagnostics: ['process.sample'] } });
  const cardId = (await be.call('followup.get', { reportId })).card.id;
  let msg = waitFor(be, (e) => e.type === 'agent.message');
  const a = await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: '  Why is it slow?  ' });
  await msg;
  msg = waitFor(be, (e) => e.type === 'agent.message');
  const k = key();
  const b = await be.call('agent.ask', { idempotencyKey: k, taskId: t.taskId, text: 'And this one?', cardId });
  await be.call('agent.ask', { idempotencyKey: k, taskId: t.taskId, text: 'And this one?', cardId });
  await msg;
  const hist = (await be.call('agent.get', { taskId: t.taskId })).activity;
  const qs = hist.filter((e) => e.type === 'agent.question').map((e) => e.payload);
  assert.deepEqual(qs, [{ turnId: a.turnId, text: 'Why is it slow?', cardId: null }, { turnId: b.turnId, text: 'And this one?', cardId }]);
  for (const q of qs) {
    const at = hist.findIndex((e) => e.type === 'agent.question' && e.payload.turnId === q.turnId);
    const started = hist.findIndex((e) => e.type === 'agent.turn' && e.payload.turnId === q.turnId);
    assert.ok(at >= 0 && at < started);
  }
});

test('Later takes a card out of the deck (the last one too); Restore brings it back', async () => {
  const be = adapter();
  const reportId = await withReport(be);
  let fu = await be.call('followup.get', { reportId });
  const first = fu.card.id;
  while (fu.card) fu = await be.call('followup.respond', { idempotencyKey: key(), reportId, expectedRevision: fu.revision, scope: 'card', cardId: fu.card.id, option: 'later' });
  assert.equal(fu.pendingCount, 0);
  assert.ok(fu.decisions.length > 0 && fu.decisions.every((d) => d.status === 'later' || d.status === 'ignored' || d.status === 'snoozed'));
  fu = await be.call('followup.respond', { idempotencyKey: key(), reportId, expectedRevision: fu.revision, scope: 'card', cardId: first, option: 'restore' });
  assert.equal(fu.card.id, first);
});
