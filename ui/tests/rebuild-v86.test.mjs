// node --test ui/tests/   — design Version 86 on the real app (AppFlow and every component with its own state,
// mini-react.mjs, on the fixture backend): the toolbar pill shows the panel's status word for every state of every AI
// kind (PillPanelStates, pillStatus); pairing in one place (the main-page PairingCard is not shown while the pill's panel
// shows pairing; Deny / Pair; busy and pairBusy); v87: Not working for an unknown key / ChatGPT error; Report a problem's failed, copyFailed, opening and stale states; and
// which failed AI turns offer Report it.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { startFullApp, all, text, byClass, button, hasClass } from './app-full.mjs';

const pillOf = (app) => byClass(app.tree, 'ot-pill')[0] || null;
const pillWord = (app) => { const b = byClass(pillOf(app), 'ot-pill-open')[0]; const w = b && byClass(b, 'text-mono-sm')[0]; return w ? text(w) : null; };
const pillName = (app) => text(byClass(byClass(pillOf(app), 'ot-pill-open')[0], 'text-callout')[0]);
const panelOf = (app) => byClass(app.tree, 'ot-pillpanel')[0] || null;
const panelWord = (app) => { const p = panelOf(app); return p ? text(byClass(byClass(p, 'ot-pillpanel-head')[0], 'text-mono-sm')[0]) : null; };
const panelHints = (app) => byClass(panelOf(app), 'ot-pillpanel-hint').map(text);
const panelButtons = (app) => all(panelOf(app)).filter((n) => n.type === 'button').map((b) => text(b).trim());
async function openPanel(app) { if (!panelOf(app)) await app.click(byClass(pillOf(app), 'ot-pill-open')[0]); assert.ok(panelOf(app), 'panel open'); }
async function closePanel(app) { if (panelOf(app)) await app.click(byClass(pillOf(app), 'ot-pill-open')[0]); assert.ok(!panelOf(app), 'panel closed'); }
async function status(app, kind, st, detail, code) {
  app.emit({ stream: 'user', type: 'provider.status', payload: { kind, status: st, detail: detail || null, code: code || null, since: new Date().toISOString() } });
  await app.settle(60);
}
async function choose(app, provider) {
  const ob = await app.inner.call('onboarding.get');
  await app.inner.call('onboarding.save', { expectedRevision: ob.revision, patch: { provider } });
  await app.reload();
  await app.until(() => !!pillOf(app) && pillName(app) === (provider.label === 'Claude (API)' ? 'Claude (API)' : provider.label), 'the pill for ' + provider.label);
}
// Every state: the pill's word is the panel's status word, which is the design's word.
async function check(app, what, word, hint) {
  await openPanel(app);
  assert.equal(panelWord(app), word, what + ': panel word');
  assert.equal(pillWord(app), word, what + ': pill word == panel word');
  if (hint !== undefined) assert.deepEqual(panelHints(app).slice(0, 1), hint ? [hint] : [], what + ': hint');
}

/* ---------- 1: pill = panel, per kind ---------- */
test('pill = panel: Claude (Claude Code), every state', async () => {
  const app = await startFullApp();
  const K = 'claude_code_mcp';
  assert.equal(pillName(app), 'Claude');
  await check(app, 'connected', 'Connected', null);
  for (const [st, d, word, hint] of [
    ['error', 'signed_out', 'Signed out', null],
    ['waiting', 'never_seen', 'Not set up', 'Claude Code isn’t installed or can’t start on this PC.'],
    ['waiting', 'idle', 'Not connected yet', 'To add Optimus to Claude Code, run this in a terminal:'],
    ['error', 'pairing_expired', 'Not connected yet', 'To add Optimus to Claude Code, run this in a terminal:'],
    ['error', 'denied', 'Not connected yet', 'To add Optimus to Claude Code, run this in a terminal:'],
    ['error', 'bridge_rejected', 'Couldn’t connect', 'Claude Code turned the connection down. Add Optimus again by running this in a terminal:'],
    ['error', 'unavailable', 'Can’t run', 'This Claude Code is too old or blocked on this PC. Update it, or pick another AI.'],
    ['client_closed', null, 'Window closed', 'The Claude Code window was closed. Open Claude Code and ask it again.'],
    ['active', null, 'Working…', null],
    ['connected', null, 'Connected', null]
  ]) {
    // Through the fixture's own entry: opening the panel may re-read it (the manual layout fetches its command).
    await app.inner.call('fixture.setProviderState', { kind: K, status: st, detail: d });
    await app.settle(60);
    await check(app, st + '/' + d, word, hint);
  }
  // The backend lost and back.
  await app.inner.call('fixture.setBackendState', { state: 'reconnecting' });
  await app.settle(60);
  await check(app, 'reconnecting', 'Reconnecting…', 'Optimus is reconnecting to its background service.');
  assert.deepEqual(panelButtons(app), []);
  await app.inner.call('fixture.setBackendState', { state: 'ready' });
  await app.settle(150);
  await check(app, 'back', 'Connected');
  // Pairing and paired come from the fixture's own entry (the app re-reads it).
  await closePanel(app);
  await app.inner.call('fixture.setProviderState', { kind: K, status: 'pending' });
  await app.until(() => byClass(app.tree, 'ot-pair').length > 0, 'the pairing card');
  await check(app, 'pending', 'Wants to pair', 'Check this code matches the one in Claude Code’s window.');
  assert.ok(!/\d{4}/.test(pillWord(app)), 'the code is only in the panel and the card');
  await closePanel(app);
  await app.inner.call('fixture.setProviderState', { kind: K, status: 'paired' });
  await app.settle(150);
  await check(app, 'paired', 'Paired', 'Claude Code can now work in its own window.');
});

test('pill = panel: ChatGPT, every state', async () => {
  const app = await startFullApp();
  await choose(app, { value: 'chatgpt', label: 'ChatGPT', kind: 'chatgpt_plan' });
  const K = 'chatgpt_plan';
  for (const [st, d, word, hint, primary] of [
    ['not_configured', null, 'Not signed in', null, 'Sign in'],
    ['checking', null, 'Checking…', null, null],
    ['connected', null, 'Connected', null, null],
    ['connected', 'not_saved', 'Connected', 'Works now, but this PC didn’t save your sign-in. You’ll sign in again next time.', null],
    ['error', 'auth', 'Signed out', null, 'Sign in'],
    ['error', 'cancelled', 'Not signed in', null, 'Sign in'],
    ['error', 'callback_timeout', 'Sign-in didn’t finish', 'The browser didn’t come back to Optimus. Try again, or pick another AI.', 'Sign in'],
    ['error', 'storage', 'Not saved', 'You signed in, but this PC didn’t let Optimus save it. Try again.', 'Sign in'],
    ['error', 'quota', 'Limit reached', 'Your ChatGPT plan’s limit is used up for now. Try again later.', 'Retry'],
    ['error', 'not_eligible', 'Not available', 'This ChatGPT account can’t be used here. Sign in with another account, or pick another AI.', 'Sign in'],
    ['error', 'network', 'Can’t reach it', 'Check your internet connection.', 'Retry'],
    ['error', 'unavailable', 'Busy right now', 'Try again in a minute.', 'Retry'],
    // design v87: an unknown or missing reason is Not working; a real network error stays Can’t reach it.
    ['error', 'something_new', 'Not working', 'Something went wrong, and Optimus doesn’t know why. Try again, or pick another AI.', 'Retry'],
    ['error', null, 'Not working', 'Something went wrong, and Optimus doesn’t know why. Try again, or pick another AI.', 'Retry'],
    ['error', 'network', 'Can’t reach it', 'Check your internet connection.', 'Retry']
  ]) {
    await status(app, K, st, d);
    await check(app, st + '/' + d, word, hint);
    const prim = byClass(panelOf(app), 'ot-pillpanel-primary')[0];
    assert.equal(prim ? text(prim).trim() : null, primary, st + '/' + d + ': primary');
  }
});

test('pill = panel: API key (Anthropic and OpenAI), every state', async () => {
  for (const [kind, value, label] of [['anthropic_api', 'api', 'Claude (API)'], ['openai_api', 'api', 'OpenAI (API)']]) {
    const app = await startFullApp();
    await app.inner.call('fixture.setProviderState', { kind, status: 'connected' });
    await choose(app, { value, label, kind });
    for (const [st, d, word, hint, primary] of [
      ['not_configured', null, 'Needs a key', null, 'Add key'],
      ['checking', null, 'Checking…', null, null],
      ['connected', null, 'Connected', null, null],
      ['connected', 'not_saved', 'Connected', 'Works now, but this PC didn’t save the key. You’ll add it again next time.', null],
      ['error', 'auth', 'Key rejected', 'The key was turned down. Check it and add it again.', 'Update key'],
      ['error', 'quota', 'No credit', 'This account has no credit left. Add credit, or use another key.', 'Update key'],
      ['error', 'not_eligible', 'Not available', 'This account can’t use it here. Use another key, or pick another AI.', 'Update key'],
      ['error', 'storage', 'Not saved', 'This PC didn’t let Optimus save the key. Try again.', 'Add key'],
      ['error', 'network', 'Can’t reach it', 'Check your internet connection.', 'Retry'],
      ['error', 'unavailable', 'Busy right now', 'Try again in a minute.', 'Retry'],
    // design v87: an unknown or missing reason is Not working; a real network error stays Can’t reach it.
    ['error', 'something_new', 'Not working', 'Something went wrong, and Optimus doesn’t know why. Try again, or pick another AI.', 'Retry'],
    ['error', null, 'Not working', 'Something went wrong, and Optimus doesn’t know why. Try again, or pick another AI.', 'Retry'],
    ['error', 'network', 'Can’t reach it', 'Check your internet connection.', 'Retry']
    ]) {
      await status(app, kind, st, d);
      await check(app, kind + ' ' + st + '/' + d, word, hint);
      const prim = byClass(panelOf(app), 'ot-pillpanel-primary')[0];
      assert.equal(prim ? text(prim).trim() : null, primary, st + '/' + d + ': primary');
    }
  }
});

test('pill = panel: Local only has no pill word; the panel says No AI connected', async () => {
  const app = await startFullApp();
  await choose(app, { value: 'local', label: 'Local only' });
  assert.equal(pillWord(app), null);
  await openPanel(app);
  assert.equal(panelWord(app), 'No AI connected');
  assert.deepEqual(panelButtons(app), ['Connect an AI']);
});

/* ---------- 2: pairing in one place ---------- */
test('pairing: the card is not shown while the panel shows pairing, and comes back when it closes; Deny / Pair; busy on both', async () => {
  const app = await startFullApp();
  await app.inner.call('fixture.setProviderState', { kind: 'claude_code_mcp', status: 'pending' });
  await app.until(() => byClass(app.tree, 'ot-pair').length > 0, 'the pairing card');
  const card = () => byClass(app.tree, 'ot-pair')[0] || null;
  assert.deepEqual(all(card()).filter((n) => n.type === 'button').map((b) => text(b).trim()), ['Deny', 'Pair']);
  await openPanel(app);
  assert.equal(panelWord(app), 'Wants to pair');
  assert.equal(card(), null, 'no card while the panel shows pairing');
  assert.equal(byClass(app.tree, 'ot-pair-code').length, 1, 'one code on the page');
  assert.deepEqual(panelButtons(app), ['Change', 'Permissions', 'Deny', 'Pair', 'Disconnect']);
  await closePanel(app);
  assert.ok(card(), 'the card is back');
  // Pair from the panel: while the answer is on its way, both are disabled and Pair reads Pairing… (panel and card).
  let release = null;
  app.gate = (m, p, run) => (m === 'agent.approvePairing' ? new Promise((r) => { release = () => r(run()); }) : undefined);
  const before = app.calls.length;
  await openPanel(app);
  await app.click(button(panelOf(app), 'Pair'));
  assert.deepEqual(app.after(before, 'agent.approvePairing').map((c) => c.params.approve), [true], 'Pair sends Approve');
  assert.equal(button(panelOf(app), 'Pairing…').props.disabled, true);
  assert.equal(button(panelOf(app), 'Deny').props.disabled, true);
  await closePanel(app);
  assert.equal(button(card(), 'Pairing…').props.disabled, true);
  assert.equal(button(card(), 'Deny').props.disabled, true);
  app.gate = null;
  release();
  await app.until(() => !card(), 'the card gone once paired');
  assert.equal(app.after(before, 'agent.approvePairing').length, 1);
});

test('pairing: Deny on the card sends approve:false', async () => {
  const app = await startFullApp();
  await app.inner.call('fixture.setProviderState', { kind: 'claude_code_mcp', status: 'pending' });
  await app.until(() => byClass(app.tree, 'ot-pair').length > 0, 'the pairing card');
  const before = app.calls.length;
  await app.click(button(byClass(app.tree, 'ot-pair')[0], 'Deny'));
  await app.settle(100);
  assert.deepEqual(app.after(before, 'agent.approvePairing').map((c) => c.params.approve), [false]);
});

/* ---------- 3: Report a problem ---------- */
const rp = (app) => byClass(app.tree, 'ot-rp')[0] || null;
const rpStatus = (app) => text(byClass(rp(app), 'ot-rp-status')[0]);
async function openReport(app) {
  await app.click(all(app.tree).find((n) => n.type === 'button' && n.props['aria-label'] === 'Optimus menu'));
  await app.click(all(app.tree).find((n) => n.type === 'button' && n.props['data-menu-item'] && text(n).includes('Report a problem')));
  await app.settle(100);
}
test('report: a build that fails keeps the panel open with Close and Try again; Try again builds it', async () => {
  const app = await startFullApp();
  let fail = true;
  app.gate = (m) => (m === 'shell.problemReport' && fail ? Promise.reject(Object.assign(new Error('x'), { code: -32013, data: { reason: 'failed' } })) : undefined);
  await openReport(app);
  assert.equal(rpStatus(app), 'Optimus couldn’t build the report.');
  assert.ok(hasClass(byClass(rp(app), 'ot-rp-status')[0], 'is-warn'));
  assert.deepEqual(all(rp(app)).filter((n) => n.type === 'button').map((b) => b.props['aria-label'] || text(b).trim()), ['Close', 'Close', 'Try again']);
  assert.ok(!app.toasts.includes('Not available'));
  fail = false;
  await app.click(button(rp(app), 'Try again'));
  await app.until(() => all(rp(app)).some((n) => n.type === 'pre'), 'the report text');
  assert.equal(app.calls.filter((c) => c.method === 'shell.problemReport').length, 2);
});
test('report: blocked copying says so in orange and the text stays; Open GitHub reads Opening… until the answer; unknown → stale', async () => {
  const app = await startFullApp();
  await openReport(app);
  await app.until(() => all(rp(app)).some((n) => n.type === 'pre'), 'the report text');
  await app.click(button(rp(app), 'Copy'));            // the harness page has no clipboard
  await app.settle(50);
  assert.equal(rpStatus(app), 'Couldn’t copy. Select the text above and copy it yourself.');
  assert.ok(hasClass(byClass(rp(app), 'ot-rp-status')[0], 'is-warn'));
  assert.ok(!app.toasts.includes('Copy failed'));
  const pre = all(rp(app)).find((n) => n.type === 'pre');
  assert.equal(pre.props.tabIndex, 0);
  assert.ok(text(pre).length > 0);
  // Opening…
  let release = null;
  app.gate = (m, p, run) => (m === 'shell.openIssue' ? new Promise((r) => { release = (x) => r(x || run()); }) : undefined);
  await app.click(button(rp(app), 'Open GitHub'));
  assert.equal(button(rp(app), 'Opening…').props.disabled, true);
  assert.equal(rpStatus(app), 'Opening your browser…');
  release();
  await app.settle(50);
  assert.equal(rpStatus(app), 'Opened in your browser. Say what you were doing, then press Submit there.');
  // The shell no longer has this report.
  app.gate = (m) => (m === 'shell.openIssue' ? Promise.resolve({ status: 'unknown' }) : undefined);
  await app.click(button(rp(app), 'Open GitHub'));
  await app.settle(50);
  assert.equal(rpStatus(app), 'This report is too old to open. Copy it, or close and report again.');
  assert.ok(hasClass(byClass(rp(app), 'ot-rp-status')[0], 'is-warn'));
  assert.equal(button(rp(app), 'Open GitHub').props.disabled, true);
  assert.ok(!button(rp(app), 'Copy').props.disabled);
});

/* ---------- 4: which failed AI turns offer Report it ---------- */
test('the prompt: a failed turn with a normal-outcome code offers nothing; timeout offers Report it', async () => {
  const app = await startFullApp();
  await app.inner.call('fixture.setFixReply', { mode: 'hold' });
  await app.click(button(app.card(app.reportCardId()), 'Fix for me'));
  await app.until(() => !!app.taskId(), 'a task');
  const stream = 'task:' + app.taskId();
  const prompt = () => byClass(app.tree, 'ot-problem')[0] || null;
  let n = 0;
  for (const code of ['cancelled', 'stopped', 'busy', 'rate_limited', 'tool_blocked', 'not_allowed', 'auth', 'expired', 'exhausted', 'quota', 'network',
    'not_eligible', 'overloaded', 'unavailable', 'incomplete', 'interrupted', 'revoked']) {
    app.emit({ stream, type: 'agent.turn', payload: { turnId: 'turn-x' + (n++), status: 'failed', code } });
    await app.settle(20);
    assert.equal(prompt(), null, code);
  }
  app.emit({ stream, type: 'agent.turn', payload: { turnId: 'turn-y', status: 'failed', code: 'timeout' } });
  await app.settle(40);
  assert.ok(prompt() && text(prompt()).includes('’s task stopped with an error.'));
});

/* ---------- designer (after v87): the failed chat answer says Not working for an unknown key error ---------- */
test('a failed chat answer: Not working for an error Optimus doesn’t recognise; Offline for a network error', async () => {
  const app = await startFullApp();
  await app.inner.call('fixture.setProviderState', { kind: 'anthropic_api', status: 'connected' });
  await choose(app, { value: 'api', label: 'Claude (API)', kind: 'anthropic_api' });
  await app.inner.call('fixture.setFixReply', { mode: 'hold' });
  await app.until(() => !!app.reportCardId() && !!button(app.card(app.reportCardId()), 'Fix for me'), 'Fix for me');
  await app.click(button(app.card(app.reportCardId()), 'Fix for me'));
  await app.until(() => !!app.taskId(), 'a task');
  const stream = 'task:' + app.taskId();
  const fails = () => byClass(app.tree, 'ot-answer-fail').map(text);
  await status(app, 'anthropic_api', 'error', 'something_new');
  app.emit({ stream, type: 'agent.turn', payload: { turnId: 'turn-u', status: 'started' } });
  app.emit({ stream, type: 'agent.turn', payload: { turnId: 'turn-u', status: 'failed', code: 'unavailable' } });
  await app.settle(60);
  assert.ok(fails().some((t) => t.startsWith('Not working')), JSON.stringify(fails()));
  await status(app, 'anthropic_api', 'error', 'network');
  await app.settle(60);
  assert.ok(fails().some((t) => t.startsWith('Offline')), JSON.stringify(fails()));
});
