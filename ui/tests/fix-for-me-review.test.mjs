// node --test ui/tests/   — review fixes of the Fix for me UI (P4 review, items 2, 3, 6, 7, 8 and (e)), each on the real app:
// AppFlow and every component with its own state (mini-react.mjs), on the fixture backend, clicking the rendered buttons.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { startFullApp, all, text, byClass, button, tick } from './app-full.mjs';

const later = (ms) => (run) => run().then((r) => tick(ms).then(() => r));
async function fix(app, reply) {
  await app.inner.call('fixture.setFixReply', { mode: reply });
  const cid = app.reportCardId();
  await app.click(button(app.card(cid), 'Fix for me'));
  return cid;
}
const switches = (n) => all(n).filter((x) => x.type === 'input' && x.props.role === 'switch');
async function planInCard(app) {
  const cid = await fix(app, 'plan');
  await app.until(() => app.panel(cid) && button(app.panel(cid), 'Approve…'), 'the plan in the card');
  return cid;
}
async function sideRequest(app) {
  const tid = app.taskId() || (await app.inner.call('agent.active')).tasks[0].taskId;
  await app.inner.call('fixture.agentTool', { taskId: tid, tool: 'run_diagnostic', args: { diagnosticId: 'security.encryption' } });
  await app.until(() => app.side() && button(app.side(), 'Run checks'), 'the request beside the chat');
}
// A task exists once Fix for me (or a question) started one.
async function withTask(app) {
  await app.inner.call('fixture.setFixReply', { mode: 'none' });
  const cid = app.reportCardId();
  await app.click(button(app.card(cid), 'Fix for me'));
  await app.until(() => app.panel(cid) && text(app.panel(cid)).includes('didn’t find a fix'), 'no fix');
  return cid;
}

/* ---------- 2: delayed replies never overwrite newer states; one call at a time ---------- */
test('review 2: a terminal agent.checks event before the older checks.approve reply stays (done, not running)', async () => {
  const app = await startFullApp({ flag: true, admin: true });
  await withTask(app);
  await sideRequest(app);
  app.gate = (m, p, run) => (m === 'checks.approve' ? later(250)(run) : undefined);
  await app.click(button(app.side(), 'Run checks'));
  await app.until(() => text(app.side()).includes('Checks done'), 'done from the events');
  await app.settle(400);                                   // the reply (running) arrives now
  assert.ok(text(app.side()).includes('Checks done'), 'still done: ' + text(app.side()));
});
test('review 2: Run checks and Not now do nothing while a call for the request is in flight', async () => {
  const app = await startFullApp({ flag: true, admin: true });
  await withTask(app);
  await sideRequest(app);
  app.holdEvents((ev) => ev.type === 'agent.checks');
  app.gate = (m, p, run) => (m === 'checks.approve' ? later(150)(run) : undefined);
  const mark = app.calls.length;
  const run = button(app.side(), 'Run checks');
  run.props.onClick(); run.props.onClick();
  button(app.side(), 'Not now').props.onClick();
  await app.settle(300);
  assert.equal(app.after(mark, 'checks.approve').length, 1);
  assert.equal(app.after(mark, 'checks.decline').length, 0);
  app.releaseEvents();
  await app.settle(300);
});

/* ---------- 3: card.fix events are never lost or undone ---------- */
test('review 3: two card.fix events before a render both show', async () => {
  const app = await startFullApp();
  const [a, b] = app.cards().map((c) => c.props['data-card']);
  const rid = (await app.inner.call('agent.active')).tasks.length ? null : null;
  const fixOf = (s) => ({ state: s, by: 'Claude', attemptId: 'fix-0000000000000001', planId: null, operationId: null, requestId: null, opinion: null, code: null, updatedAt: new Date().toISOString() });
  app.emit({ stream: 'user', type: 'card.fix', payload: { cardId: a, reportId: rid, fix: fixOf('no_fix') } });
  app.emit({ stream: 'user', type: 'card.fix', payload: { cardId: b, reportId: rid, fix: fixOf('no_fix') } });
  await app.settle();
  for (const cid of [a, b]) assert.ok(app.panel(cid) && text(app.panel(cid)).includes('didn’t find a fix'), cid);
});
test('review 3: a deck snapshot asked for before a card.fix event doesn’t bring the older fix back', async () => {
  const app = await startFullApp();
  const cid = await fix(app, 'needs_user');
  await app.until(() => app.panel(cid) && button(app.panel(cid), 'Check again'), 'needs you');
  let release;
  app.gate = (m, p, run) => (m === 'followup.get' ? run().then((r) => new Promise((res) => { release = () => res(r); })) : undefined);
  app.emit({ stream: 'task:' + app.taskId(), type: 'agent.tool', payload: { invocationId: 'inv-y', tool: 'note_card', status: 'complete', turnId: null } });   // the deck is read (held)
  await app.until(() => !!release, 'the deck read on its way');
  app.gate = null;
  app.emit({ stream: 'user', type: 'card.fix', payload: { cardId: cid, reportId: null, fix: null } });   // dismissed elsewhere
  await app.settle();
  assert.equal(app.panel(cid), null);
  release();
  await app.settle(150);
  assert.equal(app.panel(cid), null, 'the older snapshot did not bring “needs you” back');
});

/* ---------- 6: step switches ---------- */
test('review 6: the switches are locked while the approval is on its way, and a replacement plan starts with every step on', async () => {
  const app = await startFullApp();
  const cid = await planInCard(app);
  let sw = switches(app.panel(cid));
  assert.equal(sw.length, 3);
  sw[1].props.onChange({ target: { checked: false } });
  await app.settle();
  assert.deepEqual(switches(app.panel(cid)).map((s) => s.props.checked), [true, false, true]);
  // The Windows approval of the narrowed plan is closed: the new plan [1, 3] waits again.
  let answer;
  app.gate = (m, p, run) => (m === 'shell.approvePlan' ? new Promise((res) => { answer = () => res({ status: 'declined' }); }) : undefined);
  await app.click(button(app.panel(cid), 'Approve…'));
  await app.until(() => !!answer, 'the approval open');
  await app.until(() => switches(app.panel(cid)).length === 2, 'the narrowed plan in the card');
  assert.ok(switches(app.panel(cid)).every((s) => s.props.disabled), 'locked while Windows asks');
  answer();
  await app.until(() => switches(app.panel(cid)).every((s) => !s.props.disabled), 'unlocked');
  assert.deepEqual(switches(app.panel(cid)).map((s) => s.props.checked), [true, true], 'the new plan starts with both steps on');
});

/* ---------- 7: a failed plan read is tried again ---------- */
test('review 7: a card plan whose first read failed comes back at the next reconciliation', async () => {
  const app = await startFullApp();
  let fail = true;
  app.gate = (m) => (m === 'action.get' && fail ? Promise.reject({ code: -32000, message: 'lost' }) : undefined);   // every read fails for now
  const cid = await fix(app, 'plan');
  await app.settle(800);
  assert.ok(!(app.panel(cid) && button(app.panel(cid), 'Approve…')), 'no plan yet (its read failed)');
  fail = false;
  // A reconciliation: the deck is read again (here: a note on a card, as the AI's tools do).
  app.emit({ stream: 'task:' + app.taskId(), type: 'agent.tool', payload: { invocationId: 'inv-x', tool: 'note_card', status: 'complete', turnId: null } });
  await app.until(() => app.panel(cid) && button(app.panel(cid), 'Approve…'), 'the plan read again');
});

/* ---------- 8: a narrow that finds drift ends the plan: the card is blocked ---------- */
test('review 8: an action.narrow that finds drift re-reads the plan and the deck; the card is blocked (stale_plan) with Fix for me', async () => {
  const app = await startFullApp();
  const cid = await planInCard(app);
  switches(app.panel(cid))[1].props.onChange({ target: { checked: false } });
  await app.settle();
  await app.inner.call('fixture.changeStartup', { name: 'Spotify' }); // a kept step no longer matches the PC
  const mark = app.calls.length;
  await app.click(button(app.panel(cid), 'Approve…'));
  await app.until(() => app.panel(cid) && text(app.panel(cid)).includes('The setting changed since Claude looked.'), 'blocked: stale_plan');
  assert.ok(app.after(mark, 'action.narrow').some((c) => c.error && c.error.data && c.error.data.reason === 'stale_plan'));
  assert.equal(app.after(mark, 'action.get').length, 1);
  assert.ok(app.after(mark, 'followup.get').length >= 1);
  assert.ok(!app.toasts.includes('Updated'));
  assert.equal(button(app.panel(cid), 'Approve…'), null, 'the ended plan is not approvable');
  assert.ok(button(app.card(cid), 'Fix for me'), 'Fix for me again');
});

/* ---------- (e): no fix-turn reply under a card with a fix panel ---------- */
test('(e) the AI’s fix-turn reply is not shown under a card that shows a fix panel', async () => {
  const app = await startFullApp();
  const cid = await planInCard(app);
  await app.settle(300);
  assert.equal(byClass(app.card(cid), 'ot-reply').length, 0);
  assert.ok(!text(app.card(cid)).includes('I proposed a change for this card'));
});

/* ---------- 1: open requests come back after a reload or a reconnect (checks.list) ---------- */
test('review 1: requests missed while away come back from checks.list (one waiting out the cooldown keeps its wait); a card never shows an empty request', async () => {
  const app = await startFullApp({ flag: true, admin: true });
  await withTask(app);
  await sideRequest(app);
  await app.click(button(app.side(), 'Not now'));            // the cooldown starts
  await app.until(() => text(app.side()).includes('Checks skipped'), 'declined');
  // A new request while the app hears nothing (as when the connection is lost).
  app.holdEvents((ev) => ev.type === 'agent.checks');
  await app.inner.call('fixture.agentTool', { taskId: app.taskId(), tool: 'run_diagnostic', args: { diagnosticId: 'storage.health' } });
  await app.settle(100);
  assert.ok(!all(app.tree).some((n) => text(n) === 'Disk health'), 'not shown yet');
  const mark = app.calls.length;
  app.resume();
  const sideOf = (label) => all(app.tree).find((n) => /ot-checks-side/.test((n.props && n.props.className) || '') && text(n).includes(label)) || null;
  await app.until(() => !!sideOf('Disk health'), 'the request back');
  assert.equal(app.after(mark, 'checks.list').length, 1);
  const s = sideOf('Disk health');
  assert.ok(/You can run these in \d+ seconds?\./.test(text(s)), 'its wait: ' + text(s));
  assert.equal(button(s, 'Run checks').props.disabled, true);
  app.releaseEvents();
  // The wait ends (its one-second tick stops with it).
  await app.inner.call('fixture.endCooldown', {});
  await app.until(() => !button(sideOf('Disk health'), 'Run checks').props.disabled, 'back to ask');
});
test('review 1: a card waiting for checks whose request isn’t read yet shows no request to click', async () => {
  const app = await startFullApp({ flag: true, admin: true });
  const cid = app.reportCardId();
  app.emit({ stream: 'user', type: 'card.fix', payload: { cardId: cid, reportId: null, fix: { state: 'waiting_checks', by: 'Claude', taskId: 'task-0000000000000001', attemptId: 'fix-0000000000000001',
    planId: null, operationId: null, requestId: 'chk-00000000000000f1', opinion: null, code: null, updatedAt: new Date().toISOString() } } });
  await app.settle();
  assert.equal(app.panel(cid), null);
  assert.equal(button(app.card(cid), 'Run checks'), null);
});

/* ---------- 5: a card's Stop stops the run of the task that owns the fix ---------- */
test('review 5: Stop cancels card.fix.taskId’s run; off while the owner isn’t known', async () => {
  const app = await startFullApp();
  const cid = await fix(app, 'hold');
  await app.until(() => app.panel(cid) && button(app.panel(cid), 'Stop'), 'working');
  const fu = await app.inner.call('followup.get', { reportId: app.calls.find((c) => c.method === 'agent.start').params.scope.reportId });
  const owner = [fu.card].concat(fu.upcoming).find((c) => c && c.id === cid).fix.taskId;
  assert.ok(owner);
  let mark = app.calls.length;
  await app.click(button(app.panel(cid), 'Stop'));
  assert.deepEqual(app.after(mark, 'agent.cancel').map((c) => c.params.taskId), [owner]);
  // Another task's fix on another card: its Stop names that task, not the current one.
  const other = app.cards().map((c) => c.props['data-card']).find((x) => x !== cid);
  const fx = (taskId) => ({ state: 'working', by: 'Claude', taskId, attemptId: 'fix-0000000000000002', planId: null, operationId: null, requestId: null, opinion: null, code: null, updatedAt: new Date().toISOString() });
  app.emit({ stream: 'user', type: 'card.fix', payload: { cardId: other, reportId: null, fix: fx('task-00000000000000aa') } });
  await app.settle();
  mark = app.calls.length;
  await app.click(button(app.panel(other), 'Stop'));
  assert.deepEqual(app.after(mark, 'agent.cancel').map((c) => c.params.taskId), ['task-00000000000000aa']);
  app.emit({ stream: 'user', type: 'card.fix', payload: { cardId: other, reportId: null, fix: fx(undefined) } });
  await app.settle();
  assert.equal(button(app.panel(other), 'Stop').props.disabled, true);
});

/* ---------- re-check of 6aa76b9 / 21ccbdb / df07d08 ---------- */
test('re-check 1: a request that ended while away (no longer listed by checks.list) goes, and its card is read again', async () => {
  const app = await startFullApp({ flag: true, admin: true });
  await withTask(app);
  await sideRequest(app);
  app.holdEvents((ev) => ev.type === 'agent.checks');
  const list = await app.inner.call('checks.list', {});
  await app.inner.call('checks.decline', { requestId: list.requests[0].requestId });   // ends while the app hears nothing
  await app.settle(50);
  assert.ok(button(app.side(), 'Run checks'), 'still open here');
  const mark = app.calls.length;
  app.resume();
  await app.until(() => app.side() === null, 'the ended request goes');
  assert.ok(app.after(mark, 'followup.get').length >= 1, 'the deck is read again');
  app.releaseEvents();   // the held decline event now: shown as declined (the event wins)
  await app.settle(100);
  assert.ok(text(app.side()).includes('Checks skipped'));
  await app.inner.call('fixture.endCooldown', {});
});

test('re-check 3 / design v84: a card-bound script points beside the chat in every state (waiting, running, ended) and never says “Nothing changed”; closing it ends the card’s fix', async () => {
  const app = await startFullApp({ scripts: true });
  await app.inner.call('fixture.setFixReply', { mode: 'hold' });
  const cid = app.reportCardId();
  await app.click(button(app.card(cid), 'Fix for me'));
  await app.settle(200);
  const args = { title: 'Clear the DNS cache', why: 'Some websites still open at their old address.', parts: [{ fromLine: 1, toLine: 2, explains: 'Empties the list of web addresses Windows remembers.' }],
    script: ['# Clear the DNS cache', 'Clear-DnsClientCache'].join('\n') };
  const r = await app.inner.call('fixture.agentTool', { taskId: app.taskId(), tool: 'propose_script', args });
  assert.ok(r.planId, JSON.stringify(r));
  await app.inner.call('fixture.finishTurn', { taskId: app.taskId() });
  await app.until(() => text(app.panel(cid)).includes('Claude wrote a script for this. Read it beside the chat.'), 'waiting');
  // It writes, then fails (the fixture's script run: failed after its first lines).
  await app.inner.call('fixture.setScriptRun', { outcome: 'failed', seconds: 2 });
  await app.inner.call('shell.approvePlan', { planId: r.planId, options: { restorePoint: false, shareOutput: false }, ticked: true, readToEnd: true });
  await app.until(() => /Claude’s script (is running|has ended)\./.test(text(app.panel(cid))), 'running or ended');
  await app.until(() => text(app.panel(cid)).includes('Claude’s script has ended. See what happened beside the chat.'), 'ended');
  const p = app.panel(cid);
  assert.ok(button(p, 'Show script'));
  assert.ok(!text(app.card(cid)).includes('Nothing changed'), 'the card never says nothing changed');
  assert.ok(!byClass(app.card(cid), 'ot-approval').length, 'no plan card in the card');
  const side = all(app.tree).find((n) => hasClassName(n, 'ot-plan-' + r.planId));
  assert.ok(side, 'the script approval beside the chat');
  const mark = app.calls.length;
  await app.click(button(side, 'Close'));
  assert.deepEqual(app.after(mark, 'fix.dismiss').map((c) => c.params.cardId), [cid]);
  await app.until(() => app.panel(cid) === null, 'the card back to normal');
});
const hasClassName = (n, c) => String((n.props && n.props.className) || '').split(/\s+/).includes(c);

test('re-check 4: a reconnect’s snapshot retires an older card event; a later unfenced-looking deck reply doesn’t bring it back', async () => {
  const app = await startFullApp();
  const [a, b] = app.cards().map((c) => c.props['data-card']);
  app.emit({ stream: 'user', type: 'card.fix', payload: { cardId: a, reportId: null, fix: { state: 'working', by: 'Claude', taskId: 'task-00000000000000aa', attemptId: 'fix-00000000000000a1',
    planId: null, operationId: null, requestId: null, opinion: null, code: null, updatedAt: new Date().toISOString() } } });
  await app.settle();
  assert.ok(text(app.panel(a)).includes('Claude is looking into it…'));
  app.resume();                                              // the host's deck: no fix on that card
  await app.until(() => app.panel(a) === null, 'the snapshot wins');
  await app.click(button(app.card(b), 'Later'));             // followup.respond on another card
  await app.settle(600);
  assert.equal(app.panel(a), null, 'the old event stays retired');
});

test('re-check 5: a late fix.dismiss reply never hides a newer attempt', async () => {
  const app = await startFullApp();
  const cid = await fix(app, 'nothing_to_fix');
  await app.until(() => app.panel(cid) && button(app.panel(cid), 'Close'), 'nothing to fix');
  let answer;
  app.gate = (m, p, run) => (m === 'fix.dismiss' ? run().then((r) => new Promise((res) => { answer = () => res(r); })) : undefined);
  await app.click(button(app.panel(cid), 'Close'));
  await app.until(() => !!answer, 'the dismissal on its way');
  await app.until(() => app.panel(cid) === null, 'the card.fix event: normal');
  app.gate = null;
  await app.inner.call('fixture.setFixReply', { mode: 'hold' });
  await app.click(button(app.card(cid), 'Fix for me'));
  await app.until(() => app.panel(cid) && text(app.panel(cid)).includes('Claude is looking into it…'), 'a new attempt');
  answer();
  await app.settle(150);
  assert.ok(app.panel(cid) && text(app.panel(cid)).includes('Claude is looking into it…'), 'still working');
});
