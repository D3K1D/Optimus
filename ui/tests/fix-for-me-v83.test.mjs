// node --test ui/tests/   — Design System Versions 82–83 (FixMoreStates) in the real app: AppFlow and every component with its own
// state (mini-react.mjs) on the fixture backend. Each new state is reached the way the host reaches it: through the fixture
// where it can produce it, else with the host's own event (card.fix, agent.checks) sent to the app.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { startFullApp, selectors, all, text, byClass, button, hasClass, tick } from './app-full.mjs';

const T = (n) => (n ? text(n) : '');
async function fix(app, reply) {
  await app.inner.call('fixture.setFixReply', { mode: reply });
  const cid = app.reportCardId();
  await app.click(button(app.card(cid), 'Fix for me'));
  return cid;
}
async function planInCard(app) {
  const cid = await fix(app, 'plan');
  await app.until(() => app.panel(cid) && button(app.panel(cid), 'Approve…'), 'the plan in the card');
  return cid;
}
const cardFix = (state, extra) => Object.assign({ state, by: 'Claude', attemptId: 'fix-00000000000000a1', planId: null, operationId: null, requestId: null, opinion: null, code: null,
  updatedAt: new Date().toISOString() }, extra);
const sendFix = (app, cid, fx) => app.emit({ stream: 'user', type: 'card.fix', payload: { cardId: cid, reportId: null, fix: fx } });
const request = (state, code, checks, extra) => Object.assign({ requestId: 'chk-00000000000000b1', revision: 1, taskId: 'task-x', cardId: null, state, code: code || null, retryAt: null,
  expiresAt: new Date(Date.now() + 600000).toISOString(),
  checks: checks.map((c, i) => ({ operationId: 'op-' + i, id: 'storage.health', label: c[0], state: c[1] })) }, extra);
const sendChecks = (app, q) => app.emit({ stream: 'user', type: 'agent.checks', payload: q });
const LABELS = ['Disk health', 'Drive encryption status'];
const marks = (n) => byClass(n, 'ot-adminchecks-list')[0].children.map((li) => [hasClass(li, 'is-unread'), T(li)]);

/* ---------- unclear (Not confirmed) ---------- */
test('unclear: badge Not confirmed, its line, and the step’s own Check again reads the operation again', async () => {
  const app = await startFullApp();
  const cid = await planInCard(app);
  await app.inner.call('fixture.failStep', { n: 1, outcome: 'indeterminate' });
  await app.click(button(app.panel(cid), 'Approve…'));
  await app.until(() => T(app.panel(cid)).includes('Not confirmed') && !!button(app.panel(cid), 'Check again'), 'unclear, with the step’s Check again');
  const p = app.panel(cid);
  assert.ok(hasClass(p, 'is-unclear'));
  assert.ok(T(p).includes('Optimus couldn’t confirm this worked. Press Check again on the step marked Unclear.'));
  assert.ok(!T(p).includes('Failed'));
  const mark = app.calls.length;
  await app.click(button(p, 'Check again'));
  assert.equal(app.after(mark, 'operation.get').length, 1);
});

/* ---------- stopped ---------- */
test('stopped: the plan’s Stopped state in the card, never Failed', async () => {
  const app = await startFullApp();
  const cid = await planInCard(app);
  const planId = app.calls.filter((c) => c.method === 'action.get' && c.result).pop().result.planId;
  await app.click(button(app.panel(cid), 'Approve…'));
  await app.until(() => T(app.panel(cid)).includes('Verified'), 'ran');
  sendFix(app, cid, cardFix('stopped', { planId }));
  await app.settle();
  const p = app.panel(cid);
  assert.ok(hasClass(p, 'is-stopped'));
  assert.ok(T(p).includes('Stopped'));
  assert.ok(T(p).includes('Later steps weren’t run.'));
  assert.ok(!T(p).includes('Failed'));
});

/* ---------- blocked causes ---------- */
test('blocked: every code onto the design’s sentence; Permissions only for administrator rights that are off; Fix for me always', async () => {
  const app = await startFullApp();
  const cid = app.reportCardId();
  const want = [['admin_not_allowed', 'Everything in it needs administrator rights, which are off.', true],
    ['admin_unavailable', 'It needs administrator rights, which aren’t available in this version yet.', false],
    ['changed_since', 'The setting changed since Claude looked.', false], ['stale_plan', 'The setting changed since Claude looked.', false],
    ['managed', 'Windows or your organisation manages this setting.', false], ['protected', 'It would change a protected part of Windows.', false],
    ['conflict', 'Two of its steps change the same thing.', false], ['too_large', 'It has too many steps.', false],
    ['unsupported', 'This PC doesn’t support it.', false], ['not_supported', 'This PC doesn’t support it.', false], ['hardware_limit', 'This PC doesn’t support it.', false],
    ['already_set', 'Optimus can’t run it.', false]];
  for (const [code, line, perm] of want) {
    sendFix(app, cid, cardFix('blocked', { code }));
    await app.settle(30);
    const p = app.panel(cid);
    assert.ok(T(p).includes('Claude’s plan can’t be done. ' + line), code + ': ' + T(p));
    assert.equal(!!button(p, 'Permissions'), perm, code);
    assert.ok(button(p, 'Fix for me'), code);
  }
});

/* ---------- AdminChecks endings beside the chat ---------- */
test('checks beside the chat: expired, unavailable, cancelled stopped / ended / limit (the host’s events)', async () => {
  const app = await startFullApp({ flag: true, admin: true });
  let n = 0;
  for (const [q, line] of [
    [request('expired', null, [[LABELS[1], 'expired']]), 'Nobody answered, so the checks were skipped. Claude goes on without them.'],
    [request('cancelled', 'admin_unavailable', [[LABELS[1], 'cancelled']]), 'Administrator checks aren’t available in this version yet. Claude goes on without them.'],
    [request('cancelled', 'stopped', [[LABELS[1], 'cancelled']]), 'Checks cancelled because you stopped Claude.'],
    [request('cancelled', 'task_ended', [[LABELS[1], 'cancelled']]), 'Checks cancelled because the conversation ended.'],
    [request('cancelled', 'exhausted', [[LABELS[1], 'cancelled']]), 'Checks cancelled because Claude reached its step limit.']]) {
    sendChecks(app, Object.assign(q, { requestId: 'chk-00000000000000e' + (n++) }));
    await app.settle(30);
    assert.ok(all(app.tree).some((x) => hasClass(x, 'ot-checks-side') && T(x).includes(line)), line);
  }
});
test('checks beside the chat: the fixture’s own expiry reads “Nobody answered…”', async () => {
  const app = await startFullApp({ flag: true, admin: true });
  await app.inner.call('fixture.setFixReply', { mode: 'none' });
  await app.click(button(app.card(app.reportCardId()), 'Fix for me'));
  await app.settle(300);
  const got = [];
  app.inner.onEvent((e) => { if (e.type === 'agent.checks') got.push(e.payload.requestId); });
  await app.inner.call('fixture.agentTool', { taskId: app.taskId(), tool: 'run_diagnostic', args: { diagnosticId: 'security.encryption' } });
  await app.until(() => app.side() && button(app.side(), 'Run checks') && got.length > 0, 'the request');
  await app.inner.call('fixture.expireChecks', { requestId: got[0] });
  await app.until(() => T(app.side()).includes('Nobody answered, so the checks were skipped.'), 'expired');
});

/* ---------- mixed results (v83): beside the chat and in a card ---------- */
for (const [name, states, head, unread] of [
  ['all read', ['complete', 'complete'], 'Checks done. Claude can use the results.', [false, false]],
  ['some unread', ['complete', 'failed'], 'Some checks couldn’t be read. Claude uses the rest.', [false, true]],
  ['none read', ['failed', 'interrupted'], 'None of the checks could be read. Claude goes on without them.', [true, true]]]) {
  test('checks beside the chat, ' + name + ': the head line and each check marked', async () => {
    const app = await startFullApp({ flag: true, admin: true });
    sendChecks(app, request('complete', null, LABELS.map((l, i) => [l, states[i]])));
    await app.settle();
    const s = app.side();
    assert.ok(T(s).startsWith(head), T(s));
    assert.deepEqual(marks(s).map((m) => m[0]), unread);
    marks(s).forEach((m, i) => assert.equal(m[1], LABELS[i] + (unread[i] ? ' · couldn’t be read' : '')));
  });
  test('checks in a card, ' + name + ': the request in the card, then the working line while the AI reads them', async () => {
    const app = await startFullApp({ flag: true, admin: true });
    const cid = app.reportCardId();
    sendFix(app, cid, cardFix('waiting_checks', { requestId: 'chk-00000000000000b1' }));
    sendChecks(app, request('complete', null, LABELS.map((l, i) => [l, states[i]]), { cardId: cid }));
    await app.settle();
    assert.ok(T(app.panel(cid)).startsWith(head), T(app.panel(cid)));
    assert.deepEqual(marks(app.panel(cid)).map((m) => m[0]), unread);
    assert.equal(app.side(), null, 'a card’s request stays in the card');
    sendFix(app, cid, cardFix('working'));
    await app.settle();
    const line = name === 'all read' ? 'Checks done. Claude is reading the results.' : head;
    assert.ok(T(app.panel(cid)).includes('Claude is looking into it…' + line), T(app.panel(cid)));
  });
}

/* ---------- working: how the card's checks ended ---------- */
test('working after the card’s checks ended: declined, expired, unavailable, failed, timeout, cancelled', async () => {
  const app = await startFullApp({ flag: true, admin: true });
  const cid = app.reportCardId();
  const cases = [[['declined', 'user_declined'], 'Checks skipped. Claude goes on without them.'],
    [['expired', null], 'Nobody answered, so the checks were skipped. Claude goes on without them.'],
    [['cancelled', 'admin_unavailable'], 'Administrator checks aren’t available in this version yet. Claude goes on without them.'],
    [['failed', 'broker_missing'], 'The checks couldn’t run. Claude goes on without them.'],
    [['failed', 'timeout'], 'The checks took too long and were stopped. Claude goes on without them.'],
    [['cancelled', 'stopped'], 'Checks cancelled because you stopped Claude.']];
  let n = 0;
  for (const [[st, code], line] of cases) {
    sendChecks(app, request(st, code, [[LABELS[0], st]], { cardId: cid, requestId: 'chk-00000000000000d' + (n++) }));
    sendFix(app, cid, cardFix('working'));
    await app.settle(30);
    assert.ok(T(app.panel(cid)).includes(line), st + '/' + code + ': ' + T(app.panel(cid)));
    assert.ok(button(app.panel(cid), 'Stop'));
  }
});

/* ---------- script ---------- */
test('script: the card points to the script beside the chat; Show script finds it; no Fix for me while it waits', async () => {
  const app = await startFullApp();
  const cid = app.reportCardId();
  const plan = await app.inner.call('fixture.proposeScript', { sample: 'plain' });
  assert.ok(plan.planId, JSON.stringify(plan));
  sendFix(app, cid, cardFix('plan_ready', { planId: plan.planId }));
  await app.until(() => app.panel(cid) && button(app.panel(cid), 'Show script'), 'the script line');
  const p = app.panel(cid);
  assert.ok(T(p).includes('Claude wrote a script for this. Read it beside the chat.'));
  assert.equal(button(app.card(cid), 'Fix for me'), null);
  assert.ok(all(app.tree).some((n) => hasClass(n, 'ot-script') && hasClass(n, 'ot-plan-' + plan.planId)), 'the script approval beside the chat');
  selectors.length = 0;
  await app.click(button(p, 'Show script'));
  assert.ok(selectors.includes('.ot-plan-' + plan.planId));
});

/* ---------- needs you: Check again ---------- */
test('needs you: Checking… while it runs; still there; gone for about 2 seconds, then the deck is read again', async () => {
  const app = await startFullApp();
  const cid = await fix(app, 'needs_user');
  await app.until(() => app.panel(cid) && button(app.panel(cid), 'Check again'), 'needs you');
  let answer;
  app.gate = (m) => (m === 'manual.recheck' ? new Promise((r) => { answer = r; }) : undefined);
  await app.click(button(app.panel(cid), 'Check again'));
  const busy = button(app.panel(cid), 'Checking…');
  assert.ok(busy && busy.props.disabled, 'Checking… and disabled');
  answer({ status: 'awaiting_rescan' });
  await app.settle();
  assert.ok(T(app.panel(cid)).includes('Checked again: the problem is still there.'));
  assert.ok(button(app.panel(cid), 'Check again') && T(app.panel(cid)).includes('This needs a step only you can take.'), 'the note and the button stay');
  await app.click(button(app.panel(cid), 'Check again'));
  answer({ status: 'verified_by_rescan' });
  await app.settle();
  assert.ok(T(app.panel(cid)).includes('Checked again: the problem is gone.'));
  const mark = app.calls.length;
  await app.settle(1000);
  assert.equal(app.after(mark, 'followup.get').length, 0, 'not before about 2 seconds');
  await app.until(() => app.after(mark, 'followup.get').length === 1, 'the deck read again', 3000);
});
