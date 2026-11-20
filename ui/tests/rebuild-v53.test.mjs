// node --test ui/tests/   — design Version 53 (RebuildCases): single changes as one-step approval cards, whole-plan
// can't run, and why an approval couldn't start. Pure helpers in fix-plan.js.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const FIX = require('../js/fix-plan.js');

const single = (o) => Object.assign({ planId: 'plan-1', kind: 'setting', actionId: 'power.select_scheme', label: 'Switch power plan', what: 'Uses Balanced when idle.',
  status: 'awaiting_user', rows: [{ label: 'Active power plan', before: 'High performance', after: 'Balanced' }], context: [['Power', 'On battery · 64%']],
  tradeoffs: ['Slightly lower peak speed'], flags: [], privilege: 'none', undo: { supported: true, conditions: 'Only while Balanced is still active' }, ineligible: null }, o);

test('a single change is one step: rows, its sentence, context as name rows, its trade-off line (v65), badges', () => {
  const v = FIX.planView(single(), null);
  assert.equal(v.state, 'awaiting');
  assert.equal(v.steps.length, 1);
  const s = v.steps[0];
  assert.equal(s.label, 'Switch power plan');
  assert.equal(s.does, 'Uses Balanced when idle.');
  assert.deepEqual(s.rows, [['Active power plan', 'High performance', 'Balanced'], ['Power', 'On battery · 64%', null]]);
  assert.equal(s.tradeoff, 'Slightly lower peak speed');
  assert.deepEqual(s.badges, []);
  assert.equal(s.state, null);
  assert.deepEqual(FIX.planView(single({ flags: ['irreversible', 'data_loss'], privilege: 'admin' }), null).steps[0].badges, ['admin', 'irreversible', 'unsaved']);
});

test('a single change follows its operation: applying, verified with receipt and Undo, failed, unclear', () => {
  const op = (state, more) => Object.assign({ operationId: 'op-1', planId: 'plan-1', state, receipt: null, undo: null, error: null }, more);
  assert.equal(FIX.planView(single({ status: 'running' }), op('prepared')).state, 'applying');
  assert.equal(FIX.planView(single({ status: 'running' }), op('attempted')).steps[0].state, 'running');
  const ok = FIX.planView(single({ status: 'verified' }), op('verified', { receipt: { text: 'Balanced is active, read back from Windows.' }, undo: { available: true, reason: null } }));
  assert.equal(ok.state, 'verified');
  assert.deepEqual([ok.steps[0].state, ok.steps[0].receipt, ok.steps[0].undo, ok.steps[0].settings], ['done', 'Balanced is active, read back from Windows.', true, false]);
  // Optimus can't undo it and the finding names a Settings page: Open settings in place of Undo.
  const off = single({ actionId: 'startup.disable', undo: { supported: false, conditions: null } });
  const done = FIX.planView(off, op('verified', { receipt: { text: 'Defender finished the scan: no threats found.' }, undo: { available: false, reason: 'not_supported' } }), 'windowsdefender://threat');
  assert.deepEqual([done.state, done.steps[0].undo, done.steps[0].settings, done.settingsUri], ['verified', false, true, 'windowsdefender://threat']);
  assert.equal(FIX.planView(off, op('verified', { undo: { available: false, reason: 'not_supported' } })).steps[0].settings, false);   // no page: neither
  // A Defender scan changes nothing to put back: no Undo, no Settings page.
  const scan = single({ kind: 'provider', actionId: 'defender.quick_scan', undo: { supported: false, conditions: null } });
  assert.equal(FIX.planView(scan, op('verified', { undo: { available: false, reason: 'not_supported' } }), 'windowsdefender://threat').steps[0].settings, false);
  const failed = FIX.planView(single(), op('failed', { error: { code: 'write_failed', message: 'Windows refused the change.' } }));
  assert.deepEqual([failed.state, failed.steps[0].state, failed.steps[0].note], ['failed', 'failed', 'Windows refused the change.']);
  const unclear = FIX.planView(single(), op('indeterminate'));
  assert.deepEqual([unclear.state, unclear.steps[0].state], ['partial', 'unclear']);
  assert.equal(FIX.planView(single(), op('rolled_back_failed')).steps[0].state, 'unclear');
  assert.equal(FIX.planView(single({ status: 'expired' }), null).state, 'expired');
});

test('can’t run as a whole: no blocked step, the plan’s reason and its Settings page', () => {
  const v = FIX.planView(single({ status: 'ineligible', rows: [], context: [], tradeoffs: [], ineligible: { code: 'handler_off', message: 'Change it in Windows Security', manualUri: 'windowsdefender://network' } }), null);
  assert.equal(v.state, 'cantrun');
  assert.equal(v.steps[0].state, null);
  assert.deepEqual([v.blockedWhy, v.planSettingsUri], ['Change it in Windows Security', 'windowsdefender://network']);
  // A fix whose own steps are named keeps the step banner (no whole-plan reason).
  const fix = { kind: 'fix', status: 'ineligible', steps: [{ n: 1, label: 'A' }, { n: 2, label: 'B' }], ineligible: { code: 'x', message: 'One can’t run.', steps: [{ n: 2, message: 'Needs admin' }] } };
  const fv = FIX.planView(fix, null);
  assert.deepEqual([fv.state, fv.steps[1].state, fv.steps[1].note, fv.blockedWhy, fv.planSettingsUri], ['cantrun', 'blocked', 'Needs admin', null, null]);
  // A fix refused as a whole (no step listed): the whole-plan banner.
  const whole = FIX.planView({ kind: 'fix', status: 'ineligible', steps: [{ n: 1, label: 'A' }], ineligible: { code: 'too_large', message: 'Too many steps.' } }, null);
  assert.deepEqual([whole.state, whole.blockedWhy], ['cantrun', 'Too many steps.']);
});

test('why an approval couldn’t start: the shell’s answer → the design’s errorCause (never “Unavailable” or “Not approved”)', () => {
  const E = FIX.approvalError;
  assert.equal(E({ status: 'approved', operationId: 'op-1' }), null);
  assert.equal(E({ status: 'stale' }), null);
  assert.equal(E({ status: 'expired' }), null);
  assert.deepEqual(E({ status: 'unavailable', error: 'action_active' }), { errorCause: 'busy', error: null });
  assert.deepEqual(E({ status: 'unavailable', error: 'scan_active' }), { errorCause: 'scan', error: null });
  assert.deepEqual(E({ status: 'unavailable', error: 'bench_active' }), { errorCause: 'benchmark', error: null });
  for (const e of [null, 'uac_cancelled', 'broker_cancelled', 'not_ticked']) assert.deepEqual(E({ status: 'declined', error: e }), { errorCause: 'closed', error: null }, String(e));
  assert.deepEqual(E({ status: 'cancelled' }), { errorCause: 'cancelled', error: null });
  // The plan no longer waits (already declined or cancelled): Close only.
  for (const e of ['cancelled', 'declined']) assert.deepEqual(E({ status: 'unavailable', error: e }), { errorCause: 'cancelled', error: null }, e);
  // Anything else: the design's fallback line, in the designer's words.
  for (const r of [{ status: 'error' }, { status: 'unavailable' }, { status: 'unavailable', error: 'verified' }, null]) {
    assert.deepEqual(E(r), { errorCause: null, error: 'Windows didn’t answer.' }, JSON.stringify(r));
  }
});
