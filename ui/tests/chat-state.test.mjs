// node --test ui/tests/   — the chat's pure rules (connection-state.js): an ask on an ended task, answers restored
// from a task's history (a run still going included), answers kept per task; and the fixture's host parity for AI cards.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { taskEnded, turnsFromHistory, runningTurn, reconcileAnswers, answerKey, answerPanels, menuStep, keepInView, citeKind, citeRegistry, citeLabel } = require('../js/connection-state.js');

test('citation chips name the kind of check instead of a raw running id', () => {
  const reg = citeRegistry();
  assert.equal(citeLabel(reg, 'ev-a', { collectorId: 'win.security.listeners' }), 'Ports');
  assert.equal(citeLabel(reg, 'ev-b', { collectorId: 'win.process.sample' }), 'CPU');
  assert.equal(citeLabel(reg, 'ev-c', { collectorId: 'win.security.listeners' }), 'Ports 2');   // the second of a kind
  assert.equal(citeLabel(reg, 'ev-a', null), 'Ports');                                           // stable once named
  assert.equal(citeLabel(reg, 'ev-d', null), 'm4');                                              // kind not known yet
  assert.equal(citeLabel(reg, 'ev-d', { collectorId: 'bench.gpu' }), 'GPU');                     // named once it loads
  assert.equal(citeKind({ collectorId: 'win.security.startup' }), 'Startup');                    // the specific word wins
  assert.equal(citeKind({ collectorId: 'win.security.defender' }), 'Security');
  assert.equal(citeKind({ collectorId: 'win.custom.display' }), 'Display');
  assert.equal(citeKind({ collectorId: 'something.else' }), null);
  // The check that produced it is enough (the evidence itself may not be loaded yet).
  assert.equal(citeKind('process.sample'), 'CPU');
  assert.equal(citeKind('network.listeners'), 'Ports');
  assert.equal(citeKind('sensors.snapshot'), 'Sensors');
  assert.equal(citeKind('get_benchmarks'), 'Bench');
  assert.equal(citeKind('get_findings'), null);
  // No label is longer than a short word and a number: the chip stays the size it was.
  for (const id of ['win.security.wsc', 'win.system.volumes', 'win.devices.pnp', 'win.events.system', 'bench.disk'])
    assert.ok(citeKind({ collectorId: id }).length <= 8);
});

test('a popover is kept whole inside the viewport, not just its focused row', () => {
  const calls = [];
  keepInView({ scrollIntoView: (o) => calls.push(o) });
  assert.deepEqual(calls, [{ block: 'nearest', inline: 'nearest' }]);
  keepInView(null); keepInView({});   // nothing to scroll: no throw
});
const { createDesktopAdapter } = require('../js/adapter-desktop.js');
const { createFixtureAdapter } = require('../js/adapter-fixture.js');
const fixture = JSON.parse(readFileSync(new URL('../fixtures/m1-simulated-audit.json', import.meta.url), 'utf8'));
const extras = JSON.parse(readFileSync(new URL('../fixtures/m2-extras.json', import.meta.url), 'utf8'));
const schema = JSON.parse(readFileSync(new URL('../../contracts/schemas/followup.schema.json', import.meta.url), 'utf8'));

function memStore() { const m = {}; return { getItem: (k) => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); } }; }
function adapter(storage) { return createFixtureAdapter({ fixture, extras, storage: storage || memStore(), timeScale: 0.01 }); }
function waitFor(be, pred) { return new Promise((r) => { const off = be.onEvent((e) => { if (pred(e)) { off(); r(e); } }); }); }
const key = () => crypto.randomUUID();
async function withReport(be) {
  const o = await be.call('onboarding.get');
  await be.call('onboarding.save', { expectedRevision: o.revision, patch: { uses: ['Gaming'], inventoryConsent: 'skipped', step: 'complete' } });
  const fin = waitFor(be, (e) => e.type === 'scan.finished');
  await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  return (await fin).payload.reportId;
}

test('an ask on an ended task: the host error (bare status, through the desktop adapter) means a fresh task', async () => {
  const wv = { sent: [], fns: [], postMessage(m) { this.sent.push(m); }, addEventListener(t, fn) { this.fns.push(fn); }, removeEventListener() {} };
  const be = createDesktopAdapter(wv);
  for (const reason of ['expired', 'stopped', 'exhausted', 'failed', 'task_expired']) {
    const p = be.call('agent.ask', { idempotencyKey: key(), taskId: 'task-1', text: 'Why?' });
    const id = wv.sent[wv.sent.length - 1].id;
    wv.fns.forEach((fn) => fn({ data: { type: 'rpc.error', id, error: { code: -32013, message: 'invalid_state', data: { reason } } } }));
    const err = await p.then(() => null, (e) => e);
    assert.equal(err.code, -32013);
    assert.equal(taskEnded(err), true, reason);
  }
  // A busy run, a provider that isn't connected or a native client are not an ended task.
  for (const reason of ['busy', 'turn_running', 'not_connected', 'native_client', 'tools_unavailable'])
    assert.equal(taskEnded({ code: -32013, data: { reason } }), false, reason);
  assert.equal(taskEnded({ code: -32001, data: { kind: 'task' } }), false);
  assert.equal(taskEnded(null), false);
});

const ev = (seq, type, payload) => ({ seq, type, at: '2026-11-07T00:00:00Z', payload });
const strip = (t) => t.replace(/\s*\[ev-[0-9a-z-]+\]/gi, '');

test('a reload mid-answer restores the pending answer (busy, still Stop-able) and the later answer fills it', () => {
  const hist = [ev(1, 'agent.task', { status: 'active' }), ev(2, 'agent.turn', { turnId: 'turn-a', n: 1, status: 'started' }),
    ev(3, 'agent.message', { turnId: 'turn-a', text: 'First [ev-1]', citations: ['ev-1'], followUps: [] }),
    ev(4, 'agent.turn', { turnId: 'turn-a', n: 1, status: 'finished' }),
    ev(5, 'agent.turn', { turnId: 'turn-b', n: 2, status: 'started' }),
    ev(6, 'agent.tool', { invocationId: 'inv-1', turnId: 'turn-b', tool: 'run_diagnostic', status: 'running' })];
  assert.deepEqual(turnsFromHistory(hist).map((t) => [t.turnId, t.status]), [['turn-a', 'answered'], ['turn-b', 'running']]);
  assert.equal(runningTurn(hist), 'turn-b');
  const list = reconcileAnswers([], hist, { taskId: 'task-1', reportId: 'report-1', strip, busyWord: (t) => 'Running ' + t.tool });
  assert.deepEqual(list.map((a) => [a.turnId, a.text, a.busy, a.taskId]), [['turn-a', 'First', null, 'task-1'], ['turn-b', null, 'Running run_diagnostic', 'task-1']]);
  // Finished meanwhile (a reconnect): the busy entry gets its answer; nothing older than what was seen comes back.
  const later = hist.concat([ev(7, 'agent.message', { turnId: 'turn-b', text: 'Second', citations: [], followUps: ['Next?'] }), ev(8, 'agent.turn', { turnId: 'turn-b', n: 2, status: 'finished' })]);
  const after = reconcileAnswers([list[1]], later, { taskId: 'task-1', afterSeq: 6, strip });
  assert.deepEqual(after.map((a) => [a.turnId, a.text, a.busy]), [['turn-b', 'Second', null]]);
  assert.equal(runningTurn(later), null);
  // Failed or cancelled while away: "No answer", or the question simply goes.
  const failed = reconcileAnswers([list[1]], hist.concat([ev(7, 'agent.turn', { turnId: 'turn-b', status: 'failed' })]), { taskId: 'task-1', afterSeq: 6 });
  assert.equal(failed[0].failedTurn, true);
  assert.deepEqual(reconcileAnswers([list[1]], hist.concat([ev(7, 'agent.turn', { turnId: 'turn-b', status: 'failed', code: 'cancelled' })]), { taskId: 'task-1', afterSeq: 6 }), []);
  // An ask whose reply was lost while disconnected takes the turn that started for it.
  const lost = reconcileAnswers([{ id: 'q1', q: 'Why?', taskId: 'task-1', turnId: null, text: null, failedTurn: true }], hist, { taskId: 'task-1', afterSeq: 4 });
  assert.deepEqual(lost.map((a) => [a.id, a.q, a.turnId, a.failedTurn]), [['q1', 'Why?', 'turn-b', false]]);
});

test('one ask spans several provider turns: finished before its tools is not the end; only the answer, failed or the task ending is', () => {
  const hist = [ev(1, 'agent.turn', { turnId: 'turn-x', n: 1, status: 'started' }), ev(2, 'agent.turn', { turnId: 'turn-x', n: 1, status: 'finished' }),
    ev(3, 'agent.tool', { invocationId: 'inv-1', turnId: 'turn-x', tool: 'get_findings', status: 'running' })];
  assert.equal(runningTurn(hist), 'turn-x');
  const pending = [{ id: 'q', q: 'Why?', taskId: 'task-1', turnId: 'turn-x', text: null }];
  const mid = reconcileAnswers(pending, hist, { taskId: 'task-1', busyWord: (t) => 'Running ' + t.tool });
  assert.deepEqual(mid.map((a) => [a.failedTurn, a.busy]), [[undefined, 'Running get_findings']]);
  const more = hist.concat([ev(4, 'agent.tool', { invocationId: 'inv-1', turnId: 'turn-x', tool: 'get_findings', status: 'complete' }),
    ev(5, 'agent.turn', { turnId: 'turn-x', n: 2, status: 'started' })]);
  assert.equal(runningTurn(more), 'turn-x');
  const done = more.concat([ev(6, 'agent.message', { turnId: 'turn-x', text: 'Done', citations: [], followUps: [] }), ev(7, 'agent.turn', { turnId: 'turn-x', n: 2, status: 'finished' })]);
  assert.equal(runningTurn(done), null);
  assert.equal(reconcileAnswers(pending, done, { taskId: 'task-1' })[0].text, 'Done');
  // The task ended (stopped, expired…) while the ask ran: it is over, shown as failed.
  const ended = more.concat([ev(6, 'agent.task', { status: 'stopped' })]);
  assert.equal(runningTurn(ended), null);
  assert.equal(reconcileAnswers(pending, ended, { taskId: 'task-1' })[0].failedTurn, true);
});

test('a snapshot older than the live events never brings a run back', () => {
  const hist = [ev(1, 'agent.turn', { turnId: 'turn-y', n: 1, status: 'started' })];
  assert.deepEqual(reconcileAnswers([], hist, { taskId: 'task-1', stale: true }), []);
  const live = [{ id: 'q', taskId: 'task-1', turnId: 'turn-y', text: null, busy: 'Measuring CPU' }];
  assert.equal(reconcileAnswers(live, hist, { taskId: 'task-1', stale: true })[0].busy, 'Measuring CPU');
});

test("answers belong to their task: another task's unanswered question is dropped, its answers stay", () => {
  const list = [{ id: 'old-done', taskId: 'task-old', turnId: 't1', text: 'Done' }, { id: 'old-wait', taskId: 'task-old', turnId: null, text: null }];
  const out = reconcileAnswers(list, [], { taskId: 'task-new' });
  assert.deepEqual(out.map((a) => a.id), ['old-done']);
});

test('m5 §9: agent.question gives a restored answer its question, card and time; a question whose run never began ends', () => {
  const q = (seq, turnId, text, cardId, at) => ({ seq, type: 'agent.question', at, payload: { turnId, text, cardId } });
  const hist = [q(1, 'turn-a', 'Why is it slow?', null, '2026-11-07T10:00:00Z'), ev(2, 'agent.turn', { turnId: 'turn-a', n: 1, status: 'started' }),
    ev(3, 'agent.message', { turnId: 'turn-a', text: 'Because.', citations: [], followUps: ['More?'] }),
    q(4, 'turn-b', 'And this card?', 'card-1', '2026-11-07T10:05:00Z'), ev(5, 'agent.turn', { turnId: 'turn-b', n: 2, status: 'started' }),
    ev(6, 'agent.message', { turnId: 'turn-b', text: 'It is fine.', citations: [], followUps: [] }),
    q(7, 'turn-c', 'Lost?', null, '2026-11-07T10:06:00Z'),
    q(8, 'turn-d', 'Still going?', null, '2026-11-07T10:07:00Z'), ev(9, 'agent.turn', { turnId: 'turn-d', n: 3, status: 'started' })];
  assert.deepEqual(turnsFromHistory(hist).map((t) => [t.turnId, t.status, t.question && t.question.text]),
    [['turn-a', 'answered', 'Why is it slow?'], ['turn-b', 'answered', 'And this card?'], ['turn-c', 'failed', 'Lost?'], ['turn-d', 'running', 'Still going?']]);
  const list = reconcileAnswers([], hist, { taskId: 'task-1' });
  assert.deepEqual(list.map((a) => [a.turnId, a.q, a.cardId, a.at, !!a.restored]), [
    ['turn-a', 'Why is it slow?', null, '2026-11-07T10:00:00Z', true], ['turn-b', 'And this card?', 'card-1', '2026-11-07T10:05:00Z', true],
    ['turn-d', 'Still going?', null, '2026-11-07T10:07:00Z', false]]);
  // A live entry that lost its question (another window asked) gets it; one with its own question keeps it.
  const live = reconcileAnswers([{ id: 'x', q: null, taskId: 'task-1', turnId: 'turn-a', text: null }, { id: 'y', q: 'Mine', taskId: 'task-1', turnId: 'turn-d', text: null }], hist, { taskId: 'task-1' });
  assert.deepEqual(live.filter((a) => a.id === 'x' || a.id === 'y').map((a) => [a.id, a.q, a.text]), [['x', 'Why is it slow?', 'Because.'], ['y', 'Mine', null]]);
  // drop: a deleted answer doesn't come back; others 'keep' leaves another task's waiting entry alone.
  const kept = reconcileAnswers([{ id: 'w', taskId: 'task-2', turnId: null, text: null }], hist, { taskId: 'task-1', others: 'keep', drop: (a) => a.turnId === 'turn-a' });
  assert.deepEqual(kept.map((a) => a.id), ['w', 'h-turn-b', 'h-turn-d']);
});

test('answer panels: one per question, newest first; restored answers wait in History; closed stays in History; deleted shows nowhere', () => {
  const answers = [
    { id: 'h-turn-a', q: 'Old one', taskId: 't1', turnId: 'turn-a', text: 'A', at: '2026-11-07T09:00:00Z', restored: true },
    { id: 'n1', q: 'First here', taskId: 't1', turnId: 'turn-b', text: 'B', at: '2026-11-07T10:00:00Z' },
    { id: 'card', q: 'About a card', cardId: 'card-1', taskId: 't1', turnId: 'turn-c', text: 'C', at: '2026-11-07T10:01:00Z' },
    { id: 'n2', q: 'Second here', taskId: 't1', turnId: null, text: null, at: '2026-11-07T10:02:00Z' }];
  let v = answerPanels(answers, {});
  assert.deepEqual(v.panels.map((a) => a.id), ['n2', 'n1']);
  assert.deepEqual(v.history.map((r) => [r.title, r.open, r.running]), [['Second here', true, true], ['First here', true, false], ['Old one', false, false]]);
  v = answerPanels(answers, { open: { 'h-turn-a': true, n1: false } });
  assert.deepEqual(v.panels.map((a) => a.id), ['n2', 'h-turn-a']);
  assert.equal(answerKey(answers[0]), 't1:turn-a');
  assert.equal(answerKey(answers[3]), 't1:n2');
  v = answerPanels(answers, { deleted: { 't1:turn-a': true } });
  assert.deepEqual(v.history.map((r) => r.id), ['n2', 'n1']);
  // No question kept (an answer from before agent.question): its first line titles the row.
  assert.equal(answerPanels([{ id: 'z', taskId: 't', turnId: 'turn-z', text: 'Line one\nLine two', restored: true }], {}).history[0].title, 'Line one');
  assert.deepEqual(answerPanels([], {}), { panels: [], history: [] });
});

test('menu keys: arrows wrap, Home/End jump, other keys are not handled', () => {
  assert.equal(menuStep('ArrowDown', -1, 3), 0);
  assert.equal(menuStep('ArrowDown', 2, 3), 0);
  assert.equal(menuStep('ArrowUp', 0, 3), 2);
  assert.equal(menuStep('ArrowUp', -1, 3), 2);
  assert.equal(menuStep('Home', 2, 3), 0);
  assert.equal(menuStep('End', 0, 3), 2);
  assert.equal(menuStep('Enter', 0, 3), -1);
  assert.equal(menuStep('ArrowDown', 0, 0), -1);
});

test('fixture: an ask on an expired task is refused like the host (-32013, reason = bare status)', async () => {
  const be = adapter();
  const reportId = await withReport(be);
  await be.call('shell.clientStatus', { client: 'claude_code' });
  const t = await be.call('agent.start', { idempotencyKey: key(), goal: 'Look', recipientKind: 'claude_code_mcp', scope: { reportId }, grant: { diagnostics: [] } });
  await be.call('fixture.expireTask', { taskId: t.taskId });
  const err = await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'Why?' }).then(() => null, (e) => e);
  assert.deepEqual([err.code, err.data.reason], [-32013, 'expired']);
  assert.equal(taskEnded(err), true);
  await be.call('agent.stop', { taskId: t.taskId });
});

test('fixture: agent.active lists active tasks newest first; agent.get history shows a run still going', async () => {
  const be = adapter();
  const reportId = await withReport(be);
  await be.call('shell.clientStatus', { client: 'claude_code' });
  await be.call('provider.connect', { kind: 'chatgpt_plan' });
  await waitFor(be, (e) => e.type === 'provider.status' && e.payload.kind === 'chatgpt_plan' && e.payload.status === 'connected');
  const a = await be.call('agent.start', { idempotencyKey: key(), goal: 'A', recipientKind: 'claude_code_mcp', scope: { reportId }, grant: { diagnostics: ['process.sample'] } });
  const b = await be.call('agent.start', { idempotencyKey: key(), goal: 'B', recipientKind: 'chatgpt_plan', scope: { reportId }, grant: { diagnostics: [] } });
  assert.deepEqual((await be.call('agent.active')).tasks.map((t) => t.taskId), [b.taskId, a.taskId]);
  const started = waitFor(be, (e) => e.type === 'agent.turn' && e.payload.status === 'started');
  const r = await be.call('agent.ask', { idempotencyKey: key(), taskId: a.taskId, text: 'Why is it slow?' });
  await started;
  const got = await be.call('agent.get', { taskId: a.taskId });
  assert.equal(runningTurn(got.activity), r.turnId);
  assert.deepEqual(reconcileAnswers([], got.activity, { taskId: a.taskId }).map((x) => [x.turnId, x.text]), [[r.turnId, null]]);
  await be.call('agent.stop', { taskId: a.taskId });
  assert.deepEqual((await be.call('agent.active')).tasks.map((t) => t.taskId), [b.taskId]);
  await be.call('agent.stop', { taskId: b.taskId });
});

test('fixture: a run cancelled mid-way calls no more tools and adds no card', async () => {
  const be = adapter();
  const reportId = await withReport(be);
  await be.call('shell.clientStatus', { client: 'claude_code' });
  const t = await be.call('agent.start', { idempotencyKey: key(), goal: 'Look', recipientKind: 'claude_code_mcp', scope: { reportId }, grant: { diagnostics: [] } });
  const tools = [];
  be.onEvent((e) => { if (e.type === 'agent.tool' && e.stream === 'task:' + t.taskId) tools.push(e.payload.tool); });
  // The scripted first look runs its card tools back to back, so the cancel lands before it reaches them.
  await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'Look over my latest report, PC specs and past reports. Tell me the 3 things that matter most, and improve the suggestion cards where you can.' });
  assert.equal((await be.call('agent.cancel', { taskId: t.taskId })).cancelled, true);
  await new Promise((r) => setTimeout(r, 150));
  assert.ok(!tools.includes('add_card') && !tools.includes('note_card'), tools.join());
  const fu = await be.call('followup.get', { reportId });
  assert.ok(![fu.card].concat(fu.upcoming).some((c) => c && c.origin === 'ai'));
  assert.equal(fu.total, fu.pendingCount + fu.decisions.length);
  await be.call('agent.stop', { taskId: t.taskId });
});

test('fixture AI cards have the host shape (followup.schema.json card), cite only known IDs, never leak the task', async () => {
  const be = adapter();
  const reportId = await withReport(be);
  await be.call('shell.clientStatus', { client: 'claude_code' });
  const t = await be.call('agent.start', { idempotencyKey: key(), goal: 'Look', recipientKind: 'claude_code_mcp', scope: { reportId }, grant: { diagnostics: [] } });
  const msg = waitFor(be, (e) => e.type === 'agent.message');
  await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'Look over my latest report, PC specs and past reports. Tell me the 3 things that matter most, and improve the suggestion cards where you can.' });
  await msg;
  const report = await be.call('report.get', { reportId });
  let fu = await be.call('followup.get', { reportId });
  const def = schema.$defs.card, idPattern = new RegExp(def.properties.id.pattern);
  const seen = [];
  // Walk the deck (Later on each) to see every card, the AI one at the end included.
  while (fu.card) {
    for (const c of [fu.card].concat(fu.upcoming || [])) {
      for (const k of def.required) assert.ok(k in c, c.id + ' lacks ' + k);
      assert.match(c.id, idPattern);
      assert.ok(c.origin === 'report' || c.origin === 'ai');
      // The task that added an AI card (for its evidence); null on report cards.
      assert.equal(c.sourceTaskId, c.origin === 'ai' ? t.taskId : null);
      for (const k of ['taskId', 'aiTaskId', 'noteTaskId', 'key']) assert.ok(!(k in c), c.id + ' leaks ' + k);
      assert.ok(c.settingsUri === null || /^(ms-settings|windowsdefender):/.test(c.settingsUri));
      if (!seen.some((x) => x.id === c.id)) seen.push(c);
    }
    fu = await be.call('followup.respond', { idempotencyKey: key(), reportId, expectedRevision: fu.revision, scope: 'card', cardId: fu.card.id, option: 'later' });
  }
  const ai = seen.filter((c) => c.origin === 'ai');
  assert.equal(ai.length, 1);
  const c = ai[0];
  assert.equal(c.text, c.why);                        // as the host: text = why
  assert.equal(typeof c.title, 'string');
  assert.equal(c.settingsUri, null);
  assert.equal(c.addedBy, 'Claude Code');
  assert.deepEqual(c.options, ['Ask about this', 'Ignore']);
  assert.ok(c.evidenceIds.length >= 1 && c.evidenceIds.every((id) => report.evidence.some((e) => e.id === id)));
  const noted = seen.filter((x) => x.note);
  assert.ok(noted.length >= 1 && noted.every((x) => x.noteBy === 'Claude Code'));
  await be.call('agent.stop', { taskId: t.taskId });
});
