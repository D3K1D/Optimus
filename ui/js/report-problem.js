/* Report a problem (docs/error-logging.md §2): the logo's app menu, the report preview panel and the light prompt after
   a crash or a failed operation. Production copy of the design's "Report a problem" (design/…/bundle.js: AppMenu,
   ReportProblemPanel, ProblemPrompt), with the shell calls behind them:
   shell.problemReport {operation?} → {reportId, text, title, trimmed}; shell.openIssue {reportId} → {status};
   shell.crashStatus {} → {crashed, version, process?, at?}; shell.crashDismiss {}.
   The controller and the failure checks at the top also load in Node (module.exports) for tests. */
(function (root) {
  'use strict';

  /* ---------- which endings are errors ---------- */
  // Operation states that ended without the change (as app-flow's FAILED_OP); a stop is never one.
  var FAILED_OP = { failed: 1, indeterminate: 1, rolled_back_failed: 1 };
  var STOP_CODES = { cancelled: 1, stopped: 1 };
  // A failed AI turn that is a stop, a refusal or something the user acts on (sign in, permissions), not an error.
  var AI_NOT_ERROR = { cancelled: 1, stopped: 1, busy: 1, rate_limited: 1, tool_blocked: 1, not_allowed: 1, auth: 1, expired: 1, exhausted: 1 };
  var failed = {
    // scan.finished / scan.get: the scan itself ended failed (a stop ends cancelled).
    scan: function (payload) { return !!payload && payload.status === 'failed'; },
    // scan.start refused: "a scan is running" (-32004) and busy / rate limited (-32013) are refusals, not errors.
    scanStart: function (err) { var c = err && err.code; return c !== -32004 && c !== -32013; },
    // An operation that ended failed, not one the user stopped; scripts report their own exit, never an app error.
    fix: function (op, planKind) {
      if (!op || !FAILED_OP[op.state] || planKind === 'script') return false;
      return !(op.error && STOP_CODES[op.error.code]);
    },
    // A benchmark run that ended failed or interrupted (cancelled and on battery are stops).
    benchmark: function (run) { return !!run && (run.state === 'failed' || run.state === 'interrupted'); },
    // agent.turn failed with an error code, or agent.task failed.
    aiTurn: function (pl) { return !!pl && pl.status === 'failed' && !AI_NOT_ERROR[pl.code]; },
    aiTask: function (pl) { return !!pl && pl.status === 'failed'; }
  };

  /* ---------- the controller ---------- */
  /** opts: { flash(word), clipboard: {writeText} (default navigator.clipboard), onChange(state) }. */
  function createReportController(backend, opts) {
    opts = opts || {};
    var state = { prompt: null, panel: null, version: null };
    var dismissed = {};   // problem key → true: once dismissed or reported, that problem's prompt never returns
    var seq = 0;
    function set(patch) { state = Object.assign({}, state, patch); if (opts.onChange) opts.onChange(state); }
    function putPanel(patch) { if (state.panel) set({ panel: Object.assign({}, state.panel, patch) }); }
    function flash(w) { if (opts.flash) opts.flash(w); }
    function crashDismiss() { return backend.call('shell.crashDismiss', {}).catch(function () {}); }

    // At most one prompt at a time: the newest problem replaces the one shown.
    function raise(cause, key, ai) {
      key = key || cause;
      if (dismissed[key]) return false;
      set({ prompt: { cause: cause, key: key, ai: ai || null } });
      return true;
    }
    function checkCrash() {
      return backend.call('shell.crashStatus', {}).then(function (r) {
        if (r && typeof r.version === 'string') set({ version: r.version });
        if (r && r.crashed) raise('crash', 'crash:' + (r.at || ''));
      }, function () {});
    }
    function dismiss() {
      var p = state.prompt; if (!p) return;
      dismissed[p.key] = true;
      set({ prompt: null });
      if (p.cause === 'crash') crashDismiss();
    }
    // The preview builds the report; a crash's markers are cleared once its report was built.
    function open(operation) {
      var n = ++seq;
      set({ panel: { state: 'building', text: '', reportId: null, trimmed: false, operation: operation || null } });
      return backend.call('shell.problemReport', operation ? { operation: operation } : {}).then(function (r) {
        if (n !== seq || !state.panel) return;
        putPanel({ state: 'ready', text: (r && r.text) || '', reportId: (r && r.reportId) || null, trimmed: !!(r && r.trimmed) });
        if (operation === 'crash') crashDismiss();
      }, function () {
        if (n !== seq) return;
        set({ panel: null });
        flash('Not available');
      });
    }
    function report() {
      var p = state.prompt; if (!p) return;
      dismissed[p.key] = true;
      set({ prompt: null });
      return open(p.cause);
    }
    function close() { seq++; set({ panel: null }); }
    // Copy: the full text, always (the GitHub link may carry a shorter one).
    function copy() {
      var pn = state.panel; if (!pn || pn.state === 'building') return Promise.resolve();
      var cb = opts.clipboard || (root && root.navigator && root.navigator.clipboard) || (typeof navigator !== 'undefined' ? navigator.clipboard : null);
      var fail = function () { flash('Copy failed'); };
      if (!cb || !cb.writeText) { fail(); return Promise.resolve(); }
      var done;
      try { done = Promise.resolve(cb.writeText(pn.text)); } catch (e) { fail(); return Promise.resolve(); }
      return done.then(function () { if (state.panel && state.panel.reportId === pn.reportId) putPanel({ state: 'copied' }); }, fail);
    }
    // Open GitHub: only the report's id goes back; the shell builds the page from the report it holds.
    function openIssue() {
      var pn = state.panel; if (!pn || pn.state === 'building' || !pn.reportId) return Promise.resolve();
      var mine = function () { return state.panel && state.panel.reportId === pn.reportId; };
      return backend.call('shell.openIssue', { reportId: pn.reportId }).then(function (r) {
        if (mine()) putPanel({ state: r && r.status === 'opened' ? 'opened' : 'browserFailed' });
      }, function (err) {
        // Pressed again too soon: the page was just opened, nothing changes.
        if (err && err.code === -32013) return;
        if (mine()) putPanel({ state: 'browserFailed' });
      });
    }
    return { get: function () { return state; }, raise: raise, checkCrash: checkCrash, dismiss: dismiss, report: report,
      open: open, close: close, copy: copy, openIssue: openIssue };
  }

  var api = { failed: failed, createReportController: createReportController };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  if (!root || !root.React || !root.OptTool || !root.OptTool.Icon) return;

  /* ---------- components ---------- */
  var React = root.React, h = React.createElement, OT = root.OptTool;
  var Icon = OT.Icon, Button = OT.Button, GlassPanel = OT.GlassPanel, Logo = OT.Logo, Popover = OT.Popover, cx = OT.ui.cx;

  /* The report preview: exactly the text that would leave the PC. The user opens a prefilled GitHub page and submits it there. */
  var REPORT_ISSUES_URL = 'github.com/D3K1D/Optimus/issues/new';
  function ReportProblemPanel(p) {
    var st = p.state || 'ready', building = st === 'building';
    var status = st === 'copied' ? 'Copied. You can paste it anywhere.'
      : st === 'opened' ? 'Opened in your browser. Say what you were doing, then press Submit there.'
        : st === 'browserFailed' ? 'Couldn’t open your browser. Copy the report and paste it at ' + (p.issuesUrl || REPORT_ISSUES_URL) + '.' : '';
    return h(GlassPanel, { strong: true, className: cx('ot-rp', p.className), role: 'region', 'aria-labelledby': 'ot-rp-t', tabIndex: -1, 'data-rp': true, onKeyDown: p.onKeyDown },
      h('div', { className: 'ot-rp-head' },
        h('h2', { id: 'ot-rp-t', className: 'text-headline' }, 'Report a problem'),
        p.onClose && h('button', { type: 'button', className: 'ot-rp-x', 'aria-label': 'Close', onClick: p.onClose }, h(Icon, { name: 'close', size: 18 }))),
      h('p', { className: 'text-callout ot-muted' }, 'This is exactly what would be sent. It has no names, files or findings.'),
      building ? h('div', { className: 'ot-rp-box is-building text-mono-sm', role: 'status' }, h(Logo, { size: 16, mono: true, decorative: true, spin: true }), 'Building the report…')
        : h('pre', { className: 'ot-rp-box text-mono-sm', tabIndex: 0, role: 'region', 'aria-label': 'Report text' }, p.text || ''),
      p.trimmed && !building && h('p', { className: 'text-mono-sm ot-muted ot-rp-note' }, h(Icon, { name: 'alert', size: 14 }), 'Too long for GitHub’s link, so the page gets a shorter version. Copy gives you all of it.'),
      h('p', { className: cx('text-callout ot-rp-status', st === 'browserFailed' && 'is-warn'), role: 'status' }, status),
      h('div', { className: 'ot-rp-foot' },
        h('span', { className: 'text-mono-sm ot-muted ot-rp-hint' }, 'Sending needs a free GitHub account.'),
        h('div', { className: 'ot-approval-actions' },
          p.onClose && h(Button, { variant: 'ghost', onClick: p.onClose }, 'Close'),
          h(Button, { variant: 'neu', icon: st === 'copied' ? 'check' : 'copy', disabled: building, onClick: p.onCopy }, st === 'copied' ? 'Copied' : 'Copy'),
          h(Button, { variant: 'primary', icon: 'external', disabled: building, onClick: p.onOpen }, 'Open GitHub'))));
  }

  /* After a crash or a failed operation: a light, non-blocking prompt. It never takes focus. */
  var PROBLEM_TEXT = {
    crash: 'Optimus closed unexpectedly last time.',
    scan: 'The scan stopped with an error.',
    fix: 'The change stopped with an error.',
    benchmark: 'The benchmark stopped with an error.',
    ai: function (ai) { return (ai || 'Claude') + '’s task stopped with an error.'; }
  };
  function ProblemPrompt(p) {
    var t = PROBLEM_TEXT[p.cause] || 'Something stopped with an error.';
    return h(GlassPanel, { strong: true, className: cx('ot-problem', p.className), role: 'status' },
      h(Icon, { name: 'alert', size: 18, className: 'ot-problem-icon' }),
      h('span', { className: 'text-callout ot-problem-text' }, typeof t === 'function' ? t(p.ai) : t),
      h('span', { className: 'ot-problem-btns' },
        h(Button, { variant: 'neu', size: 'sm', onClick: p.onReport }, 'Report it'),
        h(Button, { variant: 'ghost', size: 'sm', onClick: p.onDismiss }, 'Not now')));
  }

  /* The logo in the top row opens a small app menu: always reachable, and the toolbar stays as it is. */
  function AppMenu(p) {
    var s = React.useState(!!p.open), open = s[0], setOpen = s[1];
    return h('div', { className: cx('ot-appmenu', p.className) },
      h('button', { type: 'button', id: 'ot-appmenu-btn', className: 'ot-appmenu-btn', 'aria-label': 'Optimus menu', 'aria-haspopup': 'true', 'aria-expanded': open, 'aria-controls': 'ot-appmenu-pop', onClick: function () { setOpen(!open); } },
        h(Logo, { size: 28, decorative: true }), h(Icon, { name: 'chevron', size: 14, className: 'ot-appmenu-chev' })),
      open && h(Popover, { id: 'ot-appmenu-pop', label: 'Optimus menu', toggleId: 'ot-appmenu-btn', onClose: function () { setOpen(false); }, className: 'ot-appmenu-pop' },
        h('ul', { className: 'ot-menu-list' },
          h('li', null, h('button', { type: 'button', 'data-menu-item': true, className: 'ot-menu-item ot-menu-item-2', onClick: function () { setOpen(false); if (p.onReport) p.onReport(); } },
            h(Icon, { name: 'alert', size: 20, className: 'ot-menu-icon' }),
            h('span', { className: 'ot-menu-text' }, h('span', { className: 'ot-menu-title' }, 'Report a problem'), h('span', { className: 'text-mono-sm ot-muted' }, 'Tell the developers what went wrong'))))),
        p.version && h('div', { className: 'ot-appmenu-ver text-mono-sm ot-muted' }, 'Optimus ' + p.version)));
  }

  /* ---------- state: useProblemReport ---------- */
  /** opts: { flash(word), version }. Returns { raise(cause, key, ai), ai (ref: the AI's name for the ai prompt),
      menu(), prompt(), panel() }: the three pieces ready to place. */
  function useProblemReport(backend, opts) {
    var s = React.useState({ prompt: null, panel: null, version: null }), st = s[0], setSt = s[1];
    var optsRef = React.useRef(opts); optsRef.current = opts;
    var ai = React.useRef(null);
    var ctl = React.useRef(null);
    if (!ctl.current) ctl.current = createReportController(backend, { flash: function (w) { if (optsRef.current && optsRef.current.flash) optsRef.current.flash(w); }, onChange: setSt });
    var c = ctl.current;
    React.useEffect(function () { c.checkCrash(); }, []);
    // Opening moves focus to the panel (like the benchmark panel); closing returns it to the menu button.
    var wasOpen = React.useRef(false), isOpen = !!st.panel;
    React.useEffect(function () {
      if (isOpen && !wasOpen.current) {
        var el = document.querySelector('[data-rp]');
        if (el) { if (el.scrollIntoView) el.scrollIntoView({ block: 'nearest' }); if (el.focus) el.focus({ preventScroll: true }); }
      } else if (!isOpen && wasOpen.current) {
        var a = document.activeElement, btn = document.getElementById('ot-appmenu-btn');
        if (btn && (!a || a === document.body)) btn.focus();
      }
      wasOpen.current = isOpen;
    }, [isOpen]);
    function closePanel() { var b = document.getElementById('ot-appmenu-btn'); c.close(); if (b) b.focus(); }
    return {
      raise: function (cause, key) { return c.raise(cause, key, cause === 'ai' ? ai.current : null); },
      ai: ai, open: !!st.panel,
      menu: function () { return h(AppMenu, { version: (optsRef.current && optsRef.current.version) || st.version, onReport: function () { c.open(); } }); },
      prompt: function (className) {
        return st.prompt && h(ProblemPrompt, { key: st.prompt.key, className: className, cause: st.prompt.cause, ai: st.prompt.ai, onReport: c.report, onDismiss: c.dismiss });
      },
      panel: function (className) {
        var pn = st.panel; if (!pn) return null;
        return h(ReportProblemPanel, { key: 'rp', className: className, state: pn.state, text: pn.text, trimmed: pn.trimmed,
          onClose: closePanel, onCopy: c.copy, onOpen: c.openIssue,
          onKeyDown: function (e) { if (e.key === 'Escape' && !e.defaultPrevented) { e.preventDefault(); e.stopPropagation(); closePanel(); } } });
      }
    };
  }

  Object.assign(OT, { ReportProblemPanel: ReportProblemPanel, ProblemPrompt: ProblemPrompt, AppMenu: AppMenu, useProblemReport: useProblemReport, problemReport: api });
})(typeof window !== 'undefined' ? window : null);
