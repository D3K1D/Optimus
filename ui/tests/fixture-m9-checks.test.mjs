// node --test ui/tests/   — m9 §17.2 / §17.6 administrator checks in the fixture, mirroring the host (src/Engine/Agent/AgentService.AdminChecks.cs,
// FixAttempts.CheckRequestOf): the admin tier, requests, agent.checks with retryAt, checks.approve / checks.decline, card.fix
// waiting_checks / checking, the continuation, and dev hooks driving every AdminChecks state. Shapes are checked against the host's schemas.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { createFixtureAdapter } = require('../js/adapter-fixture.js');
const FIX = require('../js/fix-plan.js');
const fixture = JSON.parse(readFileSync(new URL('../fixtures/m1-simulated-audit.json', import.meta.url), 'utf8'));
const extras = JSON.parse(readFileSync(new URL('../fixtures/m2-extras.json', import.meta.url), 'utf8'));
function memStore() { const m = {}; return { getItem: (k) => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); } }; }
const key = () => crypto.randomUUID();
const until = (be, pred) => new Promise((r) => { const off = be.onEvent((e) => { if (pred(e)) { off(); r(e); } }); });
const ended = (be, planId) => until(be, (e) => e.type === 'operation.state' && e.payload.planId === planId && !e.payload.step && FIX.isTerminal(e.payload.state));
const setup = (storage) => createFixtureAdapter({ fixture, extras, storage: storage || memStore(), timeScale: 0.01 });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---- the small schema validator of fixture-host-parity.test.mjs (no dependency), with fix.schema.json ---------------
const FILES = ['action-plan', 'operation', 'event', 'tool-result', 'permissions', 'agent-task', 'scan', 'research', 'report', 'provider-list', 'followup', 'onboarding', 'fix', 'checks']
  .map((n) => n + '.schema.json');
const SCHEMAS = Object.fromEntries(FILES.map((f) => [f, JSON.parse(readFileSync(new URL('../../contracts/schemas/' + f, import.meta.url), 'utf8'))]));
function schemaErrors(name, value, ptr) {
  const typeOk = (t, v) => t === 'null' ? v === null : t === 'array' ? Array.isArray(v)
    : t === 'object' ? v !== null && typeof v === 'object' && !Array.isArray(v)
    : t === 'integer' ? Number.isInteger(v) : t === 'number' ? typeof v === 'number' && Number.isFinite(v) : typeof v === t;
  const at = (doc, p) => (p || '').split('/').filter(Boolean).reduce((n, k) => n[k], doc);
  const resolve = (ref, base) => {
    const [file, p] = ref.split('#');
    const doc = file ? SCHEMAS[file] : base;
    if (!doc) throw new Error('unknown schema ' + ref);
    return [at(doc, p), doc];
  };
  const isObj = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
  const ok = (s, v, base) => { const e = []; check(s, v, base, '$', e); return !e.length; };
  function check(s, v, base, path, out) {
    if (s === true || s === undefined) return;
    if (s.$ref) { const [n, d] = resolve(s.$ref, base); check(n, v, d, path, out); }
    if (s.type && ![].concat(s.type).some((t) => typeOk(t, v))) { out.push(`${path}: expected ${s.type}, got ${JSON.stringify(v)}`); return; }
    if ('const' in s && JSON.stringify(v) !== JSON.stringify(s.const)) out.push(`${path}: not ${JSON.stringify(s.const)}`);
    if (s.enum && !s.enum.some((x) => JSON.stringify(x) === JSON.stringify(v))) out.push(`${path}: ${JSON.stringify(v)} not in [${s.enum}]`);
    if (s.not && ok(s.not, v, base)) out.push(`${path}: matches a not`);
    if (typeof v === 'string') {
      if (s.pattern && !new RegExp(s.pattern).test(v)) out.push(`${path}: "${v}" does not match ${s.pattern}`);
      if (s.maxLength != null && [...v].length > s.maxLength) out.push(`${path}: longer than ${s.maxLength}`);
      if (s.minLength != null && [...v].length < s.minLength) out.push(`${path}: shorter than ${s.minLength}`);
    }
    if (typeof v === 'number' && s.minimum != null && v < s.minimum) out.push(`${path}: below ${s.minimum}`);
    if (typeof v === 'number' && s.maximum != null && v > s.maximum) out.push(`${path}: above ${s.maximum}`);
    if (s.oneOf) { const n = s.oneOf.filter((o) => ok(o, v, base)).length; if (n !== 1) out.push(`${path}: matches ${n} of oneOf`); }
    if (s.allOf) s.allOf.forEach((o) => check(o, v, base, path, out));
    if (s.if) { if (ok(s.if, v, base)) { if (s.then) check(s.then, v, base, path, out); } else if (s.else) check(s.else, v, base, path, out); }
    if (Array.isArray(v)) {
      if (s.maxItems != null && v.length > s.maxItems) out.push(`${path}: more than ${s.maxItems} items`);
      if (s.minItems != null && v.length < s.minItems) out.push(`${path}: fewer than ${s.minItems} items`);
      if (s.uniqueItems && new Set(v.map((x) => JSON.stringify(x))).size !== v.length) out.push(`${path}: items not unique`);
      if (s.prefixItems) s.prefixItems.forEach((p, i) => { if (i < v.length) check(p, v[i], base, `${path}[${i}]`, out); });
      if (s.items) v.forEach((x, i) => { if (!s.prefixItems || i >= s.prefixItems.length) check(s.items, x, base, `${path}[${i}]`, out); });
    } else if (isObj(v)) {
      (s.required || []).forEach((k) => { if (!(k in v)) out.push(`${path}: missing ${k}`); });
      Object.keys(v).forEach((k) => {
        const pats = s.patternProperties ? Object.keys(s.patternProperties).filter((re) => new RegExp(re).test(k)) : [];
        if (s.properties && s.properties[k]) check(s.properties[k], v[k], base, `${path}.${k}`, out);
        else if (pats.length) pats.forEach((re) => check(s.patternProperties[re], v[k], base, `${path}.${k}`, out));
        else if (s.additionalProperties === false) out.push(`${path}: unexpected ${k}`);
        else if (isObj(s.additionalProperties)) check(s.additionalProperties, v[k], base, `${path}.${k}`, out);
      });
    }
  }
  const root = SCHEMAS[name + '.schema.json'];
  const errs = [];
  check(ptr ? at(root, ptr) : root, value, root, '$', errs);
  return errs;
}
const valid = (name, value, what, ptr) => assert.deepEqual(schemaErrors(name, value, ptr), [], `${what} should match ${name}.schema.json: ${JSON.stringify(value).slice(0, 400)}`);

// ---- helpers ------------------------------------------------------------------------------------------------------
const rejects = (p, code, data) => assert.rejects(p, (e) => e.code === code && Object.keys(data).every((k) => e.data && e.data[k] === data[k]));
const DEF = 'security.defender_history', DISK = 'storage.health', BL = 'security.encryption';
const ADMIN_IDS = [DEF, DISK, BL];
async function scanned(be) {
  const o = await be.call('onboarding.get');
  await be.call('onboarding.save', { expectedRevision: o.revision, patch: { inventoryConsent: 'skipped', step: 'complete', provider: { value: 'api', label: 'Claude (API)' } } });
  const fin = until(be, (e) => e.type === 'scan.finished');
  await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  return (await fin).payload.reportId;
}
// A scanned report, the Claude (API) key, the --enable-admin-actions stand-in and a task with the checks and administrator rights.
async function world(grant, flag = true, storage) {
  const be = setup(storage);
  const evs = [];
  be.onEvent((e) => { if (['agent.checks', 'card.fix', 'agent.question'].includes(e.type)) evs.push(e); });
  const reportId = await scanned(be);
  if (flag) await be.call('fixture.setAdminAvailable', { available: true });
  await be.call('shell.requestApiKey', { kind: 'anthropic_api' });
  const t = await be.call('agent.start', { idempotencyKey: key(), goal: 'Look deeper', recipientKind: 'anthropic_api', scope: { reportId },
    grant: Object.assign({ diagnostics: ADMIN_IDS.slice(), changes: 'fix', admin: true }, grant || {}) });
  const fu = await be.call('followup.get', { reportId });
  const cards = [fu.card].concat(fu.upcoming).map((c) => c.id);
  return { be, evs, reportId, t, cards };
}
const tool = (be, t, name, args) => be.call('fixture.agentTool', { taskId: t.taskId, tool: name, args });
const check = (be, t, id, window) => tool(be, t, 'run_diagnostic', window == null ? { diagnosticId: id } : { diagnosticId: id, window });
const lastChecks = async (evs, requestId) => (await sleep(30), evs).filter((e) => e.type === 'agent.checks' && e.payload.requestId === requestId).pop().payload;
const requestOf = async (evs, operationId) => (await sleep(30), evs).filter((e) => e.type === 'agent.checks' && e.payload.checks.some((c) => c.operationId === operationId)).pop().payload.requestId;
const checksEnd = (be, requestId, state) => until(be, (e) => e.type === 'agent.checks' && e.payload.requestId === requestId && e.payload.state === state);
const fixOf = async (be, reportId, cardId) => {
  const fu = await be.call('followup.get', { reportId });
  const c = [fu.card].concat(fu.upcoming).find((x) => x && x.id === cardId);
  return c ? c.fix : undefined;
};
const used = async (be, t) => (await be.call('agent.get', { taskId: t.taskId })).used;
const validEvents = (evs) => { for (const e of evs) valid('event', e, e.type + ' event'); };

test('administrator checks: the admin tier is listed only with the flag; refusals as the host (flag off wins, rights off)', async () => {
  const off = await world({}, false);
  assert.ok(!(await off.be.call('provider.list')).diagnostics.some((d) => d.tier === 'admin'));
  valid('provider-list', await off.be.call('provider.list'), 'provider.list without the flag');
  const r1 = await check(off.be, off.t, DEF);
  valid('tool-result', r1, 'refused admin check');
  assert.equal(r1.error.code, 'admin_unavailable');

  const { be, t } = await world();
  const list = await be.call('provider.list');
  valid('provider-list', list, 'provider.list with the flag');
  const def = list.diagnostics.find((d) => d.id === DEF);
  assert.deepEqual([def.tier, def.arguments.properties.window.minimum, def.arguments.properties.window.maximum, def.arguments.properties.window.default], ['admin', 1, 90, 30]);
  assert.deepEqual(list.diagnostics.filter((d) => d.tier === 'admin').map((d) => d.id), ADMIN_IDS);
  const noRights = await be.call('agent.start', { idempotencyKey: key(), goal: 'x', recipientKind: 'anthropic_api', scope: { reportId: t.scope.reportId }, grant: { diagnostics: ADMIN_IDS, admin: false } });
  const r2 = await check(be, noRights, DISK);
  assert.deepEqual(r2.error, { code: 'admin_not_allowed', message: 'Administrator rights are switched off under Permissions.' });
  // An invalid window is refused before anything is stored (the host validates arguments first).
  assert.equal((await check(be, noRights, DEF, 91)).error.code, 'invalid_arguments'); // as the host: arguments are validated first
});

test('requests: one turn\'s calls form one request (revision, labels), the same check is uncharged, a later turn starts another, two per task', async () => {
  const { be, evs, t, cards } = await world();
  await be.call('fixture.setFixReply', { mode: 'hold' });
  await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, intent: 'fix', cardId: cards[0] });
  for (const bad of [0, 91]) assert.equal((await check(be, t, DEF, bad)).error.code, 'invalid_arguments');
  assert.equal((await check(be, t, DISK, 7)).error.code, 'invalid_arguments');
  const a = await check(be, t, DEF);
  valid('tool-result', a, 'awaiting check');
  valid('checks', a, 'awaiting check (toolWaiting)', '/$defs/toolWaiting');
  assert.equal(a.status, 'awaiting_user');
  const b = await check(be, t, BL);
  const rid = (await requestOf(evs, a.operationId));
  assert.equal((await requestOf(evs, b.operationId)), rid);
  const v = (await lastChecks(evs, rid));
  valid('checks', v, 'agent.checks payload');
  assert.deepEqual([v.revision, v.state, v.cardId, v.retryAt, v.checks.map((c) => c.label)],
    [2, 'awaiting_user', cards[0], null, ['Defender detection history · last 30 days', 'Drive encryption status']]);
  const u = (await used(be, t)).diagnosticCalls;
  assert.equal((await check(be, t, DEF)).operationId, a.operationId);
  assert.equal((await used(be, t)).diagnosticCalls, u, 'the same check comes back uncharged');
  // card.fix: working while the turn runs, then waiting_checks with the request.
  assert.equal((await fixOf(be, t.scope.reportId, cards[0])).state, 'working');
  await be.call('fixture.finishTurn', { taskId: t.taskId });
  const wf = await fixOf(be, t.scope.reportId, cards[0]);
  valid('fix', wf, 'card.fix waiting_checks');
  assert.deepEqual([wf.state, wf.requestId], ['waiting_checks', rid]);
  await rejects(be.call('fix.dismiss', { cardId: cards[0] }), -32013, { reason: 'fix_active' });
  // A later turn: a new request; a third is refused.
  await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'And the disks?' });
  await be.call('fixture.setFixReply', { mode: 'none' });
  const c = await check(be, t, DISK);
  assert.notEqual((await requestOf(evs, c.operationId)), rid);
  await be.call('fixture.finishTurn', { taskId: t.taskId });
  await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'More?' });
  await be.call('fixture.expireChecks', { requestId: rid }); // as the host test: the first ends, so Defender with another window needs a third
  assert.deepEqual((await check(be, t, DEF, 7)).error, { code: 'not_allowed', message: 'This conversation has used its two requests for administrator checks.' });
  validEvents(evs);
});

test('checks.approve: stale revision -32002, running (checking) frozen, results with coverage, one continuation in the fix attempt', async () => {
  const { be, evs, t, cards, reportId } = await world();
  await be.call('fixture.setFixReply', { mode: 'hold' });
  await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, intent: 'fix', cardId: cards[0] });
  const a = await check(be, t, DEF);
  const b = await check(be, t, DISK);
  const rid = (await requestOf(evs, a.operationId));
  await be.call('fixture.finishTurn', { taskId: t.taskId });
  await rejects(be.call('checks.approve', { idempotencyKey: key(), requestId: rid, revision: 1 }), -32002, { reason: 'stale_revision' });
  valid('checks', { idempotencyKey: 'k', requestId: rid, revision: 2 }, 'approve params', '/$defs/approveParams');
  await be.call('fixture.setChecksResult', { mode: 'complete', hold: true });
  const k = key();
  const run = await be.call('checks.approve', { idempotencyKey: k, requestId: rid, revision: 2 });
  valid('checks', run, 'checks.approve result');
  assert.equal(run.state, 'running');
  assert.deepEqual(await be.call('checks.approve', { idempotencyKey: k, requestId: rid, revision: 2 }), run, 'idempotent');
  assert.equal((await fixOf(be, reportId, cards[0])).state, 'checking');
  await rejects(be.call('checks.decline', { requestId: rid }), -32013, { reason: 'running' });
  const asked = until(be, (e) => e.type === 'agent.question' && e.payload.checks === rid);
  await be.call('fixture.setFixReply', { mode: 'none' });
  const fin = await be.call('fixture.finishChecks', { requestId: rid });
  assert.equal(fin.request.state, 'complete');
  const q = (await asked).payload;
  assert.deepEqual([q.text, q.intent, q.cardId], ['', 'fix', cards[0]]);
  const op = await tool(be, t, 'get_operation', { operationId: a.operationId });
  valid('tool-result', op, 'get_operation of a complete check');
  assert.deepEqual([op.status, op.result.coverage.complete, op.result.coverage.total, op.result.evidence[0].subject], ['complete', true, 1, DEF + '.summary']);
  assert.ok(!JSON.stringify(op).includes('Wacatac'), 'names are aliases');
  const disks = await tool(be, t, 'get_operation', { operationId: b.operationId });
  assert.equal(disks.result.evidence.length, 2);
  await until(be, (e) => e.type === 'agent.message');
  await sleep(50);
  assert.equal(evs.filter((e) => e.type === 'agent.question' && e.payload.checks === rid).length, 1, 'exactly one continuation');
  assert.equal((await fixOf(be, reportId, cards[0])).state, 'no_fix');
  validEvents(evs);
});

test('every AdminChecks state can be driven: done (empty, partial), declined with the cooldown, failed (failed, timeout), cancelled (permissions, ai), expired', async () => {
  // done, nothing found; done, could not read all of it
  for (const mode of ['empty', 'partial']) {
    const { be, evs, t } = await world();
    const a = await check(be, t, BL);
    const rid = (await requestOf(evs, a.operationId));
    await be.call('fixture.setChecksResult', { mode, hold: true });
    await be.call('checks.approve', { idempotencyKey: key(), requestId: rid, revision: 1 });
    await be.call('fixture.finishChecks', { requestId: rid });
    const op = await tool(be, t, 'get_operation', { operationId: a.operationId });
    assert.deepEqual([op.status, op.result.coverage.complete, op.result.coverage.returned], mode === 'empty' ? ['complete', true, 0] : ['complete', false, 1]);
    assert.equal(op.result.status, mode === 'empty' ? 'complete' : 'partial');
  }
  // declined in the app, then the cooldown (retryAt) for another request, then declined_before; endCooldown clears it
  {
    const { be, evs, t } = await world();
    const a = await check(be, t, DEF);
    const rid = (await requestOf(evs, a.operationId));
    const d = await be.call('checks.decline', { requestId: rid });
    valid('checks', d, 'checks.decline result');
    assert.deepEqual([d.state, d.code], ['declined', 'user_declined']);
    assert.equal((await check(be, t, DEF)).error.code, 'declined_before');
    const b = await check(be, t, DISK);
    const rid2 = (await requestOf(evs, b.operationId));
    const wait = (await lastChecks(evs, rid2));
    assert.ok(Date.parse(wait.retryAt) > Date.now() + 30000, 'retryAt: the cooldown end');
    await rejects(be.call('checks.approve', { idempotencyKey: key(), requestId: rid2, revision: 1 }), -32013, { reason: 'cooldown' });
    await be.call('fixture.endCooldown', {});
    assert.equal((await lastChecks(evs, rid2)).retryAt, null);
    validEvents(evs);
  }
  // declined at UAC (the helper's session), failed, timeout
  for (const [mode, state, code] of [['declined', 'declined', 'uac_cancelled'], ['failed', 'failed', 'broker_missing'], ['timeout', 'failed', 'timeout'], ['malformed', 'failed', 'malformed']]) {
    const { be, evs, t } = await world();
    const a = await check(be, t, DISK);
    const rid = (await requestOf(evs, a.operationId));
    await be.call('fixture.setChecksResult', { mode, hold: true });
    await be.call('checks.approve', { idempotencyKey: key(), requestId: rid, revision: 1 });
    await be.call('fixture.finishChecks', { requestId: rid });
    assert.deepEqual([(await lastChecks(evs, rid)).state, (await lastChecks(evs, rid)).code], [state, code], mode);
    const op = await tool(be, t, 'get_operation', { operationId: a.operationId });
    assert.equal(op.status, state);
    if (mode === 'declined') assert.ok((await lastChecks(evs, rid)).retryAt === null);
    validEvents(evs);
  }
  // cancelled during the session: a narrowing (permissions) or a change of the chosen AI (ai); the results are discarded
  for (const cause of ['permissions', 'ai']) {
    const { be, evs, t } = await world();
    if (cause === 'permissions') {
      const p = (await be.call('ai.getPermissions')).permissions;
      p['do'].admin = true;
      await be.call('ai.setPermissions', { idempotencyKey: key(), revision: p.revision, permissions: p });
    }
    const a = await check(be, t, DEF);
    const rid = (await requestOf(evs, a.operationId));
    await be.call('fixture.setChecksResult', { mode: 'complete', hold: true });
    await be.call('checks.approve', { idempotencyKey: key(), requestId: rid, revision: 1 });
    if (cause === 'permissions') {
      const p = (await be.call('ai.getPermissions')).permissions;
      p['do'].admin = false;
      await be.call('ai.setPermissions', { idempotencyKey: key(), revision: p.revision, permissions: p });
    } else {
      const o = await be.call('onboarding.get');
      await be.call('onboarding.save', { expectedRevision: o.revision, patch: { provider: { value: 'chatgpt', label: 'ChatGPT', kind: 'chatgpt_plan' } } });
    }
    assert.deepEqual([(await lastChecks(evs, rid)).state, (await lastChecks(evs, rid)).code], ['cancelled', cause]);
    const fin = await be.call('fixture.finishChecks', { requestId: rid });
    assert.equal(fin.finished, false, 'the results are discarded');
    assert.equal((await be.call('agent.get', { taskId: t.taskId })).evidence.filter((e) => e.source === 'admin').length, 0);
  }
  // expired after 10 minutes
  {
    const { be, evs, t } = await world();
    const a = await check(be, t, BL);
    const rid = (await requestOf(evs, a.operationId));
    const x = await be.call('fixture.expireChecks', { requestId: rid });
    assert.equal(x.state, 'expired');
    await rejects(be.call('checks.approve', { idempotencyKey: key(), requestId: rid, revision: 1 }), -32013, { reason: 'expired' });
    assert.equal((await tool(be, t, 'get_operation', { operationId: a.operationId })).status, 'expired');
  }
});

test('the cooldown (as the host): Stop of running checks starts it, and it survives a reload of the saved state', async () => {
  const storage = memStore();
  const { be, evs, t, reportId } = await world(null, true, storage);
  const a = await check(be, t, DISK);
  const rid = await requestOf(evs, a.operationId);
  await be.call('fixture.setChecksResult', { mode: 'complete', hold: true });
  await be.call('checks.approve', { idempotencyKey: key(), requestId: rid, revision: 1 });
  await be.call('agent.stop', { taskId: t.taskId });
  assert.deepEqual([(await lastChecks(evs, rid)).state, (await lastChecks(evs, rid)).code], ['cancelled', 'task_ended']);

  const be2 = setup(storage); // the app reloads: the saved state, cooldown included
  const evs2 = [];
  be2.onEvent((e) => { if (e.type === 'agent.checks') evs2.push(e); });
  await be2.call('fixture.setAdminAvailable', { available: true });
  const t2 = await be2.call('agent.start', { idempotencyKey: key(), goal: 'Again', recipientKind: 'anthropic_api', scope: { reportId },
    grant: { diagnostics: ADMIN_IDS.slice(), changes: 'fix', admin: true } });
  const b = await check(be2, t2, DEF);
  const rid2 = await requestOf(evs2, b.operationId);
  assert.ok(Date.parse((await lastChecks(evs2, rid2)).retryAt) > Date.now() + 30000, 'retryAt: the cooldown end, after the reload');
  await rejects(be2.call('checks.approve', { idempotencyKey: key(), requestId: rid2, revision: 1 }), -32013, { reason: 'cooldown' });
});

test('a stop or a narrowing while the session\'s results are hashed stores nothing and completes nothing (as the host)', async () => {
  for (const cause of ['stop', 'permissions']) {
    const { be, evs, t } = await world();
    const setAdmin = async (on) => {
      const p = (await be.call('ai.getPermissions')).permissions;
      p['do'].admin = on;
      await be.call('ai.setPermissions', { idempotencyKey: key(), revision: p.revision, permissions: p });
    };
    if (cause === 'permissions') await setAdmin(true);
    const a = await check(be, t, DEF);
    const rid = await requestOf(evs, a.operationId);
    await be.call('fixture.setChecksResult', { mode: 'complete', hold: true });
    await be.call('checks.approve', { idempotencyKey: key(), requestId: rid, revision: 1 });
    // Defer the evidence hashes of this request (only they carry its requestId).
    const subtle = globalThis.crypto.subtle, digest = subtle.digest;
    let open, reached, done;
    const gate = new Promise((r) => { open = r; }), hit = new Promise((r) => { reached = r; });
    subtle.digest = function (alg, data) {
      if (!new TextDecoder().decode(data).includes(rid)) return digest.call(subtle, alg, data);
      reached();
      return gate.then(() => digest.call(subtle, alg, data));
    };
    try {
      const finishing = be.call('fixture.finishChecks', { requestId: rid });
      await hit;
      if (cause === 'stop') await be.call('agent.stop', { taskId: t.taskId });
      else await setAdmin(false);
      open();
      done = await finishing;
    } finally {
      delete subtle.digest;
    }
    const v = await lastChecks(evs, rid);
    assert.deepEqual([v.state, v.code], ['cancelled', cause === 'stop' ? 'task_ended' : 'permissions'], cause);
    assert.deepEqual(v.checks.map((c) => c.state), ['cancelled'], cause);
    assert.deepEqual(done.request.checks.map((c) => c.state), ['cancelled'], cause + ': the check was not completed after the hashes');
    assert.equal(done.request.state, 'cancelled', cause);
    assert.ok(!evs.some((e) => e.type === 'agent.checks' && e.payload.requestId === rid && e.payload.state === 'complete'), cause);
  }
});

test('checks.list (as the host): every request that has not ended, with its cooldown wait, as agent.checks carries it', async () => {
  const { be, evs, t } = await world();
  const empty = await be.call('checks.list', {});
  valid('checks', empty, 'checks.list result', '/$defs/listResult');
  assert.deepEqual(empty.requests, []);
  const a = await check(be, t, DEF);
  const declined = await requestOf(evs, a.operationId);
  const turnless = await check(be, t, DISK); // a turnless call joins the open request
  assert.equal(await requestOf(evs, turnless.operationId), declined);
  await be.call('checks.decline', { requestId: declined }); // the cooldown starts
  const b = await check(be, t, BL);
  const waiting = await requestOf(evs, b.operationId);
  const list = await be.call('checks.list', {});
  valid('checks', list, 'checks.list result', '/$defs/listResult');
  assert.deepEqual(list.requests.map((q) => [q.requestId, q.state]), [[waiting, 'awaiting_user']]);
  assert.ok(Date.parse(list.requests[0].retryAt) > Date.now() + 30000, 'inside its cooldown wait');
  assert.deepEqual(list.requests[0], await lastChecks(evs, waiting), 'the same view as agent.checks');
  await be.call('fixture.expireChecks', { requestId: waiting });
  assert.deepEqual((await be.call('checks.list', {})).requests, []);
});

test('one entry per check id in a request (as the host): the same window is the same operation; another window while it waits is not_allowed', async () => {
  const { be, evs, t } = await world();
  const one = await check(be, t, DEF, 1);
  const rid = await requestOf(evs, one.operationId);
  const charged = (await used(be, t)).diagnosticCalls;
  assert.equal((await check(be, t, DEF, 1)).operationId, one.operationId, 'the same check and window: the same, uncharged');
  const other = await check(be, t, DEF); // the default, 30 days
  valid('tool-result', other, 'another window refused');
  assert.deepEqual([other.error.code, other.error.message], ['not_allowed', 'That check is already waiting with another time span.']);
  const v = await lastChecks(evs, rid);
  assert.deepEqual([v.revision, v.checks.map((c) => c.label)], [1, ['Defender detection history · last day']]);
  assert.equal((await used(be, t)).diagnosticCalls, charged, 'refused uncharged');
});

test('a completed request with checks left unread (mixed, unread): each check keeps its own state, as the host', async () => {
  for (const mode of ['mixed', 'unread']) {
    const { be, evs, t } = await world();
    const a = await check(be, t, DEF);
    const b = await check(be, t, DISK);
    const c = await check(be, t, BL);
    const rid = await requestOf(evs, a.operationId);
    await be.call('fixture.setChecksResult', { mode, hold: true });
    await be.call('checks.approve', { idempotencyKey: key(), requestId: rid, revision: 3 });
    await be.call('fixture.finishChecks', { requestId: rid });
    const v = await lastChecks(evs, rid);
    valid('checks', v, mode + ' request');
    assert.equal(v.state, 'complete', mode);
    assert.deepEqual(v.checks.map((x) => x.state), mode === 'mixed' ? ['complete', 'failed', 'failed'] : ['failed', 'failed', 'failed'], mode);
    const ops = await Promise.all([a, b, c].map((x) => tool(be, t, 'get_operation', { operationId: x.operationId })));
    ops.forEach((o) => valid('tool-result', o, mode + ' get_operation'));
    assert.deepEqual(ops.map((o) => [o.status, o.code || null]), mode === 'mixed'
      ? [['complete', null], ['failed', 'unsupported'], ['failed', 'access_denied']]
      : [['failed', 'access_denied'], ['failed', 'unsupported'], ['failed', 'access_denied']], mode);
    validEvents(evs);
  }
});

// ---- the cooldown on the monotonic clock, and the sweep that corrects a countdown already shown (as the host:
// AdminChecksTests.TheCooldownsEnd_SendsTheWaitingRequestsAgain… and AWallClockMove_SendsTheWaitingRequestsAgain…) ----
function movableClocks() {
  const realNow = Date.now, realMono = performance.now;
  const c = { wall: 0, ahead: 0, restore() { Date.now = realNow; performance.now = realMono; } };
  Date.now = () => realNow.call(Date) + c.wall;
  performance.now = () => realMono.call(performance) + c.ahead;
  return c;
}
const views = (evs, rid) => evs.filter((e) => e.type === 'agent.checks' && e.payload.requestId === rid).map((e) => e.payload);
async function waitingRequest(be, evs, t) {
  const b = await check(be, t, DISK);
  const rid = await requestOf(evs, b.operationId);
  return { rid, shown: (await lastChecks(evs, rid)).retryAt };
}

test('the cooldown\'s end sends the waiting requests again with retryAt null, and the sweep clears the saved end (as the host)', async () => {
  const clocks = movableClocks();
  try {
    const storage = memStore();
    const { be, evs, t, reportId } = await world(null, true, storage);
    await be.call('checks.decline', { requestId: await requestOf(evs, (await check(be, t, DEF)).operationId) });
    const { rid, shown } = await waitingRequest(be, evs, t);
    assert.ok(Date.parse(shown) > Date.now() + 30000);

    assert.equal((await be.call('fixture.checksTick', {})).sent, false); // still cooling, the clock unmoved
    await sleep(30);
    assert.equal(views(evs, rid).length, 1);

    clocks.ahead = 61000; // the 60 s pass on the monotonic clock
    assert.equal((await be.call('fixture.checksTick', {})).sent, true);
    const again = await lastChecks(evs, rid);
    assert.deepEqual([again.state, again.retryAt], ['awaiting_user', null]);
    validEvents(evs);
    assert.equal((await be.call('fixture.checksTick', {})).sent, false); // once only
    await sleep(30);
    assert.equal(views(evs, rid).length, 2);

    // The saved end was cleared: a reload with the clock set back shows no cooldown.
    clocks.ahead = 0; clocks.wall = -10 * 60 * 1000;
    const be2 = setup(storage);
    const evs2 = [];
    be2.onEvent((e) => { if (e.type === 'agent.checks') evs2.push(e); });
    await be2.call('fixture.setAdminAvailable', { available: true });
    const t2 = await be2.call('agent.start', { idempotencyKey: key(), goal: 'Again', recipientKind: 'anthropic_api', scope: { reportId },
      grant: { diagnostics: ADMIN_IDS.slice(), changes: 'fix', admin: true } });
    assert.equal((await waitingRequest(be2, evs2, t2)).shown, null);
  } finally { clocks.restore(); }
});

test('a wall-clock move sends the waiting requests again with the corrected retryAt, saved normalised (as the host)', async () => {
  const clocks = movableClocks();
  try {
    const storage = memStore();
    const { be, evs, t, reportId } = await world(null, true, storage);
    await be.call('checks.decline', { requestId: await requestOf(evs, (await check(be, t, DEF)).operationId) });
    const { rid, shown } = await waitingRequest(be, evs, t);

    clocks.wall = -24 * 3600 * 1000; // set back a day: the countdown shown would say a day and a minute
    assert.equal((await be.call('fixture.checksTick', {})).sent, true);
    const corrected = await lastChecks(evs, rid);
    const at = Date.parse(corrected.retryAt);
    assert.ok(at > Date.now() + 30000 && at <= Date.now() + 61000, 'retryAt: the wall clock now plus the time left');
    assert.ok(Math.abs(Date.parse(shown) - at - 24 * 3600 * 1000) < 5000);
    assert.equal(views(evs, rid).length, 2);

    assert.equal((await be.call('fixture.checksTick', {})).sent, false); // nothing moved since
    await sleep(30);
    assert.equal(views(evs, rid).length, 2);
    // A refused approve shows the same corrected retryAt; the list too.
    await rejects(be.call('checks.approve', { idempotencyKey: key(), requestId: rid, revision: 1 }), -32013, { reason: 'cooldown', retryAt: corrected.retryAt });
    assert.equal((await be.call('checks.list', {})).requests.find((q) => q.requestId === rid).retryAt, corrected.retryAt);

    clocks.wall = 5 * 60 * 1000; // moved forward to 5 minutes ahead: still the same 60 s, corrected once more
    assert.equal((await be.call('fixture.checksTick', {})).sent, true);
    const ahead = Date.parse((await lastChecks(evs, rid)).retryAt);
    assert.ok(Math.abs(ahead - Date.parse(shown) - 5 * 60 * 1000) < 5000);

    // The corrected end was saved: after a reload the cooldown goes on (an uncorrected end would be 5 minutes past).
    const be2 = setup(storage);
    const evs2 = [];
    be2.onEvent((e) => { if (e.type === 'agent.checks') evs2.push(e); });
    await be2.call('fixture.setAdminAvailable', { available: true });
    const t2 = await be2.call('agent.start', { idempotencyKey: key(), goal: 'Again', recipientKind: 'anthropic_api', scope: { reportId },
      grant: { diagnostics: ADMIN_IDS.slice(), changes: 'fix', admin: true } });
    const resumed = (await waitingRequest(be2, evs2, t2)).shown;
    assert.ok(resumed && Math.abs(Date.parse(resumed) - ahead) < 2000, 'the saved end, normalised');
  } finally { clocks.restore(); }
});
