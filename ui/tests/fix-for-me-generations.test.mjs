// node --test ui/tests/   — review of the Fix for me UI (deck request generations, the script a card points to, closing an
// old script), each on the real app: AppFlow and every component with its own state (mini-react.mjs), on the fixture backend.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { startFullApp, all, text, button } from './app-full.mjs';

const cardFix = (state, extra) => Object.assign({ state, by: 'Claude', taskId: 'task-00000000000000aa', attemptId: 'fix-00000000000000a1', planId: null, operationId: null,
  requestId: null, opinion: null, code: null, updatedAt: new Date().toISOString() }, extra);
const withFix = (deck, cid, fx) => Object.assign({}, deck, {
  card: deck.card && deck.card.id === cid ? Object.assign({}, deck.card, { fix: fx }) : deck.card,
  upcoming: (deck.upcoming || []).map((c) => (c && c.id === cid ? Object.assign({}, c, { fix: fx }) : c)) });

/* ---------- 1: a deck reply older than the newest accepted one is dropped ---------- */
test('generations: snapshot A (before a card.fix event) returning after snapshot B (after it) doesn’t bring the older deck back', async () => {
  const app = await startFullApp();
  const cid = app.reportCardId();
  let releaseA = null;
  app.gate = (m, p, run) => (m === 'followup.get' ? run().then((r) => new Promise((res) => { releaseA = () => res(r); })) : undefined);
  app.resume();   // A: the deck is read again (held)
  await app.until(() => !!releaseA, 'snapshot A on its way');
  const fx = cardFix('working');
  app.gate = (m, p, run) => (m === 'followup.get' ? run().then((r) => withFix(r, cid, fx)) : undefined);   // B: the host's deck now has the fix
  app.emit({ stream: 'user', type: 'card.fix', payload: { cardId: cid, reportId: null, fix: fx } });
  app.resume();
  await app.settle(150);
  assert.ok(text(app.panel(cid)).includes('Claude is looking into it…'), 'B installed');
  app.gate = null;
  releaseA();
  await app.settle(150);
  assert.ok(app.panel(cid) && text(app.panel(cid)).includes('Claude is looking into it…'), 'A, older than B, was dropped');
});

/* ---------- 2 and 3: the script a card points to ---------- */
const hasClassName = (n, c) => String((n.props && n.props.className) || '').split(/\s+/).includes(c);
const scriptSide = (app, pid) => all(app.tree).find((n) => hasClassName(n, 'ot-plan-' + pid)) || null;
// A card-bound script that ran and ended (the fixture's run fails after its first lines).
async function endedScript(app) {
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
  await app.inner.call('fixture.setScriptRun', { outcome: 'failed', seconds: 2 });
  await app.inner.call('shell.approvePlan', { planId: r.planId, options: { restorePoint: false, shareOutput: false }, ticked: true, readToEnd: true });
  await app.until(() => text(app.panel(cid)).includes('Claude’s script has ended. See what happened beside the chat.'), 'ended');
  return { cid, pid: r.planId };
}

test('script pointer: after a reload the script the card points to is beside the chat (and Show script finds it)', async () => {
  const app = await startFullApp({ scripts: true });
  const { cid, pid } = await endedScript(app);
  assert.ok(scriptSide(app, pid));
  await app.reload();
  await app.until(() => app.panel(cid) && button(app.panel(cid), 'Show script'), 'the pointer after the reload');
  await app.settle(200);
  assert.ok(scriptSide(app, pid), 'the script is rendered beside the chat');
});

test('script pointer: closing the ended script hides it only once the dismissal succeeded', async () => {
  const app = await startFullApp({ scripts: true });
  const { cid, pid } = await endedScript(app);
  app.gate = (m) => (m === 'fix.dismiss' ? Promise.reject(Object.assign(new Error('busy'), { code: -32000 })) : undefined);
  await app.click(button(scriptSide(app, pid), 'Close'));
  await app.settle(150);
  assert.ok(button(app.panel(cid), 'Show script'), 'the card still points to it');
  assert.ok(scriptSide(app, pid), 'a failed dismissal leaves the script shown');
  app.gate = null;
  await app.click(button(scriptSide(app, pid), 'Close'));
  await app.until(() => app.panel(cid) === null && !scriptSide(app, pid), 'dismissed, then hidden');
});

test('closing an older script never dismisses the card’s newer attempt', async () => {
  const app = await startFullApp({ scripts: true });
  const { cid, pid } = await endedScript(app);
  const p2 = await app.inner.call('fixture.proposeScript', { sample: 'plain' });
  app.emit({ stream: 'user', type: 'card.fix', payload: { cardId: cid, reportId: null, fix: cardFix('plan_ready', { attemptId: 'fix-00000000000000a2', planId: p2.planId }) } });
  await app.until(() => !!scriptSide(app, p2.planId), 'the newer attempt’s script');
  assert.ok(scriptSide(app, pid), 'the older script is still beside the chat');
  const mark = app.calls.length;
  await app.click(button(scriptSide(app, pid), 'Close'));
  await app.settle(150);
  assert.deepEqual(app.after(mark, 'fix.dismiss'), [], 'no dismissal');
  assert.equal(scriptSide(app, pid), null, 'the older script closed');
  assert.ok(app.panel(cid) && button(app.panel(cid), 'Show script'), 'the newer attempt stays on the card');
});
