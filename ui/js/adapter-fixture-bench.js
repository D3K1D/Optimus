/* Fixture adapter, M6a part: an offline simulation of the benchmark (contracts/m6-benchmark.md §2, §5–§7) so the
   website demo shows start → live charts → result. Plugged into adapter-fixture.js. Every number is invented: no
   real PC is measured. A run is compressed to about 30 s; samples arrive four times a second (each stands for one
   1 Hz sample), so the charts fill as they would in two minutes.

   Beyond the contract (fixture only):
     fixture.benchMode {mode}   normal | battery (the run stops on battery at the GPU test, then the PC stays
                                unplugged for 15 s) | unplugged (AC offline, battery present) | partial (disk skipped).
     The website picks a mode with ?bench=<mode>. */
(function (root) {
  'use strict';

  var ORDER = ['ram', 'cpu', 'gpu', 'disk'];
  var DEVICES = { cpu: 'Ryzen 7 7700', gpu: 'GeForce RTX 4060 Ti', ram: '32 GB DDR5-5600', disk: 'C: · NVMe SSD 1 TB' };
  var PART_MS = 6500, COOL_MS = 1000, TICK = 250, WINDOW = 60;
  var BENCH_VERSION = '1', CONFIG_ID = 'cfg-f1c7a9e000000001', EST_SECONDS = 120, PART_SECONDS = 30;

  // The host's four slots per part (contracts/schemas/bench-run.schema.json, m6 §2): cpu/gpu clock|temp|power|fan,
  // ram read|write|copy|latency, disk read|write|random4k|temp. id, unit, source (pdh|emi|d3dkmt|nvml|wmi|worker|none),
  // max (fixed scale), expected, limit; base drives the invented samples.
  var METRICS = {
    ram: [{ id: 'ram.read', unit: 'GB/s', source: 'worker', max: 90, expected: 72, base: 70.4 },
      { id: 'ram.write', unit: 'GB/s', source: 'worker', max: 90, expected: 70, base: 68.9 },
      { id: 'ram.copy', unit: 'GB/s', source: 'worker', max: 90, expected: 64, base: 63.2 },
      { id: 'ram.latency', unit: 'ns', source: 'worker', max: 120, base: 72 }],
    cpu: [{ id: 'cpu.clock', unit: 'GHz', source: 'pdh', max: 5.6, expected: 5.0, base: 4.71 },
      { id: 'cpu.temp', unit: '°C', source: 'none', max: 100, available: false, reason: 'needs_advanced_sensors' },
      { id: 'cpu.power', unit: 'W', source: 'emi', max: 160, base: 88 },
      { id: 'cpu.fan', unit: 'RPM', source: 'none', max: 3000, available: false, reason: 'needs_advanced_sensors' }],
    gpu: [{ id: 'gpu.clock', unit: 'GHz', source: 'nvml', max: 3.0, expected: 2.61, base: 2.42 },
      { id: 'gpu.temp', unit: '°C', source: 'd3dkmt', max: 100, limit: 83, base: 87 },
      { id: 'gpu.power', unit: '%', source: 'd3dkmt', max: 120, base: 96 },
      { id: 'gpu.fan', unit: 'RPM', source: 'd3dkmt', max: 3200, base: 2650 }],
    disk: [{ id: 'disk.read', unit: 'MB/s', source: 'worker', max: 8000, expected: 7000, base: 6950 },
      { id: 'disk.write', unit: 'MB/s', source: 'worker', max: 8000, expected: 6300, base: 5900 },
      { id: 'disk.random4k', unit: 'MB/s', source: 'worker', max: 120, base: 86 },
      { id: 'disk.temp', unit: '°C', source: 'wmi', max: 100, available: false, reason: 'needs_admin' }]
  };
  // The finished run's result, per part (scores: 1000 × measured / reference). Observations are objects {id, value?, unit?}.
  var RESULT = {
    ram: { score: 982, verdict: 'no_issue', why: [], observations: [{ id: 'ram_speed_below_module_rating' }],
      figures: { read: [70.4, 'GB/s', 'worker'], write: [68.9, 'GB/s', 'worker'], latency: [72, 'ns', 'worker'] } },
    cpu: { score: 1045, verdict: 'slower_than_before', why: ['multi_thread_below_history'],
      observations: [{ id: 'power_limited', value: 82, unit: '%' }, { id: 'clock_drop_after_boost' }],
      figures: { clock: [4.71, 'GHz', 'pdh'], power: [88, 'W', 'emi'], limit: [82, '%', 'pdh'] } },
    gpu: { score: 2014, verdict: 'running_hot', why: ['thermal_slowdown'], observations: [],
      figures: { temp: [87, '°C', 'd3dkmt'], clock: [2.42, 'GHz', 'nvml'], power: [96, '%', 'd3dkmt'] } },
    disk: { score: 812, verdict: 'no_issue', why: [], observations: [{ id: 'lower_than_before_unconfirmed' }],
      figures: { read: [6950, 'MB/s', 'worker'], write: [5900, 'MB/s', 'worker'], random: [86, 'MB/s', 'worker'] } }
  };
  var PREV = { ram: 979, cpu: 1248, gpu: 2060, disk: 884 };
  var FAN_GUIDANCE = { brand: 'any', tips: ['bios_fan_mode', 'clean_filters', 'check_fans_spin', 'gpu_default_curve'], pump: false };
  var ACTIVE = { starting: 1, running: 1, stopping: 1 };

  function overallOf(scores) {
    var v = scores.filter(function (s) { return s != null && s > 0; });
    if (!v.length) return null;
    var lg = v.reduce(function (a, s) { return a + Math.log(s); }, 0) / v.length;
    return Math.round(Math.exp(lg) * 10);
  }

  /** ctx: { state(), emit, later, stop, withIdem, rpcError, clone, randomHex, H, save } */
  function createFixtureBench(ctx) {
    var E = ctx.rpcError, clone = ctx.clone;
    var mode = 'normal', unpluggedUntil = 0;
    try { var m = /[?&]bench=(\w+)/.exec(root && root.location ? root.location.search : ''); if (m) mode = m[1]; } catch (e) { /* no location */ }

    function B() {
      var S = ctx.state();
      if (!S.bench) S.bench = { runs: {}, order: [] };
      if (!S.bench.seeded) { S.bench.seeded = true; seedPrevious(S.bench); }
      return S.bench;
    }
    function evId(runId, part, metric) { return 'ev-' + (runId + part + metric).split('').reduce(function (h, c) { return ((h * 31) + c.charCodeAt(0)) >>> 0; }, 7).toString(16).padStart(8, '0') + 'b0c4f1e2'; }
    function figuresOf(spec) {
      var out = {};
      Object.keys(spec).forEach(function (k) { out[k] = { value: spec[k][0], unit: spec[k][1], source: spec[k][2] }; });
      return out;
    }
    // The slots a part could not read, as the result lists them: [{id: 'temp', reason}].
    function missingOf(part) {
      return METRICS[part].filter(function (m) { return m.available === false; }).map(function (m) { return { id: m.id.slice(m.id.indexOf('.') + 1), reason: m.reason }; });
    }
    function testOf(part, state, progress) {
      return { part: part, device: DEVICES[part], state: state, reason: null, progress: progress, liveScore: null, etaSec: null, subtest: null };
    }
    function metricsView() {
      var out = {};
      ORDER.forEach(function (p) {
        out[p] = METRICS[p].map(function (m) {
          return { id: m.id, unit: m.unit, source: m.source, max: m.max, expected: m.expected == null ? null : m.expected, limit: m.limit == null ? null : m.limit,
            available: m.available !== false, reason: m.available === false ? m.reason : null };
        });
      });
      return out;
    }
    // A finished run a few days ago, so the first fixture run has something to compare with (and History a row).
    function seedPrevious(b) {
      var at = new Date(Date.now() - 3 * 86400000), runId = 'bench-' + ctx.randomHex(16);
      var parts = ORDER.map(function (p) {
        return { part: p, device: DEVICES[p], score: PREV[p], verdict: 'no_issue', reason: null, why: [], observations: [], evidenceIds: [], figures: figuresOf(RESULT[p].figures),
          missingSensors: missingOf(p) };
      });
      b.runs[runId] = { runId: runId, state: 'done', reason: null, startedAt: new Date(at.getTime() - 120000).toISOString(), finishedAt: at.toISOString(), benchVersion: BENCH_VERSION, configId: CONFIG_ID,
        estSeconds: EST_SECONDS, power: { ac: 'online', battery: 'absent' },
        tests: ORDER.map(function (p) { return testOf(p, 'done', 1); }), metrics: metricsView(), live: null,
        result: { score: overallOf(ORDER.map(function (p) { return PREV[p]; })), partial: false, partsScored: ORDER.slice(), comparison: null, differentSetup: false, parts: parts, fanGuidance: null } };
      b.order.unshift(runId);
    }

    function power() {
      if (mode === 'unplugged' || Date.now() < unpluggedUntil) return { ac: 'offline', battery: 'present' };
      return { ac: 'online', battery: mode === 'battery' ? 'present' : 'absent' };
    }
    function blockedByPower(p) { return p.ac === 'offline' || (p.ac === 'unknown' && p.battery === 'present'); }
    function active() { var b = B(); return b.order.map(function (id) { return b.runs[id]; }).filter(function (r) { return r && ACTIVE[r.state]; })[0] || null; }
    function getRun(id) { var r = B().runs[id]; if (!r) throw E(-32001, 'not_found', { kind: 'bench', id: id }); return r; }
    function view(r) {
      var v = clone(r), stream = 'bench:' + r.runId;
      delete v.rng; delete v.plan; delete v.seed;
      if (!ACTIVE[r.state]) v.live = null;
      v.eventCursor = { stream: stream, seq: (ctx.state().streams || {})[stream] || 0 };
      return v;
    }
    function progress(r, extra) { ctx.emit('bench:' + r.runId, 'bench.progress', Object.assign({ runId: r.runId, state: r.state }, extra || {})); }
    function finish(r, state) {
      ctx.stop('bench:' + r.runId);
      r.state = state; r.finishedAt = new Date().toISOString();
      r.tests.forEach(function (t) {
        if (t.state === 'running' || t.state === 'queued') { t.state = state === 'done' ? 'done' : 'cancelled'; t.reason = state === 'done' ? null : state; }
        t.etaSec = null;
      });
      ctx.emit('bench:' + r.runId, 'bench.finished', { runId: r.runId, state: state });
      ctx.save();
    }

    function lease() {
      var S = ctx.state();
      var scan = Object.keys(S.scans || {}).filter(function (id) { var s = S.scans[id].status; return s === 'running' || s === 'queued'; })[0];
      if (scan) throw E(-32004, 'scan_active', { scanId: scan });
      var ops = (S.actions && S.actions.ops) || {};
      var op = Object.keys(ops).filter(function (k) { var o = ops[k]; return (o.state === 'prepared' || o.state === 'attempted') && /^(power\.|defender\.)/.test(o.actionId || ''); })[0];
      if (op) throw E(-32013, 'invalid_state', { reason: 'action_active', operationId: op });
      var a = active();
      if (a) throw E(-32013, 'invalid_state', { reason: 'bench_active', runId: a.runId });
      if (blockedByPower(power())) throw E(-32013, 'invalid_state', { reason: 'on_battery' });
    }

    function start(p) {
      var want = p.parts == null ? ORDER.slice() : p.parts;
      if (!Array.isArray(want) || !want.length || want.some(function (x, i) { return ORDER.indexOf(x) < 0 || want.indexOf(x) !== i; })) throw E(-32602, 'invalid params', { field: 'parts' });
      lease();
      var b = B(), runId = 'bench-' + ctx.randomHex(16), job = 'bench:' + runId;
      var parts = ORDER.filter(function (x) { return want.indexOf(x) >= 0; });
      var skip = mode === 'partial' ? { disk: 'needs_space' } : {};
      var r = { runId: runId, state: 'starting', reason: null, startedAt: new Date().toISOString(), finishedAt: null, benchVersion: BENCH_VERSION, configId: CONFIG_ID,
        estSeconds: Math.round(EST_SECONDS * parts.length / ORDER.length), power: power(),
        tests: parts.map(function (x) { return testOf(x, 'queued', 0); }), metrics: metricsView(), live: null, result: null, seed: 7 };
      b.runs[runId] = r; b.order.unshift(runId);
      var t = 300;
      ctx.later(job, function () { r.state = 'running'; progress(r); ctx.save(); }, t);
      parts.forEach(function (part, i) {
        var test = r.tests[i];
        if (skip[part]) { ctx.later(job, function () { test.state = 'skipped'; test.reason = skip[part]; progress(r, { part: part, test: 'skipped' }); }, t); return; }
        var begin = t;
        ctx.later(job, function () {
          if (mode === 'battery' && part === 'gpu') {
            // AC went away: stopping at once, then stopped_on_battery (§3); the PC stays unplugged a little while.
            unpluggedUntil = Date.now() + 15000;
            r.state = 'stopping'; progress(r); ctx.stop(job);
            setTimeout(function () { finish(r, 'stopped_on_battery'); }, 400);
            return;
          }
          test.state = 'running'; r.live = { part: part, t0: new Date().toISOString(), score: null, samples: {} };
          progress(r, { part: part, test: 'running' });
        }, begin);
        for (var k = 1; k * TICK <= PART_MS; k++) (function (k) {
          ctx.later(job, function () {
            if (test.state !== 'running') return;
            var prog = Math.min(1, k * TICK / PART_MS);
            tick(r, part, prog);
            test.progress = prog;
            // The running score settles toward the part's result, as the host's live score does.
            test.liveScore = r.live.score = Math.round(RESULT[part].score * (0.7 + 0.3 * Math.min(1, prog * 3)));
            test.etaSec = Math.round((1 - prog) * PART_SECONDS);
          }, begin + k * TICK);
        })(k);
        ctx.later(job, function () {
          if (test.state !== 'running') return;
          test.state = 'done'; test.progress = 1; test.liveScore = RESULT[part].score; test.etaSec = null;
          progress(r, { part: part, test: 'done' }); ctx.save();
        }, begin + PART_MS);
        t = begin + PART_MS + (i < parts.length - 1 ? COOL_MS : 0);
      });
      ctx.later(job, function () { if (r.state !== 'running') return; r.result = resultOf(r, skip); finish(r, 'done'); }, t + 200);
      ctx.save();
      return view(r);
    }
    // Invented samples: a ramp to the part's base value with a little noise; temperatures climb more slowly.
    function rnd(r) { r.seed = (r.seed * 16807) % 2147483647; return r.seed / 2147483647; }
    function tick(r, part, prog) {
      var s = r.live.samples;
      METRICS[part].forEach(function (m) {
        var v = null;
        if (m.available !== false) {
          v = m.base * (0.55 + 0.45 * Math.min(1, prog * 6)) * (1 + (rnd(r) - 0.5) * 0.05);
          if (/temp$/.test(m.id)) v = 42 + (m.base - 42) * Math.min(1, prog * 2.2) + (rnd(r) - 0.5) * 1.5;
          v = Math.round(v * 100) / 100;
        }
        var a = s[m.id] = (s[m.id] || []).concat([v]);
        if (a.length > WINDOW) a.shift();
      });
    }
    function resultOf(r, skip) {
      var parts = r.tests.map(function (t) {
        var R = RESULT[t.part], gone = t.state !== 'done';
        return { part: t.part, device: DEVICES[t.part], score: gone ? null : R.score, verdict: gone ? 'unmeasured' : R.verdict, reason: gone ? t.reason || skip[t.part] || 'skipped' : null,
          why: gone ? [] : R.why.slice(), observations: gone ? [] : clone(R.observations), missingSensors: missingOf(t.part),
          evidenceIds: gone ? [] : Object.keys(R.figures).filter(function (k) { return R.figures[k][0] != null; }).map(function (k) { return evId(r.runId, t.part, k); }),
          figures: gone ? {} : figuresOf(R.figures) };
      });
      var scored = parts.filter(function (p) { return p.score != null; });
      var partial = scored.length < ORDER.length;
      var prev = B().order.map(function (id) { return B().runs[id]; }).filter(function (x) { return x && x.runId !== r.runId && x.state === 'done' && x.result && !x.result.partial; })[0];
      var comparison = null;
      if (!partial && prev) {
        var deltas = {};
        prev.result.parts.forEach(function (pp) {
          var mine = parts.filter(function (x) { return x.part === pp.part; })[0];
          if (mine && mine.score != null && pp.score) deltas[pp.part] = { prev: pp.score, pct: Math.round((mine.score - pp.score) / pp.score * 1000) / 10 };
        });
        comparison = { prevRunId: prev.runId, prevScore: prev.result.score, prevFinishedAt: prev.finishedAt, deltas: deltas };
      }
      var hot = parts.some(function (p) { return p.verdict === 'running_hot'; });
      return { score: overallOf(scored.map(function (p) { return p.score; })), partial: partial, partsScored: scored.map(function (p) { return p.part; }),
        comparison: comparison, differentSetup: false, parts: parts, fanGuidance: hot ? clone(FAN_GUIDANCE) : null };
    }
    function cancel(p) {
      var r = getRun(p.runId);
      if (!ACTIVE[r.state] || r.state === 'stopping') return view(r);
      r.state = 'stopping'; progress(r); ctx.stop('bench:' + r.runId);
      // As the host: the worker is told to stop, cleanup runs, then cancelled.
      setTimeout(function () { finish(r, 'cancelled'); }, 600);
      return view(r);
    }

    return {
      active: active,
      // A reload is a restart: a run that was going is interrupted (§2 lifecycle).
      __restart: function () {
        var b = B();
        b.order.forEach(function (id) { var r = b.runs[id]; if (r && ACTIVE[r.state]) { r.state = 'interrupted'; r.finishedAt = new Date().toISOString(); r.live = null; } });
      },
      methods: {
        'bench.capabilities': function () {
          var a = active();
          return { power: power(), parts: ORDER.map(function (p) { return { id: p, device: DEVICES[p], testable: !(mode === 'partial' && p === 'disk'), reason: mode === 'partial' && p === 'disk' ? 'needs_space' : null }; }),
            sensors: [].concat.apply([], ORDER.map(function (p) { return METRICS[p].filter(function (m) { return m.available !== false && m.source !== 'worker'; }).map(function (m) { return { id: m.id, source: m.source, unit: m.unit }; }); })),
            estSeconds: EST_SECONDS, benchVersion: BENCH_VERSION, activeRunId: a ? a.runId : null };
        },
        'bench.start': function (p) { return ctx.withIdem('bench.start', p, start); },
        'bench.get': function (p) { return view(getRun(p.runId)); },
        'bench.cancel': function (p) { return ctx.withIdem('bench.cancel', p, cancel); },
        'bench.list': function (p) {
          var lim = Math.max(1, Math.min((p && p.limit) || 50, 50)), b = B();
          var ids = b.order.filter(function (id) { return b.runs[id] && b.runs[id].state === 'done'; });
          var from = p && p.before ? ids.indexOf(p.before) + 1 : 0, page = ids.slice(from, from + lim);
          return { runs: page.map(function (id) { var r = b.runs[id]; return { runId: id, finishedAt: r.finishedAt, score: r.result ? r.result.score : null, parts: r.tests.map(function (t) { return t.part; }), benchVersion: r.benchVersion, configId: r.configId }; }),
            next: from + lim < ids.length ? page[page.length - 1] : null };
        },
        'bench.evidence': function (p) {
          var r = getRun(p.runId), hit = null;
          if (r.result) r.result.parts.forEach(function (pt) {
            Object.keys(pt.figures || {}).forEach(function (k) {
              if (evId(r.runId, pt.part, k) === p.evidenceId) {
                var f = pt.figures[k];
                hit = { id: p.evidenceId, category: 'performance', source: f.source, observedAt: r.finishedAt, interval: null, display: pt.part.toUpperCase() + ' ' + k + ': ' + f.value + ' ' + f.unit,
                  value: clone(f), collectorId: 'bench.' + pt.part, collectorVersion: BENCH_VERSION, quality: 'measured' };
              }
            });
          });
          if (!hit) throw E(-32001, 'not_found', { kind: 'evidence', id: p.evidenceId });
          return hit;
        },
        'fixture.benchMode': function (p) { mode = p.mode || 'normal'; unpluggedUntil = 0; return { mode: mode }; }
      }
    };
  }

  createFixtureBench.overallOf = overallOf;
  if (root) { root.OptTool = root.OptTool || {}; root.OptTool.createFixtureBench = createFixtureBench; }
  if (typeof module !== 'undefined' && module.exports) module.exports = { createFixtureBench: createFixtureBench };
})(typeof window !== 'undefined' ? window : null);
