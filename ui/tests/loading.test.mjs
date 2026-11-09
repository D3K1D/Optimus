// node --test ui/tests/   — the loading screen's wiring to the backend's startup states (protocol-v1 §7).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { loadingState, wireLoading } = require('../js/connection-state.js');

test('each startup detail picks its loading state; ready and incompatible dismiss it', () => {
  assert.equal(loadingState('connecting', null), 'starting');
  assert.equal(loadingState('connecting', 'needs_update'), 'update');
  assert.equal(loadingState('reconnecting', 'other_session'), 'session');
  assert.equal(loadingState('connecting', 'cant_start'), 'failed');
  assert.equal(loadingState('connecting', 'something_new'), 'starting');
  assert.equal(loadingState('ready', null), 'ready');
  assert.equal(loadingState('incompatible', null), 'ready');
});

function fakes({ state = 'connecting', detail = null, known = true } = {}) {
  const fns = [], calls = [], seen = [];
  const backend = {
    state: () => state, detail: () => detail, known: () => known,
    onState(fn) { fns.push(fn); return () => fns.splice(fns.indexOf(fn), 1); },
    call(method, params) { calls.push([method, params]); return Promise.resolve({ retrying: true }); },
    push(s, d = null) { state = s; detail = d; known = true; fns.slice().forEach((fn) => fn(s, { detail: d })); },
    get listeners() { return fns.length; },
  };
  let handlers = null;
  const loading = { set: (s) => seen.push(s), ready: () => seen.push('ready'), on: (h) => { handlers = h; } };
  return { backend, loading, calls, seen, press: (k) => handlers[k]() };
}
const frames = () => new Promise((r) => setTimeout(r, 60));

test('follows the startup states, then goes after paint once ready and stops listening', async () => {
  const f = fakes({ detail: 'other_session' });
  wireLoading(f.backend, f.loading);
  assert.deepEqual(f.seen, ['session']);
  f.backend.push('connecting', 'cant_start');
  f.backend.push('ready');
  assert.deepEqual(f.seen, ['session', 'failed']);   // not yet: waits for the first paint
  await frames();
  assert.deepEqual(f.seen, ['session', 'failed', 'ready']);
  assert.equal(f.backend.listeners, 0);
});

test('a desktop backend with no state yet keeps the screen up until the shell reports one', async () => {
  const f = fakes({ state: 'ready', known: false });   // adapter-desktop.state() says "ready" before any message
  wireLoading(f.backend, f.loading);
  await frames();
  assert.deepEqual(f.seen, []);
  f.backend.push('connecting', 'needs_update');
  assert.deepEqual(f.seen, ['update']);
});

test('Try again asks the shell to relaunch the host', () => {
  const f = fakes({ detail: 'cant_start' });
  wireLoading(f.backend, f.loading);
  f.press('retry');
  assert.deepEqual(f.calls, [['shell.retryBackend', {}]]);
});
