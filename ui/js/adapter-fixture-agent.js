/* Fixture adapter, M3 part: an offline simulation of the agent gateway (contracts/m3-agent.md §4) so the website
   demo can show providers, native pairing, grants, a scripted two-step investigation, activity, budgets, Stop and
   the report review. Plugged into adapter-fixture.js, which owns storage, events and timers.

   Beyond the contract (fixture only; the desktop shell handles the real ones):
     shell.requestApiKey {kind} → {status}   stands in for the shell's native key dialog: no key ever reaches the page;
                                              after a short delay the provider counts as connected.
   Native clients are simulated: provider.connect on an MCP kind schedules an agent.pairing (as the bridge's first tool
   call would), and an approved task gets two tool calls with origin claude_code / codex. m5: shell.clientStatus also
   stands in for the shell's provider.setClient, so an installed, signed-in client makes its kind connected, and a
   task started without a pairing is headless: asked in the app, with the same events as an in-app turn. */
(function (root) {
  'use strict';

  var PROVIDERS = [
    { kind: 'local', label: 'Local only', mode: 'local', models: [], defaultModel: null },
    { kind: 'anthropic_api', label: 'Claude (API)', mode: 'api_key', models: ['claude-fixture'], defaultModel: 'claude-fixture' },
    { kind: 'openai_api', label: 'OpenAI (API)', mode: 'api_key', models: ['gpt-fixture'], defaultModel: 'gpt-fixture' },
    { kind: 'chatgpt_plan', label: 'ChatGPT', mode: 'sign_in', models: ['gpt-fixture'], defaultModel: 'gpt-fixture' },
    { kind: 'claude_code_mcp', label: 'Claude Code', mode: 'native_mcp', models: [], defaultModel: null },
    { kind: 'codex_mcp', label: 'Codex', mode: 'native_mcp', models: [], defaultModel: null }
  ];
  var DIAGNOSTICS = { 'power.inventory': 'win.system.power', 'process.sample': 'win.perf.sample', 'startup.list': 'win.security.startup',
    'events.recent': 'win.events.system', 'storage.volumes': 'win.system.volumes', 'network.listeners': 'win.security.listeners',
    'display.modes': 'win.custom.display', 'devices.problems': 'win.devices.pnp', 'security.status': 'win.security.wsc' };
  var LIMITS = { diagnosticCalls: 10, resultBytes: 32768, totalBytes: 131072, turns: 8, turnSeconds: 90, maxSampleSeconds: 60 };
  var CATEGORY_DIAG = { performance: 'process.sample', storage: 'storage.volumes', security: 'security.status', diagnostics: 'events.recent',
    customization: 'display.modes', misc: 'display.modes' };
  var SAMPLE_SECONDS = 10;
  var INSTALL = 'C:\\Program Files\\Optimus\\OptimizationTool.Mcp.exe';
  var CLIENT = { claude_code_mcp: 'claude_code', codex_mcp: 'codex' };

  // In-app kinds: two with API keys, and the ChatGPT plan through a browser sign-in.
  function isApi(k) { return k === 'anthropic_api' || k === 'openai_api' || k === 'chatgpt_plan'; }
  function isMcp(k) { return k === 'claude_code_mcp' || k === 'codex_mcp'; }
  function size(x) { return new TextEncoder().encode(JSON.stringify(x)).length; }

  /** ctx: { state(), fixture(), emit, later, stop, H, withIdem, getReport, rpcError, clone, randomHex, save } */
  function createFixtureAgent(ctx) {
    var E = ctx.rpcError, clone = ctx.clone;
    function A() {
      var S = ctx.state();
      if (!S.agent) S.agent = { providers: {}, keys: {}, pairings: {}, tasks: {}, activity: {}, taskEvidence: {}, previews: {}, turnOf: {} };
      return S.agent;
    }
    // As the host's events table: every task-stream event is kept, and agent.get returns the last 100 ({seq, type, at, payload}).
    var rawEmit = ctx.emit;
    ctx = Object.assign({}, ctx, { emit: function (s, type, payload) {
      var ev = rawEmit(s, type, payload);
      if (/^task:/.test(s)) {
        var h = A().history = A().history || {}, id = s.slice(5);
        (h[id] = h[id] || []).push({ seq: ev.seq, type: ev.type, at: ev.at, payload: clone(ev.payload) });
        if (h[id].length > 100) h[id] = h[id].slice(-100);
      }
      return ev;
    } });
    // §9 connection states. Every change is also a provider.status event on the user stream.
    function baseStatus(kind) { return kind === 'local' ? { status: 'local' } : isMcp(kind) ? { status: 'waiting', detail: 'never_seen' } : { status: 'not_configured' }; }
    // m5 §1: what the last provider.setClient says (installed and signed in → connected), once the shell has reported.
    var PAIRED = { pending: 1, paired: 1, active: 1, client_closed: 1 };
    function clientBase(kind, fallback) {
      var r = (A().clients || {})[CLIENT[kind]];
      if (!r || !r.reported) return fallback || baseStatus(kind);
      // As the host: an outdated or blocked CLI is error/unavailable (not usable in-app).
      if (r.blocked || (r.installed && r.outdated)) return { status: 'error', detail: 'unavailable' };
      if (!r.installed) return { status: 'waiting', detail: 'never_seen' };
      return r.signedIn === true ? { status: 'connected' } : r.signedIn === false ? { status: 'error', detail: 'signed_out' } : fallback || baseStatus(kind);
    }
    // Precedence: a user-window pairing wins while it lasts; then connected / signed_out; then waiting.
    function syncClient(kind) {
      var cur = A().providers[kind] || baseStatus(kind);
      if (PAIRED[cur.status] || (cur.status === 'error' && cur.detail === 'bridge_rejected')) return;
      var b = clientBase(kind, cur.status === 'connected' || cur.detail === 'signed_out' ? cur : { status: 'waiting', detail: cur.detail === 'never_seen' ? 'never_seen' : 'idle' });
      setStatus(kind, b.status, b.detail);
    }
    function entry(kind) {
      var p = PROVIDERS.filter(function (x) { return x.kind === kind; })[0];
      if (!p) throw E(-32602, 'invalid params', { field: 'kind' });
      var st = A().providers[kind] || baseStatus(kind);
      var out = Object.assign(clone(p), { status: st.status, detail: st.detail || null, since: st.since || null });
      if (st.code) out.code = st.code;
      if (isApi(kind)) out.capabilities = { tools: !(A().noTools && A().noTools[kind]) };
      // As the host does: the pending pairing's ID (so the app can approve it after a reload), or the approved one.
      if (isMcp(kind)) {
        var want = st.status === 'pending' ? 'pending' : st.status === 'paired' || st.status === 'active' || st.status === 'client_closed' ? 'approved' : null;
        var pr = want && Object.keys(A().pairings).map(function (id) { return A().pairings[id]; }).filter(function (x) { return x.kind === kind && x.status === want; }).pop();
        if (pr) out.pairingId = pr.pairingId;
      }
      return out;
    }
    function setStatus(kind, status, detail, code) {
      var cur = A().providers[kind] || baseStatus(kind);
      if (cur.status === status && (cur.detail || null) === (detail || null) && (cur.code || null) === (code || null)) return;
      var next = { status: status, detail: detail || null, since: new Date().toISOString() };
      if (code) next.code = code;
      A().providers[kind] = next;
      var ev = { kind: kind, status: status, detail: next.detail, since: next.since };
      if (code) ev.code = code;
      ctx.emit('user', 'provider.status', ev);
    }
    // The bridge pairs on its first tool call; here that happens a few seconds after the command is shown.
    function schedulePairing(kind, ms) {
      ctx.stop('pair:' + kind);
      ctx.later('pair:' + kind, function () {
        if (entry(kind).status !== 'waiting' && entry(kind).status !== 'client_closed') return;
        var id = 'pair-' + ctx.randomHex(8), code = String(1000 + Math.floor(Math.random() * 9000));
        A().pairings[id] = { pairingId: id, kind: kind, client: CLIENT[kind], code: code, status: 'pending', createdAt: new Date().toISOString() };
        setStatus(kind, 'pending', null, code);
        ctx.emit('user', 'agent.pairing', { pairingId: id, client: CLIENT[kind], code: code, status: 'pending' });
        ctx.save();
      }, ms == null ? 3000 : ms);
    }
    function wait(job, ms) { return new Promise(function (r) { ctx.later(job, r, ms); }); }
    function stream(taskId) { return 'task:' + taskId; }

    /* ---------- providers and pairing ---------- */
    function connect(p) {
      var kind = p.kind, e = entry(kind);
      if (kind === 'chatgpt_plan') {
        // Stand-in for the browser sign-in: checking now, the outcome a moment later as a provider.status event.
        // Dev hook fixture.setSignIn {result} picks the next outcome, as the host reports it: ok | not_eligible | auth |
        // network | unavailable | callback_timeout (the browser never came back) | cancelled | storage (not saved).
        if (e.status === 'checking') return e;
        setStatus(kind, 'checking');
        ctx.later('signin:' + kind, function () {
          var r = A().signInResult || 'ok'; A().signInResult = null;
          if (r === 'ok') { A().keys[kind] = 'signed_in'; setStatus(kind, 'connected'); } else setStatus(kind, 'error', r);
          ctx.save();
        }, 1500);
        return entry(kind);
      }
      if (isApi(kind)) {
        // Re-runs the check on the stored key: a rejected key or empty credit stays until the key is replaced.
        if (!A().keys[kind]) setStatus(kind, 'not_configured');
        else if (A().keys[kind] === 'auth' || A().keys[kind] === 'quota') setStatus(kind, 'error', A().keys[kind]);
        else { setStatus(kind, 'checking'); setStatus(kind, 'connected', (A().notSaved || {})[kind] ? 'not_saved' : null); }
        return entry(kind);
      }
      if (isMcp(kind)) {
        if (e.status === 'waiting' || (e.status === 'error' && e.detail !== 'bridge_rejected' && e.detail !== 'signed_out' && e.detail !== 'unavailable')) {
          if (e.status === 'error') setStatus(kind, 'waiting', 'idle');
          if (!ctx.manual()) schedulePairing(kind);
        }
        return Object.assign(entry(kind), { registration: {
          claude: 'claude mcp add --scope user optimization-tool -- "' + INSTALL + '"',
          codex: 'codex mcp add optimization-tool -- "' + INSTALL + '"' } });
      }
      return e;
    }
    function disconnect(p) {
      var kind = p.kind; entry(kind);
      delete A().keys[kind];
      if (A().notSaved) delete A().notSaved[kind];
      ctx.stop('pair:' + kind);
      Object.keys(A().pairings).forEach(function (id) {
        var pr = A().pairings[id];
        // As the host: an open pairing is denied, and the app hears about it.
        if (pr.kind === kind && (pr.status === 'pending' || pr.status === 'approved')) {
          pr.status = 'denied';
          ctx.emit('user', 'agent.pairing', { pairingId: pr.pairingId, client: pr.client, code: pr.code, status: 'denied' });
        }
      });
      Object.keys(A().tasks).forEach(function (id) { var t = A().tasks[id]; if (t.recipient.kind === kind && t.status === 'active') stopTask(t, 'stopped'); });
      if (kind !== 'local') { var b = isMcp(kind) ? clientBase(kind) : baseStatus(kind); setStatus(kind, b.status, b.detail); }
      return entry(kind);
    }
    // As the shell + host: the dialog, then provider.setKey's check and save. Dev hook fixture.setKeyResult {result}
    // picks the next outcome: ok | cancelled | auth | quota | network | unavailable | not_eligible | storage (nothing
    // saved) | not_saved (works, kept for this session only). Errors come back as {status:'error', error:<code>}.
    function requestApiKey(p) {
      if (!isApi(p.kind) || p.kind === 'chatgpt_plan') throw E(-32602, 'invalid params', { field: 'kind' });
      var r = A().keyResult || 'ok'; A().keyResult = null;
      if (r === 'cancelled') return wait('key:' + p.kind, 300).then(function () { return { status: 'cancelled' }; });
      setStatus(p.kind, 'checking');
      return wait('key:' + p.kind, 700).then(function () {
        A().notSaved = A().notSaved || {};
        if (r === 'ok' || r === 'not_saved') {
          A().keys[p.kind] = true; A().notSaved[p.kind] = r === 'not_saved';
          setStatus(p.kind, 'connected', r === 'not_saved' ? 'not_saved' : null); ctx.save();
          return { status: 'connected' };
        }
        // A rejected key is not kept; the previous key (if any) stays stored, as on the host.
        setStatus(p.kind, 'error', r); ctx.save();
        return { status: 'error', error: r };
      });
    }
    function approvePairing(p) {
      var pr = A().pairings[p.pairingId];
      if (!pr) throw E(-32001, 'not_found', { kind: 'pairing', id: p.pairingId });
      if (pr.status !== 'pending') throw E(-32013, 'invalid_state', { reason: 'pairing_' + pr.status });
      pr.status = p.approve ? 'approved' : 'denied';
      if (p.approve) setStatus(pr.kind, 'paired');
      else { setStatus(pr.kind, 'error', 'denied'); setStatus(pr.kind, 'waiting', 'idle'); }
      ctx.emit('user', 'agent.pairing', { pairingId: pr.pairingId, client: pr.client, code: pr.code, status: pr.status });
      return { pairingId: pr.pairingId, status: pr.status };
    }

    /* ---------- tasks ---------- */
    function taskView(t) { return clone(t); }
    function start(p) {
      var kind = p.recipientKind, e = entry(kind);
      if (kind === 'local') throw E(-32602, 'invalid params', { field: 'recipientKind' });
      if (isApi(kind) && e.status !== 'connected') throw E(-32013, 'invalid_state', { reason: 'not_connected' });
      if (isApi(kind) && e.capabilities && e.capabilities.tools === false && (((p.grant || {}).diagnostics || []).length || (p.grant || {}).canRequestChanges))
        throw E(-32013, 'invalid_state', { reason: 'tools_unavailable' });
      var pairing = null;
      var headless = isMcp(kind) && !p.pairingId && e.status === 'connected';
      if (isMcp(kind) && !headless) {
        // As the host (AgentService.Start): a user-window task names its approved pairing; without one the kind must be connected.
        if (!p.pairingId) throw E(-32602, 'invalid params', { field: 'pairingId' });
        pairing = A().pairings[p.pairingId];
        if (!pairing) throw E(-32001, 'not_found', { kind: 'pairing', id: p.pairingId });
        if (pairing.status !== 'approved') throw E(-32013, 'invalid_state', { reason: 'pairing_not_approved' });
        if (pairing.kind !== kind) throw E(-32013, 'invalid_state', { reason: 'pairing_mismatch' });
      }
      var scope = p.scope || {};
      ctx.getReport(scope.reportId);
      var g = p.grant || {}, diags = g.diagnostics || [];
      diags.forEach(function (d) { if (!DIAGNOSTICS[d]) throw E(-32602, 'invalid params', { field: 'grant.diagnostics' }); });
      Object.keys(A().tasks).forEach(function (id) { var t = A().tasks[id]; if (t.recipient.kind === kind && t.status === 'active') stopTask(t, 'stopped'); });
      var id = 'task-' + ctx.randomHex(16), now = new Date().toISOString();
      var recipient = { kind: kind, label: e.label, model: p.model || e.defaultModel };
      if (headless) recipient.mode = 'headless';
      var t = { taskId: id, goal: String(p.goal || '').slice(0, 500), recipient: recipient,
        scope: { reportId: scope.reportId, findingIds: scope.findingIds || null },
        grant: { diagnostics: diags.slice(), share: { names: !!(g.share && g.share.names) }, canRequestChanges: !!g.canRequestChanges,
          limits: Object.assign({}, LIMITS, g.limits || {}), idleExpirySeconds: 900 },
        status: 'active', used: { diagnosticCalls: 0, bytesShared: 0, turns: 0, inputTokens: 0, outputTokens: 0 },
        createdAt: now, lastActivityAt: now, pairing: pairing ? { pairingId: pairing.pairingId, client: pairing.client, companionPid: 4242 } : null };
      A().tasks[id] = t; A().activity[id] = []; A().taskEvidence[id] = [];
      ctx.emit(stream(id), 'agent.task', { status: 'active', used: clone(t.used) });
      if (pairing) { setStatus(kind, 'active'); nativeScript(t); }
      return taskView(t);
    }
    function stopTask(t, status) {
      t.status = status;
      ctx.stop(stream(t.taskId));
      (A().activity[t.taskId] || []).forEach(function (a) {
        if (a.status === 'running') { a.status = 'error'; ctx.emit(stream(t.taskId), 'agent.tool', Object.assign(clone(a), { error: 'cancelled' })); }
      });
      var turn = A().turnOf[t.taskId];
      if (turn) { delete A().turnOf[t.taskId]; ctx.emit(stream(t.taskId), 'agent.turn', { turnId: turn.turnId, n: turn.n, status: 'failed' }); }
      ctx.emit(stream(t.taskId), 'agent.task', { status: t.status, used: clone(t.used) });
      // §9: a task ending returns a native client to waiting; an API kind keeps its key status.
      if (isMcp(t.recipient.kind)) {
        var cur = entry(t.recipient.kind).status;
        if (cur === 'active' || cur === 'client_closed' || cur === 'paired') { var b = clientBase(t.recipient.kind, { status: 'waiting', detail: 'idle' }); setStatus(t.recipient.kind, b.status, b.detail); }
      }
    }
    function cancelRun(t) {
      var turn = A().turnOf[t.taskId];
      if (!turn) return false;
      ctx.stop(stream(t.taskId));
      (A().activity[t.taskId] || []).forEach(function (a) {
        if (a.status === 'running') { a.status = 'error'; ctx.emit(stream(t.taskId), 'agent.tool', Object.assign(clone(a), { error: 'cancelled' })); }
      });
      delete A().turnOf[t.taskId];
      ctx.emit(stream(t.taskId), 'agent.turn', { turnId: turn.turnId, n: turn.n, status: 'failed', code: 'cancelled' });
      return true;
    }
    function getTask(id) { var t = A().tasks[id]; if (!t) throw E(-32001, 'not_found', { kind: 'task', id: id }); return t; }

    // One recorded tool call: running, then its result (or a denial / exhausted budget).
    function tool(t, turnId, origin, name, args, run) {
      var lim = t.grant.limits, inv = { invocationId: 'inv-' + ctx.randomHex(8), turnId: turnId, tool: name, arguments: args, status: 'running',
        bytes: 0, evidenceIds: [], origin: origin, at: new Date().toISOString() };
      if (t.status !== 'active') return Promise.resolve(null);
      // A cancelled run (agent.cancel) calls no more tools.
      if (turnId && (!A().turnOf[t.taskId] || A().turnOf[t.taskId].turnId !== turnId)) return Promise.resolve(null);
      var diag = name === 'run_diagnostic';
      if (diag && t.grant.diagnostics.indexOf(args.diagnosticId) < 0) { inv.status = 'denied'; inv.error = 'not_allowed'; }
      else if ((diag && t.used.diagnosticCalls >= lim.diagnosticCalls) || t.used.bytesShared >= lim.totalBytes) { inv.status = 'error'; inv.error = 'exhausted'; }
      A().activity[t.taskId].push(inv);
      ctx.emit(stream(t.taskId), 'agent.tool', clone(inv));
      if (inv.status === 'error') { stopTask(t, 'exhausted'); return Promise.resolve(null); }
      if (inv.status === 'denied') return Promise.resolve(null);
      if (diag) { t.used.diagnosticCalls += 1; ctx.emit(stream(t.taskId), 'agent.task', { status: t.status, used: clone(t.used) }); }
      return run().then(function (res) {
        if (t.status !== 'active' || inv.status !== 'running') return null;
        inv.status = 'complete'; inv.bytes = res.bytes; inv.evidenceIds = res.evidenceIds; if (res.planId) inv.planId = res.planId;
        t.used.bytesShared += res.bytes; t.lastActivityAt = new Date().toISOString();
        ctx.emit(stream(t.taskId), 'agent.tool', clone(inv));
        ctx.emit(stream(t.taskId), 'agent.task', { status: t.status, used: clone(t.used) });
        ctx.save();
        return res;
      });
    }
    // Projection (§3.5): no local evidence, names aliased, display regenerated — here simply the fixture's own sentences.
    function scopedFindings(t, cardFindingId) {
      var r = ctx.getReport(t.scope.reportId), ids = cardFindingId ? [cardFindingId] : t.scope.findingIds;
      return r.findings.filter(function (f) { return !ids || ids.indexOf(f.id) >= 0; }).filter(function (f) { return !f.disposition; });
    }
    function getFindings(t, cardFindingId) {
      var r = ctx.getReport(t.scope.reportId), ev = {};
      r.evidence.forEach(function (e) { ev[e.id] = e; });
      var fs = scopedFindings(t, cardFindingId).slice(0, 20);
      var out = fs.map(function (f) {
        return { id: f.id, ruleId: f.ruleId, category: f.category, title: f.title, severity: f.severity, confidence: f.confidence,
          evidence: f.evidenceIds.map(function (id) { return ev[id]; }).filter(function (e) { return e && e.sensitivity !== 'local'; })
            .map(function (e) { return { id: e.id, source: e.source, observedAt: e.observedAt, quality: e.quality, display: e.display }; }) };
      });
      return wait(stream(t.taskId), 400).then(function () {
        return { bytes: size(out), evidenceIds: [].concat.apply([], out.map(function (f) { return f.evidence.map(function (e) { return e.id; }); })), findings: out };
      });
    }
    function diagnostic(t, id) {
      var collector = DIAGNOSTICS[id], sample = id === 'process.sample';
      var subject = sample ? 'perf.sample.cpu' : id;
      // A sample takes its window (here 10 s); reads take about a second.
      return wait(stream(t.taskId), sample ? SAMPLE_SECONDS * 1000 : 1100).then(function () {
        return ctx.H(t.taskId, collector, subject).then(function (h) {
          var display = sample ? 'App A averaged 31% CPU over ' + SAMPLE_SECONDS + ' s; total CPU 38%.' :
            id === 'storage.volumes' ? 'C: has 11.0 GiB free of 256 GiB; D: has 9.0 GiB free.' :
            id === 'security.status' ? 'Defender is on; the Public firewall profile is off.' :
            id === 'events.recent' ? 'Kernel-Power 41 was logged 3 times in the last 7 days.' :
            id === 'display.modes' ? 'Display 2 is set to 60 Hz; 165 Hz is available.' :
            id === 'power.inventory' ? 'High performance is active on battery; Balanced is installed.' : 'Read ' + id + '.';
          var ev = { id: 'ev-' + h, subject: sample ? 'App A' : subject, source: id, observedAt: new Date().toISOString(), quality: 'measured',
            intervalSeconds: sample ? SAMPLE_SECONDS : undefined, display: display, value: sample ? { cpuPercent: 31.0, totalCpuPercent: 38.0, windowSeconds: SAMPLE_SECONDS } : {} };
          A().taskEvidence[t.taskId].push(ev);
          return { bytes: size(ev), evidenceIds: [ev.id], evidence: ev };
        });
      });
    }
    function pickDiagnostic(t, text, finding) {
      if (/cpu|slow|loud|fan|hot|battery|busy|lag|fps/i.test(text || '')) return 'process.sample';
      return (finding && CATEGORY_DIAG[finding.category]) || t.grant.diagnostics[0] || 'process.sample';
    }
    // The in-app assistant's scripted two-step investigation: read the scoped findings, take one measurement, answer.
    function ask(p) {
      var t = getTask(p.taskId);
      // As the host: an inactive task is refused with its bare status (expired | stopped | exhausted | failed).
      if (t.status !== 'active') throw E(-32013, 'invalid_state', { reason: t.status });
      var headless = t.recipient.mode === 'headless';
      if (!isApi(t.recipient.kind) && !headless) throw E(-32013, 'invalid_state', { reason: 'native_client' });
      var origin = headless ? CLIENT[t.recipient.kind] : 'in_app';
      if (A().turnOf[t.taskId]) throw E(-32013, 'invalid_state', { reason: 'turn_running' });
      // M6a §8: context {benchRunId, part} is validated: the run exists, is done, and has that part.
      if (p.context != null) {
        var run = p.context && ctx.benchRun ? ctx.benchRun(p.context.benchRunId) : null;
        var hasPart = run && run.state === 'done' && run.tests.some(function (x) { return x.part === p.context.part; });
        if (!hasPart) throw E(-32602, 'invalid params', { field: 'context' });
      }
      if (t.used.turns >= t.grant.limits.turns) { stopTask(t, 'exhausted'); throw E(-32013, 'invalid_state', { reason: 'exhausted' }); }
      var turnId = 'turn-' + ctx.randomHex(8), n = t.used.turns + 1, s = stream(t.taskId);
      // As the host (m5 §9): the question joins the task's history with the ask, before its run starts.
      ctx.emit(s, 'agent.question', { turnId: turnId, text: String(p.text || '').trim(), cardId: p.cardId || null });
      if (entry(t.recipient.kind).status !== 'connected') {
        ctx.emit(s, 'agent.turn', { turnId: turnId, n: n, status: 'started' });
        ctx.later(s, function () { ctx.emit(s, 'agent.turn', { turnId: turnId, n: n, status: 'failed' }); }, 300);
        return { taskId: t.taskId, turnId: turnId };
      }
      var fu = ctx.state().followups[t.scope.reportId], card = p.cardId && fu ? fu.cards[p.cardId] : null;
      var rep = ctx.getReport(t.scope.reportId);
      var finding = card ? rep.findings.filter(function (f) { return f.id === card.findingId; })[0] : null;
      // A report-wide question reads the findings its words point at (the fixture's focus rules), else the first ones.
      if (!finding) {
        var cats = (ctx.fixture().focus || []).filter(function (f) { return new RegExp(f[0], 'i').test(p.text || ''); }).map(function (f) { return f[1]; });
        finding = scopedFindings(t, null).filter(function (f) { return cats.indexOf(f.category) >= 0; })[0] || null;
      }
      if (SCAN_ASK.test(p.text || '')) { firstLook(t, turnId, n, origin); return { taskId: t.taskId, turnId: turnId }; }   // starts its own turn
      // M6a §8: benchmark context, the voltage warning and the Run benchmark suggestion get short scripted answers.
      var scripted = benchScript(p);
      if (scripted) {
        t.used.turns = n; t.lastActivityAt = new Date().toISOString();
        A().turnOf[t.taskId] = { turnId: turnId, n: n };
        ctx.emit(s, 'agent.turn', { turnId: turnId, n: n, status: 'started' });
        ctx.emit(s, 'agent.task', { status: t.status, used: clone(t.used) });
        wait(s, 900).then(function () { if (t.status === 'active') answer(t, turnId, n, scripted.text, [], scripted.extra); });
        return { taskId: t.taskId, turnId: turnId };
      }
      t.used.turns = n; t.lastActivityAt = new Date().toISOString();
      A().turnOf[t.taskId] = { turnId: turnId, n: n };
      ctx.emit(s, 'agent.turn', { turnId: turnId, n: n, status: 'started' });
      ctx.emit(s, 'agent.task', { status: t.status, used: clone(t.used) });
      var cited = [], found = null, requested = null;
      wait(s, 500).then(function () {
        return tool(t, turnId, origin, 'get_findings', finding ? { findingIds: [finding.id] } : {}, function () { return getFindings(t, finding && finding.id); });
      }).then(function (res) {
        if (!res) return null;
        found = res; if (res.evidenceIds[0]) cited.push(res.evidenceIds[0]);
        var d = pickDiagnostic(t, p.text, finding || (res.findings[0] && { category: res.findings[0].category }));
        var args = d === 'process.sample' ? { diagnosticId: d, window: SAMPLE_SECONDS } : d === 'events.recent' ? { diagnosticId: d, target: 'system', window: 7 } : { diagnosticId: d };
        return tool(t, turnId, origin, 'run_diagnostic', args, function () { return diagnostic(t, d); });
      }).then(function (res) {
        return maybeRequest(t, turnId, origin, finding, p.text).then(function (plan) { requested = plan; return res; });
      }).then(function (res) {
        if (t.status !== 'active') return;
        // The provider rejected the turn meanwhile (auth / quota / network): the turn fails; the status says why.
        if (entry(t.recipient.kind).status !== 'connected') { delete A().turnOf[t.taskId]; ctx.emit(s, 'agent.turn', { turnId: turnId, n: n, status: 'failed' }); return; }
        if (res) cited.push(res.evidenceIds[0]);
        var f = found && found.findings[0], lines = [];
        if (f) lines.push(f.title + (f.evidence[0] ? ': ' + f.evidence[0].display + ' [' + f.evidence[0].id + ']' : '.'));
        if (res) lines.push('A fresh check: ' + res.evidence.display + ' [' + res.evidence.id + ']');
        else lines.push('I could not take a new measurement with the current permissions.');
        if (requested && requested.status === 'awaiting_user') lines.push('I asked to switch to ' + requested.target.label + '; approve it in the app if you want it.');
        lines.push('Nothing on this PC was changed.');
        answer(t, turnId, n, lines.join(' '), cited);
      });
      return { taskId: t.taskId, turnId: turnId };
    }
    var VOLTAGE_ASK = /undervolt|overclock|voltage|vcore|curve optimi[sz]er/i;
    var BENCH_ASK = /benchmark|how fast is|is my pc (slow|fast)/i;
    function benchScript(p) {
      var text = p.text || '';
      if (p.context && p.context.part === 'gpu') return { text: 'During the GPU test the card reached 87 °C and slowed itself down for about 9 s to stay safe. ' +
        'Check that the fans spin and the dust filters are clean, and keep the GPU on its default fan curve. Nothing on this PC was changed.' };
      if (p.context) return { text: 'The ' + String(p.context.part).toUpperCase() + ' scored lower than your last runs, and a repeat confirmed it. ' +
        'Windows reported a performance limit of 82% (power limited). Check that the PC is plugged in and on a balanced or faster power plan. Nothing on this PC was changed.' };
      if (VOLTAGE_ASK.test(text)) return { text: 'Undervolting runs the CPU or GPU at a lower voltage, which can cut heat and fan noise. ' +
        'Too little voltage makes a PC crash or corrupt data, so every step needs long stability tests. Optimus explains it but never applies it.', extra: { warnings: ['voltage'] } };
      if (BENCH_ASK.test(text)) return { text: 'A benchmark loads the CPU, GPU, RAM and disk for about 2 minutes and compares the result with your last run.', extra: { suggest: 'benchmark' } };
      return null;
    }
    function answer(t, turnId, n, text, cited, extra) {
      var s = stream(t.taskId), cur = A().turnOf[t.taskId];
      if (!cur || cur.turnId !== turnId) return;   // cancelled (agent.cancel) or replaced meanwhile
      delete A().turnOf[t.taskId];
      t.used.inputTokens += 1400; t.used.outputTokens += 160;
      // As the host (m5 §7): the answer's "Next:" line arrives as follow-up questions.
      // M6a §8: warnings ["voltage"] and suggest "benchmark" ride on the message (extra).
      ctx.emit(s, 'agent.message', Object.assign({ turnId: turnId, text: text, citations: cited.filter(Boolean), dropped: 0,
        followUps: ['What should I fix first?', 'Explain that more simply', 'Check this again in a minute'] }, extra || {}));
      ctx.emit(s, 'agent.turn', { turnId: turnId, n: n, status: 'finished', usage: { inputTokens: 1400, outputTokens: 160 } });
      ctx.emit(s, 'agent.task', { status: t.status, used: clone(t.used) });
      ctx.save();
    }

    /* ---------- m5 §5 tools: cards, PC specs, report history, notes and AI cards ---------- */
    var SCAN_ASK = /improve the suggestion cards/i;
    function fuOf(t) { return ctx.state().followups[t.scope.reportId] || null; }
    function cardsOf(t) {
      var fu = fuOf(t); if (!fu) return [];
      var dec = {}; fu.decisions.forEach(function (d) { dec[d.cardId] = d.status; });
      return fu.queue.concat(Object.keys(dec)).map(function (cid) {
        var c = fu.cards[cid]; if (!c) return null;
        var out = { cardId: cid, findingId: c.findingId || null, title: c.title, category: c.category, status: dec[cid] || 'open', origin: c.origin || 'report' };
        if (c.note) out.note = c.note;
        return out;
      }).filter(Boolean);
    }
    function result(t, out) { return wait(stream(t.taskId), 300).then(function () { return { bytes: size(out), evidenceIds: [], result: out }; }); }
    function getCards(t) { return result(t, { reportId: t.scope.reportId, cards: cardsOf(t) }); }
    function getSpecs(t) {
      var S = ctx.state(), best = null;
      Object.keys(S.inventories || {}).forEach(function (id) { var i = S.inventories[id]; if (i.facts && i.facts.length && (!best || i.startedAt > best.startedAt)) best = i; });
      return result(t, { facts: (best ? best.facts : []).map(function (f, i) { return { id: 'spec-' + (i + 1), label: f.group || f.kind, value: f.label }; }) });
    }
    function getHistory(t) {
      var S = ctx.state(), ids = (S.reportOrder || []).slice(0, 5);
      return result(t, { reports: ids.map(function (id) { var r = S.reports[id]; return { reportId: id, generatedAt: r.generatedAt, counts: r.counts }; }), changes: { 'new': [], resolved: [] } });
    }
    // note_card / add_card change the deck the user sees (followup.get carries note and origin).
    function noteCard(t, cid, text) {
      var fu = fuOf(t); if (!fu || !fu.cards[cid]) return Promise.resolve({ bytes: 0, evidenceIds: [] });
      // As the host: the note, who wrote it (provider label) and, internally, the task (never shown).
      var c = fu.cards[cid];
      c.note = String(text).slice(0, 600); c.noteBy = t.recipient.label; c.noteTaskId = t.taskId; fu.revision += 1; ctx.save();
      return Promise.resolve({ bytes: 40, evidenceIds: [], result: { cardId: cid, status: 'noted' } });
    }
    // As the host's Followups.AddAiCard: card-<16 hex> from (report, task, n); text = why; cited IDs must exist for
    // this task or report (others are dropped); the owning task is internal (aiTaskId, stripped from followup views).
    function addCard(t, a, turnId) {
      var fu = fuOf(t); if (!fu) return Promise.resolve({ bytes: 0, evidenceIds: [] });
      var mine = Object.keys(fu.cards).filter(function (k) { return fu.cards[k].origin === 'ai' && fu.cards[k].aiTaskId === t.taskId; }).length;
      if (mine >= 5) return Promise.resolve({ bytes: 0, evidenceIds: [] });
      var rep = ctx.getReport(t.scope.reportId), known = {};
      rep.evidence.forEach(function (e) { known[e.id] = true; });
      (A().taskEvidence[t.taskId] || []).forEach(function (e) { known[e.id] = true; });
      var findingIds = (a.findingIds || []).filter(function (id) { return rep.findings.some(function (f) { return f.id === id; }); }).slice(0, 5);
      var evidenceIds = (a.evidenceIds || []).filter(function (id) { return known[id]; }).slice(0, 5);
      var why = String(a.why || '').slice(0, 400);
      return ctx.H(t.scope.reportId, 'ai', t.taskId, String(mine)).then(function (h) {
        // Stopped or cancelled while the ID was being made: the card is not added.
        var run = A().turnOf[t.taskId];
        if (t.status !== 'active' || (turnId && (!run || run.turnId !== turnId))) return { bytes: 0, evidenceIds: [] };
        var cid = 'card-' + h;
        fu.cards[cid] = { id: cid, findingId: findingIds[0] || null, category: a.category || 'misc', title: String(a.title).slice(0, 120), text: why, why: why,
          evidenceIds: evidenceIds, options: ['Ask about this', 'Ignore'], guide: null, preview: null, actionId: null, settingsUri: null,
          origin: 'ai', findingIds: findingIds, addedBy: t.recipient.label, aiTaskId: t.taskId };
        fu.queue.push(cid); fu.total += 1; fu.revision += 1; ctx.save();
        return { bytes: 60, evidenceIds: [], result: { cardId: cid, status: 'added' } };
      });
    }
    // The first-connect scan (m5 §6): cards, specs, history, findings; one note and one new card; the three things that matter.
    function firstLook(t, turnId, n, origin) {
      var s = stream(t.taskId), cards = null, found = null;
      t.used.turns = n; t.lastActivityAt = new Date().toISOString();
      A().turnOf[t.taskId] = { turnId: turnId, n: n };
      ctx.emit(s, 'agent.turn', { turnId: turnId, n: n, status: 'started' });
      ctx.emit(s, 'agent.task', { status: t.status, used: clone(t.used) });
      wait(s, 500).then(function () { return tool(t, turnId, origin, 'get_cards', {}, function () { return getCards(t); }); })
        .then(function (r) { cards = r && r.result.cards; return tool(t, turnId, origin, 'get_pc_specs', {}, function () { return getSpecs(t); }); })
        .then(function () { return tool(t, turnId, origin, 'get_report_history', { limit: 5 }, function () { return getHistory(t); }); })
        .then(function () { return tool(t, turnId, origin, 'get_findings', {}, function () { return getFindings(t, null); }); })
        .then(function (r) {
          found = r;
          var first = (cards || []).filter(function (c) { return c.status === 'open'; })[0];
          if (!first) return null;
          return tool(t, turnId, origin, 'note_card', { cardId: first.cardId }, function () {
            return noteCard(t, first.cardId, 'Worth doing first: it is the change you will notice most day to day.');
          });
        })
        .then(function () {
          // It cites what it read: the first finding's evidence, plus this task's own measurement when it has one.
          var f0 = found && found.findings[0], cite = [];
          if (f0 && f0.evidence[0]) cite.push(f0.evidence[0].id);
          var mine = A().taskEvidence[t.taskId] || [];
          if (mine.length) cite.push(mine[mine.length - 1].id);
          var args = { title: 'Restart once after the pending updates', why: 'Several checks read values that settle only after a restart.', category: 'misc', evidenceIds: cite };
          return tool(t, turnId, origin, 'add_card', args, function () { return addCard(t, args, turnId); });
        })
        .then(function () {
          if (t.status !== 'active') return;
          if (entry(t.recipient.kind).status !== 'connected') { delete A().turnOf[t.taskId]; ctx.emit(s, 'agent.turn', { turnId: turnId, n: n, status: 'failed' }); return; }
          var fs = (found && found.findings) || [], cited = [];
          var lines = fs.slice(0, 3).map(function (f, i) {
            var ev = f.evidence[0]; if (ev) cited.push(ev.id);
            return (i + 1) + '. ' + f.title + (ev ? ' [' + ev.id + ']' : '');
          });
          lines.push('I added a note to the first card and one new card.');
          answer(t, turnId, n, lines.join('\n'), cited);
        });
    }
    // A paired native client reads findings and takes a measurement through the optimizer.
    function nativeScript(t) {
      var origin = t.pairing.client;
      wait(stream(t.taskId), 1500).then(function () {
        return tool(t, null, origin, 'get_findings', {}, function () { return getFindings(t, null); });
      }).then(function (res) {
        if (!res) return null;
        var d = t.grant.diagnostics.indexOf('power.inventory') >= 0 ? 'power.inventory' : t.grant.diagnostics[0];
        if (!d) return null;
        return tool(t, null, origin, 'run_diagnostic', { diagnosticId: d }, function () { return diagnostic(t, d); });
      }).then(function () { return maybeRequest(t, null, origin, null, 'battery'); });
    }
    // With "Can request changes", the assistant asks for the power plan change (M4 request_action); the user approves in the app.
    function maybeRequest(t, turnId, origin, finding, text) {
      if (!t.grant.canRequestChanges || !ctx.actions || t.status !== 'active') return Promise.resolve(null);
      var fits = (finding && finding.ruleId === 'power.high_perf_on_battery') || /battery|loud|quiet|fan|power/i.test(text || '');
      if (!fits) return Promise.resolve(null);
      var plan = null, args = { actionId: 'power.select_scheme', parameters: { personality: 'balanced' } };
      return tool(t, turnId, origin, 'request_action', args, function () {
        return ctx.actions.requestFromAgent(t, 'power.select_scheme', { personality: 'balanced' }, finding && finding.id, null).then(function (pl) {
          plan = pl; return { bytes: 180, evidenceIds: [], planId: pl.planId };
        });
      }).then(function () { return plan; });
    }

    /* ---------- one-time report review ---------- */
    function projection(reportId, findingIds) {
      var r = ctx.getReport(reportId), ev = {};
      r.evidence.forEach(function (e) { ev[e.id] = e; });
      var fs = r.findings.filter(function (f) { return !findingIds || findingIds.indexOf(f.id) >= 0; });
      return { reportId: reportId, generatedAt: r.generatedAt, findings: fs.map(function (f) {
        return { id: f.id, ruleId: f.ruleId, category: f.category, title: f.title, severity: f.severity,
          evidence: f.evidenceIds.map(function (id) { return ev[id]; }).filter(function (e) { return e && e.sensitivity !== 'local'; })
            .map(function (e) { return { id: e.id, source: e.source, observedAt: e.observedAt, quality: e.quality, display: e.display }; }) };
      }) };
    }
    function preview(p) {
      var kind = p.recipientKind;
      if (!isApi(kind) || entry(kind).status !== 'connected') throw E(-32013, 'invalid_state', { reason: 'not_connected' });
      var payload = projection(p.reportId, p.findingIds || null), bytes = size(payload);
      var measurements = payload.findings.reduce(function (n, f) { return n + f.evidence.length; }, 0);
      return ctx.H(JSON.stringify(payload)).then(function (digest) {
        var id = 'rev-' + ctx.randomHex(16);
        var pv = { previewId: id, recipientKind: kind, reportId: p.reportId, expiresAt: new Date(Date.now() + 600000).toISOString(), digest: digest,
          payload: payload, bytes: bytes, consumed: false,
          fieldClasses: [{ id: 'findings', label: 'Findings', count: payload.findings.length, shared: true },
            { id: 'measurements', label: 'Measurements and times', count: measurements, shared: true },
            { id: 'names', label: 'App names', shared: false },
            { id: 'identifiers', label: 'Paths, accounts, addresses', shared: false, locked: true }] };
        A().previews[id] = pv;
        var v = clone(pv); delete v.consumed; delete v.recipientKind; return v;
      });
    }
    function submit(p) {
      var pv = A().previews[p.previewId];
      if (!pv) throw E(-32001, 'not_found', { kind: 'preview', id: p.previewId });
      if (pv.consumed) throw E(-32013, 'invalid_state', { reason: 'preview_consumed' });
      if (Date.parse(pv.expiresAt) < Date.now()) throw E(-32013, 'invalid_state', { reason: 'preview_expired' });
      pv.consumed = true;
      var shared = [].concat.apply([], pv.payload.findings.map(function (f) { return f.evidence.map(function (e) { return e.id; }); }));
      var sc = ctx.state().scans[ctx.getReport(pv.reportId).scanId];
      var lines = ctx.fixture().mockAiSummary.map(function (l) {
        return { text: l.text, evidenceIds: l.evidence.map(function (k) { return sc && sc.keys[k] ? 'ev-' + sc.keys[k] : null; }).filter(function (id) { return id && shared.indexOf(id) >= 0; }) };
      }).filter(function (l) { return l.evidenceIds.length; });
      return wait('review', 900).then(function () { return { lines: lines, usage: { inputTokens: 900, outputTokens: 220 }, dropped: 0 }; });
    }

    // A reload is a restart: native bridges and in-flight turns are gone.
    function restart() {
      var a = A();
      Object.keys(a.providers).forEach(function (k) {
        var st = a.providers[k].status;
        if (st === 'pending' || st === 'active' || st === 'client_closed') a.providers[k] = Object.assign(clientBase(k, { status: 'waiting', detail: 'idle' }), { since: new Date().toISOString() });
        if (st === 'checking') a.providers[k] = { status: a.keys[k] ? 'connected' : 'not_configured', since: new Date().toISOString() };
      });
      Object.keys(a.pairings).forEach(function (id) { if (a.pairings[id].status === 'pending') a.pairings[id].status = 'expired'; });
      // A turn that was running ends failed, so its history doesn't show it as still going.
      Object.keys(a.turnOf || {}).forEach(function (id) {
        var turn = a.turnOf[id], t = a.tasks[id];
        if (turn && t) ctx.emit(stream(id), 'agent.turn', { turnId: turn.turnId, n: turn.n, status: 'failed' });
      });
      a.turnOf = {};
      Object.keys(a.activity).forEach(function (id) { a.activity[id].forEach(function (x) { if (x.status === 'running') x.status = 'error'; }); });
    }

    // Dev hook (fixture only): put a provider in any §9 state, as the backend would after events the UI cannot cause.
    function setProviderState(p) {
      var kind = p.kind; entry(kind);
      if (isApi(kind)) {
        if (p.status === 'not_configured') delete A().keys[kind];
        else if (p.status === 'error' && (p.detail === 'auth' || p.detail === 'quota')) A().keys[kind] = p.detail;
        else A().keys[kind] = true;
        setStatus(kind, p.status, p.detail);
      } else if (isMcp(kind)) {
        A().manual = !p.auto;      // states set by hand stay put (no automatic pairing)
        ctx.stop('pair:' + kind);
        if (p.status === 'pending') { setStatus(kind, 'waiting', 'idle'); schedulePairing(kind, 0); }
        else if (p.status === 'paired' || p.status === 'active' || p.status === 'client_closed') {
          var has = Object.keys(A().pairings).some(function (id) { return A().pairings[id].kind === kind && A().pairings[id].status === 'approved'; });
          if (!has) { var id = 'pair-' + ctx.randomHex(8); A().pairings[id] = { pairingId: id, kind: kind, client: CLIENT[kind], code: '0000', status: 'approved', createdAt: new Date().toISOString() }; }
          setStatus(kind, p.status, p.detail);
          // A closed client pairs again on its next call.
          if (p.status === 'client_closed') schedulePairing(kind, 8000);
        } else setStatus(kind, p.status, p.detail);
      } else setStatus(kind, p.status, p.detail);
      return entry(kind);
    }

    // Fixture stand-ins for the shell's one-click connect (onboarding §2): Claude Code installed, Codex not, by default.
    var CLIENTS = { claude_code: { installed: true, version: '2.1.0', signedIn: true, auth: 'subscription' }, codex: { installed: false, signedIn: false } };
    function clientRec(c) {
      if (!CLIENTS[c]) throw E(-32602, 'invalid params', { field: 'client' });
      var a = A(); a.clients = a.clients || {};
      return a.clients[c] = a.clients[c] || Object.assign({ registered: 'no', failNext: false }, CLIENTS[c]);
    }
    function clientCommand(c) { return c === 'codex' ? 'codex mcp add optimization-tool -- "' + INSTALL + '"' : 'claude mcp add --scope user optimization-tool -- "' + INSTALL + '"'; }
    function clientStatus(p) {
      var r = clientRec(p.client);
      // As the shell (onboarding §2): a CLI this PC won't run is reported blocked + blockedReason, with installed:false and no paths.
      var usable = !!r.installed && !r.blocked;
      var out = { installed: usable, registered: usable ? r.registered : 'unknown', command: clientCommand(p.client) };
      if (usable && r.version) out.version = r.version;
      if (usable) out.source = r.source || 'path';
      if (usable) out.signedIn = r.signedIn == null ? null : !!r.signedIn;
      if (!usable && p.client === 'claude_code' && r.app) out.app = true;
      if (r.blocked) { out.blocked = true; out.blockedReason = r.blockedReason || 'launch_failed'; }
      if (usable && r.outdated) out.outdated = true;
      // What a sign-in pays with, only when signed in.
      if (usable && out.signedIn === true && r.auth) out.auth = r.auth;
      // As the shell: provider.setClient after every answer.
      r.reported = true; syncClient(p.client === 'codex' ? 'codex_mcp' : 'claude_code_mcp');
      return out;
    }
    function registerClient(p) {
      var r = clientRec(p.client);
      return wait('client:' + p.client, 600).then(function () {
        if (!r.installed) return { status: 'not_installed' };
        if (r.failNext) { r.failNext = false; return { status: 'failed', error: 'exit_1' }; }
        if (r.cancelNext) { r.cancelNext = false; return { status: 'cancelled' }; }
        r.registered = 'yes'; ctx.save();
        return { status: 'added' };
      });
    }

    // Stand-in for the official installer in a PowerShell window: the CLI appears a few seconds later.
    function installClient(p) {
      var r = clientRec(p.client);
      if (r.cancelNext) { r.cancelNext = false; return { status: 'cancelled' }; }
      // The official installer also replaces an outdated CLI and gives a working one where the old install was blocked.
      if (!r.installed || r.blocked || r.outdated) setTimeout(function () {
        r.installed = true; r.version = r.outdated || !r.version ? '2.1.0' : r.version; r.source = 'installer';
        r.blocked = false; r.blockedReason = null; r.outdated = false; ctx.save();
      }, r.installDelay != null ? r.installDelay : 3000);
      return { status: 'started' };
    }

    return {
      __restart: restart,
      'shell.installClient': installClient,
      'shell.clientStatus': clientStatus,
      'shell.registerClient': registerClient,
      // Stand-in for the sign-in console (m5 §2): signed in a few seconds later; the app's next clientStatus sees it.
      'shell.signInClient': function (p) {
        var r = clientRec(p.client);
        if (!r.installed || r.blocked) return { status: 'failed', error: 'not_installed' };
        if (r.failNext) { r.failNext = false; return { status: 'failed', error: 'exit_1' }; }
        if (!r.signedIn) ctx.later('signin:' + p.client, function () { r.signedIn = true; ctx.save(); }, r.signInDelay != null ? r.signInDelay : 3000);
        return { status: 'started' };
      },
      'shell.unregisterClient': function (p) { clientRec(p.client).registered = 'no'; return { status: 'removed' }; },
      'fixture.setSignIn': function (p) { A().signInResult = p.result || 'ok'; return { ok: true }; },
      'fixture.setKeyResult': function (p) { A().keyResult = p.result || 'ok'; return { ok: true }; },
      // Dev hook: { client, installed?, registered?, app?, signedIn? (true|false|null), auth? ('subscription'|'api_key'|'other'),
      //   outdated?, blocked?, blockedReason? (policy|access_denied|node_missing|unsupported_path|launch_failed), installDelay?, signInDelay?, failNext?, cancelNext? }
      'fixture.setClient': function (p) { var r = clientRec(p.client); ['installed', 'registered', 'app', 'signedIn', 'auth', 'outdated', 'blocked', 'blockedReason', 'installDelay', 'signInDelay', 'failNext', 'cancelNext'].forEach(function (k) { if (p[k] !== undefined) r[k] = p[k]; }); return clientStatus(p); },
      'fixture.setProviderState': setProviderState,
      // Dev hook: { kind, tools:false } makes an in-app account explain-only (no diagnostics, no change requests).
      'fixture.setTools': function (p) { var a = A(); a.noTools = a.noTools || {}; a.noTools[p.kind] = p.tools === false; return entry(p.kind); },
      // As the host: the diagnostics this build can run, for the grant sheet.
      'provider.list': function () { return { providers: PROVIDERS.map(function (p) { return entry(p.kind); }),
        diagnostics: Object.keys(DIAGNOSTICS).map(function (id) { return { id: id, description: id, arguments: {} }; }) }; },
      'provider.connect': connect,
      'provider.disconnect': disconnect,
      'shell.requestApiKey': requestApiKey,
      'agent.approvePairing': approvePairing,
      'agent.start': function (p) { return ctx.withIdem('agent.start', p, start); },
      // As the host: the task with its stream's last 100 events (tool calls, turns, answers) and its own evidence.
      'agent.get': function (p) {
        var t = getTask(p.taskId), h = (A().history || {})[t.taskId];
        var activity = h ? h.slice(-100) : (A().activity[t.taskId] || []).concat(((A().messages || {})[t.taskId]) || []).slice(-100);   // state saved before history
        return Object.assign(taskView(t), { activity: clone(activity), evidence: clone(A().taskEvidence[t.taskId] || []) });
      },
      // As the host: active tasks, newest first, so the app can re-attach after a restart.
      'agent.active': function () {
        var list = Object.keys(A().tasks).reverse().map(function (id) { return A().tasks[id]; }).filter(function (t) { return t.status === 'active'; });
        list.sort(function (a, b) { return a.createdAt < b.createdAt ? 1 : a.createdAt > b.createdAt ? -1 : 0; });
        return { tasks: list.map(taskView) };
      },
      // Dev hook: the task idles out (as the host's idle expiry: no event until the next call finds it expired).
      'fixture.expireTask': function (p) { var t = getTask(p.taskId); if (t.status === 'active') { t.status = 'expired'; ctx.stop(stream(t.taskId)); delete A().turnOf[t.taskId]; } return taskView(t); },
      'agent.ask': function (p) { return ctx.withIdem('agent.ask', p, ask); },
      'agent.stop': function (p) { var t = getTask(p.taskId); if (t.status === 'active') stopTask(t, 'stopped'); return taskView(t); },
      // m5 §8: ends only the running turn or run (failed, code cancelled); the task stays active.
      'agent.cancel': function (p) { var t = getTask(p.taskId); return { taskId: t.taskId, cancelled: cancelRun(t) }; },
      'review.preview': preview,
      'review.submit': function (p) { return ctx.withIdem('review.submit', p, submit); },
      'review.export': function (p) {
        var payload = projection(p.reportId, p.findingIds || null), id = 'r-' + ctx.randomHex(8);
        return { reviewId: id, json: JSON.stringify({ v: 1, reviewId: id, findings: payload.findings }) };
      }
    };
  }

  createFixtureAgent.DIAGNOSTICS = Object.keys(DIAGNOSTICS);
  if (root) { root.OptTool = root.OptTool || {}; root.OptTool.createFixtureAgent = createFixtureAgent; }
  if (typeof module !== 'undefined' && module.exports) module.exports = { createFixtureAgent: createFixtureAgent };
})(typeof window !== 'undefined' ? window : null);
