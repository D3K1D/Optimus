/* M6a Benchmark (contracts/m6-benchmark.md §2, §5–§8): the Scan menu, the Benchmark panel (start → live charts →
   result), the score card with Copy / Save image, cooling tips, and useBench, the state behind them.
   Production copy of the design's "Scan menu and Benchmark" and "Benchmark additions" (design/…/bundle.js), with the
   contract's names: verdicts and observations come from the host; nothing here judges a number.
   The pure helpers at the top also load in Node (module.exports) for tests. */
(function (root) {
  'use strict';

  /* ---------- pure helpers ---------- */
  var PARTS = [{ id: 'cpu', label: 'CPU', icon: 'cpu' }, { id: 'gpu', label: 'GPU', icon: 'gpu' }, { id: 'ram', label: 'RAM', icon: 'ram' }, { id: 'disk', label: 'Disk', icon: 'disk' }];
  var PART = {}; PARTS.forEach(function (p) { PART[p.id] = p; });
  var VERDICT = {
    no_issue: { label: 'No issue found', icon: 'check', cls: 'is-ok' },
    slower_than_before: { label: 'Slower than before', icon: 'alert', cls: 'is-slow' },
    running_hot: { label: 'Running hot', icon: 'thermo', cls: 'is-hot' },
    unmeasured: { label: 'Not measured', icon: 'gap', cls: 'is-gap' }
  };
  // §6 observations: neutral, very short.
  var OBS = {
    power_limited: 'Power limited', clock_drop_after_boost: 'Boost settles', thermal_slowdown_brief: 'Brief thermal slowdown',
    thermal_slowdown: 'Thermal slowdown', ram_speed_below_module_rating: 'RAM below module rating',
    disk_below_typical_for_type: 'Below typical for this drive', lower_than_before_unconfirmed: 'Lower than last time, not confirmed'
  };
  // Why a part wasn't measured (test / part reasons).
  var REASON = { needs_space: 'Needs 4 GB free', no_gpu: 'No GPU found', unsupported: 'Not supported', low_memory: 'Not enough free memory',
    busy: 'Busy', thermal_stop: 'Stopped to stay cool', check_failed: 'Check failed', gpu_reset: 'GPU reset', too_slow: 'Too slow to measure',
    cancelled: 'Cancelled', skipped: 'Skipped', failed: 'Failed' };
  var FIGURE = { clock: 'Est. clock', temp: 'Peak', power: 'Power', read: 'Read', write: 'Write', copy: 'Copy', latency: 'Latency', random: 'Random 4K', limit: 'Limit' };
  var FIG_ORDER = ['read', 'write', 'copy', 'random', 'latency', 'temp', 'clock', 'power', 'limit'];
  var METRIC = { clock: ['Clock', 'accent'], memClock: ['Mem clock', 'accent'], temp: ['Temp', 'exposure'], slowdownTempC: ['Slowdown', 'exposure'],
    power: ['Power', 'storage'], powerPct: ['Power', 'storage'], powerW: ['Power', 'storage'], limit: ['Limit', 'storage'],
    fan: ['Fan', 'performance'], fanRpm: ['Fan', 'performance'], fanPct: ['Fan', 'performance'],
    read: ['Read', 'privacy'], write: ['Write', 'privacy'], copy: ['Copy', 'privacy'], latency: ['Latency', 'accent'], random: ['Random 4K', 'accent'],
    random4k: ['Random 4K', 'accent'] };
  var TIPS = { bios_fan_mode: ['sliders', 'BIOS fan mode: Standard or Performance'], clean_filters: ['dots', 'Clean the dust filters'],
    check_fans_spin: ['fan', 'Check that every fan spins'], gpu_default_curve: ['gpu', 'Keep the GPU on its default curve'] };
  var FAN_APPS = [{ id: 'any', name: 'FanControl', note: 'Desktops' }, { id: 'asus', name: 'G-Helper', note: 'ASUS laptops' }, { id: 'lenovo', name: 'Legion Toolkit', note: 'Lenovo Legion and LOQ' }];
  var ACTIVE = { starting: 1, running: 1, stopping: 1 };

  function isActive(state) { return !!ACTIVE[state]; }
  function nfmt(v, dp) { return v == null || isNaN(v) ? '—' : Number(v).toLocaleString('en-US', { minimumFractionDigits: dp || 0, maximumFractionDigits: dp || 0 }); }
  function dpFor(unit) { return unit === 'GHz' ? 2 : unit === 'GB/s' ? 1 : 0; }
  function suffix(id) { var s = String(id || ''); return s.slice(s.lastIndexOf('.') + 1); }
  function cap(s) { s = String(s || '').replace(/_/g, ' '); return s.charAt(0).toUpperCase() + s.slice(1); }
  // A figure {value, unit, source} as text, or null when the value is missing.
  function figureText(f) {
    if (!f || f.value == null) return null;
    var u = f.unit || '';
    return nfmt(f.value, dpFor(u)) + (u === '%' ? '%' : u ? ' ' + u : '');
  }
  // Figures (an object keyed by figure id, or an array of {id, …}) as [{id, label, text, source}] in a fixed order.
  function figureList(figures, part) {
    var list = Array.isArray(figures) ? figures.map(function (f) { return [f.id, f]; }) : Object.keys(figures || {}).map(function (k) { return [k, figures[k]]; });
    list.sort(function (a, b) { var x = FIG_ORDER.indexOf(a[0]), y = FIG_ORDER.indexOf(b[0]); return (x < 0 ? 99 : x) - (y < 0 ? 99 : y); });
    // Only the CPU clock is an estimate (§3: PDH performance × nominal); the GPU's comes from NVML.
    return list.map(function (e) { return { id: e[0], label: e[0] === 'clock' && part && part !== 'cpu' ? 'Clock' : FIGURE[e[0]] || cap(e[0]), text: figureText(e[1]), source: (e[1] || {}).source || null }; });
  }
  // An observation is the host's {id, value?, unit?} (or just its id).
  function obsWord(o) { var code = o && typeof o === 'object' ? o.id : o; return OBS[code] || cap(code); }
  function reasonWord(code) { return REASON[code] || (code ? cap(code) : 'Not measured'); }
  function metricInfo(slot) {
    var m = METRIC[suffix(slot.id)] || [cap(suffix(slot.id)), 'accent'];
    return { label: m[0], tone: m[1], dp: dpFor(slot.unit) };
  }
  // The sensors a part couldn't read, as quiet words ("No temp sensor").
  function missingSensors(slots) {
    var out = [];
    (slots || []).forEach(function (s) {
      if (s.available !== false) return;
      var w = 'No ' + metricInfo(s).label.toLowerCase() + ' sensor';
      if (out.indexOf(w) < 0) out.push(w);
    });
    return out;
  }
  // Change since the last comparable run: up / down / same (within 2%).
  function deltaInfo(score, prev, pct) {
    if (score == null) return null;
    var d = typeof pct === 'number' ? pct : prev ? (score - prev) / prev * 100 : null;
    if (d == null || isNaN(d)) return null;
    var k = d >= 2 ? 'up' : d <= -2 ? 'down' : 'same', n = Math.abs(Math.round(d));
    return { kind: k, text: k === 'same' ? 'Same' : (k === 'up' ? '▲ ' : '▼ ') + n + '%',
      name: k === 'same' ? 'About the same as last run' : n + '% ' + (k === 'up' ? 'better' : 'worse') + ' than last run' };
  }
  // A part's delta from comparison.deltas[part] ({prev, pct} or a bare percentage).
  function partDelta(comparison, part, score) {
    var d = comparison && comparison.deltas ? comparison.deltas[part] : null;
    if (d == null) return null;
    return typeof d === 'number' ? deltaInfo(score, null, d) : deltaInfo(score, d.prev, d.pct);
  }
  // The live chart: a fixed window (newest at the right) on a fixed scale; a null sample breaks the line.
  function chartGeom(values, max, n, W, H) {
    n = n || 60; W = W || 120; H = H || 40;
    var vals = (values || []).slice(-n), nums = vals.filter(function (v) { return v != null && !isNaN(v); });
    var top = max || Math.max.apply(null, nums.concat([1])) * 1.15;
    function y(v) { return (H - 1) - Math.max(0, Math.min(1, v / top)) * (H - 2); }
    function x(i) { return n > 1 ? (n - vals.length + i) / (n - 1) * W : W; }
    var line = '', pen = false;
    vals.forEach(function (v, i) {
      if (v == null || isNaN(v)) { pen = false; return; }
      line += (pen ? 'L' : 'M') + x(i).toFixed(2) + ' ' + y(v).toFixed(2); pen = true;
    });
    var area = nums.length > 1 && nums.length === vals.length ? line + 'L' + W + ' ' + H + 'L' + x(0).toFixed(2) + ' ' + H + 'Z' : '';
    var cur = null; for (var i = vals.length - 1; i >= 0; i--) if (vals[i] != null && !isNaN(vals[i])) { cur = vals[i]; break; }
    return { line: line, area: area, cur: cur, peak: nums.length ? Math.max.apply(null, nums) : null, y: y };
  }
  // The progress ring: circumference and dash offset for 0…1.
  function ringDash(progress, r) {
    var c = 2 * Math.PI * r, p = Math.max(0, Math.min(1, progress || 0));
    return { c: Number(c.toFixed(1)), offset: Number((c * (1 - p)).toFixed(1)) };
  }
  function resultParts(result) {
    var by = {}; ((result && result.parts) || []).forEach(function (p) { by[p.part] = p; });
    return PARTS.filter(function (p) { return by[p.id]; }).map(function (p) { return by[p.id]; });
  }
  // Model names only (§5: never user or PC names, paths): from the result, else the capabilities.
  function devicesOf(caps, result) {
    var out = {};
    ((caps && caps.parts) || []).forEach(function (p) { if (p.device) out[p.id] = p.device; });
    ((result && result.parts) || []).forEach(function (p) { if (p.device) out[p.part] = p.device; });
    return out;
  }
  // What Copy puts on the clipboard: three lines (score, part scores, CPU · GPU · RAM).
  function shareText(result, devices) {
    var parts = resultParts(result);
    return 'Optimus benchmark: ' + nfmt(result.score) + (result.partial ? ' (partial)' : '') + '\n' +
      parts.map(function (p) { return PART[p.part].label + ' ' + nfmt(p.score); }).join(' · ') + '\n' +
      ['cpu', 'gpu', 'ram'].map(function (k) { return (devices || {})[k]; }).filter(Boolean).join(' · ');
  }
  function powerBlocked(power) { return !!power && (power.ac === 'offline' || (power.ac === 'unknown' && power.battery === 'present')); }
  // bench.start errors as short inline words.
  function startErrorWord(err) {
    var reason = err && err.data && err.data.reason;
    if (err && err.code === -32004) return 'After the scan';
    if (err && err.code === -32013) return reason === 'on_battery' ? 'Plug in to run' : reason === 'bench_active' ? 'Already running' : 'Busy · try again';
    return 'Not started';
  }
  function pad(n) { return (n < 10 ? '0' : '') + n; }
  var MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  function whenShort(iso, now) {
    var d = new Date(iso), t = now ? new Date(now) : new Date();
    if (isNaN(d)) return '';
    if (d.toDateString() === t.toDateString()) return 'Today ' + pad(d.getHours()) + ':' + pad(d.getMinutes());
    return d.getDate() + ' ' + MONTHS[d.getMonth()] + (d.getFullYear() !== t.getFullYear() ? ' ' + d.getFullYear() : '');
  }
  // One call at a time: while fn's promise is pending, callers share it instead of starting another.
  function singleFlight(fn) {
    var pending = null;
    return function () {
      if (pending) return pending;
      var done = function () { pending = null; };
      pending = Promise.resolve().then(fn);
      pending.then(done, done);
      return pending;
    };
  }
  // Polls fn: the next call starts `every` ms after the previous one settles (never piles up). Returns stop().
  function pollAfter(fn, every, timers) {
    // Wrapped: window.setTimeout called as a method of another object throws "Illegal invocation".
    var t = timers || { set: function (fn, ms) { return setTimeout(fn, ms); }, clear: function (h) { clearTimeout(h); } }, stopped = false, handle = null;
    function next() { if (!stopped) handle = t.set(run, every); }
    function run() { handle = null; if (stopped) return; Promise.resolve().then(fn).then(next, next); }
    next();
    return function () { stopped = true; if (handle != null) t.clear(handle); handle = null; };
  }
  function fileDate(iso) { var d = iso ? new Date(iso) : new Date(); if (isNaN(d)) d = new Date(); return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()); }

  var helpers = { PARTS: PARTS, VERDICT: VERDICT, OBS: OBS, TIPS: TIPS, FAN_APPS: FAN_APPS, isActive: isActive, nfmt: nfmt, figureText: figureText, figureList: figureList,
    obsWord: obsWord, reasonWord: reasonWord, metricInfo: metricInfo, missingSensors: missingSensors, deltaInfo: deltaInfo, partDelta: partDelta,
    chartGeom: chartGeom, ringDash: ringDash, resultParts: resultParts, devicesOf: devicesOf, shareText: shareText, powerBlocked: powerBlocked,
    startErrorWord: startErrorWord, whenShort: whenShort, fileDate: fileDate, singleFlight: singleFlight, pollAfter: pollAfter };
  if (typeof module !== 'undefined' && module.exports) module.exports = helpers;
  if (!root || !root.React || !root.OptTool || !root.OptTool.Icon) return;

  /* ---------- components ---------- */
  var React = root.React, h = React.createElement, OT = root.OptTool;
  var Icon = OT.Icon, Button = OT.Button, GlassPanel = OT.GlassPanel, Logo = OT.Logo, Popover = OT.Popover, cx = OT.ui.cx;

  // Scan's menu: Full scan or Benchmark, each with a one-line hint. While a benchmark runs, Full scan waits.
  function ScanMenu(p) {
    var busy = !!p.benchRunning;
    var rows = [
      { id: 'full', icon: 'scan', title: 'Full scan', hint: busy ? 'After the benchmark' : 'Everything, compared with last time', disabled: busy },
      { id: 'bench', icon: 'gauge', title: 'Benchmark', hint: busy ? 'Running' : 'CPU, GPU, RAM, disk · about 2 min' }
    ];
    return h(Popover, { id: p.id, label: 'Scan', toggleId: p.toggleId, onClose: p.onClose, className: 'ot-scanmenu' },
      h('ul', { className: 'ot-menu-list' }, rows.map(function (r) {
        return h('li', { key: r.id }, h('button', { type: 'button', 'data-menu-item': true, className: 'ot-menu-item ot-menu-item-2', disabled: r.disabled,
            onClick: function () { p.onPick(r.id); } },
          h(Icon, { name: r.icon, size: 20, className: 'ot-menu-icon' }),
          h('span', { className: 'ot-menu-text' }, h('span', { className: 'ot-menu-title' }, r.title), h('span', { className: 'text-mono-sm ot-muted' }, r.hint))));
      })));
  }

  // One live line chart on the slot's fixed scale; an unreadable slot keeps the footprint with a dashed well.
  function LiveChart(p) {
    var slot = p.slot, info = metricInfo(slot), off = slot.available === false, W = 120, H = 40;
    var g = chartGeom(p.values, slot.max, p.window || 60, W, H);
    var name = info.label + (off ? ', not available' : g.cur == null ? '' : ', ' + nfmt(g.cur, info.dp) + ' ' + slot.unit + ', peak ' + nfmt(g.peak, info.dp));
    return h('figure', { className: cx('ot-chart', 'tone-' + info.tone, off && 'is-off'), role: 'img', 'aria-label': name },
      h('div', { className: 'ot-chart-head' },
        h('span', { className: 'text-caption ot-chart-label' }, info.label),
        h('span', { className: 'ot-chart-value' }, off ? '—' : g.cur == null ? '…' : nfmt(g.cur, info.dp), !off && g.cur != null && h('span', { className: 'ot-chart-unit' }, slot.unit))),
      off ? h('div', { className: 'ot-chart-off' }, h(Icon, { name: 'gap', size: 16 }), h('span', { className: 'text-mono-sm' }, 'Not available'))
        : h('svg', { className: 'ot-chart-plot', viewBox: '0 0 ' + W + ' ' + H, preserveAspectRatio: 'none', 'aria-hidden': true },
            slot.expected != null && h('line', { className: 'ot-chart-ref', x1: 0, x2: W, y1: g.y(slot.expected), y2: g.y(slot.expected), vectorEffect: 'non-scaling-stroke' }),
            slot.limit != null && h('line', { className: 'ot-chart-limit', x1: 0, x2: W, y1: g.y(slot.limit), y2: g.y(slot.limit), vectorEffect: 'non-scaling-stroke' }),
            g.area && h('path', { className: 'ot-chart-area', d: g.area }),
            g.line && h('path', { className: 'ot-chart-line', d: g.line, vectorEffect: 'non-scaling-stroke' })),
      h('div', { className: 'ot-chart-foot text-mono-sm' },
        h('span', null, off || g.peak == null ? ' ' : 'Peak ' + nfmt(g.peak, info.dp)),
        !off && (slot.limit != null ? h('span', { className: 'ot-chart-key is-limit' }, 'Limit ' + nfmt(slot.limit, info.dp))
          : slot.expected != null ? h('span', { className: 'ot-chart-key' }, 'Expected ' + nfmt(slot.expected, info.dp)) : null)));
  }

  // The current test: a ring that fills as it runs; inside, the live score when the host sends one, else the percent.
  function BenchGauge(p) {
    var r = 52, d = ringDash(p.progress, r), pct = Math.round(Math.max(0, Math.min(1, p.progress || 0)) * 100);
    return h('div', { className: 'ot-gauge' },
      h('div', { className: 'ot-gauge-ring', role: 'progressbar', 'aria-label': p.part.label, 'aria-valuemin': 0, 'aria-valuemax': 100, 'aria-valuenow': pct },
        h('svg', { viewBox: '0 0 120 120', 'aria-hidden': true },
          h('circle', { className: 'ot-gauge-track', cx: 60, cy: 60, r: r }),
          h('circle', { className: 'ot-gauge-fill', cx: 60, cy: 60, r: r, strokeDasharray: d.c, strokeDashoffset: d.offset, transform: 'rotate(-90 60 60)' })),
        h('div', { className: 'ot-gauge-mid' },
          h('span', { className: 'ot-gauge-score' }, p.score != null ? nfmt(p.score) : pct),
          h('span', { className: 'text-caption ot-muted' }, p.score != null ? 'pts' : '%'))),
      h('div', { className: 'ot-gauge-text' },
        h('span', { className: 'ot-gauge-part' }, h(Icon, { name: p.part.icon, size: 22 }), h('span', { className: 'text-title-2' }, p.part.label)),
        p.device && h('span', { className: 'text-mono-sm ot-muted ot-wrap' }, p.device),
        p.stopping && h('span', { className: 'text-mono-sm ot-muted' }, 'Stopping…')));
  }

  // The tests in run order: done (check), running (turning gears + fill), skipped, queued.
  function BenchSteps(p) {
    return h('ol', { className: 'ot-bsteps', 'aria-label': 'Tests' }, (p.tests || []).map(function (t) {
      var part = PART[t.part] || { label: cap(t.part) }, st = t.state || 'queued';
      var gone = st === 'skipped' || st === 'failed' || st === 'cancelled';
      return h('li', { key: t.part, className: cx('ot-bstep', 'is-' + (gone ? 'skipped' : st)), 'aria-current': st === 'running' ? 'step' : undefined },
        h('span', { className: 'ot-bstep-bar', 'aria-hidden': true }, h('span', { style: { width: (st === 'done' || gone ? 100 : Math.round((t.progress || 0) * 100)) + '%' } })),
        h('span', { className: 'ot-bstep-label text-callout' },
          st === 'done' ? h(Icon, { name: 'check', size: 14, strokeWidth: 2.5 }) : st === 'running' ? h(Logo, { size: 14, spin: true, decorative: true, className: 'ot-bstep-logo' })
            : gone ? h(Icon, { name: 'gap', size: 14 }) : null,
          part.label));
    }));
  }

  function Verdict(p) {
    var v = VERDICT[p.kind] || VERDICT.unmeasured;
    return h('span', { className: cx('ot-verdict', v.cls) }, h(Icon, { name: v.icon, size: 14, strokeWidth: 2 }), v.label);
  }
  function Delta(p) {
    var d = p.info; if (!d) return null;
    return h('span', { className: cx('ot-delta', 'is-' + d.kind), title: d.name, 'aria-label': d.name }, d.text);
  }

  // One result row: part, score and change, verdict, figures, observations, missing sensors, Investigate with AI.
  function BenchRow(p) {
    var r = p.row, part = PART[r.part], v = VERDICT[r.verdict] || VERDICT.unmeasured, figs = figureList(r.figures, r.part);
    var ask = p.onInvestigate && (r.verdict === 'slower_than_before' || r.verdict === 'running_hot');
    var quiet = (r.observations || []).map(obsWord).concat(r.score == null ? [] : p.missing || []);
    return h('li', { className: cx('ot-brow', v.cls) },
      h('span', { className: 'ot-brow-icon', 'aria-hidden': true }, h(Icon, { name: part.icon, size: 22 })),
      h('div', { className: 'ot-brow-name' },
        h('span', { className: 'text-headline' }, part.label),
        p.device && h('span', { className: 'text-mono-sm ot-muted ot-wrap' }, p.device)),
      h('div', { className: 'ot-brow-score' },
        h('span', { className: 'ot-brow-num' }, nfmt(r.score), r.score != null && h('span', { className: 'text-caption ot-muted' }, ' pts')),
        h(Delta, { info: p.delta })),
      h('div', { className: 'ot-brow-verdict' }, h(Verdict, { kind: r.verdict })),
      (r.score == null || figs.length > 0) && h('div', { className: 'ot-brow-stats text-mono-sm' },
        r.score == null ? h('span', { className: 'ot-muted' }, reasonWord(r.reason)) : figs.map(function (s) {
          return h('span', { key: s.id, className: cx('ot-stat', s.text == null && 'is-off'), title: s.text == null ? 'Not available' : null },
            h('span', { className: 'ot-muted' }, s.label + ' '), h('span', { className: 'ot-stat-v' }, s.text == null ? '—' : s.text));
        })),
      quiet.length > 0 && h('ul', { className: 'ot-brow-obs text-mono-sm', 'aria-label': 'Notes' }, quiet.map(function (w) { return h('li', { key: w }, w); })),
      ask && h('div', { className: 'ot-brow-act' }, h(Button, { variant: 'neu', size: 'sm', icon: 'spark', onClick: function () { p.onInvestigate(r.part, r.verdict); } }, 'Investigate with AI')));
  }

  // A 1200×630 PNG of the score card, drawn on a canvas (no DOM capture, no library). Model names, date, version only.
  function shareImage(result, devices, theme) {
    var dark = theme !== 'light', c = document.createElement('canvas'); c.width = 1200; c.height = 630;
    var x = c.getContext('2d'), ink = dark ? '#eef1f6' : '#161a22', mut = dark ? '#a2aab8' : '#545c6b';
    x.fillStyle = dark ? '#14161b' : '#e9ecf1'; x.fillRect(0, 0, 1200, 630);
    x.strokeStyle = dark ? 'rgba(200,212,235,0.06)' : 'rgba(46,58,82,0.1)'; x.lineWidth = 1;
    for (var g = 0; g <= 1200; g += 40) { x.beginPath(); x.moveTo(g + 0.5, 0); x.lineTo(g + 0.5, 630); x.stroke(); }
    for (g = 0; g <= 630; g += 40) { x.beginPath(); x.moveTo(0, g + 0.5); x.lineTo(1200, g + 0.5); x.stroke(); }
    var font = function (w, s, mono) { x.font = w + ' ' + s + 'px ' + (mono ? '"Geist Mono", Consolas, monospace' : 'Geist, "Segoe UI", sans-serif'); };
    x.fillStyle = mut; font(600, 28); x.fillText('Optimus benchmark', 152, 108);
    var big = nfmt(result.score);
    x.fillStyle = ink; font(800, 150); x.fillText(big, 72, 300);
    var bw = x.measureText(big).width;
    font(500, 34); x.fillStyle = mut; x.fillText(result.partial ? 'pts (partial)' : 'pts', 72 + bw + 16, 300);
    var parts = resultParts(result), w = 1056 / Math.max(1, parts.length);
    parts.forEach(function (row, i) {
      var left = 72 + i * w;
      x.fillStyle = mut; font(600, 26); x.fillText(PART[row.part].label, left, 440);
      x.fillStyle = ink; font(700, 48); x.fillText(nfmt(row.score), left, 496);
    });
    x.fillStyle = mut; font(400, 24, true);
    x.fillText(['cpu', 'gpu', 'ram'].map(function (k) { return (devices || {})[k]; }).filter(Boolean).join('  ·  '), 72, 560);
    x.fillText(fileDate(result.finishedAt) + '  ·  v' + (result.benchVersion || '1'), 72, 598);
    return new Promise(function (done) {
      var img = new Image();
      img.onload = function () { try { x.drawImage(img, 72, 62, 64, 64); } catch (e) { /* drawn without the logo */ } done(c.toDataURL('image/png')); };
      img.onerror = function () { done(c.toDataURL('image/png')); };
      img.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(String(OT.ui.LOGO_SVG || '').split('__ID__').join('s'));
    });
  }

  // The overall score: the big number, its change and the previous run (comparable runs only), the part scores,
  // Copy (text) and Save image (PNG). A partial run shows Partial and never a comparison.
  function ScoreCard(p) {
    var r = p.result, cmp = r.partial ? null : r.comparison, parts = resultParts(r);
    var cs = React.useState(null), done = cs[0], setDone = cs[1];
    React.useEffect(function () { if (!done) return; var t = setTimeout(function () { setDone(null); }, 1800); return function () { clearTimeout(t); }; }, [done]);
    function copy() {
      var t = shareText(r, p.devices), ok = function () { setDone('Copied'); };
      if (navigator.clipboard && navigator.clipboard.writeText) navigator.clipboard.writeText(t).then(ok, function () { setDone('Copy failed'); }); else setDone('Copy failed');
    }
    function save() {
      var theme = document.documentElement.getAttribute('data-theme') || 'dark';
      shareImage(r, p.devices, theme).then(function (url) { return p.onSave(url); }).then(function (saved) { if (saved !== false) setDone('Saved'); }, function () { setDone('Not saved'); });
    }
    return h('section', { className: 'ot-score', 'aria-label': 'Overall score' },
      h('div', { className: 'ot-score-main' },
        h('div', { className: 'ot-score-now' },
          h('span', { className: 'text-caption ot-muted ot-score-cap' }, 'Overall'),
          h('span', { className: 'ot-score-big' }, nfmt(r.score)),
          (r.partial || cmp) && h('span', { className: 'ot-score-sub' }, r.partial ? h('span', { className: 'ot-verdict is-gap' }, 'Partial') : h(Delta, { info: deltaInfo(r.score, cmp.prevScore) }))),
        cmp && cmp.prevScore != null && h('div', { className: 'ot-score-prev' },
          h('span', { className: 'text-caption ot-muted ot-score-cap' }, cmp.prevFinishedAt ? whenShort(cmp.prevFinishedAt) : 'Last run'),
          h('span', { className: 'ot-score-mid' }, nfmt(cmp.prevScore)))),
      h('ul', { className: 'ot-score-parts' }, parts.map(function (row) {
        return h('li', { key: row.part }, h('span', { className: 'text-caption ot-muted' }, PART[row.part].label), h('span', { className: 'ot-score-part' }, nfmt(row.score)));
      })),
      h('div', { className: 'ot-score-foot' },
        h('span', { className: 'text-mono-sm ot-muted ot-wrap' }, ['cpu', 'gpu', 'ram'].map(function (k) { return (p.devices || {})[k]; }).filter(Boolean).join(' · ')),
        h('span', { className: 'ot-score-acts' },
          done && h('span', { className: 'text-mono-sm ot-score-done', role: 'status' }, done),
          h(Button, { variant: 'ghost', size: 'sm', icon: 'copy', onClick: copy }, 'Copy'),
          p.onSave && h(Button, { variant: 'neu', size: 'sm', icon: 'download', onClick: save }, 'Save image'))));
  }

  // §7 Cooling tips (design v37): the host's guidance codes as short lines, and the fan apps (names only), this PC's marked.
  function CoolingTips(p) {
    var g = p.guidance || {}, tips = (g.tips || []).filter(function (t) { return TIPS[t]; }).map(function (t) { return TIPS[t]; });
    if (g.pump) tips = tips.concat([['sliders', 'Use your cooler maker’s pump setting']]);
    return h('section', { className: 'ot-cool', 'aria-label': 'Cooling tips' },
      h('span', { className: 'text-headline ot-fan-title' }, h(Icon, { name: 'thermo', size: 18, className: 'ot-cool-glyph' }), 'Cooling tips'),
      tips.length > 0 && h('ul', { className: 'ot-cool-list' }, tips.map(function (t) {
        return h('li', { key: t[1], className: 'text-callout' }, h(Icon, { name: t[0], size: 16, className: 'ot-cool-icon' }), h('span', null, t[1]));
      })),
      h('div', { className: 'ot-cool-apps' },
        h('span', { className: 'text-caption ot-muted ot-cool-cap' }, 'Fan app'),
        h('ul', { className: 'ot-cool-applist' }, FAN_APPS.map(function (a) {
          var mine = g.brand === a.id;
          return h('li', { key: a.id, className: cx('ot-cool-app', mine && 'is-mine') }, h('span', { className: 'ot-cool-app-n' }, a.name), h('span', { className: 'text-mono-sm ot-muted' }, a.note),
            mine && h('span', { className: 'ot-fan-tag text-caption' }, 'This PC'));
        }))));
  }

  function plugStrip() { return h('div', { key: 'plug', className: 'ot-bench-warn', role: 'status' }, h(Icon, { name: 'plug', size: 18 }), h('span', { className: 'text-callout' }, 'Plug in to run')); }

  /* The Benchmark panel. view: start | running | result. */
  function BenchPanel(p) {
    var st = p.view, run = p.run || {}, caps = p.caps || {}, res = st === 'result' ? run.result : null, blocked = powerBlocked(caps.power);
    var devices = devicesOf(caps, res);
    var head = h('div', { className: 'ot-answer-head' },
      h('div', { className: 'ot-bench-titles' },
        h('h3', { className: 'text-headline ot-answer-title' }, h(Icon, { name: 'gauge', size: 18, className: 'ot-bench-glyph' }), 'Benchmark'),
        res && h('span', { className: 'text-mono-sm ot-muted ot-bench-when' }, whenShort(run.finishedAt) +
          (!res.partial && res.comparison && res.comparison.prevFinishedAt ? ' · vs ' + whenShort(res.comparison.prevFinishedAt) : !res.partial && res.differentSetup ? ' · Different setup' : ''))),
      p.onClose && h(Button, { variant: 'ghost', size: 'sm', icon: 'close', className: 'ot-answer-x', 'aria-label': 'Close', title: 'Close', onClick: p.onClose }));
    var warn = p.warn && p.warn !== 'Plug in to run' && h('div', { key: 'err', className: 'ot-bench-err text-callout', role: 'alert' }, p.warn);
    var body;
    if (st === 'running') {
      var tests = run.tests || [], live = run.live || null;
      var curTest = tests.filter(function (t) { return t.state === 'running'; })[0] || (live && tests.filter(function (t) { return t.part === live.part; })[0]) ||
        tests.filter(function (t) { return t.state === 'queued'; })[0] || tests[tests.length - 1] || { part: 'cpu' };
      var cur = PART[curTest.part] || PARTS[0], slots = (run.metrics || {})[cur.id] || [];
      var samples = live && live.part === cur.id ? live.samples || {} : {};
      body = [
        h(BenchSteps, { key: 'steps', tests: tests }),
        h(BenchGauge, { key: 'gauge', part: cur, device: devices[cur.id], progress: curTest.progress,
          score: live && live.part === cur.id && live.score != null ? live.score : curTest.liveScore != null ? curTest.liveScore : null, stopping: run.state === 'stopping' }),
        slots.length > 0 && h('div', { key: 'charts', className: 'ot-charts' }, slots.map(function (s) { return h(LiveChart, { key: s.id, slot: s, values: samples[s.id] }); })),
        h('div', { key: 'foot', className: 'ot-choice-foot' }, h(Button, { variant: 'neu', icon: 'stop', disabled: run.state === 'stopping' || p.cancelling, onClick: p.onCancel }, 'Cancel'))
      ];
    } else if (st === 'result' && res) {
      var rows = resultParts(res), cmp = res.partial ? null : res.comparison;
      body = [
        h(ScoreCard, { key: 'score', result: Object.assign({ finishedAt: run.finishedAt, benchVersion: run.benchVersion }, res), devices: devices, onSave: p.onSaveImage }),
        blocked && plugStrip(),
        h('ul', { key: 'rows', className: 'ot-brows' }, rows.map(function (r) {
          return h(BenchRow, { key: r.part, row: r, device: devices[r.part], delta: partDelta(cmp, r.part, r.score), missing: missingSensors((run.metrics || {})[r.part]),
            onInvestigate: p.onInvestigate });
        })),
        res.fanGuidance && h(CoolingTips, { key: 'cool', guidance: res.fanGuidance }),
        warn,
        h('div', { key: 'foot', className: 'ot-choice-foot' }, h(Button, { variant: 'neu', icon: 'repeat', disabled: blocked || p.starting, onClick: p.onStart }, 'Run again'))
      ];
    } else {
      var testable = (caps.parts || PARTS.map(function (x) { return { id: x.id }; })).slice().sort(function (a, b) {
        var x = PARTS.indexOf(PART[a.id]), y = PARTS.indexOf(PART[b.id]); return (x < 0 ? 9 : x) - (y < 0 ? 9 : y);
      });
      body = [
        h('ul', { key: 'parts', className: 'ot-bparts' }, testable.map(function (part) {
          var meta = PART[part.id] || { label: cap(part.id), icon: 'gap' }, off = part.testable === false;
          return h('li', { key: part.id, className: cx('ot-bpart', off && 'is-off') }, h(Icon, { name: meta.icon, size: 22 }),
            h('span', { className: 'ot-bpart-text' }, h('span', { className: 'text-headline' }, meta.label),
              h('span', { className: 'text-mono-sm ot-muted ot-wrap' }, off ? reasonWord(part.reason) : part.device || '')));
        })),
        h('div', { key: 'meta', className: 'ot-bench-meta text-mono-sm ot-muted' },
          h('span', null, h(Icon, { name: 'clock', size: 16 }), 'About ' + Math.max(1, Math.round((caps.estSeconds || 120) / 60)) + ' min'),
          h('span', null, h(Icon, { name: 'fan', size: 16 }), 'Fans may get loud')),
        blocked && plugStrip(),
        warn,
        p.toast && h('div', { key: 'toast', className: 'ot-toast text-mono-sm ot-fade', role: 'status' }, p.toast),
        h('div', { key: 'foot', className: 'ot-choice-foot' }, h(Button, { variant: 'primary', icon: 'play', disabled: blocked || p.starting || !p.caps, onClick: p.onStart }, 'Start'))
      ];
    }
    return h(GlassPanel, { strong: true, className: cx('ot-bench', 'is-' + st, p.className), role: 'region', 'aria-label': 'Benchmark', 'aria-busy': st === 'running' || undefined, 'data-bench': true, tabIndex: -1 }, head, body);
  }

  /* ---------- state: useBench ---------- */
  function uuid() {
    if (root.crypto && root.crypto.randomUUID) return root.crypto.randomUUID();
    return 'k-' + Date.now().toString(16) + '-' + Math.random().toString(16).slice(2);
  }
  /** opts: { flash(word) }. Returns the panel's state and actions; onEvent takes bench:* events from the app. */
  function useBench(backend, opts) {
    var o = React.useState(false), open = o[0], setOpen = o[1];
    var v = React.useState('start'), view = v[0], setView = v[1];
    var c = React.useState(null), caps = c[0], setCaps = c[1];
    var r = React.useState(null), run = r[0], setRunState = r[1];
    var w = React.useState(null), warn = w[0], setWarn = w[1];
    var t = React.useState(null), toast = t[0], setToast = t[1];
    var l = React.useState([]), list = l[0], setList = l[1];
    var s = React.useState(false), starting = s[0], setStarting = s[1];
    var k = React.useState(false), cancelling = k[0], setCancelling = k[1];
    var runRef = React.useRef(null), openRef = React.useRef(false), cancelAsked = React.useRef({}), mounted = React.useRef(true);
    openRef.current = open;
    React.useEffect(function () { return function () { mounted.current = false; }; }, []);
    React.useEffect(function () { if (!toast) return; var x = setTimeout(function () { setToast(null); }, 2400); return function () { clearTimeout(x); }; }, [toast]);

    // At most one bench.capabilities on its way; every caller shares it.
    var capsCall = React.useRef(null);
    if (!capsCall.current) capsCall.current = singleFlight(function () {
      return backend.call('bench.capabilities').then(function (cp) { if (mounted.current) setCaps(cp); return cp; }, function () { return null; });
    });
    function loadCaps() { return capsCall.current(); }
    function loadList() { return backend.call('bench.list', { limit: 20 }).then(function (x) { if (mounted.current) setList(x.runs || []); }, function () {}); }
    // Show a run: the live view while it runs, its result when done; a stopped run returns to the start state.
    function track(x, fromHistory) {
      var prev = runRef.current;
      runRef.current = x; setRunState(x);
      if (isActive(x.state)) { setView('running'); return; }
      // The same ended state again (an event and a poll both re-read it): nothing new to say.
      if (!fromHistory && prev && prev.runId === x.runId && prev.state === x.state) return;
      setCancelling(false);
      if (x.state === 'done') { setView('result'); if (!fromHistory && prev && isActive(prev.state)) loadList(); return; }
      if (fromHistory) return;
      // Ended without a result: back to Start (a partial run is not shown).
      setView('start');
      if (x.state === 'cancelled' && cancelAsked.current[x.runId]) { delete cancelAsked.current[x.runId]; setToast('Cancelled'); }
      else if (x.state === 'stopped_on_battery') setToast('Stopped · on battery');
      else if (x.state === 'failed' || x.state === 'interrupted' || x.state === 'cancelled') setWarn('Stopped');
      loadCaps();
    }
    function refresh() {
      var cur = runRef.current; if (!cur) return Promise.resolve();
      return backend.call('bench.get', { runId: cur.runId }).then(function (x) { if (mounted.current && runRef.current && runRef.current.runId === x.runId) track(x); }, function () {});
    }
    // First look: a run already going (another window, or before a reload) and History's rows.
    React.useEffect(function () {
      loadList();
      loadCaps().then(function (cp) {
        if (cp && cp.activeRunId) backend.call('bench.get', { runId: cp.activeRunId }).then(function (x) { if (mounted.current) track(x); }, function () {});
      });
    }, []);
    var active = !!(run && isActive(run.state));
    // §2 live view: bench.get once a second while the panel is open and a run is active.
    React.useEffect(function () {
      if (!open || !active) return;
      return pollAfter(refresh, 1000);   // the next read 1 s after the last one settles
    }, [open, active, run && run.runId]);
    // While the panel is open and idle, re-read the power state (plugging in unblocks Start): 3 s after the last
    // read finished, never while one is still on its way; nothing while the panel is closed.
    React.useEffect(function () {
      if (!open || active) return;
      return pollAfter(loadCaps, 3000);
    }, [open, active]);

    function start() {
      if (starting) return;
      setStarting(true); setWarn(null); setToast(null);
      backend.call('bench.start', { idempotencyKey: uuid() }).then(function (x) {
        setStarting(false); setOpen(true); track(x);
      }, function (err) {
        setStarting(false);
        setWarn(startErrorWord(err));
        var d = err && err.data;
        if (d && d.reason === 'bench_active' && d.runId) backend.call('bench.get', { runId: d.runId }).then(track, function () {});
        loadCaps();
      });
    }
    function cancel() {
      var cur = runRef.current; if (!cur || !isActive(cur.state) || cancelling) return;
      setCancelling(true); cancelAsked.current[cur.runId] = true;
      backend.call('bench.cancel', { idempotencyKey: uuid(), runId: cur.runId }).then(function (x) { track(x); refresh(); }, function () {
        setCancelling(false); delete cancelAsked.current[cur.runId]; if (opts && opts.flash) opts.flash('Not stopped');
      });
    }
    // Scan → Benchmark (or Run benchmark under an answer): the live view while a run is active, else the start state.
    function openPanel() {
      setOpen(true); setWarn(null);
      if (runRef.current && isActive(runRef.current.state)) { setView('running'); refresh(); } else setView('start');
      loadCaps();
    }
    function openRun(runId) {
      backend.call('bench.get', { runId: runId }).then(function (x) {
        if (runRef.current && isActive(runRef.current.state)) { setOpen(true); setView('running'); if (opts && opts.flash) opts.flash('Running'); return; }
        setOpen(true); setWarn(null); track(x, true); loadCaps();
      }, function () { if (opts && opts.flash) opts.flash('Not available'); });
    }
    function onEvent(ev) {
      var cur = runRef.current;
      if (cur && ev.stream === 'bench:' + cur.runId) refresh();
      else if (ev.type === 'bench.progress' || ev.type === 'bench.finished') {
        // A run this window doesn't show yet (started elsewhere): pick it up.
        loadCaps().then(function (cp) { if (cp && cp.activeRunId && (!runRef.current || !isActive(runRef.current.state))) backend.call('bench.get', { runId: cp.activeRunId }).then(track, function () {}); });
        if (ev.type === 'bench.finished') loadList();
      }
    }
    // events.read stale (or a reconnect): re-read what is on screen.
    function resync() { refresh(); loadCaps(); loadList(); }
    var shown = open && view === 'result' && run ? run.runId : null;
    var history = list.map(function (x) { return { id: 'bench:' + x.runId, key: 'bench:' + x.runId, title: 'Benchmark', at: x.finishedAt, keep: true, bench: x.runId, open: shown === x.runId, running: false }; });
    return { open: open, view: view, caps: caps, run: run, warn: warn, toast: toast, starting: starting, cancelling: cancelling, active: active, history: history,
      start: start, cancel: cancel, openPanel: openPanel, openRun: openRun, close: function () { setOpen(false); setWarn(null); }, onEvent: onEvent, resync: resync };
  }

  Object.assign(OT, { ScanMenu: ScanMenu, LiveChart: LiveChart, BenchPanel: BenchPanel, ScoreCard: ScoreCard, CoolingTips: CoolingTips, useBench: useBench,
    benchShareImage: shareImage, bench: helpers });
})(typeof window !== 'undefined' ? window : null);
