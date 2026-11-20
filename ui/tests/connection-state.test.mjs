// node --test ui/tests/   — m3-agent.md §9: one state function for every row, and where asking goes.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { choose } from './choose-ai.mjs';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { connectionState, routeAsk, aiName, pickerTile, storedValue, tileKind, kindTile, retiredProvider, retiredLanding, retiredRedirect } = require('../js/connection-state.js');
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
    [mcp('waiting', 'never_seen'), 'Claude', 'Not set up', 'install'],
    [mcp('connected'), 'Claude', 'Connected', null],
    [mcp('error', 'signed_out'), 'Claude', 'Signed out', 'signIn'],
    [mcp('waiting', 'idle'), 'Claude', 'Waiting', 'showCommand'],
    [mcp('pending', null, '2760'), 'Claude', 'Approve 2760', 'approve'],
    [mcp('paired'), 'Claude', 'Connected', 'start'],
    [mcp('active'), 'Claude', 'Active', 'activity'],
    [mcp('client_closed'), 'Claude', 'Closed', 'askAgain'],
    [mcp('error', 'bridge_rejected'), 'Claude', 'Setup issue', 'showCommand'],
    [mcp('error', 'pairing_expired'), 'Claude', 'Waiting', 'showCommand'],
    [mcp('error', 'denied'), 'Claude', 'Waiting', 'showCommand'],
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
  // The pill says "Claude"; the outside-window texts name the program the user acts on.
  assert.deepEqual([mcp('waiting', 'idle').label, mcp('waiting', 'idle').client, mcp('waiting', 'idle').cli], ['Claude', 'Claude Code', 'claude']);
  assert.equal(connectionState({ kind: 'claude_code_mcp', entry: { status: 'connected', label: 'Anything' } }).label, 'Claude');
  assert.equal(connectionState({ kind: 'claude_code_mcp', entry: null, label: 'Claude Code' }).label, 'Claude');
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
  await choose(be, 'anthropic_api');
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

test('Claude is shown as "Claude": pill words for every Claude state, and display names', () => {
  const pill = (st) => st.label + ' · ' + st.word;
  assert.equal(pill(mcp('connected')), 'Claude · Connected');
  assert.equal(pill(mcp('error', 'signed_out')), 'Claude · Signed out');
  assert.equal(pill(mcp('pending', null, '2760')), 'Claude · Approve 2760');
  assert.equal(aiName('claude_code_mcp', 'Claude Code'), 'Claude');
  assert.equal(aiName('claude_code_mcp', null), 'Claude');
  assert.equal(aiName(null, 'Claude Code'), 'Claude');               // a research or plan label from an older host
  assert.equal(aiName('chatgpt_plan', 'ChatGPT'), 'ChatGPT');
  assert.equal(aiName('chatgpt_plan', null), 'ChatGPT');
  assert.equal(aiName('anthropic_api', 'Claude (API)'), 'Claude (API)'); // API kinds keep the host's label
  assert.equal(aiName('openai_api', 'OpenAI (API)'), 'OpenAI (API)');
  assert.equal(aiName(null, null), null);
});

test('picker ids (design) ↔ stored onboarding values and provider kinds', () => {
  // Stored provider.value → the tile it selects (local and later: none of the AI tiles, Local only).
  assert.deepEqual(['claude-code', 'chatgpt', 'api', 'local', 'later'].map(pickerTile), ['claude', 'chatgpt', 'api-key', 'local', 'local']);
  assert.equal(pickerTile('anything'), null);
  assert.equal(pickerTile(null), null);
  // Tile → stored value; stored values pass through unchanged; nothing else is stored.
  assert.deepEqual(['claude', 'chatgpt', 'api-key', 'local', 'later'].map(storedValue), ['claude-code', 'chatgpt', 'api', 'local', 'later']);
  assert.deepEqual(['claude-code', 'api'].map(storedValue), ['claude-code', 'api']);
  assert.equal(storedValue('other'), null);
  // Round trip for every stored value the schema keeps.
  for (const v of ['claude-code', 'chatgpt', 'api', 'local']) assert.equal(storedValue(pickerTile(v)), v);
  // Tiles and kinds (the kinds on the wire do not change).
  assert.equal(tileKind('claude'), 'claude_code_mcp');
  assert.equal(tileKind('chatgpt'), 'chatgpt_plan');
  assert.equal(tileKind('api-key', 'anthropic'), 'anthropic_api');
  assert.equal(tileKind('api-key', 'openai'), 'openai_api');
  assert.equal(tileKind('local'), null);
  assert.equal(tileKind(null), null);
  assert.deepEqual(['claude_code_mcp', 'chatgpt_plan', 'anthropic_api', 'openai_api', 'local'].map(kindTile), ['claude', 'chatgpt', 'api-key', 'api-key', null]);
});

test('a saved AI that was removed: picker first, nothing selected, then where the user was', () => {
  const onb = (value, step) => ({ step, profile: { provider: value ? { value, label: value } : null } });
  assert.equal(retiredProvider({ value: 'codex', label: 'Codex' }), true);
  for (const v of ['claude-code', 'chatgpt', 'api', 'local', 'later']) assert.equal(retiredProvider({ value: v, label: v }), false);
  assert.equal(retiredProvider(null), false);
  // Complete onboarding: the picker, then the main page (the rest of onboarding stays complete).
  assert.deepEqual(retiredLanding(onb('codex', 'complete')), { stage: 'login', returnTo: 'main' });
  // Mid-onboarding: the picker, then the step they were on.
  assert.deepEqual(retiredLanding(onb('codex', 'use')), { stage: 'login', returnTo: 'use' });
  assert.deepEqual(retiredLanding(onb('codex', 'goal')), { stage: 'login', returnTo: 'goal' });
  assert.deepEqual(retiredLanding(onb('codex', 'specs')), { stage: 'login', returnTo: 'specs' });
  // Still on the provider step: just the picker, which leads on as usual.
  assert.deepEqual(retiredLanding(onb('codex', 'provider')), { stage: 'login', returnTo: null });
  // Anyone else boots as before.
  assert.equal(retiredLanding(onb('claude-code', 'complete')), null);
  assert.equal(retiredLanding(onb(null, 'use')), null);
  assert.equal(retiredLanding(null), null);
  // Nothing is pre-selected: the retired value selects no tile.
  assert.equal(pickerTile('codex'), null);
});

test('a removed AI with a scan or inventory still running: that view first, then the picker before the next stage', () => {
  const onb = (value, step) => ({ step, profile: { provider: value ? { value, label: value } : null } });
  const hold = retiredLanding(onb('codex', 'complete'));
  // The running scan (or the inventory on the specs step) shows first: no redirect while it is on screen or fading.
  for (const id of ['boot', 'scanning', 'specs']) assert.equal(retiredRedirect(hold, { id, leaving: false }), null, id);
  assert.equal(retiredRedirect(hold, { id: 'main', leaving: true }), null);
  // The scan ends (or the user goes on from the specs): the picker comes first, then that stage.
  assert.deepEqual(retiredRedirect(hold, { id: 'main', leaving: false }), { stage: 'login', returnTo: 'main' });
  assert.deepEqual(retiredRedirect(retiredLanding(onb('codex', 'specs')), { id: 'main', leaving: false }), { stage: 'login', returnTo: 'main' });
  // Already on the picker (the boot landing, or the user opened it): the hold is done.
  assert.deepEqual(retiredRedirect(hold, { id: 'login', leaving: false }), { clear: true });
  // Anyone else, or after the hold is done: nothing.
  assert.equal(retiredRedirect(retiredLanding(onb('claude-code', 'complete')), { id: 'main', leaving: false }), null);
  assert.equal(retiredRedirect(null, { id: 'main', leaving: false }), null);
});

test('picker: Claude Code status → the design\'s one grey line (claudeNote)', () => {
  const { claudeNote } = require('../js/connection-state.js');
  const cs = (o) => Object.assign({ known: true, installed: true, signedIn: true, blocked: false, outdated: false, signing: false, failed: null, auth: 'subscription' }, o);
  assert.equal(claudeNote(null), 'unknown');
  assert.equal(claudeNote({ known: false }), 'unknown');
  assert.equal(claudeNote(cs({})), null);
  assert.equal(claudeNote(cs({ auth: 'api_key' })), 'apikey');
  assert.equal(claudeNote(cs({ signedIn: null })), 'unknown');
  assert.equal(claudeNote(cs({ signedIn: false, signing: true })), 'signingin');
  assert.equal(claudeNote(cs({ signedIn: false })), null);
  assert.equal(claudeNote(cs({ installed: false, signedIn: null })), null);
  assert.equal(claudeNote(cs({ installed: false, signedIn: null, failed: 'Not started' })), 'installfailed');
  assert.equal(claudeNote(cs({ installed: false, signedIn: null, failed: 'Not finished' })), 'installfailed');
  // Installing (updating when it replaces an outdated copy): step 1 turns its gears.
  assert.equal(claudeNote(cs({ installed: false, signedIn: null, installing: true })), 'installing');
  assert.equal(claudeNote(cs({ installed: false, signedIn: null, installing: true, failed: 'Not started' })), 'installing');
  assert.equal(claudeNote(cs({ outdated: true, installing: true })), 'updating');
  assert.equal(claudeNote(cs({ blocked: true, blockedReason: 'node_missing', installing: true })), 'installing');
  assert.equal(claudeNote(cs({ outdated: true })), null);   // the steps say Update needed
  assert.equal(claudeNote(cs({ outdated: true, failed: 'Not started' })), 'installfailed');
  // The Claude app without Claude Code: the normal Install step with its line.
  assert.equal(claudeNote(cs({ installed: false, signedIn: null, app: true })), 'desktoponly');
  assert.equal(claudeNote(cs({ installed: false, signedIn: null, app: true, failed: 'Not started' })), 'installfailed');
  // A sign-in that didn't open: Sign in stays on.
  assert.equal(claudeNote(cs({ signedIn: false, failed: 'Not started' })), 'signinfailed');
  assert.equal(claudeNote(cs({ signedIn: false, failed: 'Not started', signing: true })), 'signingin');
  for (const r of ['policy', 'access_denied']) assert.equal(claudeNote(cs({ blocked: true, blockedReason: r })), 'blocked', r);
  assert.equal(claudeNote(cs({ blocked: true, blockedReason: 'launch_failed' })), 'unknown');
  // Both causes the installer fixes read as one repair, never named.
  for (const r of ['node_missing', 'unsupported_path']) assert.equal(claudeNote(cs({ blocked: true, blockedReason: r })), 'repair', r);
  assert.equal(claudeNote(cs({ blocked: true, blockedReason: 'unsupported_path', failed: 'Not started' })), 'installfailed');
});

test('picker: the mono word under each tile (tileStatus, v54): the designer’s words only, warnings marked by the design', () => {
  const { tileStatus, connectionState: cst } = require('../js/connection-state.js');
  const cs = (o) => Object.assign({ known: true, installed: true, signedIn: true, blocked: false, outdated: false, signing: false, failed: null }, o);
  assert.equal(tileStatus('claude', undefined, null), null);                       // not checked yet
  assert.equal(tileStatus('claude', { known: false }, null), 'Couldn’t check');
  assert.equal(tileStatus('claude', cs({ installed: false, signedIn: null }), null), 'Not installed');
  assert.equal(tileStatus('claude', cs({ installed: false, signedIn: null, app: true }), null), 'Not installed');
  assert.equal(tileStatus('claude', cs({ outdated: true }), null), 'Update needed');
  assert.equal(tileStatus('claude', cs({ signedIn: null }), null), 'Couldn’t check');
  assert.equal(tileStatus('claude', cs({ signedIn: false }), null), 'Signed out');
  assert.equal(tileStatus('claude', cs({}), { row: 'connected' }), 'Connected');
  assert.equal(tileStatus('claude', cs({}), null), null);
  assert.equal(tileStatus('claude', cs({ blocked: true, blockedReason: 'launch_failed' }), null), 'Couldn’t check');
  for (const r of ['policy', 'access_denied']) assert.equal(tileStatus('claude', cs({ blocked: true, blockedReason: r }), null), 'Blocked on this PC', r);
  for (const r of ['node_missing', 'unsupported_path']) assert.equal(tileStatus('claude', cs({ blocked: true, blockedReason: r }), null), 'Needs repair', r);
  const mcp = (status, detail) => cst({ kind: 'claude_code_mcp', entry: { kind: 'claude_code_mcp', status, detail } });
  assert.equal(tileStatus('claude', cs({ signedIn: false }), mcp('error', 'signed_out')), 'Signed out');
  const plan = (status, detail) => cst({ kind: 'chatgpt_plan', entry: { kind: 'chatgpt_plan', status, detail } });
  assert.equal(tileStatus('chatgpt', null, plan('connected')), 'Connected');
  assert.equal(tileStatus('chatgpt', null, plan('error', 'auth')), 'Signed out');
  assert.equal(tileStatus('chatgpt', null, plan('error', 'quota')), 'Limit reached');
  assert.equal(tileStatus('chatgpt', null, plan('error', 'not_eligible')), 'Not available for this account');
  assert.equal(tileStatus('chatgpt', null, plan('error', 'network')), 'Offline');
  for (const d of ['cancelled', 'callback_timeout', 'storage', 'unavailable']) assert.equal(tileStatus('chatgpt', null, plan('error', d)), null, d);
  assert.equal(tileStatus('chatgpt', null, plan('not_configured')), null);
  assert.equal(tileStatus('chatgpt', null, null), null);
  const key = (status, detail) => cst({ kind: 'anthropic_api', entry: { kind: 'anthropic_api', status, detail } });
  assert.equal(tileStatus('api-key', null, key('connected')), 'Connected');
  assert.equal(tileStatus('api-key', null, key('error', 'auth')), 'Key rejected');
  assert.equal(tileStatus('api-key', null, key('error', 'quota')), 'No credit');
  assert.equal(tileStatus('api-key', null, key('error', 'not_eligible')), 'Not available for this account');
  assert.equal(tileStatus('api-key', null, key('error', 'network')), 'Offline');
  for (const d of ['storage', 'unavailable']) assert.equal(tileStatus('api-key', null, key('error', d)), null, d);
  assert.equal(tileStatus('api-key', null, key('not_configured')), null);
});