/* The whole app: login → setup → prompt → scan → report and follow-ups. Production copy of the prototype's AppFlow
   (design/design-system/components/bundle.js): same screens, transitions and timers, but every piece of data and
   every state change the contract gives the backend goes through OptTool.backend (see backend.js). */
(function () {
  var React = window.React, h = React.createElement, OT = window.OptTool, cx = OT.ui.cx, CATS = OT.ui.CATS;
  var WaveBackground = OT.WaveBackground, GlassPanel = OT.GlassPanel, Button = OT.Button, Logo = OT.Logo, LoginPanel = OT.LoginPanel,
    ChoicePanel = OT.ChoicePanel, SpecScanPanel = OT.SpecScanPanel, PromptBox = OT.PromptBox, StarterPrompt = OT.StarterPrompt,
    ScanStatus = OT.ScanStatus, InsightCard = OT.InsightCard, ReviewComplete = OT.ReviewComplete, SharePreview = OT.SharePreview,
    ReportSheet = OT.ReportSheet, ConnectionPill = OT.ConnectionPill, PairingCard = OT.PairingCard, GrantSheet = OT.GrantSheet,
    PlanCard = OT.PlanCard, ActivityPanel = OT.ActivityPanel, AgentAnswers = OT.AgentAnswers, ContinuePanel = OT.ContinuePanel, ConnectionDetail = OT.ConnectionDetail;
  var connectionState = OT.connectionState, routeAsk = OT.routeAsk, taskEnded = OT.taskEnded, reconcileAnswers = OT.reconcileAnswers, runningTurn = OT.runningTurn,
    answerKey = OT.answerKey, answerPanels = OT.answerPanels;
  var KEEP_ANSWERS = 40;                 // answers kept in memory (History), as reconcileAnswers keeps
  var DELETED_KEY = 'ot.hist.del';       // History rows the user deleted (task:turn keys), per window
  var TASKS_KEY = 'ot.tasks';            // recent tasks, so History comes back after a restart
  function loadDeleted() { try { var l = JSON.parse(storeGet(DELETED_KEY) || '[]'); var m = {}; (Array.isArray(l) ? l : []).forEach(function (k) { m[k] = true; }); return m; } catch (e) { return {}; } }
  function recentTasks() { try { var l = JSON.parse(storeGet(TASKS_KEY) || '[]'); return Array.isArray(l) ? l.filter(function (x) { return typeof x === 'string'; }) : []; } catch (e) { return []; } }
  function rememberTask(id) { storeSet(TASKS_KEY, JSON.stringify([id].concat(recentTasks().filter(function (x) { return x !== id; })).slice(0, 5))); }

  var USE_OPTIONS = ['Work', 'Gaming', 'Editing', 'Browsing', 'Hosting / development'];
  var GOAL_OPTIONS = ['Check for security concerns', 'Improve PC performance', 'Optimize Windows', 'Customize Windows'];
  // Inventory rows shown while discovery runs; facts land in their `group`, or by kind when no group is given.
  var SPEC_ROWS = ['Specs', 'Graphics', 'Displays', 'Devices', 'Audio', 'Network', 'Drivers'];
  var KIND_ROW = { computer: 'Specs', gpu: 'Graphics', display: 'Displays', input: 'Devices', audio: 'Audio', network: 'Network', driver: 'Drivers' };
  var FULL_AUDIT = /^\s*(begin|start|run)( a)?( new)? scan/i;
  var DO_THAT = /\b(do (that|it)( for me)?|go ahead|fix (it|that))\b/i;
  var STEP_STAGE = { provider: 'login', use: 'use', goal: 'goal', specs: 'specs', complete: 'main' };
  var STEP_ORDER = ['provider', 'use', 'goal', 'specs', 'complete'];
  var OPTION_IDS = { Ignore: 'ignore', Hide: 'ignore', Done: 'done', Later: 'later', Decline: 'decline' };
  var SNOOZE_DAYS = 30;
  var TASK_WORD_KEYS = { stopped: 1, expired: 1, exhausted: 1, failed: 1 };   // task statuses an ask can be refused for
  // Ignore's compact scope choice (protocol §9 finding.setDisposition), each saying what it hides. "All like this"
  // names the rule's kind (first matching rule-ID pattern, else the card's category), or stays generic.
  var RULE_KINDS = [[/^net\./, 'open-port alerts'], [/^startup\.unsigned/, 'unsigned-startup alerts'], [/^startup\./, 'startup alerts'],
    [/^(fw|firewall)\./, 'firewall alerts'], [/^av\./, 'antivirus alerts'], [/^(uac|secureboot|security)\./, 'security alerts'],
    [/^storage\./, 'low-storage alerts'], [/^perf\./, 'performance alerts'], [/^power\./, 'power-plan alerts'], [/^privacy\./, 'privacy alerts'],
    [/^reliability\./, 'stability alerts'], [/^display\./, 'display alerts'], [/^start\./, 'Start menu alerts'], [/^devices\./, 'device alerts'],
    [/^apps\./, 'large-app alerts']];
  var CATEGORY_KINDS = { security: 'security alerts', performance: 'performance alerts', storage: 'storage alerts', privacy_telemetry: 'privacy alerts',
    diagnostics: 'diagnostics alerts', customization: 'Windows tweak alerts' };
  function ruleKind(ruleId, category) {
    for (var i = 0; i < RULE_KINDS.length; i++) if (RULE_KINDS[i][0].test(ruleId || '')) return RULE_KINDS[i][1];
    return CATEGORY_KINDS[category] || null;
  }
  function ignoreChoices(ruleId, category) {
    var kind = ruleKind(ruleId, category);
    return [{ id: 'finding', label: 'Hide this', title: 'Hide this finding, now and in later scans.' },
      { id: 'snooze', label: 'Snooze ' + SNOOZE_DAYS + ' days', title: 'Hide it for ' + SNOOZE_DAYS + ' days, then show it again.' },
      { id: 'rule', label: kind ? 'Hide all ' + kind : 'Hide all like this', title: 'Hide every finding of this kind, now and in later scans.' }];
  }
  // m2-collectors §5: the only destinations the UI offers (the desktop shell checks again).
  var SETTINGS_CATALOG = ['windowsdefender://threat', 'windowsdefender://network', 'ms-settings:windowsdefender', 'ms-settings:startupapps',
    'ms-settings:powersleep', 'ms-settings:storagesense', 'ms-settings:appsfeatures', 'ms-settings:privacy-feedback',
    'ms-settings:display-advanced', 'ms-settings:personalization-start', 'ms-settings:windowsupdate-history', 'ms-settings:troubleshoot'];
  var COVERAGE_WORD = { complete: 'Checked', partial: 'Partly checked', denied: 'No access', unsupported: 'Not supported', timed_out: 'Timed out',
    failed: 'Failed', skipped: 'Skipped', cancelled: 'Stopped' };
  function notSupported(err) { return err && err.code === -32601; }

  /* ---------- M3: providers, tasks, grants (contracts/m3-agent.md) ---------- */
  // §3.3 diagnostics, in plain words; used when the backend does not list them itself.
  var DIAG_LABELS = { 'power.inventory': 'Power plans', 'process.sample': 'CPU sample', 'startup.list': 'Startup apps', 'events.recent': 'Event logs',
    'storage.volumes': 'Drives', 'network.listeners': 'Open ports', 'display.modes': 'Displays', 'devices.problems': 'Devices', 'security.status': 'Security status' };
  var DIAG_WORDS = { 'process.sample': ['Measuring CPU', 'Measured CPU'], 'power.inventory': ['Reading power plans', 'Read power plans'],
    'startup.list': ['Reading startup apps', 'Read startup apps'], 'events.recent': ['Reading event logs', 'Read event logs'],
    'storage.volumes': ['Reading drives', 'Read drives'], 'network.listeners': ['Reading open ports', 'Read open ports'],
    'display.modes': ['Reading displays', 'Read displays'], 'devices.problems': ['Reading devices', 'Read devices'],
    'security.status': ['Reading security status', 'Read security status'] };
  var DEFAULT_LIMITS = { diagnosticCalls: 10, totalBytes: 131072, turns: 8 };
  var NATIVE_KIND = { 'claude-code': 'claude_code_mcp', codex: 'codex_mcp' };
  var TILE_OF = { claude_code_mcp: 'claude-code', codex_mcp: 'codex', anthropic_api: 'api-key', openai_api: 'api-key', chatgpt_plan: 'chatgpt' };
  var CLIENT_KIND = { claude_code: 'claude_code_mcp', codex: 'codex_mcp' };
  var CLIENT_OF_KIND = { claude_code_mcp: 'claude_code', codex_mcp: 'codex' };
  var READY_ROWS = { paired: 1, active: 1, client_closed: 1 };
  var ORIGIN_LABEL = { in_app: 'In app', claude_code: 'Claude Code', codex: 'Codex' };
  var WAIT_HELP_MS = 60000;
  var ACTION_WORD = { addKey: 'Add key', updateKey: 'Update key', retry: 'Retry', signIn: 'Sign in', install: 'Set up', start: 'Start', approve: 'Approve', activity: 'Activity', showCommand: 'Show command' };
  /* ---------- M5: chat with every AI in the app (contracts/m5-chat.md) ---------- */
  var SCAN_PROMPT = 'Look over my latest report, PC specs and past reports. Tell me the 3 things that matter most, and improve the suggestion cards where you can.';
  var SUMMARY_PROMPT = 'Summarize my latest report in a few lines: what matters most.';
  var SCANNED_KEY = 'ot.aiscan';   // kinds that have had their first-connect scan
  var SIGNIN_POLL_MS = 4000, SIGNIN_POLL_FOR_MS = 5 * 60 * 1000;
  function scannedKinds() { try { return JSON.parse(storeGet(SCANNED_KEY) || '[]') || []; } catch (e) { return []; } }
  function markScanned(kind) { var l = scannedKinds(); if (l.indexOf(kind) < 0) storeSet(SCANNED_KEY, JSON.stringify(l.concat([kind]))); }
  /* ---------- M4: plans, approval, receipts (contracts/m4-actions.md) ---------- */
  // Card options that start a Defender operation, offered only when action.catalog enables them.
  var DEFENDER_OPTIONS = { 'Update definitions': 'defender.update_signatures', 'Quick scan': 'defender.quick_scan' };
  var FAIL_WORD = { failed: 'Not changed', indeterminate: 'Unclear', rolled_back_failed: 'Unclear' };
  var REQUESTER = { claude_code: 'Claude Code', codex: 'Codex', in_app: 'Assistant' };
  var PROGRESS = { 'defender.quick_scan': 'Scanning · quick', 'defender.full_scan': 'Scanning · full', 'defender.update_signatures': 'Updating definitions' };
  // The preview block from a plan: before → after rows, context, trade-offs, Undo line, Needs admin.
  function planPreview(plan) {
    var d = (plan.context || []).map(function (c) { return [c[0], c[1]]; });
    if ((plan.tradeoffs || []).length) d.push(['Trade-off', plan.tradeoffs.join(' · ')]);
    if (plan.privilege === 'admin') d.push(['Admin', 'Needs admin']);
    d.push(['Undo', plan.undo && plan.undo.supported ? plan.undo.conditions || 'Yes' : 'No']);
    return { rows: plan.rows || [], details: d };
  }
  function opPhase(op) {
    if (!op) return null;
    if (op.state === 'verified') return 'verified';
    if (FAIL_WORD[op.state]) return 'failed';
    return 'running';
  }
  /* ---------- One-click connect (contracts/onboarding.md §2–3) ---------- */
  var CLIENT_OF_TILE = { 'claude-code': 'claude_code', codex: 'codex' };
  // Not installed: the official app download (Claude's app includes Claude Code), or the official PowerShell installer.
  var CLIENT_PAGE = { claude_code: 'https://claude.com/download', codex: 'https://chatgpt.com/codex' };
  var INSTALL_CMD = { claude_code: 'irm https://claude.ai/install.ps1 | iex', codex: 'irm https://chatgpt.com/codex/install.ps1 | iex' };
  var INSTALL_POLL_MS = 4000, INSTALL_POLL_FOR_MS = 5 * 60 * 1000;
  // shell.requestApiKey errors (m3 §9 codes, or the shell's own): a short word for the flash.
  var KEY_FAIL = { auth: 'Key not accepted', quota: 'No credit left', network: 'Offline', offline: 'Offline', backend_unavailable: 'Offline',
    unavailable: 'Busy · try again', not_eligible: 'Not available', storage: 'Key not saved', busy: 'Already open' };
  // What a CLI's sign-in pays with (shell.clientStatus auth).
  var BILLING = { claude_code: { subscription: 'Uses your Claude plan', api_key: 'Billed to your Anthropic API key' },
    codex: { subscription: 'Uses your ChatGPT plan', api_key: 'Billed to your OpenAI API key' } };
  // A CLI that is there but cannot run (shell.clientStatus blocked + reason).
  // blockedReason: policy | access_denied | node_missing | unsupported_path | launch_failed.
  var BLOCKED = {
    policy: { word: 'Blocked on this PC', note: 'A rule on this PC stops it. Ask whoever manages this PC.' },
    access_denied: { word: 'Blocked on this PC', note: 'Windows won’t let the app run it. Ask whoever manages this PC.' },
    node_missing: { word: 'Needs a reinstall', note: 'This copy needs Node.js. The installer below doesn’t.', reinstall: true },
    unsupported_path: { word: 'Needs a reinstall', note: 'This copy is in a folder the app can’t use. Install it again below.', reinstall: true },
    launch_failed: { word: 'Couldn’t start', note: 'It didn’t start. Close other windows using it, then check again.' } };
  // shell.openUrl opens the allowlisted client pages in the system browser.
  var DESKTOP_OPEN_URL = true;
  // card.reply is {question, text, evidenceIds} (followup.schema.json); older fixture state saved a bare string.
  function replyText(r) { return r == null ? null : typeof r === 'string' ? r : typeof r.text === 'string' ? r.text : null; }
  var CONTINUE_PROMPT = 'Use optimization-tool to review my latest report and explain what matters most.';
  function isApiKind(k) { return k === 'anthropic_api' || k === 'openai_api' || k === 'chatgpt_plan'; }
  function isMcpKind(k) { return k === 'claude_code_mcp' || k === 'codex_mcp'; }
  function kindForProfile(pr) {
    if (!pr) return null;
    if (pr.value === 'api') return /openai/i.test(pr.label || '') ? 'openai_api' : 'anthropic_api';
    if (pr.value === 'chatgpt') return 'chatgpt_plan';
    return NATIVE_KIND[pr.value] || null;
  }
  function kib(b) { return b < 1024 ? b + ' B' : (b / 1024).toFixed(1) + ' KiB'; }
  // A tool call in plain words: "Measured CPU · 15 s", "Read findings".
  function toolWords(t) {
    var a = t.arguments || {}, done = t.status !== 'running';
    if (t.tool === 'get_findings') return done ? 'Read findings' : 'Reading findings';
    if (t.tool === 'request_action') return done ? 'Requested a change' : 'Requesting a change';
    if (t.tool === 'list_capabilities') return done ? 'Listed checks' : 'Listing checks';
    if (t.tool === 'get_operation') return 'Checked progress';
    if (t.tool === 'get_cards') return done ? 'Read suggestions' : 'Reading suggestions';
    if (t.tool === 'get_pc_specs') return done ? 'Read PC specs' : 'Reading PC specs';
    if (t.tool === 'get_report_history') return done ? 'Read past reports' : 'Reading past reports';
    if (t.tool === 'note_card') return done ? 'Noted a suggestion' : 'Noting a suggestion';
    if (t.tool === 'add_card') return done ? 'Added a suggestion' : 'Adding a suggestion';
    if (t.tool === 'run_diagnostic') {
      var w = DIAG_WORDS[a.diagnosticId] || ['Checking ' + a.diagnosticId, 'Checked ' + a.diagnosticId];
      return w[done ? 1 : 0] + (a.diagnosticId === 'process.sample' ? ' · ' + (a.window || 15) + ' s' : '');
    }
    return t.tool || 'Check';
  }
  // Assistant text is plain text; its [ev-…] markers become citation chips.
  function stripCites(text) { return String(text || '').replace(/\s*\[ev-[0-9a-z-]+\]/gi, ''); }
  function storeGet(k) { try { return window.localStorage.getItem(k); } catch (e) { return null; } }
  function storeSet(k, v) { try { if (v == null) window.localStorage.removeItem(k); else window.localStorage.setItem(k, v); } catch (e) { /* per-viewer convenience only */ } }

  function uuid() {
    if (window.crypto && crypto.randomUUID) return crypto.randomUUID();
    var b = new Uint8Array(16); crypto.getRandomValues(b); b[6] = (b[6] & 15) | 64; b[8] = (b[8] & 63) | 128;
    var x = Array.prototype.map.call(b, function (v) { return ('0' + v.toString(16)).slice(-2); }).join('');
    return x.slice(0, 8) + '-' + x.slice(8, 12) + '-' + x.slice(12, 16) + '-' + x.slice(16, 20) + '-' + x.slice(20);
  }
  function delay(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }
  function pad(n) { return (n < 10 ? '0' : '') + n; }
  function hhmm(iso) { var d = new Date(iso); return isNaN(d) ? '' : pad(d.getHours()) + ':' + pad(d.getMinutes()); }
  function intervalLabel(i) { var m = /^P(\d+)D$/.exec(i || ''); return m ? 'Last ' + m[1] + ' days' : i; }
  function observedLabel(ev) { return ev.interval ? intervalLabel(ev.interval) : hhmm(ev.observedAt); }
  var STATUS_LABEL = { complete: 'complete', complete_with_gaps: 'complete with gaps', partial: 'partial' };
  function whenDay(iso) {
    var d = new Date(iso), now = new Date();
    return (d.toDateString() === now.toDateString() ? 'Today' : d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })) + ' ' + hhmm(iso);
  }
  function whenLabel(r) {
    if (!r) return '';
    var d = new Date(r.generatedAt), now = new Date();
    var day = d.toDateString() === now.toDateString() ? 'Today' : d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
    var c = r.baseReportId && r.comparison;
    return day + ' ' + hhmm(r.generatedAt) + ' · ' + (STATUS_LABEL[r.status] || r.status) +
      (c ? ' · +' + (c['new'] || []).length + ' new · ' + (c.resolved || []).length + ' fixed' : '');
  }
  // Short citation labels (e1, e2 … in finding order; gap-1 …) stand in for the long stable IDs on screen.
  function citeLabels(r) {
    var labels = {}, n = 0;
    if (!r) return labels;
    r.findings.forEach(function (f) { f.evidenceIds.forEach(function (id) { if (!labels[id]) labels[id] = 'e' + (++n); }); });
    r.evidence.forEach(function (e) { if (!labels[e.id]) labels[e.id] = 'e' + (++n); });
    r.gaps.forEach(function (g, i) { labels[g.id] = 'gap-' + (i + 1); });
    return labels;
  }
  // Findings first, then the category's informational evidence (`info`), then gaps. Evidence marked
  // sensitivity "local" (like all evidence here) shows its Display text only, never its raw value.
  function reportRows(r, labels) {
    if (!r) return [];
    var ev = {}, used = {}, fresh = (r.baseReportId && r.comparison && r.comparison['new']) || [];
    r.evidence.forEach(function (e) { ev[e.id] = e; });
    var rows = [];
    r.findings.forEach(function (f) {
      var e = ev[f.evidenceIds[0]]; if (!e || used[e.id]) return; used[e.id] = true;
      f.evidenceIds.forEach(function (id) { used[id] = true; });
      rows.push({ id: e.id, label: labels[e.id], cat: f.category, title: f.title, value: e.display, src: e.source, time: observedLabel(e),
        isNew: fresh.indexOf(f.fingerprint) >= 0 });
    });
    r.evidence.forEach(function (e) {
      if (used[e.id]) return;
      rows.push({ id: e.id, label: labels[e.id], cat: e.category, title: e.display, value: null, src: e.source, time: observedLabel(e), info: true });
    });
    r.gaps.forEach(function (g) {
      rows.push({ id: g.id, label: labels[g.id], cat: g.category, title: g.title, value: g.display, src: g.source || g.collectorId, time: hhmm(r.generatedAt), gap: true });
    });
    return rows;
  }
  function splitList(list, options, customKey, profile) {
    var std = list.filter(function (x) { return options.indexOf(x) >= 0; }), custom = list.filter(function (x) { return options.indexOf(x) < 0; });
    var o = {}; o[customKey] = custom.join('\n').slice(0, 200); o.list = std; return o;
  }
  function profileList(std, custom) { return (std || []).concat(custom ? custom.split('\n').filter(Boolean) : []); }
  function emptyRows() { return SPEC_ROWS.map(function (n) { return { name: n, found: [] }; }); }

  function AppFlow(p) {
    var backend = p.backend, isFixture = backend.kind === 'fixture';
    var reduce = typeof window !== 'undefined' && window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    var FADE = reduce ? 0 : 650;
    var st = React.useState({ id: 'boot', leaving: false }), stage = st[0], setStage = st[1];
    var ob = React.useState(null), onb = ob[0], setOnbState = ob[1];
    var u = React.useState([]), usage = u[0], setUsage = u[1];
    var g = React.useState([]), goals = g[0], setGoals = g[1];
    var w = React.useState({ speed: 1, words: [] }), wave = w[0], setWave = w[1];
    var iv = React.useState({ current: -1, rows: emptyRows() }), inv = iv[0], setInv = iv[1];
    var sr = React.useState([]), starters = sr[0], setStarters = sr[1];
    var sc = React.useState([]), scanCats = sc[0], setScanCats = sc[1];
    var sd = React.useState(0), scanDone = sd[0], setScanDone = sd[1];
    var rp = React.useState(null), report = rp[0], setReport = rp[1];
    var fq = React.useState(null), fu = fq[0], setFuState = fq[1];
    var cs = React.useState({}), cardState = cs[0], setCardState = cs[1];   // card id → { mode } (UI only)
    var lv = React.useState(null), cardLeaving = lv[0], setCardLeaving = lv[1];   // id of the card sinking
    var at = React.useState({ cid: null, n: 0 }), askToken = at[0], setAskToken = at[1];
    var fc = React.useState({ cid: null, main: false, why: {}, ask: {} }), focus = fc[0], setFocus = fc[1];   // which card is open full width (main: clicked)
    var mn = React.useState(null), menu = mn[0], setMenu = mn[1];               // the toolbar's open list: suggest | history
    var po = React.useState({}), panelOpen = po[0], setPanelOpen = po[1];     // answer id → panel shown (true) or closed (false)
    var dl = React.useState(loadDeleted), deleted = dl[0], setDeleted = dl[1]; // History rows deleted (answerKey → true)
    var deletedRef = React.useRef(deleted); deletedRef.current = deleted;
    function isDeleted(a) { return !!deletedRef.current[answerKey(a)]; }
    var lt = React.useState(null), liveTurn = lt[0], setLiveTurn = lt[1];      // agent.turn started, not yet finished/failed
    var wb = React.useState(null), welcome = wb[0], setWelcome = wb[1];            // { when, pending } on reopen
    var rs = React.useState(null), sheet = rs[0], setSheet = rs[1];
    var sm = React.useState(null), summary = sm[0], setSummary = sm[1];     // null | connect | preview | loading | lines[]
    var tt = React.useState(null), toast = tt[0], setToast = tt[1];
    var dr = React.useState(false), draft = dr[0], setDraft = dr[1];
    var ai = React.useState({ value: 'local', label: 'Local only' }), conn = ai[0], setConn = ai[1];
    var rt = React.useState(null), returnTo = rt[0], setReturnTo = rt[1];
    var bs = React.useState(backend.state()), bstate = bs[0], setBstate = bs[1];
    var re = React.useState(null), restore = re[0], setRestore = re[1];       // draft put back in the main box
    var ar = React.useState(null), askRestore = ar[0], setAskRestore = ar[1]; // draft put back in the card box
    var fl = React.useState(false), failed = fl[0], setFailed = fl[1];
    var scu = React.useState(null), scanCurrent = scu[0], setScanCurrent = scu[1];   // Scan.progress.current (sampler)
    var fx = React.useState(null), fixed = fx[0], setFixed = fx[1];                   // resolved findings vs the base report
    var dt = React.useState({}), details = dt[0], setDetails = dt[1];                 // finding id → finding.get result
    var pv = React.useState({}), providers = pv[0], setProviders = pv[1];             // kind → provider.list entry
    var cn = React.useState(null), connect = cn[0], setConnect = cn[1];               // connection in progress on the chooser
    var pg = React.useState(null), pairing = pg[0], setPairing = pg[1];               // pending agent.pairing
    var gs = React.useState(null), grant = gs[0], setGrant = gs[1];                   // grant sheet: { kind, pairingId?, pending? }
    var tk = React.useState(null), task = tk[0], setTask = tk[1];
    var acs = React.useState([]), activity = acs[0], setActivity = acs[1];            // agent.tool events (optimizer calls only)
    var ans = React.useState([]), answers = ans[0], setAnswers = ans[1];              // { id, q, cardId, turnId, text, citations, followUps, busy }
    var answersRef = React.useRef(answers); answersRef.current = answers;
    var sa = React.useState(false), showActivity = sa[0], setShowActivity = sa[1];
    var ah = React.useState(null), actHit = ah[0], setActHit = ah[1];
    var pk = React.useState(null), peek = pk[0], setPeek = pk[1];                      // the evidence a citation chip opened
    var tev = React.useRef({});                                                         // this task's own evidence, by ID
    var cdo = React.useState(false), detailOpen = cdo[0], setDetailOpen = cdo[1];   // the pill's connection detail
    // Escape closes the evidence card, then the pill's detail (an outside click already does), then the Activity panel.
    React.useEffect(function () {
      if (!detailOpen && !showActivity && !peek) return;
      function onKey(e) { if (e.key !== 'Escape') return; if (peek) setPeek(null); else if (detailOpen) setDetailOpen(false); else setShowActivity(false); }
      window.addEventListener('keydown', onKey);
      return function () { window.removeEventListener('keydown', onKey); };
    }, [detailOpen, showActivity, peek]);
    var adg = React.useState(null), availDiag = adg[0], setAvailDiag = adg[1];     // provider.list diagnostics (ids) this build has
    var pb = React.useState(false), pairBusy = pb[0], setPairBusy = pb[1];          // a pairing decision is on its way
    var bi = React.useState({}), busyItems = bi[0], setBusyItems = bi[1];           // review-list card id → restoring
    var sk = React.useState(null), skipping = sk[0], setSkipping = sk[1];           // collector being skipped
    var pairedIds = React.useRef({});                                               // kind → approved pairingId
    var undoing = React.useRef({});                                                 // operationId → undo requested
    var stepSaving = React.useRef(false);                                           // a setup step is being saved
    var sc2 = React.useState(false), showCmd = sc2[0], setShowCmd = sc2[1];
    var wl = React.useState(false), waitLong = wl[0], setWaitLong = wl[1];          // 60 s in waiting: show the check help
    var regs = React.useRef({});                                                    // kind → registration command

    var timers = React.useRef([]);
    var onbRef = React.useRef(null), fuRef = React.useRef(null), stageRef = React.useRef(stage);
    var scanRef = React.useRef(null);          // { scanId, stream, cursor, handled }
    var invRef = React.useRef(null);           // { inventoryId, stream, finished }
    var buffer = React.useRef([]);             // events that arrived before their job's ID was known
    var fuChain = React.useRef(Promise.resolve()), busy = React.useRef(false);
    var cardFinding = React.useRef({});        // card id → finding id, for restoring a disposition from the review list
    var taskRef = React.useRef(null);          // { taskId, stream }
    var taskLabels = React.useRef({});         // task evidence id → short label (m1, m2 …)
    var pendingSummary = React.useRef(false);
    var cls2 = React.useState({}), clients = cls2[0], setClients = cls2[1];      // tile → shell.clientStatus result (+ busy / failed)
    function putClient(tile, patch) { setClients(function (m) { var n = Object.assign({}, m); n[tile] = Object.assign({}, m[tile] || {}, patch); return n; }); }
    var ct = React.useState({}), catalog = ct[0], setCatalog = ct[1];             // actionId → catalog entry
    var pls = React.useState({}), plans = pls[0], setPlans = pls[1];            // planId → { plan, op, progress, cardId, kind }
    function putPlan(planId, patch) {
      setPlans(function (m) { var n = Object.assign({}, m); n[planId] = Object.assign({}, m[planId] || {}, patch); return n; });
    }
    var startedAt = React.useRef({});          // invocation id → when it started (event time, else first seen)
    var wk = React.useState(0), workedAt = wk[0], setWorkedAt = wk[1];
    function workPulse() { setWorkedAt(Date.now()); }
    React.useEffect(function () {
      // "Working" lingers ~3 s after the last tool or turn event, then the pill settles.
      if (!workedAt) return;
      var t = setTimeout(function () { setWorkedAt(0); }, 3000);
      return function () { clearTimeout(t); };
    }, [workedAt]);
    stageRef.current = stage;
    function later(fn, ms) { var t = setTimeout(fn, ms); timers.current.push(t); return t; }
    React.useEffect(function () { return function () { timers.current.forEach(clearTimeout); }; }, []);

    function setOnb(o) { onbRef.current = o; setOnbState(o); return o; }
    function setFu(f) {
      fuRef.current = f; setFuState(f);
      if (f && f.card) cardFinding.current[f.card.id] = f.card.findingId;
      if (f) (f.decisions || []).forEach(function (d) { if (d.findingId) cardFinding.current[d.cardId] = d.findingId; });
      return f;
    }
    function flash(msg) { setToast(msg); later(function () { setToast(null); }, 2000); }

    // Everything on screen sinks into the sea first; the next step rises once it has gone.
    function go(next) {
      setStage(function (s) { return { id: s.id, leaving: true, next: next }; });
      later(function () { if (next === 'login') setSheet(null); setStage({ id: next, leaving: false }); }, FADE);
    }

    /* ---------- onboarding ---------- */
    function adoptProfile(o) {
      var pr = o.profile;
      setUsage(profileList(pr.uses, pr.customUse));
      setGoals(profileList(pr.goals, pr.customGoal));
      if (pr.provider) setConn({ value: pr.provider.value, label: pr.provider.label, kind: kindForProfile(pr.provider) });
    }
    function mergeProvider(e) { if (e && e.kind) setProviders(function (m) { var n = Object.assign({}, m); n[e.kind] = e; return n; }); return e; }
    function loadProviders() {
      return backend.call('provider.list').then(function (r) {
        var m = {}; (r.providers || []).forEach(function (e) { m[e.kind] = e; }); setProviders(m);
        if (Array.isArray(r.diagnostics)) setAvailDiag(r.diagnostics.map(function (d) { return typeof d === 'string' ? d : d && d.id; }).filter(Boolean));
        // A pairing still waiting (after a reload or a reconnect): the entry carries its ID, so the card can come back.
        Object.keys(m).forEach(function (k) {
          var e = m[k]; if (!isMcpKind(k) || !e.pairingId) return;
          if (e.status === 'pending' && e.code) setPairing(function (cur) { return cur && cur.pairingId === e.pairingId ? cur : { pairingId: e.pairingId, client: CLIENT_OF_KIND[k], code: e.code, status: 'pending' }; });
          if (e.status === 'paired' || e.status === 'active' || e.status === 'client_closed') pairedIds.current[k] = e.pairingId;
        });
        return m;
      }, function () { return {}; });
    }
    function pairingFor(kind) { return pairedIds.current[kind] || (providers[kind] && providers[kind].pairingId) || null; }
    // agent.get's history holds the questions (agent.question, m5 §9), the assistant's answers (agent.message) and the
    // turns (a started turn with nothing after it is still running). afterSeq: only turns newer than this are added.
    // Sequence-aware: a snapshot older than the live events already applied (its last seq below the one seen) only
    // adds what ended; it never brings back a run or replaces the live state. Returns whether the snapshot was stale.
    function syncAnswers(t, afterSeq) {
      var hist = t.activity || [], cur = taskRef.current && taskRef.current.taskId === t.taskId ? taskRef.current : null;
      var last = hist.length ? hist[hist.length - 1].seq : null;
      var stale = !!(cur && cur.seq != null && (last == null || last < cur.seq));
      setAnswers(function (list) {
        return reconcileAnswers(list, hist, { taskId: t.taskId, reportId: t.scope && t.scope.reportId, afterSeq: afterSeq, stale: stale, strip: stripCites, busyWord: toolWords, drop: isDeleted });
      });
      if (!stale) {
        var run = t.status === 'active' ? runningTurn(hist) : null;
        setLiveTurn(run);
        if (run) workPulse();
      }
      if (cur && last != null) cur.seq = Math.max(cur.seq || 0, last);
      return stale;
    }
    // agent.get returns the task's event history ({seq, type, at, payload}); Activity wants one row per tool call,
    // in first-seen order with its latest state, exactly as live agent.tool events build it.
    function activityFromHistory(list) {
      var rows = [], at = {};
      (list || []).forEach(function (e) {
        var pl = e && e.type ? (e.type === 'agent.tool' ? Object.assign({ at: e.at }, e.payload) : null) : e;
        if (!pl || !pl.invocationId) return;
        if (at[pl.invocationId] == null) { at[pl.invocationId] = rows.length; rows.push(pl); } else rows[at[pl.invocationId]] = pl;
      });
      return rows;
    }
    function attachTask(t, activityList) {
      var prev = taskRef.current;
      taskRef.current = { taskId: t.taskId, stream: 'task:' + t.taskId, seq: prev && prev.taskId === t.taskId ? prev.seq : null, reportId: t.scope && t.scope.reportId };
      storeSet('ot.task', t.taskId); rememberTask(t.taskId);
      setTask(t); setActivity(activityFromHistory(activityList));
      // Another task's run is not this one's: its unanswered questions go (they would hold the next ask).
      if (!prev || prev.taskId !== t.taskId) setLiveTurn(null);
      // After a reload the answers come back from the task's history, a run still going included (Stop and all).
      syncAnswers(Object.assign({}, t, { activity: activityList || [] }), null);
      drain();
    }
    function saveOnb(patch, retried) {
      var o = onbRef.current;
      if (patch.step && STEP_ORDER.indexOf(patch.step) <= STEP_ORDER.indexOf(o.step)) { patch = Object.assign({}, patch); delete patch.step; }
      return backend.call('onboarding.save', { expectedRevision: o.revision, patch: patch }).then(setOnb, function (err) {
        // Someone else saved first: adopt their revision and apply this edit once more.
        if (err.code === -32002 && err.data && err.data.current && !retried) { setOnb(err.data.current); return saveOnb(patch, true); }
        flash('Not saved'); throw err;
      });
    }
    function loadStarters() {
      var o = onbRef.current;
      return backend.call('starter.list', { profileRevision: o ? o.revision : 0 }).then(function (r) { setStarters(r.starters || []); }, function () { setStarters([]); });
    }
    function loadReport(reportId) {
      return Promise.all([backend.call('report.get', { reportId: reportId }), backend.call('followup.get', { reportId: reportId })]).then(function (res) {
        var r = res[0];
        setReport(r); setFu(res[1]); setDetails({}); setFixed(null);
        // A rescan's comparison: list what was fixed since the base report.
        if (r.baseReportId && r.comparison && (r.comparison.resolved || []).length) {
          backend.call('report.compare', { reportId: r.reportId, baseReportId: r.baseReportId }).then(function (c) { setFixed(c.resolved || []); }, function () {});
        }
        return r;
      });
    }

    function boot() {
      setFailed(false);
      loadProviders();
      // Claude Code / Codex are usable only once the shell has checked them (installed, signed in): check at start.
      ['claude-code', 'codex'].forEach(function (t) { checkClient(t); });
      // Receipts survive a reload: re-read recent operations and their plans.
      backend.call('operation.list', { limit: 50 }).then(function (r) {
        (r.operations || []).forEach(function (op) {
          backend.call('action.get', { planId: op.planId }).then(function (plan) { putPlan(op.planId, { plan: plan, op: op, kind: plan.undoOf ? 'undo-done' : 'card' }); }, function () {});
        });
      }, function () {});
      backend.call('action.catalog').then(function (r) { var m = {}; (r.actions || []).forEach(function (a) { m[a.actionId] = a; }); setCatalog(m); }, function () {});
      var lastTask = storeGet('ot.task');
      // The last task this window used; without one (another window, cleared storage), the newest active task (agent.active).
      var reattach = function (t) { if (!taskRef.current || taskRef.current.taskId === t.taskId) attachTask(t, t.activity || []); };
      var newest = function () {
        return backend.call('agent.active').then(function (r) {
          var t = r && r.tasks && r.tasks[0];
          if (t) return backend.call('agent.get', { taskId: t.taskId }).then(reattach);
        }).catch(function () {});
      };
      var current = lastTask ? backend.call('agent.get', { taskId: lastTask }).then(reattach, function () { storeSet('ot.task', null); return newest(); }) : newest();
      // History: the answers of this window's earlier tasks come back too (only what ended; they never run here).
      Promise.resolve(current).catch(function () {}).then(function () {
        recentTasks().forEach(function (tid) {
          if (taskRef.current && taskRef.current.taskId === tid) return;
          backend.call('agent.get', { taskId: tid }).then(function (t) {
            if (taskRef.current && taskRef.current.taskId === t.taskId) return;
            setAnswers(function (list) {
              return reconcileAnswers(list, t.activity || [], { taskId: t.taskId, reportId: t.scope && t.scope.reportId, stale: true, others: 'keep', strip: stripCites, drop: isDeleted });
            });
          }, function () { storeSet(TASKS_KEY, JSON.stringify(recentTasks().filter(function (x) { return x !== tid; }))); });
        });
      });
      backend.call('onboarding.get').then(function (o) {
        // Past this point the backend answered: a later failure (one report call, a bug) lands on a screen with a short
        // word instead of "Backend unavailable".
        return Promise.resolve().then(function () { return resumeFrom(o); }).catch(function (err) {
          if (typeof console !== 'undefined') console.error('boot', err);
          if (stageRef.current.id === 'boot') {
            var done = o && o.step === 'complete';
            if (done) loadStarters();
            setStage({ id: done ? 'main' : (o && STEP_STAGE[o.step]) || 'login', leaving: false });
          }
          flash('Not loaded');
        });
      }, function (err) { if (typeof console !== 'undefined') console.error('boot', err); setFailed(true); });
    }
    function resumeFrom(o) {
      {
        setOnb(o); adoptProfile(o);
        if (o.step !== 'complete') {
          if (o.step === 'specs' && o.resume && o.resume.inventoryId) return resumeInventory(o.resume.inventoryId);
          setStage({ id: STEP_STAGE[o.step] || 'login', leaving: false }); return;
        }
        var r = o.resume || {};
        // Resume: never replay onboarding. A running scan reattaches; a report opens with its saved review state.
        var scanP = r.scanId ? backend.call('scan.get', { scanId: r.scanId }).catch(function () { return null; }) : Promise.resolve(null);
        return scanP.then(function (s) {
          if (s && (s.status === 'running' || s.status === 'queued')) { attachScan(s); setWave({ speed: 2.3, words: s.waveWords || [] }); setStage({ id: 'scanning', leaving: false }); return; }
          var rid = (s && s.reportId) || r.reportId;
          // Welcome back: the composer with the latest report at a glance; Continue opens it where it was left.
          if (rid) return Promise.all([loadReport(rid), loadStarters(), backend.call('report.list', { limit: 1 }).catch(function () { return null; })]).then(function (res) {
            var latest = res[2] && res[2].reports && res[2].reports[0];
            setWelcome({ generatedAt: (latest && latest.generatedAt) || res[0].generatedAt, pending: fuRef.current ? fuRef.current.pendingCount : 0 });
            setStage({ id: 'main', leaving: false });
          });
          return loadStarters().then(function () { setStage({ id: 'main', leaving: false }); });
        });
      }
    }
    function resumeInventory(id) {
      return backend.call('inventory.get', { inventoryId: id }).then(function (i) {
        if (i.status !== 'running') { setStage({ id: 'specs', leaving: false }); return; }
        invRef.current = { inventoryId: id, stream: 'inventory:' + id, finished: false };
        var rows = emptyRows(), cur = 0;
        i.facts.forEach(function (f) { cur = Math.max(cur, placeFact(rows, f)); });
        setInv({ current: cur, rows: rows }); setWave({ speed: 2.2, words: i.facts.map(function (f) { return f.label; }).slice(-10) });
        setStage({ id: 'specs', leaving: false }); drain();
      }, function () { setStage({ id: 'specs', leaving: false }); });
    }

    /* ---------- events ---------- */
    function placeFact(rows, f) {
      var name = f.group || KIND_ROW[f.kind] || f.kind, idx = -1;
      for (var i = 0; i < rows.length; i++) if (rows[i].name === name) idx = i;
      if (idx < 0) { rows.push({ name: name, found: [] }); idx = rows.length - 1; }
      rows[idx].found = rows[idx].found.concat([f.label]);
      return idx;
    }
    function onPairing(pl) {
      var kind = CLIENT_KIND[pl.client];
      if (pl.status === 'pending') {
        setPairing(pl);
        setConnect(function (c) { return c && (!kind || c.kind === kind) ? Object.assign({}, c, { phase: 'pairing', code: pl.code, pairingId: pl.pairingId }) : c; });
        return;
      }
      if (kind && pl.status === 'approved') pairedIds.current[kind] = pl.pairingId;
      else if (kind && pairedIds.current[kind] === pl.pairingId) delete pairedIds.current[kind];
      setPairing(function (cur) { return cur && cur.pairingId === pl.pairingId ? null : cur; });
      // Decided: an approved pairing leaves the chooser's connect step (the grant sheet follows); any other outcome waits again.
      setConnect(function (c) {
        if (!c || c.pairingId !== pl.pairingId) return c;
        return pl.status === 'approved' ? null : Object.assign({}, c, { phase: 'register', code: null, pairingId: null });
      });
      loadProviders();
    }
    // An answer belongs to the task it was asked on: events of a task only ever touch that task's entries.
    function answerIndex(list, turnId, taskId) {
      for (var i = list.length - 1; i >= 0; i--) if (list[i].taskId === taskId && turnId && list[i].turnId === turnId) return i;
      for (var j = list.length - 1; j >= 0; j--) if (list[j].taskId === taskId && !list[j].turnId && list[j].text == null && !list[j].failedTurn) return j;
      return -1;
    }
    function dropAnswer(turnId, taskId) {
      setAnswers(function (list) { var i = answerIndex(list, turnId, taskId); return i < 0 ? list : list.slice(0, i).concat(list.slice(i + 1)); });
    }
    function patchAnswer(turnId, patch, taskId) {
      setAnswers(function (list) {
        var i = answerIndex(list, turnId, taskId); if (i < 0) return list;
        var n = list.slice(); n[i] = Object.assign({}, n[i], { turnId: turnId }, patch); return n;
      });
    }
    function isCurrentTask(taskId) { return !!(taskRef.current && taskRef.current.taskId === taskId); }
    function onTaskEvent(ev) {
      var pl = ev.payload || {}, tid = ev.stream.slice(5);
      if (taskRef.current && ev.seq != null) taskRef.current.seq = Math.max(taskRef.current.seq || 0, ev.seq);
      var patch = function (turnId, p) { patchAnswer(turnId, p, tid); };
      // A turn started before a reload or a reconnect, or from another window, has no entry yet: it gets one.
      var ensureAnswer = function (turnId, seq, fields) {
        setAnswers(function (list) {
          if (answerIndex(list, turnId, tid) >= 0) return list;
          var rid = taskRef.current && taskRef.current.taskId === tid ? taskRef.current.reportId || null : null;
          return list.concat([Object.assign({ id: 'h-' + (turnId || seq), q: null, cardId: null, taskId: tid, turnId: turnId, reportId: rid, at: ev.at || new Date().toISOString() }, fields)]).slice(-KEEP_ANSWERS);
        });
      };
      if (ev.type === 'agent.question') {
        // m5 §9: an ask from here already has its entry (it takes the turn); one from elsewhere gets its panel.
        ensureAnswer(pl.turnId, ev.seq, { q: pl.text || null, cardId: pl.cardId || null, text: null, busy: 'Thinking…', failedTurn: false });
        setAnswers(function (list) {
          var i = answerIndex(list, pl.turnId, tid); if (i < 0) return list;
          var n = list.slice(); n[i] = Object.assign({}, n[i], { turnId: pl.turnId, q: n[i].q || pl.text || null }); return n;
        });
      }
      else if (ev.type === 'agent.task') {
        setTask(function (t) { return t && t.taskId === tid ? Object.assign({}, t, { status: pl.status, used: pl.used || t.used }) : t; });
        if (pl.status && pl.status !== 'active') setLiveTurn(null);
      }
      else if (ev.type === 'agent.tool') {
        if (!startedAt.current[pl.invocationId]) startedAt.current[pl.invocationId] = Date.parse(pl.at || '') || Date.now();
        workPulse();
        setActivity(function (list) {
          var n = list.filter(function (a) { return a.invocationId !== pl.invocationId; });
          return n.length === list.length ? list.concat([pl]) : list.map(function (a) { return a.invocationId === pl.invocationId ? pl : a; });
        });
        if (pl.turnId && pl.status === 'running') patch(pl.turnId, { busy: toolWords(pl) });
        // m5 §5: a note or a new card changes the deck the user sees.
        if (pl.status === 'complete' && (pl.tool === 'note_card' || pl.tool === 'add_card')) refreshDeck();
      } else if (ev.type === 'agent.message') {
        // A turn started before a reload or a reconnect has no entry yet: it gets one (the answer lands somewhere).
        var mtext = { text: stripCites(pl.text), citations: pl.citations || [], followUps: pl.followUps || [], busy: null, failedTurn: false };
        ensureAnswer(pl.turnId || null, ev.seq, mtext);
        patch(pl.turnId, mtext);
        setLiveTurn(function (cur) { return cur && (!pl.turnId || cur === pl.turnId || cur === 'turn') ? null : cur; });
        // m5 §7: the answer to a box question becomes the main panel; Activity steps aside.
        if (!(pl.turnId && answersRef.current.some(function (a) { return a.turnId === pl.turnId && a.cardId; }))) { setShowActivity(false); setActHit(null); }
      } else if (ev.type === 'agent.turn') {
        workPulse();
        // One ask spans several provider turns, each `finished` before its tools run: only failed (or the answer) ends it.
        if (pl.status === 'started') setLiveTurn(pl.turnId || 'turn');
        else if (pl.status === 'failed') setLiveTurn(null);
        if (pl.status === 'started') { ensureAnswer(pl.turnId, ev.seq, { text: null, busy: 'Thinking…', failedTurn: false }); patch(pl.turnId, { busy: 'Thinking…' }); }
        // Stopped by the user (agent.cancel): the question simply goes away; any other failure says so.
        else if (pl.status === 'failed' && pl.code === 'cancelled') dropAnswer(pl.turnId, tid);
        else if (pl.status === 'failed') patch(pl.turnId, { failedTurn: true, busy: null });
      }
    }
    function refreshDeck() {
      var f = fuRef.current; if (!f) return;
      var run = function () { return backend.call('followup.get', { reportId: f.reportId }).then(function (cur) { if (fuRef.current && fuRef.current.reportId === cur.reportId) setFu(cur); }, function () {}); };
      fuChain.current = fuChain.current.then(run, run);
    }
    function handleEvent(ev) {
      var s = scanRef.current, i = invRef.current, tr = taskRef.current;
      if (ev.type === 'agent.pairing') { onPairing(ev.payload || {}); return true; }
      if (ev.type === 'action.plan') { onPlanEvent(ev.payload || {}); return true; }
      if (ev.type === 'operation.state') {
        var os = ev.payload || {};
        putPlan(os.planId, { op: Object.assign({}, (plansRef.current[os.planId] || {}).op, { operationId: os.operationId, planId: os.planId, state: os.state, receipt: os.receipt || null, manualUri: os.manualUri || null }),
          progress: os.progress || null });
        if (os.state === 'verified') backend.call('operation.get', { operationId: os.operationId }).then(function (op) { putPlan(os.planId, { op: op }); }, function () {});
        return true;
      }
      if (ev.type === 'provider.status') {
        var ps = ev.payload || {};
        if (ps.kind) setProviders(function (m) {
          var n = Object.assign({}, m), prev = m[ps.kind] || { kind: ps.kind };
          n[ps.kind] = Object.assign({}, prev, { status: ps.status, detail: ps.detail || null, code: ps.code || null, since: ps.since || null });
          if (ps.status !== 'pending' && ps.status !== 'paired') delete n[ps.kind].pairingId;
          return n;
        });
        // A native client that is no longer asking to pair: its pairing card (and code) go away, e.g. once it lapsed.
        // Pending or paired: re-read the entry for its pairingId (the event doesn't carry it).
        if (isMcpKind(ps.kind) && (ps.status === 'pending' || ps.status === 'paired')) loadProviders();
        if (isMcpKind(ps.kind) && ps.status !== 'pending') {
          var gone = CLIENT_OF_KIND[ps.kind];
          setPairing(function (cur) { return cur && cur.client === gone ? null : cur; });
          setConnect(function (c) { return c && c.kind === ps.kind && c.phase === 'pairing' ? Object.assign({}, c, { phase: 'register', code: null, pairingId: null }) : c; });
          if (ps.status !== 'paired' && ps.status !== 'active' && ps.status !== 'client_closed') delete pairedIds.current[ps.kind];
        }
        return true;
      }
      if (tr && ev.stream === tr.stream) { onTaskEvent(ev); return true; }
      if (i && ev.stream === i.stream) {
        if (ev.type === 'inventory.fact') {
          setInv(function (cur) { var rows = cur.rows.map(function (r) { return { name: r.name, found: r.found }; }); var idx = placeFact(rows, ev.payload); return { current: Math.max(cur.current, idx), rows: rows }; });
          setWave(function (wv) { return { speed: 2.2, words: wv.words.concat([ev.payload.label]).slice(-10) }; });
        } else if (ev.type === 'inventory.finished' && !i.finished) {
          i.finished = true;
          setInv(function (cur) { return { current: cur.rows.length, rows: cur.rows }; });
        }
        return true;
      }
      if (s && ev.stream === s.stream) {
        if (s.cursor != null && ev.seq <= s.cursor) return true;   // already in the snapshot
        if (ev.type === 'scan.progress') {
          var pl = ev.payload, n = s.cats || 1;
          var d = pl.total === n ? pl.done : Math.floor(pl.done / (pl.total || 1) * n);
          setScanDone(function (x) { return Math.max(x, d); });
          // v1.1 events carry the running collector in `collector` (`current` stays the v1.0 category label).
          var cur = pl.collector && typeof pl.collector === 'object' ? pl.collector : pl.current && typeof pl.current === 'object' ? pl.current : null;
          setScanCurrent(cur);
        } else if (ev.type === 'scan.finished') finishScan(s, ev.payload);
        return true;
      }
      return false;
    }
    function drain() {
      var keep = [];
      buffer.current.forEach(function (ev) { if (!handleEvent(ev)) keep.push(ev); });
      buffer.current = keep;
    }
    var handleRef = React.useRef(handleEvent); handleRef.current = handleEvent;
    var resyncRef = React.useRef(null);
    React.useEffect(function () {
      var offE = backend.onEvent(function (ev) {
        if (!handleRef.current(ev)) { buffer.current.push(ev); if (buffer.current.length > 200) buffer.current.shift(); }
      });
      var offS = backend.onState(function (state, info) { setBstate(state); if (info && info.resumed && resyncRef.current) resyncRef.current(); });
      boot();
      return function () { offE(); offS(); };
    }, []);

    // After the shell reconnects to the host: re-read snapshots instead of trusting missed events.
    resyncRef.current = function () {
      backend.call('onboarding.get').then(setOnb).catch(function () {});
      var s = scanRef.current;
      if (s && !s.handled) backend.call('scan.get', { scanId: s.scanId }).then(function (snap) {
        if (snap.status === 'running' || snap.status === 'queued') setScanDone(function (x) { return Math.max(x, snap.progress.done); });
        else finishScan(s, { status: snap.status, reportId: snap.reportId });
      }).catch(function () {});
      var i = invRef.current;
      if (i && !i.finished) backend.call('inventory.get', { inventoryId: i.inventoryId }).then(function (snap) {
        var rows = emptyRows(), cur = 0;
        snap.facts.forEach(function (f) { cur = Math.max(cur, placeFact(rows, f)); });
        if (snap.status !== 'running') { i.finished = true; cur = rows.length; }
        setInv({ current: cur, rows: rows });
      }).catch(function () {});
      var f = fuRef.current;
      if (f) backend.call('followup.get', { reportId: f.reportId }).then(setFu).catch(function () {});
      loadProviders();
      // A restarted host forgot which CLI the shell found: check both again, whatever the last answer said.
      ['claude-code', 'codex'].forEach(function (t) { checkClient(t); });
      // A change that was applying: its final state may have been missed.
      Object.keys(plansRef.current).forEach(function (k) {
        var e = plansRef.current[k];
        if (e.op && e.op.operationId && e.op.state !== 'verified' && !FAIL_WORD[e.op.state])
          backend.call('operation.get', { operationId: e.op.operationId }).then(function (op) { putPlan(k, { op: op }); }, function () {});
      });
      var tr = taskRef.current;
      // The task: its status, Activity, and the answers (and whether a run is still going) missed while disconnected.
      if (tr) backend.call('agent.get', { taskId: tr.taskId }).then(function (t) {
        if (!isCurrentTask(t.taskId)) return;
        var seen = tr.seq;
        var rows = activityFromHistory(t.activity);
        if (!syncAnswers(t, seen)) { setTask(t); setActivity(rows); return; }
        // Older than what live events showed: keep the live task and rows; add only rows it hadn't seen.
        setActivity(function (list) {
          var have = {}; list.forEach(function (a) { have[a.invocationId] = true; });
          return list.concat(rows.filter(function (a) { return !have[a.invocationId]; }));
        });
      }).catch(function () {});
    };

    /* ---------- scans ---------- */
    function attachScan(s) {
      scanRef.current = { scanId: s.scanId, stream: (s.eventCursor && s.eventCursor.stream) || 'scan:' + s.scanId, cursor: s.eventCursor ? s.eventCursor.seq : null,
        cats: s.progress.categories.length, handled: false };
      setScanCats(s.progress.categories.map(function (c) { return { id: c.id, label: c.label }; }));
      setScanDone(function (x) { return Math.max(x, s.progress.done); });
      setScanCurrent(s.progress.current && typeof s.progress.current === 'object' ? s.progress.current : null);
      drain();
    }
    function starterHit(text) {
      var norm = function (t) { return t.toLowerCase().replace(/[\s.?!]+$/, '').trim(); };
      return starters.filter(function (s) { return norm(s.text) === norm(text); })[0] || null;
    }
    function routeIntent(text) {
      if (FULL_AUDIT.test(text)) return 'audit.full';
      var hit = starterHit(text);
      return hit ? hit.intent : 'audit.full';
    }
    function runAudit(text, intent, baseReportId) {
      if (busy.current) return; busy.current = true;
      setWelcome(null);
      var from = stageRef.current.id;
      setScanDone(0); setScanCurrent(null);
      setStage({ id: from, leaving: true, next: 'scanning' });
      var params = { idempotencyKey: uuid(), intent: intent, text: text };
      if (baseReportId) params.baseReportId = baseReportId;
      var started = backend.call('scan.start', params);
      Promise.all([started, delay(reduce ? 0 : 450)]).then(function (res) {
        busy.current = false;
        var s = res[0];
        attachScan(s);
        setStage({ id: 'scanning', leaving: false });
        setWave({ speed: 2.3, words: s.waveWords || [] });
        if (s.status !== 'running' && s.status !== 'queued') finishScan(scanRef.current, { status: s.status, reportId: s.reportId });
      }, function (err) {
        busy.current = false;
        setStage({ id: from, leaving: false });
        flash(err && err.code === -32004 ? 'A scan is running' : 'Scan failed');
      });
    }
    // Scan (toolbar): every check, compared with the current report; PC specs are re-read alongside when the user
    // allowed them during setup (the inventory runs on its own stream; a refusal changes nothing).
    // Toolbar labels can hide on narrow windows (icons stay, with the name as tooltip and accessible name).
    function tbLabel(t) { return h('span', { className: 'ot-tb-label' }, t); }
    function fullScan() {
      if (busy.current) return;
      var prof = onbRef.current && onbRef.current.profile;
      if (prof && prof.inventoryConsent === 'granted') backend.call('inventory.start', { idempotencyKey: uuid() }).catch(function () {});
      runAudit('Full scan', 'audit.full', report ? report.reportId : null);
    }
    function skipCollector(collectorId) {
      var s = scanRef.current; if (!s || s.handled || skipping === collectorId) return;
      setSkipping(collectorId);
      backend.call('scan.skipCollector', { scanId: s.scanId, collectorId: collectorId }).then(function (snap) {
        setSkipping(null);
        setScanCurrent(snap.progress && snap.progress.current && snap.progress.current.collectorId !== collectorId ? snap.progress.current : null);
      }, function () { setSkipping(null); flash('Not skipped'); });
    }
    function finishScan(s, payload) {
      if (!s || s.handled) return; s.handled = true;
      setScanCurrent(null);
      var done = function (stageId) {
        setCardState({}); setSummary(null); setSheet(null); closePanels();
        setWave({ speed: 0.7, words: [] });
        setStage({ id: stageId, leaving: false });
      };
      if (!payload.reportId) { loadStarters(); done('main'); if (payload.status === 'failed') flash('Scan failed'); return; }
      loadReport(payload.reportId).then(function () { done('main'); }, function () { loadStarters(); done('main'); });
    }
    function stopScan() {
      var s = scanRef.current; if (!s || s.handled) return;
      s.handled = true; setScanCurrent(null);
      backend.call('scan.cancel', { scanId: s.scanId }).then(function (snap) {
        setWave({ speed: 0.7, words: [] });
        var show = function (stageId) { setCardState({}); setSummary(null); closePanels(); setStage({ id: stageId, leaving: false }); flash('Scan stopped'); };
        if (!snap.reportId) { loadStarters(); show('main'); return; }
        // The partial report keeps what was already checked; with nothing checked yet there is nothing to review.
        loadReport(snap.reportId).then(function () { show('main'); }, function () { loadStarters(); show('main'); });
      }, function () { s.handled = false; flash('Not stopped'); });
    }

    /* ---------- follow-ups ---------- */
    // Follow-up calls run one at a time against the revision the person was looking at. A newer revision
    // (-32002) is adopted as-is; what they typed is put back so they can send it again.
    function respond(params, revision) {
      var f = fuRef.current;
      var run = function () {
        return backend.call('followup.respond', Object.assign({ idempotencyKey: uuid(), reportId: f.reportId, expectedRevision: revision == null ? f.revision : revision }, params))
          .then(function (res) { setFu(res); return { ok: true, fu: res }; }, function (err) {
            if (err && err.code === -32002 && err.data && err.data.current) { setFu(err.data.current); flash('Updated'); return { ok: false, stale: true }; }
            if (err && (err.code === -32013 || err.code === -32001)) {
              return backend.call('followup.get', { reportId: f.reportId }).then(function (cur) { setFu(cur); flash('Updated'); return { ok: false }; }, function () { return { ok: false }; });
            }
            flash('Not saved'); return { ok: false };
          });
      };
      var next = fuChain.current.then(run, run);
      fuChain.current = next;
      return next;
    }
    function setMode(cid, mode, extra) { setCardState(function (s) { var n = Object.assign({}, s); n[cid] = Object.assign({}, s[cid], { mode: mode }, extra); return n; }); }
    function clearMode(cid) { setCardState(function (s) { var n = Object.assign({}, s); delete n[cid]; return n; }); }
    // The card sinks while the backend records the decision; the next one rises after both are done.
    function leave(cid, option) {
      var rev = fuRef.current.revision;
      setCardLeaving(cid); unfocus(cid);
      Promise.all([respond({ scope: 'card', cardId: cid, option: option }, rev), delay(reduce ? 0 : 420)]).then(function (res) {
        if (res[0].ok) clearMode(cid);
        setCardLeaving(null);
      });
    }
    // Ignore with a scope: this finding, snoozed for 30 days, or every finding of its rule. Stored by the backend
    // by fingerprint or rule, so it carries into later scans. A 1.0 backend falls back to the queue's Ignore.
    function ignoreAs(card, choice) {
      var cid = card.id;
      // An AI card has no finding of its own: it is hidden as a card (followup.respond ignore), never by disposition.
      if (card.origin === 'ai' || !card.findingId) { if (choice === 'intended') flash('Not available'); else leave(cid, 'ignore'); return; }
      var params = { idempotencyKey: uuid(), reportId: fuRef.current.reportId, findingId: card.findingId,
        status: choice === 'snooze' ? 'snoozed' : choice === 'intended' ? 'intended' : 'ignored', scope: choice === 'rule' ? 'rule' : 'finding' };
      if (choice === 'snooze') params.expiresAt = new Date(Date.now() + SNOOZE_DAYS * 864e5).toISOString();
      setCardLeaving(cid); unfocus(cid);
      var run = function () {
        return backend.call('finding.setDisposition', params).then(function (res) { setFu(res); return { ok: true }; }, function (err) {
          if (notSupported(err)) return null;
          flash('Not saved'); return { ok: false };
        });
      };
      var call = fuChain.current.then(run, run);
      fuChain.current = call;
      Promise.all([call, delay(reduce ? 0 : 420)]).then(function (res) {
        if (res[0] === null) { setCardLeaving(null); if (choice !== 'intended') leave(cid, 'ignore'); else flash('Not available'); return; }
        if (res[0].ok) clearMode(cid);
        setCardLeaving(null);
      });
    }
    function markBusy(cid, on) { setBusyItems(function (m) { var n = Object.assign({}, m); if (on) n[cid] = true; else delete n[cid]; return n; }); }
    function restoreItem(cid) {
      if (busyItems[cid]) return;
      markBusy(cid, true);
      // Decided in this report only (Later, or an AI card's Hide): followup.respond restore. Otherwise its disposition is cleared.
      var dec = ((fuRef.current && fuRef.current.decisions) || []).filter(function (d) { return d.cardId === cid; })[0];
      var fid = dec ? (dec.status === 'later' ? null : dec.findingId) : cardFinding.current[cid];
      var legacy = function () { return respond({ scope: 'card', cardId: cid, option: 'restore' }).then(function (r) { if (r.ok) clearMode(cid); markBusy(cid, false); }); };
      if (!fid) { legacy(); return; }
      var run = function () {
        return backend.call('finding.clearDisposition', { idempotencyKey: uuid(), reportId: fuRef.current.reportId, findingId: fid })
          .then(function (res) { setFu(res); clearMode(cid); return true; }, function (err) { if (notSupported(err)) return false; flash('Not saved'); return true; });
      };
      var call = fuChain.current.then(run, run);
      fuChain.current = call;
      call.then(function (done) { if (!done) legacy(); else markBusy(cid, false); });
    }
    function openSettings(uri) {
      if (SETTINGS_CATALOG.indexOf(uri) < 0) return;
      // The website cannot open Windows Settings; only the desktop shell can (and checks the catalog again).
      if (isFixture) { flash('Opens Windows Settings'); return; }
      backend.call('shell.openSettings', { uri: uri }).catch(function () { flash('Not available'); });
    }
    function loadDetails(findingId) {
      if (!findingId || details[findingId] || !report) return;
      backend.call('finding.get', { reportId: report.reportId, findingId: findingId }).then(function (d) {
        setDetails(function (m) { var n = Object.assign({}, m); n[findingId] = d; return n; });
      }, function () {});
    }
    // An assistant asked for a change (requestedBy not the user): show it for approval; the agent can never approve.
    function onPlanEvent(pl) {
      var known = plansRef.current[pl.planId];
      if (known) { putPlan(pl.planId, { plan: Object.assign({}, known.plan, { status: pl.status }) }); return; }
      if (pl.requestedBy && pl.requestedBy.kind && pl.requestedBy.kind !== 'user' && pl.status === 'awaiting_user') {
        backend.call('action.get', { planId: pl.planId }).then(function (plan) { putPlan(pl.planId, { plan: plan, op: plan.operation || null, kind: 'agent' }); }, function () {});
      }
    }
    // Preview change (and Defender options): the backend builds the plan from live state; the card shows it.
    function previewAction(card, actionId) {
      // A card action that isn't a catalog handler (privacy, display, Start…) is a manual task: Settings, then Check again.
      if (Object.keys(catalog).length && !catalog[actionId]) actionId = 'manual.open';
      var cid = card.id, params = { idempotencyKey: uuid(), actionId: actionId, findingId: card.findingId };
      if (actionId === 'power.select_scheme') params.parameters = { personality: 'balanced' };
      setMode(cid, 'loading');
      backend.call('action.preview', params).then(function (plan) {
        putPlan(plan.planId, { plan: plan, op: null, cardId: cid, kind: 'card' });
        setMode(cid, plan.status === 'ineligible' ? 'ineligible' : 'preview', { planId: plan.planId });
      }, function () { setMode(cid, null); flash('Not available'); });
    }
    // Approve goes to the desktop shell's native dialog (shell.approvePlan); the page never approves by itself.
    function approvePlan(planId, cid) {
      if (cid) setMode(cid, 'running', { planId: planId });
      putPlan(planId, { approving: true });
      putPlan(planId, { approveError: null });
      // §8: approved | declined | expired | stale | unavailable | error.
      backend.call('shell.approvePlan', { planId: planId }).then(function (r) { onApproval(planId, cid, r || {}); }, function () { onApproval(planId, cid, { status: 'error' }); });
    }
    function onApproval(planId, cid, r) {
      var entry = plansRef.current[planId] || {}, plan = entry.plan || {};
      putPlan(planId, { approving: false });
      if (r.status === 'approved') { if (r.operationId) putPlan(planId, { op: Object.assign({ operationId: r.operationId, planId: planId, state: 'prepared' }, entry.op) }); return; }
      if (r.status === 'declined') {
        putPlan(planId, { plan: Object.assign({}, plan, { status: 'declined' }) });
        if (cid) setMode(cid, 'preview', { planId: planId });
        flash(r.error === 'uac_cancelled' ? 'Cancelled' : 'Declined');
        return;
      }
      if (r.status === 'stale' || r.status === 'expired') {
        // The plan changed or lapsed: preview again from live state.
        flash(r.status === 'expired' ? 'Expired' : 'Updated');
        var card = cid ? cardById(cid) : null;
        if (card) previewAction(card, plan.actionId);
        else if (plan.undoOf) { putPlan(planId, { closed: true }); undoOperation({ operationId: plan.undoOf }); }
        else putPlan(planId, { closed: true });
        return;
      }
      // unavailable / error: a short word and Retry.
      putPlan(planId, { approveError: r.status === 'unavailable' ? 'Unavailable' : 'Not approved' });
      if (cid) setMode(cid, 'preview', { planId: planId });
    }
    function declinePlan(planId) {
      putPlan(planId, { plan: Object.assign({}, (plansRef.current[planId] || {}).plan, { status: 'declined' }) });
      return backend.call('action.decline', { planId: planId }).catch(function () {});
    }
    function undoOperation(op) {
      if (!op || undoing.current[op.operationId]) return;
      // One Undo per change: a second click while its Undo is open (or on its way) does nothing.
      var open = Object.keys(plansRef.current).some(function (k) {
        var e = plansRef.current[k];
        return e.kind === 'undo' && e.plan && e.plan.undoOf === op.operationId && !e.closed && !e.op && e.plan.status !== 'declined' && e.plan.status !== 'expired';
      });
      if (open) return;
      undoing.current[op.operationId] = true;
      backend.call('action.undo', { idempotencyKey: uuid(), operationId: op.operationId }).then(function (plan) {
        delete undoing.current[op.operationId];
        putPlan(plan.planId, { plan: plan, op: null, kind: 'undo' });
      }, function () { delete undoing.current[op.operationId]; flash('Not available'); });
    }
    function checkOperation(entry) {
      if (!entry || !entry.op) return;
      backend.call('operation.get', { operationId: entry.op.operationId }).then(function (op) { putPlan(op.planId, { op: op }); }, function () { flash('Not available'); });
    }
    function recheckManual(cid, findingId) {
      backend.call('manual.recheck', { findingId: findingId }).then(function (r) {
        setMode(cid, (cardState[cid] || {}).mode || 'guide', { recheck: r.status === 'verified_by_rescan' ? 'Verified' : 'Still the same' });
      }, function () { flash('Not available'); });
    }
    // One card at a time opens full width (Why, steps, preview, ask); closing returns to the grid.
    function setFocusPart(cid, part, on) {
      setFocus(function (f) {
        var n = { cid: on ? cid : f.cid, main: on && cid !== f.cid ? false : f.main, why: Object.assign({}, f.why), ask: Object.assign({}, f.ask) };
        n[part][cid] = on; return n;
      });
    }
    function unfocus(cid) { setFocus(function (f) { return f.cid === cid ? { cid: null, main: false, why: {}, ask: {} } : f; }); }
    // A click anywhere on a card makes it the main card (top, full width, full text); the answer panel steps aside.
    function focusCard(cid) {
      setFocus(function (f) { return f.cid === cid && f.main ? f : { cid: cid, main: true, why: f.why, ask: f.ask }; });
    }
    function cardById(cid) { var f = fuRef.current; return f ? [f.card].concat(f.upcoming || []).filter(function (c) { return c && c.id === cid; })[0] || null : null; }
    function cardSettings(card) {
      var f = card && report ? report.findings.filter(function (x) { return x.id === card.findingId; })[0] : null;
      var u = card ? card.settingsUri || (f && f.settingsUri) || null : null;
      return u && SETTINGS_CATALOG.indexOf(u) >= 0 ? u : null;
    }
    function onOption(cid, o) {
      var curCard = cardById(cid), curSettings = cardSettings(curCard);
      if (!curCard) return;
      if (o === 'Intended') { ignoreAs(curCard, 'intended'); return; }
      if (o !== 'Ask about this') setFocus(function (f) { return { cid: cid, main: f.cid === cid && f.main, why: f.why, ask: f.ask }; });
      if (o === 'Open settings') { openSettings(curSettings); setMode(cid, (cardState[cid] || {}).mode || 'guide', { opened: true }); return; }
      if (o === 'Check again') { recheckManual(cid, curCard.findingId); return; }
      if (DEFENDER_OPTIONS[o]) { previewAction(curCard, DEFENDER_OPTIONS[o]); return; }
      if (o === 'View steps') setMode(cid, 'guide');
      // A preview is always built by the backend (a plan); without an action there is nothing to preview.
      else if (o === 'Preview change') { if (curCard.actionId) previewAction(curCard, curCard.actionId); else if (curCard.guide) setMode(cid, 'guide'); else flash('Not available'); }
      else if (o === 'Ask about this') { setFocusPart(cid, 'ask', true); setAskToken(function (t) { return { cid: cid, n: t.n + 1 }; }); }
      else if (OPTION_IDS[o]) leave(cid, OPTION_IDS[o]);
    }
    // With an in-app provider connected, questions go to the assistant (agent.ask). The first one opens the grant sheet.
    // The box, a card's "Ask about this" and the summary all use the same active task (m5 §6).
    function agentAsk(text, cardId, t, extra) {
      t = t || task;
      // A task is bound to the report it was started on; a newer report gets its own (the grant sheet again).
      // Claude Code / Codex are asked in the app only through a headless task (a user-window task is asked in their window).
      var sameReport = t && report && t.scope && t.scope.reportId === report.reportId;
      var askable = t && (!isMcpKind(t.recipient.kind) || t.recipient.mode === 'headless');
      if (!t || t.status !== 'active' || t.recipient.kind !== conn.kind || !sameReport || !askable) { setGrant({ kind: conn.kind, pending: Object.assign({ text: text, cardId: cardId }, extra) }); return; }
      // Only an answer still on its way holds the next question; a failed one doesn't.
      // Only this task's unanswered question holds the next one (another task's never does).
      if (answers.some(function (a) { return a.taskId === t.taskId && a.text == null && !a.failedTurn; })) { flash('One moment'); return; }
      var id = uuid(), kind = conn.kind;
      // m5 §9: every question from the box is a panel of its own, newest on top.
      setAnswers(function (list) { return list.concat([Object.assign({ id: id, q: text, cardId: cardId || null, taskId: t.taskId, turnId: null, text: null, busy: 'Thinking…', reportId: report.reportId, at: new Date().toISOString() }, extra)]).slice(-KEEP_ANSWERS); });
      var params = { idempotencyKey: uuid(), taskId: t.taskId, text: text };
      if (cardId) params.cardId = cardId;
      backend.call('agent.ask', params).then(function (r) {
        setAnswers(function (list) { return list.map(function (a) { return a.id === id && !a.turnId ? Object.assign({}, a, { turnId: r.turnId }) : a; }); });
      }, function (err) {
        var drop = function () { setAnswers(function (list) { return list.filter(function (a) { return a.id !== id; }); }); };
        // The answer to a task that is no longer the current one: nothing to show or offer.
        if (!isCurrentTask(t.taskId)) { drop(); return; }
        // The task ended (expired, stopped, budget used, failed): a fresh one, through the grant sheet, with the same question.
        if (taskEnded(err)) {
          drop();
          var reason = err.data && err.data.reason ? String(err.data.reason).replace(/^task_/, '') : 'stopped';
          setTask(function (x) { return x && x.taskId === t.taskId ? Object.assign({}, x, { status: x.status === 'active' ? (TASK_WORD_KEYS[reason] ? reason : 'stopped') : x.status }) : x; });
          setGrant({ kind: kind, pending: Object.assign({ text: text, cardId: cardId }, extra) });
        } else setAnswers(function (list) { return list.map(function (a) { return a.id === id ? Object.assign({}, a, { failedTurn: true, busy: null }) : a; }); });
      });
    }
    function startTask(sel) {
      var g = grant; if (!g || !report || g.busy) return;
      var params = { idempotencyKey: uuid(), goal: (g.pending && g.pending.text) || 'Review my report', recipientKind: g.kind,
        scope: { reportId: report.reportId, findingIds: null }, grant: { diagnostics: sel.diagnostics, share: sel.share, canRequestChanges: !!sel.canRequestChanges } };
      // A user-window task needs its approved pairing, whichever way the sheet was opened; a connected
      // Claude Code / Codex without one runs headless in the app (m5 §3).
      var headless = isMcpKind(g.kind) && !g.pairingId && stateOf(g.kind).row === 'connected';
      var pid = headless ? null : g.pairingId || (isMcpKind(g.kind) ? pairingFor(g.kind) : null);
      if (pid) params.pairingId = pid;
      setGrant(function (x) { return x && Object.assign({}, x, { busy: true }); });
      backend.call('agent.start', params).then(function (t) {
        attachTask(t, []); setGrant(null);
        if (g.pending && g.pending.scan) firstScan(t);
        else if (g.pending) agentAsk(g.pending.text, g.pending.cardId, t);
        else if (isMcpKind(g.kind) && !headless) setShowActivity(true);
      }, function () { setGrant(function (x) { return x && Object.assign({}, x, { busy: false }); }); flash('Not started'); });
    }
    // First-connect scan (m5 §6, stage aiscan): once per newly connected kind, right after its grant; Skip leaves it running.
    var scanBack = React.useRef('main');
    function firstScan(t) {
      markScanned(t.recipient.kind);
      scanBack.current = 'main';
      setShowActivity(false);
      setAnswers(function (list) { return list.filter(function (a) { return !a.scan; }); });
      agentAsk(SCAN_PROMPT, null, t, { scan: true });
      go('aiscan');
    }
    function offerFirstScan(kind) {
      if (!kind || !fuRef.current || scannedKinds().indexOf(kind) >= 0) return;
      var t = task, live = t && t.status === 'active' && t.recipient.kind === kind && t.scope && t.scope.reportId === fuRef.current.reportId &&
        (!isMcpKind(kind) || t.recipient.mode === 'headless');
      if (live) { firstScan(t); return; }
      setGrant({ kind: kind, pending: { text: SCAN_PROMPT, cardId: null, scan: true } });
    }
    // m5 §8: Stop ends only the running answer (turn or headless run); the task and its permissions stay.
    function cancelRun() {
      var t = task; if (!t) return;
      // Late replies for a task that is no longer current change nothing on screen.
      var clear = function () {
        if (!isCurrentTask(t.taskId)) return;
        setLiveTurn(null);
        setAnswers(function (list) { return list.filter(function (a) { return a.taskId !== t.taskId || a.text != null || a.failedTurn; }); });
      };
      // cancelled:false: nothing was running any more (the answer was already on its way); keep the entry for it.
      backend.call('agent.cancel', { taskId: t.taskId }).then(function (r) { if (r && r.cancelled) clear(); }, function (err) {
        if (!isCurrentTask(t.taskId)) return;
        if (err && err.code === -32001) clear(); else flash(notSupported(err) ? 'Not available' : 'Not stopped');
      });
    }
    /* ---------- m5 §9: answer panels and History ---------- */
    function setOpen(ids, on) { setPanelOpen(function (m) { var n = Object.assign({}, m); ids.forEach(function (i) { n[i] = on; }); return n; }); }
    // A new report (or a stopped scan) starts with a clear page; the answers wait in History.
    function closePanels() { setOpen(answersRef.current.map(function (a) { return a.id; }), false); }
    function showPanel(aid) {
      setOpen([aid], true); setMenu(null);
      later(function () {
        var el = document.querySelector('[data-answer="' + aid + '"]');
        if (el) { if (el.scrollIntoView) el.scrollIntoView({ block: 'nearest', behavior: reduce ? 'auto' : 'smooth' }); if (el.focus) el.focus({ preventScroll: true }); }
      }, 30);
    }
    function deleteAnswer(row) {
      var n = Object.assign({}, deletedRef.current); n[row.key] = true; deletedRef.current = n; setDeleted(n);
      storeSet(DELETED_KEY, JSON.stringify(Object.keys(n).slice(-300)));
      setAnswers(function (list) { return list.filter(function (a) { return answerKey(a) !== row.key; }); });
    }
    // A follow-up asks in its panel's conversation and opens a new panel: the panel's task when it is the current one
    // (agentAsk opens the grant sheet if it ended), or an earlier task that is still active; otherwise the usual way.
    function followUp(a, q) {
      if (!task || a.taskId === task.taskId) { agentAsk(q, null); return; }
      if (runInProgress) { flash('One moment'); return; }
      backend.call('agent.get', { taskId: a.taskId }).then(function (t) {
        var fits = t.status === 'active' && t.recipient && t.recipient.kind === conn.kind && report && t.scope && t.scope.reportId === report.reportId &&
          (!isMcpKind(t.recipient.kind) || t.recipient.mode === 'headless');
        if (!fits) { agentAsk(q, null); return; }
        attachTask(t, t.activity || []); agentAsk(q, null, t);
      }, function () { agentAsk(q, null); });
    }
    function foldCards() { setFocus({ cid: null, main: false, why: {}, ask: {} }); }
    function mergeTask(t) { setTask(function (x) { return x && t && x.taskId === t.taskId ? Object.assign({}, x, t) : x; }); }
    function stopTask() {
      if (!task) return;
      var tid = task.taskId;
      backend.call('agent.stop', { taskId: tid }).then(mergeTask, function () { if (isCurrentTask(tid)) flash('Not stopped'); });
    }
    function decidePairing(approve) {
      var pr = pairing || (connect && connect.pairingId ? { pairingId: connect.pairingId, client: null } : null);
      if (!pr || pairBusy) return;
      var kind = CLIENT_KIND[pr.client] || (connect && connect.kind);
      setPairBusy(true);
      backend.call('agent.approvePairing', { pairingId: pr.pairingId, approve: approve }).then(function (r) {
        setPairBusy(false); setPairing(null); loadProviders();
        if (r.status !== 'approved') { flash('Denied'); setConnect(function (c) { return c ? Object.assign({}, c, { phase: 'register', code: null, pairingId: null }) : c; }); return; }
        if (kind) pairedIds.current[kind] = r.pairingId;
        setGrant({ kind: kind, pairingId: r.pairingId });
        var label = (providers[kind] && providers[kind].label) || ORIGIN_LABEL[pr.client] || (kind === 'codex_mcp' ? 'Codex' : 'Claude Code');
        if (stageRef.current.id === 'login') { commitProvider({ value: TILE_OF[kind], label: label, kind: kind }); return; }
        // Approved from the card or the pill: this client is now the chosen AI (the chooser's connect step is over).
        setConnect(null);
        if (kind && conn.kind !== kind) { setConn({ value: TILE_OF[kind], label: label, kind: kind }); saveOnb({ provider: { value: TILE_OF[kind], label: label } }).catch(function () {}); }
      }, function (err) {
        setPairBusy(false);
        var reason = (err && err.data && err.data.reason) || '';
        if (err && err.code === -32013) {
          // Lapsed (or already decided): drop the card; the client asks again on its next call.
          setPairing(function (cur) { return cur && cur.pairingId === pr.pairingId ? null : cur; });
          setConnect(function (c) { return c && c.pairingId === pr.pairingId ? Object.assign({}, c, { phase: 'register', code: null, pairingId: null }) : c; });
          loadProviders();
          flash(/expired/.test(reason) ? 'Expired' : 'Updated');
        } else flash('Not saved');
      });
    }
    // Any citation chip opens a small evidence card: report evidence straight away, the AI's own measurements from its
    // task (fetched once if this window hasn't seen them yet).
    // ownerTaskId: the task that cited it (an AI card's own task when the host says so); the current task is the fallback.
    function openCite(eid, ownerTaskId) {
      var label = labels[eid] || taskLabels.current[eid] || null;
      if (evById[eid]) { setPeek({ id: eid, label: label, ev: evById[eid], inReport: true }); return; }
      if (tev.current[eid]) { setPeek({ id: eid, label: label, ev: tev.current[eid] }); return; }
      setPeek({ id: eid, label: label, ev: null });
      loadTaskEvidence([eid], ownerTaskId).then(function () {
        setPeek(function (cur) { return cur && cur.id === eid ? { id: eid, label: label, ev: tev.current[eid] || null, missing: !tev.current[eid] } : cur; });
      });
    }
    // Task evidence by ID: asks the owning task, then the current one (agent.get evidence); what neither has is gone.
    var tevLoads = React.useRef({});            // taskId → agent.get on its way
    var tevGone = React.useRef({});             // evidence ID → no task has it
    var tvn = React.useState(0), setTevN = tvn[1];
    function cardTaskId(card) { return (card && (card.sourceTaskId || card.aiTaskId || card.taskId)) || null; }
    function fetchTaskEvidence(tid) {
      if (!tevLoads.current[tid]) tevLoads.current[tid] = backend.call('agent.get', { taskId: tid }).then(function (t) {
        (t.evidence || []).forEach(function (e) { tev.current[e.id] = e; delete tevGone.current[e.id]; });
      }, function () {}).then(function () { delete tevLoads.current[tid]; });
      return tevLoads.current[tid];
    }
    function loadTaskEvidence(eids, ownerTaskId) {
      var want = (eids || []).filter(function (id) { return !evById[id] && !tev.current[id]; });
      var cur = taskRef.current && taskRef.current.taskId;
      var tids = [ownerTaskId, cur].filter(function (x, i, a) { return x && a.indexOf(x) === i; });
      var step = function (i) {
        if (i >= tids.length || !want.some(function (id) { return !tev.current[id]; })) return Promise.resolve();
        return fetchTaskEvidence(tids[i]).then(function () { return step(i + 1); });
      };
      return step(0).then(function () {
        want.forEach(function (id) { if (!tev.current[id]) tevGone.current[id] = true; });
        if (want.length) setTevN(function (n) { return n + 1; });
      });
    }
    // The selected AI isn't usable: offer its action (grant sheet on paired, the pill detail otherwise).
    function openChooser() { setDetailOpen(false); setReturnTo(stageRef.current.id); setWave({ speed: 0.7, words: [] }); go('login'); }
    function offerAction(st) {
      if (st.action === 'start') { setGrant({ kind: conn.kind }); return; }
      setDetailOpen(true);
      if (st.word) flash(st.word);
    }
    function ask(cid, text) {
      var route = routeAsk(askState);
      if (route === 'agent') { agentAsk(text, cid); return; }
      if (route !== 'local') offerAction(askState);
      respond({ scope: 'card', cardId: cid, text: text }).then(function (res) { if (res.stale) setAskRestore({ text: text, token: Date.now(), cid: cid }); });
    }
    function submitMain(text) {
      // With a report, free text is a reply or a question about it. Starters, "scan…", and anything before a report scan.
      if (stage.id !== 'main' || !report || FULL_AUDIT.test(text) || starterHit(text)) { runAudit(text, routeIntent(text)); return; }
      var card = fu && fu.card;
      // "Do that for me" opens the exact preview; it never approves.
      if (DO_THAT.test(text)) {
        // The exact preview is a plan the backend builds from live state, never the card's own sketch.
        if (card && card.actionId) { previewAction(card, card.actionId); flash('Here’s exactly what would change'); }
        else if (card && card.guide) { setMode(card.id, 'guide'); flash('This one is manual: here are the steps'); }
        else flash('Which change do you mean?');
        return;
      }
      // Anything else is a reply to the whole report: the backend re-ranks what's left; decisions stay.
      // With an in-app assistant it also answers.
      var before = card ? card.id : null, route = routeAsk(askState);
      if (inApp) agentAsk(text, null);
      else if (route !== 'local') offerAction(askState);
      respond({ scope: 'report', text: text }).then(function (res) {
        if (res.stale) { if (!inApp) setRestore({ text: text, token: Date.now() }); return; }
        if (!res.ok) return;
        var c = res.fu.card;
        if (c && c.id !== before) flash('Updated priorities: ' + (CATS[c.category] || CATS.misc).label + ' first'); else if (!inApp) flash('Noted');
      });
    }

    // Summarize with AI by route: in-app → review.preview / review.submit; native client → continue there; else connect.
    function requestSummary() {
      var k = conn.kind, st = askState;
      setShowActivity(false); // the share preview shows in the same place; Activity would cover it
      if (st.row === 'local' || !report) { setSummary('connect'); return; }
      // Claude Code / Codex in the app: the summary is one more question to the same task, answered under the box.
      if (isMcpKind(k) && st.usable) { agentAsk(SUMMARY_PROMPT, null); return; }
      if (isMcpKind(k)) { if (READY_ROWS[st.row] || st.row === 'waiting') setSummary('continue'); else offerAction(st); return; }
      if (!st.usable) { offerAction(st); return; }
      setSummary('loading');
      backend.call('review.preview', { recipientKind: k, reportId: report.reportId }).then(function (v) { setSummary({ preview: v }); }, function () { setSummary(null); flash('Not available'); });
    }
    function share() {
      var v = summary && summary.preview; if (!v) return;
      setSummary('loading');
      backend.call('review.submit', { idempotencyKey: uuid(), previewId: v.previewId }).then(function (r) { setSummary(r.lines || []); }, function () { setSummary(null); flash('Not available'); });
    }
    React.useEffect(function () {
      if (pendingSummary.current && stage.id === 'main' && !stage.leaving && report) { pendingSummary.current = false; requestSummary(); }
    }, [stage.id, stage.leaving, conn.kind, providers]);

    /* ---------- login and setup ---------- */
    function afterProvider() {
      var o = onbRef.current;
      if (!o || o.step === 'provider') return 'use';
      if (o.step !== 'complete') return STEP_STAGE[o.step];
      return 'main';
    }
    // Committing a connection: a different recipient revokes the current task; drafts and the report stay.
    // usable: the kind is connected right now, so a first connect offers its first look (m5 §6).
    function commitProvider(c, usable) {
      var next = returnTo || afterProvider();
      if (task && task.status === 'active' && task.recipient.kind !== c.kind) {
        // The old task's unanswered questions go with it; its stop reply only updates it while it is still the current one.
        var old = task.taskId;
        setLiveTurn(null);
        setAnswers(function (list) { return list.filter(function (a) { return a.taskId !== old || a.text != null || a.failedTurn; }); });
        backend.call('agent.stop', { taskId: old }).then(mergeTask, function () {});
      }
      setConn(c); setConnect(null);
      saveOnb({ provider: { value: c.value, label: c.label }, step: 'use' }).catch(function () { flash('Not saved'); });
      if (summary === 'connect' && c.kind) { pendingSummary.current = true; setSummary(null); }
      if (next === 'main') loadStarters();
      setReturnTo(null); go(next);
      if (usable && next === 'main') offerFirstScan(c.kind);
    }
    // API keys are entered in the desktop shell's own Windows dialog (shell.requestApiKey); the page never sees one.
    // Cancel says nothing; a failed check or save comes back as { failed: code } so the chooser can say why.
    function keyDialog(kind) {
      return backend.call('shell.requestApiKey', { kind: kind }).then(function (r) {
        if (r && r.status === 'connected') return backend.call('provider.connect', { kind: kind });
        if (r && r.status === 'error') return { failed: r.error || 'failed' };
        return null;
      }, function (e) { return { failed: e && e.code === -32013 ? 'busy' : 'offline' }; });
    }
    // ChatGPT plan: the backend opens OpenAI's sign-in in the browser and reports the outcome as provider.status;
    // the effect below commits the choice once it reads connected (or clears "Connecting…" on an error).
    function signInChatGpt(commit) {
      var kind = 'chatgpt_plan', e0 = providers[kind];
      if (commit && e0 && e0.status === 'connected') { commitProvider({ value: 'chatgpt', label: e0.label || 'ChatGPT', kind: kind }, true); return Promise.resolve(e0); }
      if (commit) setConnect({ tile: 'chatgpt', kind: kind, phase: 'key' });
      return backend.call('provider.connect', { kind: kind }).then(function (e) { mergeProvider(e); return e; },
        function () { if (commit) setConnect(null); flash('Offline'); return null; });
    }
    var signInEntry = providers.chatgpt_plan;
    React.useEffect(function () {
      if (!connect || connect.kind !== 'chatgpt_plan' || !signInEntry) return;
      if (signInEntry.status === 'connected') commitProvider({ value: 'chatgpt', label: signInEntry.label || 'ChatGPT', kind: 'chatgpt_plan' }, true);
      else if (signInEntry.status === 'error' || signInEntry.status === 'not_configured') { setConnect(null); flash(stateOf('chatgpt_plan').word || 'Not signed in'); }
    }, [signInEntry && signInEntry.status, signInEntry && signInEntry.detail]);
    function apiAction(kind, commit) {
      if (kind === 'chatgpt_plan') return signInChatGpt(commit);
      var st = stateOf(kind);
      var run = st.action === 'retry' ? backend.call('provider.connect', { kind: kind })
        : st.action === 'addKey' || st.action === 'updateKey' ? keyDialog(kind)
        : backend.call('provider.connect', { kind: kind }).then(function (e) { mergeProvider(e); return e.status === 'not_configured' ? keyDialog(kind) : e; });
      if (commit) setConnect({ tile: 'api-key', kind: kind, phase: 'key' });
      return run.then(function (e) {
        if (commit) setConnect(null);
        if (!e) return null;
        if (e.failed) {
          // The key's check already moved the tile (provider.status); re-read it in case the event was missed.
          if (e.failed !== 'busy') loadProviders();
          flash(KEY_FAIL[e.failed] || 'Not connected');
          return null;
        }
        mergeProvider(e);
        if (e.status !== 'connected') { flash(stateOf(kind, e).word || 'Not set up'); return null; }
        if (commit) commitProvider({ value: 'api', label: e.label, kind: kind }, true);
        return e;
      }, function () { if (commit) setConnect(null); flash('Offline'); return null; });
    }
    function connectApi(kind) { apiAction(kind, true); }
    function disconnectKind(kind) {
      if (!kind) return;
      backend.call('provider.disconnect', { kind: kind }).then(function (e) {
        mergeProvider(e); setDetailOpen(false); setConnect(null);
        if (isMcpKind(kind)) { delete pairedIds.current[kind]; setPairing(function (cur) { return cur && cur.client === CLIENT_OF_KIND[kind] ? null : cur; }); }
        if (task && task.status === 'active' && task.recipient.kind === kind) {
          var old = task.taskId;
          setTask(function (x) { return x && x.taskId === old ? Object.assign({}, x, { status: 'stopped' }) : x; });
          setLiveTurn(null);
          setAnswers(function (list) { return list.filter(function (a) { return a.taskId !== old || a.text != null || a.failedTurn; }); });
        }
        if (conn.kind === kind) { setConn({ value: 'local', label: 'Local only', kind: null }); saveOnb({ provider: { value: 'local', label: 'Local only' } }).catch(function () {}); }
      }, function () { flash('Not saved'); });
    }
    function showCommandFor(kind) {
      setShowCmd(true);
      if (regs.current[kind]) return;
      backend.call('provider.connect', { kind: kind }).then(function (e) { mergeProvider(e); rememberReg(kind, e); }, function () {});
    }
    function rememberReg(kind, e) { var reg = e && e.registration; if (reg) regs.current[kind] = kind === 'codex_mcp' ? reg.codex : reg.claude; }
    // Claude Code / Codex: show the registration command, then wait for the bridge's pairing request.
    function connectNative(tile, kind, label) {
      backend.call('provider.connect', { kind: kind }).then(function (e) {
        mergeProvider(e); rememberReg(kind, e);
        if (e.status === 'paired') { setGrant({ kind: kind, pairingId: e.pairingId || pairingFor(kind) }); commitProvider({ value: tile, label: e.label || label, kind: kind }); return; }
        if (e.status === 'active' || e.status === 'client_closed') { commitProvider({ value: tile, label: e.label || label, kind: kind }); return; }
        var reg = e.registration || {};
        // The choice is only made on Continue (or an approved pairing); Back leaves the previous AI as it was.
        setConnect({ tile: tile, kind: kind, label: e.label || label, phase: 'register', command: kind === 'codex_mcp' ? reg.codex : reg.claude });
        if (pairing && CLIENT_KIND[pairing.client] === kind) setConnect(function (c) { return Object.assign({}, c, { phase: 'pairing', code: pairing.code, pairingId: pairing.pairingId }); });
      }, function () { flash('Not connected'); });
    }
    // The shell runs one client check or change at a time; the page queues them instead of colliding ("busy").
    var clientChain = React.useRef(Promise.resolve());
    function clientCall(method, params) {
      var run = function () { return backend.call(method, params); };
      var next = clientChain.current.then(run, run);
      clientChain.current = next.catch(function () {});
      return next;
    }
    // One check per client at a time, and at most one more waiting behind it (focus, polls and clicks coalesce).
    var clientChecks = React.useRef({});
    function checkClient(tile) {
      var client = CLIENT_OF_TILE[tile]; if (!client) return Promise.resolve();
      var q = clientChecks.current[tile] || (clientChecks.current[tile] = { running: null, again: false });
      if (q.running) { q.again = true; return q.running; }
      q.running = runClientCheck(tile, client, 0).then(function () {
        q.running = null;
        if (q.again) { q.again = false; return checkClient(tile); }
      });
      return q.running;
    }
    function runClientCheck(tile, client, tries) {
      return clientCall('shell.clientStatus', { client: client }).then(function (r) {
        // Fields a newer answer leaves out must not linger from an older one.
        putClient(tile, Object.assign({ known: true, busy: null, failed: null, blocked: false, blockedReason: null, outdated: false, auth: null, app: false }, r));
      }, function (e) {
        // Busy (another check or a change is running): try again shortly, in the same slot. Anything else is an older
        // shell without one-click connect: keep the manual command flow.
        if (e && e.code === -32013 && tries < 5) return new Promise(function (res) { setTimeout(res, 800); }).then(function () { return runClientCheck(tile, client, tries + 1); });
        putClient(tile, { known: false });
      });
    }
    // The chooser's bottom button for Claude Code / Codex (m5 §6): Get / Install, then Sign in; Continue is chooseProvider.
    function clientAction(tile) {
      var cs = clients[tile], client = CLIENT_OF_TILE[tile];
      if (!cs || !cs.known) { checkClient(tile); return; }
      // Blocked on this PC: nothing to click through; check again after the user changed something.
      // (node_missing / unsupported_path: the official installer needs neither, so it installs.)
      if (cs.blocked) { if (cs.blockedReason === 'node_missing' || cs.blockedReason === 'unsupported_path') installClient(tile); else checkClient(tile); return; }
      // Only an outdated CLI: the official installer brings the current one.
      if (cs.outdated) { installClient(tile); return; }
      // Installed, but the sign-in check gave no answer: ask again instead of opening another sign-in.
      if (cs.installed && cs.signedIn == null && !cs.signing) { checkClient(tile); return; }
      if (!cs.installed) {
        if (cs.app) { checkClient(tile); return; }   // Claude's app is here; its Code tab brings Claude Code on first open.
        if (isFixture) { try { window.open(CLIENT_PAGE[client], '_blank', 'noopener'); } catch (e) { /* popup blocked */ } return; }
        if (DESKTOP_OPEN_URL) backend.call('shell.openUrl', { url: CLIENT_PAGE[client] }).catch(function () { flash('Not available'); });
        return;
      }
      signInClient(tile);
    }
    // Optional: also add the connector to the client's own window (m3 pairing path), after the shell's native confirm.
    function registerClient(tile) {
      var client = CLIENT_OF_TILE[tile]; if (!client) return;
      putClient(tile, { busy: 'Adding…', failed: null });
      clientCall('shell.registerClient', { client: client }).then(function (r) {
        putClient(tile, { busy: null });
        if (r.status === 'added') { putClient(tile, { registered: 'yes' }); flash('Added'); }
        else if (r.status === 'failed') flash('Not added');
        else if (r.status === 'not_installed') putClient(tile, { installed: false });
        // cancelled: nothing changes
      }, function () { putClient(tile, { busy: null }); flash('Not added'); });
    }
    // Install and sign-in both finish in a window of their own: the tile re-checks every few seconds until they show.
    var installTimers = React.useRef({});
    function stopInstallPoll(key) { var t = installTimers.current[key]; if (t) { t.stopped = true; clearTimeout(t.timer); delete installTimers.current[key]; } }
    React.useEffect(function () { return function () { Object.keys(installTimers.current).forEach(stopInstallPoll); }; }, []);
    // The next check is scheduled only after the previous one answered, so a slow shell never piles them up.
    function pollClient(tile, key, every, forMs, flag) {
      stopInstallPoll(key);
      var until = Date.now() + forMs, job = { stopped: false, timer: null };
      installTimers.current[key] = job;
      function next() {
        job.timer = setTimeout(function () {
          if (job.stopped) return;
          if (Date.now() > until) { stopInstallPoll(key); var off = {}; off[flag] = false; putClient(tile, off); return; }
          checkClient(tile).then(function () { if (!job.stopped) next(); });
        }, every);
      }
      next();
    }
    function installClient(tile) {
      var client = CLIENT_OF_TILE[tile]; if (!client) return;
      putClient(tile, { failed: null });
      backend.call('shell.installClient', { client: client }).then(function (r) {
        if (r.status !== 'started') { if (r.status === 'failed') putClient(tile, { failed: 'Not started' }); return; }
        putClient(tile, { installing: true });
        pollClient(tile, tile, INSTALL_POLL_MS, INSTALL_POLL_FOR_MS, 'installing');
      }, function () { putClient(tile, { failed: 'Not started' }); });
    }
    // m5 §2: a visible console runs the client's own sign-in; nothing on the PC changes, so there is no confirm.
    function signInClient(tile) {
      var client = CLIENT_OF_TILE[tile]; if (!client) return;
      putClient(tile, { failed: null });
      clientCall('shell.signInClient', { client: client }).then(function (r) {
        if (r.status !== 'started') { putClient(tile, { failed: 'Not started' }); if (r.error === 'not_installed') putClient(tile, { installed: false }); return; }
        putClient(tile, { signing: true });
        pollClient(tile, tile + ':signin', SIGNIN_POLL_MS, SIGNIN_POLL_FOR_MS, 'signing');
      }, function () { putClient(tile, { failed: 'Not started' }); });
    }
    React.useEffect(function () {
      Object.keys(clients).forEach(function (tile) {
        var cs = clients[tile];
        if (cs.installed && !cs.outdated && !cs.blocked && installTimers.current[tile]) { stopInstallPoll(tile); putClient(tile, { installing: false }); }
        if (cs.signedIn === true && installTimers.current[tile + ':signin']) { stopInstallPoll(tile + ':signin'); putClient(tile, { signing: false }); }
      });
    }, [clients]);
    // Installed or signed in elsewhere while the app was open: re-check when the window comes back.
    var clientsRef = React.useRef(clients); clientsRef.current = clients;
    React.useEffect(function () {
      function onFocus() { Object.keys(clientsRef.current).forEach(function (tile) { var cs = clientsRef.current[tile]; if (cs && cs.known && (!cs.installed || cs.signedIn !== true || cs.blocked || cs.outdated)) checkClient(tile); }); }
      window.addEventListener('focus', onFocus);
      return function () { window.removeEventListener('focus', onFocus); };
    }, []);
    function chooseProvider(value, label, api) {
      if (value === 'api-key') { connectApi(api === 'openai' ? 'openai_api' : 'anthropic_api'); return; }
      if (value === 'chatgpt') { signInChatGpt(true); return; }
      // Already paired, working or just closed: continue with it, whatever the CLI check says.
      if (NATIVE_KIND[value] && READY_ROWS[stateOf(NATIVE_KIND[value]).row]) { connectNative(value, NATIVE_KIND[value], label); return; }
      // Installed and signed in: the app asks it in the background (m5); its first look follows the grant.
      if (NATIVE_KIND[value] && stateOf(NATIVE_KIND[value]).row === 'connected') {
        var e = providers[NATIVE_KIND[value]];
        commitProvider({ value: value, label: (e && e.label) || label, kind: NATIVE_KIND[value] }, true); return;
      }
      if (NATIVE_KIND[value]) { clientAction(value); return; }
      commitProvider({ value: value, label: label, kind: null });
    }

    var labels = React.useMemo(function () { return citeLabels(report); }, [report]);
    var evById = React.useMemo(function () { var m = {}; if (report) report.evidence.forEach(function (e) { m[e.id] = e; }); return m; }, [report]);
    var rows = React.useMemo(function () { return reportRows(report, labels); }, [report, labels]);

    // v1.4: the visible card plus up to two upcoming ones, each with its own state and index.
    var cards = fu ? [fu.card].concat(fu.upcoming || []).filter(Boolean).slice(0, 3) : [];
    var curCard = cards[0] || null, curId = curCard ? curCard.id : null;
    var total = fu ? fu.total : 0;
    var plansRef = React.useRef(plans); plansRef.current = plans;
    function cardOp(cid) {
      var best = null;
      var fid = cardFinding.current[cid];
      Object.keys(plans).forEach(function (k) { var e = plans[k]; if (e.kind === 'card' && e.op && (e.cardId === cid || (fid && e.plan && e.plan.findingId === fid))) best = e; });
      return best;
    }
    var reviewed = fu ? fu.decisions.map(function (d) {
      var e = cardOp(d.cardId), verified = e && e.op.state === 'verified';
      return { id: d.cardId, status: verified ? 'verified' : d.status, undo: !!(verified && e.op.undo && e.op.undo.available), busy: !!busyItems[d.cardId],
        text: verified && e.op.receipt ? e.op.receipt.text : (d.status === 'verified' || d.status === 'restored') && d.done ? d.done : d.title };
    }) : [];
    function cardOptions(card, finding) {
      var o = (card.options || []).map(function (x) { return x === 'Ignore' && card.origin === 'ai' ? 'Hide' : x; }), extra = [];
      var rule = finding ? finding.ruleId || '' : '';
      Object.keys(DEFENDER_OPTIONS).forEach(function (label) {
        var a = DEFENDER_OPTIONS[label], c = catalog[a];
        var fits = a === 'defender.update_signatures' ? card.actionId === a || rule === 'av.signatures_stale' : /^av\./.test(rule) || card.actionId === a;
        if (c && c.enabled && fits) extra.push(label);
      });
      return o.length ? [o[0]].concat(extra, o.slice(1)) : extra;
    }
    var openId = focus.cid && cards.some(function (c) { return c.id === focus.cid; }) &&
      (focus.main || (cardState[focus.cid] || {}).mode || focus.why[focus.cid] || focus.ask[focus.cid]) ? focus.cid : null;
    // The open card stays open: a click elsewhere leaves it. It folds by its Collapse control or Escape (when nothing
    // else Escape closes is open), or when another card is opened; not while a change is prepared or applied in it.
    var openMode = openId ? (cardState[openId] || {}).mode : null;
    var canFold = !!openId && openMode !== 'loading' && openMode !== 'preview' && openMode !== 'running';
    React.useEffect(function () {
      if (!canFold || peek || detailOpen || showActivity || sheet || menu) return;
      function onKey(e) { if (e.key === 'Escape' && !e.defaultPrevented) foldCards(); }
      window.addEventListener('keydown', onKey);
      return function () { window.removeEventListener('keydown', onKey); };
    }, [canFold, peek, detailOpen, showActivity, sheet, menu]);
    var sidePlans = Object.keys(plans).map(function (k) { return Object.assign({ planId: k }, plans[k]); }).filter(function (e) {
      return (e.kind === 'agent' || e.kind === 'undo') && e.plan && !e.closed && e.plan.status !== 'declined' && e.plan.status !== 'expired' && e.plan.status !== 'cancelled';
    });
    var cls = function (base) { return cx(base, stage.leaving ? 'ot-sink' : 'ot-enter'); };
    var id = stage.id, signedIn = id !== 'login' && id !== 'boot';
    var toLogin = stage.leaving && stage.next === 'login';
    var summaryLines = Array.isArray(summary) ? summary : null;
    var isLocal = conn.value === 'local' || conn.value === 'later';
    var curProvider = !isLocal && conn.kind ? providers[conn.kind] : null;
    var taskActive = !!(task && task.status === 'active');
    // A paired native client working on an active task is the live connection shown in the pill, whatever is selected.
    var nativeLive = taskActive && task.recipient && isMcpKind(task.recipient.kind);
    function stateOf(kind, entryOverride) {
      return connectionState({ kind: kind, local: !kind, entry: entryOverride || (kind ? providers[kind] : null), label: conn.label, backend: bstate, task: task });
    }
    var pillKind = nativeLive ? task.recipient.kind : isLocal ? null : conn.kind;
    var pillState = stateOf(pillKind);
    var askState = stateOf(isLocal ? null : conn.kind);
    var inApp = routeAsk(askState) === 'agent';
    // Chooser view of each native client (m5 §6): Install → Sign in → Connected, and the optional window connector.
    var clientViews = {};
    Object.keys(CLIENT_OF_TILE).forEach(function (tile) {
      var cs = clients[tile]; if (!cs || !cs.known) return;
      var client = CLIENT_OF_TILE[tile], name = tile === 'codex' ? 'Codex' : 'Claude Code';
      var installing = { installCmd: INSTALL_CMD[client], installing: !!cs.installing, failed: cs.failed,
        onInstall: function () { installClient(tile); }, onCheck: function () { checkClient(tile); } };
      // The CLI is there but this PC won't let it run, or only an old one is: both stay on the install step.
      if (cs.blocked) {
        var b = BLOCKED[cs.blockedReason] || BLOCKED.launch_failed;
        // reinstall: the official installer works where this copy can't (no Node needed, a normal folder).
        clientViews[tile] = Object.assign({ notInstalled: true, blocked: true, reinstall: !!b.reinstall, hint: cs.installing ? 'Installing…' : b.word,
          note: b.note, title: b.reinstall ? 'Install ' + name : b.word, primary: b.reinstall ? 'Install ' + name : 'Check again', external: false }, installing);
        return;
      }
      if (cs.outdated) {
        clientViews[tile] = Object.assign({ notInstalled: true, outdated: true, hint: cs.installing ? 'Updating…' : 'Update needed',
          note: 'This version is too old for the app.', primary: 'Update ' + name, external: false }, installing);
        return;
      }
      clientViews[tile] = !cs.installed
        ? { notInstalled: true, app: !!cs.app, hint: cs.installing ? 'Installing…' : cs.app ? 'Open Claude once' : 'Not installed',
            primary: cs.app ? 'Check again' : tile === 'codex' ? 'Get Codex' : 'Get Claude', external: !cs.app,
            disabled: !cs.app && !isFixture && !DESKTOP_OPEN_URL, installCmd: INSTALL_CMD[client], installing: !!cs.installing, failed: cs.failed,
            onInstall: function () { installClient(tile); }, onCheck: function () { checkClient(tile); } }
        : { installed: true, signedIn: cs.signedIn, signing: !!cs.signing, failed: cs.failed,
            // signedIn null: the sign-in check gave no answer (slow PC, antivirus): check again rather than sign in again.
            unknown: cs.signedIn == null && !cs.signing,
            hint: cs.signedIn === true ? null : cs.signing ? 'Signing in…' : cs.signedIn == null ? 'Couldn’t check' : 'Not signed in',
            billing: cs.signedIn === true && BILLING[client] ? BILLING[client][cs.auth] || null : null,
            onSignIn: function () { signInClient(tile); },
            registered: cs.registered, adding: cs.busy, onRegister: function () { registerClient(tile); }, onCheck: function () { checkClient(tile); } };
    });
    var tileStates = {};
    ['anthropic_api', 'openai_api', 'chatgpt_plan', 'claude_code_mcp', 'codex_mcp'].forEach(function (k) { if (providers[k]) tileStates[k] = stateOf(k); });
    // The row's actions, for the pill detail.
    function rowActions(st, kind) {
      var a = [];
      if (st.action === 'signIn' && isMcpKind(kind)) a.push({ label: ACTION_WORD.signIn, onClick: function () { signInClient(TILE_OF[kind]); } });
      else if (st.action === 'install') a.push({ label: ACTION_WORD.install, onClick: openChooser });
      else if (st.action === 'addKey' || st.action === 'updateKey' || st.action === 'retry' || st.action === 'signIn') a.push({ label: ACTION_WORD[st.action], onClick: function () { apiAction(kind, false); } });
      else if (st.action === 'approve' && pairing) {
        a.push({ label: 'Deny', onClick: function () { decidePairing(false); } });
        a.push({ label: 'Approve', icon: 'check', onClick: function () { decidePairing(true); } });
      } else if (st.action === 'start') a.push({ label: 'Start', onClick: function () { if (report) { setGrant({ kind: kind }); setDetailOpen(false); } else flash('Scan first'); } });
      else if (st.action === 'activity' || st.action === 'askAgain') {
        if (task) a.push({ label: 'Activity', onClick: function () { setShowActivity(true); setDetailOpen(false); } });
        if (st.action === 'activity' && taskActive) a.push({ label: 'Stop', icon: 'stop', onClick: stopTask });
      } else if (st.action === 'showCommand' && !(showCmd || waitLong)) a.push({ label: 'Show command', onClick: function () { showCommandFor(kind); } });
      return a;
    }
    var waitKind = id === 'login' ? (connect && connect.kind) : detailOpen ? pillKind : null;
    var waitEntry = waitKind ? providers[waitKind] : null;
    React.useEffect(function () {
      // After 60 s in waiting with the chooser or the pill detail open, show the command again with the check help.
      setWaitLong(false);
      if (!waitEntry || waitEntry.status !== 'waiting') return;
      var since = Date.parse(waitEntry.since || '') || Date.now();
      var t = setTimeout(function () { setWaitLong(true); if (waitKind) showCommandFor(waitKind); }, Math.max(0, since + WAIT_HELP_MS - Date.now()));
      return function () { clearTimeout(t); };
    }, [waitKind, waitEntry && waitEntry.status, waitEntry && waitEntry.since]);
    React.useEffect(function () { setShowCmd(false); }, [pillKind, pillState.row]);
    // Report evidence keeps its e-label; evidence from this task's own checks is numbered m1, m2 … as it is cited.
    function citeChip(id) {
      if (!labels[id] && !taskLabels.current[id]) taskLabels.current[id] = 'm' + (Object.keys(taskLabels.current).length + 1);
      return { id: id, label: labels[id] || taskLabels.current[id] };
    }
    // A failed in-app turn shows the connection's state word and its action (or "No answer").
    var failWord = askState.tone === 'expired' && askState.word ? askState.word : 'No answer';
    var failAction = ACTION_WORD[askState.action] && isApiKind(conn.kind) ? { label: ACTION_WORD[askState.action], onClick: function () { apiAction(conn.kind, false); } }
      : askState.action === 'signIn' && isMcpKind(conn.kind) ? { label: ACTION_WORD.signIn, onClick: function () { signInClient(TILE_OF[conn.kind]); } } : null;
    var aiLabel = (task && task.recipient && task.recipient.label) || (curProvider && curProvider.label) || (isLocal ? null : conn.label);
    var reportAnswers = answers.filter(function (a) { return !a.cardId && (!a.reportId || !report || a.reportId === report.reportId); }).map(function (a) {
      return Object.assign({}, a, { cites: (a.citations || []).map(citeChip), failed: a.failedTurn ? failWord : null, action: a.failedTurn ? failAction : null });
    });
    // A turn or run in progress (m5 §8): only then is there something to Stop; Stop cancels the run, never the task.
    var taskWaiting = !!task && answers.some(function (a) { return a.taskId === task.taskId && a.text == null && !a.failedTurn; });
    var runInProgress = taskActive && (!!liveTurn || taskWaiting);
    var answersBusy = reportAnswers.some(function (a) { return a.text == null && !a.failed; });
    // While a report-wide answer is on its way the toolbar stays; only Summarize with AI waits (one run per task).
    var reportBusy = (answersBusy && runInProgress) || summary === 'loading';
    // m5 §9: one panel per question, newest first; History lists them all (closed ones included).
    var pv2 = answerPanels(answers, { open: panelOpen, deleted: deleted });
    var panels = pv2.panels.map(function (a) {
      return Object.assign({}, a, { cites: (a.citations || []).map(citeChip), failed: a.failedTurn ? failWord : null, action: a.failedTurn ? failAction : null });
    });
    var historyRows = pv2.history.map(function (r) { return Object.assign({}, r, { when: r.at ? whenDay(r.at) : '' }); });
    var actItems = activity.map(function (a) {
      var args = a.arguments || {};
      return { id: a.invocationId, title: toolWords(a).replace(/ · \d+ s$/, ''), origin: a.origin, originLabel: ORIGIN_LABEL[a.origin] || a.origin, size: a.bytes ? kib(a.bytes) : null,
        status: a.status, evidenceIds: a.evidenceIds, startedAt: startedAt.current[a.invocationId],
        window: a.status === 'running' && a.tool === 'run_diagnostic' && args.diagnosticId === 'process.sample' ? (args.window || 15) : null };
    }).map(function (it, i, all) { return it.status === 'running' || it.window ? it : Object.assign({}, it, { title: toolWords(activity[i]) }); });
    // While the assistant or a native client works and Activity is closed, the pill says so.
    var working = taskActive && !showActivity && (!!workedAt || activity.some(function (a) { return a.status === 'running'; }) || taskWaiting);
    var TASK_WORD = { stopped: 'Stopped', expired: 'Expired', exhausted: 'Budget used', failed: 'Failed' };
    var grantFor = grant && report && grant.kind ? grant : null;
    // What the grant sheet offers: the diagnostics this build has (provider.list), none for an explain-only account.
    var grantEntry = grantFor ? providers[grantFor.kind] : null;
    var grantToolsOff = !!(grantEntry && grantEntry.capabilities && grantEntry.capabilities.tools === false);
    var grantDiagnostics = grantToolsOff ? [] : availDiag || Object.keys(DIAG_LABELS);
    var classes = summary && summary.preview ? (summary.preview.fieldClasses || []).map(function (c) { return typeof c === 'string' ? { id: c, label: c, shared: true } : c; }) : null;
    var agentPanels = function () {
      return sidePlans.map(function (e) {
        var pl = e.plan, by = pl.requestedBy || {}, phase = pl.status === 'ineligible' ? 'ineligible' : opPhase(e.op) || (e.approving || pl.status === 'approved' || pl.status === 'running' ? 'running' : 'awaiting');
        var close = function () { putPlan(e.planId, { closed: true }); };
        return h(PlanCard, { key: 'plan:' + e.planId, className: 'ot-enter',
          title: e.kind === 'undo' ? 'Undo' : (REQUESTER[by.kind] || 'Assistant') + ' wants to change', subtitle: pl.label || (pl.target && pl.target.label),
          state: phase, preview: planPreview(pl), ineligible: pl.ineligible, progress: e.progress || PROGRESS[pl.actionId] || 'Applying…',
          receipt: e.op && e.op.receipt && e.op.receipt.text, failWord: e.op && FAIL_WORD[e.op.state],
          onApprove: function () { approvePlan(e.planId, null); }, onDecline: function () { declinePlan(e.planId); },
          approveError: e.approveError, onRetry: function () { approvePlan(e.planId, null); }, failManualUri: e.op && e.op.manualUri,
          onOpenSettings: openSettings, onClose: close, onCheck: function () { checkOperation(e); },
          onUndo: e.op && e.op.undo && e.op.undo.available ? function () { close(); undoOperation(e.op); } : null });
      }).concat([
        pairing && id !== 'login' && h(PairingCard, { key: 'pair', className: 'ot-enter', client: ORIGIN_LABEL[pairing.client] || 'Claude Code', code: pairing.code, busy: pairBusy,
          onApprove: function () { decidePairing(true); }, onDeny: function () { decidePairing(false); } }),
        grantFor && h(GrantSheet, { key: 'grant:' + grantFor.kind, className: 'ot-enter', recipient: (providers[grantFor.kind] && providers[grantFor.kind].label) || '',
          goal: grantFor.pending && grantFor.pending.text, findings: report.counts.findings, busy: !!grantFor.busy,
          limits: grantDiagnostics.length ? DEFAULT_LIMITS : { totalBytes: DEFAULT_LIMITS.totalBytes, turns: DEFAULT_LIMITS.turns },
          diagnostics: grantDiagnostics.map(function (d) { return { id: d, label: DIAG_LABELS[d] || d }; }), changes: !grantToolsOff,
          onStart: startTask, onCancel: function () { if (grantFor.pending && !grantFor.pending.cardId && !grantFor.pending.scan) setRestore({ text: grantFor.pending.text, token: Date.now() }); setGrant(null); } }),
        showActivity && task && h(ActivityPanel, { key: 'act', className: 'ot-enter', title: task.recipient.label + (taskActive ? '' : ' · ' + (TASK_WORD[task.status] || task.status)),
          active: taskActive, items: actItems, nativeClient: !!(task.recipient && /_mcp$/.test(task.recipient.kind || '') && task.recipient.mode !== 'headless'), used: task.used, limits: task.grant && task.grant.limits, highlight: actHit,
          // Stop only while a turn or run is in progress, and it cancels that run (agent.cancel), not the task.
          onStop: runInProgress ? cancelRun : null, onClose: function () { setShowActivity(false); setActHit(null); } })]);
    };

    var toastEl = toast && h('div', { className: 'ot-toast text-mono-sm ot-fade', role: 'status' }, toast);
    // A setup step moves on only once it is saved (saveOnb shows "Not saved" otherwise).
    function saveStep(patch, next, after) {
      if (stepSaving.current) return;
      stepSaving.current = true;
      saveOnb(patch).then(function () { stepSaving.current = false; if (after) after(); go(next); }, function () { stepSaving.current = false; });
    }
    var body = null;
    if (bstate === 'incompatible' || (failed && id === 'boot')) {
      body = h('div', { key: 'down', className: 'ot-flow-panel ot-enter' }, h(GlassPanel, { strong: true, className: 'ot-choice ot-frost' },
        h('h2', { className: 'text-title-2' }, bstate === 'incompatible' ? 'Update Optimus' : 'Backend unavailable'),
        bstate !== 'incompatible' && h('div', { className: 'ot-choice-foot' }, h(Button, { variant: 'primary', onClick: boot }, 'Retry'))));
    } else if (id === 'login') {
      body = h('div', { key: 'login', className: cls('ot-flow-panel') }, h(LoginPanel, {
        initialProvider: isLocal ? 'claude-code' : conn.value === 'api' ? 'api-key' : conn.value,
        // Back leaves everything as it was: a half-made connection on the chooser is dropped.
        onBack: returnTo ? function () { var r = returnTo; setReturnTo(null); setConnect(null); go(r); } : null,
        initialApi: conn.kind === 'openai_api' ? 'openai' : 'anthropic', connect: connect, pairBusy: pairBusy,
        onApprove: function () { decidePairing(true); }, onDeny: function () { decidePairing(false); },
        onContinue: function () { if (connect) commitProvider({ value: connect.tile, label: connect.label || conn.label, kind: connect.kind }); },
        onCopyFail: function () { flash('Copy failed'); },
        tileStates: tileStates, waitLong: waitLong, onDisconnect: disconnectKind,
        onTile: checkClient, onClientAction: clientAction, clientViews: clientViews,
        onLater: function () { chooseProvider('later', 'Later'); },
        onConnect: chooseProvider }),
        toastEl);
    } else if (id === 'use') {
      body = h('div', { key: 'use', className: cls('ot-flow-panel') }, h(ChoicePanel, { step: '1 / 3', title: 'How do you use your PC?', initial: usage,
        options: USE_OPTIONS, onNext: function (sel) {
          var s = splitList(sel, USE_OPTIONS, 'customUse');
          setUsage(sel); saveStep({ uses: s.list, customUse: s.customUse, step: 'goal' }, 'goal');
        } }), toastEl);
    } else if (id === 'goal') {
      body = h('div', { key: 'goal', className: cls('ot-flow-panel') }, h(ChoicePanel, { step: '2 / 3', title: 'What would you like help with?', initial: goals,
        options: GOAL_OPTIONS, onNext: function (sel) {
          var s = splitList(sel, GOAL_OPTIONS, 'customGoal');
          setGoals(sel); saveStep({ goals: s.list, customGoal: s.customGoal, step: 'specs' }, 'specs');
        } }), toastEl);
    } else if (id === 'specs') {
      body = h('div', { key: 'specs', className: cls('ot-flow-panel') }, h(SpecScanPanel, { step: '3 / 3', steps: inv.rows, current: inv.current,
        onStart: function () {
          if (busy.current || inv.current >= 0) return; busy.current = true;
          setInv({ current: 0, rows: emptyRows() });
          setWave({ speed: 2.2, words: [] });
          saveOnb({ inventoryConsent: 'granted' }).then(function () {
            return backend.call('inventory.start', { idempotencyKey: uuid() });
          }).then(function (r) {
            busy.current = false;
            invRef.current = { inventoryId: r.inventoryId, stream: 'inventory:' + r.inventoryId, finished: false };
            drain();
          }, function () { busy.current = false; setInv({ current: -1, rows: emptyRows() }); setWave({ speed: 0.7, words: [] }); });
        },
        onDone: function () {
          saveOnb({ step: 'complete' }).then(loadStarters, function () {});
          setWave({ speed: 0.7, words: [] }); go('main');
        },
        // Skip creates no inventory job.
        onSkip: function () {
          if (busy.current) return;
          saveStep({ inventoryConsent: 'skipped', step: 'complete' }, 'main', loadStarters);
        } }), toastEl);
    } else if (id === 'aiscan') {
      // First look: live tool rows, then the answer; Skip any time (the run goes on), Continue once it answered.
      var scanAns = reportAnswers.filter(function (a) { return a.scan; }).pop() || null;
      var back = function () { go(scanBack.current); };
      body = h('div', { key: 'aiscan', className: cls('ot-flow-panel') }, h(OT.AiScanPanel, { title: aiLabel, active: taskActive, items: actItems,
        answer: scanAns, done: !!(scanAns && (scanAns.text != null || scanAns.failed)), onCite: openCite, onContinue: back, onSkip: back }), toastEl);
    } else if (id !== 'boot') {
      var onMain = id === 'main';
      body = h('div', { key: 'stage-main', className: cx('ot-flow-main', toLogin && 'ot-sink') },
        // Welcome back: when the latest report is from, and Continue to the cards left to review (on this page).
        onMain && welcome && h('div', { key: 'wb', className: 'ot-welcome-block ot-enter' },
          h('div', { className: 'ot-welcome text-headline' }, 'Welcome back'),
          h('div', { className: 'ot-reportbar ot-welcome-bar' },
            h('span', { className: 'text-mono-sm ot-muted' }, whenDay(welcome.generatedAt) + (welcome.pending > 0 ? ' · ' + welcome.pending + ' to review' : '')))),
        h(PromptBox, { className: 'ot-enter', busy: id === 'scanning', autoFocus: true, onDraft: setDraft, restore: restore,
          placeholder: onMain && report ? 'Reply, or ask about the report' : 'What would you like to check?',
          onSubmit: submitMain }),
        toast && h('div', { className: 'ot-toast text-mono-sm ot-fade', role: 'status' }, toast),
        id === 'scanning' && h(ScanStatus, { className: 'ot-enter', cats: scanCats.length ? scanCats : OT.ui.SCAN_CATS, done: scanDone, current: scanCurrent, skipping: skipping, onSkip: skipCollector, onStop: stopScan }),
        // One toolbar under the box. Suggestions and History drop down here; Report opens the report over the page.
        // While a report-wide answer runs it stays: only Summarize waits (the answer's panel has the gear and Stop).
        onMain && h('div', { className: 'ot-toolwrap ot-enter' },
          h('div', { className: 'ot-reportbar ot-toolbar', role: 'toolbar', 'aria-label': 'Tools' },
            h(Button, { variant: 'neu', size: 'sm', icon: 'list', id: 'ot-suggest-btn', 'aria-expanded': menu === 'suggest', 'aria-controls': 'ot-suggest-menu',
              className: menu === 'suggest' ? 'is-on' : null, 'aria-label': 'Suggestions', title: 'Suggestions', onClick: function () { setMenu(menu === 'suggest' ? null : 'suggest'); } }, tbLabel('Suggestions')),
            report && h(Button, { variant: 'neu', size: 'sm', icon: 'spark', 'aria-label': 'Summarize with AI', title: 'Summarize with AI', onClick: requestSummary, disabled: !!summary || reportBusy }, tbLabel('Summarize with AI')),
            h(Button, { variant: 'neu', size: 'sm', icon: 'history', id: 'ot-history-btn', 'aria-expanded': menu === 'history', 'aria-controls': 'ot-history-menu',
              className: menu === 'history' ? 'is-on' : null, 'aria-label': 'History', title: 'History', onClick: function () { setMenu(menu === 'history' ? null : 'history'); } }, tbLabel('History')),
            task && h(Button, { variant: 'neu', size: 'sm', icon: 'pulse', 'aria-pressed': showActivity, 'aria-label': 'Activity', title: 'Activity', onClick: function () { setShowActivity(!showActivity); } }, tbLabel('Activity')),
            h(Button, { variant: 'neu', size: 'sm', icon: 'repeat', 'aria-label': 'Scan', title: 'Check everything again and compare', onClick: fullScan }, tbLabel('Scan')),
            report && h(Button, { variant: 'neu', size: 'sm', icon: 'report', 'aria-label': 'Report', title: 'Report', onClick: function () { setMenu(null); setSheet('open'); } }, tbLabel('Report'))),
          menu === 'suggest' && h(OT.SuggestionList, { id: 'ot-suggest-menu', toggleId: 'ot-suggest-btn', items: starters, onClose: function () { setMenu(null); },
            onPick: function (s) { setMenu(null); runAudit(s.text, s.intent); } }),
          menu === 'history' && h(OT.HistoryList, { id: 'ot-history-menu', toggleId: 'ot-history-btn', rows: historyRows, onClose: function () { setMenu(null); },
            onOpen: function (r) { showPanel(r.id); }, onDelete: deleteAnswer })),
        onMain && agentPanels(),
        onMain && summary === 'connect' && h(GlassPanel, { className: 'ot-share ot-enter' },
          h('div', { className: 'text-headline' }, 'Summaries use an AI'),
          h('div', { className: 'ot-choice-foot' },
            h(Button, { variant: 'ghost', onClick: function () { setSummary(null); } }, 'Not now'),
            h(Button, { variant: 'primary', iconRight: 'chevron', onClick: function () { setReturnTo('main'); setWave({ speed: 0.7, words: [] }); go('login'); } }, 'Choose AI'))),
        onMain && classes && h(SharePreview, { className: 'ot-enter', agent: (curProvider && curProvider.label) || conn.label, classes: classes, size: kib(summary.preview.bytes || 0),
          onCancel: function () { setSummary(null); }, onShare: share }),
        onMain && summary === 'continue' && h(ContinuePanel, { className: 'ot-enter', client: (curProvider && curProvider.label) || conn.label, prompt: CONTINUE_PROMPT,
          onCopyFail: function () { flash('Copy failed'); }, onClose: function () { setSummary(null); } }),
        // m5 §9: each question its own panel, newest first. A follow-up opens a new panel above (same conversation).
        onMain && panels.map(function (a) {
          var mine = !!task && a.taskId === task.taskId;
          return h(OT.AnswerPanel, { key: a.id, className: 'ot-enter', item: a, onCite: openCite,
            onFollow: inApp ? function (q) { followUp(a, q); } : null, followBusy: runInProgress,
            onStop: mine && runInProgress ? cancelRun : null, onClose: function () { setOpen([a.id], false); } });
        }),
        onMain && (summary === 'loading' || summaryLines) && h(GlassPanel, { className: 'ot-summary-inline ot-enter' },
          summary === 'loading' ? h('div', { className: 'text-mono-sm ot-muted ot-turn' }, h(Logo, { size: 16, spin: true, decorative: true, className: 'ot-busy-logo' }), 'Summarizing…') :
            h('ul', null, summaryLines.map(function (l) {
              return h('li', { key: l.text, className: 'text-callout' }, l.text, ' ', l.evidenceIds.map(function (eid) {
                return h('button', { key: eid, type: 'button', className: 'ot-cite text-mono-sm', title: eid, onClick: function () { openCite(eid); } }, labels[eid] || eid);
              }));
            }))),
        // The deck, on the same page: the open card stays open until another is opened, Collapse or Escape.
        onMain && report && fu && (curCard
          ? h(OT.CardGrid, { className: cx('ot-cards', 'n-' + cards.length, openId && 'has-open'), openKey: openId }, cards.map(function (card, ci) {
            var cid = card.id, curCard = card, curId = cid, curState = cardState[cid] || {};
            var curFinding = report ? report.findings.filter(function (f) { return f.id === card.findingId; })[0] : null;
            var curSettings = cardSettings(card), curDetails = details[card.findingId];
            // "Why am I seeing this?": the finding's own evidence (finding.get) when loaded, else the card's citations.
            var curEvidence = curDetails ? curDetails.evidence.map(function (e) { return e.id; }) : card.evidenceIds || [];
            var against = (curDetails && curDetails.finding && curDetails.finding.counterevidence) || (curFinding && curFinding.counterevidence) || [];
            var curPlanEntry = curState.planId ? plans[curState.planId] : null;
            var curPlan = curPlanEntry ? curPlanEntry.plan : null, curOp = curPlanEntry ? curPlanEntry.op : null;
            var curPhase = curState.mode === 'running' || curState.mode === 'verified' || curState.mode === 'failed' ? (opPhase(curOp) || 'running') : curState.mode;
            if ((curPhase === 'preview' || curPhase === 'ineligible') && !curPlan) curPhase = null;   // never a blank card
            var cardAnswer = answers.filter(function (a) { return a.cardId === cid; }).pop() || null;
            var aiCard = curCard.origin === 'ai', aiTask = aiCard ? cardTaskId(curCard) : null;
            // An AI card cites the evidence of the task that added it: shown once fetched, or as no longer available.
            if (aiCard) curEvidence = (card.evidenceIds || []).concat(curDetails ? curEvidence : []).filter(function (x, i, a) { return a.indexOf(x) === i; });
            var aiWhy = aiCard ? curCard.why || (curCard.text !== curCard.title ? curCard.text : null) : null;
            return h(InsightCard, { key: curId, cardKey: curId, className: cid === openId ? 'is-open' : null, category: curCard.category,
              // An AI card: its title is the claim and its why the observation (the host sends text = why).
              text: aiCard ? curCard.title || curCard.text : curCard.text,
              onFocus: function () { focusCard(cid); }, onCollapse: cid === openId && canFold ? foldCards : null,
              observation: aiCard ? aiWhy : evById[(curCard.evidenceIds || [])[0]] ? evById[curCard.evidenceIds[0]].display : curCard.why || null,
              // m5 §5: the AI's note (plain text, shown as text only) and the AI tag on cards it added.
              note: typeof curCard.note === 'string' && curCard.note ? curCard.note : null, noteBy: aiLabel, ai: curCard.origin === 'ai',
              evidence: curEvidence.map(function (eid) {
                var e = evById[eid] || (curDetails && curDetails.evidence.filter(function (x) { return x.id === eid; })[0]) || (aiCard ? tev.current[eid] : null);
                if (!e) return aiCard && tevGone.current[eid] ? { id: eid, label: citeChip(eid).label, display: 'No longer available', missing: true } : null;
                return { id: eid, label: labels[eid] || (aiCard ? citeChip(eid).label : undefined), source: e.source, observedAt: observedLabel(e), display: e.display, against: against.indexOf(eid) >= 0 };
              }).filter(Boolean),
              coverage: curDetails ? (curDetails.coverage || []).map(function (c) { return { collectorId: c.collectorId, label: c.collectorId, state: COVERAGE_WORD[c.status] || c.status }; }) : null,
              onWhy: function () { if (curCard.findingId) loadDetails(curCard.findingId); if (aiCard) loadTaskEvidence(curCard.evidenceIds, aiTask); },
              onWhyToggle: function (on) { setFocusPart(cid, 'why', on); },
              onAskFocus: function () { setFocusPart(cid, 'ask', true); }, onAskBlur: function () { if (!cardAnswer || cardAnswer.text != null || cardAnswer.failedTurn) setFocusPart(cid, 'ask', false); },
              onLater: function () { leave(cid, 'later'); },
              // An AI card has one plain Hide (card-level); a finding's card offers what Ignore would hide.
              settingsUri: curSettings, ignoreChoices: aiCard ? null : ignoreChoices(curFinding ? curFinding.ruleId : '', curCard.category),
              onIgnore: function (choice) { ignoreAs(curCard, choice); }, hideLabel: aiCard ? 'Hide' : null,
              options: cardOptions(curCard, curFinding), guide: curCard.guide, preview: curPlan ? planPreview(curPlan) : null,
              ineligible: curPlan && curPlan.ineligible, progress: curPlanEntry && (curPlanEntry.progress || PROGRESS[curPlan.actionId]) || null,
              receipt: curOp && curOp.receipt ? curOp.receipt.text : null, failWord: curOp && FAIL_WORD[curOp.state],
              onUndo: curOp && curOp.undo && curOp.undo.available ? function () { undoOperation(curOp); } : null,
              onCheck: function () { checkOperation(curPlanEntry); },
              approveError: curPlanEntry && curPlanEntry.approveError, onRetry: function () { if (curState.planId) approvePlan(curState.planId, curId); },
              failManualUri: curOp && curOp.manualUri,
              onOpenSettings: function (uri) { openSettings(uri); setMode(curId, 'guide', { opened: true }); },
              onBack: function () { setMode(curId, null); unfocus(curId); },
              // Back from a preview: no decision about the card; the unused plan is dropped.
              onPreviewBack: function () { if (curState.planId && curPlan && curPlan.status === 'awaiting_user') declinePlan(curState.planId); setMode(curId, null); unfocus(curId); },
              checkAgain: !!curState.opened, recheck: curState.recheck,
              mode: curPhase, reply: cardAnswer ? (cardAnswer.failedTurn ? failWord : cardAnswer.text) : replyText(curCard.reply),
              // A report-only answer (no AI in the app): offer the chooser once, under the answer.
              onReplyConnect: !cardAnswer && curCard.reply && !inApp && !nativeLive && !isMcpKind(conn.kind) ? function () { setDetailOpen(false); setReturnTo(id); setWave({ speed: 0.7, words: [] }); go('login'); } : null,
              replyBusy: cardAnswer && cardAnswer.text == null && !cardAnswer.failedTurn ? cardAnswer.busy || 'Thinking…' : null,
              replyCites: cardAnswer && cardAnswer.text ? (cardAnswer.citations || []).map(citeChip) : null, leaving: cardLeaving === cid, askToken: askToken.cid === cid ? askToken.n : 0, askRestore: askRestore && askRestore.cid === cid ? askRestore : null,
              index: total - fu.pendingCount + 1 + ci, total: total,
              onOption: function (o) { onOption(curId, o); }, onAsk: function (t) { ask(curId, t); }, onCite: function (eid) { openCite(eid, aiTask); },
              onApprove: function () { if (curState.planId) approvePlan(curState.planId, curId); },
              onDecline: function () { if (curState.planId) declinePlan(curState.planId); leave(curId, 'decline'); },
              onNext: function () { leave(curId, 'done'); } });
          }))
          : h(ReviewComplete, { key: 'complete', className: 'ot-enter', items: reviewed,
              onUndo: function (cid) { var e = cardOp(cid); if (e && e.op.undo && e.op.undo.available) undoOperation(e.op); },
              onRestore: restoreItem })),
        null);
    }

    return h('div', { className: cx('ot-flow', p.className) },
      // The sea pauses while the full-screen report covers it.
      h(WaveBackground, { speed: wave.speed, words: wave.words, paused: !!sheet && !toLogin }),
      signedIn && h('div', { className: cx('ot-flow-top', toLogin ? 'ot-sink' : 'ot-enter') },
        h('div', { className: 'ot-brand' }, h(Logo, { size: 28 })),
        // M1 makes no AI connection, so the pill never says Connected.
        // Status only as the backend reports it (provider.list / provider.connect / pairing events).
        // A paired native client working on an active task is the live connection, whatever the chooser says.
        // §9: label · word and tone come only from connectionState (provider.list + provider.status events).
        h(ConnectionPill, { provider: pillState.label, status: working && bstate !== 'reconnecting' ? 'live' : pillState.tone,
          detail: working && bstate !== 'reconnecting' ? 'Working' : pillState.word,
          onOpen: pillKind ? function () { setDetailOpen(!detailOpen); } : null, open: detailOpen,
          // Change works anywhere except mid-scan; Back on the login returns to this exact step.
          onChange: id === 'scanning' || stage.leaving ? null : function () { setReturnTo(id); setWave({ speed: 0.7, words: [] }); go('login'); } })),
      signedIn && detailOpen && pillKind && h('div', { className: 'ot-conn-wrap' }, h(ConnectionDetail, { className: 'ot-enter', state: pillState, waitLong: waitLong,
        command: pillState.command && (showCmd || waitLong) ? regs.current[pillKind] || null : null, onCopyFail: function () { flash('Copy failed'); },
        actions: rowActions(pillState, pillKind),
        onDisconnect: pillState.disconnect ? function () { disconnectKind(pillKind); } : null,
        onChange: id === 'scanning' ? null : function () { setDetailOpen(false); setReturnTo(id); setWave({ speed: 0.7, words: [] }); go('login'); } })),
      h('div', { className: cx('ot-flow-stage', id === 'login' ? 'is-login' : null) }, body),
      peek && h(OT.EvidencePeek, { label: peek.label, ev: peek.ev, missing: peek.missing, when: peek.ev ? observedLabel(peek.ev) : null,
        onReport: peek.inReport ? function () { var id = peek.id; setPeek(null); setSheet(rows.some(function (r) { return r.id === id; }) ? id : 'open'); } : null,
        onClose: function () { setPeek(null); } }),
      sheet && h(ReportSheet, { className: toLogin ? 'ot-sink' : null, rows: rows, categories: report ? report.categories : null, fixed: fixed, when: whenLabel(report), highlight: sheet === 'open' ? null : sheet, summary: summaryLines,
        // The summary answers on the main page, under the box.
        onSummarize: summaryLines ? null : function () { setSheet(null); requestSummary(); }, onClose: function () { setSheet(null); } }));
  }

  // No render error ever leaves a black window: a short word and Reload instead (the error goes to the console).
  function ErrorBoundary(props) { React.Component.call(this, props); this.state = { error: null }; }
  ErrorBoundary.prototype = Object.create(React.Component.prototype);
  ErrorBoundary.prototype.constructor = ErrorBoundary;
  ErrorBoundary.getDerivedStateFromError = function (error) { return { error: error }; };
  ErrorBoundary.prototype.componentDidCatch = function (error, info) { if (typeof console !== 'undefined') console.error('render', error, info && info.componentStack); };
  ErrorBoundary.prototype.render = function () {
    if (!this.state.error) return this.props.children;
    return h('div', { className: 'ot-flow' }, h('div', { className: 'ot-flow-stage' }, h('div', { className: 'ot-flow-panel ot-enter' },
      h(GlassPanel, { strong: true, className: 'ot-choice ot-frost', role: 'alert' },
        h('h2', { className: 'text-title-2' }, 'Something went wrong'),
        h('div', { className: 'ot-choice-foot' }, h(Button, { variant: 'primary', icon: 'repeat', onClick: function () { window.location.reload(); } }, 'Reload'))))));
  };

  OT.AppFlow = AppFlow;
  OT.ErrorBoundary = ErrorBoundary;
})();
