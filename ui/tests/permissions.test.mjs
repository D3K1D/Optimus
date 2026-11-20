// node --test ui/tests/   — M9 §1: the permissions record (fixture), the chat line, the page mapping and routing by `set`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { createFixtureAdapter } = require('../js/adapter-fixture.js');
const { permDefaults, permToggles, permRecord, permissionLine, grantFromToggles, permissionsGate, nextAfterProvider, isAiKind } = require('../js/connection-state.js');
const fixture = JSON.parse(readFileSync(new URL('../fixtures/m1-simulated-audit.json', import.meta.url), 'utf8'));
const extras = JSON.parse(readFileSync(new URL('../fixtures/m2-extras.json', import.meta.url), 'utf8'));

function memStore() { const m = {}; return { getItem: (k) => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); } }; }
function adapter(storage) { return createFixtureAdapter({ fixture, extras, storage: storage || memStore(), timeScale: 0.01 }); }
function waitFor(be, pred) { return new Promise((r) => { const off = be.onEvent((e) => { if (pred(e)) { off(); r(e); } }); }); }
const key = () => crypto.randomUUID();
const ALL = ['power.inventory', 'process.sample', 'startup.list', 'events.recent', 'storage.volumes', 'network.listeners', 'display.modes', 'devices.problems', 'security.status'];
const rec = (see, d) => ({ see: Object.assign({ specs: true, history: true, names: false }, see), do: Object.assign({ checks: true, changes: 'ask', scripts: false, web: false, admin: false }, d) });

async function withReport(be, provider) {
  const o = await be.call('onboarding.get');
  await be.call('onboarding.save', { expectedRevision: o.revision, patch: { uses: ['Gaming'], inventoryConsent: 'skipped', step: 'complete', provider: provider || { value: 'api', label: 'Claude (API)' } } });
  const fin = waitFor(be, (e) => e.type === 'scan.finished');
  await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  return (await fin).payload.reportId;
}
async function save(be, permissions, revision) {
  const cur = revision == null ? (await be.call('ai.getPermissions')).permissions.revision : revision;
  return be.call('ai.setPermissions', { idempotencyKey: key(), revision: cur, permissions });
}

test('fixture: defaults before the user answers, and what the provider offers', async () => {
  const be = adapter();
  const r = await be.call('ai.getPermissions');
  assert.deepEqual(r.permissions, { version: 1, revision: 0, set: false, see: { specs: true, history: true, names: false },
    do: { checks: true, changes: 'off', scripts: false, web: false, admin: false } });
  assert.deepEqual(r.available, { tools: true, web: 'unknown', scripts: true, admin: false });   // as the host: tools && the build has scripts (P6)
  // The dev hook varies `available` for demos and tests; {} clears it.
  assert.deepEqual((await be.call('fixture.setAvailable', { tools: false, web: 'no', scripts: true })).available, { tools: false, web: 'no', scripts: true, admin: false });
  assert.deepEqual((await be.call('ai.getPermissions')).available, { tools: false, web: 'no', scripts: true, admin: false });
  await be.call('fixture.setAvailable', {});
  assert.deepEqual((await be.call('ai.getPermissions')).available, { tools: true, web: 'unknown', scripts: true, admin: false });
});

test('fixture: setPermissions sets, bumps the revision, emits ai.permissions; stale revision and bad values refused', async () => {
  const be = adapter();
  const ev = waitFor(be, (e) => e.type === 'ai.permissions');
  const r = await save(be, rec({}, { changes: 'fix' }), 0);
  assert.equal(r.permissions.set, true);
  assert.equal(r.permissions.revision, 1);
  assert.equal(r.permissions.do.changes, 'fix');
  const e = await ev;
  assert.equal(e.stream, 'user');
  assert.deepEqual(e.payload.permissions, r.permissions);
  // Stale revision → -32002 with the current record.
  await assert.rejects(save(be, rec({}, {}), 0), (err) => err.code === -32002 && err.data.current.permissions.revision === 1);
  // Enums, booleans, and scripts only with multi-step fixes → -32602.
  await assert.rejects(save(be, rec({}, { changes: 'all' })), (err) => err.code === -32602 && err.data.field === 'permissions.do.changes');
  await assert.rejects(save(be, rec({ names: 'yes' }, {})), (err) => err.code === -32602);
  await assert.rejects(save(be, rec({}, { changes: 'ask', scripts: true })), (err) => err.code === -32602 && err.data.field === 'permissions.do.scripts');
  assert.equal((await save(be, rec({}, { changes: 'fix', scripts: true }))).permissions.do.scripts, true);
  // Same idempotency key, same request → the same result.
  const k = key(), cur = (await be.call('ai.getPermissions')).permissions.revision;
  const a = await be.call('ai.setPermissions', { idempotencyKey: k, revision: cur, permissions: rec({}, { web: true }) });
  const b = await be.call('ai.setPermissions', { idempotencyKey: k, revision: cur, permissions: rec({}, { web: true }) });
  assert.deepEqual(a, b);
});

test('fixture: agent.start without a grant builds it from the record (all checks, changes, see, revision)', async () => {
  const be = adapter();
  const reportId = await withReport(be);
  await be.call('shell.requestApiKey', { kind: 'anthropic_api' });
  await save(be, rec({ history: false, names: true }, { changes: 'fix', web: true, admin: false }));
  const t = await be.call('agent.start', { idempotencyKey: key(), goal: 'Why?', recipientKind: 'anthropic_api', scope: { reportId } });
  assert.deepEqual(t.grant.diagnostics, ALL);
  assert.deepEqual(t.grant.share, { names: true });
  assert.deepEqual(t.grant.see, { specs: true, history: false });
  assert.equal(t.grant.changes, 'fix');
  assert.equal(t.grant.web, true);
  assert.equal(t.grant.scripts, false);
  assert.equal(t.grant.canRequestChanges, true);
  assert.equal(t.grant.permissionsRevision, 1);
  // Checks off → no diagnostics; an explain-only account gets no checks, changes or web whatever the record says.
  await save(be, rec({}, { checks: false, changes: 'off' }));
  const t2 = await be.call('agent.start', { idempotencyKey: key(), goal: 'Why?', recipientKind: 'anthropic_api', scope: { reportId } });
  assert.deepEqual(t2.grant.diagnostics, []);
  assert.equal(t2.grant.canRequestChanges, false);
  await save(be, rec({}, { changes: 'fix', web: true, admin: false }));
  await be.call('fixture.setTools', { kind: 'anthropic_api', tools: false });
  const t3 = await be.call('agent.start', { idempotencyKey: key(), goal: 'Why?', recipientKind: 'anthropic_api', scope: { reportId } });
  assert.deepEqual([t3.grant.diagnostics, t3.grant.changes, t3.grant.web], [[], 'off', false]);
  // A given grant (the pairing panel) is still validated; the legacy flag maps to "ask".
  await be.call('fixture.setTools', { kind: 'anthropic_api', tools: true });
  const t4 = await be.call('agent.start', { idempotencyKey: key(), goal: 'Why?', recipientKind: 'anthropic_api', scope: { reportId }, grant: { diagnostics: ['process.sample'], share: { names: false }, canRequestChanges: true } });
  assert.equal(t4.grant.changes, 'ask');
  await assert.rejects(be.call('agent.start', { idempotencyKey: key(), goal: 'x', recipientKind: 'anthropic_api', scope: { reportId }, grant: { diagnostics: [], changes: 'ask', scripts: true } }),
    (err) => err.code === -32602);
});

test('fixture: a save that narrows ends active tasks and cancels their pending plans; widening updates grants in place', async () => {
  const be = adapter();
  const reportId = await withReport(be);
  await be.call('shell.requestApiKey', { kind: 'anthropic_api' });
  await save(be, rec({}, { changes: 'ask' }));
  const t = await be.call('agent.start', { idempotencyKey: key(), goal: 'Loud', recipientKind: 'anthropic_api', scope: { reportId } });
  // Widen (web on): same task, grant updated in place, announced with an agent.task event.
  const upd = waitFor(be, (e) => e.stream === 'task:' + t.taskId && e.type === 'agent.task' && e.payload.grant);
  await save(be, rec({}, { changes: 'fix', web: true, admin: false }));
  const ev = await upd;
  assert.equal(ev.payload.status, 'active');
  assert.equal(ev.payload.grant.changes, 'fix');
  assert.equal(ev.payload.grant.web, true);
  const got = await be.call('agent.get', { taskId: t.taskId });
  assert.equal(got.status, 'active');
  assert.equal(got.grant.changes, 'fix');
  // The assistant asks for a change: a plan awaiting the user.
  const plan = waitFor(be, (e) => e.type === 'action.plan' && e.payload.status === 'awaiting_user');
  await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'Why is it loud on battery?' });
  const pl = (await plan).payload;
  // Narrow (changes back to ask): the task ends and its pending plan is cancelled.
  const cancelled = waitFor(be, (e) => e.type === 'action.plan' && e.payload.planId === pl.planId && e.payload.status === 'cancelled');
  await save(be, rec({}, { changes: 'ask', web: true, admin: false }));
  await cancelled;
  assert.equal((await be.call('agent.get', { taskId: t.taskId })).status, 'stopped');
  await assert.rejects(be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'again' }), (err) => err.code === -32013);
});

test('fixture: tool gates follow the grant (specs, history)', async () => {
  const be = adapter();
  const reportId = await withReport(be);
  await be.call('shell.requestApiKey', { kind: 'anthropic_api' });
  await save(be, rec({ specs: false, history: false }, {}));
  const t = await be.call('agent.start', { idempotencyKey: key(), goal: 'Look', recipientKind: 'anthropic_api', scope: { reportId } });
  const denied = [];
  const done = waitFor(be, (e) => e.stream === 'task:' + t.taskId && e.type === 'agent.message');
  const off = be.onEvent((e) => { if (e.type === 'agent.tool' && e.payload.status === 'denied') denied.push(e.payload.tool); });
  await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'Look over my latest report, PC specs and past reports. Tell me the 3 things that matter most, and improve the suggestion cards where you can.' });
  await done; off();
  assert.deepEqual(denied.sort(), ['get_pc_specs', 'get_report_history']);
});

test('permissionLine: the main combinations', () => {
  const set = (d, see) => Object.assign(permDefaults(), { set: true, revision: 1 }, rec(see || {}, d));
  assert.equal(permissionLine(set({ changes: 'ask' })), 'Reads your report · runs checks · asks before changes');
  assert.equal(permissionLine(set({ changes: 'fix' })), 'Reads your report · runs checks · asks before changes');
  assert.equal(permissionLine(set({ checks: false, changes: 'ask' })), 'Reads your report · asks before changes');
  assert.equal(permissionLine(set({ changes: 'off' })), 'Reads your report · runs checks · explains only');
  assert.equal(permissionLine(set({ changes: 'fix', web: true, admin: false }), { tools: true, web: 'yes' }), 'Reads your report · runs checks · asks before changes · looks things up');
  assert.equal(permissionLine(set({ changes: 'fix', scripts: true }), { tools: true, scripts: true }), 'Reads your report · runs checks · asks before changes · runs approved scripts');
  // Explain-only account: only reading, whatever the record allows.
  assert.equal(permissionLine(set({ changes: 'fix', web: true, scripts: true }), { tools: false }), 'Reads your report · explains only');
  // The provider can't search: no lookups in the line.
  assert.equal(permissionLine(set({ changes: 'ask', web: true, admin: false }), { tools: true, web: 'no' }), 'Reads your report · runs checks · asks before changes');
});

test('record ↔ page toggles, both ways', () => {
  // Before the user answered: single changes and multi-step fixes pre-selected; web and scripts off.
  assert.deepEqual(permToggles(permDefaults()), { specs: true, history: true, names: false, checks: true, ask: true, fix: true, scripts: false, web: false, admin: false });
  const r = (changes, extra) => Object.assign(permDefaults(), { set: true, revision: 4 }, rec({}, Object.assign({ changes }, extra)));
  assert.deepEqual([permToggles(r('off')).ask, permToggles(r('off')).fix], [false, false]);
  assert.deepEqual([permToggles(r('ask')).ask, permToggles(r('ask')).fix], [true, false]);
  assert.deepEqual([permToggles(r('fix')).ask, permToggles(r('fix')).fix], [true, true]);
  assert.equal(permToggles(r('fix', { scripts: true, web: true })).scripts, true);
  assert.equal(permToggles(r('fix', { scripts: true, web: true })).web, true);
  // Toggles → record: fix wins over ask; scripts only with fix; the revision comes from the record read.
  const base = r('ask');
  const t = { specs: false, history: true, names: true, checks: false, ask: true, fix: false, scripts: true, web: true };
  assert.deepEqual(permRecord(t, base), { version: 1, revision: 4, set: true, see: { specs: false, history: true, names: true },
    do: { checks: false, changes: 'ask', scripts: false, web: true, admin: false } });
  assert.equal(permRecord(Object.assign({}, t, { fix: true })).do.changes, 'fix');
  assert.equal(permRecord(Object.assign({}, t, { fix: true })).do.scripts, true);
  assert.equal(permRecord(Object.assign({}, t, { ask: false })).do.changes, 'off');
  // Round trip.
  for (const c of ['off', 'ask', 'fix']) assert.deepEqual(permRecord(permToggles(r(c, { web: true })), r(c)), Object.assign(r(c, { web: true })));
  // The pairing panel's grant: every diagnostic when checks is on.
  assert.deepEqual(grantFromToggles(Object.assign({}, t, { checks: true, fix: true }), ALL), { diagnostics: ALL, share: { names: true },
    see: { specs: false, history: true }, changes: 'fix', scripts: true, web: true, admin: false, canRequestChanges: true });
  assert.deepEqual(grantFromToggles(Object.assign({}, t, { ask: false }), ALL).diagnostics, []);
  assert.equal(grantFromToggles(Object.assign({}, t, { ask: false }), ALL).canRequestChanges, false);
});

test('routing by `set`: AI kinds see the page, local / later never; a completed user stays complete', () => {
  const unset = permDefaults(), set = Object.assign(permDefaults(), { set: true, revision: 1 });
  for (const k of ['anthropic_api', 'openai_api', 'chatgpt_plan', 'claude_code_mcp']) {
    assert.ok(isAiKind(k));
    assert.deepEqual(permissionsGate({ kind: k, permissions: unset, next: 'use' }), { stage: 'permissions', returnTo: 'use' });
    assert.deepEqual(permissionsGate({ kind: k, permissions: set, next: 'use' }), { stage: 'use', returnTo: null });
  }
  // local / later (no kind) never; an older host without the record never blocks.
  assert.deepEqual(permissionsGate({ kind: null, permissions: unset, next: 'use' }), { stage: 'use', returnTo: null });
  assert.deepEqual(permissionsGate({ kind: 'local', permissions: unset, next: 'main' }), { stage: 'main', returnTo: null });
  assert.deepEqual(permissionsGate({ kind: 'anthropic_api', permissions: null, next: 'main' }), { stage: 'main', returnTo: null });
  // New user: after the chooser comes the first setup step (through the page when unset).
  assert.equal(nextAfterProvider('provider', null), 'use');
  assert.equal(nextAfterProvider(undefined, null), 'use');
  assert.equal(nextAfterProvider('goal', null), 'goal');
  // Completed onboarding, switching provider (from the pill: returnTo is the page it was opened on, or main).
  assert.equal(nextAfterProvider('complete', null), 'main');
  assert.equal(nextAfterProvider('complete', 'main'), 'main');
  assert.deepEqual(permissionsGate({ kind: 'chatgpt_plan', permissions: unset, next: nextAfterProvider('complete', null) }), { stage: 'permissions', returnTo: 'main' });
  assert.deepEqual(permissionsGate({ kind: 'chatgpt_plan', permissions: set, next: nextAfterProvider('complete', null) }), { stage: 'main', returnTo: null });
});

test('fixture: a provider switch never moves a completed onboarding back', async () => {
  const be = adapter();
  await withReport(be);
  const o = await be.call('onboarding.get');
  assert.equal(o.step, 'complete');
  // The app saves the provider without a step that would move it back (saveOnb drops a step behind the current one).
  const o2 = await be.call('onboarding.save', { expectedRevision: o.revision, patch: { provider: { value: 'chatgpt', label: 'ChatGPT' } } });
  assert.equal(o2.step, 'complete');
  assert.equal((await be.call('ai.getPermissions')).permissions.set, false);
});
