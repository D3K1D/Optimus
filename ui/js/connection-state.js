/* One function for every AI connection state (contracts/m3-agent.md §9). The pill, the chooser tiles, the pill
   detail and the asking paths render only from what it returns. Pure; loadable from Node for tests. */
(function (root) {
  'use strict';

  // In-app kinds: API keys, and the ChatGPT plan (browser sign-in instead of a key).
  function isApi(k) { return k === 'anthropic_api' || k === 'openai_api' || k === 'chatgpt_plan'; }
  function isMcp(k) { return k === 'claude_code_mcp' || k === 'codex_mcp'; }
  var CLIENT = { claude_code_mcp: 'Claude Code', codex_mcp: 'Codex' };
  var CLI = { claude_code_mcp: 'claude', codex_mcp: 'codex' };

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
    var label = (e && e.label) || s.label || CLIENT[kind] || 'Local only';
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
  // status; task_* is still accepted). The UI then opens the grant sheet for a fresh task instead of "No answer".
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
      else if (t.status === 'running' && pl.status === 'failed') { t.status = pl.code === 'cancelled' ? 'cancelled' : 'failed'; t.tool = null; }
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
      return Object.assign({}, a, { turnId: t.turnId, text: strip(m.text || ''), citations: m.citations || [], followUps: m.followUps || [], busy: null, failedTurn: false });
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
      else if (t.status === 'failed') out.push(pending ? Object.assign({}, a, { failedTurn: true, busy: null }) : a);
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

  var api = { connectionState: connectionState, routeAsk: routeAsk, taskEnded: taskEnded, turnsFromHistory: turnsFromHistory,
    runningTurn: runningTurn, reconcileAnswers: reconcileAnswers, answerKey: answerKey, answerPanels: answerPanels, menuStep: menuStep };
  if (root) {
    root.OptTool = root.OptTool || {};
    Object.keys(api).forEach(function (k) { root.OptTool[k] = api[k]; });
  }
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof window !== 'undefined' ? window : null);
