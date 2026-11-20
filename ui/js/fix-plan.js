/* Fix plans (contracts/m9-ai-powers.md §4) and approved scripts (§5): pure helpers that turn a plan manifest and its operation into what the
   approval card shows. No React, no DOM: shared by components.js, app-flow.js and the node tests. */
(function (root) {
  'use strict';

  // §3.1 flags (closed set) and privilege → the design's step badge keys (ApprovalCard STEP_BADGES), in a fixed order.
  var FLAG_BADGE = { irreversible: 'irreversible', data_loss: 'unsaved', runs_code: 'program', restart: 'restart', sign_out: 'signout', network: 'network', long: 'slow' };
  var FLAG_ORDER = ['irreversible', 'data_loss', 'runs_code', 'restart', 'sign_out', 'network', 'long'];
  function badgeKeys(flags, privilege) {
    var f = flags || [], out = privilege === 'admin' ? ['admin'] : [];
    FLAG_ORDER.forEach(function (k) { if (f.indexOf(k) >= 0) out.push(FLAG_BADGE[k]); });
    return out;
  }
  // §0.3: any irreversible or data-loss flag needs the tick before Approve.
  function needsTick(flags) { var f = flags || []; return f.indexOf('irreversible') >= 0 || f.indexOf('data_loss') >= 0; }

  // A step's trade-offs (the host's step or plan `tradeoffs`) → the design's step.tradeoff (v66 OneStepCards, after the rows, on every
  // step that has one, in multi-step fixes too): one → a string ("Trade-off: <text>"), several → an array ("Trade-offs:" and a short
  // list), none (blank entries count as none) → null.
  function stepTradeoff(list) {
    var t = (Array.isArray(list) ? list : []).filter(function (x) { return typeof x === 'string' && x.trim(); }).map(function (x) { return x.trim(); });
    return t.length === 0 ? null : t.length === 1 ? t[0] : t;
  }

  // Journal step state → the card's step state.
  var STEP_STATE = { pending: 'queued', prepared: 'running', attempted: 'running', verified: 'done', failed: 'failed', indeterminate: 'unclear', skipped: 'skipped' };
  var RUNNING = { approved: 1, running: 1 };
  // completed (m9 §13): a report-only action (repair.sfc, repair.dism_restorehealth, network.flush_dns) finished; nothing was
  // read back, so it is never verified.
  var TERMINAL = { verified: 1, completed: 1, failed: 1, indeterminate: 1, partial: 1, declined: 1, rolled_back_failed: 1, cancelled: 1, expired: 1 };
  function isTerminal(state) { return !!TERMINAL[state]; }

  // The card for a fix plan (design ApprovalCard with steps, v51): { state, steps, settingsUri } where state is
  // awaiting | applying | verified | partial | stopped | failed | cantrun | expired, and each step's state is
  // queued | running | done | failed | unclear | blocked | skipped | undone (null before approval).
  // op may be partial (built from operation.state events) or missing.
  //   cantrun   the plan is ineligible; its listed steps are 'blocked' with the host's message as the note.
  //   unclear   the step's result couldn't be confirmed (journal 'indeterminate'): "Check again" re-reads the operation.
  //   settings  a done step Optimus can't undo (undo not_supported, not flagged irreversible) while the operation names a
  //             Settings page (op.manualUri): "Open settings" in place of Undo. A failed step with that page: Open settings
  //             beside Check again (design v54). Before approval (opts.manualUri, a single change): the plan can't be undone
  //             by Optimus but a Settings page changes it back: the design's "You can change it back in Settings." line.
  //   undo      before approval: the plan's (or step's) undo.supported, so the design's undo line can show; after: the
  //             operation's undo.available. undoNote: the plan's own undo condition (the host's sentence, as it is).
  //   A run that ended with unclear steps and no failed one is 'partial' (something may have changed: never "Nothing changed").
  function fixView(plan, op, opts) {
    opts = opts || {};
    var byN = {};
    ((op && op.steps) || []).forEach(function (s) { byN[s.n] = s; });
    var blocked = {};
    ((plan.ineligible && plan.ineligible.steps) || []).forEach(function (s) { blocked[s.n] = s; });
    var manual = (op && op.manualUri) || null;
    var steps = (plan.steps || []).map(function (s) {
      var o = byN[s.n] || null, st = null, note = null;
      if (plan.status === 'ineligible') { if (blocked[s.n]) { st = 'blocked'; note = blocked[s.n].message || null; } }
      else if (op) st = o ? STEP_STATE[o.state] || 'queued' : 'queued';
      if (o && o.state === 'verified' && o.undo && o.undo.reason === 'undone') st = 'undone';
      if (o && (o.state === 'failed' || o.state === 'indeterminate') && o.error) note = o.error.message || null;
      // The design's own grey line stands for an unclear step ("Optimus couldn't confirm this worked.").
      if (st === 'unclear') note = null;
      var irr = (s.flags || []).indexOf('irreversible') >= 0, before = !op && plan.status !== 'ineligible';
      // Design v62: a step that changes nothing that would need undoing (the host's undo.nothingToUndo) has no undo line,
      // no Undo, no Settings route back, and is never part of Undo all.
      var nothing = nothingToUndo(s.undo, s.flags);
      var undo = !nothing && (before ? !!(s.undo && s.undo.supported) : !!(o && o.state === 'verified' && o.undo && o.undo.available));
      var settings = nothing ? false : before ? !undo && !!opts.manualUri && !irr
        : st === 'failed' ? !!manual
          : st === 'done' && !undo && !!manual && !!(o.undo && o.undo.reason === 'not_supported') && !irr;
      var undoNote = before && undo && s.undo && typeof s.undo.conditions === 'string' && s.undo.conditions.trim() ? s.undo.conditions.trim() : null;
      return { n: s.n, label: s.label, does: s.what || null, rows: (s.rows || []).map(function (r) { return [r.label, r.before, r.after]; }),
        tradeoff: stepTradeoff(s.tradeoffs), badges: badgeKeys(s.flags, s.privilege), why: s.why || null, state: st, note: note, settings: settings,
        undo: undo, undoNote: undoNote, nothingToUndo: nothing, receipt: o && o.receipt ? o.receipt.text || null : null };
    });
    var state, inel = plan.status === 'ineligible' ? plan.ineligible || {} : null;
    // Can't run with no single step to blame (design RebuildCases): the plan's own reason, and its Settings page.
    var whole = !!inel && !steps.some(function (s) { return s.state === 'blocked'; });
    if (plan.status === 'ineligible') state = 'cantrun';
    else if (!op && plan.status === 'expired') state = 'expired';
    else if (!op) state = RUNNING[plan.status] ? 'applying' : plan.status === 'declined' || plan.status === 'cancelled' ? 'failed' : 'awaiting';
    else if (!isTerminal(op.state)) state = 'applying';
    else if (op.state === 'verified') state = 'verified';
    else if (opts.stopped) state = 'stopped';
    else {
      var done = steps.filter(function (s) { return s.state === 'done' || s.state === 'undone'; }).length;
      var failed = steps.some(function (s) { return s.state === 'failed'; });
      var unclear = steps.some(function (s) { return s.state === 'unclear'; });
      state = failed ? (done ? 'partial' : 'failed') : unclear ? 'partial' : done ? 'stopped' : 'failed';
    }
    return { state: state, steps: steps, settingsUri: manual,
      blockedWhy: whole ? inel.message || null : null, planSettingsUri: whole ? inel.manualUri || null : null };
  }

  // Design v62 (OneStepCards "Nothing to undo"): the host's undo {supported: false, nothingToUndo: true} on a plan or a
  // step. A step flagged irreversible or data_loss changes something, so it is never "nothing to undo".
  function nothingToUndo(undo, flags) {
    return !!(undo && undo.nothingToUndo === true && undo.supported === false) && !needsTick(flags);
  }
  // The card's Undo rule (design ApprovalCard canUndo, v62): the operation's `undo` wins; without it, the badges. Never for
  // a step with a Settings route back or with nothing to undo.
  function stepCanUndo(s) {
    if (!s || s.settings || s.nothingToUndo) return false;
    return s.undo != null ? !!s.undo : !s.restorePoint && (s.badges || []).indexOf('irreversible') < 0;
  }
  // How many steps Undo all would undo (the card shows it from two): done and undoable, never a nothing-to-undo step.
  function undoAllCount(steps) { return (steps || []).filter(function (s) { return !!s && s.state === 'done' && stepCanUndo(s); }).length; }
  // A single change (an AI's request_action, a card's preview, an Undo, a Defender scan) is the same card with one step
  // (design RebuildCases, v53): the change rows on the step, the plan's 'what' as its sentence, its context as name rows
  // (label · value), its trade-offs as the step's Trade-off line (v65), its flags as badges. The operation's state is the step's state.
  // manualUri: the Settings page that changes it back when Optimus can't undo it (else the operation's own). A provider
  // operation (a Defender scan or update) changes nothing to put back, so it never offers one.
  var SINGLE_STEP = { prepared: 'prepared', attempted: 'attempted', verified: 'verified', failed: 'failed', indeterminate: 'indeterminate',
    rolled_back_failed: 'indeterminate', cancelled: 'skipped' };
  function sentence(t) { t = String(t || '').trim(); return !t ? '' : /[.!?…]$/.test(t) ? t : t + '.'; }
  // A single long operation the user stopped (design v54 "Stopped. Anything already done stays."): the host ends it
  // cancelled (op state cancelled, or failed with code cancelled / stopped).
  function singleStopped(op) {
    return !!op && (op.state === 'cancelled' || (op.state === 'failed' && !!op.error && (op.error.code === 'cancelled' || op.error.code === 'stopped')));
  }
  function singleView(plan, op, manualUri) {
    var p = plan || {};
    if (isGuided(p)) return guidedView(p, op);
    var rep = reportedView(p, op);
    if (rep) return rep;
    var rows = (p.rows || []).map(function (r) { return { label: r.label, before: r.before, after: r.after }; })
      .concat((p.context || []).map(function (c) { return { label: c[0], before: c[1], after: null }; }));
    var does = sentence(p.what) || null;
    var step = { n: 1, label: p.label || (p.target && p.target.label) || p.title || '', what: does, rows: rows, tradeoffs: p.tradeoffs || [], flags: p.flags, privilege: p.privilege, undo: p.undo || null };
    var undoable = !!(op && op.undo && op.undo.available);
    var reason = op && op.undo && op.undo.reason === 'undone' ? 'undone' : undoable ? null : !(p.undo && p.undo.supported) ? 'not_supported' : op && op.undo ? op.undo.reason || null : null;
    var o = op ? { state: op.state, manualUri: p.kind === 'provider' ? null : op.manualUri || manualUri || null,
      steps: SINGLE_STEP[op.state] ? [{ n: 1, state: SINGLE_STEP[op.state], undo: { available: undoable, reason: reason }, receipt: op.receipt || null, error: op.error || null }] : [] } : null;
    var inel = p.ineligible ? { message: p.ineligible.message, manualUri: p.ineligible.manualUri || null } : null;
    var stopped = singleStopped(op);
    if (stopped && o) o.steps = [{ n: 1, state: 'skipped', undo: { available: false, reason: reason }, receipt: null, error: null }];
    var v = fixView({ status: p.status, steps: [step], ineligible: inel }, o, { manualUri: p.kind === 'provider' ? null : manualUri || null, stopped: stopped });
    // A report-only action's own failure (design v59): the step's failKey stands for the engine's sentence.
    var fk = !stopped ? failKey(p.actionId, op) : null;
    if (fk && v.steps[0] && v.steps[0].state === 'failed') { v.steps[0].failKey = fk; v.steps[0].note = null; }
    return v;
  }
  // A guided task (design ApprovalCard state 'guided', v54): the host's manual.open plan (kind manual), or a change the
  // app can only send to Windows Settings (ineligible manual_only). The user makes the change; Open settings opens the
  // page, Check again rereads the finding (manual.recheck). Not "Can't run": this is normal, not an error.
  // Design v60: a manual plan with no Windows page (target {type: manual, label}: steps done elsewhere, e.g. the BIOS) has
  // no planSettingsUri, so the card has no Open settings and reads "You do this one yourself.".
  function isGuided(p) {
    return !!p && (p.kind === 'manual' || p.actionId === 'manual.open' || (p.status === 'ineligible' && !!p.ineligible && p.ineligible.code === 'manual_only'));
  }
  function guidedUri(p) {
    if (p.target && p.target.type === 'settings' && p.target.id) return p.target.id;
    return (p.ineligible && p.ineligible.manualUri) || null;
  }
  // The Settings path the host sends beside a Settings page (settingsPath, only on English Windows; design v59 ReportsMore):
  // a manual plan's target, else an ineligible plan's manual route. null when there is none.
  function planSettingsPath(p) {
    var t = p && p.target && p.target.type === 'settings' ? p.target.settingsPath : null;
    var i = p && p.ineligible ? p.ineligible.settingsPath : null;
    return cleanPath(t) || cleanPath(i);
  }
  function cleanPath(x) { return typeof x === 'string' && x.trim() ? x.trim() : null; }
  // The card title stays the finding: the plan's target (the host names the finding), else opts.title (the page's name for
  // the finding). The steps (design v56, v59): opts.guide, the card's own lines (≤ 6); without them one step, the action
  // "Change it in Windows Settings", never the finding's title again. The Settings path (the plan's settingsPath, else
  // opts.settingsPath: the card's or the finding's) is the first step's `does`, and nothing when there is none.
  // opts.recheck: manual.recheck's answer status; verified_by_rescan → the Verified card: the same steps, each done, with
  // the receipt "You changed it in Settings." on the last one.
  // Design v61: a task with no Windows page (target {type: manual}) ends with "You made the change."; asked for by an AI
  // with no guide lines, its single step is "Make the change yourself" with "<AI>’s answer above says how." (the AI's name
  // as on the card's head and its "From <AI>" line: requesterName). Design v63: no AI named ("the assistant", or a change
  // the user started) → "The answer above says how.".
  var GUIDED_STEP = 'Change it in Windows Settings', GUIDED_RECEIPT = 'You changed it in Settings.';
  var PAGELESS_STEP = 'Make the change yourself', PAGELESS_RECEIPT = 'You made the change.', PAGELESS_UNNAMED = 'The answer above says how.';
  function guidedReceipt(pageless) { return pageless ? PAGELESS_RECEIPT : GUIDED_RECEIPT; }
  // The single step of a guided task without guide lines: {label, does} (does null: the Settings path, when there is one).
  function guidedSingleStep(pageless, ai) {
    if (!pageless) return { label: GUIDED_STEP, does: null };
    var name = typeof ai === 'string' && ai.trim() && ai.trim() !== REQUESTER_UNNAMED ? ai.trim() : null;
    return { label: PAGELESS_STEP, does: name ? name + '’s answer above says how.' : PAGELESS_UNNAMED };
  }
  function guidedStep(n, label, does) {
    return { n: n, label: label, does: does || null, rows: [], badges: [], why: null, state: null, note: null, settings: false, undo: false, undoNote: null, receipt: null };
  }
  function guidedView(p, op, opts) {
    opts = opts || {};
    var lines = (opts.guide || []).filter(function (g) { return typeof g === 'string' && g.trim(); }).slice(0, 6);
    var title = (p.target && (p.target.type === 'settings' || p.target.type === 'manual') && p.target.label) || opts.title || p.title || p.label || '';
    var path = planSettingsPath(p) || cleanPath(opts.settingsPath);
    var done = opts.recheck === 'verified_by_rescan';
    // A first line that already names the path ("Open Windows Security › Firewall & network protection.") isn't repeated.
    var uri = guidedUri(p), pageless = !uri, one = guidedSingleStep(pageless, requesterName(p, opts.task || null));
    var steps = lines.length ? lines.map(function (g, i) { return guidedStep(i + 1, g, i === 0 && path && g.indexOf(path) < 0 ? path : null); })
      : [guidedStep(1, one.label, one.does || path)];
    if (done) steps.forEach(function (s, i) { s.state = 'done'; if (i === steps.length - 1) s.receipt = guidedReceipt(pageless); });
    return { state: done ? 'verified' : 'guided', steps: steps, settingsUri: null, blockedWhy: null, planSettingsUri: uri, guided: true, title: title };
  }
  // A report-only action's result (design ApprovalCard state 'reported', v56, v59): the host's operation ends `completed`
  // (m9 §13) with `result` naming what the tool reported, or — DISM only — `failed` with result failed. → the design's
  // result {key}; the card takes the tone from the key. A result the designer gave no key for is null: the card shows the
  // plain "Finished" state with no sentence.
  //   finished      sfc / DISM ended but their result lines could not be read (non-English Windows): sfcUnknown / dismUnknown
  //   explorer.restart  restarted | started | not_running (a completed result, never indeterminate)
  var REPORT_KEY = {
    'repair.sfc': { no_violations: 'sfcClean', repaired: 'sfcRepaired', could_not_repair: 'sfcPartial', finished: 'sfcUnknown' },
    'repair.dism_restorehealth': { healthy: 'dismHealthy', repaired: 'dismRepaired', finished: 'dismUnknown', failed: 'dismFailed' },
    'network.flush_dns': { flushed: 'dnsCleared' },
    'explorer.restart': { restarted: 'explorerRestarted', started: 'explorerStarted', not_running: 'explorerDown' } };
  function opResult(op) {
    var r = op && (op.result || (op.receipt && op.receipt.observed && op.receipt.observed.result) || (op.observed && op.observed.result));
    return typeof r === 'string' ? r : null;
  }
  function reportResult(actionId, op) {
    var k = Object.prototype.hasOwnProperty.call(REPORT_KEY, actionId) ? REPORT_KEY[actionId][opResult(op)] : null;
    return typeof k === 'string' ? { key: k } : null;
  }
  // A report-only action's own failure (host result `failed`) → the design's step failKey (v59): the Failed card shows the
  // designed sentence instead of the engine's, with Check again (no Retry). DISM's failure is a Finished-with-problems card.
  // Design v60: explorer.restart's failure is explorerFailed.
  var FAIL_KEY = { 'repair.sfc': 'sfcFailed', 'network.flush_dns': 'dnsFailed', 'explorer.restart': 'explorerFailed' };
  function failKey(actionId, op) {
    if (!op || op.state !== 'failed' || opResult(op) !== 'failed' || singleStopped(op)) return null;
    return Object.prototype.hasOwnProperty.call(FAIL_KEY, actionId) ? FAIL_KEY[actionId] : null;
  }
  // Check again on a Finished card (design v59 recheck): manual.recheck's status → gone | still.
  function recheckWord(status) { return status === 'verified_by_rescan' ? 'gone' : 'still'; }
  // The operation is a report-only result: completed, or DISM's own failure (not a stop: that is Stopped).
  function isReported(actionId, op) {
    if (!op) return false;
    if (op.state === 'completed') return true;
    return actionId === 'repair.dism_restorehealth' && op.state === 'failed' && opResult(op) === 'failed' && !singleStopped(op);
  }
  // The card: one step with only the action's name (no state words, no rows), and the result.
  function reportedView(p, op) {
    if (!isReported(p.actionId, op)) return null;
    var label = p.label || (p.target && p.target.label) || p.title || '';
    return { state: 'reported', result: reportResult(p.actionId, op), steps: [{ n: 1, label: label }], settingsUri: null, blockedWhy: null, planSettingsUri: null };
  }
  // The running step's progress line (design step.progress {text?, pct?}, v54) from the host's operation progress text, as
  // the host words it (v57): "Quick scan · running for 4 min" → {text} (no percent: the card shows the indeterminate bar);
  // "Repairing the Windows image · 40%" → {text: 'Repairing the Windows image', pct: 40}. A fix's "Step n of k" and a script's "Running · 0:42" have
  // their own lines on their cards: null.
  function stepProgress(text) {
    var t = String(text == null ? '' : text).trim();
    if (!t || /^Step \d+ of \d+$/.test(t) || /^Running · \d+:\d{2}$/.test(t)) return null;
    var pct = null, m = /\s*·\s*(\d{1,3})\s*%$/.exec(t);
    if (m) { pct = Math.max(0, Math.min(100, parseInt(m[1], 10))); t = t.slice(0, m.index).trim(); }
    var out = {};
    if (t) out.text = t;
    if (pct != null) out.pct = pct;
    return t || pct != null ? out : null;
  }
  // The card for any plan the approval card shows: a fix as it is, a single change as one step.
  // opts (guided tasks only): {guide, recheck, settingsPath, task} as guidedView. progress: the operation's progress text, shown
  // under the running step of a single change.
  function planView(plan, op, manualUri, opts) {
    if (plan && plan.kind !== 'fix' && isGuided(plan)) return guidedView(plan, op, opts);
    var v = plan && plan.kind === 'fix' ? fixView(plan, op) : singleView(plan, op, manualUri);
    var pr = opts && opts.progress != null && v.steps.length === 1 && v.steps[0].state === 'running' ? stepProgress(opts.progress) : null;
    if (pr) v.steps[0].progress = pr;
    return v;
  }

  // Why an approval couldn't start (design errorCause, v53), from shell.approvePlan's answer {status, error?} (§8).
  //   busy | scan | benchmark  another change, a scan or a benchmark holds the PC (the plan keeps waiting): Retry
  //   closed     the Windows approval window was closed or declined (uac_cancelled, broker_cancelled …): Retry
  //   cancelled  the plan no longer waits (cancelled, or already declined): Close only
  // A shell error or an answer with no known reason: the design's fallback line ("Couldn’t start: Windows didn’t answer.").
  // approved, stale and expired are not errors: null.
  var BUSY_CAUSE = { action_active: 'busy', scan_active: 'scan', bench_active: 'benchmark' };
  var GONE = { cancelled: 1, declined: 1 };
  var NO_ANSWER = 'Windows didn’t answer.';
  function approvalError(r) {
    var s = (r && r.status) || 'error', e = (r && r.error) || null;
    if (s === 'approved' || s === 'stale' || s === 'expired') return null;
    if (s === 'cancelled' || e === 'cancelled') return { errorCause: 'cancelled', error: null };
    if (s === 'declined') return { errorCause: 'closed', error: null };
    if (s === 'unavailable' && BUSY_CAUSE[e]) return { errorCause: BUSY_CAUSE[e], error: null };
    if (s === 'unavailable' && GONE[e]) return { errorCause: 'cancelled', error: null };
    return { errorCause: null, error: NO_ANSWER };
  }

  // What the page does when shell.approvePlan answers stale or expired (the plan changed or lapsed before approval):
  //   'expire'     a fix: it cannot be previewed again (only the AI proposes a fix); show it as expired, dismissable
  //   'repreview'  a single change shown in its finding card: preview again from live state
  //   'undo'       an Undo plan: ask for the undo again
  //   'close'      anything else: drop the card
  function afterStale(plan, inCard) {
    var p = plan || {};
    if (p.kind === 'fix' && !p.undoOf) return 'expire';
    if (p.undoOf) return 'undo';
    if (inCard && p.actionId) return 'repreview';
    return 'close';
  }

  /* ---------- M9 §5: approved scripts ---------- */
  // Operation state (§5.4) → the design ScriptApproval state.
  var SCRIPT_STATE = { completed: 'finished', failed: 'failed', stopped: 'stopped', timed_out: 'timeout', not_run: 'notrun', indeterminate: 'interrupted' };
  function scriptEnded(state) { return !!SCRIPT_STATE[state]; }
  // §5.8: the run never started (nothing was changed). failed + not_started, or not_run because the app stopped first.
  function scriptNotStarted(op) {
    var e = op && op.error;
    return !!(e && ((op.state === 'failed' && /^not_started(:|$)/.test(e.code || '')) || (op.state === 'not_run' && e.code === 'interrupted')));
  }
  // Why it never started, as the design's notStartedCause (RebuildCases, v53); the banner never carries the host's text.
  // The host sends code not_started (or not_run + interrupted when it stopped first) and its own sentence, so the reason
  // is told apart by a more specific code when there is one, else by that sentence. Anything else: 'failed'.
  var NOT_STARTED_CODE = { hash_mismatch: 'changed', policy_changed: 'policy', interpreter_untrusted: 'powershell', launch_failed: 'failed',
    scripts_unavailable: 'version', admin_unavailable: 'version', app_stopped: 'appstopped',
    // design v54: the user closed the Windows prompt; the administrator helper refused it.
    uac_cancelled: 'declined', broker_cancelled: 'declined', broker_refused: 'helper' };
  var NOT_STARTED_TEXT = [
    [/cancelled in Windows|closed the Windows/i, 'declined'],
    [/administrator helper/i, 'helper'],
    [/hash|did not match|no longer matches|changed after/i, 'changed'],
    [/polic/i, 'policy'],
    [/interpreter|powershell[^.]*(check|trust|verif)/i, 'powershell'],
    [/this version/i, 'version'],
    [/stopped before|closed before|shut down before/i, 'appstopped']];
  function notStartedCause(op) {
    if (!scriptNotStarted(op)) return null;
    var e = op.error || {}, sub = String(e.code || '').replace(/^not_started:?/, '') || String(e.detail || '');
    if (op.state === 'not_run') return 'appstopped';
    if (NOT_STARTED_CODE[sub]) return NOT_STARTED_CODE[sub];
    if (/^guard_/.test(sub)) return 'helper';
    var m = String(e.message || ''), hit = NOT_STARTED_TEXT.filter(function (r) { return r[0].test(m); })[0];
    return hit ? hit[1] : 'failed';
  }
  function scriptState(plan, op) {
    if (scriptNotStarted(op)) return 'notstarted';
    if (op && SCRIPT_STATE[op.state]) return SCRIPT_STATE[op.state];
    if (op) return 'running';
    if (SCRIPT_STATE[plan.status]) return SCRIPT_STATE[plan.status];
    return plan.status === 'approved' || plan.status === 'running' ? 'running' : 'awaiting';
  }
  function lowerFirst(t) { t = String(t || ''); return t.charAt(0).toLowerCase() + t.slice(1); }
  // The restore-point line under the unticked box, by the plan's reason (§5.7: flag_off until the broker batch).
  var SCRIPT_RP_LINE = { unsupported: 'Not available in this version', off: 'Restore points are off on this PC' };
  // The card's props from the plan manifest and its operation. progress: the last operation.state progress ("Running · 0:42").
  function scriptView(plan, op, progress) {
    var s = plan.script || {}, rp = s.restorePoint || {}, rc = (op && op.receipt) || {};
    var m =/(\d+:\d{2})\s*$/.exec(progress || '');
    var rpView = rp.available ? 'available' : rp.reason === 'flag_off' ? 'unsupported' : 'off';
    return { state: scriptState(plan, op), script: s.text || '', parts: s.parts || [], admin: !!s.elevated || plan.privilege === 'admin',
      // "Line 12: turns off a Windows security feature"
      warnings: ((s.guard && s.guard.warnings) || []).map(function (w) { return 'Line ' + w.line + ': ' + lowerFirst(w.text); }),
      restorePoint: rpView, restorePointNote: SCRIPT_RP_LINE[rpView] || null,
      policyNote: !!s.bypassesPolicy, elapsed: m ? m[1] : '0:00', exitCode: rc.exitCode == null ? null : rc.exitCode,
      // §5.8: why a run never started (design 'notstarted').
      notStartedCause: notStartedCause(op),
      sentExcerpt: op && op.sharedOutput ? op.sharedOutput.excerpt : null, contentKey: plan.planHash || s.sha256 || plan.planId };
  }
  // Activity's detail for a run, in the script card's own badge words: "Finished · exit 0", "Failed · exit 1", "Not started", …
  function scriptRunWords(op) {
    var rc = (op && op.receipt) || {}, st = op && op.state;
    if (scriptNotStarted(op)) return 'Not started';
    if (st === 'completed') return 'Finished · exit ' + (rc.exitCode == null ? 0 : rc.exitCode);
    if (st === 'failed') return rc.exitCode == null ? 'Failed' : 'Failed · exit ' + rc.exitCode;
    return { stopped: 'Stopped', timed_out: 'Timed out after 10 min', not_run: 'Not run', indeterminate: 'Interrupted' }[st] || null;
  }
  /* design AgentActivity wording rule (v53): the title is verb + object in plain words, never a tool name (the tool id is
     the mono line). Running: the -ing form with "…"; done: the past tense. Every tool and diagnostic the app has, in one
     table: [running, done]. The designer's own examples are marked (D); the rest follow the rule. */
  var ACT_TOOL = {
    get_findings: ['Reading the report', 'Read the report'],                       // (D)
    propose_fix: ['Proposing a fix', 'Proposed a fix'],                             // (D)
    propose_script: ['Proposing a script', 'Proposed a script'],                    // (D, M9 wording)
    list_capabilities: ['Listing the checks', 'Listed the checks'],
    get_operation: ['Checking progress', 'Checked progress'],
    request_action: ['Proposing a change', 'Proposed a change'],
    get_cards: ['Reading the suggestions', 'Read the suggestions'],
    get_pc_specs: ['Reading PC specs', 'Read PC specs'],
    get_report_history: ['Reading past reports', 'Read past reports'],
    note_card: ['Adding a note', 'Added a note'],
    add_card: ['Adding a suggestion', 'Added a suggestion'],
    get_benchmarks: ['Reading benchmark results', 'Read benchmark results'],
    get_fan_guidance: ['Reading fan advice', 'Read fan advice'],
    get_app_guide: ['Reading the app guide', 'Read the app guide'] };
  var ACT_DIAG = {
    'process.sample': ['Measuring CPU use', 'Measured CPU use'],                    // (D)
    'startup.list': ['Checking startup apps', 'Checked startup apps'],              // (D)
    'network.listeners': ['Checking open ports', 'Checked open ports'],             // (D)
    'events.recent': ['Reading recent errors', 'Read recent errors'],               // (D)
    'storage.volumes': ['Checking disk space', 'Checked disk space'],               // (D)
    'power.inventory': ['Checking power plans', 'Checked power plans'],
    'display.modes': ['Checking displays', 'Checked displays'],
    'devices.problems': ['Checking devices', 'Checked devices'],
    'security.status': ['Checking security', 'Checked security'],
    'sensors.snapshot': ['Reading sensors', 'Read sensors'] };
  var ACT_SCRIPT_RUN = ['Running a script', 'Ran a script'];                        // (D)
  var ACT_OTHER = ['Running a check', 'Ran a check'];
  var DIAGNOSTICS = Object.keys(ACT_DIAG);
  function form(w, done) { return done ? w[1] : w[0] + '…'; }
  // An Activity row's words: { title, detail, tool }. tool: the mono line (the diagnostic's id for run_diagnostic).
  // label: a lookup's "Steam · open port" (→ "Looked up Steam", detail "Open port"); steps: a proposed fix's step count.
  function activityWords(tool, done, label, steps, diagnosticId) {
    if (tool === 'web_lookup') {
      var parts = String(label || '').split(' · '), what = parts[0], rest = parts.slice(1).join(' · ');
      return { title: (done ? 'Looked up ' : 'Looking up ') + (what || 'a finding') + (done ? '' : '…'),
        detail: what && rest ? rest.charAt(0).toUpperCase() + rest.slice(1) : null, tool: tool };
    }
    if (tool === 'run_diagnostic') return { title: form(ACT_DIAG[diagnosticId] || ACT_OTHER, done), detail: null, tool: diagnosticId || tool };
    if (tool === 'script.run') return { title: form(ACT_SCRIPT_RUN, done), detail: null, tool: tool };
    var detail = tool === 'propose_fix' && done && steps ? steps + (steps === 1 ? ' step' : ' steps') : null;
    return { title: form(ACT_TOOL[tool] || ACT_OTHER, done), detail: detail, tool: tool || null };
  }
  // Activity offers the transcript only for a run that started (one that never did has none).
  function scriptHasTranscript(op) { return !!(op && scriptEnded(op.state) && op.startedAt); }

  // m9 §3.1/§3.6/§12 target refs. action.catalog rows carry targetKind (what a targetRef must name; null when the action
  // takes no target); the handlers resolve it from evidence the finding cites. The UI keeps which evidence each kind can
  // name (its collector, and the local key the handler finds it by). TARGET_BY_ACTION is the fallback for an older host
  // whose rows have no targetKind field. The page only ever sends {evidenceId}: never a path, PID or name.
  var TARGET_EVIDENCE = {
    'startup entry': { collector: 'win.security.startup', fits: function (v) { return !!v.entryKey && (v.source === 'run' || v.source === 'folder'); },
      label: function (v) { return v.name || v.image || 'Startup app'; } },
    'process (from a performance sample)': { collector: 'win.perf.sample', fits: function (v) { return v.pid != null && v.createTime != null && !!v.image; },
      label: function (v) { return v.image; } } };
  var TARGET_BY_ACTION = { 'startup.disable': 'startup entry', 'process.end': 'process (from a performance sample)' };
  // The row's targetKind when the host sends the field (null: no target), else the local table.
  function targetKindOf(actionId, catalogEntry) {
    if (catalogEntry && Object.prototype.hasOwnProperty.call(catalogEntry, 'targetKind')) return catalogEntry.targetKind || null;
    return TARGET_BY_ACTION[actionId] || null;
  }
  function needsTarget(actionId, catalogEntry) { return !!targetKindOf(actionId, catalogEntry); }
  // The cited evidence an action can target, in citation order: [{evidenceId, label}] (a repeated label gets " (2)", …).
  // A target kind the UI has no evidence rule for gives no choices (the card falls back to the manual route).
  function targetChoices(actionId, evidenceIds, evidenceById, catalogEntry) {
    var t = TARGET_EVIDENCE[targetKindOf(actionId, catalogEntry)], seen = {};
    if (!t) return [];
    return (evidenceIds || []).filter(function (id, i, a) { return a.indexOf(id) === i; }).map(function (id) { return evidenceById[id]; })
      .filter(function (e) { return e && e.collectorId === t.collector && e.value && t.fits(e.value); })
      .map(function (e) {
        var label = String(t.label(e.value)), n = seen[label] = (seen[label] || 0) + 1;
        return { evidenceId: e.id, label: n > 1 ? label + ' (' + n + ')' : label };
      });
  }
  // Who asked for a plan, as the card names it. The host's why.by is the AI's display name (AgentService.AiName: "ChatGPT"
  // for OpenAI kinds, else "Claude"); a single change asked without a reason has no why, so the requesting task's own
  // recipient gives the same name when the page knows that task. A paired window is Claude Code ("Claude"); the user "You".
  // Design v62 (OneStepCards "Who asked"): a change the user started from a card is null (the line is left out); an AI
  // request whose AI isn't known is "the assistant".
  var REQUESTER_UNNAMED = 'the assistant';
  function aiNameOf(kind) { return kind === 'openai_api' || kind === 'chatgpt_plan' ? 'ChatGPT' : 'Claude'; }
  function requesterName(plan, task) {
    var by = (plan && plan.requestedBy) || {}, why = plan && plan.why;
    if (by.kind === 'user') return null;
    if (why && typeof why.by === 'string' && why.by) return why.by;
    if (by.kind === 'claude_code') return 'Claude';
    if (task && task.recipient && by.taskId && task.taskId === by.taskId) return aiNameOf(task.recipient.kind);
    return REQUESTER_UNNAMED;
  }
  // action.preview's params from a card: the finding and its report, and a target only as the evidence the finding cites.
  function previewParams(idempotencyKey, actionId, findingId, reportId, evidenceId) {
    var p = { idempotencyKey: idempotencyKey, actionId: actionId, findingId: findingId };
    if (evidenceId) { p.reportId = reportId; p.targetRef = { evidenceId: evidenceId }; }
    if (actionId === 'power.select_scheme') p.parameters = { personality: 'balanced' };
    return p;
  }

  /* ---------- m9 §17 Fix for me and administrator checks (design v75–v83): the host's state → the design's props ---------- */
  // card.fix.state (contracts/schemas/fix.schema.json) → FixPanel state. A script plan waiting on the card is 'script'
  // (the app decides that from the plan's kind).
  var FIX_PANEL = { working: 'working', waiting_checks: 'checks', checking: 'checksRunning', plan_ready: 'plan', blocked: 'blocked',
    running: 'applying', fixed: 'verified', done_reduced: 'doneLeftOut', partly_fixed: 'partial', finished: 'finished', failed: 'failed',
    unclear: 'unclear', stopped: 'stopped', needs_you: 'needsYou', nothing_to_fix: 'nothing', no_fix: 'noFix' };
  // The FixPanel states that show the plan (ApprovalCard props from the plan and its operation).
  var FIX_WITH_PLAN = { plan: 1, applying: 1, verified: 1, doneLeftOut: 1, partial: 1, finished: 1, failed: 1, unclear: 1, stopped: 1 };
  // → {state, withPlan} or null (no fix on the card: its normal state).
  function fixPanelState(fix) {
    var st = fix && FIX_PANEL[fix.state];
    if (!st) return null;
    return { state: st, withPlan: !!FIX_WITH_PLAN[st] };
  }
  // blocked (design v82 FixMoreStates): the ineligible plan's code → the design's cause; only adminOff offers Permissions.
  var BLOCKED_CAUSE = { admin_not_allowed: 'adminOff', admin_unavailable: 'adminUnavailable', changed_since: 'changed', stale_plan: 'changed',
    managed: 'managed', 'protected': 'protected', conflict: 'conflict', too_large: 'tooLarge', unsupported: 'unsupported', not_supported: 'unsupported',
    hardware_limit: 'unsupported' };
  function blockedCause(code) { return BLOCKED_CAUSE[code] || 'other'; }
  // plan.leftOut [{actionId, label, what, code}] → the design's [{label, reason: off | unavailable}].
  function leftOutView(plan) {
    return ((plan && plan.leftOut) || []).map(function (x) { return { label: x.label, reason: x.code === 'admin_not_allowed' ? 'off' : 'unavailable' }; });
  }
  // plan.removed [{label}] → the labels (design ApprovalCard removed).
  function removedLabels(plan) { return ((plan && plan.removed) || []).map(function (x) { return typeof x === 'string' ? x : x && x.label; }).filter(Boolean); }
  // Approve in a card with some steps switched off (ApprovalCard onApprove(keptIndexes)): null when every step is kept (the
  // normal approval), else action.narrow's keep, the plan's own step numbers in order.
  function narrowKeep(keptIndexes, steps) {
    var list = steps || [], kept = (keptIndexes || []).filter(function (i) { return i >= 0 && i < list.length; }).sort(function (a, b) { return a - b; });
    if (!kept.length || kept.length === list.length) return null;
    return kept.map(function (i) { return list[i].n != null ? list[i].n : i + 1; });
  }
  // FixButton availability (design FixForMe): unset (null) without an AI with tools or on a card that isn't open; nochanges
  // when changes are off; busy while a turn runs; else yes. s: {tools, open, changes, busy}.
  function fixAvailability(s) {
    if (!s || !s.tools || !s.open) return null;
    if (s.changes === 'off') return 'nochanges';
    return s.busy ? 'busy' : 'yes';
  }
  // An administrator-checks request (contracts/schemas/checks.schema.json; agent.checks) → AdminChecks {state, cause,
  // secondsLeft, checks, unread} (design v83). nowMs: the clock.
  //   awaiting_user → ask, or wait while retryAt is ahead (seconds left, rounded up; back to ask at 0) · running → running
  //   complete → done, with unread = the checks whose own state isn't complete (the head line says some or none were read)
  //   declined (user_declined | uac_cancelled | broker_cancelled) → declined · expired → expired
  //   failed timeout → failed/timeout · failed admin_unavailable (not started) → unavailable · failed (else) → failed/failed
  //   cancelled permissions | ai → that cause · task_ended → ended, or ai when the app stopped that task because the user
  //   chose another AI (q.switchedAi: §16 stops the old AI's task before the host sees the change) · stopped → stopped ·
  //   exhausted → limit · admin_unavailable → unavailable. unfit: a code the design has no words for (failed is shown).
  var CANCEL_CAUSE = { permissions: 'permissions', ai: 'ai', stopped: 'stopped', task_ended: 'ended', exhausted: 'limit' };
  function checksView(q, nowMs) {
    if (!q) return null;
    var list = q.checks || [];
    var out = { state: 'ask', cause: null, secondsLeft: null, checks: list.map(function (c) { return c.label; }), unread: [], unfit: false };
    if (q.state === 'awaiting_user') {
      var left = q.retryAt ? Date.parse(q.retryAt) - (nowMs == null ? Date.now() : nowMs) : 0;
      if (left > 0) { out.state = 'wait'; out.secondsLeft = Math.ceil(left / 1000); }
    } else if (q.state === 'running') out.state = 'running';
    else if (q.state === 'complete') {
      out.state = 'done';
      list.forEach(function (c, i) { if (c.state !== 'complete') out.unread.push(i); });
    }
    else if (q.state === 'declined') out.state = 'declined';
    else if (q.state === 'expired') out.state = 'expired';
    else if (q.state === 'failed' && q.code === 'admin_unavailable') out.state = 'unavailable';
    else if (q.state === 'failed') { out.state = 'failed'; out.cause = q.code === 'timeout' ? 'timeout' : 'failed'; }
    else if (q.state === 'cancelled' && q.code === 'admin_unavailable') out.state = 'unavailable';
    else if (q.state === 'cancelled' && q.code === 'task_ended' && q.switchedAi) { out.state = 'cancelled'; out.cause = 'ai'; }
    else if (q.state === 'cancelled' && CANCEL_CAUSE[q.code]) { out.state = 'cancelled'; out.cause = CANCEL_CAUSE[q.code]; }
    else { out.state = 'failed'; out.cause = 'failed'; out.unfit = true; }
    return out;
  }
  // FixPanel working's checksResult (design v82/v83) for a request that ended: how it ended, as the line under the working
  // line says it; null while it is open.
  function checksResult(q) {
    var v = checksView(q, 0);
    if (!v || v.state === 'ask' || v.state === 'wait' || v.state === 'running') return null;
    if (v.state === 'done') return { result: !v.unread.length ? 'done' : v.unread.length >= v.checks.length ? 'noneRead' : 'partial', cause: null };
    if (v.state === 'failed') return { result: v.cause === 'timeout' ? 'timeout' : 'failed', cause: v.cause };
    return { result: v.state, cause: v.cause };
  }
  // Design v84: a card-bound script's pointer (FixPanel script, scriptState): awaiting → waiting; approved / running → running;
  // any end state → ended.
  function scriptPointer(plan, op) {
    if (op && scriptEnded(op.state)) return 'ended';
    if (op || (plan && (plan.status === 'approved' || plan.status === 'running'))) return 'running';
    return plan && plan.status === 'awaiting_user' ? 'waiting' : 'ended';
  }
  var CHECKS_OPEN = { awaiting_user: 1, running: 1 };
  function checksOpen(q) { return !!q && !!CHECKS_OPEN[q.state]; }

  var api = { fixPanelState: fixPanelState, blockedCause: blockedCause, checksResult: checksResult, scriptPointer: scriptPointer, leftOutView: leftOutView, removedLabels: removedLabels, narrowKeep: narrowKeep,
    fixAvailability: fixAvailability, checksView: checksView, checksOpen: checksOpen,
    badgeKeys: badgeKeys, stepTradeoff: stepTradeoff, needsTick: needsTick, fixView: fixView, isTerminal: isTerminal, FLAG_BADGE: FLAG_BADGE, afterStale: afterStale,
    scriptView: scriptView, scriptState: scriptState, scriptEnded: scriptEnded, scriptRunWords: scriptRunWords, scriptHasTranscript: scriptHasTranscript,
    notStartedCause: notStartedCause, activityWords: activityWords, DIAGNOSTICS: DIAGNOSTICS,
    singleView: singleView, planView: planView, approvalError: approvalError, stepProgress: stepProgress, isGuided: isGuided, guidedView: guidedView,
    reportResult: reportResult, isReported: isReported, failKey: failKey, recheckWord: recheckWord, planSettingsPath: planSettingsPath,
    needsTarget: needsTarget, targetKindOf: targetKindOf, targetChoices: targetChoices, previewParams: previewParams, requesterName: requesterName,
    guidedReceipt: guidedReceipt, guidedSingleStep: guidedSingleStep, nothingToUndo: nothingToUndo, stepCanUndo: stepCanUndo, undoAllCount: undoAllCount };
  if (root) { root.OptTool = root.OptTool || {}; root.OptTool.fixPlan = api; }
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof window !== 'undefined' ? window : null);
