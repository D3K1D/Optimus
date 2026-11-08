// node --test ui/tests/   — first-run "Choose your AI" on other PCs (m3 §9, m5 §1–2, onboarding §2): every failure
// has its own word, a short reason and a next step; the fixture mirrors what the host and shell report.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { connectionState } = require('../js/connection-state.js');
const { createFixtureAdapter } = require('../js/adapter-fixture.js');
const fixture = JSON.parse(readFileSync(new URL('../fixtures/m1-simulated-audit.json', import.meta.url), 'utf8'));
const extras = JSON.parse(readFileSync(new URL('../fixtures/m2-extras.json', import.meta.url), 'utf8'));
function memStore() { const m = {}; return { getItem: (k) => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); } }; }
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const entryOf = async (be, kind) => (await be.call('provider.list')).providers.find((p) => p.kind === kind);

test('ChatGPT: every sign-in outcome has a word, a plain reason and a way on (never stuck)', () => {
  const st = (status, detail) => connectionState({ kind: 'chatgpt_plan', entry: { status, detail, label: 'ChatGPT' } });
  const rows = [
    ['not_eligible', 'Not available', null],
    ['callback_timeout', 'Not finished', 'signIn'],
    ['cancelled', 'Not signed in', 'signIn'],
    ['storage', 'Not saved', 'signIn'],
    ['network', 'Offline', 'retry'],
    ['unavailable', 'Busy', 'retry'],
    ['auth', 'Signed out', 'signIn'],
    ['quota', 'Limit reached', 'retry'],
  ];
  for (const [detail, word, action] of rows) {
    const s = st('error', detail);
    assert.deepEqual([s.word, s.action], [word, action], detail);
    if (detail !== 'cancelled') assert.ok(s.hint && s.hint.length < 110, detail + ' has a short reason');
  }
  assert.match(st('error', 'callback_timeout').hint, /browser/);
  assert.match(st('error', 'not_eligible').hint, /another AI/);
});

test('API key: rejected, offline, busy, not available and not saved each say why; not_saved still works', () => {
  const st = (status, detail) => connectionState({ kind: 'openai_api', entry: { status, detail, label: 'OpenAI (API)' } });
  for (const d of ['auth', 'quota', 'network', 'unavailable', 'not_eligible', 'storage']) assert.ok(st('error', d).hint, d);
  const ns = st('connected', 'not_saved');
  assert.deepEqual([ns.word, ns.usable], ['Connected', true]);
  assert.match(ns.hint, /next time/);
  assert.equal(st('connected').hint, null);
  assert.equal(st('error', 'storage').disconnect, false);
});

test('CLI blocked or outdated (backend detail): words and the install step, nothing to disconnect', () => {
  const st = (detail) => connectionState({ kind: 'claude_code_mcp', entry: { status: 'error', detail, label: 'Claude Code' } });
  const u = st('unavailable');
  assert.deepEqual([u.word, u.action, u.disconnect, u.usable], ['Can’t run', 'install', false, false]);
});

test('fixture shell.requestApiKey mirrors the shell: cancel is quiet, failures carry a code, storage and not_saved', async () => {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  const kind = 'anthropic_api';
  await be.call('fixture.setKeyResult', { result: 'cancelled' });
  assert.deepEqual(await be.call('shell.requestApiKey', { kind }), { status: 'cancelled' });
  assert.equal((await entryOf(be, kind)).status, 'not_configured');

  for (const code of ['auth', 'network', 'unavailable', 'not_eligible', 'storage', 'quota']) {
    await be.call('fixture.setKeyResult', { result: code });
    assert.deepEqual(await be.call('shell.requestApiKey', { kind }), { status: 'error', error: code }, code);
    const e = await entryOf(be, kind);
    assert.deepEqual([e.status, e.detail], ['error', code], code);
  }

  await be.call('fixture.setKeyResult', { result: 'not_saved' });
  assert.deepEqual(await be.call('shell.requestApiKey', { kind }), { status: 'connected' });
  let e = await entryOf(be, kind);
  assert.deepEqual([e.status, e.detail], ['connected', 'not_saved']);
  e = await be.call('provider.connect', { kind });   // Retry keeps saying it was not saved
  assert.deepEqual([e.status, e.detail], ['connected', 'not_saved']);
  e = await be.call('provider.disconnect', { kind });
  assert.deepEqual([e.status, e.detail], ['not_configured', null]);

  await be.call('shell.requestApiKey', { kind });     // the next one is a plain success
  e = await entryOf(be, kind);
  assert.deepEqual([e.status, e.detail], ['connected', null]);
});

test('fixture ChatGPT sign-in can end in callback_timeout, cancelled or storage, never left checking', async () => {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  for (const r of ['callback_timeout', 'cancelled', 'storage', 'network']) {
    await be.call('fixture.setSignIn', { result: r });
    assert.equal((await be.call('provider.connect', { kind: 'chatgpt_plan' })).status, 'checking');
    await sleep(80);
    const e = await entryOf(be, 'chatgpt_plan');
    assert.deepEqual([e.status, e.detail], ['error', r]);
  }
});

test('fixture shell.clientStatus: blocked, outdated, unknown sign-in and plan vs API key, as the shell reports them', async () => {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  let st = await be.call('shell.clientStatus', { client: 'claude_code' });
  assert.deepEqual([st.signedIn, st.auth], [true, 'subscription']);
  assert.equal((await entryOf(be, 'claude_code_mcp')).status, 'connected');

  st = await be.call('fixture.setClient', { client: 'claude_code', auth: 'api_key' });
  assert.equal(st.auth, 'api_key');

  st = await be.call('fixture.setClient', { client: 'claude_code', signedIn: null });
  assert.equal(st.signedIn, null);
  assert.equal(st.auth, undefined);

  // Blocked: installed:false, no paths, a reason; the host shows error/unavailable.
  st = await be.call('fixture.setClient', { client: 'claude_code', signedIn: true, blocked: true, blockedReason: 'policy' });
  assert.deepEqual([st.installed, st.blocked, st.blockedReason, st.source, st.version], [false, true, 'policy', undefined, undefined]);
  let e = await entryOf(be, 'claude_code_mcp');
  assert.deepEqual([e.status, e.detail], ['error', 'unavailable']);

  // The official installer gives a working CLI.
  await be.call('fixture.setClient', { client: 'claude_code', blockedReason: 'node_missing', installDelay: 20 });
  assert.equal((await be.call('shell.installClient', { client: 'claude_code' })).status, 'started');
  await sleep(60);
  st = await be.call('shell.clientStatus', { client: 'claude_code' });
  assert.deepEqual([st.installed, st.blocked, st.outdated], [true, undefined, undefined]);
  assert.equal((await entryOf(be, 'claude_code_mcp')).status, 'connected');

  // Outdated: installed with its details, outdated:true; error/unavailable until updated.
  st = await be.call('fixture.setClient', { client: 'codex', installed: true, signedIn: true, outdated: true });
  assert.deepEqual([st.installed, st.outdated, st.signedIn], [true, true, true]);
  e = await entryOf(be, 'codex_mcp');
  assert.deepEqual([e.status, e.detail], ['error', 'unavailable']);
  await be.call('fixture.setClient', { client: 'codex', installDelay: 20 });
  await be.call('shell.installClient', { client: 'codex' });
  await sleep(60);
  st = await be.call('shell.clientStatus', { client: 'codex' });
  assert.equal(st.outdated, undefined);
  assert.equal((await entryOf(be, 'codex_mcp')).status, 'connected');
});
