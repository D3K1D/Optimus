// node --test ui/tests/   — M9 fix plans in the fixture adapter (contracts/m9-ai-powers.md §4) and the pure card helpers.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { choose } from './choose-ai.mjs';
import { readFileSync } from 'node:fs';
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
const STEP_KEYS = ['n', 'actionId', 'handlerVersion', 'label', 'what', 'target', 'before', 'after', 'parameters', 'rows', 'context', 'tradeoffs',
  'privilege', 'flags', 'undo', 'resources', 'contentHash', 'why'];

test('propose_fix: the default risky sample is four steps for your account, one that can\'t be undone', async () => {
  const be = setup();
  const r = await be.call('fixture.proposeFix', { sample: 'risky' });
  assert.equal(r.status, 'awaiting_user');
  assert.equal(r.summary, '4 steps, waiting for the user\'s approval in Optimus. Poll get_operation with this planId.');
  const plan = await be.call('action.get', { planId: r.planId });
  assert.deepEqual(plan.steps.map((s) => s.privilege), ['none', 'none', 'none', 'none']);
  assert.equal(plan.privilege, 'none');
  assert.deepEqual(plan.steps.map((s) => s.undo.supported), [true, false, true, true]);
  assert.deepEqual(plan.flags, ['irreversible', 'data_loss']);
  // Step labels are the handler's; the target's name is in the rows.
  assert.deepEqual(plan.steps.map((s) => s.label), ['Change a Windows setting', 'Empty the Recycle Bin', 'Turn off a startup app', 'Change a Windows setting']);
  assert.deepEqual(plan.steps[2].rows, [{ label: 'Starts when you sign in', before: 'On', after: 'Off' }, { label: 'Startup entry', before: 'Microsoft Teams (personal)', after: 'Microsoft Teams (personal)' }]);
  assert.equal(plan.why.by, 'Claude');
});

test('administrator steps: ineligible (admin_unavailable) until the broker lands; the dev hook turns them on', async () => {
  const be = setup();
  const off = await be.call('fixture.proposeFix', { sample: 'admin' });
  assert.equal(off.status, 'ineligible');
  assert.deepEqual(off.steps, [{ n: 1, status: 'ineligible', code: 'admin_unavailable', message: 'Administrator steps aren\'t available in this version.' }]);
  const po = await be.call('action.get', { planId: off.planId });
  assert.equal(po.ineligible.code, 'admin_unavailable');
  assert.equal(po.ineligible.manualUri, null);
  assert.equal(po.privilege, 'admin');
  assert.deepEqual(await be.call('fixture.setAdminAvailable', { available: true }), { available: true });
  const on = await be.call('fixture.proposeFix', { sample: 'admin' });
  assert.equal(on.status, 'awaiting_user');
});

test('propose_fix with admin steps on: the §4 manifest, user steps before admin steps, flags as the union', async () => {
  const be = setup();
  await be.call('fixture.setAdminAvailable', { available: true });
  const planEv = until(be, (e) => e.type === 'action.plan');
  const r = await be.call('fixture.proposeFix', { sample: 'risky' });
  assert.equal(r.status, 'awaiting_user');
  assert.deepEqual(r.steps.map((s) => s.status), ['ok', 'ok', 'ok', 'ok']);
  const ev = (await planEv).payload;
  assert.equal(ev.planId, r.planId);
  assert.notEqual(ev.requestedBy.kind, 'user');   // the app shows it as an assistant's request
  const plan = await be.call('action.get', { planId: r.planId });
  assert.equal(plan.kind, 'fix');
  assert.equal(plan.title.length <= 80, true);
  assert.deepEqual(Object.keys(plan.why).sort(), ['by', 'text']);
  assert.ok(plan.why.text.length > 350 && plan.why.text.length <= 400);
  // The AI asked for the admin step first; the host runs user-scope steps first, each group in the AI's order.
  assert.deepEqual(plan.steps.map((s) => s.actionId), ['setting.set', 'cleanup.empty_recycle_bin', 'startup.disable', 'service.disable']);
  assert.deepEqual(plan.steps.map((s) => s.n), [1, 2, 3, 4]);
  assert.deepEqual(plan.steps.map((s) => s.privilege), ['none', 'none', 'none', 'admin']);
  for (const s of plan.steps) {
    for (const k of STEP_KEYS) assert.ok(k in s, k);
    assert.match(s.contentHash, /^[0-9a-f]{64}$/);
    assert.ok(s.rows.length >= 1 && s.rows.length <= 4);
  }
  assert.equal(plan.steps[1].why, 'It holds 3.4 GB of files you already deleted.');
  assert.deepEqual(plan.flags, ['irreversible', 'data_loss']);   // canonical order
  assert.equal(plan.privilege, 'admin');
  assert.equal(plan.steps[1].undo.supported, false);
});

test('ineligible step and overlapping resources make the whole plan ineligible, naming the step', async () => {
  const be = setup();
  const bad = await be.call('fixture.proposeFix', { sample: 'ineligible' });
  assert.equal(bad.status, 'ineligible');
  assert.deepEqual(bad.steps[1], { n: 2, status: 'ineligible', code: 'protected', message: 'Optimus doesn’t change security services' });
  const pb = await be.call('action.get', { planId: bad.planId });
  assert.equal(pb.status, 'ineligible');
  assert.equal(pb.ineligible.code, 'protected');
  assert.deepEqual(pb.ineligible.steps.map((s) => s.n), [2]);
  assert.equal(pb.steps.length, 2);   // the card can still say which step
  const c = await be.call('fixture.proposeFix', { sample: 'conflict' });
  assert.equal(c.status, 'ineligible');
  assert.equal(c.steps[0].status, 'ok');
  assert.equal(c.steps[1].code, 'conflict');
  // Nothing ineligible can be approved.
  assert.deepEqual(await be.call('shell.approvePlan', { planId: c.planId }), { status: 'unavailable', error: 'ineligible' });   // PlanApproval: no dialog
  assert.equal(c.steps[1].message, 'Changes the same thing as step 1.');
  // Caps, as the host: more than six steps (a task allowed more) → too_large with steps []; an over-long title or reason is
  // refused by the argument check (invalid_arguments), as is more steps than the task allows.
  const step = { actionId: 'setting.set', parameters: { settingId: 'privacy.advertising_id', value: 'off' } };
  const big = await be.call('fixture.proposeFix', { args: { title: 'x', why: 'y', steps: Array(7).fill(step) }, fixSteps: 8 });
  assert.equal(big.status, 'ineligible');
  assert.deepEqual(big.steps, []);
  const bp = await be.call('action.get', { planId: big.planId });
  assert.deepEqual([bp.ineligible.code, bp.ineligible.message, bp.ineligible.steps, bp.ineligible.manualUri, bp.steps], ['too_large', 'More than 6 steps.', [], null, []]);
  assert.equal((await be.call('fixture.proposeFix', { args: { title: 'x', why: 'y', steps: Array(7).fill(step) } })).error.code, 'invalid_arguments');
  assert.equal((await be.call('fixture.proposeFix', { args: { title: 'x', why: 'w'.repeat(401), steps: [step] } })).error.code, 'invalid_arguments');
  assert.equal((await be.call('fixture.proposeFix', { args: { title: 't'.repeat(81), why: 'w', steps: [step] } })).error.code, 'invalid_arguments');
  // A step that is not a setting-kind action, or an unknown action → not_supported.
  const ns = await be.call('fixture.proposeFix', { args: { title: 'x', why: 'y', steps: [step, { actionId: 'defender.quick_scan' }, { actionId: 'made.up' }] } });
  assert.deepEqual(ns.steps.slice(1).map((s) => s.code), ['not_supported', 'not_supported']);
  const np = await be.call('action.get', { planId: ns.planId });
  assert.deepEqual(np.steps.map((s) => [s.label, s.handlerVersion]), [['Change a Windows setting', '1.0.0'], ['Quick scan', '1.0.0'], ['made.up', null]]);
});

test('the hashes change when a step changes', async () => {
  const be = setup();
  const mk = (value) => be.call('fixture.proposeFix', { args: { title: 'Files', why: 'Show them.', steps: [
    { actionId: 'setting.set', parameters: { settingId: 'explorer.file_extensions', value } }] } });
  const a = await be.call('action.get', { planId: (await mk('shown')).planId });
  const b = await be.call('action.get', { planId: (await mk('shown')).planId });
  assert.equal(a.steps[0].contentHash, b.steps[0].contentHash);
  // Another target for the same value: a different step, so a different contentHash and planHash.
  const c = await be.call('action.get', { planId: (await be.call('fixture.proposeFix', { args: { title: 'Files', why: 'Show them.', steps: [
    { actionId: 'setting.set', parameters: { settingId: 'privacy.advertising_id', value: 'off' } }] } })).planId });
  assert.notEqual(a.steps[0].contentHash, c.steps[0].contentHash);
  assert.notEqual(a.planHash, c.planHash);
});

test('a failed middle step → partial: earlier receipts and Undo kept, later steps skipped; step events carry progress', async () => {
  const be = setup();
  const r = await be.call('fixture.proposeFix', { sample: 'startup' });
  await be.call('fixture.failStep', { n: 2 });
  const evs = [];
  be.onEvent((e) => { if (e.type === 'operation.state') evs.push(e.payload); });
  const done = ended(be, r.planId);
  const a = await be.call('shell.approvePlan', { planId: r.planId });
  assert.equal(a.status, 'approved');
  const last = (await done).payload;
  assert.equal(last.state, 'partial');
  assert.deepEqual(evs.filter((e) => e.step && e.step.n === 1).map((e) => e.step.state), ['prepared', 'attempted', 'verified']);
  assert.ok(evs.some((e) => e.progress === 'Step 2 of 3'));
  const op = await be.call('operation.get', { operationId: a.operationId });
  assert.deepEqual(op.steps.map((s) => s.state), ['verified', 'failed', 'skipped']);
  assert.ok(op.steps[0].receipt.receiptId);
  assert.equal(op.steps[0].undo.available, true);
  assert.equal(op.error.n, 2);
  assert.equal((await be.call('action.get', { planId: r.planId })).status, 'partial');
  const v = FIX.fixView(await be.call('action.get', { planId: r.planId }), op);
  assert.equal(v.state, 'partial');
  assert.deepEqual(v.steps.map((s) => s.state), ['done', 'failed', 'skipped']);
  assert.deepEqual(v.steps.map((s) => s.undo), [true, false, false]);
  // The first step failing: nothing written → failed.
  const r2 = await be.call('fixture.proposeFix', { args: { title: 'Ads', why: 'No ads.', steps: [{ actionId: 'setting.set', parameters: { settingId: 'privacy.advertising_id', value: 'off' } }] } });
  await be.call('fixture.failStep', { n: 1, outcome: 'indeterminate' });
  const d2 = ended(be, r2.planId);
  await be.call('shell.approvePlan', { planId: r2.planId });
  assert.equal((await d2).payload.state, 'indeterminate');
});

test('Stop after this step: the step in flight finishes, the rest are skipped, the operation ends partial', async () => {
  const be = setup();
  const r = await be.call('fixture.proposeFix', { sample: 'startup' });
  const done = ended(be, r.planId);
  const a = await be.call('shell.approvePlan', { planId: r.planId });
  const stopped = await be.call('operation.stop', { operationId: a.operationId });
  assert.equal(stopped.operationId, a.operationId);
  assert.equal((await done).payload.state, 'partial');
  const op = await be.call('operation.get', { operationId: a.operationId });
  assert.deepEqual(op.steps.map((s) => s.state), ['verified', 'skipped', 'skipped']);
  assert.equal(op.error, null);
  assert.equal(FIX.fixView(await be.call('action.get', { planId: r.planId }), op).state, 'stopped');
  // Stop on the last step: nothing left, so verified.
  const r2 = await be.call('fixture.proposeFix', { args: { title: 'Ads', why: 'No ads.', steps: [{ actionId: 'setting.set', parameters: { settingId: 'privacy.advertising_id', value: 'off' } }] } });
  const d2 = ended(be, r2.planId);
  const a2 = await be.call('shell.approvePlan', { planId: r2.planId });
  await be.call('operation.stop', { operationId: a2.operationId });
  assert.equal((await d2).payload.state, 'verified');
});

test('undo of selected steps and Undo all: new fix plans of the verified, undoable steps in host order', async () => {
  const be = setup();
  const r = await be.call('fixture.proposeFix', { sample: 'risky' });
  const done = ended(be, r.planId);
  const a = await be.call('shell.approvePlan', { planId: r.planId });
  assert.equal((await done).payload.state, 'verified');
  const op = await be.call('operation.get', { operationId: a.operationId });
  // The Recycle Bin step can't be undone.
  assert.deepEqual(op.steps.map((s) => s.undo.available), [true, false, true, true]);
  await assert.rejects(be.call('action.undo', { idempotencyKey: key(), operationId: a.operationId, steps: [2] }), (e) => e.code === -32013 && e.data.n === 2);
  const one = await be.call('action.undo', { idempotencyKey: key(), operationId: a.operationId, steps: [3] });
  assert.equal(one.kind, 'fix');
  assert.equal(one.undoOf, a.operationId);
  assert.equal(one.title, null);
  assert.equal(one.why, null);
  assert.deepEqual(one.steps.map((s) => [s.n, s.actionId, s.label]), [[1, 'startup.disable', 'Undo: Turn off a startup app']]);
  assert.deepEqual(one.steps[0].rows, [{ label: 'Starts when you sign in', before: 'Off', after: 'On' }, { label: 'Startup entry', before: 'Microsoft Teams (personal)', after: 'Microsoft Teams (personal)' }]);
  assert.deepEqual(one.steps[0].flags, ['runs_code']);   // re-enabling lets its program run
  assert.equal(one.steps[0].parameters, null);              // the handler's undo request (startup: the target only)
  assert.deepEqual(one.steps[0].undoOf, { operationId: a.operationId, receiptId: op.steps[2].receipt.receiptId, n: 3 });
  const ud = ended(be, one.planId);
  // Marking the original step undone emits a step-less operation.state on the original operation.
  const orig = until(be, (e) => e.type === 'operation.state' && e.payload.operationId === a.operationId);
  const ua = await be.call('shell.approvePlan', { planId: one.planId });
  assert.equal((await ud).payload.state, 'verified');
  assert.equal((await orig).payload.step, undefined);
  assert.ok(ua.operationId);
  const after = await be.call('operation.get', { operationId: a.operationId });
  assert.equal(after.steps[2].undo.reason, 'undone');
  assert.equal(FIX.fixView(await be.call('action.get', { planId: r.planId }), after).steps[2].state, 'undone');
  // Undo all: the remaining undoable steps; a setting's undo carries the handler's restore request.
  const all = await be.call('action.undo', { idempotencyKey: key(), operationId: a.operationId });
  assert.deepEqual(all.steps.map((s) => [s.actionId, s.label]), [['setting.set', 'Undo: Change a Windows setting'], ['setting.set', 'Undo: Change a Windows setting']]);
  assert.deepEqual(all.steps[0].parameters, { settingId: 'privacy.app_launch_tracking', restore: { present: true, type: 'dword', data: 1 } });   // the host's UndoRequest: the recorded preimage
  assert.equal(all.status, 'awaiting_user');
  // With administrator steps on, Undo all keeps the host order: user scope first, then administrator.
  await be.call('fixture.setAdminAvailable', { available: true });
  await be.call('fixture.resetFixPc');
  const r2 = await be.call('fixture.proposeFix', { sample: 'risky' });
  const d2 = ended(be, r2.planId);
  const a2 = await be.call('shell.approvePlan', { planId: r2.planId });
  assert.equal((await d2).payload.state, 'verified');
  const all2 = await be.call('action.undo', { idempotencyKey: key(), operationId: a2.operationId });
  assert.deepEqual(all2.steps.map((s) => s.actionId), ['setting.set', 'startup.disable', 'service.disable']);
  assert.deepEqual(all2.steps.map((s) => s.privilege), ['none', 'none', 'admin']);
});

test('the tick rule: a flagged plan is declined when the simulated dialog was not ticked; unflagged plans are not affected', async () => {
  const be = setup();
  const risky = await be.call('fixture.proposeFix', { sample: 'risky' });
  await be.call('fixture.setApproval', { tick: false });
  assert.deepEqual(await be.call('shell.approvePlan', { planId: risky.planId }), { status: 'declined', error: 'not_ticked' });
  assert.equal((await be.call('action.get', { planId: risky.planId })).status, 'declined');
  const calm = await be.call('fixture.proposeFix', { sample: 'startup' });
  await be.call('fixture.setApproval', { tick: false });
  const d = ended(be, calm.planId);
  assert.equal((await be.call('shell.approvePlan', { planId: calm.planId })).status, 'approved');
  await d;
  const again = await be.call('fixture.proposeFix', { args: { title: 'Bin', why: 'Space.', steps: [{ actionId: 'cleanup.empty_recycle_bin' }] } });
  const d2 = ended(be, again.planId);
  assert.equal((await be.call('shell.approvePlan', { planId: again.planId })).status, 'approved');   // the tick was given (the default)
  await d2;
});

test('badge keys from flags and privilege (pure)', () => {
  assert.deepEqual(FIX.badgeKeys([], 'none'), []);
  assert.deepEqual(FIX.badgeKeys(null, 'admin'), ['admin']);
  assert.deepEqual(FIX.badgeKeys(['long', 'network', 'sign_out', 'restart', 'runs_code', 'data_loss', 'irreversible'], 'admin'),
    ['admin', 'irreversible', 'unsaved', 'program', 'restart', 'signout', 'network', 'slow']);
  assert.deepEqual(FIX.badgeKeys(['data_loss', 'made_up'], 'none'), ['unsaved']);
  assert.equal(FIX.needsTick(['data_loss']), true);
  assert.equal(FIX.needsTick(['irreversible']), true);
  assert.equal(FIX.needsTick(['restart', 'network']), false);
});

test('the in-app assistant proposes a fix (propose_fix) only with "multi-step fixes" allowed', async () => {
  const be = setup();
  const o = await be.call('onboarding.get');
  await be.call('onboarding.save', { expectedRevision: o.revision, patch: { inventoryConsent: 'skipped', step: 'complete' } });
  const fin = until(be, (e) => e.type === 'scan.finished');
  await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  const reportId = (await fin).payload.reportId;
  await be.call('shell.requestApiKey', { kind: 'anthropic_api' });
  await choose(be, 'anthropic_api');
  const ask = async (changes) => {
    const t = await be.call('agent.start', { idempotencyKey: key(), goal: 'Startup', recipientKind: 'anthropic_api', scope: { reportId }, grant: { diagnostics: [], changes } });
    const tools = [];
    be.onEvent((e) => { if (e.stream === 'task:' + t.taskId && e.type === 'agent.tool' && e.payload.status !== 'running') tools.push(e.payload); });
    const msg = until(be, (e) => e.type === 'agent.message' && e.stream === 'task:' + t.taskId);
    await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'Can you speed up startup?' });
    await msg;
    await new Promise((res) => setTimeout(res, 20));
    return tools;
  };
  const withFix = await ask('fix');
  const pf = withFix.find((x) => x.tool === 'propose_fix');
  assert.ok(pf && pf.status === 'complete' && pf.planId);
  const plan = await be.call('action.get', { planId: pf.planId });
  assert.equal(plan.kind, 'fix');
  assert.equal(plan.requestedBy.kind, 'in_app');
  assert.equal(plan.steps.length, 3);
  const askOnly = await ask('ask');
  assert.equal(askOnly.some((x) => x.tool === 'propose_fix'), false);
});

test('a fix that comes back stale or expired from approval is shown as expired, never previewed again', () => {
  const fix = { kind: 'fix', actionId: null, undoOf: null, status: 'awaiting_user', steps: [{ n: 1, label: 'One' }, { n: 2, label: 'Two' }] };
  assert.equal(FIX.afterStale(fix, true), 'expire');            // in a finding card too: actionId is null, nothing to preview
  assert.equal(FIX.afterStale(fix, false), 'expire');
  assert.equal(FIX.afterStale({ kind: 'single', actionId: 'power.select_scheme' }, true), 'repreview');
  assert.equal(FIX.afterStale({ kind: 'single', actionId: 'power.select_scheme' }, false), 'close');
  assert.equal(FIX.afterStale({ kind: 'fix', undoOf: 'op-1' }, false), 'undo');
  assert.equal(FIX.afterStale(null, false), 'close');
  const v = FIX.fixView(Object.assign({}, fix, { status: 'expired' }), null);
  assert.equal(v.state, 'expired');
  assert.deepEqual(v.steps.map((s) => s.state), [null, null]);
  assert.equal(FIX.fixView(Object.assign({}, fix, { status: 'declined' }), null).state, 'failed');
});

test("design ApprovalCard states (v51) from a plan and its operation: can't run, unclear, stopped, partial, settings", () => {
  const plan = { kind: 'fix', status: 'awaiting_user', steps: [{ n: 1, label: 'One', flags: [] }, { n: 2, label: 'Two', flags: [] }, { n: 3, label: 'Three', flags: ['irreversible'] }] };
  // Ineligible: Can't run, the listed step blocked with the host's message.
  const cr = FIX.fixView(Object.assign({}, plan, { status: 'ineligible', ineligible: { code: 'protected', message: 'Step 2: Windows protects this setting.', steps: [{ n: 2, status: 'ineligible', code: 'protected', message: 'Windows protects this setting.' }] } }), null);
  assert.deepEqual([cr.state, cr.steps.map((s) => s.state), cr.steps[1].note], ['cantrun', [null, 'blocked', null], 'Windows protects this setting.']);
  const op = (steps, extra) => Object.assign({ state: 'partial', steps: steps.map(([n, state, undo]) => ({ n, state, receipt: state === 'verified' ? { text: 'r' + n } : null, undo: undo || { available: false, reason: 'not_verified' }, error: state === 'failed' || state === 'indeterminate' ? { message: 'boom' } : null })) }, extra);
  const yes = { available: true, reason: null };
  // Unclear: the design's own grey line (no host note); a run with only unclear steps is partial, never "Nothing changed".
  const un = FIX.fixView(plan, op([[1, 'verified', yes], [2, 'indeterminate'], [3, 'skipped']], { state: 'indeterminate' }));
  assert.deepEqual([un.state, un.steps[1].state, un.steps[1].note, un.steps[0].receipt], ['partial', 'unclear', null, 'r1']);
  assert.equal(FIX.fixView(plan, op([[1, 'indeterminate'], [2, 'skipped'], [3, 'skipped']], { state: 'indeterminate' })).state, 'partial');
  // Failed: partial with done steps, failed without; the failed step keeps the host's note.
  const fp = FIX.fixView(plan, op([[1, 'verified', yes], [2, 'failed'], [3, 'skipped']]));
  assert.deepEqual([fp.state, fp.steps[1].note], ['partial', 'boom']);
  assert.equal(FIX.fixView(plan, op([[1, 'failed'], [2, 'skipped'], [3, 'skipped']], { state: 'failed' })).state, 'failed');
  // Stopped: done steps and skipped ones, nothing failed.
  assert.equal(FIX.fixView(plan, op([[1, 'verified', yes], [2, 'skipped'], [3, 'skipped']])).state, 'stopped');
  // Settings: a done step Optimus can't undo, not irreversible, with a Settings page on the operation.
  const ns = { available: false, reason: 'not_supported' };
  const st = FIX.fixView(plan, op([[1, 'verified', ns], [2, 'verified', yes], [3, 'verified', ns]], { state: 'verified', manualUri: 'ms-settings:gaming-gamebar' }));
  assert.deepEqual([st.state, st.settingsUri, st.steps.map((s) => s.settings), st.steps.map((s) => s.undo)], ['verified', 'ms-settings:gaming-gamebar', [true, false, false], [false, true, false]]);
  assert.deepEqual(FIX.fixView(plan, op([[1, 'verified', ns], [2, 'verified', yes], [3, 'verified', ns]], { state: 'verified' })).steps.map((s) => s.settings), [false, false, false]);
  // Running, and approved but not started: applying.
  assert.equal(FIX.fixView(plan, op([[1, 'prepared'], [2, 'pending'], [3, 'pending']], { state: 'running' })).state, 'applying');
  assert.equal(FIX.fixView(Object.assign({}, plan, { status: 'approved' }), null).state, 'applying');
  assert.equal(FIX.fixView(plan, null).state, 'awaiting');
});