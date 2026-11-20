/* One function for every AI connection state (contracts/m3-agent.md §9). The pill, the chooser tiles, the pill
   detail and the asking paths render only from what it returns. Pure; loadable from Node for tests. */
(function (root) {
  'use strict';

  // In-app kinds: API keys, and the ChatGPT plan (browser sign-in instead of a key).
  function isApi(k) { return k === 'anthropic_api' || k === 'openai_api' || k === 'chatgpt_plan'; }
  function isMcp(k) { return k === 'claude_code_mcp'; }
  // The program that runs in the background (named only where the user acts on it: install, update, its own window).
  var CLIENT = { claude_code_mcp: 'Claude Code' };
  var CLI = { claude_code_mcp: 'claude' };
  // What users see for each AI: Claude Code is shown as "Claude" (the user's Claude plan), whatever label the host sends.
  var AI_NAME = { claude_code_mcp: 'Claude', chatgpt_plan: 'ChatGPT' };
  function aiName(kind, label) {
    if (kind === 'claude_code_mcp') return AI_NAME[kind];
    if (label === 'Claude Code') return 'Claude';
    return label || AI_NAME[kind] || null;
  }

  /* ---------- The picker's ids (design: claude | chatgpt | api-key | local) and the stored onboarding values ---------- */
  // onboarding profile provider.value stays claude-code | chatgpt | api | local | later; only the picker's ids differ.
  var TILE_OF_VALUE = { 'claude-code': 'claude', chatgpt: 'chatgpt', api: 'api-key', local: 'local', later: 'local' };
  var VALUE_OF_TILE = { claude: 'claude-code', chatgpt: 'chatgpt', 'api-key': 'api', local: 'local', later: 'later' };
  var KIND_OF_TILE = { claude: 'claude_code_mcp', chatgpt: 'chatgpt_plan' };
  var TILE_OF_KIND = { claude_code_mcp: 'claude', chatgpt_plan: 'chatgpt', anthropic_api: 'api-key', openai_api: 'api-key' };
  // A stored value (or null) → the picker tile it selects; anything retired or unknown selects nothing.
  function pickerTile(value) { return TILE_OF_VALUE[value] || null; }
  // A picker tile (or an already stored value) → the value onboarding.save stores.
  function storedValue(v) { return VALUE_OF_TILE[v] || (TILE_OF_VALUE[v] ? v : null); }
  // A tile → its provider kind (api-key needs the Anthropic / OpenAI choice); a kind → its tile.
  function tileKind(tile, api) { return tile === 'api-key' ? (api === 'openai' ? 'openai_api' : 'anthropic_api') : KIND_OF_TILE[tile] || null; }
  function kindTile(kind) { return TILE_OF_KIND[kind] || null; }
  // One-time migration: Codex was removed (it used the same ChatGPT plan). A user whose saved AI is Codex lands on the
  // picker with nothing selected, then goes on where they were: main once onboarding is complete, else their step.
  var RETIRED = { codex: 1 };
  function retiredProvider(pr) { return !!(pr && RETIRED[pr.value]); }
  function retiredLanding(o) {
    if (!o || !retiredProvider(o.profile && o.profile.provider)) return null;
    var step = o.step;
    return { stage: 'login', returnTo: step === 'complete' ? 'main' : !step || step === 'provider' ? null : STEP_STAGE[step] || null };
  }
  // A retired AI's user who resumed into a running scan or inventory sees that view first; the picker then comes before
  // the next stage the app moves to (where it would otherwise ask the AI), and they go on there afterwards.
  // hold: retiredLanding's result while it waits; stage: {id, leaving}. → {stage:'login', returnTo} | {clear:true} | null.
  var HOLD_VIEWS = { boot: 1, scanning: 1, specs: 1 };
  function retiredRedirect(hold, stage) {
    if (!hold || !stage || stage.leaving) return null;
    if (stage.id === 'login') return { clear: true };   // already on the picker
    if (HOLD_VIEWS[stage.id]) return null;
    return { stage: 'login', returnTo: stage.id };
  }

  /**
   * @param {{kind:(string|null), local?:boolean, entry?:{status:string, detail?:string, code?:string, label?:string}|null,
   *          label?:string, backend?:string, task?:{status:string, recipient:{kind:string}}|null}} s
   * @returns {{label:string, word:(string|null), tone:string, action:(string|null), usable:boolean, hint:(string|null),
   *            command:boolean, disconnect:boolean, row:string, client:(string|null), cli:(string|null)}}
   *   action ∈ addKey | updateKey | retry | signIn | install | approve | start | activity | askAgain | showCommand | null
   *   tone   ∈ local | connected | live | expired (attention)
   */
  function connectionState(s) {
    var kind = s.local ? null : s.kind, e = s.entry || null;
    var label = aiName(kind, (e && e.label) || s.label) || 'Local only';
    var out = { label: label, word: null, tone: 'local', action: null, usable: false, hint: null, command: false, disconnect: false,
      row: 'local', client: CLIENT[kind] || null, cli: CLI[kind] || null };
    if (!kind) { out.label = 'Local only'; out.action = null; }
    else if (!e) { out.word = 'Checking…'; out.row = 'unknown'; }
    else if (isApi(kind)) {
      var st = e.status, d = e.detail;
      out.disconnect = st !== 'not_configured';
      var plan = kind === 'chatgpt_plan', bad = st === 'error';
      function err(row, word, action, hint) { out.row = row; out.word = word; out.tone = 'expired'; out.action = action; out.hint = hint || null; }
      // detail (m3 §9): auth | quota | network | unavailable | not_eligible | storage | callback_timeout | cancelled; connected may say not_saved.
      if (st === 'connected') {
        out.row = 'connected'; out.word = 'Connected'; out.tone = 'connected'; out.usable = true;
        if (d === 'not_saved') out.hint = 'Works now, but this PC didn’t save the key. You’ll add it again next time.';
      }
      else if (st === 'checking') { out.row = 'checking'; out.word = 'Checking…'; out.disconnect = !plan; } // nothing to sign out of mid sign-in
      else if (plan && bad && d === 'not_eligible') { err('not_eligible', 'Not available', null, 'This ChatGPT account can’t be used here. Try another account or pick another AI.'); out.disconnect = false; }
      else if (plan && bad && d === 'auth') err('auth', 'Signed out', 'signIn', 'Sign in to ChatGPT again.');
      else if (plan && bad && d === 'quota') err('quota', 'Limit reached', 'retry', 'Your ChatGPT plan’s limit is used up for now.');
      else if (plan && bad && d === 'callback_timeout') { err('callback_timeout', 'Not finished', 'signIn', 'The browser didn’t come back to the app. Try again, or pick another AI.'); out.disconnect = false; }
      else if (plan && bad && d === 'cancelled') { err('cancelled', 'Not signed in', 'signIn', null); out.tone = 'local'; out.disconnect = false; }
      else if (plan && bad && d === 'storage') { err('storage', 'Not saved', 'signIn', 'Signed in, but this PC didn’t let the app save it. Try again.'); out.disconnect = false; }
      else if (plan && st === 'not_configured') { out.row = 'not_configured'; out.word = 'Not signed in'; out.action = 'signIn'; out.disconnect = false; }
      else if (bad && d === 'auth') err('auth', 'Key rejected', 'updateKey', 'The key wasn’t accepted. Check it and try again.');
      else if (bad && d === 'quota') err('quota', 'No credit', 'updateKey', 'The account has no credit left.');
      else if (bad && d === 'not_eligible') err('not_eligible', 'Not available', 'updateKey', 'This account can’t use it here.');
      else if (bad && d === 'storage') { err('storage', 'Not saved', 'addKey', 'This PC didn’t let the app save the key. Try again.'); out.disconnect = false; }
      else if (bad && d === 'unavailable') err('busy', 'Busy', 'retry', 'The service didn’t answer. Try again in a moment.');
      else if (bad) err('offline', 'Offline', 'retry', 'Can’t reach it. Check your internet and try again.');
      else { out.row = 'not_configured'; out.word = 'Not set up'; out.action = 'addKey'; out.disconnect = false; }
    } else if (isMcp(kind)) {
      var ms = e.status, md = e.detail;
      // An MCP task that is active counts as active even if the entry lags behind (older backends said "paired").
      var live = s.task && s.task.status === 'active' && s.task.recipient && s.task.recipient.kind === kind;
      out.disconnect = true;
      // A headless task (m5 §3) is asked in the app, whatever the entry says while it runs.
      if (live && s.task.recipient.mode === 'headless' && (ms === 'active' || ms === 'paired' || ms === 'connected')) { out.row = 'connected'; out.word = 'Connected'; out.tone = 'connected'; out.usable = true; }
      else if (ms === 'pending') { out.row = 'pending'; out.word = 'Approve' + (e.code ? ' ' + e.code : ''); out.tone = 'expired'; out.action = 'approve'; }
      else if (ms === 'client_closed') { out.row = 'client_closed'; out.word = 'Closed'; out.tone = 'expired'; out.action = 'askAgain'; out.hint = 'Ask ' + out.client + ' again'; }
      else if (ms === 'error' && md === 'bridge_rejected') { out.row = 'bridge_rejected'; out.word = 'Setup issue'; out.tone = 'expired'; out.action = 'showCommand'; out.command = true; }
      else if (ms === 'active' || (live && ms === 'paired')) { out.row = 'active'; out.word = 'Active'; out.tone = 'live'; out.action = 'activity'; }
      else if (ms === 'paired') { out.row = 'paired'; out.word = 'Connected'; out.tone = 'connected'; out.action = 'start'; }
      // m5 §1: the CLI is installed and signed in, so the app runs it in the background (headless).
      else if (ms === 'connected') { out.row = 'connected'; out.word = 'Connected'; out.tone = 'connected'; out.usable = true; }
      else if (ms === 'error' && md === 'signed_out') { out.row = 'signed_out'; out.word = 'Signed out'; out.tone = 'expired'; out.action = 'signIn'; }
      // A CLI that is there but cannot run (policy, access), or only an outdated one: the chooser explains and offers the next step.
      // error/unavailable: the shell found an outdated CLI, or one this PC won't run; the chooser says which and how to fix it.
      else if (ms === 'error' && md === 'unavailable') { out.row = 'unavailable'; out.word = 'Can’t run'; out.tone = 'expired'; out.action = 'install'; out.disconnect = false; }
      // No CLI reported: nothing to disconnect yet; the chooser installs it.
      else if (ms === 'waiting' && md === 'never_seen') { out.row = 'not_set_up'; out.word = 'Not set up'; out.action = 'install'; out.disconnect = false; }
      else {
        // waiting (never_seen / idle), and error pairing_expired / denied, which go back to waiting.
        out.row = 'waiting'; out.word = 'Waiting'; out.action = 'showCommand'; out.command = true; out.hint = 'Ask ' + out.client + ' about your PC';
      }
    }
    if (s.backend === 'reconnecting') { out.word = 'Reconnecting…'; out.action = null; out.usable = false; }
    return out;
  }

  // Where a question goes: "agent" (in-app assistant), "local" (templates and re-ranking only), or the row's action
  // to offer instead. Local answers keep working in every case.
  function routeAsk(st) {
    if (st.row === 'local') return 'local';
    if (st.usable) return 'agent';
    if (st.row === 'active') return 'local';            // a native client is asked in its own window
    return st.action || 'local';
  }

  // An ask on a task that is no longer active: -32013 with data.reason = the task's status (the host sends the bare
  // status; task_* is still accepted). The UI then starts a fresh task instead of "No answer".
  var ENDED = { expired: 1, stopped: 1, exhausted: 1, failed: 1 };
  function taskEnded(err) {
    if (!err || err.code !== -32013) return false;
    var r = (err.data && err.data.reason) || '';
    return !r || !!ENDED[r] || /^task_/.test(r);
  }

  // A task's asks from its agent.get history ({seq, type, payload}), in order. One ask (turnId) spans several provider
  // turns (n grows): each ends `finished` before the tools it asked for run, so `finished` alone is not the end.
  // An ask is running until its agent.message (answered), agent.turn failed (failed / cancelled) or the task ending.
  // tool: the ask's last running tool call, for the busy word. question: the asked text and card (m5 §9 agent.question,
  // written with the ask, before its turn starts); at: when it was asked (or first seen).
  function turnsFromHistory(list) {
    var out = [], by = {};
    (list || []).forEach(function (e) {
      if (!e || !e.type || !e.payload) return;
      var pl = e.payload;
      if (e.type === 'agent.task') {
        if (pl.status && pl.status !== 'active') out.forEach(function (t) { if (t.status === 'running') { t.status = 'failed'; t.tool = null; } });
        return;
      }
      var id = pl.turnId || (e.type === 'agent.message' ? 'h-' + e.seq : null);
      if (!id || (e.type !== 'agent.turn' && e.type !== 'agent.message' && e.type !== 'agent.tool' && e.type !== 'agent.question')) return;
      var t = by[id];
      if (!t) {
        if (e.type === 'agent.tool') return;
        t = by[id] = { turnId: id, status: 'running', firstSeq: e.seq || 0, at: e.at || null, message: null, tool: null, question: null, begun: false };
        out.push(t);
      }
      if (e.type === 'agent.question') { t.question = { text: String(pl.text || ''), cardId: pl.cardId || null }; return; }
      t.begun = true;
      if (e.type === 'agent.message') { t.status = 'answered'; t.message = pl; t.tool = null; }
      else if (e.type === 'agent.tool') { if (t.status === 'running') t.tool = pl.status === 'running' ? pl : null; }
      else if (t.status === 'running' && pl.status === 'failed') { t.status = pl.code === 'cancelled' ? 'cancelled' : 'failed'; t.tool = null; t.code = pl.code || null; }
    });
    // A question whose run never began (the host stopped in between) is over once a later ask exists.
    out.forEach(function (t, i) { if (t.status === 'running' && !t.begun && i < out.length - 1) t.status = 'failed'; });
    return out;
  }
  function runningTurn(list) {
    var r = turnsFromHistory(list).filter(function (t) { return t.status === 'running'; }).pop();
    return r ? r.turnId : null;
  }

  /**
   * Brings the answer list in line with a task's history (after a reload or a reconnect). Answers belong to a task
   * (taskId): unanswered entries of another task are dropped; this task's entries get the answer, failure or cancel
   * they missed; turns not on screen are added (answered or still running) unless they are older than afterSeq.
   * opts: { taskId, reportId, afterSeq?, stale?, strip?(text), busyWord?(toolPayload), others?:'keep', drop?(entry) }
   *   stale: the snapshot is older than live events already applied; it only adds what ended (answers, failures).
   *   others 'keep': entries of other tasks stay as they are (an older task's history merged in beside the current one).
   *   drop: entries it returns true for are left out (deleted from History).
   * Entries added from the history carry the question (q, cardId), when it was asked (at), and restored:true when
   * already answered.
   */
  var KEEP = 40;
  function reconcileAnswers(list, history, opts) {
    var turns = turnsFromHistory(history), by = {}, seen = {}, tid = opts.taskId;
    var strip = opts.strip || function (x) { return x; }, word = opts.busyWord || function () { return 'Thinking…'; };
    var drop = opts.drop || function () { return false; };
    turns.forEach(function (t) { by[t.turnId] = t; });
    function answered(a, t) {
      var m = t.message;
      return Object.assign({}, a, { turnId: t.turnId, text: strip(m.text || ''), citations: m.citations || [], followUps: m.followUps || [],
        warnings: m.warnings || [], suggest: m.suggest || null, sources: (m.sources || []).slice(0, 8), busy: null, failedTurn: false });
    }
    // The question from the history fills an entry that lost it (a reload, another window).
    function asked(a, t) {
      if (!t.question) return a;
      var n = Object.assign({}, a);
      if (n.q == null) n.q = t.question.text;
      if (n.cardId == null) n.cardId = t.question.cardId;
      if (!n.at && t.at) n.at = t.at;
      return n;
    }
    var out = [];
    (list || []).forEach(function (a) {
      var pending = a.text == null && !a.failedTurn;
      if (a.taskId !== tid) { if (!pending || opts.others === 'keep') out.push(a); return; }
      var t = a.turnId ? by[a.turnId] : null;
      if (a.turnId) seen[a.turnId] = true;
      if (!t) { out.push(a); return; }
      a = asked(a, t);
      if (t.status === 'answered') out.push(a.text == null ? answered(a, t) : a);
      else if (t.status === 'cancelled') { if (!pending) out.push(a); }
      else if (t.status === 'failed') out.push(pending ? Object.assign({}, a, { failedTurn: true, failCode: t.code || null, busy: null }) : a);
      else out.push(pending && !opts.stale ? Object.assign({}, a, { busy: t.tool ? word(t.tool) : a.busy || 'Thinking…' }) : a);
    });
    turns.forEach(function (t) {
      if (seen[t.turnId] || (t.status !== 'answered' && t.status !== 'running')) return;
      // An older snapshot than what live events already showed never brings back a run.
      if (opts.stale && t.status === 'running') return;
      if (opts.afterSeq != null && t.firstSeq <= opts.afterSeq) return;
      // An ask whose reply was lost (no turnId yet) is this turn.
      var j = -1;
      for (var i = out.length - 1; i >= 0; i--) if (out[i].taskId === tid && !out[i].turnId && (out[i].text == null)) { j = i; break; }
      // restored: an answer that had already ended (it waits in History); a run still going shows as a panel.
      var base = j >= 0 ? out[j] : { id: 'h-' + t.turnId, q: null, cardId: null, taskId: tid, reportId: opts.reportId || null, at: t.at || null, restored: t.status === 'answered' };
      base = asked(base, t);
      var next = t.status === 'answered' ? answered(base, t)
        : Object.assign({}, base, { turnId: t.turnId, text: null, busy: t.tool ? word(t.tool) : 'Thinking…', failedTurn: false });
      if (drop(next)) { if (j >= 0) out.splice(j, 1); return; }
      if (j >= 0) out[j] = next; else out.push(next);
    });
    return out.slice(-KEEP);
  }

  /* ---------- The main page's answer panels and History (m5 §9) ---------- */

  // The stable History key of an answer: its task and turn (or its local ID until the ask returns a turn).
  function answerKey(a) { return (a.taskId || '') + ':' + (a.turnId || a.id); }
  function isPending(a) { return a.text == null && !a.failedTurn; }
  function timeOf(a, i) { var t = Date.parse(a.at || ''); return isNaN(t) ? i : t; }

  /**
   * Report-wide answers (no card) as panels on the page and as History rows, both newest first.
   * opts: { open: {id → true|false}, deleted: {answerKey → true} }
   *   A panel shows when open[id] is true, or by default when it was asked here (not restored) or is still running;
   *   open[id] false closes it (it stays in History). Deleted answers show nowhere.
   * History: every answer with a question or a text, newest first: { id, key, title, at, open, running }.
   */
  function answerPanels(answers, opts) {
    var open = (opts && opts.open) || {}, deleted = (opts && opts.deleted) || {};
    var list = (answers || []).map(function (a, i) { return { a: a, i: i }; }).filter(function (x) {
      return !x.a.cardId && !deleted[answerKey(x.a)];
    });
    list.sort(function (x, y) { var d = timeOf(y.a, y.i) - timeOf(x.a, x.i); return d || y.i - x.i; });
    function shown(a) { return open[a.id] != null ? !!open[a.id] : !a.restored || isPending(a); }
    var panels = list.filter(function (x) { return shown(x.a); }).map(function (x) { return x.a; });
    var history = list.filter(function (x) { return x.a.q || x.a.text; }).map(function (x) {
      var a = x.a;
      return { id: a.id, key: answerKey(a), title: a.q || firstLine(a.text), at: a.at || null, open: shown(a), running: isPending(a) };
    });
    return { panels: panels, history: history };
  }
  function firstLine(t) { var s = String(t || '').split('\n')[0].trim(); return s.length > 120 ? s.slice(0, 117) + '…' : s; }

  // Arrow keys in a menu list: the next index to focus (wrapping), or -1 for a key the list doesn't handle.
  function menuStep(key, at, count) {
    if (!count) return -1;
    if (key === 'ArrowDown') return at < 0 ? 0 : (at + 1) % count;
    if (key === 'ArrowUp') return at < 0 ? count - 1 : (at - 1 + count) % count;
    if (key === 'Home') return 0;
    if (key === 'End') return count - 1;
    return -1;
  }

  // The one line shown in place of the app while the backend cannot be used, or null. "other_session": this user's
  // Optimus already runs in another Windows session (one host per user); the shell keeps trying and it clears by itself.
  function backendNotice(state, detail) {
    if (state === 'incompatible') return 'Update Optimus';
    if (state !== 'ready' && detail === 'other_session') return 'Optimus is open in another session';
    return null;
  }

  // The loading screen's state for a backend state and the shell's detail code (protocol-v1 §7), or 'ready' when it goes.
  // "incompatible" also dismisses it: the app's own "Update Optimus" notice covers that case.
  var LOAD_FOR_DETAIL = { needs_update: 'update', other_session: 'session', cant_start: 'failed' };
  function loadingState(state, detail) {
    if (state === 'ready' || state === 'incompatible') return 'ready';
    return LOAD_FOR_DETAIL[detail] || 'starting';
  }
  var UPDATE_URL = 'https://github.com/D3K1D/Optimus/releases/latest';
  // Drives the loading screen (ui/js/loading.js) from the backend: its buttons retry, open the download page, or close.
  // It goes once the backend is ready (after the first paint, so the app is never shown half-drawn); afterwards the app's
  // own notices take over. A desktop backend whose first state hasn't arrived yet (known() false) keeps it up.
  function wireLoading(backend, loading) {
    if (!backend || !loading) return function () {};
    loading.on({
      retry: function () { Promise.resolve(backend.call('shell.retryBackend', {})).catch(function () {}); },
      update: function () { window.open(UPDATE_URL, '_blank', 'noopener'); },
      close: function () { window.close(); }
    });
    var off = null;
    function apply(state, detail) {
      var next = loadingState(state, detail);
      if (next !== 'ready') { loading.set(next); return; }
      if (off) { off(); off = null; }
      var paint = typeof requestAnimationFrame === 'function' ? requestAnimationFrame : function (f) { setTimeout(f, 16); };
      paint(function () { paint(function () { loading.ready(); }); });
    }
    off = backend.onState(function (state, info) { apply(state, info && info.detail); });
    if (typeof backend.known !== 'function' || backend.known()) apply(backend.state(), backend.detail ? backend.detail() : null);
    return function () { if (off) off(); };
  }

  // Brings a whole popover inside the viewport. Focus alone only reveals the focused row, which can leave the
  // panel's top edge (its padding and first row) cut off above the page when the page is scrolled.
  function keepInView(el) {
    if (el && typeof el.scrollIntoView === 'function') el.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }

  // Citation chips for evidence the assistant measured itself (it is not in the report, so it has no e-label): a short
  // word for the kind of check instead of a bare running number, "Ports", "CPU", and "CPU 2" for the second of a
  // kind. Evidence whose kind is not known (yet) keeps the running label, "m3".
  var CITE_KINDS = [[/bench/, 'Bench'], [/sensor/, 'Sensors'], [/process|cpu/, 'CPU'], [/power/, 'Power'], [/startup/, 'Startup'], [/event/, 'Events'],
    [/volume|storage|disk/, 'Drives'], [/listener|network/, 'Ports'], [/display/, 'Display'], [/device|pnp/, 'Devices'], [/app/, 'Apps'],
    [/wsc|defender|firewall|security/, 'Security']];
  var BENCH_PART = { cpu: 'CPU', gpu: 'GPU', ram: 'RAM', disk: 'Disk' };
  // hint: the evidence ({collectorId}) or the check that produced it (a diagnostic id such as "network.listeners", or a tool name).
  function citeKind(hint) {
    var id = String((hint && typeof hint === 'object' ? hint.collectorId : hint) || '').toLowerCase();
    var bench = /^bench\.([a-z]+)/.exec(id);
    if (bench) return BENCH_PART[bench[1]] || 'Bench';
    for (var i = 0; i < CITE_KINDS.length; i++) if (CITE_KINDS[i][0].test(id)) return CITE_KINDS[i][1];
    return null;
  }
  function citeRegistry() { return { n: 0, order: {}, named: {}, counts: {} }; }
  // The label for one evidence id; stable once its kind is known. reg: citeRegistry(); hint: as for citeKind, when known.
  function citeLabel(reg, id, hint) {
    if (reg.named[id]) return reg.named[id];
    if (!reg.order[id]) reg.order[id] = ++reg.n;
    var kind = citeKind(hint);
    if (!kind) return 'm' + reg.order[id];
    var k = reg.counts[kind] = (reg.counts[kind] || 0) + 1;
    return (reg.named[id] = k === 1 ? kind : kind + ' ' + k);
  }

  /* ---------- M9 §1: AI permissions (contracts/m9-ai-powers.md §1) ---------- */
  // The record before the user has answered the page (set:false).
  function permDefaults() {
    return { version: 1, revision: 0, set: false, see: { specs: true, history: true, names: false },
      'do': { checks: true, changes: 'off', scripts: false, web: false, admin: false } };
  }
  var CHANGES = { off: 0, ask: 1, fix: 2 };
  // Record → the page's toggles {specs, history, names, checks, ask, fix, scripts, web}. Before the user answered,
  // the page pre-selects single changes and multi-step fixes; online lookups and scripts stay off (D3).
  function permToggles(rec) {
    rec = rec || permDefaults();
    var see = rec.see || {}, d = rec['do'] || {}, set = !!rec.set;
    var ch = CHANGES[d.changes] != null ? d.changes : 'off';
    return { specs: see.specs !== false, history: see.history !== false, names: !!see.names, checks: d.checks !== false,
      ask: set ? ch !== 'off' : true, fix: set ? ch === 'fix' : true,
      scripts: set ? !!d.scripts && ch === 'fix' : false, web: set ? !!d.web : false,
      // m9 §17.1 (design v75 AdminRights): never pre-selected; absent reads as off.
      admin: set ? d.admin === true : false };
  }
  // The page's toggles → the record ai.setPermissions takes (revision from the record it was read from).
  function permRecord(v, base) {
    base = base || permDefaults();
    var changes = v.fix ? 'fix' : v.ask ? 'ask' : 'off';
    return { version: 1, revision: base.revision || 0, set: !!base.set,
      see: { specs: !!v.specs, history: !!v.history, names: !!v.names },
      // m9 §17.1 review: admin is always sent (the host requires it): the toggle when the page has one, else the stored value.
      'do': { checks: !!v.checks, changes: changes, scripts: changes === 'fix' && !!v.scripts, web: !!v.web,
        admin: v.admin != null ? !!v.admin : !!(base['do'] && base['do'].admin === true) } };
  }
  // The one line under the ask box (design PermissionsSummary), for the effective grant: an account without tools
  // explains only. "Change" is the line's button, not part of the text.
  function permissionLine(rec, available) {
    var v = permToggles(rec && rec.set ? rec : Object.assign({}, rec || permDefaults(), { set: true }));
    var explain = !!(available && available.tools === false);
    var parts = ['Reads your report'];
    if (v.checks && !explain) parts.push('runs checks');
    parts.push(explain || !(v.ask || v.fix) ? 'explains only' : 'asks before changes');
    if (v.scripts && !explain && !(available && available.scripts === false)) parts.push('runs approved scripts');
    // design v75: the summary adds administrator rights when they are on (and available in this version).
    if (v.admin && !explain && !(available && available.admin === false)) parts.push('can ask for administrator rights');
    if (v.web && !explain && !(available && available.web === 'no')) parts.push('looks things up');
    return parts.join(' · ');
  }
  // The page's linking (design PERM_DO): scripts on turns multi-step fixes and single changes on; fixes on turns single
  // changes on; single changes off turns fixes and scripts off; fixes off turns scripts off (§1.1: scripts need changes:"fix").
  function permSet(cur, id, val) {
    var n = Object.assign({}, cur); n[id] = val;
    if (id === 'scripts' && val) { n.fix = true; n.ask = true; }
    if (id === 'fix' && val) n.ask = true;
    if (id === 'ask' && !val) { n.fix = false; n.scripts = false; }
    if (id === 'fix' && !val) n.scripts = false;
    return n;
  }
  // The pairing panel's choices → an agent.start grant (diagnostics: every available one when checks is on).
  function grantFromToggles(v, diagnostics) {
    var r = permRecord(v);
    return { diagnostics: r['do'].checks ? (diagnostics || []).slice() : [], share: { names: r.see.names },
      see: { specs: r.see.specs, history: r.see.history }, changes: r['do'].changes, scripts: r['do'].scripts, web: r['do'].web, admin: r['do'].admin,
      canRequestChanges: r['do'].changes !== 'off' };
  }
  // Is it an AI the permissions page is for? local / later (no kind) never see it.
  function isAiKind(k) { return isApi(k) || isMcp(k); }
  // Routing by `set`, not by the onboarding step: where to go instead of `next`. s: {kind, permissions, next}.
  // permissions null (not loaded, or an older host without ai.getPermissions) never blocks.
  function permissionsGate(s) {
    var need = isAiKind(s.kind) && !!s.permissions && s.permissions.set === false;
    return need ? { stage: 'permissions', returnTo: s.next } : { stage: s.next, returnTo: null };
  }
  // Where the chooser leads: back where it was opened from, else the next setup step (a completed user stays on main).
  var STEP_STAGE = { provider: 'login', use: 'use', goal: 'goal', specs: 'specs', complete: 'main' };
  function nextAfterProvider(step, returnTo) {
    if (returnTo) return returnTo;
    if (!step || step === 'provider') return 'use';
    return STEP_STAGE[step] || 'main';
  }

  /* ---------- M9 §6–§7: researched reports and the local guide answer ---------- */
  // The toolbar's one chip (design Toolbar v51, CheckupStatus / ResearchStatus): the Toolbar props for the chip to show,
  // or null. One at a time, in the designer's priority: running → new check-up cards → check-up result → research status.
  //   running   the check-up ({checking: true}), the first-ever lookups ({checking: 'setup'}), or research
  //             ({researching: [n, m]}, n = the subject being worked on, so the first reads "1 of 3").
  //   new       {newCheckups: n}: check-up cards added since the user last saw the deck (o.newCheckups).
  //   check-up  {checkupStatus: 'none' | 'failed' | 'unavailable' | 'partial', missed?, ai?}: done with no cards is
  //             'none'; done with cards shows no chip (the cards are the result). missed: the area KEYS the partial run
  //             did not check (design Toolbar v55 builds the sentence); all five missed reads as 'failed'.
  //   research  {researchStatus: 'failed' | 'unavailable', ai?}.
  // r: research.get (+ research.state events); o: { newCheckups, ai }.
  // The check-up's five inventory areas (m9 §6.10); unknown ids from the host are dropped, never shown.
  var CHECKUP_AREA_IDS = ['hardware', 'drivers', 'windows', 'programs', 'background'];
  function toolbarChip(r, o) {
    o = o || {};
    var ck = (r && r.checkup) || null;
    if (ck && ck.status === 'running') return { checking: true };
    if (r && (r.status === 'queued' || r.status === 'running')) {
      if (r.trial) return { checking: 'setup' };
      var total = r.total || 0;
      // A run that has not counted its subjects yet: "Researching…" (design Toolbar researching: true).
      return total ? { researching: [Math.min((r.done || 0) + 1, total), total] } : { researching: true };
    }
    if (o.newCheckups > 0) return { newCheckups: o.newCheckups };
    if (ck) {
      if (ck.status === 'done' && !(ck.cards || []).length) return { checkupStatus: 'none' };
      if (ck.status === 'failed') return { checkupStatus: 'failed' };
      if (ck.status === 'unavailable') return { checkupStatus: 'unavailable', ai: o.ai || null };
      if (ck.status === 'partial') {
        var missed = CHECKUP_AREA_IDS.filter(function (a) { return (ck.missed || []).indexOf(a) >= 0; });
        if (missed.length === CHECKUP_AREA_IDS.length) return { checkupStatus: 'failed' };
        return missed.length ? { checkupStatus: 'partial', missed: missed } : { checkupStatus: 'partial' };
      }
    }
    if (r && r.status === 'failed') return { researchStatus: 'failed' };
    if (r && r.status === 'unavailable') return { researchStatus: 'unavailable', ai: o.ai || null };
    return null;
  }
  // The deck's display order (design v54 Show: the new cards come to the front of the deck). ids: the cards the deck
  // shows, in the host's queue order (never changed here); front: the ids already moved to the front (kept, so a card
  // does not jump back once it has been seen); fresh: new check-up ids not seen yet. → {order: ids in display order,
  // front: the updated front list}. Only cards the deck shows can move: the host sends the next three, and acts only
  // on those.
  function deckOrder(ids, front, fresh) {
    var f = (front || []).slice();
    (fresh || []).forEach(function (id) { if (ids.indexOf(id) >= 0 && f.indexOf(id) < 0) f.push(id); });
    var order = ids.map(function (id, i) { return { id: id, i: i }; }).sort(function (x, y) {
      var fx = f.indexOf(x.id), fy = f.indexOf(y.id);
      return (fx < 0 ? 1e6 : fx) - (fy < 0 ? 1e6 : fy) || x.i - y.i;
    }).map(function (x) { return x.id; });
    return { order: order, front: f };
  }
  // Check-up cards the user has not seen yet: ids in `cards` (the deck's check-up card ids, in order) that are not in
  // `seen`. The page adds a card to `seen` when it has been on screen, and all of them when Show is used.
  function newCheckupIds(cards, seen) {
    var s = seen || {};
    return (cards || []).filter(function (id) { return !s[id]; });
  }
  // Show on the "n new from the online check-up" chip: followup.front {reportId, cardIds ≤ 8} moves the new cards to the front
  // of the report's undecided queue (in this order), so cards beyond the three on screen really come forward. Resolves to
  // the new followup view, or null when nothing was sent or the call failed (-32601 on an older host): the page then keeps
  // today's behaviour (deckOrder for the cards already on screen).
  var FRONT_MAX = 8;
  function frontCards(call, reportId, ids) {
    var list = (ids || []).filter(function (id, i, a) { return typeof id === 'string' && id && a.indexOf(id) === i; }).slice(0, FRONT_MAX);
    if (!reportId || !list.length) return Promise.resolve(null);
    return Promise.resolve().then(function () { return call('followup.front', { reportId: reportId, cardIds: list }); })
      .then(function (v) { return v && v.reportId === reportId ? v : null; }, function () { return null; });
  }
  // Claude Code's state in the picker → the design's one grey line (LoginScreen claudeNote, v53), or null.
  // cs: shell.clientStatus as the page keeps it (null or known:false: the status could not be read).
  //   unknown       no answer, a sign-in check without an answer, or the program wouldn't start (launch_failed): Check again
  //   installing    the installer runs (updating: it replaces an outdated copy): step 1 turns its gears, disabled
  //   signingin     the browser sign-in is open
  //   blocked       a rule on this PC stops it (policy, access_denied): Install disabled
  //   repair        node_missing / unsupported_path: the installer fixes both (Repair = Install); the causes are never named
  //   installfailed the installer didn't start or didn't finish: Show command
  //   desktoponly   the Claude app is there, Claude Code isn't: the normal Install step
  //   signinfailed  installed, but the sign-in didn't start: Sign in stays on
  //   apikey        signed in with an API key (billed to that key)
  function claudeNote(cs) {
    if (!cs || !cs.known) return 'unknown';
    if (cs.installing) return cs.outdated ? 'updating' : 'installing';
    if (cs.signing) return 'signingin';
    if (cs.blocked) return cs.blockedReason === 'policy' || cs.blockedReason === 'access_denied' ? 'blocked'
      : cs.blockedReason === 'node_missing' || cs.blockedReason === 'unsupported_path' ? (cs.failed ? 'installfailed' : 'repair') : 'unknown';
    if (!cs.installed) return cs.failed ? 'installfailed' : cs.app ? 'desktoponly' : null;
    if (cs.outdated) return cs.failed ? 'installfailed' : null;
    if (cs.signedIn == null) return 'unknown';
    if (cs.signedIn !== true && cs.failed) return 'signinfailed';
    if (cs.signedIn === true && cs.auth === 'api_key') return 'apikey';
    return null;
  }
  // The mono word under a picker tile (design LoginScreen tileStatus, v54), or null. The designer's words only; the
  // design itself marks the warning words (alert mark, bold): "Couldn’t check", "Update needed", "Signed out", "Blocked on
  // this PC", "Needs repair", "Limit reached", "No credit", "Key rejected", "Offline", "Not available for this account".
  // "Not installed" and "Connected" stay plain.
  //   tile 'claude': cs is shell.clientStatus (undefined: not checked yet), st its connectionState row.
  //   other tiles:   st only (ChatGPT, API keys): the m3 §9 rows from connectionState.
  // Rows with no designed word (busy, a sign-in that was cancelled, timed out or not saved, not set up) show none.
  var PLAN_WORD = { auth: 'Signed out', quota: 'Limit reached', not_eligible: 'Not available for this account', offline: 'Offline' };
  var KEY_WORD = { auth: 'Key rejected', quota: 'No credit', not_eligible: 'Not available for this account', offline: 'Offline' };
  function tileStatus(tile, cs, st) {
    var row = st && st.row;
    if (row === 'connected' || row === 'paired' || row === 'active' || row === 'client_closed') return 'Connected';
    if (tile === 'claude') {
      if (cs === undefined) return null;
      if (!cs || !cs.known) return 'Couldn’t check';
      if (cs.blocked) return cs.blockedReason === 'policy' || cs.blockedReason === 'access_denied' ? 'Blocked on this PC'
        : cs.blockedReason === 'node_missing' || cs.blockedReason === 'unsupported_path' ? 'Needs repair' : 'Couldn’t check';
      if (cs.outdated) return 'Update needed';
      if (!cs.installed) return 'Not installed';
      if (cs.signedIn == null) return 'Couldn’t check';
      if (cs.signedIn === false) return 'Signed out';
      return null;
    }
    if (tile === 'chatgpt') return PLAN_WORD[row] || null;
    if (tile === 'api-key') return KEY_WORD[row] || null;
    return null;
  }
  /* ---------- design v72 (PillAndStates): the picker's ChatGPT line and the bottom button ---------- */
  // The ChatGPT line (LoginScreen chatgptState), from its connectionState row:
  //   connected                                                   → signedin      “Signed in with ChatGPT” (check)
  //   signing (the browser sign-in is open, or the row checking)  → signingin     [Signing in…] (turning gears, disabled)
  //   auth (the saved sign-in was rejected)                       → signedout     “Signed out of ChatGPT” [Sign in]
  //   quota                                                       → limit         “ChatGPT plan limit reached”, no button
  //   not_eligible                                                → notavailable  “Not available for this account”, no button
  //   callback_timeout, cancelled, storage, busy (unavailable),
  //   offline (network)                                           → failed        “Sign-in didn’t finish” [Try again]
  //   not_configured, unknown, no row                             → new           “Sign in with ChatGPT” [Sign in]
  var CHATGPT_FAILED = { callback_timeout: 1, cancelled: 1, storage: 1, busy: 1, offline: 1 };
  var CHATGPT_ROW = { auth: 'signedout', quota: 'limit', not_eligible: 'notavailable' };
  function chatgptState(st, signing) {
    var row = st && st.row;
    if (row === 'connected') return 'signedin';
    if (signing || row === 'checking') return 'signingin';
    if (CHATGPT_ROW[row]) return CHATGPT_ROW[row];
    return CHATGPT_FAILED[row] ? 'failed' : 'new';
  }
  // Claude through Claude Code is connected (step 3 checked) when the connection is ready and nothing is being
  // installed, updated or repaired. st: its connectionState row; cv: the picker's client view ({note, outdated}).
  var CLAUDE_READY = { connected: 1, paired: 1, active: 1, client_closed: 1 };
  function claudeConnected(st, cv) {
    if (!(st && CLAUDE_READY[st.row])) return false;
    var note = cv ? cv.note : null;
    return !(cv && cv.outdated) && note !== 'repair' && note !== 'installing' && note !== 'updating';
  }
  // The picker's bottom button: “Continue” when the chosen AI is already connected (Claude connected, ChatGPT signed
  // in, Local only), otherwise “Connect”. An API key always says Connect: the key is checked.
  function connectLabel(tile, st, cv) {
    if (tile === 'local') return 'Continue';
    if (tile === 'chatgpt') return chatgptState(st) === 'signedin' ? 'Continue' : 'Connect';
    if (tile === 'claude') return claudeConnected(st, cv) ? 'Continue' : 'Connect';
    return 'Connect';
  }
  // design v73 (PillPanelStates): the picker's bottom button for ChatGPT. Signing in → 'waiting' (“Waiting for
  // browser…”, disabled, turning gears); plan limit or not available → 'none' (no primary button: Later and Back stay);
  // otherwise null (Connect / Continue). Other tiles: null.
  var CHATGPT_NO_PRIMARY = { limit: 1, notavailable: 1 };
  function pickerPrimary(tile, st, signing) {
    if (tile !== 'chatgpt') return null;
    var gs = chatgptState(st, signing);
    return gs === 'signingin' ? 'waiting' : CHATGPT_NO_PRIMARY[gs] ? 'none' : null;
  }

  /* ---------- design v73 (PillPanelStates): the AI pill panel's state ---------- */
  // One of the design's twelve states for a connection. s: connectionState's input; working: a run is going on the
  // active task. → { state, code?, own?, detail?, hint?, primary?, tone? }.
  // A row the design has no state for keeps its own word, hint and action (own:true): detail, hint (null = none),
  // primary (the button's name, null = none) and tone override the nearest state's, inside its layout.
  //   local / later                                       → local
  //   a run going (working), MCP active                   → live
  //   connected                                           → connected      (not_saved: + its hint, own)
  //   API not_configured                                  → nokey
  //   API error auth                                      → badkey
  //   ChatGPT not_configured, auth, cancelled; MCP signed_out → signedout
  //   error network                                       → offline
  //   error unavailable (API, ChatGPT)                    → busy
  //   MCP pending (+ code)                                → pairing
  //   MCP paired                                          → paired
  //   MCP waiting never_seen                              → setup
  //   MCP waiting idle / pairing_expired / denied         → manual
  //   own: checking, no entry yet, Reconnecting…, quota, not_eligible, storage, callback_timeout, MCP client_closed,
  //        bridge_rejected (manual's layout, “Setup issue”), error unavailable (outdated / blocked: “Can’t run”).
  var PANEL_ACTION = { addKey: 'Add key', updateKey: 'Update key', retry: 'Retry', signIn: 'Sign in', install: 'Set up', start: 'Start', approve: 'Approve', activity: 'Activity', askAgain: 'Activity' };
  var PANEL_TONE = { connected: 'ok', live: 'live', expired: 'warn', local: 'muted' };
  var PANEL_ROW = { connected: 'connected', offline: 'offline', busy: 'busy', pending: 'pairing', paired: 'paired', active: 'live', signed_out: 'signedout', not_set_up: 'setup', waiting: 'manual' };
  function pillPanelState(s, working) {
    var st = connectionState(s), kind = s.local ? null : s.kind;
    if (!kind) return { state: 'local' };
    var own = function (base, x) {
      return Object.assign({ state: base || 'connected', own: true, detail: st.word, hint: st.hint || null,
        primary: st.action === 'askAgain' && !s.task ? null : PANEL_ACTION[st.action] || null, tone: PANEL_TONE[st.tone] || 'muted' }, x || {});
    };
    if (s.backend === 'reconnecting') return own(null, { primary: null });
    if (working) return { state: 'live' };
    var row = st.row, plan = kind === 'chatgpt_plan';
    if (row === 'connected') return st.hint ? own('connected') : { state: 'connected' };
    if (row === 'not_configured') return { state: plan ? 'signedout' : 'nokey' };
    if (row === 'auth') return { state: plan ? 'signedout' : 'badkey' };
    if (row === 'cancelled') return { state: 'signedout' };
    if (row === 'pending') return { state: 'pairing', code: (s.entry && s.entry.code) || null };
    if (row === 'bridge_rejected') return own('manual', { hint: undefined, primary: undefined, tone: 'warn' });
    if (PANEL_ROW[row]) return { state: PANEL_ROW[row] };
    return own(null);
  }
  // design v70 (PermissionsStep skipNote): the “change these settings any time from the AI pill” note shows only the first
  // time the page is finished (the save that sets set:true, in onboarding or a first connect). A record already set (the
  // page reopened from the pill or Change), or none at all (an older host: nothing is saved), goes straight on.
  function permSkipNote(rec) { return !rec || rec.set !== false; }

  // research.get items → findingId → the design's ResearchNote props {verdict:'safe'|'look', summary, unsigned, sources}.
  function researchNotes(view) {
    var out = {};
    ((view && view.items) || []).forEach(function (it) {
      if (!it || !it.findingId || (it.verdict !== 'considered_safe' && it.verdict !== 'worth_a_look')) return;
      out[it.findingId] = { verdict: it.verdict === 'considered_safe' ? 'safe' : 'look', summary: typeof it.summary === 'string' ? it.summary : '',
        unsigned: it.basis === 'name_only', sources: (it.sources || []).slice(0, 3) };
    });
    return out;
  }
  // §6.8: a check-up card (followup card with `checkup:{kind, sources}`) → the design's InsightCard `checkup` prop
  // {kind?, title, text?, sources}; null for any other card. An unknown kind shows no kind chip.
  var CHECKUP_KIND = { bug: 'Bug', security: 'Security', end_of_life: 'End of life', conflict: 'Conflict', update: 'Update' };
  function checkupOf(card) {
    if (!card || !card.checkup || typeof card.checkup !== 'object') return null;
    var text = card.text && card.text !== card.title ? card.text : null;
    return { kind: CHECKUP_KIND[card.checkup.kind], title: card.title || '', text: text,
      sources: (Array.isArray(card.checkup.sources) ? card.checkup.sources : []).filter(function (s) { return s && s.sourceId; }).slice(0, 3) };
  }
  // §6.8: the report sheet's "AI check-up" group (design ReportSheet `checkups`, v51): every check-up card of the report,
  // hidden ones included, in deck order → {id: "ck-1", kind?, title, text?, sources: [{sourceId, site}], time}.
  // time(iso) formats checkup.checkedAt ("14:10").
  function checkupRow(card, i, time) {
    var c = checkupOf(card); if (!c) return null;
    var at = card.checkup.checkedAt;
    return { id: 'ck-' + (i + 1), kind: c.kind, title: c.title, text: c.text,
      sources: c.sources.map(function (s) { return { sourceId: s.sourceId, site: s.site || s.domain || '' }; }),
      time: at && time ? time(at) : '' };
  }
  // A research.state event: which part of the view it updates. The check-up phase only moves checkup.status (a running
  // check-up, or its end: an ended or cancelled check-up is re-read from research.get).
  function researchFromState(r, pl) {
    if (pl.phase === 'checkup') {
      var ck = Object.assign({ status: 'idle', lastAt: null, cards: [] }, r && r.checkup);
      return Object.assign({ items: [] }, r, { reportId: pl.reportId, checkup: Object.assign(ck, { status: pl.status === 'running' ? 'running' : ck.status === 'running' ? 'idle' : ck.status }) });
    }
    return Object.assign({ items: [] }, r, { reportId: pl.reportId, runId: pl.runId, status: pl.status, done: pl.done, total: pl.total, trial: !!pl.trial });
  }
  /* ---------- the chosen AI is used for everything ---------- */
  // An in-app task (API kinds, ChatGPT, headless Claude Code): one the app itself asks. A paired outside window is not.
  function inAppTask(t) { return !!(t && t.recipient && (!isMcp(t.recipient.kind) || t.recipient.mode === 'headless')); }
  // Where a question goes. s: {given, current, chosenKind, reportId}. A task handed in decides the recipient (it was
  // started or checked for this question); otherwise the current task only when it is the chosen AI's, active, on this
  // report and askable here; else a new task for the chosen AI. → {task} | {start: kind} | {task: null} (no AI chosen).
  function askTarget(s) {
    if (s.given) return { task: s.given };
    var k = s.chosenKind, t = s.current;
    if (!k) return { task: null };
    var fits = !!t && t.status === 'active' && t.recipient.kind === k && !!s.reportId && !!t.scope && t.scope.reportId === s.reportId && inAppTask(t);
    return fits ? { task: t } : { start: k };
  }
  // Switching to nextKind stops this task: an active in-app task of another AI (an outside window is left alone).
  function switchStops(t, nextKind) { return !!t && t.status === 'active' && t.recipient.kind !== nextKind && inAppTask(t); }
  // A start that came back for an AI the user has since switched away from (an in-app start, not a paired window's).
  function staleStart(g, t, chosenKind) { return !!g && !g.pairingId && !g.grant && !!t && inAppTask(t) && t.recipient.kind !== chosenKind; }
  // The host refused a start, ask or review for an AI it does not have saved as the chosen one (-32013 not_chosen).
  function notChosen(err) { return !!err && err.code === -32013 && !!err.data && err.data.reason === 'not_chosen'; }

  // "What can this app do?" and the like: answered locally from guide.get when no AI is connected (§2).
  // Whole questions only: "help my PC is slow" or "what can you do about my fan" are not about the app.
  var APP_Q = /^\s*(?:help|features?|what can (?:you|this app|optimus|the app) do(?: for me)?|what (?:does|do) (?:this app|optimus|the app|you) do|what is (?:this app|optimus)|how (?:does|do) (?:this app|optimus|the app) work|how do i use (?:this app|optimus|the app)|what can i do(?: here| with (?:this app|optimus|the app))?|(?:what are |show (?:me )?|list )?(?:your |the app'?s? |optimus'?s? |the |all )?features|what (?:features|tools) (?:are there|do you have|does (?:this app|optimus) have))\s*[?.!]*\s*$/i;
  function isAppQuestion(text) { return APP_Q.test(String(text || '')); }

  var api = { connectionState: connectionState, routeAsk: routeAsk, taskEnded: taskEnded, turnsFromHistory: turnsFromHistory,
    runningTurn: runningTurn, reconcileAnswers: reconcileAnswers, answerKey: answerKey, answerPanels: answerPanels, menuStep: menuStep,
    backendNotice: backendNotice, loadingState: loadingState, wireLoading: wireLoading, keepInView: keepInView, citeKind: citeKind, citeRegistry: citeRegistry, citeLabel: citeLabel,
    permDefaults: permDefaults, permToggles: permToggles, permRecord: permRecord, permissionLine: permissionLine, permSet: permSet, grantFromToggles: grantFromToggles,
    isAiKind: isAiKind, permissionsGate: permissionsGate, nextAfterProvider: nextAfterProvider,
    aiName: aiName, pickerTile: pickerTile, storedValue: storedValue, tileKind: tileKind, kindTile: kindTile, retiredProvider: retiredProvider, retiredLanding: retiredLanding, retiredRedirect: retiredRedirect,
    toolbarChip: toolbarChip, newCheckupIds: newCheckupIds, deckOrder: deckOrder, frontCards: frontCards, claudeNote: claudeNote, tileStatus: tileStatus,
    chatgptState: chatgptState, claudeConnected: claudeConnected, connectLabel: connectLabel, pickerPrimary: pickerPrimary, pillPanelState: pillPanelState, permSkipNote: permSkipNote, checkupRow: checkupRow, researchNotes: researchNotes, checkupOf: checkupOf, researchFromState: researchFromState, isAppQuestion: isAppQuestion,
    askTarget: askTarget, switchStops: switchStops, staleStart: staleStart, notChosen: notChosen };
  if (root) {
    root.OptTool = root.OptTool || {};
    Object.keys(api).forEach(function (k) { root.OptTool[k] = api[k]; });
  }
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof window !== 'undefined' ? window : null);
