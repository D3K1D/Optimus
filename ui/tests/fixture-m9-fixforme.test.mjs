// node --test ui/tests/   — m9 §17.3 / §17.6 "Fix for me" in the fixture, mirroring the host (src/Engine/FixAttempts.cs,
// Actions/ActionService.Cards.cs, ActionService.Narrow.cs, Agent/AgentService.FixAttempts.cs): fix attempts and card.fix with
// its precedence, the card.fix events, plans bound to cards, the one slot rule, action.narrow, "Change something…" (revise),
// fix.dismiss and note_card's outcome. Every new shape is checked against the host's schemas (contracts/schemas).
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
const setup = () => createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---- the small schema validator of fixture-host-parity.test.mjs (no dependency), with fix.schema.json ---------------
const FILES = ['action-plan', 'operation', 'event', 'tool-result', 'permissions', 'agent-task', 'scan', 'research', 'report', 'provider-list', 'followup', 'onboarding', 'fix']
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
const NO_CARD = 'A change is already waiting for the user\'s decision.';
const CARD_TAKEN = 'A change is already waiting on this card.';
const THREE = 'Three changes are already waiting.';
function recordEvents(be) {
  const evs = [];
  be.onEvent((e) => { if (['card.fix', 'agent.question', 'action.plan'].includes(e.type)) evs.push(e); });
  return evs;
}
async function scanned(be) {
  const o = await be.call('onboarding.get');
  await be.call('onboarding.save', { expectedRevision: o.revision, patch: { inventoryConsent: 'skipped', step: 'complete', provider: { value: 'api', label: 'Claude (API)' } } });
  const fin = until(be, (e) => e.type === 'scan.finished');
  await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  return (await fin).payload.reportId;
}
// A scanned report, the Claude (API) key and a task with the given grant; the three open cards at the front of the deck.
async function world(grant) {
  const be = setup();
  const evs = recordEvents(be);
  const reportId = await scanned(be);
  await be.call('shell.requestApiKey', { kind: 'anthropic_api' });
  const t = await be.call('agent.start', { idempotencyKey: key(), goal: 'Fix things', recipientKind: 'anthropic_api', scope: { reportId },
    grant: Object.assign({ diagnostics: [], changes: 'fix' }, grant || {}) });
  const fu = await be.call('followup.get', { reportId });
  const cards = [fu.card].concat(fu.upcoming).map((c) => c.id);
  return { be, evs, reportId, t, cards };
}
const fixOf = async (be, reportId, cardId) => {
  const fu = await be.call('followup.get', { reportId });
  valid('followup', fu, 'followup.get');
  const c = [fu.card].concat(fu.upcoming).find((x) => x && x.id === cardId);
  return c ? c.fix : undefined;
};
const stateOf = async (be, reportId, cardId) => { const f = await fixOf(be, reportId, cardId); return f ? f.state : null; };
const fixAsk = (be, t, cardId, extra) => be.call('agent.ask', Object.assign({ idempotencyKey: key(), taskId: t.taskId, intent: 'fix', cardId }, extra || {}));
const tool = (be, t, name, args) => be.call('fixture.agentTool', { taskId: t.taskId, tool: name, args });
const STARTUP = { title: 'Quieter startup', why: 'Two apps open with Windows that you rarely use.', steps: [
  { actionId: 'startup.disable', targetRef: { evidenceId: 'ev-fx-startup-spotify' } },
  { actionId: 'startup.disable', targetRef: { evidenceId: 'ev-fx-startup-steam' } },
  { actionId: 'setting.set', parameters: { settingId: 'notifications.windows_tips', value: 'off' } }] };
const getPlan = async (be, planId) => { const p = await be.call('action.get', { planId }); delete p.operation; return p; };
function canonical(x) {
  if (Array.isArray(x)) return '[' + x.map(canonical).join(',') + ']';
  if (x && typeof x === 'object') return '{' + Object.keys(x).sort().map((k) => JSON.stringify(k) + ':' + canonical(x[k])).join(',') + '}';
  return JSON.stringify(x);
}
// The host's HashFix over a fix plan (leftOut, narrowedFrom and removed only when present).
function hashFix(p) {
  const f = { planId: p.planId, kind: p.kind, findingId: p.findingId, title: p.title, why: p.why, flags: p.flags, privilege: p.privilege, resources: p.resources,
    undoOf: p.undoOf, steps: p.steps, expiresAt: p.expiresAt };
  for (const k of ['leftOut', 'narrowedFrom', 'removed']) if (p[k] !== undefined) f[k] = p[k];
  return createHash('sha256').update(canonical(f)).digest('hex');
}
const validEvents = (evs) => { for (const e of evs) valid('event', e, e.type + ' event'); };

test('Fix for me: working → plan_ready → running → fixed; agent.question, plans, followup cards and card.fix events match the host schemas', async () => {
  const { be, evs, reportId, t, cards } = await world();
  assert.equal(await fixOf(be, reportId, cards[0]), null, 'no fix on a card: null');
  await be.call('fixture.setFixReply', { mode: 'hold' });
  const r = await fixAsk(be, t, cards[0]);
  await sleep(20);
  const q = evs.find((e) => e.type === 'agent.question' && e.payload.turnId === r.turnId);
  assert.deepEqual([q.payload.intent, q.payload.text, q.payload.cardId, q.payload.revise], ['fix', '', cards[0], undefined]);
  assert.match(q.payload.attemptId, /^fix-[0-9a-f]{16}$/);
  const w = await fixOf(be, reportId, cards[0]);
  valid('fix', w, 'card.fix working');
  assert.deepEqual([w.state, w.by, w.attemptId, w.taskId, w.planId, w.operationId, w.requestId, w.opinion, w.code], ['working', 'Claude', q.payload.attemptId, t.taskId, null, null, null, null, null]);
  assert.ok(evs.some((e) => e.type === 'card.fix' && e.payload.fix && e.payload.fix.taskId === t.taskId), 'card.fix names the task of the attempt');
  // The turn's plan belongs to the attempt; while the turn runs the card is still working (with the plan named).
  const res = await tool(be, t, 'propose_fix', STARTUP);
  valid('tool-result', res, 'propose_fix result');
  const plan = await getPlan(be, res.planId);
  valid('action-plan', plan, 'a card-bound fix plan');
  assert.deepEqual([plan.cardId, plan.attemptId], [cards[0], q.payload.attemptId]);
  assert.deepEqual([(await fixOf(be, reportId, cards[0])).state, (await fixOf(be, reportId, cards[0])).planId], ['working', plan.planId]);
  await be.call('fixture.finishTurn', { taskId: t.taskId });
  assert.equal(await stateOf(be, reportId, cards[0]), 'plan_ready');
  const done = ended(be, plan.planId);
  const a = await be.call('shell.approvePlan', { planId: plan.planId });
  assert.equal(a.status, 'approved');
  assert.equal(await stateOf(be, reportId, cards[0]), 'running');
  await done;
  await sleep(20);
  const fixed = await fixOf(be, reportId, cards[0]);
  assert.deepEqual([fixed.state, fixed.planId, fixed.operationId], ['fixed', plan.planId, a.operationId]);
  // Every plan without a card carries nulls (the user's own previews and undo plans included).
  const user = await getPlan(be, (await be.call('fixture.proposeFix', { sample: 'startup' })).planId);
  assert.deepEqual([user.cardId, user.attemptId], [null, null]);
  const fixEvents = evs.filter((e) => e.type === 'card.fix');
  assert.ok(fixEvents.length >= 4 && fixEvents.every((e) => e.stream === 'user' && e.payload.cardId === cards[0] && e.payload.reportId === reportId));
  assert.deepEqual([...new Set(fixEvents.map((e) => e.payload.fix && e.payload.fix.state))].filter(Boolean), ['working', 'plan_ready', 'running', 'fixed']);
  validEvents(evs);
});

test('card.fix precedence: plan_ready over running, the last plan decides, every terminal status, blocked with its code, back to normal', async () => {
  const { be, evs, reportId, t, cards } = await world();
  await be.call('fixture.setFixReply', { mode: 'hold' });
  await fixAsk(be, t, cards[0]);
  const p1 = (await tool(be, t, 'request_action', { actionId: 'network.flush_dns' })).planId;
  await be.call('fixture.setPlanStatus', { planId: p1, status: 'running' });
  assert.equal(await stateOf(be, reportId, cards[0]), 'working', 'working wins over everything');
  const p2 = (await tool(be, t, 'request_action', { actionId: 'defender.update_signatures' })).planId;
  await be.call('fixture.finishTurn', { taskId: t.taskId });
  assert.deepEqual([(await fixOf(be, reportId, cards[0])).state, (await fixOf(be, reportId, cards[0])).planId], ['plan_ready', p2], 'plan_ready over running');
  await be.call('fixture.setPlanStatus', { planId: p2, status: 'approved' });
  assert.deepEqual([(await fixOf(be, reportId, cards[0])).state, (await fixOf(be, reportId, cards[0])).planId], ['running', p2], 'approved counts as running; the last one named');
  await be.call('fixture.setPlanStatus', { planId: p1, status: 'verified' });
  // The attempt's last plan decides once nothing waits or runs.
  const want = { verified: 'fixed', partial: 'partly_fixed', completed: 'finished', failed: 'failed', stopped: 'stopped', timed_out: 'stopped', not_run: 'failed',
    indeterminate: 'unclear', declined: null, expired: null, cancelled: null };
  for (const [status, state] of Object.entries(want)) {
    await be.call('fixture.setPlanStatus', { planId: p2, status });
    const f = await fixOf(be, reportId, cards[0]);
    assert.equal(f ? f.state : null, state, status + ' → ' + state);
    if (f) { valid('fix', f, 'card.fix ' + state); assert.equal(f.planId, p2); }
  }
  await be.call('fixture.setPlanStatus', { planId: p2, status: 'ineligible', ineligible: { code: 'admin_not_allowed', message: 'x' } });
  const blocked = await fixOf(be, reportId, cards[0]);
  assert.deepEqual([blocked.state, blocked.code], ['blocked', 'admin_not_allowed']);
  // Waiting again: plan_ready (over the verified plan before it).
  await be.call('fixture.setPlanStatus', { planId: p2, status: 'awaiting_user' });
  assert.equal(await stateOf(be, reportId, cards[0]), 'plan_ready');
  // A fix ask that leaves neither a plan nor an outcome: no_fix (the default reply).
  await be.call('fixture.setFixReply', { mode: 'none' });
  const msg = until(be, (e) => e.type === 'agent.message' && e.stream === 'task:' + t.taskId);
  await fixAsk(be, t, cards[1]);
  await msg;
  await sleep(20);
  assert.equal(await stateOf(be, reportId, cards[1]), 'no_fix');
  // A refused ask (fix_active while the plan waits) changes nothing.
  await rejects(fixAsk(be, t, cards[0]), -32013, { reason: 'fix_active' });
  assert.equal(await stateOf(be, reportId, cards[0]), 'plan_ready');
  validEvents(evs);
});

test('an opinion (note_card outcome) never changes the card\'s status and never wins over a host state; bad outcomes are invalid_arguments', async () => {
  const { be, evs, reportId, t, cards } = await world();
  await be.call('fixture.setFixReply', { mode: 'hold' });
  await fixAsk(be, t, cards[0]);
  const bad = await tool(be, t, 'note_card', { cardId: cards[0], text: 'x', outcome: 'fixed' });
  assert.deepEqual(bad.error, { code: 'invalid_arguments', message: 'outcome must be one of: needs_user, nothing_to_fix.' });
  assert.equal((await tool(be, t, 'note_card', { cardId: cards[0], text: 'Only you can do this.', outcome: 'needs_user' })).status, 'noted');
  assert.equal(await stateOf(be, reportId, cards[0]), 'working', 'the turn still runs');
  await be.call('fixture.finishTurn', { taskId: t.taskId });
  const f = await fixOf(be, reportId, cards[0]);
  valid('fix', f, 'needs_you');
  assert.deepEqual([f.state, f.opinion.outcome, f.opinion.by, f.opinion.taskId], ['needs_you', 'needs_user', 'Claude', t.taskId]);
  const fu = await be.call('followup.get', { reportId });
  assert.equal(fu.card.id, cards[0], 'the card stays at the front, open');
  // A plan on the card wins over the opinion (which stays attached until a new attempt starts).
  const p = (await tool(be, t, 'request_action', { actionId: 'network.flush_dns', cardRef: { cardId: cards[0] } })).planId;
  const withPlan = await fixOf(be, reportId, cards[0]);
  assert.equal(withPlan.state, 'plan_ready');
  assert.notEqual(withPlan.attemptId, f.attemptId, 'a cardRef outside a fix ask starts a new attempt');
  assert.equal(withPlan.opinion, null, 'a new attempt clears the opinion');
  await be.call('fixture.setPlanStatus', { planId: p, status: 'verified' });
  assert.equal((await tool(be, t, 'note_card', { cardId: cards[0], text: 'Nothing left.', outcome: 'nothing_to_fix' })).status, 'noted');
  const after = await fixOf(be, reportId, cards[0]);
  assert.deepEqual([after.state, after.opinion.outcome], ['fixed', 'nothing_to_fix'], 'a host state wins over the opinion');
  // Outside a turn, on a card with no attempt: a new ended attempt with the opinion.
  assert.equal((await tool(be, t, 'note_card', { cardId: cards[2], text: 'Fine as it is.', outcome: 'nothing_to_fix' })).status, 'noted');
  assert.equal(await stateOf(be, reportId, cards[2]), 'nothing_to_fix');
  validEvents(evs);
});

test('the slot rule: one pending AI plan per card across tasks, three per task with one without a card, approval not counted, previews take no slot', async () => {
  const { be, evs, reportId, t, cards } = await world({ changes: 'fix' });
  const a = await tool(be, t, 'request_action', { actionId: 'power.select_scheme', parameters: { personality: 'balanced' } });
  assert.equal(a.status, 'awaiting_user');
  assert.deepEqual((await tool(be, t, 'request_action', { actionId: 'network.flush_dns' })).error, { code: 'not_allowed', message: NO_CARD });
  assert.deepEqual((await tool(be, t, 'propose_fix', STARTUP)).error, { code: 'not_allowed', message: NO_CARD }, 'one without a card');
  const b = await tool(be, t, 'request_action', { actionId: 'network.flush_dns', cardRef: { cardId: cards[0] } });
  const pb = await getPlan(be, b.planId);
  valid('action-plan', pb, 'a cardRef plan');
  assert.equal(pb.cardId, cards[0]);
  assert.equal(await stateOf(be, reportId, cards[0]), 'plan_ready', 'a cardRef outside a fix ask: an ended attempt');
  assert.deepEqual((await tool(be, t, 'propose_fix', Object.assign({ cardRef: { cardId: cards[0] } }, STARTUP))).error, { code: 'not_allowed', message: CARD_TAKEN });
  const c = await tool(be, t, 'propose_fix', Object.assign({ cardRef: { cardId: cards[1] } }, STARTUP));
  assert.equal(c.status, 'awaiting_user');
  assert.deepEqual((await tool(be, t, 'request_action', { actionId: 'defender.quick_scan', cardRef: { cardId: cards[2] } })).error, { code: 'not_allowed', message: THREE }, 'the fourth');
  // The user's own previews take no slot.
  assert.equal((await be.call('fixture.proposeFix', { sample: 'startup' })).status, 'awaiting_user');
  // Approval with other plans pending: the approved plan is not counted against itself.
  const done = until(be, (e) => e.type === 'operation.state' && e.payload.planId === b.planId && FIX.isTerminal(e.payload.state));
  assert.equal((await be.call('shell.approvePlan', { planId: b.planId })).status, 'approved');
  await done;
  assert.equal((await tool(be, t, 'request_action', { actionId: 'defender.quick_scan', cardRef: { cardId: cards[2] } })).status, 'awaiting_user', 'a slot freed');
  assert.equal(await stateOf(be, reportId, cards[0]), 'finished', 'a report-only change that completed is finished, never fixed');

  // The same card from another task (a paired outside window): refused while the first task's plan waits on it.
  const pending = until(be, (e) => e.type === 'agent.pairing' && e.payload.status === 'pending');
  await be.call('provider.connect', { kind: 'claude_code_mcp' });
  const pr = (await pending).payload;
  await be.call('agent.approvePairing', { pairingId: pr.pairingId, approve: true });
  const t2 = await be.call('agent.start', { idempotencyKey: key(), goal: 'x', recipientKind: 'claude_code_mcp', pairingId: pr.pairingId, scope: { reportId },
    grant: { diagnostics: [], changes: 'fix' } });
  assert.deepEqual((await tool(be, t2, 'request_action', { actionId: 'network.flush_dns', cardRef: { cardId: cards[2] } })).error, { code: 'not_allowed', message: CARD_TAKEN });
  validEvents(evs);
});

test('the slot rule with scripts: the pending script in the same slot comes back uncharged; another occupied slot or three pending is not_allowed', async () => {
  const { be, evs, t, cards } = await world({ changes: 'fix', scripts: true });
  const S = { title: 'Flush DNS', why: 'Clears the cache.', script: 'Clear-DnsClientCache', parts: [{ fromLine: 1, toLine: 1, explains: 'Clears the DNS cache.' }] };
  const s1 = await tool(be, t, 'propose_script', S);
  assert.equal(s1.status, 'awaiting_user');
  const again = await tool(be, t, 'propose_script', S);
  valid('tool-result', again, 'the pending script');
  assert.deepEqual([again.planId, again.summary], [s1.planId, NO_CARD]);
  assert.equal((await be.call('agent.get', { taskId: t.taskId })).scriptProposals, 1, 'uncharged');
  assert.deepEqual((await tool(be, t, 'request_action', { actionId: 'network.flush_dns' })).error, { code: 'not_allowed', message: NO_CARD });
  const s2 = await tool(be, t, 'propose_script', Object.assign({ cardRef: { cardId: cards[0] } }, S));
  assert.notEqual(s2.planId, s1.planId);
  const p2 = await getPlan(be, s2.planId);
  valid('action-plan', p2, 'a card-bound script plan');
  assert.equal(p2.cardId, cards[0]);
  assert.equal((await tool(be, t, 'propose_script', Object.assign({ cardRef: { cardId: cards[0] } }, S))).planId, s2.planId, 'same card: the same plan back');
  assert.equal((await be.call('agent.get', { taskId: t.taskId })).scriptProposals, 2);
  // A fix plan holds card 1: a script there is not this task's script slot → not_allowed.
  assert.equal((await tool(be, t, 'request_action', { actionId: 'network.flush_dns', cardRef: { cardId: cards[1] } })).status, 'awaiting_user');
  assert.deepEqual((await tool(be, t, 'propose_script', Object.assign({ cardRef: { cardId: cards[2] } }, S))).error, { code: 'not_allowed', message: THREE });
  assert.equal((await be.call('agent.get', { taskId: t.taskId })).scriptProposals, 2, 'a refusal by the slot rule is not charged');
  // A bad cardRef is invalid_arguments (Diagnostics.Validate).
  assert.equal((await tool(be, t, 'propose_script', Object.assign({ cardRef: { cardId: 'nope' } }, S))).error.code, 'invalid_arguments');
  validEvents(evs);
});

test('action.narrow: kept steps renumbered in order, the parent\'s expiry, its own hash, the cancelled parent with replacedBy; refusals; a reduced run is done_reduced', async () => {
  const { be, evs, reportId, t, cards } = await world();
  await be.call('fixture.setFixReply', { mode: 'hold' });
  await fixAsk(be, t, cards[0]);
  const parentId = (await tool(be, t, 'propose_fix', STARTUP)).planId;
  await be.call('fixture.finishTurn', { taskId: t.taskId });
  const parent = await getPlan(be, parentId);
  for (const keep of [[], [3, 1], [1, 1], [0], [1.5], [1, 2, 3], [4], 'x', [1, 2, 3, 4, 5, 6, 7]])
    await rejects(be.call('action.narrow', { idempotencyKey: key(), planId: parentId, keep }), -32602, { field: 'keep' });
  const k = key();
  const n = await be.call('action.narrow', { idempotencyKey: k, planId: parentId, keep: [1, 3] });
  valid('action-plan', n, 'a narrowed plan');
  assert.deepEqual(n.steps.map((s) => [s.n, s.actionId, s.contentHash]), [[1, parent.steps[0].actionId, parent.steps[0].contentHash], [2, parent.steps[2].actionId, parent.steps[2].contentHash]]);
  assert.deepEqual([n.narrowedFrom, n.removed, n.expiresAt, n.status, n.cardId, n.attemptId, n.requestedBy],
    [parentId, [{ label: parent.steps[1].label }], parent.expiresAt, 'awaiting_user', parent.cardId, parent.attemptId, parent.requestedBy]);
  assert.equal(n.planHash, hashFix(n), 'its own hash over the fix fields plus narrowedFrom and removed');
  assert.notEqual(n.planHash, parent.planHash);
  const p = await getPlan(be, parentId);
  valid('action-plan', p, 'the cancelled parent');
  assert.deepEqual([p.status, p.replacedBy, p.ineligible], ['cancelled', n.planId, { code: 'replaced', message: 'Replaced by the plan with the steps you kept.' }]);
  assert.deepEqual(await be.call('action.narrow', { idempotencyKey: k, planId: parentId, keep: [1, 3] }), n, 'idempotent');
  await rejects(be.call('action.narrow', { idempotencyKey: key(), planId: parentId, keep: [1] }), -32013, { reason: 'cancelled' });
  assert.deepEqual([(await fixOf(be, reportId, cards[0])).state, (await fixOf(be, reportId, cards[0])).planId], ['plan_ready', n.planId]);
  // A narrowed (reduced) plan verified: done_reduced, the receipt keeps what was switched off, and the card stays open.
  const done = ended(be, n.planId);
  const a = await be.call('shell.approvePlan', { planId: n.planId });
  await done;
  await sleep(20);
  assert.equal(await stateOf(be, reportId, cards[0]), 'done_reduced');
  const op = await be.call('operation.get', { operationId: a.operationId });
  valid('operation', op, 'a narrowed fix operation');
  assert.deepEqual(op.removed, n.removed);
  // Not narrowable: a single plan, an undo plan.
  const single = await be.call('fixture.requestAction', { actionId: 'network.flush_dns' });
  await rejects(be.call('action.narrow', { idempotencyKey: key(), planId: single.planId, keep: [1] }), -32013, { reason: 'not_narrowable' });
  const u = await be.call('action.undo', { idempotencyKey: key(), operationId: a.operationId });
  assert.ok(u.undoOf && u.kind === 'fix');
  await rejects(be.call('action.narrow', { idempotencyKey: key(), planId: u.planId, keep: [1] }), -32013, { reason: 'not_narrowable' });
  validEvents(evs);
});

test('action.narrow twice carries the earlier removed steps forward (parent first), hashed in full, into the receipt', async () => {
  const { be, evs, reportId, t, cards } = await world();
  await be.call('fixture.setFixReply', { mode: 'hold' });
  await fixAsk(be, t, cards[0]);
  const parentId = (await tool(be, t, 'propose_fix', STARTUP)).planId;
  await be.call('fixture.finishTurn', { taskId: t.taskId });
  const parent = await getPlan(be, parentId);
  const first = await be.call('action.narrow', { idempotencyKey: key(), planId: parentId, keep: [2, 3] });
  const second = await be.call('action.narrow', { idempotencyKey: key(), planId: first.planId, keep: [2] });
  valid('action-plan', second, 'a plan narrowed twice');
  assert.deepEqual(second.removed, [{ label: parent.steps[0].label }, { label: parent.steps[1].label }]);
  assert.equal(second.planHash, hashFix(second));
  const done = ended(be, second.planId);
  const a = await be.call('shell.approvePlan', { planId: second.planId });
  await done;
  assert.deepEqual((await be.call('operation.get', { operationId: a.operationId })).removed, second.removed);
  void reportId;
  validEvents(evs);
});

test('revise ("Change something…"): only the awaiting plan of this card\'s attempt in this task; a busy or refused ask cancels nothing; parameter checks', async () => {
  const { be, evs, reportId, t, cards } = await world();
  await be.call('fixture.setFixReply', { mode: 'hold' });
  await fixAsk(be, t, cards[0]);
  const pid = (await tool(be, t, 'propose_fix', STARTUP)).planId;
  await be.call('fixture.finishTurn', { taskId: t.taskId });
  const first = await fixOf(be, reportId, cards[0]);
  // Parameters (before anything is read).
  await rejects(be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'x', revise: pid }), -32602, { field: 'revise' });
  await rejects(be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, intent: 'fix' }), -32602, { field: 'cardId' });
  await rejects(be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, intent: 'repair', cardId: cards[0] }), -32602, { field: 'intent' });
  await rejects(be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, intent: 'fix', cardId: cards[0], context: { benchRunId: 'bench-1', part: 'cpu' } }), -32602, { field: 'context' });
  await rejects(be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: '  ' }), -32602, { field: 'text' });
  // The card: in the task's report, and open.
  await rejects(fixAsk(be, t, 'card-not-in-this-report'), -32602, { field: 'cardId' });
  const fu = await be.call('followup.get', { reportId });
  await be.call('followup.respond', { idempotencyKey: key(), reportId, expectedRevision: fu.revision, scope: 'card', cardId: cards[2], option: 'ignore' });
  await rejects(fixAsk(be, t, cards[2]), -32013, { reason: 'card_not_open' });
  // Wrong plans: a user's plan, a plan on another card; without revise the waiting plan makes it fix_active.
  const user = (await be.call('fixture.proposeFix', { sample: 'startup' })).planId;
  await rejects(fixAsk(be, t, cards[0], { revise: user }), -32602, { field: 'revise' });
  await rejects(fixAsk(be, t, cards[1], { revise: pid }), -32602, { field: 'revise' });
  await rejects(fixAsk(be, t, cards[0]), -32013, { reason: 'fix_active' });
  // Busy: another turn runs → refused, nothing cancelled.
  const other = await fixAsk(be, t, cards[1]);
  await rejects(fixAsk(be, t, cards[0], { revise: pid, text: 'Keep Steam.' }), -32013, { reason: 'busy' });
  assert.equal((await getPlan(be, pid)).status, 'awaiting_user');
  assert.equal((await be.call('agent.cancel', { taskId: t.taskId })).cancelled, true);
  await sleep(20);
  assert.equal(await stateOf(be, reportId, cards[1]), 'no_fix', 'a cancelled turn ends its attempt');
  assert.ok(other.turnId);
  // The real revise: the plan cancelled (revised), a new attempt working, the question names it.
  const r = await fixAsk(be, t, cards[0], { revise: pid, text: 'Keep Steam.' });
  await sleep(20);
  const rp = await getPlan(be, pid);
  assert.deepEqual([rp.status, rp.ineligible], ['cancelled', { code: 'revised', message: 'You asked for a different plan.' }]);
  const now = await fixOf(be, reportId, cards[0]);
  assert.equal(now.state, 'working');
  assert.notEqual(now.attemptId, first.attemptId);
  const q = evs.find((e) => e.type === 'agent.question' && e.payload.turnId === r.turnId);
  assert.deepEqual([q.payload.intent, q.payload.revise, q.payload.text, q.payload.attemptId], ['fix', pid, 'Keep Steam.', now.attemptId]);
  validEvents(evs);
});

test('cardRef and findingRef: inside a fix ask the plan belongs to that card; outside, an open card of the task\'s report', async () => {
  const { be, evs, reportId, t, cards } = await world();
  const report = await be.call('report.get', { reportId });
  const fu = await be.call('followup.get', { reportId });
  const cardFinding = fu.card.findingId, otherFinding = report.findings.find((f) => f.id !== cardFinding).id;
  assert.equal((await tool(be, t, 'request_action', { actionId: 'network.flush_dns', cardRef: { cardId: cards[0], x: 1 } })).error.code, 'invalid_arguments');
  assert.deepEqual((await tool(be, t, 'request_action', { actionId: 'network.flush_dns', cardRef: { cardId: 'card-unknown' } })).error, { code: 'not_allowed', message: 'That card is not part of this task.' });
  await be.call('followup.respond', { idempotencyKey: key(), reportId, expectedRevision: fu.revision, scope: 'card', cardId: cards[2], option: 'later' });
  assert.deepEqual((await tool(be, t, 'request_action', { actionId: 'network.flush_dns', cardRef: { cardId: cards[2] } })).error, { code: 'not_allowed', message: 'That card is not open.' });
  await be.call('fixture.setFixReply', { mode: 'hold' });
  await fixAsk(be, t, cards[0]);
  assert.deepEqual((await tool(be, t, 'propose_fix', Object.assign({ cardRef: { cardId: cards[1] } }, STARTUP))).error,
    { code: 'invalid_arguments', message: 'cardRef must name the card you are fixing.' });
  assert.deepEqual((await tool(be, t, 'propose_fix', Object.assign({ findingRef: otherFinding }, STARTUP))).error,
    { code: 'invalid_arguments', message: 'findingRef must be the finding of the card you are fixing, or left out.' });
  const ok = await tool(be, t, 'propose_fix', Object.assign({ findingRef: cardFinding, cardRef: { cardId: cards[0] } }, STARTUP));
  assert.equal((await getPlan(be, ok.planId)).cardId, cards[0]);
  validEvents(evs);
});

test('fix.dismiss: no attempt → already normal; refused fix_active while a plan waits; an ended fix is dismissed (card.fix null)', async () => {
  const { be, evs, reportId, t, cards } = await world();
  valid('fix', await be.call('fix.dismiss', { cardId: cards[1] }), 'dismiss result', '/$defs/dismissResult');
  assert.deepEqual(await be.call('fix.dismiss', { cardId: cards[1] }), { cardId: cards[1], fix: null });
  await rejects(be.call('fix.dismiss', {}), -32602, { field: 'cardId' });
  await be.call('fixture.setFixReply', { mode: 'hold' });
  await fixAsk(be, t, cards[0]);
  await rejects(be.call('fix.dismiss', { cardId: cards[0] }), -32013, { reason: 'fix_active' });
  const pid = (await tool(be, t, 'request_action', { actionId: 'network.flush_dns' })).planId;
  await be.call('fixture.finishTurn', { taskId: t.taskId });
  await rejects(be.call('fix.dismiss', { cardId: cards[0] }), -32013, { reason: 'fix_active' });
  await be.call('action.decline', { planId: pid });
  assert.equal(await fixOf(be, reportId, cards[0]), null, 'a declined plan: back to normal');
  await be.call('fixture.setPlanStatus', { planId: pid, status: 'failed' });
  assert.equal(await stateOf(be, reportId, cards[0]), 'failed');
  assert.deepEqual(await be.call('fix.dismiss', { cardId: cards[0] }), { cardId: cards[0], fix: null });
  assert.equal(await fixOf(be, reportId, cards[0]), null);
  await sleep(20);
  const ev = evs.filter((e) => e.type === 'card.fix' && e.payload.cardId === cards[0]).pop();
  assert.deepEqual(ev.payload, { cardId: cards[0], reportId, fix: null });
  validEvents(evs);
});

test('action.narrow finding drift ends the parent as an approval does (ineligible, stale_plan): the card is blocked with stale_plan, as the host', async () => {
  const { be, reportId, t, cards } = await world();
  await be.call('fixture.setFixReply', { mode: 'hold' });
  await fixAsk(be, t, cards[0]);
  const parentId = (await tool(be, t, 'propose_fix', STARTUP)).planId;
  await be.call('fixture.finishTurn', { taskId: t.taskId });
  await be.call('fixture.changeStartup', { name: 'Spotify' }); // a kept step no longer resolves the same
  await rejects(be.call('action.narrow', { idempotencyKey: key(), planId: parentId, keep: [1, 3] }), -32002, { reason: 'stale_plan' });
  const p = await getPlan(be, parentId);
  valid('action-plan', p, 'the parent ended');
  assert.deepEqual([p.status, p.ineligible.code], ['ineligible', 'stale_plan']);
  const f = await fixOf(be, reportId, cards[0]);
  valid('fix', f, 'card.fix blocked');
  assert.deepEqual([f.state, f.code, f.planId], ['blocked', 'stale_plan', parentId]);
});

test('card.fix stopped from a real run (as the host): a card script stopped by the user, or out of time; dismissable once ended', async () => {
  const S = { title: 'Flush DNS', why: 'Clears the cache.', script: 'Clear-DnsClientCache', parts: [{ fromLine: 1, toLine: 1, explains: 'Clears the DNS cache.' }] };
  const OK = { ticked: true, readToEnd: true, options: { restorePoint: false } };
  for (const how of ['stop', 'timeout']) {
    const { be, reportId, t, cards } = await world({ changes: 'fix', scripts: true });
    const planId = (await tool(be, t, 'propose_script', Object.assign({ cardRef: { cardId: cards[0] } }, S))).planId;
    await be.call('fixture.setScriptRun', how === 'stop' ? { outcome: 'completed', seconds: 30 } : { outcome: 'timeout' });
    const running = until(be, (e) => e.type === 'operation.state' && e.payload.planId === planId && e.payload.state === 'running');
    const end = until(be, (e) => e.type === 'operation.state' && e.payload.planId === planId && FIX.scriptEnded(e.payload.state));
    const a = await be.call('shell.approvePlan', Object.assign({ planId }, OK));
    assert.equal(a.status, 'approved', how);
    if (how === 'stop') { await running; assert.equal((await be.call('operation.stop', { operationId: a.operationId })).state, 'stopped'); }
    const op = (await end).payload;
    assert.equal(op.state, how === 'stop' ? 'stopped' : 'timed_out', how);
    const f = await fixOf(be, reportId, cards[0]);
    valid('fix', f, 'card.fix ' + how);
    assert.deepEqual([f.state, f.planId], ['stopped', planId], how);
    assert.equal((await be.call('fix.dismiss', { cardId: cards[0] })).fix, null, how + ': an ended script can be dismissed');
  }
});
