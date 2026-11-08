// node --test ui/tests/   — one-click connect stand-ins (contracts/onboarding.md §2): clientStatus / registerClient.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { createFixtureAdapter } = require('../js/adapter-fixture.js');
const fixture = JSON.parse(readFileSync(new URL('../fixtures/m1-simulated-audit.json', import.meta.url), 'utf8'));
const extras = JSON.parse(readFileSync(new URL('../fixtures/m2-extras.json', import.meta.url), 'utf8'));
function memStore() { const m = {}; return { getItem: (k) => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); } }; }

test('installed client: not registered → added; failed and cancelled once; outdated → update', async () => {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  let st = await be.call('shell.clientStatus', { client: 'claude_code' });
  assert.equal(st.installed, true);
  assert.equal(st.registered, 'no');
  assert.match(st.command, /^claude mcp add --scope user optimization-tool -- /);
  await be.call('fixture.setClient', { client: 'claude_code', failNext: true });
  assert.deepEqual(await be.call('shell.registerClient', { client: 'claude_code' }), { status: 'failed', error: 'exit_1' });
  await be.call('fixture.setClient', { client: 'claude_code', cancelNext: true });
  assert.deepEqual(await be.call('shell.registerClient', { client: 'claude_code' }), { status: 'cancelled' });
  assert.deepEqual(await be.call('shell.registerClient', { client: 'claude_code' }), { status: 'added' });   // Retry
  assert.equal((await be.call('shell.clientStatus', { client: 'claude_code' })).registered, 'yes');
  await be.call('fixture.setClient', { client: 'claude_code', registered: 'outdated' });
  assert.equal((await be.call('shell.clientStatus', { client: 'claude_code' })).registered, 'outdated');
  assert.deepEqual(await be.call('shell.registerClient', { client: 'claude_code' }), { status: 'added' });
});

test('client not installed: status says so, register answers not_installed, the command is the fallback', async () => {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  const st = await be.call('shell.clientStatus', { client: 'codex' });
  assert.deepEqual([st.installed, st.registered], [false, 'unknown']);
  assert.match(st.command, /^codex mcp add optimization-tool -- /);
  assert.deepEqual(await be.call('shell.registerClient', { client: 'codex' }), { status: 'not_installed' });
  await assert.rejects(be.call('shell.clientStatus', { client: 'other' }), (e) => e.code === -32602);
});

test('install with PowerShell: started (or cancelled), the CLI shows up a little later with source installer', async () => {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  await be.call('fixture.setClient', { client: 'codex', cancelNext: true });
  assert.deepEqual(await be.call('shell.installClient', { client: 'codex' }), { status: 'cancelled' });
  await be.call('fixture.setClient', { client: 'codex', installDelay: 20 });
  assert.deepEqual(await be.call('shell.installClient', { client: 'codex' }), { status: 'started' });
  assert.equal((await be.call('shell.clientStatus', { client: 'codex' })).installed, false);
  await new Promise((r) => setTimeout(r, 60));
  const st = await be.call('shell.clientStatus', { client: 'codex' });
  assert.deepEqual([st.installed, st.source], [true, 'installer']);
});

test('Claude app present without Claude Code yet: app flag, only for Claude and only while not installed', async () => {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  await be.call('fixture.setClient', { client: 'claude_code', installed: false, app: true });
  assert.equal((await be.call('shell.clientStatus', { client: 'claude_code' })).app, true);
  await be.call('fixture.setClient', { client: 'claude_code', installed: true });
  const st = await be.call('shell.clientStatus', { client: 'claude_code' });
  assert.equal(st.app, undefined);
  assert.equal(st.source, 'path');
  await be.call('fixture.setClient', { client: 'codex', app: true });
  assert.equal((await be.call('shell.clientStatus', { client: 'codex' })).app, undefined);
});

test('ChatGPT sign-in stand-in: checking, then connected; a chosen failure comes back as the error detail', async () => {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  const kinds = (await be.call('provider.list')).providers.map((p) => p.kind);
  assert.ok(kinds.includes('chatgpt_plan'));
  assert.equal((await be.call('provider.connect', { kind: 'chatgpt_plan' })).status, 'checking');
  await new Promise((r) => setTimeout(r, 80));
  const ok = (await be.call('provider.list')).providers.find((p) => p.kind === 'chatgpt_plan');
  assert.equal(ok.status, 'connected');
  await be.call('provider.disconnect', { kind: 'chatgpt_plan' });
  await be.call('fixture.setSignIn', { result: 'not_eligible' });
  await be.call('provider.connect', { kind: 'chatgpt_plan' });
  await new Promise((r) => setTimeout(r, 80));
  const bad = (await be.call('provider.list')).providers.find((p) => p.kind === 'chatgpt_plan');
  assert.deepEqual([bad.status, bad.detail], ['error', 'not_eligible']);
  await assert.rejects(be.call('shell.requestApiKey', { kind: 'chatgpt_plan' }), (e) => e.code === -32602);
});
