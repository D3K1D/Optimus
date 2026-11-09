/* Fixture adapter: an in-browser simulation of the backend (protocol v1 §6 methods, §5 events and the §9 v1.1
   additions) driven by fixtures/m1-simulated-audit.json, the same synthetic dataset the host's fixture collectors
   read, plus fixtures/m2-extras.json (UI-only: a skippable sampler, informational evidence, settings links, a
   simulated change on rescan). State persists to localStorage, so a reload behaves like restarting the app (a
   running scan or inventory is interrupted; its committed evidence stays).

   Deterministic IDs, same scheme as the backend: H(parts) = first 16 lowercase hex of SHA-256 over the UTF-8 string
   of the parts joined with "\u0000" (the ‖ separator).
     evidence  "ev-"     + H(scanId, collectorId, subject)
     finding   "fnd-"    + H(scanId, ruleId, fingerprint),  fingerprint = H(ruleId, subject of its first evidence)
     card      "card-"   + H(reportId, findingId)
     report    "report-" + H(scanId)
     gap       "gap-"    + H(scanId, collectorId)
     scan "scan-" / inventory "inv-" + 16 random hex.

   Fields and calls beyond the contract (readers ignore unknown fields; the UI treats them as optional):
     inventory.fact payload.group   the inventory row ("Specs", "Graphics", …)
     Scan.waveWords                 display labels for the sea during the scan
     Gap.source                     source label for the report row
     Followup.card.receipt / .done, decisions[].done   simulated-approval texts
     Followup.card.settingsUri, decisions[].findingId  (the finding's settingsUri; the decided card's finding)
     followup.respond option "simulate.approve" | "simulate.undo"   fixture-only simulated changes
     fixture.summary {reportId}     the mock AI summary (mockAiSummary), only lines whose evidence is in the report */
(function (root) {
  'use strict';

  var STORE_KEY = 'ot.fixture.v1';
  var SETTINGS_CATALOG = ['windowsdefender://threat', 'windowsdefender://network', 'ms-settings:windowsdefender', 'ms-settings:startupapps',
    'ms-settings:powersleep', 'ms-settings:storagesense', 'ms-settings:appsfeatures', 'ms-settings:privacy-feedback',
    'ms-settings:display-advanced', 'ms-settings:personalization-start', 'ms-settings:windowsupdate-history', 'ms-settings:troubleshoot'];
  var YEAR_MS = 366 * 24 * 3600 * 1000;
  // v1.4: rules whose cards offer "Intended" (added by the engine).
  var INTENDED_RULES = ['net.wildcard_listener_public', 'fw.public_off', 'fw.profile_off', 'startup.many_entries', 'uac.disabled', 'secureboot.off',
    'display.below_max_refresh', 'start.suggestions_on', 'privacy.optional_diag', 'power.high_perf_on_battery'];

  function rpcError(code, message, data) {
    var e = new Error(message); e.code = code; e.data = data === undefined ? null : data; return e;
  }
  function clone(x) { return x == null ? x : JSON.parse(JSON.stringify(x)); }
  function canonical(x) {
    if (Array.isArray(x)) return '[' + x.map(canonical).join(',') + ']';
    if (x && typeof x === 'object') return '{' + Object.keys(x).sort().map(function (k) { return JSON.stringify(k) + ':' + canonical(x[k]); }).join(',') + '}';
    return JSON.stringify(x);
  }
  function randomHex(n) {
    var b = new Uint8Array(n / 2); root.crypto.getRandomValues(b);
    return Array.prototype.map.call(b, function (v) { return ('0' + v.toString(16)).slice(-2); }).join('');
  }
  function H() {
    var s = Array.prototype.slice.call(arguments).join('\u0000');
    return root.crypto.subtle.digest('SHA-256', new TextEncoder().encode(s)).then(function (buf) {
      return Array.prototype.map.call(new Uint8Array(buf), function (v) { return ('0' + v.toString(16)).slice(-2); }).join('').slice(0, 16);
    });
  }
  function hasWord(list, w) { return list.join(' ').toLowerCase().indexOf(w) >= 0; }
  function fetchJson(url) {
    return root.fetch(url, { cache: 'no-store' }).then(function (r) { if (!r.ok) throw new Error(url + ' ' + r.status); return r.json(); });
  }

  // The M1 fixture plus the UI-only M2 extras, as one dataset.
  function merge(fx, x) {
    var F = clone(fx);
    x = x || {};
    F.collectors = F.collectors.concat(clone(x.collectors || []));
    F.rules = F.rules.concat(clone(x.rules || []));
    F.rules.forEach(function (r) { r.settingsUri = (x.settingsUri || {})[r.card.key] || null; });
    F.sensitivity = x.sensitivity || {};
    F.rescan = x.rescan || { skipEvidence: [] };
    return F;
  }

  /**
   * @param {{fixture?:object, fixtureUrl?:string, extras?:object, extrasUrl?:string, storage?:Storage, timeScale?:number}} opts
   * @returns {import('./backend.js').Backend}
   */
  function createFixtureAdapter(opts) {
    opts = opts || {};
    var storage = opts.storage || null;
    var scale = opts.timeScale == null ? 1 : opts.timeScale;
    var F = null, S = null;
    var eventFns = [], stateFns = [], timers = {};   // timers: job id → [timeout]
    var samplers = {};                              // scan id → the running skippable collector
    var bench = null;                               // M6a benchmark simulation (set below)
    // The shell's backend state as the page sees it (protocol-v1 §7). Always ready unless a dev hook sets another.
    var BACKEND_STATES = ['connecting', 'ready', 'reconnecting', 'incompatible'];
    var backendState = 'ready', backendDetail = null, recoverOnRetry = false;
    function setBackendState(next, detail) {
      var prev = backendState;
      if (prev === next && JSON.stringify(backendDetail) === JSON.stringify(detail)) return;
      backendState = next; backendDetail = detail;
      var info = { previous: prev, detail: detail, resumed: prev === 'reconnecting' && next === 'ready' };
      stateFns.slice().forEach(function (fn) { try { fn(next, info); } catch (e) { setTimeout(function () { throw e; }); } });
    }

    function fresh() {
      return { v: 1, onboarding: { revision: 1, step: 'provider',
        profile: { provider: null, uses: [], customUse: '', goals: [], customGoal: '', inventoryConsent: 'unset' },
        resume: { inventoryId: null, scanId: null, reportId: null } },
        inventories: {}, scans: {}, reports: {}, followups: {}, idem: {}, streams: {}, reportOrder: [],
        dispositions: { fingerprint: {}, rule: {} } };
    }
    function load() {
      var raw = null;
      try { raw = storage ? storage.getItem(STORE_KEY) : null; } catch (e) { raw = null; }
      try { var s = raw ? JSON.parse(raw) : null; if (s && s.v === 1) { s.dispositions = s.dispositions || { fingerprint: {}, rule: {} }; return s; } } catch (e) { /* corrupt: start over */ }
      return fresh();
    }
    function save() { try { if (storage) storage.setItem(STORE_KEY, JSON.stringify(S)); } catch (e) { /* storage unavailable */ } }

    var ready = Promise.all([
      opts.fixture ? Promise.resolve(opts.fixture) : fetchJson(opts.fixtureUrl || 'fixtures/m1-simulated-audit.json'),
      opts.extras !== undefined ? Promise.resolve(opts.extras) : fetchJson(opts.extrasUrl || 'fixtures/m2-extras.json')
    ]).then(function (res) {
      F = merge(res[0], res[1]); S = load();
      // A reload is a restart: work that was running is interrupted and keeps what it had committed.
      var jobs = [];
      Object.keys(S.inventories).forEach(function (id) { var inv = S.inventories[id]; if (inv.status === 'running') inv.status = 'cancelled'; });
      Object.keys(S.scans).forEach(function (id) { var sc = S.scans[id]; if (sc.status === 'running' || sc.status === 'queued') jobs.push(endScan(sc, 'interrupted', false)); });
      if (agent) agent.__restart();
      if (bench) bench.__restart();
      return Promise.all(jobs).then(save);
    });

    function emit(stream, type, payload) {
      var seq = (S.streams[stream] || 0) + 1; S.streams[stream] = seq;
      var ev = { stream: stream, seq: seq, type: type, at: new Date().toISOString(), payload: payload };
      setTimeout(function () { eventFns.slice().forEach(function (fn) { fn(clone(ev)); }); }, 0);
      return ev;
    }
    function later(job, fn, ms) { (timers[job] = timers[job] || []).push(setTimeout(fn, ms * scale)); }
    function wait(job, ms) { return new Promise(function (r) { later(job, r, ms); }); }
    function stop(job) { (timers[job] || []).forEach(clearTimeout); delete timers[job]; }

    function withIdem(method, params, fn) {
      var key = params && params.idempotencyKey;
      if (!key || typeof key !== 'string' || key.length > 64) return Promise.reject(rpcError(-32602, 'invalid params', { field: 'idempotencyKey' }));
      var rest = Object.assign({}, params); delete rest.idempotencyKey;
      var k = method + '\u0000' + key, digest = canonical(rest), rec = S.idem[k];
      if (rec) return rec.digest === digest ? Promise.resolve(clone(rec.result)) : Promise.reject(rpcError(-32003, 'idempotency_mismatch', { idempotencyKey: key }));
      return Promise.resolve(fn(rest)).then(function (result) { S.idem[k] = { digest: digest, result: clone(result) }; save(); return result; });
    }

    /* ---------- profile helpers (ports of the prototype) ---------- */
    function usesOf() { var p = S.onboarding.profile; return p.uses.concat(p.customUse ? p.customUse.split('\n') : []); }
    function goalsOf() { var p = S.onboarding.profile; return p.goals.concat(p.customGoal ? p.customGoal.split('\n') : []); }
    function latestCapabilities() {
      var best = null;
      Object.keys(S.inventories).forEach(function (id) { var i = S.inventories[id]; if ((i.status === 'complete' || i.status === 'partial') && (!best || i.startedAt > best.startedAt)) best = i; });
      return best ? best.capabilities : null;
    }
    // Port of buildQueue: security first; goals and usage order the rest (fixture queuePolicy).
    function baseOrder() {
      var u = usesOf(), g = goalsOf();
      var flags = { perf: hasWord(g, 'performance') || hasWord(g, 'optimi') || hasWord(u, 'gam'), custom: hasWord(g, 'custom') || hasWord(u, 'gam') };
      var out = [];
      F.queuePolicy.order.forEach(function (o) {
        if (typeof o === 'string') out.push(o);
        else { var neg = o['if'][0] === '!', v = flags[o['if'].replace('!', '')]; if (neg ? !v : v) out = out.concat(o.then); }
      });
      return out.filter(function (x, i) { return out.indexOf(x) === i; });
    }
    function focusCats(text) {
      return F.focus.filter(function (f) { return new RegExp(f[0], 'i').test(text || ''); }).map(function (f) { return f[1]; });
    }

    /* ---------- dispositions (stored by fingerprint or rule, so they carry into later scans) ---------- */
    function activeDisp(d) { return d && (d.status === 'ignored' || d.status === 'intended' || !d.expiresAt || Date.parse(d.expiresAt) > Date.now()) ? d : null; }
    function dispositionFor(f) { return activeDisp(S.dispositions.fingerprint[f.fingerprint]) || activeDisp(S.dispositions.rule[f.ruleId]) || null; }

    /* ---------- views ---------- */
    function onboardingView() { return clone(S.onboarding); }
    function scanView(sc) {
      var v = { scanId: sc.scanId, intent: sc.intent, status: sc.status, startedAt: sc.startedAt, finishedAt: sc.finishedAt,
        progress: sc.progress, coverage: sc.coverage, reportId: sc.reportId, eventCursor: { stream: 'scan:' + sc.scanId, seq: S.streams['scan:' + sc.scanId] || 0 },
        waveWords: sc.waveWords, baseReportId: sc.baseReportId || null };
      return clone(v);
    }
    function reportView(rep) {
      var r = clone(rep);
      r.findings.forEach(function (f) { f.disposition = clone(dispositionFor(f)); });
      return r;
    }
    function cardView(fu, cid) {
      var c = cid ? fu.cards[cid] : null;
      var card = c ? Object.assign({}, c, { reply: fu.replies[cid] || null }) : null;
      if (!card) return null;
      // As the host's View: an AI card names the task that added it (sourceTaskId, null on report cards); other
      // internal fields never leave; every card says where it came from and carries its note.
      card.sourceTaskId = c.origin === 'ai' ? c.aiTaskId || null : null;
      ['key', 'aiTaskId', 'noteTaskId', 'taskId'].forEach(function (k) { delete card[k]; });
      if (card.settingsUri === undefined) card.settingsUri = null;
      card.origin = card.origin || 'report';
      card.note = typeof card.note === 'string' && card.note ? card.note : null;
      card.noteBy = card.note ? card.noteBy || null : null;
      return card;
    }
    // v1.4: the visible card plus up to two upcoming ones.
    function followupView(fu) {
      var card = cardView(fu, fu.queue[0]);
      return clone({ reportId: fu.reportId, revision: fu.revision, total: fu.total, pendingCount: fu.queue.length, card: card,
        upcoming: fu.queue.slice(1, 3).map(function (cid) { return cardView(fu, cid); }),
        // An AI card's decision has no finding (Restore goes through followup.respond), as the host's View.
        decisions: fu.decisions.map(function (d) { var c = fu.cards[d.cardId] || {}; return { cardId: d.cardId, findingId: c.origin === 'ai' ? null : d.findingId || c.findingId || null, status: d.status, title: d.title, done: d.done }; }) });
    }
    function getScan(id) { var sc = S.scans[id]; if (!sc) throw rpcError(-32001, 'not_found', { kind: 'scan', id: id }); return sc; }
    function getReport(id) { var r = S.reports[id]; if (!r) throw rpcError(-32001, 'not_found', { kind: 'report', id: id }); return r; }
    function getFinding(rep, id) { var f = rep.findings.filter(function (x) { return x.id === id; })[0]; if (!f) throw rpcError(-32001, 'not_found', { kind: 'finding', id: id }); return f; }
    function getFollowup(id) { var fu = S.followups[id]; if (!fu) throw rpcError(-32001, 'not_found', { kind: 'report', id: id }); return fu; }

    /* ---------- inventory ---------- */
    function startInventory() {
      if (S.onboarding.profile.inventoryConsent !== 'granted') throw rpcError(-32013, 'invalid_state', { reason: 'inventory_consent_required' });
      var id = 'inv-' + randomHex(16), inv = F.inventory, stream = 'inventory:' + id;
      S.inventories[id] = { inventoryId: id, status: 'running', startedAt: new Date().toISOString(), facts: [], coverage: {}, capabilities: null };
      S.onboarding.resume.inventoryId = id; save();
      inv.steps.forEach(function (step, i) {
        later(id, function () {
          var rec = S.inventories[id]; if (rec.status !== 'running') return;
          step.facts.forEach(function (f) {
            var ev = emit(stream, 'inventory.fact', { kind: f.kind, label: f.label, group: step.name });
            rec.facts.push({ seq: ev.seq, kind: f.kind, label: f.label });
          });
          save();
        }, i * inv.stepDelayMs);
      });
      later(id, function () {
        var rec = S.inventories[id]; if (rec.status !== 'running') return;
        rec.status = inv.status; rec.coverage = clone(inv.coverage); rec.capabilities = clone(inv.capabilities);
        emit(stream, 'inventory.finished', { status: rec.status }); save(); stop(id);
      }, inv.steps.length * inv.stepDelayMs);
      return { inventoryId: id, status: 'running' };
    }
    function inventoryView(rec) { return clone({ inventoryId: rec.inventoryId, status: rec.status, facts: rec.facts, coverage: rec.coverage }); }

    /* ---------- scan ---------- */
    function previousReport(intent) {
      for (var i = 0; i < S.reportOrder.length; i++) { var r = S.reports[S.reportOrder[i]]; if (r && r.intent === intent) return r; }
      return null;
    }
    function startScan(p) {
      var intent = p.intent, text = p.text || '', base = null, rescan = intent === 'rescan', cats;
      if (rescan) {
        base = S.reports[p.baseReportId];
        if (!base) throw rpcError(-32001, 'not_found', { kind: 'report', id: p.baseReportId });
        intent = base.intent; text = (S.scans[base.scanId] || {}).text || text;
      }
      if (intent === 'audit.full') cats = F.scanCategories;
      else if (F.intentCategories[intent]) cats = F.scanCategories.filter(function (c) { return F.intentCategories[intent].indexOf(c.id) >= 0; });
      else throw rpcError(-32602, 'invalid params', { field: 'intent' });
      var active = Object.keys(S.scans).filter(function (id) { var s = S.scans[id].status; return s === 'running' || s === 'queued'; })[0];
      if (active) throw rpcError(-32004, 'scan_active', { scanId: active });
      // M6a: one lease for scans and benchmarks (§2).
      var benchRun = bench && bench.active();
      if (benchRun) throw rpcError(-32013, 'invalid_state', { reason: 'bench_active', runId: benchRun.runId });
      if (!base) base = previousReport(intent);
      var scanId = 'scan-' + randomHex(16), catIds = cats.map(function (c) { return c.id; });
      var planned = F.collectors.filter(function (c) { return catIds.indexOf(c.category) >= 0 && (!c.onlyOnRescan || rescan); });
      // The fixture's sea words follow its collectors' categories (in order).
      var WORD_CATS = ['security', 'security', 'performance', 'performance', 'storage', 'privacy_telemetry', 'diagnostics', 'customization', 'customization'];
      var sc = { scanId: scanId, intent: intent, text: text, status: 'running', startedAt: new Date().toISOString(), finishedAt: null, rescan: rescan,
        baseReportId: base ? base.reportId : null,
        progress: { done: 0, total: cats.length, categories: cats.map(function (c, i) { return { id: c.id, label: c.label, status: i === 0 ? 'running' : 'queued' }; }), current: null },
        coverage: [], reportId: null, planned: planned.map(function (c) { return c.id; }), evidence: [], gaps: [], keys: {},
        waveWords: F.scanWaveWords.filter(function (w, i) { return catIds.indexOf(WORD_CATS[i]) >= 0; }) };
      S.scans[scanId] = sc; S.onboarding.resume.scanId = scanId; save();
      runScan(sc, cats, planned);
      return scanView(sc);
    }
    // Categories run one after another; collectors within a category run side by side.
    function runScan(sc, cats, planned) {
      var stream = 'scan:' + sc.scanId, chain = Promise.resolve();
      cats.forEach(function (cat, ci) {
        chain = chain.then(function () {
          if (sc.status !== 'running') return;
          var cols = planned.filter(function (c) { return c.category === cat.id; });
          return Promise.all(cols.map(function (col) { return runCollector(sc, col); })).then(function () {
            return wait(sc.scanId, 1);
          }).then(function () {
            if (sc.status !== 'running') return;
            var c = sc.progress.categories[ci], next = sc.progress.categories[ci + 1];
            c.status = sc.gaps.some(function (g) { return g.category === cat.id; }) ? 'gap' : 'done';
            if (next) next.status = 'running';
            sc.progress.done = ci + 1;
            emit(stream, 'scan.progress', { done: ci + 1, total: cats.length, current: null });
            save();
          });
        });
      });
      // Building the report takes a beat after the last check, as in the prototype.
      chain.then(function () { return sc.status === 'running' ? wait(sc.scanId, 600) : null; })
        .then(function () { if (sc.status === 'running') return endScan(sc, null, true).then(save); });
    }
    function runCollector(sc, col) {
      if (col.skippable) return runSampler(sc, col);
      var timedOut = col.outcome === 'timed_out' || (col.timeoutMs != null && col.delayMs > col.timeoutMs);
      var at = timedOut ? (col.timeoutMs || col.delayMs) : col.delayMs;
      return wait(sc.scanId, at).then(function () {
        if (timedOut) return H(sc.scanId, col.id).then(function (gh) {
          if (sc.status !== 'running') return;
          sc.gaps.push({ id: 'gap-' + gh, category: col.category, title: col.gap.title, display: col.gap.display, collectorId: col.id, status: 'timed_out', source: col.gap.source });
          sc.coverage.push({ collectorId: col.id, category: col.category, status: 'timed_out', observations: 0, reason: 'No answer within ' + col.timeoutMs + ' ms' });
          emit('scan:' + sc.scanId, 'scan.coverage', { collectorId: col.id, status: 'timed_out' });
        });
        var skip = sc.rescan ? F.rescan.skipEvidence || [] : [];
        return commit(sc, col, col.evidence.filter(function (e) { return skip.indexOf(e.key) < 0; }), col.outcome === 'partial' ? 'partial' : 'complete');
      });
    }
    function commit(sc, col, evidence, status, reason) {
      var now = new Date().toISOString();
      return Promise.all(evidence.map(function (e) { return H(sc.scanId, col.id, e.subject); })).then(function (ids) {
        if (sc.status !== 'running') return;
        evidence.forEach(function (e, i) {
          sc.keys[e.key] = ids[i];
          sc.evidence.push({ id: 'ev-' + ids[i], category: col.category, source: e.source, observedAt: now, interval: col.interval || null,
            display: e.display, value: clone(e.value), collectorId: col.id, collectorVersion: col.version, quality: e.quality,
            sensitivity: F.sensitivity[e.key] || 'public', subject: e.subject, key: e.key });
        });
        sc.coverage.push({ collectorId: col.id, category: col.category, status: status, observations: evidence.length, reason: reason || null });
        emit('scan:' + sc.scanId, 'scan.coverage', { collectorId: col.id, status: status });
        save();
      });
    }
    // A long sampling collector: reports a countdown in Scan.progress.current and can be skipped.
    function runSampler(sc, col) {
      var stream = 'scan:' + sc.scanId, total = col.durationSeconds, elapsed = 0;
      return new Promise(function (resolve) {
        var job = { collectorId: col.id, skipped: false, finish: null };
        samplers[sc.scanId] = job;
        function cur() { return { collectorId: col.id, label: col.label, skippable: true, remainingSeconds: Math.max(0, total - elapsed) }; }
        function done(skipped) {
          if (job.finish === null) return;
          job.finish = null; delete samplers[sc.scanId];
          sc.progress.current = null;
          if (sc.status !== 'running') { resolve(); return; }
          var e = clone(col.evidence[0]);
          if (skipped) {
            // Committed samples are kept; the collector's coverage says it was skipped.
            e.value.intervalSeconds = elapsed; e.value.samples = elapsed;
            e.display = e.display.replace(/over \d+ s/, 'over ' + elapsed + ' s');
          }
          commit(sc, col, skipped && elapsed === 0 ? [] : [e], skipped ? 'skipped' : 'complete', skipped ? 'Skipped after ' + elapsed + ' s' : null).then(resolve);
        }
        job.finish = done;
        sc.progress.current = cur();
        emit(stream, 'scan.progress', { done: sc.progress.done, total: sc.progress.total, current: cur() });
        (function tick() {
          later(sc.scanId, function () {
            if (job.finish === null) return;
            if (sc.status !== 'running') { done(false); return; }
            elapsed += 1;
            if (elapsed >= total) { done(false); return; }
            sc.progress.current = cur();
            emit(stream, 'scan.progress', { done: sc.progress.done, total: sc.progress.total, current: cur() });
            tick();
          }, 1000);
        })();
      });
    }
    // Ends a scan (finished, cancelled or interrupted) and builds its report from committed evidence only.
    function endScan(sc, endStatus, notify) {
      stop(sc.scanId);
      var smp = samplers[sc.scanId]; if (smp && smp.finish) { smp.finish = null; delete samplers[sc.scanId]; }
      var status = endStatus || (sc.gaps.length ? 'complete_with_gaps' : 'complete');
      sc.status = status; sc.finishedAt = new Date().toISOString(); sc.progress.current = null;
      sc.planned.forEach(function (cid) {
        if (!sc.coverage.some(function (c) { return c.collectorId === cid; })) {
          var col = F.collectors.filter(function (c) { return c.id === cid; })[0];
          sc.coverage.push({ collectorId: cid, category: col.category, status: 'cancelled', observations: 0, reason: null });
        }
      });
      return buildReport(sc).then(function (rep) {
        sc.reportId = rep.reportId; S.onboarding.resume.scanId = sc.scanId; S.onboarding.resume.reportId = rep.reportId;
        if (notify !== false) emit('scan:' + sc.scanId, 'scan.finished', { status: status, reportId: rep.reportId });
        return rep;
      });
    }
    function categoryRows(sc, findings) {
      return sc.progress.categories.map(function (c) {
        var cov = sc.coverage.filter(function (x) { return x.category === c.id; });
        var ok = cov.filter(function (x) { return x.status === 'complete'; }).length;
        var some = cov.filter(function (x) { return x.status === 'partial' || (x.status === 'skipped' && x.observations > 0); }).length;
        var status = cov.length && ok === cov.length ? 'complete' : ok + some > 0 ? 'partial' : 'none';
        return { id: c.id, label: c.label, status: status, checked: ok + some, planned: cov.length,
          findings: findings.filter(function (f) { return f.category === c.id; }).length };
      });
    }
    function buildReport(sc) {
      return H(sc.scanId).then(function (rh) {
        var reportId = 'report-' + rh;
        var rules = F.rules.filter(function (r) { return r.evidence.every(function (k) { return sc.keys[k]; }); });
        return Promise.all(rules.map(function (r) {
          var first = sc.evidence.filter(function (e) { return e.key === r.evidence[0]; })[0];
          return H(r.ruleId, first.subject).then(function (fp) { return H(sc.scanId, r.ruleId, fp).then(function (fh) { return { rule: r, fp: fp, id: 'fnd-' + fh, subject: first.subject }; }); });
        })).then(function (fs) {
          var findings = fs.map(function (f) {
            return { id: f.id, ruleId: f.rule.ruleId, ruleVersion: f.rule.version, fingerprint: f.fp, category: f.rule.category, title: f.rule.title,
              severity: f.rule.severity, confidence: f.rule.confidence, evidenceIds: f.rule.evidence.map(function (k) { return 'ev-' + sc.keys[k]; }),
              actionEligibility: f.rule.actionEligibility, subject: f.subject, counterevidence: [], settingsUri: f.rule.settingsUri, disposition: null };
          });
          var partial = sc.status === 'cancelled' || sc.status === 'interrupted';
          var base = sc.baseReportId ? S.reports[sc.baseReportId] : null, comparison = null;
          if (base) {
            var now = findings.map(function (f) { return f.fingerprint; }), was = base.findings.map(function (f) { return f.fingerprint; });
            comparison = { 'new': now.filter(function (x) { return was.indexOf(x) < 0; }), resolved: was.filter(function (x) { return now.indexOf(x) < 0; }),
              unchanged: now.filter(function (x) { return was.indexOf(x) >= 0; }).length };
          }
          var rep = { reportId: reportId, scanId: sc.scanId, revision: 1,
            status: partial ? 'partial' : sc.gaps.length ? 'complete_with_gaps' : 'complete',
            generatedAt: new Date().toISOString(), intent: sc.intent, counts: { findings: findings.length, gaps: sc.gaps.length },
            evidence: sc.evidence.map(function (e) { var o = Object.assign({}, e); delete o.subject; delete o.key; return o; }),
            findings: findings, coverage: clone(sc.coverage), gaps: clone(sc.gaps),
            categories: categoryRows(sc, findings), baseReportId: base ? base.reportId : null, comparison: comparison };
          S.reports[reportId] = rep; S.reportOrder.unshift(reportId);
          return Promise.all(fs.map(function (f) { return H(reportId, f.id); })).then(function (chs) {
            var cards = {}, byKey = {};
            fs.forEach(function (f, i) {
              var c = f.rule.card, cid = 'card-' + chs[i];
              byKey[c.key] = cid;
              cards[cid] = { id: cid, findingId: f.id, category: f.rule.category, title: f.rule.title, text: c.text,
                evidenceIds: f.rule.evidence.map(function (k) { return 'ev-' + sc.keys[k]; }), options: c.options.concat(INTENDED_RULES.indexOf(f.rule.ruleId) >= 0 ? ['Intended'] : []),
                guide: c.guide || null, preview: c.preview || null, actionId: c.actionId || null, receipt: c.receipt || null, done: c.done || null,
                settingsUri: f.rule.settingsUri, key: c.key };
            });
            // A targeted scan puts its own categories first; typed text is matched by its words (prototype rules).
            var present = F.rules.map(function (r) { return r.card.key; }).filter(function (k) { return byKey[k]; });
            var fc = F.intentCategories[sc.intent] || (new RegExp(F.fullAuditPattern, 'i').test(sc.text) ? [] : focusCats(sc.text));
            var front = present.filter(function (k) { return fc.indexOf(cards[byKey[k]].category) >= 0; });
            var base2 = baseOrder().filter(function (k) { return byKey[k]; });
            var order = front.concat(base2, present).filter(function (k, i, a) { return a.indexOf(k) === i; });
            // Ignored or snoozed (unexpired) findings stay out of the queue; they show as decisions so they can be restored.
            var decisions = [], queue = [];
            order.forEach(function (k) {
              var cid = byKey[k], f = findings.filter(function (x) { return x.id === cards[cid].findingId; })[0], d = dispositionFor(f);
              if (d) decisions.push({ cardId: cid, findingId: f.id, status: d.status, title: f.title });
              else queue.push(cid);
            });
            S.followups[reportId] = { reportId: reportId, revision: 1, total: queue.length, queue: queue, cards: cards, decisions: decisions, replies: {} };
            return rep;
          });
        });
      });
    }

    /* ---------- follow-ups ---------- */
    function respond(p) {
      var fu = getFollowup(p.reportId);
      if (p.expectedRevision !== fu.revision) throw rpcError(-32002, 'stale_revision', { current: followupView(fu) });
      if (p.scope === 'report') {
        if (!p.text) throw rpcError(-32602, 'invalid params', { field: 'text' });
        var cats = focusCats(p.text);
        var ids = fu.queue.filter(function (cid) { return cats.indexOf(fu.cards[cid].category) >= 0; });
        // Keep rule order for the moved cards, as the prototype's focusFor does.
        ids.sort(function (a, b) { return keyIndex(fu.cards[a].key) - keyIndex(fu.cards[b].key); });
        fu.queue = ids.concat(fu.queue.filter(function (x) { return ids.indexOf(x) < 0; }));
      } else if (p.scope === 'card') {
        var cid = p.cardId, card = fu.cards[cid];
        if (!card) throw rpcError(-32001, 'not_found', { kind: 'card', id: cid });
        var opt = p.option, visible = fu.queue.slice(0, 3).indexOf(cid) >= 0;   // v1.4: card or upcoming
        var decided = fu.decisions.filter(function (d) { return d.cardId === cid; })[0];
        if (opt === 'restore') {
          if (!decided || (decided.status !== 'ignored' && decided.status !== 'later')) throw rpcError(-32013, 'invalid_state', { reason: 'not_ignored' });
          fu.decisions = fu.decisions.filter(function (d) { return d !== decided; });
          fu.queue = [cid].concat(fu.queue.filter(function (x) { return x !== cid; }));
        } else if (opt === 'simulate.undo') {
          if (!decided || decided.status !== 'verified') throw rpcError(-32013, 'invalid_state', { reason: 'not_verified' });
          decided.status = 'restored';
        } else {
          if (!visible) throw rpcError(-32013, 'invalid_state', { reason: 'not_visible_card' });
          // Later leaves the deck for this report (restorable); a new scan builds a fresh deck, as the host does.
          if (opt === 'ignore' || opt === 'done' || opt === 'decline' || opt === 'later' || opt === 'simulate.approve') {
            var status = { ignore: 'ignored', done: 'done', decline: 'declined', later: 'later', 'simulate.approve': 'verified' }[opt];
            if (opt === 'simulate.approve' && !card.preview) throw rpcError(-32013, 'invalid_state', { reason: 'no_preview' });
            fu.queue = fu.queue.filter(function (x) { return x !== cid; });
            fu.decisions = fu.decisions.filter(function (d) { return d.cardId !== cid; }).concat([{ cardId: cid, findingId: card.findingId, status: status, title: card.title, done: card.done }]);
          } else if (opt == null && p.text) {
            fu.replies[cid] = askReply(fu, card, p.text);
          } else throw rpcError(-32602, 'invalid params', { field: 'option' });
        }
      } else throw rpcError(-32602, 'invalid params', { field: 'scope' });
      fu.revision += 1;
      return followupView(fu);
    }
    function keyIndex(k) { for (var i = 0; i < F.rules.length; i++) if (F.rules[i].card.key === k) return i; return 99; }
    function askReply(fu, card, text) {
      var rep = S.reports[fu.reportId], ev = rep.evidence.filter(function (e) { return e.id === card.evidenceIds[0]; })[0];
      var t = F.askTemplates.filter(function (a) { return (a.card === card.key || a.card === '*') && new RegExp(a.pattern, 'i').test(text); })[0];
      // Same shape as the backend (followup.schema.json card.reply): {question, text, evidenceIds}.
      return { question: text, text: t.reply.replace('{display}', ev ? ev.display : ''), evidenceIds: card.evidenceIds.slice() };
    }
    function cardsOf(fu, rep, match) {
      return Object.keys(fu.cards).filter(function (cid) { var f = rep.findings.filter(function (x) { return x.id === fu.cards[cid].findingId; })[0]; return f && match(f); });
    }
    function setDisposition(p) {
      var rep = getReport(p.reportId), fu = getFollowup(p.reportId), f = getFinding(rep, p.findingId);
      if (p.status !== 'ignored' && p.status !== 'snoozed' && p.status !== 'intended') throw rpcError(-32602, 'invalid params', { field: 'status' });
      if (p.status === 'intended' && p.scope !== 'finding') throw rpcError(-32602, 'invalid params', { field: 'scope' });
      if (p.scope !== 'finding' && p.scope !== 'rule') throw rpcError(-32602, 'invalid params', { field: 'scope' });
      var exp = p.expiresAt ? Date.parse(p.expiresAt) : NaN;
      if (p.status === 'snoozed' && !(exp > Date.now() && exp - Date.now() <= YEAR_MS)) throw rpcError(-32602, 'invalid params', { field: 'expiresAt' });
      var d = { status: p.status, scope: p.scope, expiresAt: p.status === 'snoozed' ? new Date(exp).toISOString() : (p.expiresAt || null) };
      if (p.scope === 'finding') S.dispositions.fingerprint[f.fingerprint] = d; else S.dispositions.rule[f.ruleId] = d;
      var hit = cardsOf(fu, rep, function (x) { return p.scope === 'finding' ? x.fingerprint === f.fingerprint : x.ruleId === f.ruleId; });
      hit.forEach(function (cid) {
        var c = fu.cards[cid];
        fu.queue = fu.queue.filter(function (x) { return x !== cid; });
        fu.decisions = fu.decisions.filter(function (x) { return x.cardId !== cid; }).concat([{ cardId: cid, findingId: c.findingId, status: d.status, title: c.title }]);
      });
      fu.revision += 1;
      return followupView(fu);
    }
    function clearDisposition(p) {
      var rep = getReport(p.reportId), fu = getFollowup(p.reportId), f = getFinding(rep, p.findingId);
      delete S.dispositions.fingerprint[f.fingerprint];
      var byRule = S.dispositions.rule[f.ruleId]; delete S.dispositions.rule[f.ruleId];
      var hit = cardsOf(fu, rep, function (x) { return x.fingerprint === f.fingerprint || (byRule && x.ruleId === f.ruleId && !dispositionFor(x)); });
      // The chosen finding re-enters at the front; review state only, no setting changes.
      hit.sort(function (a, b) { return fu.cards[a].findingId === f.id ? -1 : fu.cards[b].findingId === f.id ? 1 : 0; });
      hit.slice().reverse().forEach(function (cid) {
        var dec = fu.decisions.filter(function (x) { return x.cardId === cid; })[0];
        if (dec && (dec.status === 'ignored' || dec.status === 'snoozed' || dec.status === 'intended')) {
          fu.decisions = fu.decisions.filter(function (x) { return x !== dec; });
          fu.queue = [cid].concat(fu.queue.filter(function (x) { return x !== cid; }));
        }
      });
      // An item kept out when the report was built now counts toward the total.
      fu.total = Math.max(fu.total, fu.queue.length + fu.decisions.length);
      fu.revision += 1;
      return followupView(fu);
    }

    /* ---------- starters ---------- */
    function starters() {
      var u = usesOf(), caps = latestCapabilities();
      var ok = F.starters.filter(function (s) {
        if (s.needsUse && !hasWord(u, s.needsUse)) return false;
        if (s.needsInventory) { if (!caps) return false; for (var k in s.needsInventory) if (caps[k] !== s.needsInventory[k]) return false; }
        return true;
      });
      var personal = ok.filter(function (s) { return s.needsUse || s.needsInventory; }), general = ok.filter(function (s) { return !s.needsUse && !s.needsInventory; });
      var out = [general[0]], gi = 1;
      personal.forEach(function (s) { out.push(s); if (general[gi]) out.push(general[gi++]); });
      out = out.concat(general.slice(gi)).filter(Boolean);
      return { starters: out.map(function (s) { return { id: s.id, text: s.text, intent: s.intent, basis: s.needsUse ? 'profile' : s.needsInventory ? 'inventory' : 'general' }; }) };
    }

    /* ---------- onboarding ---------- */
    var STEPS = ['provider', 'use', 'goal', 'specs', 'complete'];
    function saveOnboarding(p) {
      var o = S.onboarding, patch = p.patch || {};
      if (p.expectedRevision !== o.revision) throw rpcError(-32002, 'stale_revision', { current: onboardingView() });
      if (patch.step != null && STEPS.indexOf(patch.step) < 0) throw rpcError(-32602, 'invalid params', { field: 'step' });
      ['customUse', 'customGoal'].forEach(function (f) { if (patch[f] != null && String(patch[f]).length > 200) throw rpcError(-32602, 'invalid params', { field: f }); });
      if (patch.inventoryConsent != null && ['unset', 'granted', 'skipped'].indexOf(patch.inventoryConsent) < 0) throw rpcError(-32602, 'invalid params', { field: 'inventoryConsent' });
      if (patch.provider && ['local', 'later', 'claude-code', 'codex', 'api', 'chatgpt'].indexOf(patch.provider.value) < 0) throw rpcError(-32602, 'invalid params', { field: 'provider' });
      if (patch.step) o.step = patch.step;
      ['provider', 'uses', 'customUse', 'goals', 'customGoal', 'inventoryConsent'].forEach(function (f) { if (patch[f] !== undefined) o.profile[f] = clone(patch[f]); });
      o.revision += 1;
      return onboardingView();
    }

    var METHODS = {
      'onboarding.get': function () { return onboardingView(); },
      'onboarding.save': saveOnboarding,
      'inventory.start': function (p) { return withIdem('inventory.start', p, startInventory); },
      'inventory.get': function (p) { var r = S.inventories[p.inventoryId]; if (!r) throw rpcError(-32001, 'not_found', { kind: 'inventory', id: p.inventoryId }); return inventoryView(r); },
      'inventory.cancel': function (p) {
        var r = S.inventories[p.inventoryId]; if (!r) throw rpcError(-32001, 'not_found', { kind: 'inventory', id: p.inventoryId });
        if (r.status === 'running') { stop(r.inventoryId); r.status = 'cancelled'; emit('inventory:' + r.inventoryId, 'inventory.finished', { status: 'cancelled' }); }
        return inventoryView(r);
      },
      'starter.list': function () { return starters(); },
      'scan.start': function (p) { return withIdem('scan.start', p, startScan); },
      'scan.get': function (p) { return scanView(getScan(p.scanId)); },
      'scan.cancel': function (p) {
        var sc = getScan(p.scanId);
        if (sc.status !== 'running' && sc.status !== 'queued') return scanView(sc);
        return endScan(sc, 'cancelled', true).then(function () { return scanView(sc); });
      },
      'scan.skipCollector': function (p) {
        var sc = getScan(p.scanId), job = samplers[p.scanId];
        if (sc.status !== 'running' || !job || job.collectorId !== p.collectorId || !job.finish) throw rpcError(-32013, 'invalid_state', { reason: 'not_skippable' });
        job.finish(true);
        return scanView(sc);
      },
      'report.get': function (p) { return reportView(getReport(p.reportId)); },
      'report.list': function (p) {
        var lim = Math.min((p && p.limit) || 50, 50), ids = S.reportOrder, start = p && p.before ? ids.indexOf(p.before) + 1 : 0;
        var page = ids.slice(start, start + lim);
        return { reports: page.map(function (id) { var r = S.reports[id]; return { reportId: id, scanId: r.scanId, generatedAt: r.generatedAt, status: r.status, intent: r.intent }; }),
          next: start + lim < ids.length ? page[page.length - 1] : null };
      },
      'report.compare': function (p) {
        var a = reportView(getReport(p.reportId)), b = reportView(getReport(p.baseReportId));
        var fa = a.findings.map(function (f) { return f.fingerprint; }), fb = b.findings.map(function (f) { return f.fingerprint; });
        return { 'new': a.findings.filter(function (f) { return fb.indexOf(f.fingerprint) < 0; }),
          resolved: b.findings.filter(function (f) { return fa.indexOf(f.fingerprint) < 0; }),
          unchanged: fa.filter(function (x) { return fb.indexOf(x) >= 0; }) };
      },
      'evidence.get': function (p) {
        var ev = getReport(p.reportId).evidence.filter(function (e) { return e.id === p.evidenceId; })[0];
        if (!ev) throw rpcError(-32001, 'not_found', { kind: 'evidence', id: p.evidenceId });
        return clone(ev);
      },
      'finding.get': function (p) {
        var rep = getReport(p.reportId), f = getFinding(rep, p.findingId), rule = F.rules.filter(function (r) { return r.ruleId === f.ruleId; })[0];
        var ids = f.evidenceIds.concat(f.counterevidence || []);
        var inputs = F.collectors.filter(function (c) { return (c.evidence || []).some(function (e) { return rule.evidence.indexOf(e.key) >= 0; }); }).map(function (c) { return c.id; });
        var fv = clone(f); fv.disposition = clone(dispositionFor(f));
        return { finding: fv, evidence: rep.evidence.filter(function (e) { return ids.indexOf(e.id) >= 0; }),
          coverage: rep.coverage.filter(function (c) { return inputs.indexOf(c.collectorId) >= 0; }),
          rule: { id: rule.ruleId, version: rule.version, inputs: inputs } };
      },
      'finding.setDisposition': function (p) { return withIdem('finding.setDisposition', p, setDisposition); },
      'finding.clearDisposition': function (p) { return withIdem('finding.clearDisposition', p, clearDisposition); },
      'followup.get': function (p) { return followupView(getFollowup(p.reportId)); },
      'followup.respond': function (p) { return withIdem('followup.respond', p, respond); },
      // Shell-handled on the desktop; here it only checks the catalog (a browser cannot open Windows Settings).
      'shell.openSettings': function (p) {
        if (SETTINGS_CATALOG.indexOf(p.uri) < 0) throw rpcError(-32602, 'invalid params', { field: 'uri' });
        return { opened: false };
      },
      // Shell-handled on the desktop (protocol-v1 §7): "Try again" on the startup screen. As the shell: back to plain
      // connecting at once, then the simulated host ends the same way again (or opens, when set with recoverOnRetry).
      'shell.retryBackend': function (p) {
        if (Object.keys(p).length) throw rpcError(-32602, 'invalid params', { field: 'params' });
        if (backendState === 'ready' || backendState === 'incompatible') return { retrying: true };
        var outcome = recoverOnRetry ? ['ready', null] : [backendState, backendDetail];
        setBackendState(backendState, null);
        later('backend', function () { setBackendState(outcome[0], outcome[1]); }, 1200);
        return { retrying: true };
      },
      // Dev hook: show a startup state (state connecting|reconnecting|ready|incompatible, detail null or a §7 code
      // such as other_session|needs_update|cant_start). recoverOnRetry: the next shell.retryBackend ends in ready.
      'fixture.setBackendState': function (p) {
        if (BACKEND_STATES.indexOf(p.state) < 0) throw rpcError(-32602, 'invalid params', { field: 'state' });
        recoverOnRetry = !!p.recoverOnRetry;
        setBackendState(p.state, p.detail == null ? null : p.detail);
        return { state: backendState, detail: backendDetail };
      },
      'fixture.summary': function (p) {
        var sc = S.scans[getReport(p.reportId).scanId];
        return { lines: F.mockAiSummary.filter(function (l) { return l.evidence.every(function (k) { return sc.keys[k]; }); })
          .map(function (l) { return { text: l.text, evidenceIds: l.evidence.map(function (k) { return 'ev-' + sc.keys[k]; }) }; }) };
      }
    };

    // M4: plans, approval, operations and undo (adapter-fixture-actions.js).
    var makeActions = (root.OptTool && root.OptTool.createFixtureActions) || (typeof require === 'function' ? require('./adapter-fixture-actions.js').createFixtureActions : null);
    var actions = makeActions ? makeActions({ state: function () { return S; }, emit: emit, later: later, H: H, withIdem: withIdem, getReport: getReport,
      rpcError: rpcError, clone: clone, randomHex: randomHex, save: save,
      ruleOf: function (findingId) {
        for (var id in S.reports) { var f = S.reports[id].findings.filter(function (x) { return x.id === findingId; })[0]; if (f) return F.rules.filter(function (r) { return r.ruleId === f.ruleId; })[0] || null; }
        return null;
      } }) : null;
    if (actions) Object.keys(actions.methods).forEach(function (k) { METHODS[k] = actions.methods[k]; });

    // M6a: the benchmark (adapter-fixture-bench.js), with invented numbers.
    var makeBench = (root.OptTool && root.OptTool.createFixtureBench) || (typeof require === 'function' ? require('./adapter-fixture-bench.js').createFixtureBench : null);
    bench = makeBench ? makeBench({ state: function () { return S; }, emit: emit, later: later, stop: stop, withIdem: withIdem, rpcError: rpcError, clone: clone, randomHex: randomHex, H: H, save: save }) : null;
    if (bench) Object.keys(bench.methods).forEach(function (k) { METHODS[k] = bench.methods[k]; });

    // M3: the agent gateway simulation (adapter-fixture-agent.js).
    var makeAgent = (root.OptTool && root.OptTool.createFixtureAgent) || (typeof require === 'function' ? require('./adapter-fixture-agent.js').createFixtureAgent : null);
    var agent = makeAgent ? makeAgent({ state: function () { return S; }, fixture: function () { return F; }, emit: emit, later: later, stop: stop, H: H,
      withIdem: withIdem, getReport: getReport, rpcError: rpcError, clone: clone, randomHex: randomHex, save: save,
      manual: function () { return !!(S && S.agent && S.agent.manual); }, actions: actions,
      benchRun: function (runId) { var b = S && S.bench; return b && b.runs[runId] ? clone(b.runs[runId]) : null; } }) : null;
    if (agent) Object.keys(agent).forEach(function (k) { if (k !== '__restart') METHODS[k] = agent[k]; });

    function call(method, params) {
      return ready.then(function () {
        var fn = METHODS[method];
        if (!fn) throw rpcError(-32601, 'method not found', { method: method });
        return fn(params || {});
      }).then(function (res) { save(); return clone(res); });
    }
    function sub(list, fn) { list.push(fn); return function () { var i = list.indexOf(fn); if (i >= 0) list.splice(i, 1); }; }

    return {
      kind: 'fixture',
      ready: ready,
      call: call,
      onEvent: function (fn) { return sub(eventFns, fn); },
      onState: function (fn) { return sub(stateFns, fn); },
      state: function () { return backendState; },
      detail: function () { return backendDetail; },
      reset: function () { Object.keys(timers).forEach(stop); S = fresh(); save(); }
    };
  }

  createFixtureAdapter.SETTINGS_CATALOG = SETTINGS_CATALOG;
  if (root) { root.OptTool = root.OptTool || {}; root.OptTool.createFixtureAdapter = createFixtureAdapter; }
  if (typeof module !== 'undefined' && module.exports) module.exports = { createFixtureAdapter: createFixtureAdapter };
})(typeof window !== 'undefined' ? window : globalThis);
