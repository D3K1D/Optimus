// node --test ui/tests/   — m3-agent.md §9: one state function for every row, and where asking goes.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { connectionState, routeAsk } = require('../js/connection-state.js');
const { createFixtureAdapter } = require('../js/adapter-fixture.js');

const api = (status, detail) => connectionState({ kind: 'anthropic_api', entry: { kind: 'anthropic_api', label: 'Claude (API)', status, detail } });
const mcp = (status, detail, code, task) => connectionState({ kind: 'claude_code_mcp', entry: { kind: 'claude_code_mcp', label: 'Claude Code', status, detail, code }, task });

test('every §9 row: pill label · word, tone and action', () => {
  const rows = [
    [connectionState({ kind: null, local: true }), 'Local only', null, null, 'local'],
    [api('not_configured'), 'Claude (API)', 'Not set up', 'addKey', 'agent?no'],
    [api('checking'), 'Claude (API)', 'Checking…', null],
    [api('connected'), 'Claude (API)', 'Connected', null],
    [api('error', 'auth'), 'Claude (API)', 'Key rejected', 'updateKey'],
    [api('error', 'quota'), 'Claude (API)', 'No credit', 'updateKey'],
    [api('error', 'network'), 'Claude (API)', 'Offline', 'retry'],
    [api('error', 'unavailable'), 'Claude (API)', 'Busy', 'retry'],
    [api('error', 'not_eligible'), 'Claude (API)', 'Not available', 'updateKey'],
    [api('error', 'storage'), 'Claude (API)', 'Not saved', 'addKey'],
    [api('connected', 'not_saved'), 'Claude (API)', 'Connected', null],
    [mcp('waiting', 'never_seen'), 'Claude Code', 'Not set up', 'install'],
    [mcp('connected'), 'Claude Code', 'Connected', null],
    [mcp('error', 'signed_out'), 'Claude Code', 'Signed out', 'signIn'],
    [mcp('waiting', 'idle'), 'Claude Code', 'Waiting', 'showCommand'],
    [mcp('pending', null, '2760'), 'Claude Code', 'Approve 2760', 'approve'],
    [mcp('paired'), 'Claude Code', 'Connected', 'start'],
    [mcp('active'), 'Claude Code', 'Active', 'activity'],
    [mcp('client_closed'), 'Claude Code', 'Closed', 'askAgain'],
    [mcp('error', 'bridge_rejected'), 'Claude Code', 'Setup issue', 'showCommand'],
    [mcp('error', 'pairing_expired'), 'Claude Code', 'Waiting', 'showCommand'],
    [mcp('error', 'denied'), 'Claude Code', 'Waiting', 'showCommand'],
  ];
  for (const [st, label, word, action] of rows) {
    assert.equal(st.label, label);
    assert.equal(st.word, word, label + ' ' + st.row);
    assert.equal(st.action, action, label + ' ' + st.row);
  }
  assert.equal(api('connected').tone, 'connected');
  assert.equal(api('connected').usable, true);
  assert.equal(api('error', 'auth').usable, false);
  assert.equal(mcp('active').tone, 'live');
  assert.equal(mcp('waiting', 'idle').hint, 'Ask Claude Code about your PC');
  assert.equal(connectionState({ kind: 'codex_mcp', entry: { status: 'waiting', label: 'Codex' } }).hint, 'Ask Codex about your PC');
  assert.equal(connectionState({ kind: 'codex_mcp', entry: { status: 'waiting', label: 'Codex' } }).cli, 'codex');
  assert.equal(mcp('client_closed').hint, 'Ask Claude Code again');
  // Disconnect for any configured kind, never for a missing key.
  assert.equal(api('not_configured').disconnect, false);
  assert.equal(api('error', 'auth').disconnect, true);
  assert.equal(mcp('paired').disconnect, true);
  // An active MCP task counts as active even when the entry still says paired.
  assert.equal(mcp('paired', null, null, { status: 'active', recipient: { kind: 'claude_code_mcp' } }).row, 'active');
  // Backend connection lost: current label · Reconnecting…, no action.
  const rc = connectionState({ kind: 'anthropic_api', entry: { label: 'Claude (API)', status: 'connected' }, backend: 'reconnecting' });
  assert.deepEqual([rc.label, rc.word, rc.action, rc.usable], ['Claude (API)', 'Reconnecting…', null, false]);
  // No entry yet: never "Connected" before the backend says so.
  assert.equal(connectionState({ kind: 'anthropic_api', label: 'Claude (API)' }).word, 'Checking…');
});

test('asking routes to the agent only when usable, else to the row action (local answers keep working)', () => {
  assert.equal(routeAsk(connectionState({ kind: null, local: true })), 'local');
  assert.equal(routeAsk(api('connected')), 'agent');
  assert.equal(routeAsk(api('not_configured')), 'addKey');
  assert.equal(routeAsk(api('error', 'auth')), 'updateKey');
  assert.equal(routeAsk(api('error', 'quota')), 'updateKey');
  assert.equal(routeAsk(api('error', 'network')), 'retry');
  assert.equal(routeAsk(api('checking')), 'local');
  assert.equal(routeAsk(mcp('waiting', 'idle')), 'showCommand');
  assert.equal(routeAsk(mcp('waiting', 'never_seen')), 'install');
  assert.equal(routeAsk(mcp('connected')), 'agent');
  assert.equal(routeAsk(mcp('error', 'signed_out')), 'signIn');
  assert.equal(routeAsk(mcp('pending', null, '1234')), 'approve');
  assert.equal(routeAsk(mcp('paired')), 'start');
  assert.equal(routeAsk(mcp('active')), 'local');
  assert.equal(routeAsk(mcp('client_closed')), 'askAgain');
  assert.equal(routeAsk(mcp('error', 'bridge_rejected')), 'showCommand');
});

const fixture = JSON.parse(readFileSync(new URL('../fixtures/m1-simulated-audit.json', import.meta.url), 'utf8'));
const extras = JSON.parse(readFileSync(new URL('../fixtures/m2-extras.json', import.meta.url), 'utf8'));
function memStore() { const m = {}; return { getItem: (k) => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); } }; }

test('fixture emits provider.status for every transition, and the dev hook reaches every row', async () => {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  const seen = [];
  be.onEvent((e) => { if (e.type === 'provider.status') seen.push(e.payload.kind + ':' + e.payload.status + (e.payload.detail ? ':' + e.payload.detail : '')); });
  const tick = () => new Promise((r) => setTimeout(r, 60));
  const list = async () => Object.fromEntries((await be.call('provider.list')).providers.map((p) => [p.kind, p]));
  let m = await list();
  assert.equal(m.local.status, 'local');
  assert.equal(m.anthropic_api.status, 'not_configured');
  assert.deepEqual([m.claude_code_mcp.status, m.claude_code_mcp.detail], ['waiting', 'never_seen']);
  await be.call('shell.requestApiKey', { kind: 'anthropic_api' });
  await be.call('fixture.setProviderState', { kind: 'anthropic_api', status: 'error', detail: 'auth' });
  assert.equal((await be.call('provider.connect', { kind: 'anthropic_api' })).status, 'error');   // key kept, still rejected
  await be.call('shell.requestApiKey', { kind: 'anthropic_api' });                                 // Update key
  await be.call('fixture.setProviderState', { kind: 'anthropic_api', status: 'error', detail: 'network' });
  assert.equal((await be.call('provider.connect', { kind: 'anthropic_api' })).status, 'connected'); // Retry
  await be.call('provider.connect', { kind: 'claude_code_mcp' });
  await new Promise((r) => setTimeout(r, 80));
  m = await list();
  assert.equal(m.claude_code_mcp.status, 'pending');
  assert.match(m.claude_code_mcp.code, /^\d{4}$/);
  await be.call('fixture.setProviderState', { kind: 'claude_code_mcp', status: 'client_closed' });
  await be.call('fixture.setProviderState', { kind: 'claude_code_mcp', status: 'error', detail: 'bridge_rejected' });
  await be.call('provider.disconnect', { kind: 'anthropic_api' });
  await tick();
  for (const s of ['anthropic_api:checking', 'anthropic_api:connected', 'anthropic_api:error:auth', 'anthropic_api:error:network',
    'claude_code_mcp:pending', 'claude_code_mcp:client_closed', 'claude_code_mcp:error:bridge_rejected', 'anthropic_api:not_configured']) {
    assert.ok(seen.includes(s), 'missing ' + s + ' in ' + seen.join(' '));
  }
});

test('an in-app ask while the key is rejected fails its turn (the UI shows Key rejected · Update key)', async () => {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  const key = () => crypto.randomUUID();
  let o = await be.call('onboarding.get');
  await be.call('onboarding.save', { expectedRevision: o.revision, patch: { inventoryConsent: 'skipped', step: 'complete' } });
  const fin = new Promise((r) => { const off = be.onEvent((e) => { if (e.type === 'scan.finished') { off(); r(e); } }); });
  await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  const reportId = (await fin).payload.reportId;
  await be.call('shell.requestApiKey', { kind: 'anthropic_api' });
  const t = await be.call('agent.start', { idempotencyKey: key(), goal: 'x', recipientKind: 'anthropic_api', scope: { reportId }, grant: { diagnostics: [] } });
  await be.call('fixture.setProviderState', { kind: 'anthropic_api', status: 'error', detail: 'auth' });
  const failed = new Promise((r) => { const off = be.onEvent((e) => { if (e.type === 'agent.turn' && e.payload.status === 'failed') { off(); r(e); } }); });
  await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'hi' });
  assert.equal((await failed).payload.status, 'failed');
});

test('ChatGPT plan: sign-in words, no key actions, no Disconnect mid sign-in', () => {
  const st = (status, detail) => connectionState({ kind: 'chatgpt_plan', entry: { status, detail, label: 'ChatGPT' } });
  assert.deepEqual([st('not_configured').word, st('not_configured').action, st('not_configured').disconnect], ['Not signed in', 'signIn', false]);
  assert.deepEqual([st('checking').word, st('checking').disconnect], ['Checking…', false]);
  assert.deepEqual([st('connected').word, st('connected').usable], ['Connected', true]);
  assert.deepEqual([st('error', 'auth').word, st('error', 'auth').action], ['Signed out', 'signIn']);
  assert.deepEqual([st('error', 'not_eligible').word, st('error', 'not_eligible').action], ['Not available', null]);
  assert.deepEqual([st('error', 'quota').word, st('error', 'quota').action], ['Limit reached', 'retry']);
  assert.equal(st('error', 'network').action, 'retry');
});

test('m5 §1: an MCP kind signed in is usable in the app; a headless task keeps it usable', () => {
  const c = mcp('connected');
  assert.deepEqual([c.row, c.tone, c.usable, c.disconnect], ['connected', 'connected', true, true]);
  assert.deepEqual([mcp('error', 'signed_out').tone, mcp('error', 'signed_out').usable], ['expired', false]);
  assert.equal(mcp('waiting', 'never_seen').disconnect, false);
  const headless = { status: 'active', recipient: { kind: 'claude_code_mcp', mode: 'headless' } };
  assert.equal(routeAsk(mcp('active', null, null, headless)), 'agent');
  assert.equal(routeAsk(mcp('connected', null, null, headless)), 'agent');
  // A user-window task still counts as active there (asked in Claude Code's own window).
  assert.equal(routeAsk(mcp('active', null, null, { status: 'active', recipient: { kind: 'claude_code_mcp' } })), 'local');
});
