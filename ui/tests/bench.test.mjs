// node --test ui/tests/   — M6a benchmark: the pure helpers in bench.js and the fixture's bench.* simulation.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const B = require('../js/bench.js');
const { createFixtureAdapter } = require('../js/adapter-fixture.js');
const fixture = JSON.parse(readFileSync(new URL('../fixtures/m1-simulated-audit.json', import.meta.url), 'utf8'));
const extras = JSON.parse(readFileSync(new URL('../fixtures/m2-extras.json', import.meta.url), 'utf8'));
function memStore() { const m = {}; return { getItem: (k) => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); } }; }
const key = () => crypto.randomUUID();
const until = (be, pred) => new Promise((r) => { const off = be.onEvent((e) => { if (pred(e)) { off(); r(e); } }); });

test('score and figure formatting', () => {
  assert.equal(B.nfmt(11382), '11,382');
  assert.equal(B.nfmt(null), '—');
  assert.equal(B.figureText({ value: 4.712, unit: 'GHz', source: 'pdh' }), '4.71 GHz');
  assert.equal(B.figureText({ value: 96, unit: '%', source: 'd3dkmt' }), '96%');
  assert.equal(B.figureText({ value: 6950, unit: 'MB/s' }), '6,950 MB/s');
  assert.equal(B.figureText({ value: null, unit: '°C', source: null }), null);
  const cpu = B.figureList({ power: { value: null, unit: 'W' }, clock: { value: 4.71, unit: 'GHz' }, temp: { value: null, unit: '°C' } }, 'cpu');
  assert.deepEqual(cpu.map((f) => [f.label, f.text]), [['Peak', null], ['Est. clock', '4.71 GHz'], ['Power', null]]);
  assert.equal(B.figureList({ clock: { value: 2.42, unit: 'GHz' } }, 'gpu')[0].label, 'Clock');
});

test('delta: up, down, same within 2%, none without a comparison', () => {
  assert.deepEqual(B.deltaInfo(1100, 1000), { kind: 'up', text: '▲ 10%', name: '10% better than last run' });
  assert.equal(B.deltaInfo(840, 1000).text, '▼ 16%');
  assert.equal(B.deltaInfo(1010, 1000).text, 'Same');
  assert.equal(B.deltaInfo(null, 1000), null);
  assert.equal(B.deltaInfo(1000, null), null);
  assert.equal(B.partDelta({ deltas: { cpu: { prev: 1248, pct: -16.3 } } }, 'cpu', 1045).text, '▼ 16%');
  assert.equal(B.partDelta({ deltas: { gpu: -5 } }, 'gpu', 1000).text, '▼ 5%');
  assert.equal(B.partDelta(null, 'cpu', 1000), null);
});

test('observation and reason words; missing sensors', () => {
  assert.equal(B.obsWord('power_limited'), 'Power limited');
  assert.equal(B.obsWord('clock_drop_after_boost'), 'Boost settles');
  assert.equal(B.obsWord('thermal_slowdown_brief'), 'Brief thermal slowdown');
  assert.equal(B.obsWord('ram_speed_below_module_rating'), 'RAM below module rating');
  assert.equal(B.obsWord('disk_below_typical_for_type'), 'Below typical for this drive');
  assert.equal(B.obsWord('lower_than_before_unconfirmed'), 'Lower than last time, not confirmed');
  assert.equal(B.obsWord('something_new'), 'Something new');
  assert.equal(B.reasonWord('needs_space'), 'Needs 4 GB free');
  assert.deepEqual(B.missingSensors([{ id: 'cpu.clock', available: true }, { id: 'cpu.temp', available: false }, { id: 'cpu.power', available: false }]),
    ['No temp sensor', 'No power sensor']);
});

test('share text: three lines, (partial), model names only', () => {
  const result = { score: 11382, partial: false, parts: [{ part: 'ram', score: 982 }, { part: 'cpu', score: 1045 }, { part: 'disk', score: 812 }, { part: 'gpu', score: 2014 }] };
  const dev = { cpu: 'Ryzen 7 7700', gpu: 'GeForce RTX 4060 Ti', ram: '32 GB DDR5-5600', disk: 'C: · NVMe SSD' };
  assert.equal(B.shareText(result, dev), 'Optimus benchmark: 11,382\nCPU 1,045 · GPU 2,014 · RAM 982 · Disk 812\nRyzen 7 7700 · GeForce RTX 4060 Ti · 32 GB DDR5-5600');
  const partial = Object.assign({}, result, { partial: true, parts: result.parts.map((p) => (p.part === 'disk' ? { part: 'disk', score: null } : p)) });
  const lines = B.shareText(partial, dev).split('\n');
  assert.equal(lines.length, 3);
  assert.match(lines[0], /\(partial\)$/);
  assert.match(lines[1], /Disk —$/);
});

test('live chart geometry and ring math', () => {
  const g = B.chartGeom([10, 20, 30], 40, 4, 120, 40);
  assert.equal(g.cur, 30); assert.equal(g.peak, 30);
  assert.match(g.line, /^M40\.00 /);              // newest at the right: 3 of 4 slots, first at x = 40
  assert.ok(g.line.endsWith('L120.00 10.50'));        // y = 39 − 30/40 × 38 on the fixed scale
  assert.ok(g.area.endsWith('Z'));
  const gap = B.chartGeom([10, null, 30], 40, 3, 120, 40);
  assert.equal((gap.line.match(/M/g) || []).length, 2);   // a null sample breaks the line
  assert.equal(gap.area, '');
  assert.equal(B.chartGeom([], 40).cur, null);
  const r = B.ringDash(0.25, 52);
  assert.equal(r.c, 326.7); assert.equal(r.offset, 245);
  assert.equal(B.ringDash(2, 52).offset, 0);
});

test('singleFlight: one call on its way at a time', async () => {
  let calls = 0, release;
  const f = B.singleFlight(() => { calls++; return new Promise((r) => { release = r; }); });
  const a = f(), b = f();
  await Promise.resolve(); await Promise.resolve();
  assert.equal(calls, 1);
  assert.equal(a, b);
  release('x');
  assert.equal(await a, 'x');
  await f; await new Promise((r) => setTimeout(r, 0));
  f(); await Promise.resolve(); await Promise.resolve();
  assert.equal(calls, 2);   // a new call once the first settled
});

test('pollAfter: next poll only after the previous one settles; stop() ends it', async () => {
  const q = []; const timers = { set: (fn, ms) => { q.push({ fn, ms }); return q.length; }, clear: () => { q.length = 0; } };
  let calls = 0, release;
  const stop = B.pollAfter(() => { calls++; return new Promise((r) => { release = r; }); }, 3000, timers);
  assert.equal(q.length, 1); assert.equal(q[0].ms, 3000);
  q.shift().fn();
  await Promise.resolve(); await Promise.resolve();
  assert.equal(calls, 1);
  assert.equal(q.length, 0);              // nothing scheduled while the call is pending
  release(); await new Promise((r) => setTimeout(r, 0));
  assert.equal(q.length, 1);              // scheduled 3 s after it completed
  stop();
  assert.equal(q.length, 0);
});

test('power and start errors', () => {
  assert.equal(B.powerBlocked({ ac: 'offline', battery: 'present' }), true);
  assert.equal(B.powerBlocked({ ac: 'unknown', battery: 'present' }), true);
  assert.equal(B.powerBlocked({ ac: 'unknown', battery: 'absent' }), false);
  assert.equal(B.powerBlocked({ ac: 'online', battery: 'present' }), false);
  assert.equal(B.startErrorWord({ code: -32013, data: { reason: 'on_battery' } }), 'Plug in to run');
  assert.equal(B.startErrorWord({ code: -32013, data: { reason: 'action_active' } }), 'Busy · try again');
  assert.equal(B.startErrorWord({ code: -32004, data: {} }), 'After the scan');
});

test('history keeps the voltage warning, the benchmark suggestion and a tool_blocked failure', () => {
  const { reconcileAnswers } = require('../js/connection-state.js');
  const hist = [
    { seq: 1, type: 'agent.question', payload: { turnId: 't1', text: 'Undervolt?' } },
    { seq: 2, type: 'agent.turn', payload: { turnId: 't1', status: 'started' } },
    { seq: 3, type: 'agent.message', payload: { turnId: 't1', text: 'It is your call.', warnings: ['voltage'], suggest: 'benchmark' } },
    { seq: 4, type: 'agent.question', payload: { turnId: 't2', text: 'Look it up' } },
    { seq: 5, type: 'agent.turn', payload: { turnId: 't2', status: 'started' } },
    { seq: 6, type: 'agent.turn', payload: { turnId: 't2', status: 'failed', code: 'tool_blocked' } }
  ];
  const list = [{ id: 'a2', taskId: 'k', turnId: 't2', q: 'Look it up', text: null, failedTurn: false }];
  const out = reconcileAnswers(list, hist, { taskId: 'k' });
  assert.equal(out.find((a) => a.turnId === 't2').failCode, 'tool_blocked');
  const a1 = out.find((a) => a.turnId === 't1');
  assert.deepEqual(a1.warnings, ['voltage']);
  assert.equal(a1.suggest, 'benchmark');
});

test('fixture: a run goes running → done with a result, comparison and fan guidance; History lists it', async () => {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  const caps = await be.call('bench.capabilities');
  assert.deepEqual(caps.power, { ac: 'online', battery: 'absent' });
  assert.equal(caps.activeRunId, null);
  const before = (await be.call('bench.list', { limit: 5 })).runs.length;
  const fin = until(be, (e) => e.type === 'bench.finished');
  const run = await be.call('bench.start', { idempotencyKey: key() });
  assert.equal(run.state, 'starting');
  assert.equal(run.metrics.cpu.length, 4);
  await assert.rejects(be.call('bench.start', { idempotencyKey: key() }), (e) => e.code === -32013 && e.data.reason === 'bench_active');
  await assert.rejects(be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'x' }), (e) => e.code === -32013 && e.data.reason === 'bench_active');
  assert.equal((await fin).payload.state, 'done');
  const done = await be.call('bench.get', { runId: run.runId });
  assert.equal(done.live, null);
  const r = done.result;
  assert.equal(r.partial, false);
  assert.ok(r.score > 0);
  assert.ok(r.comparison && r.comparison.deltas.cpu.pct < -15);
  assert.equal(r.parts.find((p) => p.part === 'gpu').verdict, 'running_hot');
  assert.deepEqual(r.fanGuidance.tips, ['bios_fan_mode', 'clean_filters', 'check_fans_spin', 'gpu_default_curve']);
  const list = (await be.call('bench.list', { limit: 5 })).runs;
  assert.equal(list.length, before + 1);
  const ev = r.parts.find((p) => p.part === 'gpu').evidenceIds[0];
  assert.match(ev, /^ev-[0-9a-f]{16}$/);
  assert.equal((await be.call('bench.evidence', { runId: run.runId, evidenceId: ev })).id, ev);
});

test('fixture: cancel → cancelled (not listed); unplugged → on_battery', async () => {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  const before = (await be.call('bench.list', { limit: 5 })).runs.length;
  const run = await be.call('bench.start', { idempotencyKey: key() });
  const fin = until(be, (e) => e.type === 'bench.finished');
  const c = await be.call('bench.cancel', { idempotencyKey: key(), runId: run.runId });
  assert.equal(c.state, 'stopping');
  assert.equal((await fin).payload.state, 'cancelled');
  assert.equal((await be.call('bench.list', { limit: 5 })).runs.length, before);
  await be.call('fixture.benchMode', { mode: 'unplugged' });
  assert.equal(B.powerBlocked((await be.call('bench.capabilities')).power), true);
  await assert.rejects(be.call('bench.start', { idempotencyKey: key() }), (e) => e.code === -32013 && e.data.reason === 'on_battery');
});

// ---- the fixture's runs against the host's schema (contracts/schemas/bench-run.schema.json) -------------------------
// A small validator for the JSON Schema keywords these schemas use (no dependency).
function schemaErrors(schemas, name, value) {
  const typeOk = (t, v) => t === 'null' ? v === null : t === 'array' ? Array.isArray(v)
    : t === 'object' ? v !== null && typeof v === 'object' && !Array.isArray(v)
    : t === 'integer' ? Number.isInteger(v) : t === 'number' ? typeof v === 'number' && Number.isFinite(v) : typeof v === t;
  const resolve = (ref, base) => {
    const [file, ptr] = ref.split('#');
    const doc = file ? schemas[file] : base;
    return [(ptr || '').split('/').filter(Boolean).reduce((n, k) => n[k], doc), doc];
  };
  const errs = [];
  const check = (s, v, base, path, out) => {
    if (s.$ref) { const [n, d] = resolve(s.$ref, base); check(n, v, d, path, out); return; }
    if (s.type && ![].concat(s.type).some((t) => typeOk(t, v))) { out.push(`${path}: expected ${s.type}, got ${JSON.stringify(v)}`); return; }
    if ('const' in s && v !== s.const) out.push(`${path}: not ${s.const}`);
    if (s.enum && !s.enum.includes(v)) out.push(`${path}: ${JSON.stringify(v)} not in [${s.enum}]`);
    if (typeof v === 'string' && s.pattern && !new RegExp(s.pattern).test(v)) out.push(`${path}: "${v}" does not match ${s.pattern}`);
    if (typeof v === 'number' && s.minimum != null && v < s.minimum) out.push(`${path}: below ${s.minimum}`);
    if (typeof v === 'number' && s.maximum != null && v > s.maximum) out.push(`${path}: above ${s.maximum}`);
    if (s.oneOf) {
      const n = s.oneOf.filter((o) => { const e = []; check(o, v, base, path, e); return !e.length; }).length;
      if (n !== 1) out.push(`${path}: matches ${n} of oneOf`);
    }
    if (Array.isArray(v)) {
      if (s.maxItems != null && v.length > s.maxItems) out.push(`${path}: more than ${s.maxItems} items`);
      if (s.items) v.forEach((x, i) => check(s.items, x, base, `${path}[${i}]`, out));
    } else if (v !== null && typeof v === 'object') {
      (s.required || []).forEach((k) => { if (!(k in v)) out.push(`${path}: missing ${k}`); });
      Object.keys(v).forEach((k) => {
        if (s.propertyNames) check(s.propertyNames, k, base, `${path}.{${k}}`, out);
        if (s.properties && s.properties[k]) check(s.properties[k], v[k], base, `${path}.${k}`, out);
        else if (s.additionalProperties === false) out.push(`${path}: unexpected ${k}`);
        else if (s.additionalProperties && typeof s.additionalProperties === 'object') check(s.additionalProperties, v[k], base, `${path}.${k}`, out);
      });
    }
  };
  check(schemas[name], value, schemas[name], '$', errs);
  return errs;
}
const benchSchemas = Object.fromEntries(['bench-run.schema.json', 'scan.schema.json'].map((f) =>
  [f, JSON.parse(readFileSync(new URL('../../contracts/schemas/' + f, import.meta.url), 'utf8'))]));
const benchErrors = (run) => schemaErrors(benchSchemas, 'bench-run.schema.json', run);

test('the schema check itself rejects what the fixture used to send', () => {
  const errs = benchErrors({ runId: 'bench-0123456789abcdef', state: 'running', startedAt: '2026-11-09T10:00:00.000Z', finishedAt: null, benchVersion: '1',
    configId: 'cfg-fixture-0001', tests: [{ part: 'cpu', state: 'running', reason: null, progress: 0.5 }],
    metrics: { cpu: [{ id: 'cpu.clock', unit: 'GHz', source: 'bench', max: 5, expected: null, limit: null, available: true, reason: null }] },
    live: { part: 'cpu', t0: '2026-11-09T10:00:00.000Z', samples: {} }, result: null });
  for (const want of ['missing reason', 'missing estSeconds', 'missing eventCursor', 'cfg-fixture-0001', 'missing liveScore', '"bench" not in', 'matches 0 of oneOf'])
    assert.ok(errs.some((e) => e.includes(want)), `expected an error about ${want}: ${errs.join(' | ')}`);
});

test('fixture runs match bench-run.schema.json: starting, live, done, cancelled, seeded and partial', async () => {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  const ok = (run, what) => assert.deepEqual(benchErrors(run), [], what);

  // The seeded earlier run (History).
  const seeded = (await be.call('bench.list', { limit: 5 })).runs[0];
  ok(await be.call('bench.get', { runId: seeded.runId }), 'seeded run');

  const fin = until(be, (e) => e.type === 'bench.finished');
  const gpuDone = until(be, (e) => e.type === 'bench.progress' && e.payload.part === 'gpu' && e.payload.test === 'done');
  const started = await be.call('bench.start', { idempotencyKey: key() });
  ok(started, 'starting run');
  assert.deepEqual(started.eventCursor, { stream: 'bench:' + started.runId, seq: 0 });
  await gpuDone;
  let live = await be.call('bench.get', { runId: started.runId });
  ok(live, 'live run');                                                    // a part that just started has live.score null
  // Its first reading gives live.score a number, as the host sends it. Under load the get above can land before it.
  for (let i = 0; i < 100 && typeof live.live?.score !== 'number'; i++) {
    await new Promise((r) => setTimeout(r, 20));
    live = await be.call('bench.get', { runId: started.runId });
  }
  ok(live, 'live run with a score');
  assert.equal(typeof live.live.score, 'number');
  assert.ok(live.eventCursor.seq > 0);
  assert.deepEqual(live.metrics.gpu.map((m) => m.id), ['gpu.clock', 'gpu.temp', 'gpu.power', 'gpu.fan']);
  assert.deepEqual(live.metrics.disk.map((m) => m.id), ['disk.read', 'disk.write', 'disk.random4k', 'disk.temp']);
  await fin;
  const done = await be.call('bench.get', { runId: started.runId });
  ok(done, 'done run');
  const cpu = done.result.parts.find((p) => p.part === 'cpu');
  assert.deepEqual(cpu.observations[0], { id: 'power_limited', value: 82, unit: '%' });   // objects, as the host sends them
  assert.equal(B.obsWord(cpu.observations[0]), 'Power limited');
  assert.deepEqual(cpu.missingSensors.map((m) => m.id), ['temp', 'fan']);
  assert.equal(B.metricInfo({ id: 'disk.random4k', unit: 'MB/s' }).label, 'Random 4K');

  // Cancelled, then a partial run (disk skipped).
  const fin2 = until(be, (e) => e.type === 'bench.finished');
  const second = await be.call('bench.start', { idempotencyKey: key() });
  ok(await be.call('bench.cancel', { idempotencyKey: key(), runId: second.runId }), 'stopping run');
  await fin2;
  ok(await be.call('bench.get', { runId: second.runId }), 'cancelled run');
  await be.call('fixture.benchMode', { mode: 'partial' });
  const fin3 = until(be, (e) => e.type === 'bench.finished');
  const third = await be.call('bench.start', { idempotencyKey: key() });
  await fin3;
  const partial = await be.call('bench.get', { runId: third.runId });
  ok(partial, 'partial run');
  assert.equal(partial.result.partial, true);
});
