// node --test ui/tests/   — the desktop adapter against a fake chrome.webview (protocol v1 §7).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { createDesktopAdapter } = require('../js/adapter-desktop.js');

function fakeWebview() {
  const sent = [], fns = [];
  return {
    sent,
    postMessage(m) { sent.push(structuredClone(m)); },
    addEventListener(type, fn) { if (type === 'message') fns.push(fn); },
    removeEventListener(type, fn) { const i = fns.indexOf(fn); if (i >= 0) fns.splice(i, 1); },
    // PostWebMessageAsJson delivers a parsed object in e.data.
    deliver(data) { fns.forEach((fn) => fn({ data })); },
  };
}

test('posts rpc messages and resolves by id, out of order', async () => {
  const wv = fakeWebview(), be = createDesktopAdapter(wv);
  const a = be.call('onboarding.get'), b = be.call('report.get', { reportId: 'report-1' });
  assert.deepEqual(wv.sent.map((m) => [m.type, m.method]), [['rpc', 'onboarding.get'], ['rpc', 'report.get']]);
  assert.notEqual(wv.sent[0].id, wv.sent[1].id);
  wv.deliver({ type: 'rpc.result', id: wv.sent[1].id, result: { reportId: 'report-1' } });
  wv.deliver({ type: 'rpc.result', id: wv.sent[0].id, result: { step: 'complete' } });
  assert.deepEqual(await b, { reportId: 'report-1' });
  assert.deepEqual(await a, { step: 'complete' });
});

test('rpc.error rejects with code and data; unknown ids are ignored', async () => {
  const wv = fakeWebview(), be = createDesktopAdapter(wv);
  const p = be.call('followup.respond', { idempotencyKey: 'k', reportId: 'r', expectedRevision: 1, scope: 'report', text: 'x' });
  wv.deliver({ type: 'rpc.result', id: 999, result: {} });
  wv.deliver(JSON.stringify({ type: 'rpc.error', id: wv.sent[0].id, error: { code: -32002, message: 'stale_revision', data: { current: { revision: 5 } } } }));
  await assert.rejects(p, (e) => e.code === -32002 && e.data.current.revision === 5);
});

test('events are delivered once per stream seq', () => {
  const wv = fakeWebview(), be = createDesktopAdapter(wv), got = [];
  const off = be.onEvent((e) => got.push(e.stream + '#' + e.seq));
  const ev = (stream, seq) => ({ type: 'event', event: { stream, seq, type: 'scan.progress', at: 'x', payload: { done: seq, total: 6 } } });
  wv.deliver(ev('scan:a', 1)); wv.deliver(ev('scan:a', 2)); wv.deliver(ev('scan:a', 2)); wv.deliver(ev('scan:a', 1));
  wv.deliver(ev('inventory:b', 1)); wv.deliver(ev('scan:a', 3));
  off(); wv.deliver(ev('scan:a', 4));
  assert.deepEqual(got, ['scan:a#1', 'scan:a#2', 'inventory:b#1', 'scan:a#3']);
});

test('reconnecting → ready re-sends unanswered calls with the same idempotencyKey and signals a resync', async () => {
  const wv = fakeWebview(), be = createDesktopAdapter(wv), states = [];
  be.onState((s, info) => states.push([s, info.resumed]));
  wv.deliver({ type: 'backend', state: 'ready' });
  const p = be.call('scan.start', { idempotencyKey: 'key-1', intent: 'audit.full', text: 'Begin scan' });
  const first = wv.sent[0];
  wv.deliver({ type: 'backend', state: 'reconnecting' });
  const q = be.call('scan.get', { scanId: 'scan-1' });           // held while reconnecting
  assert.equal(wv.sent.length, 1);
  wv.deliver({ type: 'backend', state: 'ready' });
  assert.equal(wv.sent.length, 3);
  const retry = wv.sent.find((m) => m.method === 'scan.start' && m.id !== first.id);
  assert.ok(retry, 'scan.start re-sent with a new rpc id');
  assert.equal(retry.params.idempotencyKey, 'key-1');
  assert.deepEqual(retry.params, first.params);
  // A late answer to the old id is ignored; the retry's answer resolves the call.
  wv.deliver({ type: 'rpc.result', id: first.id, result: { scanId: 'old' } });
  wv.deliver({ type: 'rpc.result', id: retry.id, result: { scanId: 'scan-1' } });
  const get = wv.sent.find((m) => m.method === 'scan.get');
  wv.deliver({ type: 'rpc.result', id: get.id, result: { status: 'running' } });
  assert.deepEqual(await p, { scanId: 'scan-1' });
  assert.deepEqual(await q, { status: 'running' });
  assert.deepEqual(states, [['ready', false], ['reconnecting', false], ['ready', true]]);
});

test('calls wait while the shell reports connecting', async () => {
  const wv = fakeWebview(), be = createDesktopAdapter(wv);
  wv.deliver({ type: 'backend', state: 'connecting' });
  const p = be.call('onboarding.get');
  assert.equal(wv.sent.length, 0);
  wv.deliver({ type: 'backend', state: 'ready' });
  assert.equal(wv.sent.length, 1);
  wv.deliver({ type: 'rpc.result', id: wv.sent[0].id, result: { revision: 1 } });
  assert.deepEqual(await p, { revision: 1 });
});

test('incompatible rejects pending and later calls', async () => {
  const wv = fakeWebview(), be = createDesktopAdapter(wv);
  const p = be.call('onboarding.get');
  wv.deliver({ type: 'backend', state: 'incompatible', detail: { supported: { major: 1, minor: 0 } } });
  await assert.rejects(p, (e) => e.code === -32010);
  await assert.rejects(be.call('onboarding.get'), (e) => e.code === -32010);
  assert.equal(be.state(), 'incompatible');
});

test('ignores malformed messages', () => {
  const wv = fakeWebview(), be = createDesktopAdapter(wv);
  wv.deliver('not json'); wv.deliver(null); wv.deliver({ type: 'nope' }); wv.deliver({ type: 'event' });
  assert.equal(be.state(), 'ready');
});

test('a retryable error before the first state message is held and re-sent on ready', async () => {
  // Real-app race: the shell posts its first state before the page can hear it, then fails the
  // page's first call as retryable while the host is still starting.
  const wv = fakeWebview(), be = createDesktopAdapter(wv);
  const p = be.call('onboarding.get');
  wv.deliver({ type: 'rpc.error', id: wv.sent[0].id, error: { code: -32000, message: 'backend unavailable', data: { retryable: true } } });
  wv.deliver({ type: 'backend', state: 'connecting' });
  await new Promise((r) => setTimeout(r, 300));
  assert.equal(wv.sent.length, 1);                                // held, not re-sent yet
  wv.deliver({ type: 'backend', state: 'ready' });
  assert.equal(wv.sent.length, 2);
  wv.deliver({ type: 'rpc.result', id: wv.sent[1].id, result: { step: 'provider' } });
  assert.deepEqual(await p, { step: 'provider' });
});
