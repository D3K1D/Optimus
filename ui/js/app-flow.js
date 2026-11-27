/* The whole app: login → setup → prompt → scan → report and follow-ups. Production copy of the prototype's AppFlow
   (design/design-system/components/bundle.js): same screens, transitions and timers, but every piece of data and
   every state change the contract gives the backend goes through OptTool.backend (see backend.js). */
(function () {
  var React = window.React, h = React.createElement, OT = window.OptTool, cx = OT.ui.cx, CATS = OT.ui.CATS;
  var WaveBackground = OT.WaveBackground, GlassPanel = OT.GlassPanel, Button = OT.Button, Logo = OT.Logo, LoginPanel = OT.LoginPanel,
    ChoicePanel = OT.ChoicePanel, SpecScanPanel = OT.SpecScanPanel, PromptBox = OT.PromptBox, StarterPrompt = OT.StarterPrompt,
    ScanStatus = OT.ScanStatus, InsightCard = OT.InsightCard, ReviewComplete = OT.ReviewComplete, SharePreview = OT.SharePreview,
    ReportSheet = OT.ReportSheet, ConnectionPill = OT.ConnectionPill, PairingCard = OT.PairingCard,
    ApprovalCard = OT.ApprovalCard, ScriptApproval = OT.ScriptApproval, ActivityPanel = OT.ActivityPanel, AgentAnswers = OT.AgentAnswers, ContinuePanel = OT.ContinuePanel, ConnectionDetail = OT.ConnectionDetail,
    PermissionsPanel = OT.PermissionsPanel, PermissionGrant = OT.PermissionGrant, PermissionsSummary = OT.PermissionsSummary;
  var connectionState = OT.connectionState, routeAsk = OT.routeAsk, taskEnded = OT.taskEnded, reconcileAnswers = OT.reconcileAnswers, runningTurn = OT.runningTurn,
    answerKey = OT.answerKey, answerPanels = OT.answerPanels;
  // M9 §1: the permissions record, its page, the chat line and the pairing panel (contracts/m9-ai-powers.md §1.4).
  var permDefaults = OT.permDefaults, permToggles = OT.permToggles, permRecord = OT.permRecord, permissionLine = OT.permissionLine,
    grantFromToggles = OT.grantFromToggles, permissionsGate = OT.permissionsGate, nextAfterProvider = OT.nextAfterProvider;
  var PERM_STAGE = 'permissions';
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
    'ms-settings:display-advanced', 'ms-settings:display-advancedgraphics', 'ms-settings:personalization-start', 'ms-settings:windowsupdate-history', 'ms-settings:troubleshoot'];
  var COVERAGE_WORD = { complete: 'Checked', partial: 'Partly checked', denied: 'No access', unsupported: 'Not supported', timed_out: 'Timed out',
    failed: 'Failed', skipped: 'Skipped', cancelled: 'Stopped' };
  function notSupported(err) { return err && err.code === -32601; }

  /* ---------- M3: providers, tasks, grants (contracts/m3-agent.md) ---------- */
  // Picker tile → provider kind for the CLI-backed AI (the picker's ids and the stored values: connection-state.js).
  var NATIVE_KIND = { claude: 'claude_code_mcp' };
  var CLIENT_KIND = { claude_code: 'claude_code_mcp' };
  var CLIENT_OF_KIND = { claude_code_mcp: 'claude_code' };
  var READY_ROWS = { paired: 1, active: 1, client_closed: 1 };
  // Users see "Claude"; the program's own name only where they act on it (install, update, its own window, pairing).
  var ORIGIN_LABEL = { in_app: 'In app', claude_code: 'Claude' };
  var WINDOW_NAME = 'Claude Code';
  function tileOfKind(kind) { return OT.kindTile(kind); }
  function aiName(kind, label) { return OT.aiName(kind, label); }
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
  var FIX = window.OptTool.fixPlan;
  // Operation states that ended without the change (the approval card shows which step and why).
  var FAILED_OP = { failed: 1, indeterminate: 1, rolled_back_failed: 1 };
  function opPhase(op) {
    if (!op) return null;
    if (op.state === 'verified' || op.state === 'completed') return 'verified';
    if (FAILED_OP[op.state] || FIX.isTerminal(op.state)) return 'failed';
    return 'running';
  }
  /* ---------- One-click connect (contracts/onboarding.md §2–3) ---------- */
  var CLIENT_OF_TILE = { claude: 'claude_code' };
  // Not installed: the official PowerShell installer (shell.installClient); its command can be copied if it can't start.
  var INSTALL_CMD = { claude_code: 'irm https://claude.ai/install.ps1 | iex' };
  var INSTALL_POLL_MS = 4000, INSTALL_POLL_FOR_MS = 5 * 60 * 1000;
  // shell.requestApiKey errors (m3 §9 codes, or the shell's own): a short word for the flash.
  var KEY_FAIL = { auth: 'Key not accepted', quota: 'No credit left', network: 'Offline', offline: 'Offline', backend_unavailable: 'Offline',
    unavailable: 'Busy · try again', not_eligible: 'Not available', storage: 'Key not saved', busy: 'Already open' };
  // What a CLI's sign-in pays with (shell.clientStatus auth).
  var BILLING = { claude_code: { subscription: 'Uses your Claude plan', api_key: 'Billed to your Anthropic API key' } };
  // card.reply is {question, text, evidenceIds} (followup.schema.json); older fixture state saved a bare string.
  function replyText(r) { return r == null ? null : typeof r === 'string' ? r : typeof r.text === 'string' ? r.text : null; }
  var CONTINUE_PROMPT = 'Use optimization-tool to review my latest report and explain what matters most.';
  function isApiKind(k) { return k === 'anthropic_api' || k === 'openai_api' || k === 'chatgpt_plan'; }
  function isMcpKind(k) { return k === 'claude_code_mcp'; }
  function kindForProfile(pr) {
    if (!pr) return null;
    if (pr.kind) return pr.kind;   // the exact AI chosen; older profiles fall back to the value (and the label for API keys)
    if (pr.value === 'api') return /openai/i.test(pr.label || '') ? 'openai_api' : 'anthropic_api';
    if (pr.value === 'chatgpt') return 'chatgpt_plan';
    return pr.value === 'claude-code' ? 'claude_code_mcp' : null;
  }
  function kib(b) { return b < 1024 ? b + ' B' : (b / 1024).toFixed(1) + ' KiB'; }
  // A tool call in plain words (design AgentActivity wording rule, fix-plan.js): 'Measuring CPU use…', 'Read the report'.
  function toolWords(t) {
    var a = t.arguments || {};
    return FIX.activityWords(t.tool, t.status !== 'running', t.label, 0, a.diagnosticId).title;
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
        isNew: fresh.indexOf(f.fingerprint) >= 0, fid: f.id });
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
    var reportRef = React.useRef(null); reportRef.current = report;
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
    var ai = React.useState({ value: 'local', label: 'Local only' }), conn = ai[0], setConnState = ai[1];
    // The chosen AI, one source of truth: set together with the render state, read by every callback that can run after
    // a change of AI (promise replies, events, timers), never the `conn` an older render's closure holds.
    var connRef = React.useRef(conn);
    function setConn(c) { connRef.current = c; setConnState(c); }
    var rt = React.useState(null), returnTo = rt[0], setReturnTo = rt[1];
    var bs = React.useState(backend.state()), bstate = bs[0], setBstate = bs[1];
    var bd = React.useState(backend.detail ? backend.detail() : null), bdetail = bd[0], setBdetail = bd[1];   // the shell's detail code
    var re = React.useState(null), restore = re[0], setRestore = re[1];       // draft put back in the main box
    var ar = React.useState(null), askRestore = ar[0], setAskRestore = ar[1]; // draft put back in the card box
    var fl = React.useState(false), failed = fl[0], setFailed = fl[1];
    var scu = React.useState(null), scanCurrent = scu[0], setScanCurrent = scu[1];   // Scan.progress.current (sampler)
    var fx = React.useState(null), fixed = fx[0], setFixed = fx[1];                   // resolved findings vs the base report
    var dt = React.useState({}), details = dt[0], setDetails = dt[1];                 // finding id → finding.get result
    var pv = React.useState({}), providers = pv[0], setProviders = pv[1];             // kind → provider.list entry
    var cn = React.useState(null), connect = cn[0], setConnect = cn[1];               // connection in progress on the chooser
    var pg = React.useState(null), pairing = pg[0], setPairing = pg[1];               // pending agent.pairing
    var gs = React.useState(null), grant = gs[0], setGrant = gs[1];                   // pairing panel: { kind, pairingId?, busy? }
    var rsr = React.useState(null), research = rsr[0], setResearch = rsr[1];      // M9 §6: research.get for the current report (null: none or not offered)
    var researchRef = React.useRef(null); researchRef.current = research;
    var pms = React.useState(null), perm = pms[0], setPermState = pms[1];            // ai.getPermissions: { permissions, available } (null: not offered)
    var permRef = React.useRef(null);
    function setPerm(r) { permRef.current = r; setPermState(r); return r; }
    var pmb = React.useState(false), permBusy = pmb[0], setPermBusy = pmb[1];       // the page's choice is being saved
    var permLater = React.useRef(false);       // Back on the page: not asked again in this window until the AI changes
    var permAfter = React.useRef(null);        // a newly connected kind whose first look waits for the page
    var permFocus = React.useRef(false);       // the page was opened from the pill or Change: its title takes focus
    var permSeen = React.useRef(null);         // the permissions revision the open page (or pairing panel) started from
    var grantSeen = React.useRef(null);
    var ckCards = React.useRef({ reportId: null, byId: {} });   // check-up cards seen in the deck, by id (for the report)
    var ckSeen = React.useRef(null);                            // check-up card ids already seen (null: not read yet)
    var ckt = React.useState(0), setCkTick = ckt[1];
    // The New tag (design isNew): ids there when the check-up was first read here (never new), and new cards that have
    // been on screen and left it again (their tag is gone). ckFront: the new cards moved to the front of the deck (they
    // stay there for the report, so nothing jumps back once seen).
    var ckBase = React.useRef({}), ckOnScreen = React.useRef({}), ckTagGone = React.useRef({}), ckFront = React.useRef([]);
    // The card whose preview Back just closed: its Preview change gets focus after the render.
    var backFocus = React.useRef(null);
    var starting = React.useRef(null);         // agent.start on its way: { kind } (one at a time per AI)
    var choiceSave = React.useRef(null);       // the chosen AI's onboarding.save on its way (settles either way); starts wait for it
    var tk = React.useState(null), task = tk[0], setTask = tk[1];
    var liveTask = React.useRef(null); liveTask.current = task;   // the current task for callbacks (attachTask sets it at once)
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

    // M6a: the benchmark panel's state (bench.js); flash is defined below and only called later.
    // Report a problem (report-problem.js): the logo's menu, the preview and the prompt after a crash or a failed operation.
    var problems = OT.useProblemReport(backend, { flash: function (w) { flash(w); }, version: OT.appVersion || null }), PF = OT.problemReport.failed;
    var bench = OT.useBench(backend, { flash: function (w) { flash(w); }, onFailed: function (x) { problems.raise('benchmark', 'bench:' + x.runId); } });
    var timers = React.useRef([]);
    var onbRef = React.useRef(null), fuRef = React.useRef(null), stageRef = React.useRef(stage);
    var scanRef = React.useRef(null);          // { scanId, stream, cursor, handled }
    var invRef = React.useRef(null);           // { inventoryId, stream, finished }
    var buffer = React.useRef([]);             // events that arrived before their job's ID was known
    var fuChain = React.useRef(Promise.resolve()), busy = React.useRef(false);
    var cardFinding = React.useRef({});        // card id → finding id, for restoring a disposition from the review list
    var taskRef = React.useRef(null);          // { taskId, stream }
    var taskLabels = React.useRef(OT.citeRegistry()); // task evidence id → short label by kind (CPU, Ports 2 …; m3 until known)
    var pendingSummary = React.useRef(false);
    var cls2 = React.useState({}), clients = cls2[0], setClients = cls2[1];      // tile → shell.clientStatus result (+ busy / failed)
    function putClient(tile, patch) { setClients(function (m) { var n = Object.assign({}, m); n[tile] = Object.assign({}, m[tile] || {}, patch); return n; }); }
    var ct = React.useState({}), catalog = ct[0], setCatalog = ct[1];             // actionId → catalog entry
    var pls = React.useState({}), plans = pls[0], setPlans = pls[1];            // planId → { plan, op, progress, cardId, kind }
    // m9 §17.2: administrator-check requests as agent.checks last showed them (requestId → request view, + hidden once the
    // user asked something new beside the chat); a card's latest request (cardId → requestId), for "Checks skipped" in working.
    var ckq = React.useState({}), checkReqs = ckq[0], setCheckReqs = ckq[1];
    var checkReqsRef = React.useRef(checkReqs); checkReqsRef.current = checkReqs;
    var cardChecks = React.useRef({});
    // Review 2: per request, how many agent.checks events arrived (a reply to a call that began before the latest event is
    // older than it and is ignored), and the calls in flight (the request's buttons do nothing meanwhile).
    var ckEvents = React.useRef({}), ckBusy = React.useRef({});
    // Review 1: the open requests are read again (checks.list) at start and after a reconnect.
    // Review 3: the latest card.fix event per card ({seq, fix, reportId}); a deck snapshot asked for before it keeps it.
    var fixEvents = React.useRef({}), fixSeq = React.useRef(0), deckGen = React.useRef(0), deckAccepted = React.useRef({});
    var fixGen = React.useRef({});   // re-check 5: per card, the count of its latest card.fix event (never retired)
    var ptk = React.useState(0), planTick = ptk[0], setPlanTick = ptk[1];   // review 7: retry card plans not read yet
    var rck = React.useState({}), rechecks = rck[0], setRechecks = rck[1];  // design v82: needs you's Check again (cardId → {checking, recheck})
    var switchedTasks = React.useRef({});      // tasks the app stopped because the user chose another AI
    var planLoads = React.useRef({});          // planId → action.get on its way (a card's fix plan not seen yet)
    var nt = React.useState(0), setTick = nt[1];   // re-renders once a second while a checks request counts down
    // The plans as last set, at once (plansRef): several operation.state events, or an approval's reply, before the next render
    // build on each other instead of on the last render's copy (a fix's step states were lost that way).
    function putPlan(planId, patch) {
      var m = plansRef.current || {}, n = Object.assign({}, m);
      n[planId] = Object.assign({}, m[planId] || {}, patch);
      plansRef.current = n; setPlans(n);
    }
    // Show script on a script that was closed: it opens again, and is found once it is rendered.
    var pendingShow = React.useRef(null);
    React.useEffect(function () { var pid = pendingShow.current; if (pid) { pendingShow.current = null; showScript(pid); } });
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
    // A retired AI (Codex) whose user resumed into a running scan or inventory: after that view, the picker (retiredRedirect).
    var retiredHold = React.useRef(null);
    React.useEffect(function () {
      var r = OT.retiredRedirect(retiredHold.current, stage);
      if (!r) return;
      retiredHold.current = null;
      if (r.clear) return;
      setReturnTo(r.returnTo); setWave({ speed: 0.7, words: [] }); go('login');
    }, [stage.id, stage.leaving]);
    function later(fn, ms) { var t = setTimeout(fn, ms); timers.current.push(t); return t; }
    React.useEffect(function () { return function () { timers.current.forEach(clearTimeout); }; }, []);
    // M9 §6: the current report's research (labels, run state); re-read when the report changes.
    var reportKey = report ? report.reportId : null;
    React.useEffect(function () { setResearch(null); loadResearch(reportKey); }, [reportKey]);

    function setOnb(o) { onbRef.current = o; setOnbState(o); return o; }
    // A deck snapshot from the host (re-check 4): since = the card.fix event count when it was asked for. Events after it
    // still win; events up to it are superseded by the snapshot and retired. Every asynchronous deck request captures it
    // (deckSince) before it is sent; a local patch (patchCardFix) is not a snapshot (setDeck).
    // A token from deckSince also carries the request's generation: a reply older than the newest accepted one for its
    // report is dropped (a newer snapshot already retired the events it would have needed to stay current).
    function setFu(f, since) {
      var tok = since && typeof since === 'object' ? since : null;
      var s = tok ? tok.seq : typeof since === 'number' ? since : -1;
      if (tok && f && f.reportId) {
        if (tok.gen < (deckAccepted.current[f.reportId] || 0)) return fuRef.current;
        deckAccepted.current[f.reportId] = tok.gen;
      }
      f = overlayFix(f, s);
      Object.keys(fixEvents.current).forEach(function (k) { if (fixEvents.current[k].seq <= s) delete fixEvents.current[k]; });
      return setDeck(f);
    }
    function deckSince() { return { seq: fixSeq.current, gen: ++deckGen.current }; }
    function setDeck(f) {
      fuRef.current = f; setFuState(f);
      if (f && f.card) cardFinding.current[f.card.id] = f.card.findingId;
      if (f) (f.decisions || []).forEach(function (d) { if (d.findingId) cardFinding.current[d.cardId] = d.findingId; });
      return f;
    }
    function overlayFix(f, since) {
      if (!f) return f;
      var ev = fixEvents.current;
      var put = function (c) { var e = c && ev[c.id]; return e && e.seq > since && (!e.reportId || e.reportId === f.reportId) ? Object.assign({}, c, { fix: e.fix }) : c; };
      return Object.assign({}, f, { card: put(f.card), upcoming: (f.upcoming || []).map(put) });
    }
    function flash(msg) { setToast(msg); later(function () { setToast(null); }, 2000); }

    // Everything on screen sinks into the sea first; the next step rises once it has gone.
    function go(next) {
      setStage(function (s) { return { id: s.id, leaving: true, next: next }; });
      later(function () { if (next === 'login' || next === PERM_STAGE) setSheet(null); setStage({ id: next, leaving: false }); }, FADE);
    }
    /* ---------- M9 §1: permissions ---------- */
    // An older host without ai.getPermissions: no page, no line (null never blocks routing).
    function loadPermissions() {
      return backend.call('ai.getPermissions').then(setPerm, function (err) { if (notSupported(err)) setPerm(null); return permRef.current; });
    }
    /* ---------- M9 §6: researched reports ---------- */
    // An older host without research.get shows no labels and no chip.
    function loadResearch(reportId) {
      if (!reportId) { setResearch(null); return Promise.resolve(null); }
      return backend.call('research.get', { reportId: reportId }).then(function (r) {
        if (reportRef.current && reportRef.current.reportId === reportId) setResearch(Object.assign({ reportId: reportId }, r));
        return r;
      }, function () { return null; });
    }
    function onResearchState(pl) {
      var cur = reportRef.current;
      if (!cur || pl.reportId !== cur.reportId) return;
      var was = researchRef.current;
      setResearch(function (r) { return OT.researchFromState(r, pl); });
      loadResearch(pl.reportId);
      // §6.8: each checked area may add or replace check-up cards (after the report's own cards).
      if (pl.phase === 'checkup') { refreshDeck(); return; }
      // §6.4: the trial's outcome changes what the provider offers (web yes / no).
      if (was && was.trial && !pl.trial) loadPermissions();
    }
    // A source opens through the shell by its id; the page never holds the URL.
    // A second click within 1 s is refused by the shell (rate_limited): the first one is already opening, so say nothing.
    function openSource(sourceId) {
      backend.call('shell.openSource', { sourceId: sourceId }).then(function (r) { if (!r || r.status !== 'opened') flash('Not available'); },
        function (e) { if (!(e && e.code === -32013 && e.data && e.data.reason === 'rate_limited')) flash('Not available'); });
    }
    // Where to land instead of `next`: the page first when an AI is selected and its permissions were never set.
    function gateTo(kind, next) {
      if (permLater.current) return { stage: next, returnTo: null };
      return permissionsGate({ kind: kind, permissions: permRef.current && permRef.current.permissions, next: next });
    }
    // focus: opened by the user (the pill's Permissions, the summary's Change): the page's title takes focus.
    function openPermissions(from, focus) {
      setDetailOpen(false); setReturnTo(from); setWave({ speed: 0.7, words: [] }); go(PERM_STAGE);
      permFocus.current = !!focus;
      // The revision the page opened with is the one read now (an older copy in memory must not read as "Updated").
      permSeen.current = 'loading';
      loadPermissions().then(function (r) { permSeen.current = r && r.permissions ? r.permissions.revision : null; });
    }
    // Back and Continue both return to where the page was opened from (or the stage the user resumes).
    function leavePermissions(back) {
      var r = returnTo || 'main', kind = permAfter.current;
      if (back) permLater.current = true;
      permAfter.current = null;
      setReturnTo(null);
      go(r);
      if (kind && r === 'main') offerFirstScan(kind);
    }
    function savePermissions(v) {
      var cur = permRef.current;
      if (!cur) { leavePermissions(false); return; }
      if (permBusy) return;
      var rec = permRecord(v, cur.permissions);
      setPermBusy(true);
      backend.call('ai.setPermissions', { idempotencyKey: uuid(), revision: cur.permissions.revision, permissions: rec }).then(function (r) {
        setPermBusy(false); setPerm(r); permLater.current = false; leavePermissions(false);
      }, function (err) {
        setPermBusy(false);
        // Saved elsewhere first: show the newer record (the page re-fills); the user decides again.
        var c = err && err.code === -32002 && err.data && err.data.current;
        if (c) { setPerm(c.permissions ? c : { permissions: c, available: cur.available }); return; }   // the page shows "Updated"
        if (notSupported(err)) { setPerm(null); leavePermissions(false); return; }
        flash('Not saved');
      });
    }

    /* ---------- onboarding ---------- */
    function adoptProfile(o) {
      var pr = o.profile;
      setUsage(profileList(pr.uses, pr.customUse));
      setGoals(profileList(pr.goals, pr.customGoal));
      // A saved AI that no longer exists (retired): nothing selected; the picker comes first (resumeFrom).
      if (OT.retiredProvider(pr.provider)) setConn({ value: 'local', label: 'Local only', kind: null, retired: true });
      else if (pr.provider) setConn({ value: pr.provider.value, label: aiName(kindForProfile(pr.provider), pr.provider.label), kind: kindForProfile(pr.provider) });
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
      liveTask.current = t; setTask(t); setActivity(activityFromHistory(activityList));
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
    // The chosen AI's save (c: {value, label, kind}): the host only starts and asks the AI saved as chosen, so every
    // in-app start waits for this save. One at a time, in order, so a quick back-and-forth ends on the last choice.
    function saveChoice(c, step) {
      var provider = { value: c.value, label: c.label };
      if (c.kind) provider.kind = c.kind;
      var patch = { provider: provider };
      if (step) patch.step = step;
      var noop = function () {};
      var p = (choiceSave.current || Promise.resolve()).then(function () { return saveOnb(patch); });
      var mine = choiceSave.current = p.then(noop, noop);
      mine.then(function () { if (choiceSave.current === mine) choiceSave.current = null; });
      return p;
    }
    function loadStarters() {
      var o = onbRef.current;
      return backend.call('starter.list', { profileRevision: o ? o.revision : 0 }).then(function (r) { setStarters(r.starters || []); }, function () { setStarters([]); });
    }
    function loadReport(reportId) {
      var since = deckSince();
      return Promise.all([backend.call('report.get', { reportId: reportId }), backend.call('followup.get', { reportId: reportId })]).then(function (res) {
        var r = res[0];
        setReport(r); setFu(res[1], since); setDetails({}); setFixed(null);
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
      // Claude is usable only once the shell has checked Claude Code (installed, signed in): check at start.
      checkClient('claude');
      // Receipts survive a reload: re-read recent operations and their plans.
      backend.call('operation.list', { limit: 50 }).then(function (r) {
        (r.operations || []).forEach(function (op) {
          backend.call('action.get', { planId: op.planId }).then(function (plan) { putPlan(op.planId, { plan: plan, op: op, kind: plan.undoOf ? 'undo-done' : 'card' }); }, function () {});
        });
      }, function () {});
      loadChecks();
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
        // M9: the permissions record first, so the page can come before the stage the user resumes.
        return loadPermissions().then(function () { return resumeFrom(o); }).catch(function (err) {
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
        // M9 §1.4: an AI that never had its permissions set shows the page before the stage the user resumes.
        var kind = kindForProfile(o.profile && o.profile.provider);
        // A retired AI: its users land on the picker (nothing selected, no message), then go on where they were.
        var retired = OT.retiredLanding(o);
        retiredHold.current = retired;   // cleared on the picker; kept while a running scan or inventory is shown first
        var land = function (next) {
          if (retired) { setReturnTo(o.step === 'complete' ? next : retired.returnTo); setStage({ id: 'login', leaving: false }); return; }
          var g = next === 'login' ? { stage: next } : gateTo(kind, next);
          if (g.stage === PERM_STAGE) setReturnTo(next);
          setStage({ id: g.stage, leaving: false });
        };
        if (o.step !== 'complete') {
          if (o.step === 'specs' && o.resume && o.resume.inventoryId) return resumeInventory(o.resume.inventoryId);
          land(STEP_STAGE[o.step] || 'login'); return;
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
            land('main');
          });
          return loadStarters().then(function () { land('main'); });
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
      // Decided: an approved pairing leaves the chooser's connect step (the permissions panel follows); any other outcome waits again.
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
        // m9 §17.3: a fix ask (or its continuation after administrator checks): the card's FixPanel shows the work, not a reply.
        ensureAnswer(pl.turnId, ev.seq, { q: pl.text || null, cardId: pl.cardId || null, text: null, busy: 'Thinking…', failedTurn: false, fix: pl.intent === 'fix' || !!pl.checks });
        setAnswers(function (list) {
          var i = answerIndex(list, pl.turnId, tid); if (i < 0) return list;
          var n = list.slice(); n[i] = Object.assign({}, n[i], { turnId: pl.turnId, q: n[i].q || pl.text || null }); return n;
        });
      }
      else if (ev.type === 'agent.task') {
        // M9 §1.2: a save that only widens updates the grant in place (the event carries it).
        setTask(function (t) { return t && t.taskId === tid ? Object.assign({}, t, { status: pl.status, used: pl.used || t.used, grant: pl.grant || t.grant }) : t; });
        if (pl.status && pl.status !== 'active') setLiveTurn(null);
        if (PF.aiTask(pl)) problems.raise('ai', 'ai:' + tid);
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
        var mtext = { text: stripCites(pl.text), citations: pl.citations || [], followUps: pl.followUps || [], busy: null, failedTurn: false,
          warnings: pl.warnings || [], suggest: pl.suggest || null,   // M6a §8: voltage note, Run benchmark
          sources: (pl.sources || []).slice(0, 8) };                   // M9 §7: that turn's web sources (UI-only)
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
        else if (pl.status === 'failed') patch(pl.turnId, { failedTurn: true, failCode: pl.code || null, busy: null });
        if (PF.aiTurn(pl)) problems.raise('ai', 'ai:' + tid);
      }
    }
    function refreshDeck() {
      var f = fuRef.current; if (!f) return;
      var run = function () {
        var since = deckSince();
        return backend.call('followup.get', { reportId: f.reportId }).then(function (cur) {
          if (fuRef.current && fuRef.current.reportId === cur.reportId) setFu(cur, since);
          setPlanTick(function (n) { return n + 1; });   // review 7: card plans not read yet are tried again
        }, function () {});
      };
      fuChain.current = fuChain.current.then(run, run);
    }
    /* ---------- m9 §17 Fix for me and administrator checks ---------- */
    // card.fix {cardId, reportId, fix} into the deck the user sees (followup.get carries it on load).
    // The event is recorded first (review 3), so a deck snapshot asked for earlier can't bring the older fix back, and two
    // events before a render both apply (fuRef is the latest deck at once).
    function patchCardFix(cardId, reportId, fix, fromReply) {
      if (!cardId) return;
      if (!fromReply) { fixEvents.current[cardId] = { seq: ++fixSeq.current, fix: fix, reportId: reportId || null }; fixGen.current[cardId] = fixSeq.current; }
      var f = fuRef.current; if (!f || (reportId && f.reportId !== reportId)) return;
      if (!(f.card && f.card.id === cardId) && !(f.upcoming || []).some(function (c) { return c && c.id === cardId; })) return;
      var put = function (c) { return c && c.id === cardId ? Object.assign({}, c, { fix: fix }) : c; };
      setDeck(overlayFix(Object.assign({}, f, { card: put(f.card), upcoming: (f.upcoming || []).map(put) }), -1));
    }
    // agent.checks: events always win (review 2); an older revision never replaces a newer one.
    function onChecksEvent(q) {
      if (!q || !q.requestId) return;
      ckEvents.current[q.requestId] = (ckEvents.current[q.requestId] || 0) + 1;
      putCheck(q);
    }
    // checks.list: the open requests as the host has them now. A request with an agent.checks event since the call began keeps
    // what the event said; an older host without the method shows what events bring.
    function loadChecks() {
      var seen = Object.assign({}, ckEvents.current);
      backend.call('checks.list', {}).then(function (r) {
        var listed = {};
        ((r && r.requests) || []).forEach(function (q) { if (q && q.requestId) listed[q.requestId] = true; if (q && q.requestId && (ckEvents.current[q.requestId] || 0) === (seen[q.requestId] || 0)) putCheck(q); });
        // Re-check 1: a request open here that the host no longer lists ended while we were away: it goes (its card is read
        // again, which says how it went on), unless an event for it came after the call began.
        var gone = Object.keys(checkReqsRef.current).filter(function (id) {
          return !listed[id] && FIX.checksOpen(checkReqsRef.current[id]) && (ckEvents.current[id] || 0) === (seen[id] || 0);
        });
        if (!gone.length) return;
        setCheckReqs(function (m) { var n = Object.assign({}, m); gone.forEach(function (id) { if (FIX.checksOpen(n[id])) delete n[id]; }); return n; });
        refreshDeck();
      }, function () {});
    }
    function putCheck(q) {
      if (!q || !q.requestId) return;
      if (q.cardId) cardChecks.current[q.cardId] = q.requestId;
      setCheckReqs(function (m) {
        var old = m[q.requestId];
        if (old && typeof old.revision === 'number' && typeof q.revision === 'number' && q.revision < old.revision) return m;
        var n = Object.assign({}, m); n[q.requestId] = Object.assign({}, q, { hidden: !!(old && old.hidden), switchedAi: !!switchedTasks.current[q.taskId] }); return n;
      });
    }
    // A call's reply (or its error's request view) counts only when no agent.checks event for the request came after the
    // call began; running and an end state share a revision, so the revision alone can't tell.
    function checksCall(requestId, method, params, onError) {
      if (ckBusy.current[requestId]) return;
      ckBusy.current[requestId] = true;
      var seen = ckEvents.current[requestId] || 0;
      var fresh = function () { return (ckEvents.current[requestId] || 0) === seen; };
      backend.call(method, params).then(function (r) { ckBusy.current[requestId] = false; if (fresh()) putCheck(r); }, function (err) {
        ckBusy.current[requestId] = false;
        onError(err, fresh());
      });
    }
    // Run checks → checks.approve with the request's revision; Windows asks next. A stale revision shows the request as it
    // is now (the user clicks again); in the cooldown the request waits with its retryAt.
    function approveChecks(requestId) {
      var q = checkReqsRef.current[requestId]; if (!q || !(q.checks || []).length) return;
      checksCall(requestId, 'checks.approve', { idempotencyKey: uuid(), requestId: requestId, revision: q.revision }, function (err, fresh) {
        var d = err && err.data;
        if (err && err.code === -32002 && d && d.current) { if (fresh) putCheck(d.current); }
        else if (err && err.code === -32013 && d && d.reason === 'cooldown') { if (fresh) putCheck(Object.assign({}, checkReqsRef.current[requestId] || q, { retryAt: d.retryAt || null })); }
        else if (!(err && err.code === -32013)) flash(notSupported(err) ? 'Not available' : 'Not saved');
      });
    }
    function declineChecks(requestId) {
      if (!checkReqsRef.current[requestId]) return;
      checksCall(requestId, 'checks.decline', { requestId: requestId }, function (err) { if (!(err && err.code === -32013)) flash(notSupported(err) ? 'Not available' : 'Not saved'); });
    }
    // Fix for me (and its Try again / Fix for me retries, and "Change something…" with revise): agent.ask with intent fix
    // in the app's chat task (a task is started for the chosen AI when there is none).
    function fixAsk(cid, text, revise) {
      var extra = { intent: 'fix' };
      if (revise) extra.revise = revise;
      agentAsk(text || '', cid, null, extra);
    }
    // [Close] on an ended fix: fix.dismiss; the card is back to normal.
    function dismissFix(cid, done) {
      // Re-check 5: the reply counts only when no card.fix event for the card came after the dismissal began.
      var f = fuRef.current, gen = fixGen.current[cid] || 0;
      backend.call('fix.dismiss', { cardId: cid }).then(function (r) {
        if ((fixGen.current[cid] || 0) === gen) patchCardFix(cid, f && f.reportId, r ? r.fix || null : null, true);
        if (done) done(true);
      }, function () { if (done) done(false); refreshDeck(); });
    }
    // The plan the card's current fix points to (the deck as the user sees it), or null.
    function cardFixPlan(cid) {
      var f = fuRef.current; if (!f || !cid) return null;
      var c = [f.card].concat(f.upcoming || []).find(function (x) { return x && x.id === cid; });
      return c && c.fix ? c.fix.planId || null : null;
    }
    // needs you, on a report card: Check again re-reads the card's finding (the card closes when the issue is gone).
    // Design v82: Checking… while it runs; still there → the line under the note; gone → the green line for about 2 seconds,
    // then the deck is read again (the host closed the card).
    function recheckCard(cid, findingId) {
      var put = function (v) { setRechecks(function (m) { var n = Object.assign({}, m); if (v) n[cid] = v; else delete n[cid]; return n; }); };
      put({ checking: true, recheck: (rechecks[cid] || {}).recheck || null });
      backend.call('manual.recheck', { findingId: findingId }).then(function (r) {
        var word = FIX.recheckWord(r && r.status);
        put({ checking: false, recheck: word });
        if (word === 'gone') later(function () { put(null); refreshDeck(); }, 2000);
      }, function () { put(null); flash('Not available'); });
    }
    // Design v82: Show script brings the script waiting beside the chat into view and focuses it.
    function showScript(planId) {
      var ent = plansRef.current[planId];
      if (ent && ent.closed) { putPlan(planId, { closed: false }); pendingShow.current = planId; return; }
      var el = document.querySelector('.ot-plan-' + planId);
      if (!el) return;
      if (el.scrollIntoView) el.scrollIntoView({ block: 'start', behavior: reduce ? 'auto' : 'smooth' });
      // The script's text first (reading it is the first thing to do there), never Decline or Approve.
      var f = el.querySelector('[tabindex="0"]');
      if (f && f.focus) f.focus({ preventScroll: true });
    }
    // Approve… in a card: with a step switched off, action.narrow first, then the native approval of the new plan.
    function approveInCard(planId, kept) {
      var e = plansRef.current[planId] || {}, keep = FIX.narrowKeep(kept, (e.plan && e.plan.steps) || []);
      if (e.approving) return;   // review 6: one submit at a time
      if (!keep) { approvePlan(planId, null); return; }
      putPlan(planId, { approving: true });
      backend.call('action.narrow', { idempotencyKey: uuid(), planId: planId, keep: keep }).then(function (np) {
        putPlan(planId, { approving: false });
        putPlan(np.planId, { plan: np, op: null, kind: 'agent' });
        approvePlan(np.planId, null);
      }, function () {
        // Review 8: any refusal (drifted, no longer awaiting, cancelled …): the plan and the card are read again and show
        // what is true now.
        putPlan(planId, { approving: false });
        backend.call('action.get', { planId: planId }).then(function (plan) { putPlan(planId, { plan: plan, op: plan.operation || null }); }, function () {});
        refreshDeck();
      });
    }
    function handleEvent(ev) {
      var s = scanRef.current, i = invRef.current, tr = taskRef.current;
      if (ev.type === 'agent.pairing') { onPairing(ev.payload || {}); return true; }
      // M9 §1.2: saved here or in another window; `available` stays as last read.
      if (ev.type === 'ai.permissions') {
        var np = (ev.payload || {}).permissions;
        if (np) setPerm({ permissions: np, available: (permRef.current && permRef.current.available) || { tools: true, web: 'unknown', scripts: false } });
        return true;
      }
      // m9 §17.3: a card's fix changed (stream user): the card shows it at once.
      if (ev.type === 'card.fix') { var cf = ev.payload || {}; patchCardFix(cf.cardId, cf.reportId, cf.fix || null); return true; }
      // m9 §17.2: an administrator-checks request changed (stream user).
      if (ev.type === 'agent.checks') { onChecksEvent(ev.payload || {}); return true; }
      // M9 §6.2: research progress for a report; the current report's labels are re-read as they land.
      if (ev.type === 'research.state') { onResearchState(ev.payload || {}); return true; }
      if (typeof ev.stream === 'string' && ev.stream.indexOf('bench:') === 0) { bench.onEvent(ev); return true; }
      if (ev.type === 'action.plan') { onPlanEvent(ev.payload || {}); return true; }
      if (ev.type === 'operation.state') {
        var os = ev.payload || {};
        // M9 §4: a fix's step states merge into its operation; once it ends it is re-read for receipts and Undo.
        var prevOp = (plansRef.current[os.planId] || {}).op || {}, osteps = prevOp.steps;
        if (os.step) {
          osteps = (osteps || []).filter(function (x) { return x.n !== os.step.n; }).concat([Object.assign({}, (osteps || []).filter(function (x) { return x.n === os.step.n; })[0], os.step)])
            .sort(function (a, b) { return a.n - b.n; });
        }
        putPlan(os.planId, { op: Object.assign({}, prevOp, { operationId: os.operationId, planId: os.planId, state: os.state, receipt: os.receipt || null, manualUri: os.manualUri || null, steps: osteps }),
          progress: os.progress || null });
        var fixDone = !os.step && !!osteps && FIX.isTerminal(os.state);
        // M9 §5.4: a script's end state is re-read too (receipt, what was sent to the AI); it never becomes verified.
        var scriptDone = ((plansRef.current[os.planId] || {}).plan || {}).kind === 'script' && FIX.scriptEnded(os.state);
        // A single change that ended otherwise is re-read too: the event has no error (why it failed, or that it was stopped).
        var opKind = ((plansRef.current[os.planId] || {}).plan || {}).kind;
        var singleDone = !os.step && opKind !== 'fix' && opKind !== 'script' && FIX.isTerminal(os.state);
        if (os.state === 'verified' || fixDone || scriptDone || singleDone) backend.call('operation.get', { operationId: os.operationId }).then(function (op) {
          putPlan(os.planId, { op: op, stopping: false });
          // A change that ended in an error (not a stop, not an action's own reported result): the Report it prompt.
          var fp = (plansRef.current[os.planId] || {}).plan || {};
          if (PF.fix(op, fp.kind) && !FIX.isReported(fp.actionId, op) && !FIX.failKey(fp.actionId, op)) problems.raise('fix', 'fix:' + op.operationId);
          // An undo of fix steps: the original's steps now read Undone.
          var up = (plansRef.current[os.planId] || {}).plan;
          if (up && up.kind === 'fix' && up.undoOf) refreshOperation(up.undoOf);
        }, function () {});
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
      var offS = backend.onState(function (state, info) {
        setBstate(state); setBdetail(info ? info.detail : null);
        if (info && info.resumed && resyncRef.current) resyncRef.current();
      });
      boot();
      return function () { offE(); offS(); };
    }, []);

    // After the shell reconnects to the host: re-read snapshots instead of trusting missed events.
    resyncRef.current = function () {
      backend.call('onboarding.get').then(setOnb).catch(function () {});
      bench.resync();
      if (reportRef.current) loadResearch(reportRef.current.reportId);
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
      loadChecks();
      var since = deckSince();
      if (f) backend.call('followup.get', { reportId: f.reportId }).then(function (cur) { setFu(cur, since); setPlanTick(function (n) { return n + 1; }); }).catch(function () {});
      loadProviders();
      // A restarted host forgot which CLI the shell found: check both again, whatever the last answer said.
      checkClient('claude');
      // A change that was applying: its final state may have been missed.
      Object.keys(plansRef.current).forEach(function (k) {
        var e = plansRef.current[k];
        if (e.op && e.op.operationId && !FIX.isTerminal(e.op.state))
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
        var benchBusy = err && err.code === -32013 && err.data && err.data.reason === 'bench_active';
        flash(err && err.code === -32004 ? 'A scan is running' : benchBusy ? 'After the benchmark' : 'Scan failed');
        if (PF.scanStart(err)) problems.raise('scan', 'scan:' + params.idempotencyKey);
      });
    }
    // Scan (toolbar): every check, compared with the current report; PC specs are re-read alongside when the user
    // allowed them during setup (the inventory runs on its own stream; a refusal changes nothing).
    // Toolbar labels can hide on narrow windows (icons stay, with the name as tooltip and accessible name).
    function tbLabel(t) { return h('span', { className: 'ot-tb-label' }, t); }
    function fullScan() {
      if (busy.current) return;
      if (bench.active) { flash('After the benchmark'); return; }
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
      if (PF.scan(payload)) problems.raise('scan', 'scan:' + s.scanId);
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
        var since = deckSince();
        return backend.call('followup.respond', Object.assign({ idempotencyKey: uuid(), reportId: f.reportId, expectedRevision: revision == null ? f.revision : revision }, params))
          .then(function (res) { setFu(res, since); return { ok: true, fu: res }; }, function (err) {
            if (err && err.code === -32002 && err.data && err.data.current) { setFu(err.data.current, since); flash('Updated'); return { ok: false, stale: true }; }
            if (err && (err.code === -32013 || err.code === -32001)) {
              var since2 = deckSince();
              return backend.call('followup.get', { reportId: f.reportId }).then(function (cur) { setFu(cur, since2); flash('Updated'); return { ok: false }; }, function () { return { ok: false }; });
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
        var since = deckSince();
        return backend.call('finding.setDisposition', params).then(function (res) { setFu(res, since); return { ok: true }; }, function (err) {
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
        var since = deckSince();
        return backend.call('finding.clearDisposition', { idempotencyKey: uuid(), reportId: fuRef.current.reportId, findingId: fid })
          .then(function (res) { setFu(res, since); clearMode(cid); return true; }, function (err) { if (notSupported(err)) return false; flash('Not saved'); return true; });
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
      // A fix the host refused is shown too (which step and why).
      if (pl.requestedBy && pl.requestedBy.kind && pl.requestedBy.kind !== 'user' && (pl.status === 'awaiting_user' || pl.status === 'ineligible')) {
        backend.call('action.get', { planId: pl.planId }).then(function (plan) {
          if (plan.status === 'awaiting_user' || plan.kind === 'fix') putPlan(pl.planId, { plan: plan, op: plan.operation || null, kind: 'agent' });
        }, function () {});
      }
    }
    // Preview change (and Defender options): the backend builds the plan from live state; the card shows it.
    // M9 §3.6: a handler that needs a target (a startup entry, a process) gets targetRef {evidenceId} of evidence the card's
    // finding cites; several candidates → the card asks which one first. evidenceId: the one already chosen.
    function previewAction(card, actionId, evidenceId) {
      // A card action that isn't a catalog handler (privacy, display, Start…) is a manual task: Settings, then Check again.
      if (Object.keys(catalog).length && !catalog[actionId]) actionId = 'manual.open';
      var cid = card.id;
      if (!evidenceId && FIX.needsTarget(actionId, catalog[actionId])) {
        var choices = FIX.targetChoices(actionId, card.evidenceIds, evById, catalog[actionId]);
        if (choices.length > 1) { setMode(cid, 'pick', { pick: { actionId: actionId, choices: choices } }); return; }
        if (choices.length === 1) evidenceId = choices[0].evidenceId;
        else actionId = 'manual.open';   // nothing here it can name: the manual route
      }
      var params = FIX.previewParams(uuid(), actionId, card.findingId, report && report.reportId, actionId === 'manual.open' ? null : evidenceId);
      setMode(cid, 'loading');
      backend.call('action.preview', params).then(function (plan) {
        putPlan(plan.planId, { plan: plan, op: null, cardId: cid, kind: 'card' });
        setMode(cid, plan.status === 'ineligible' ? 'ineligible' : 'preview', { planId: plan.planId });
      }, function () { setMode(cid, null); flash('Not available'); });
    }
    // Approve goes to the desktop shell's native dialog (shell.approvePlan); the page never approves by itself.
    function approvePlan(planId, cid, choice) {
      if (cid) setMode(cid, 'running', { planId: planId });
      putPlan(planId, { approving: true });
      putPlan(planId, { approveError: null, approveCause: null });
      // M9 §5.2: a script's restore-point and output choices are made in the native dialog; only the browser demo's
      // simulated dialog takes them (and the read-to-the-end and tick) from the card.
      var params = { planId: planId };
      if (choice && isFixture) { params.options = { restorePoint: !!choice.restorePoint, shareOutput: !!choice.shareOutput }; params.ticked = true; params.readToEnd = true; }
      // §8: approved | declined | expired | stale | unavailable | error.
      backend.call('shell.approvePlan', params).then(function (r) { onApproval(planId, cid, r || {}); }, function () { onApproval(planId, cid, { status: 'error' }); });
    }
    function onApproval(planId, cid, r) {
      var entry = plansRef.current[planId] || {}, plan = entry.plan || {};
      putPlan(planId, { approving: false });
      if (r.status === 'approved') { if (r.operationId) putPlan(planId, { op: Object.assign({ operationId: r.operationId, planId: planId, state: 'prepared' }, entry.op) }); return; }
      if (r.status === 'stale' || r.status === 'expired') {
        // The plan changed or lapsed. A single change previews again from live state; a fix cannot (only the AI proposes
        // one), so it stays on screen as expired until dismissed.
        var card = cid ? cardById(cid) : null, next = FIX.afterStale(plan, !!card);
        if (next === 'expire') {
          putPlan(planId, { plan: Object.assign({}, plan, { status: 'expired' }) });
          if (cid) setMode(cid, 'preview', { planId: planId });
          return;
        }
        flash(r.status === 'expired' ? 'Expired' : 'Updated');
        if (next === 'repreview') previewAction(card, plan.actionId, plan.targetRef && plan.targetRef.evidenceId);
        else if (next === 'undo') { putPlan(planId, { closed: true }); undoOperation({ operationId: plan.undoOf }); }
        else putPlan(planId, { closed: true });
        return;
      }
      // Why it couldn't start, on the card (design errorCause; fix-plan.js approvalError): declined or the Windows window
      // closed, another change, a scan or a benchmark running (Retry), or the plan no longer waits (Close only).
      var why = FIX.approvalError(r) || FIX.approvalError({ status: 'error' });
      putPlan(planId, { approveError: why.error, approveCause: why.errorCause });
      if (cid) setMode(cid, 'preview', { planId: planId });
    }
    function declinePlan(planId) {
      putPlan(planId, { plan: Object.assign({}, (plansRef.current[planId] || {}).plan, { status: 'declined' }), approveCause: null, approveError: null });
      return backend.call('action.decline', { planId: planId }).catch(function () {});
    }
    // steps: [n] undoes those steps of a fix; without it, every undoable step (Undo all), or the single change.
    function undoOperation(op, steps) {
      if (!op || undoing.current[op.operationId]) return;
      // One Undo per change: a second click while its Undo is open (or on its way) does nothing.
      var open = Object.keys(plansRef.current).some(function (k) {
        var e = plansRef.current[k];
        return e.kind === 'undo' && e.plan && e.plan.undoOf === op.operationId && !e.closed && !e.op && e.plan.status !== 'declined' && e.plan.status !== 'expired';
      });
      if (open) return;
      undoing.current[op.operationId] = true;
      var up = { idempotencyKey: uuid(), operationId: op.operationId };
      if (steps) up.steps = steps;
      backend.call('action.undo', up).then(function (plan) {
        delete undoing.current[op.operationId];
        putPlan(plan.planId, { plan: plan, op: null, kind: 'undo' });
      }, function () { delete undoing.current[op.operationId]; flash('Not available'); });
    }
    function refreshOperation(operationId) {
      backend.call('operation.get', { operationId: operationId }).then(function (op) { if (plansRef.current[op.planId]) putPlan(op.planId, { op: op }); }, function () {});
    }
    // Stop after this step (m9 §4): the step in flight finishes; the rest are skipped. A single long operation (a Defender
    // scan, a repair): Stop ends it now (design v54); what it already did stays.
    function stopOperation(planId, op) {
      if (!op || !op.operationId) return;
      putPlan(planId, { stopping: true });
      backend.call('operation.stop', { operationId: op.operationId }).catch(function () { putPlan(planId, { stopping: false }); flash('Not available'); });
    }
    // The approval card for any change (entry: { planId, plan, op, approving, approveError, approveCause, stopping }).
    // design ApprovalCard (v53): a fix with its steps, a single change as one step (fix-plan.js planView); waiting while
    // the Windows approval is open, why it couldn't start (errorCause) with Retry or Close, Check again on an unclear step,
    // Open settings on a step Optimus can't undo or a plan that can't run. extra.onClose is Close (Dismiss when expired);
    // extra.onNext is Next; extra.manualUri: the Settings page that changes a single change back; extra.onPlanSettings(uri).
    // design v54: the running step's progress (a long single change), Stop on a single long operation, the undo line
    // before approval, Check again / Open settings on a failed step, Back (extra.onBack: card previews only, no decision
    // recorded), and a guided task (a manual.open plan: Open settings, Check again = manual.recheck; extra.guide: the
    // card's own steps; extra.settingsPath: the card's Settings path, design v59, when the plan carries none).
    function planCardProps(e, extra) {
      extra = extra || {};
      var pl = e.plan, single = pl.kind !== 'fix';
      var v = FIX.planView(pl, e.op, extra.manualUri || null, { progress: e.progress, guide: extra.guide || null, recheck: e.recheck || null, title: extra.guideTitle || null,
        settingsPath: extra.settingsPath || null, task: task });
      // A long single operation reports progress and can be stopped (the host's provider tools and long flags).
      var long = single && (pl.kind === 'provider' || (pl.flags || []).indexOf('long') >= 0);
      var why = pl.why && typeof pl.why === 'object' ? pl.why : pl.why ? { text: String(pl.why) } : null;
      // An approval that couldn't start keeps the card waiting, whatever the plan's status says meanwhile.
      var state = (e.approveCause || e.approveError) && !e.op ? 'awaiting' : v.state;
      var planSettings = extra.onPlanSettings || openSettings;
      var props = Object.assign({ title: pl.undoOf ? 'Undo' : pl.title || (v.steps[0] && v.steps[0].label) || 'Change',
        requestedBy: FIX.requesterName(pl, task),
        reason: why && why.text, from: why && why.by, steps: v.steps, state: state, blockedWhy: v.blockedWhy,
        onOpenPlanSettings: v.planSettingsUri ? function () { planSettings(v.planSettingsUri); } : null,
        waiting: !!e.approving, error: e.approveError || null, errorCause: e.approveCause || null, stopping: !!e.stopping,
        onApprove: function () { approvePlan(e.planId, null); },
        onDecline: function () { declinePlan(e.planId); },
        // A single change stops only when it is a long operation; a fix stops after the step in flight.
        onStop: single && !long ? null : function () { stopOperation(e.planId, e.op); },
        onUndoStep: single ? function () { undoOperation(e.op); } : function (i) { undoOperation(e.op, [v.steps[i].n]); },
        onUndoAll: function () { undoOperation(e.op); },
        onCheckStep: function () { checkOperation(e); },
        onOpenSettings: v.settingsUri || extra.manualUri ? function () { openSettings(v.settingsUri || extra.manualUri); } : null }, extra);
      delete props.manualUri; delete props.onPlanSettings; delete props.guide; delete props.guideTitle; delete props.settingsPath;
      if (v.guided) {
        // The user makes this change in Windows Settings (normal, not an error): the page, then Check again (design v56:
        // Checking… while it runs, and the guided note's line when nothing has changed yet).
        props.onOpenPlanSettings = v.planSettingsUri ? function () { openSettings(v.planSettingsUri); } : null;
        props.onCheckAgain = v.state === 'guided' && pl.findingId ? function () { recheckPlan(e); } : null;
        props.checking = !!e.checking; props.stillSame = v.state === 'guided' && !!e.stillSame;
        if (v.state !== 'guided') delete props.onBack;
        if (v.title && !pl.title) props.title = v.title;
      }
      // A report-only result (design v56 'reported'): the tool's result, Close and Check again (the finding's own check;
      // none without a finding: Close alone). Design v59: what Check again found is a line in the result note (recheck).
      if (v.state === 'reported') {
        props.result = v.result;
        props.onCheckAgain = pl.findingId ? function () { recheckPlan(e); } : null;
        props.checking = !!e.checking;
        props.recheck = !e.checking && e.reportRecheck ? e.reportRecheck : null;
        delete props.onBack;
      }
      // A plan that no longer waits: its one button reads Close and closes the card.
      if (e.approveCause === 'cancelled') props.onDecline = props.onClose || null;
      return props;
    }
    // M9 §5: the script approval card (entry: { planId, plan, op, progress, approving, approveError, stopping, view, transcript }).
    function scriptCardProps(e, extra) {
      var pl = e.plan, v = FIX.scriptView(pl, e.op, e.progress), who = FIX.requesterName(pl, task);
      // An approval that couldn't start keeps the card waiting; a plan that no longer waits closes on Decline.
      if ((e.approveCause || e.approveError) && !e.op) v.state = 'awaiting';
      var close = e.approveCause === 'cancelled' && extra && extra.onClose;
      return Object.assign(v, { title: pl.title || 'Script', requestedBy: who, from: who, reason: pl.why && pl.why.text,
        waiting: !!e.approving, error: e.approveError || null, errorCause: e.approveCause || null, stopping: !!e.stopping,
        transcript: e.transcript ? e.transcript.text : '', transcriptCut: !!(e.transcript && e.transcript.truncated),
        view: e.view || null, onView: function (x) { putPlan(e.planId, { view: x }); if (x === 'transcript') loadTranscript(e.planId, e.op); },
        onApprove: function (choice) { approvePlan(e.planId, null, choice); },
        onDecline: close || function () { declinePlan(e.planId); }, onStop: function () { stopOperation(e.planId, e.op); } }, extra || {});
    }
    // operation.transcript (desktop): the captured output, read-only; read again each time it is opened.
    function loadTranscript(planId, op) {
      if (!op || !op.operationId) return;
      backend.call('operation.transcript', { operationId: op.operationId }).then(function (r) { putPlan(planId, { transcript: r || { text: '', truncated: false } }); },
        function () { flash('Not available'); });
    }
    // Activity's Transcript on a run row: the script's card comes back with its transcript open.
    function showTranscript(planId) {
      var e = plansRef.current[planId]; if (!e) return;
      putPlan(planId, { closed: false, view: 'transcript' }); loadTranscript(planId, e.op);
    }
    function checkOperation(entry) {
      if (!entry || !entry.op) return;
      backend.call('operation.get', { operationId: entry.op.operationId }).then(function (op) { putPlan(op.planId, { op: op }); }, function () { flash('Not available'); });
    }
    // Check again (a guided task, or a report-only result): manual.recheck rereads the finding, the button reads Checking…
    // meanwhile. A guided task that is fixed → the Verified card with its steps done; not yet → the guided note's "Checked
    // again. Nothing has changed yet." (design v56, no toast). A report-only result stays a Finished card with the line
    // gone | still in its note (design v59 recheck).
    function recheckPlan(e) {
      if ((plansRef.current[e.planId] || {}).checking) return;
      putPlan(e.planId, { checking: true, stillSame: false });
      backend.call('manual.recheck', { findingId: e.plan.findingId }).then(function (r) {
        var status = r && r.status;
        if (!FIX.isGuided(e.plan)) { putPlan(e.planId, { checking: false, reportRecheck: FIX.recheckWord(status) }); return; }
        putPlan(e.planId, status === 'verified_by_rescan' ? { checking: false, recheck: status } : { checking: false, stillSame: true });
      }, function () { putPlan(e.planId, { checking: false }); flash('Not available'); });
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
      if (o === 'Open settings') { openSettings(curSettings); return; }
      if (DEFENDER_OPTIONS[o]) { previewAction(curCard, DEFENDER_OPTIONS[o]); return; }
      // Design v59: a guided finding's steps are the guided approval card (the host's manual.open plan), never a mode of
      // the card itself.
      if (o === 'View steps') previewAction(curCard, 'manual.open');
      // A preview is always built by the backend (a plan); without an action there is nothing to preview.
      else if (o === 'Preview change') { if (curCard.actionId) previewAction(curCard, curCard.actionId); else if (curCard.guide) previewAction(curCard, 'manual.open'); else flash('Not available'); }
      else if (o === 'Ask about this') { setFocusPart(cid, 'ask', true); setAskToken(function (t) { return { cid: cid, n: t.n + 1 }; }); }
      else if (OPTION_IDS[o]) leave(cid, OPTION_IDS[o]);
    }
    // With an in-app provider connected, questions go to the assistant (agent.ask). The first one starts a task with the
    // permissions the user set (M9 §1.4: no grant sheet; the host builds the grant).
    // The box, a card's "Ask about this" and the summary all use the same active task (m5 §6).
    // The chosen AI (connRef) decides: a task handed in is asked as it is (its recipient is the one it was started for);
    // otherwise the current task only when it is the chosen AI's, else a new task for the chosen AI (OT.askTarget).
    function agentAsk(text, cardId, t, extra) {
      var rep = reportRef.current, pending = Object.assign({ text: text, cardId: cardId }, extra);
      var isFix = !!(extra && extra.intent === 'fix');
      var target = OT.askTarget({ given: t || null, current: liveTask.current, chosenKind: connRef.current.kind, reportId: rep && rep.reportId });
      if (target.start) { startTask({ kind: target.start, pending: pending }); return; }
      if (!target.task) return;
      t = target.task;
      // Only an answer still on its way holds the next question; a failed one doesn't.
      // Only this task's unanswered question holds the next one (another task's never does).
      if (answersRef.current.some(function (a) { return a.taskId === t.taskId && a.text == null && !a.failedTurn; })) { flash('One moment'); return; }
      var id = uuid();
      // m5 §9: every question from the box is a panel of its own, newest on top.
      setAnswers(function (list) { return list.concat([Object.assign({ id: id, q: text || null, cardId: cardId || null, taskId: t.taskId, turnId: null, text: null, busy: 'Thinking…', reportId: rep ? rep.reportId : null, at: new Date().toISOString() }, extra, isFix ? { fix: true } : null)]).slice(-KEEP_ANSWERS); });
      var params = { idempotencyKey: uuid(), taskId: t.taskId };
      if (text || !isFix) params.text = text;
      if (cardId) params.cardId = cardId;
      // m9 §17.3: Fix for me (intent fix; text optional), "Change something…" (revise: the plan it replaces).
      if (isFix) { params.intent = 'fix'; if (extra.revise) params.revise = extra.revise; }
      // A new question beside the chat: ended checks requests there step aside.
      if (!cardId && !(extra && extra.scan)) setCheckReqs(function (m) { var n = {}; Object.keys(m).forEach(function (k) { n[k] = !FIX.checksOpen(m[k]) && !m[k].cardId ? Object.assign({}, m[k], { hidden: true }) : m[k]; }); return n; });
      // M6a §8: Investigate with AI names the benchmark run and part it is about.
      if (extra && extra.context) params.context = extra.context;
      backend.call('agent.ask', params).then(function (r) {
        setAnswers(function (list) { return list.map(function (a) { return a.id === id && !a.turnId ? Object.assign({}, a, { turnId: r.turnId }) : a; }); });
      }, function (err) {
        var drop = function () { setAnswers(function (list) { return list.filter(function (a) { return a.id !== id; }); }); };
        // The answer to a task that is no longer the current one: nothing to show or offer.
        if (!isCurrentTask(t.taskId)) { drop(); return; }
        // m9 §17.3: a refused fix ask changes nothing: busy says so; anything else re-reads the cards (their fix as it is).
        if (isFix && !taskEnded(err)) {
          drop();
          if (err && err.code === -32013 && err.data && err.data.reason === 'busy') flash('One moment'); else refreshDeck();
          return;
        }
        // The task ended (expired, stopped, budget used, failed): a fresh one with the same question.
        if (taskEnded(err)) {
          drop();
          var reason = err.data && err.data.reason ? String(err.data.reason).replace(/^task_/, '') : 'stopped';
          setTask(function (x) { return x && x.taskId === t.taskId ? Object.assign({}, x, { status: x.status === 'active' ? (TASK_WORD_KEYS[reason] ? reason : 'stopped') : x.status }) : x; });
          // Asked again of the AI chosen now (never of one the user has switched away from).
          if (connRef.current.kind) startTask({ kind: connRef.current.kind, pending: pending });
        } else setAnswers(function (list) { return list.map(function (a) { return a.id === id ? Object.assign({}, a, { failedTurn: true, busy: null }) : a; }); });
      });
    }
    // g: { kind, pending?, pairingId?, grant? }. In-app and headless asks start without a grant (the host builds it from
    // the permissions record); only the pairing panel sends one, for that task alone.
    function startTask(g) {
      var rep = reportRef.current;
      if (!g || !rep) return;
      // A typed question that can't go now goes back in the box, so nothing typed is lost.
      var giveBack = function () { if (g.pending && !g.pending.cardId && !g.pending.scan) setRestore({ text: g.pending.text, token: Date.now() }); };
      // One start at a time for an AI; a start for an AI the user has switched away from doesn't hold the new one's.
      if (starting.current && starting.current.kind === g.kind) { giveBack(); flash('One moment'); return; }
      var params = { idempotencyKey: uuid(), goal: (g.pending && g.pending.text) || 'Review my report', recipientKind: g.kind,
        scope: { reportId: rep.reportId, findingIds: null } };
      if (g.grant) params.grant = g.grant;
      // A user-window task needs its approved pairing, whichever way the panel was opened; a connected
      // Claude without one runs headless in the app (m5 §3).
      var headless = isMcpKind(g.kind) && !g.pairingId && stateOf(g.kind).row === 'connected';
      var pid = headless ? null : g.pairingId || (isMcpKind(g.kind) ? pairingFor(g.kind) : null);
      if (pid) params.pairingId = pid;
      var mine = { kind: g.kind };
      starting.current = mine;
      var done = function () { if (starting.current === mine) starting.current = null; };
      if (g.grant) setGrant(function (x) { return x && Object.assign({}, x, { busy: true }); });
      var send = function () {
        // The choice changed while it was being saved: nothing is started for the AI left behind.
        if (!pid && g.kind !== connRef.current.kind) { done(); giveBack(); return; }
        backend.call('agent.start', params).then(function (t) {
          done();
          // Started for an AI that is no longer the chosen one: it ends at once and nothing is asked of it.
          if (OT.staleStart(g, t, connRef.current.kind)) { backend.call('agent.stop', { taskId: t.taskId }).catch(function () {}); giveBack(); return; }
          attachTask(t, []);
          if (g.grant) setGrant(null);
          if (g.pending && g.pending.scan) firstScan(t);
          else if (g.pending) agentAsk(g.pending.text, g.pending.cardId, t, g.pending.context ? { context: g.pending.context }
            : g.pending.intent ? { intent: g.pending.intent, revise: g.pending.revise || undefined } : undefined);
          else if (isMcpKind(g.kind) && !headless) setShowActivity(true);
        }, function (err) {
          done();
          // The host has another AI saved as chosen: read the saved choice again and try once more (never an error shown).
          if (OT.notChosen(err)) {
            if (g.rechecked) { giveBack(); return; }
            backend.call('onboarding.get').then(setOnb, function () {}).then(function () {
              if (g.kind === connRef.current.kind) startTask(Object.assign({}, g, { rechecked: true })); else giveBack();
            });
            return;
          }
          if (g.grant) setGrant(function (x) { return x && Object.assign({}, x, { busy: false }); });
          giveBack();
          flash('Not started');
        });
      };
      // An in-app or headless start waits for the chosen AI's save (the host refuses an AI not saved as chosen).
      if (!pid && choiceSave.current) choiceSave.current.then(send); else send();
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
      // Only ever for the AI chosen now (a first look offered before a later switch is not run).
      if (!kind || kind !== connRef.current.kind || !fuRef.current || scannedKinds().indexOf(kind) >= 0) return;
      var live = OT.askTarget({ current: liveTask.current, chosenKind: kind, reportId: fuRef.current.reportId }).task;
      if (live) { firstScan(live); return; }
      startTask({ kind: kind, pending: { text: SCAN_PROMPT, cardId: null, scan: true } });
    }
    // m5 §8: Stop ends only the running answer (turn or headless run); the task and its permissions stay.
    // forTask: the task whose run to stop (a card's fix names its own, card.fix.taskId); else the current task.
    function cancelRun(forTask) {
      var t = typeof forTask === 'string' ? { taskId: forTask } : task; if (!t) return;
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
    // (agentAsk starts a fresh one if it ended), or an earlier task that is still active; otherwise the usual way.
    function followUp(a, q) {
      if (!task || a.taskId === task.taskId) { agentAsk(q, null); return; }
      if (runInProgress) { flash('One moment'); return; }
      backend.call('agent.get', { taskId: a.taskId }).then(function (t) {
        var rep = reportRef.current;
        var fits = !!OT.askTarget({ current: t, chosenKind: connRef.current.kind, reportId: rep && rep.reportId }).task;
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
        var label = aiName(kind, providers[kind] && providers[kind].label) || 'Claude';
        if (stageRef.current.id === 'login') { commitProvider({ value: tileOfKind(kind), label: label, kind: kind }); return; }
        // Approved from the card or the pill: this client is now the chosen AI (the chooser's connect step is over).
        setConnect(null);
        if (kind && connRef.current.kind !== kind) { var v = OT.storedValue(tileOfKind(kind)); setConn({ value: v, label: label, kind: kind }); stopOtherAi(kind); saveChoice({ value: v, label: label, kind: kind }).catch(function () {}); }
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
      var label = labels[eid] || OT.citeLabel(taskLabels.current, eid, citeHint(eid));
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
    // The selected AI isn't usable: offer its action (the pairing panel on paired, the pill detail otherwise).
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
    // M9 §2: with no AI connected, "what can this app do?" is answered here from the app guide (guide.get), not
    // routed to "connect an AI". An older host without guide.get takes the usual path.
    function guideAnswer(text) {
      backend.call('guide.get', { topic: 'overview' }).then(function (g) {
        var id = 'guide-' + Date.now();
        setAnswers(function (list) { return list.concat([{ id: id, q: text, cardId: null, taskId: null, turnId: null, text: String(g.text || ''), citations: [], followUps: [], busy: null,
          local: true, reportId: reportRef.current ? reportRef.current.reportId : null, at: new Date().toISOString() }]).slice(-KEEP_ANSWERS); });
        showPanel(id);
      }, function () { submitMain(text, true); });
    }
    function submitMain(text, noGuide) {
      if (!noGuide && isLocal && OT.isAppQuestion(text)) { guideAnswer(text); return; }
      // With a report, free text is a reply or a question about it. Starters, "scan…", and anything before a report scan.
      if (stage.id !== 'main' || !report || FULL_AUDIT.test(text) || starterHit(text)) { runAudit(text, routeIntent(text)); return; }
      var card = fu && fu.card;
      // "Do that for me" opens the exact preview; it never approves.
      if (DO_THAT.test(text)) {
        // The exact preview is a plan the backend builds from live state, never the card's own sketch.
        if (card && card.actionId) { previewAction(card, card.actionId); flash('Here’s exactly what would change'); }
        else if (card && card.guide) { previewAction(card, 'manual.open'); flash('This one is manual: here are the steps'); }
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
      // Claude in the app: the summary is one more question to the same task, answered under the box.
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
    // M9 §1.4, the catch-all: on the main page with an AI whose permissions were never set (e.g. after a scan the
    // user resumed, or a pairing approved from a card), the page comes first.
    React.useEffect(function () {
      if (stage.id !== 'main' || stage.leaving || conn.value === 'local' || conn.value === 'later') return;
      if (gateTo(conn.kind, 'main').stage === PERM_STAGE) openPermissions('main');
    }, [stage.id, stage.leaving, conn.kind, perm]);

    /* ---------- login and setup ---------- */
    // Committing a connection: a different recipient revokes the current task; drafts and the report stay.
    // usable: the kind is connected right now, so a first connect offers its first look (m5 §6).
    // Switching stops the old AI's in-app task first (its running answer and any approval it asked for end with it, on
    // the host); an outside Claude Code window that is paired is left as it is.
    function stopOtherAi(kind) {
      var cur = liveTask.current;
      if (!OT.switchStops(cur, kind)) return;
      var old = cur.taskId;
      // m9 §17.2: its open administrator checks end task_ended; on screen that is "you changed the AI".
      switchedTasks.current[old] = true;
      setCheckReqs(function (m) { var n = Object.assign({}, m); Object.keys(n).forEach(function (k) { if (n[k].taskId === old) n[k] = Object.assign({}, n[k], { switchedAi: true }); }); return n; });
      liveTask.current = Object.assign({}, cur, { status: 'stopped' });
      setTask(function (x) { return x && x.taskId === old ? Object.assign({}, x, { status: 'stopped' }) : x; });
      setLiveTurn(null); setShowActivity(false);
      // The old task's unanswered questions go with it; its answered ones close (they stay in History).
      setOpen(answersRef.current.filter(function (a) { return a.taskId === old; }).map(function (a) { return a.id; }), false);
      setAnswers(function (list) { return list.filter(function (a) { return a.taskId !== old || a.text != null || a.failedTurn; }); });
      backend.call('agent.stop', { taskId: old }).then(mergeTask, function () {});
    }
    function commitProvider(c, usable) {
      // The picker's tile ids (claude, api-key) are stored as the onboarding values (claude-code, api); labels as users see them.
      c = Object.assign({}, c, { value: OT.storedValue(c.value) || c.value, label: aiName(c.kind, c.label) || c.label });
      var next = nextAfterProvider(onbRef.current && onbRef.current.step, returnTo);
      var prevKind = connRef.current.kind;
      // The choice first, before anything else runs: every later reply and event reads it (connRef).
      setConn(c); setConnect(null);
      stopOtherAi(c.kind);
      // A summary preview made for the previous AI is not sent to the new one.
      if (c.kind !== prevKind && summary && summary.preview) setSummary(null);
      // The save goes first; the new AI's first look starts only once the host has it as the chosen one.
      var saved = saveChoice(c, 'use');
      saved.then(function () { if (c.kind) loadPermissions(); }, function () { flash('Not saved'); });
      if (summary === 'connect' && c.kind) { pendingSummary.current = true; setSummary(null); }
      if (next === 'main') loadStarters();
      // M9 §1.4: an AI whose permissions were never set shows the page first; Back and Continue lead on to `next`.
      // A different AI asks again even after a Back. The provider's `available` is read again once it is saved.
      if (c.kind !== prevKind) permLater.current = false;
      var gate = gateTo(c.kind, next);
      if (gate.stage === PERM_STAGE) {
        permAfter.current = usable && next === 'main' ? c.kind : null;
        setReturnTo(next); go(PERM_STAGE);
        return;
      }
      setReturnTo(null); go(next);
      if (usable && next === 'main') saved.then(function () { offerFirstScan(c.kind); }, function () {});
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
      return backend.call('provider.disconnect', { kind: kind }).then(function (e) {
        mergeProvider(e); setDetailOpen(false); setConnect(null);
        if (isMcpKind(kind)) { delete pairedIds.current[kind]; setPairing(function (cur) { return cur && cur.client === CLIENT_OF_KIND[kind] ? null : cur; }); }
        var cur = liveTask.current;
        if (cur && cur.status === 'active' && cur.recipient.kind === kind) {
          var old = cur.taskId;
          setTask(function (x) { return x && x.taskId === old ? Object.assign({}, x, { status: 'stopped' }) : x; });
          setLiveTurn(null);
          setAnswers(function (list) { return list.filter(function (a) { return a.taskId !== old || a.text != null || a.failedTurn; }); });
        }
        if (connRef.current.kind === kind) { setConn({ value: 'local', label: 'Local only', kind: null }); saveChoice({ value: 'local', label: 'Local only' }).catch(function () {}); }
      }, function () { flash('Not saved'); });
    }
    function showCommandFor(kind) {
      setShowCmd(true);
      if (regs.current[kind]) return;
      backend.call('provider.connect', { kind: kind }).then(function (e) { mergeProvider(e); rememberReg(kind, e); }, function () {});
    }
    function rememberReg(kind, e) { var reg = e && e.registration; if (reg) regs.current[kind] = reg.claude; }
    // Claude Code in its own window: show the registration command, then wait for the bridge's pairing request.
    function connectNative(tile, kind, label) {
      backend.call('provider.connect', { kind: kind }).then(function (e) {
        mergeProvider(e); rememberReg(kind, e);
        if (e.status === 'paired') { setGrant({ kind: kind, pairingId: e.pairingId || pairingFor(kind) }); commitProvider({ value: tile, label: e.label || label, kind: kind }); return; }
        if (e.status === 'active' || e.status === 'client_closed') { commitProvider({ value: tile, label: e.label || label, kind: kind }); return; }
        var reg = e.registration || {};
        // The choice is only made on Continue (or an approved pairing); Back leaves the previous AI as it was.
        setConnect({ tile: tile, kind: kind, label: e.label || label, phase: 'register', command: reg.claude });
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
    // Claude's next step in the picker (m5 §6): Install (or Update), then Sign in; Connect once connected is chooseProvider.
    function clientAction(tile) {
      var cs = clients[tile];
      if (!cs || !cs.known) { checkClient(tile); return; }
      // Blocked on this PC: nothing to click through; check again after the user changed something.
      // (node_missing / unsupported_path: the official installer needs neither, so it installs.)
      if (cs.blocked) { if (cs.blockedReason === 'node_missing' || cs.blockedReason === 'unsupported_path') installClient(tile); else checkClient(tile); return; }
      // Only an outdated CLI: the official installer brings the current one.
      if (cs.outdated) { installClient(tile); return; }
      // Installed, but the sign-in check gave no answer: ask again instead of opening another sign-in.
      if (cs.installed && cs.signedIn == null && !cs.signing) { checkClient(tile); return; }
      if (!cs.installed) { installClient(tile); return; }
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
          if (Date.now() > until) { stopInstallPoll(key); var off = {}; off[flag] = false; if (flag === 'installing') off.failed = 'Not finished'; putClient(tile, off); return; }
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
    // m9 §17.3: a plan bound to a card on screen is shown inside that card (a script stays here: the card has no script view).
    var deckIds = {}; cards.forEach(function (c) { deckIds[c.id] = true; });
    var inCard = function (e) { return !!(e.plan && e.plan.cardId && deckIds[e.plan.cardId] && e.plan.kind !== 'script'); };
    // A script a card on screen points to is always here (whatever read it first: a reload's operations, the card's plan read),
    // until closed; a script reopened from Activity too.
    var pointed = {}; cards.forEach(function (c) { if (c.fix && c.fix.planId) pointed[c.fix.planId] = true; });
    var sidePlans = Object.keys(plans).map(function (k) { return Object.assign({ planId: k }, plans[k]); }).filter(function (e) {
      if (e.plan && e.plan.kind === 'script' && (pointed[e.planId] || e.closed === false)) return !e.closed;
      return (e.kind === 'agent' || e.kind === 'undo') && e.plan && !e.closed && !inCard(e) && ((e.plan.status !== 'declined' && e.plan.status !== 'cancelled') || !!e.approveCause) &&
        // An expired fix stays until dismissed (it says so and offers Dismiss); other expired plans just go.
        (e.plan.status !== 'expired' || (e.plan.kind === 'fix' && !e.plan.undoOf && !e.op));
    });
    var cls = function (base) { return cx(base, stage.leaving ? 'ot-sink' : 'ot-enter'); };
    var id = stage.id, signedIn = id !== 'login' && id !== 'boot' && id !== PERM_STAGE;
    // The chooser and the permissions page are full pages on the sea: the main page sinks away before either.
    var toLogin = stage.leaving && (stage.next === 'login' || stage.next === PERM_STAGE);
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
    // The picker's view of Claude Code (m5 §6): Install → Sign in → Connected, and the optional window connector.
    var clientViews = {};
    Object.keys(CLIENT_OF_TILE).forEach(function (tile) {
      var cs = clients[tile]; if (!cs || !cs.known) return;
      var client = CLIENT_OF_TILE[tile];
      // note: the design's one grey line (claudeNote), from the same status.
      var installing = { installCmd: INSTALL_CMD[client], installing: !!cs.installing, failed: cs.failed, note: OT.claudeNote(cs),
        onInstall: function () { installClient(tile); }, onCheck: function () { checkClient(tile); } };
      // The CLI is there but this PC won't let it run, or only an old one is: both stay on the install step
      // (the note says which: blocked, repair, updating …).
      if (cs.blocked || cs.outdated || !cs.installed) {
        clientViews[tile] = Object.assign({ notInstalled: true, blocked: !!cs.blocked, outdated: !cs.blocked && !!cs.outdated, app: !!cs.app }, installing);
        return;
      }
      clientViews[tile] = { installed: true, signedIn: cs.signedIn, signing: !!cs.signing, failed: cs.failed, note: OT.claudeNote(cs), installCmd: INSTALL_CMD[client],
        billing: cs.signedIn === true && BILLING[client] ? BILLING[client][cs.auth] || null : null,
        onSignIn: function () { signInClient(tile); }, onInstall: function () { installClient(tile); },
        registered: cs.registered, adding: cs.busy, onRegister: function () { registerClient(tile); }, onCheck: function () { checkClient(tile); } };
    });
    var tileStates = {};
    ['anthropic_api', 'openai_api', 'chatgpt_plan', 'claude_code_mcp'].forEach(function (k) { if (providers[k]) tileStates[k] = stateOf(k); });
    // The mono word under each picker tile (design tileStatus): connection-state.js tileStatus.
    var apiState = tileStates.anthropic_api && tileStates.anthropic_api.row === 'connected' ? tileStates.anthropic_api : tileStates.openai_api || tileStates.anthropic_api;
    var tileStatus = { claude: OT.tileStatus('claude', clients.claude, tileStates.claude_code_mcp), chatgpt: OT.tileStatus('chatgpt', null, tileStates.chatgpt_plan),
      'api-key': OT.tileStatus('api-key', null, apiState) };
    // design v73: the pill panel's buttons by name (AIPillPanel onAction), for the connection `kind` and its state.
    function pillAction(name, kind) {
      if (name === 'Activity') { if (task) { setShowActivity(true); setDetailOpen(false); } }
      // Stop: a native client's task ends (as before); an in-app run is cancelled (agent.cancel), the task stays.
      else if (name === 'Stop') { if (nativeLive || !runInProgress) stopTask(); else cancelRun(); }
      else if (name === 'Approve' || name === 'Deny') { if (pairing) decidePairing(name === 'Approve'); }
      else if (name === 'Start') { if (report) { setGrant({ kind: kind }); setDetailOpen(false); } else flash('Scan first'); }
      else if (name === 'Set up') openChooser();
      else if (name === 'Check again') { loadProviders(); if (isMcpKind(kind)) checkClient(tileOfKind(kind)); }
      else if (name === 'Sign in' && isMcpKind(kind)) signInClient(tileOfKind(kind));
      else if (name === 'Sign in' || name === 'Add key' || name === 'Update key' || name === 'Retry') apiAction(kind, false);
    }
    // The manual layout's Copy: the command to the clipboard (Copied once it is there).
    function copyCommand(text) {
      var fail = function () { flash('Copy failed'); throw new Error('copy'); };
      try { return navigator.clipboard.writeText(text || '').then(null, fail); } catch (e) { flash('Copy failed'); return Promise.reject(e); }
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
    // design v73: the pill panel's manual layout (waiting, bridge_rejected) shows the command at once: fetch it when opened.
    var pillCmd = !!(detailOpen && pillKind && pillState.command);
    React.useEffect(function () { if (pillCmd) showCommandFor(pillKind); }, [pillCmd, pillKind]);
    // Report evidence keeps its e-label; evidence from this task's own checks is named by its kind as it is cited
    // ("Ports", "CPU 2"), or numbered m1, m2 … while its kind is not known.
    function citeChip(id) {
      return { id: id, label: labels[id] || OT.citeLabel(taskLabels.current, id, citeHint(id)) };
    }
    // What an evidence id came from: the check that returned it (Activity), else the evidence itself once loaded.
    function citeHint(id) {
      for (var i = 0; i < activity.length; i++) {
        var a = activity[i];
        if (a.evidenceIds && a.evidenceIds.indexOf(id) >= 0) return (a.arguments && a.arguments.diagnosticId) || a.tool;
      }
      return tev.current[id] || null;
    }
    // A failed in-app turn shows the connection's state word and its action (or "No answer").
    var failWord = askState.tone === 'expired' && askState.word ? askState.word : 'No answer';
    var failAction = ACTION_WORD[askState.action] && isApiKind(conn.kind) ? { label: ACTION_WORD[askState.action], onClick: function () { apiAction(conn.kind, false); } }
      : askState.action === 'signIn' && isMcpKind(conn.kind) ? { label: ACTION_WORD.signIn, onClick: function () { signInClient(tileOfKind(conn.kind)); } } : null;
    // M6.0: a turn the host stopped because the AI tried a blocked tool says so (no connection action).
    var BLOCKED_WORD = 'Stopped · the AI tried a blocked tool';
    function failOf(a) { return !a.failedTurn ? null : a.failCode === 'tool_blocked' ? BLOCKED_WORD : failWord; }
    function failActionOf(a) { return a.failedTurn && a.failCode !== 'tool_blocked' ? failAction : null; }
    var aiLabel = (task && task.recipient && aiName(task.recipient.kind, task.recipient.label)) || (curProvider && aiName(curProvider.kind, curProvider.label)) || (isLocal ? null : conn.label);
    problems.ai.current = aiLabel;
    var reportAnswers = answers.filter(function (a) { return !a.cardId && (!a.reportId || !report || a.reportId === report.reportId); }).map(function (a) {
      return Object.assign({}, a, { cites: (a.citations || []).map(citeChip), failed: failOf(a), action: failActionOf(a) });
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
      return Object.assign({}, a, { cites: (a.citations || []).map(citeChip), failed: failOf(a), action: failActionOf(a) });
    });
    // History: answers and finished benchmarks together, newest first.
    var historyRows = pv2.history.concat(bench.history).map(function (r, i) { return { r: r, i: i, t: Date.parse(r.at || '') || 0 }; })
      .sort(function (x, y) { return (y.t - x.t) || (x.i - y.i); })
      .map(function (x) { return Object.assign({}, x.r, { when: x.r.at ? whenDay(x.r.at) : '' }); });
    // M6a §8: Investigate with AI asks on the active task with the run and part as context; the answer is a normal panel.
    var benchAi = inApp && !!report;
    function investigate(part, verdict) {
      var run = bench.run; if (!run) return;
      var label = (OT.bench.PARTS.filter(function (x) { return x.id === part; })[0] || { label: part }).label;
      agentAsk(verdict === 'running_hot' ? 'Why is my ' + label + ' running hot?' : 'Why is my ' + label + ' slower than before?', null, null,
        { context: { benchRunId: run.runId, part: part } });
    }
    // Save image: the desktop shell's Save dialog (shell.saveImage, it never tells the page the path); the website downloads.
    function saveBenchImage(dataUrl) {
      var name = 'Optimus-benchmark-' + OT.bench.fileDate(bench.run && bench.run.finishedAt) + '.png';
      if (backend.kind === 'desktop') {
        return backend.call('shell.saveImage', { pngBase64: String(dataUrl).split(',')[1] || '', suggestedName: name }).then(function (r) { return !!(r && r.saved); });
      }
      var a = document.createElement('a'); a.href = dataUrl; a.download = name; document.body.appendChild(a); a.click(); a.remove();
      return Promise.resolve(true);
    }
    function focusBench(ms) {
      later(function () { var el = document.querySelector('[data-bench]'); if (el) { if (el.scrollIntoView) el.scrollIntoView({ block: 'nearest', behavior: reduce ? 'auto' : 'smooth' }); if (el.focus) el.focus({ preventScroll: true }); } }, ms || 30);
    }
    function openBench() { setMenu(null); bench.openPanel(); focusBench(); }
    // design AgentActivity rows: the words (fix-plan.js activityWords), the tool id as the mono line, where it ran and its size.
    var actItems = [].concat.apply([], activity.map(function (a) {
      var args = a.arguments || {}, pe = a.planId ? plans[a.planId] : null;
      var w = FIX.activityWords(a.tool, a.status !== 'running', a.label, pe && pe.plan && pe.plan.steps ? pe.plan.steps.length : 0, args.diagnosticId);
      var it = { id: a.invocationId, title: w.title, detail: w.detail, tool: w.tool, origin: a.origin, originLabel: ORIGIN_LABEL[a.origin] || a.origin, size: a.bytes ? kib(a.bytes) : null,
        status: a.status, evidenceIds: a.evidenceIds, startedAt: startedAt.current[a.invocationId],
        window: a.status === 'running' && a.tool === 'run_diagnostic' && args.diagnosticId === 'process.sample' ? (args.window || 15) : null };
      var e = a.tool === 'propose_script' && pe ? pe : null;
      if (!e || !e.op || !e.plan || e.plan.kind !== 'script') return [it];
      var ended = FIX.scriptEnded(e.op.state), pid = a.planId, run = FIX.activityWords('script.run', ended);
      return [it, { id: 'run:' + e.op.operationId, title: run.title, tool: run.tool, detail: ended ? FIX.scriptRunWords(e.op) : null, origin: it.origin, originLabel: it.originLabel,
        status: ended ? 'complete' : 'running', startedAt: e.op.startedAt ? Date.parse(e.op.startedAt) : null,
        action: FIX.scriptHasTranscript(e.op) ? { label: 'Transcript', onClick: function () { showTranscript(pid); } } : null }];
    }));
    // While the assistant or a native client works and Activity is closed, the pill says so.
    var working = taskActive && !showActivity && (!!workedAt || activity.some(function (a) { return a.status === 'running'; }) || taskWaiting);
    // design v86 (PillPanelStates): the pill panel's state (pillPanelState), and the toolbar pill shows its status word.
    var pillPanel = OT.pillPanelState({ kind: pillKind, local: !pillKind, entry: pillKind ? providers[pillKind] : null, label: conn.label, backend: bstate, task: task }, working);
    var pillWord = OT.pillStatus(pillPanel.state);
    // Pairing in one place: while the open panel shows pairing, the main-page PairingCard is not shown.
    var pairInPanel = detailOpen && pillPanel.state === 'pairing';
    var TASK_WORD = { stopped: 'Stopped', expired: 'Expired', exhausted: 'Budget used', failed: 'Failed' };
    // M9 §6: labels by finding (design ResearchNote props) and the toolbar chip, for the current report only.
    var curResearch = research && report && research.reportId === report.reportId ? research : null;
    var researchNotes = OT.researchNotes(curResearch);
    if (id !== PERM_STAGE && permSeen.current !== 'loading') permSeen.current = null;
    // §6.8 check-up cards: the deck only ever holds three, so each check-up card seen in it is kept here for the
    // report's "AI check-up" group (a hidden card stays listed); ids in the check-up's own order.
    var ckIds = (curResearch && curResearch.checkup && curResearch.checkup.cards) || [];
    if (report && ckCards.current.reportId !== report.reportId) { ckCards.current = { reportId: report.reportId, byId: {} }; ckSeen.current = null; ckOnScreen.current = {}; ckTagGone.current = {}; ckFront.current = []; }
    if (fu && report && fu.reportId === report.reportId) [fu.card].concat(fu.upcoming || []).forEach(function (c) { if (c && c.checkup) ckCards.current.byId[c.id] = c; });
    var sheetCheckups = ckIds.map(function (cid, i) { var c = ckCards.current.byId[cid]; return c ? OT.checkupRow(c, i, hhmm) : null; }).filter(Boolean);
    // "New from the online check-up": cards added since the report's check-up cards were first read here, until each has
    // been on screen (or Show is used).
    if (curResearch && ckSeen.current === null) { ckSeen.current = {}; ckBase.current = {}; ckIds.forEach(function (cid) { ckSeen.current[cid] = true; ckBase.current[cid] = true; }); }
    // A check-up card added since then wears the New tag until it has been on screen (and left it).
    function isNewCard(cid) { return ckIds.indexOf(cid) >= 0 && !!ckSeen.current && !ckBase.current[cid] && !ckTagGone.current[cid]; }
    var ckNew = OT.newCheckupIds(ckIds, ckSeen.current || {});
    // Watched: the deck's cards that still wear the tag (seen when half on screen; the tag goes once it has left it).
    var ckWatch = cards.map(function (c) { return c.id; }).filter(isNewCard), ckWatchKey = ckWatch.join(',');
    React.useEffect(function () {
      if (!ckWatchKey || typeof IntersectionObserver === 'undefined') return;
      var io = new IntersectionObserver(function (es) {
        var hit = false;
        es.forEach(function (e) {
          var cid = e.target.getAttribute('data-card');
          if (e.isIntersecting) {
            ckOnScreen.current[cid] = true;
            if (ckSeen.current && !ckSeen.current[cid]) { ckSeen.current[cid] = true; hit = true; }
          } else if (ckOnScreen.current[cid] && !ckTagGone.current[cid]) { ckTagGone.current[cid] = true; hit = true; }
        });
        if (hit) setCkTick(function (n) { return n + 1; });
      }, { threshold: 0.5 });
      ckWatchKey.split(',').forEach(function (cid) { var el = document.querySelector('[data-card="' + cid + '"]'); if (el) io.observe(el); });
      return function () { io.disconnect(); };
    }, [ckWatchKey, fu]);
    // Back on a card's preview (or its Which one?): focus returns to that card's Preview change (else its first action),
    // not to the page or the ask box.
    React.useEffect(function () {
      var cid = backFocus.current; if (!cid) return;
      backFocus.current = null;
      var el = [].slice.call(document.querySelectorAll('[data-card]')).filter(function (x) { return x.getAttribute('data-card') === cid; })[0];
      var bs = el ? [].slice.call(el.querySelectorAll('.ot-insight-actions button:not(:disabled)')) : [];
      var b = bs.filter(function (x) { return x.textContent.trim() === 'Preview change'; })[0] || bs[0];
      if (b) b.focus();
    });
    // The deck in its display order (design v54): new check-up cards not seen yet come first, the rest as the host's
    // queue has them (each keeps its own number; the stored order is never changed). connection-state.js deckOrder.
    var shownIds = cards.map(function (c) { return c.id; });
    var ordered = OT.deckOrder(shownIds, ckFront.current, ckNew);
    ckFront.current = ordered.front;
    var deck = ordered.order.map(function (cid) { var i = shownIds.indexOf(cid); return { card: cards[i], i: i }; });
    // Show: the new cards are at the front of the deck; the first is scrolled to. Their New tags stay until each has been
    // on screen.
    // design v56 / host followup.front: the new cards are first moved to the front of the host's queue (so ones beyond the
    // three on screen come forward); an older host without it keeps the local order of the cards already on screen.
    function showNewCheckups() {
      var fresh = ckNew.slice(), f = fuRef.current;
      var run = function () {
        var since = deckSince();
        return OT.frontCards(function (m, prm) { return backend.call(m, prm); }, f && f.reportId, fresh).then(function (view) {
          if (view && fuRef.current && fuRef.current.reportId === view.reportId) setFu(view, since);
          fresh.forEach(function (cid) { if (ckSeen.current) ckSeen.current[cid] = true; });
          setCkTick(function (n) { return n + 1; });
          later(function () {
            // The first new card on screen, in the deck's order.
            var el = [].slice.call(document.querySelectorAll('[data-card]')).filter(function (x) { return fresh.indexOf(x.getAttribute('data-card')) >= 0; })[0];
            if (el && el.scrollIntoView) el.scrollIntoView({ block: 'start', behavior: reduce ? 'auto' : 'smooth' });
          }, 30);
        });
      };
      fuChain.current = fuChain.current.then(run, run);
    }
    var chipAi = (curProvider && aiName(curProvider.kind, curProvider.label)) || null;
    var toolbarChip = OT.toolbarChip(curResearch, { newCheckups: ckNew.length, ai: chipAi });
    var sheetRows = rows.map(function (r) { return r.fid && researchNotes[r.fid] ? Object.assign({}, r, { research: researchNotes[r.fid].verdict }) : r; });
    var grantFor = grant && report && grant.kind ? grant : null;
    // The pairing panel: "Updated" when the choices changed elsewhere while it is open.
    var grantKey = grantFor ? grantFor.kind + ':' + (grantFor.pairingId || '') : null;
    if (!grantKey) grantSeen.current = null;
    else if (!grantSeen.current || grantSeen.current.key !== grantKey) grantSeen.current = { key: grantKey, rev: perm ? perm.permissions.revision : null };
    var grantUpdated = !!(grantSeen.current && grantSeen.current.rev != null && perm && perm.permissions.revision !== grantSeen.current.rev);
    // The pairing panel (m3 §6, M9 §1.4): what a paired outside window may see and do, pre-filled from the record.
    // Checks on = every diagnostic this build has (provider.list); the choice applies to that task only.
    // Only Claude Code pairs from an outside window, so the panel names the program.
    var grantName = grantFor ? WINDOW_NAME : null;
    var grantAvail = grantFor && perm && conn.kind === grantFor.kind ? perm.available || {} : {};
    function allowPairing(v) {
      startTask({ kind: grantFor.kind, pairingId: grantFor.pairingId, grant: grantFromToggles(v, availDiag || FIX.DIAGNOSTICS) });
    }
    // The line under the ask box (in-app asks only), and Change to the page.
    var permLine = perm && inApp ? permissionLine(perm.permissions, perm.available) : null;
    var classes = summary && summary.preview ? (summary.preview.fieldClasses || []).map(function (c) { return typeof c === 'string' ? { id: c, label: c, shared: true } : c; }) : null;
    // The task's latest question, when it has its answer (the Activity panel's finished view).
    var lastAsk = task ? answers.filter(function (a) { return a.taskId === task.taskId; }).pop() || null : null;
    var lastDone = lastAsk && typeof lastAsk.text === 'string' && lastAsk.text && !lastAsk.failedTurn ? lastAsk : null;
    /* ---------- m9 §17: Fix for me on the cards, administrator checks ---------- */
    // A card's fix plan not seen yet (after a reload, or bound before its event came): read it once.
    var fixPlanIds = cards.map(function (c) { return c.fix && c.fix.planId; }).filter(function (x) { return x && !plans[x]; });
    React.useEffect(function () {
      fixPlanIds.forEach(function (pid) {
        if (planLoads.current[pid]) return;
        planLoads.current[pid] = true;
        // Review 7: a failed read is tried again at the next reconciliation (deck re-read, reconnect).
        backend.call('action.get', { planId: pid }).then(function (plan) { putPlan(pid, { plan: plan, op: plan.operation || null, kind: 'agent' }); }, function () { delete planLoads.current[pid]; });
      });
    }, [fixPlanIds.join(','), planTick]);
    // The wait after a no counts down once a second (design AdminChecks wait), and goes back to ask at 0.
    var ckWaiting = Object.keys(checkReqs).some(function (k) { var q = checkReqs[k]; return q.state === 'awaiting_user' && q.retryAt && Date.parse(q.retryAt) > Date.now(); });
    React.useEffect(function () {
      if (!ckWaiting) return;
      var t = setInterval(function () { setTick(function (n) { return n + 1; }); }, 1000);
      return function () { clearInterval(t); };
    }, [ckWaiting]);
    // Fix for me is offered (design FixForMe) only with an AI that has tools, on the open cards of the deck.
    var fixTools = inApp && !!report && !!perm && !!perm.available && perm.available.tools !== false;
    var fixChanges = perm && perm.permissions && perm.permissions['do'] ? perm.permissions['do'].changes : 'off';
    function checksProps(requestId) {
      var v = FIX.checksView(checkReqs[requestId], Date.now()) || { state: 'ask', cause: null, secondsLeft: null, checks: [] };
      return { checks: v.checks, checksState: v.state, checksCause: v.cause || undefined, secondsLeft: v.secondsLeft || undefined, unread: v.unread,
        onRunChecks: function () { approveChecks(requestId); }, onDeclineChecks: function () { declineChecks(requestId); } };
    }
    // card.fix → the card's FixPanel props (fix-plan.js fixPanelState), or null: nothing to show (no fix, or its plan not read
    // yet, or a script, which stays beside the chat).
    function cardFixProps(card, opts) {
      var cf = card.fix, ps = FIX.fixPanelState(cf);
      if (!ps) return null;
      var cid = card.id, ai = cf.by || aiLabel || 'Claude';
      var base = { state: ps.state, ai: ai };
      if (ps.state === 'working') {
        // Design v82/v83: how the card's checks ended, under the working line, while the AI goes on.
        var cr = FIX.checksResult(checkReqs[cardChecks.current[cid]]);
        // Review 5: Stop stops the run of the task that owns this fix (card.fix.taskId); off until that is known.
        var owner = cf.taskId || null;
        return Object.assign(base, { checksResult: cr ? cr.result : undefined, checksCause: cr && cr.cause ? cr.cause : undefined, onStop: owner ? function () { cancelRun(owner); } : null });
      }
      // Review 1: a request not read yet (after a reload, before checks.list answers) is not shown as an empty one.
      if (ps.state === 'checks' || ps.state === 'checksRunning') return checkReqs[cf.requestId] ? Object.assign(base, checksProps(cf.requestId)) : null;
      if (ps.state === 'blocked') {
        // Design v82: the cause sets the sentence; only adminOff offers Permissions; every cause keeps Fix for me.
        var cause = FIX.blockedCause(cf.code);
        return Object.assign(base, { cause: cause, onPermissions: cause === 'adminOff' ? function () { openPermissions(id, true); } : null, onRetry: function () { fixAsk(cid); } });
      }
      if (ps.state === 'needsYou') {
        var rc = rechecks[cid] || {};
        return Object.assign(base, { note: opts.note, checking: !!rc.checking, recheck: rc.recheck || undefined,
          onCheckAgain: opts.findingId ? function () { if (!(rechecks[cid] || {}).checking) recheckCard(cid, opts.findingId); } : null, onClose: function () { dismissFix(cid); } });
      }
      if (ps.state === 'nothing') return Object.assign(base, { note: opts.note, onClose: function () { dismissFix(cid); } });
      if (ps.state === 'noFix') return Object.assign(base, { onRetry: function () { fixAsk(cid); } });
      var e = cf.planId ? plans[cf.planId] : null;
      if (!e || !e.plan) return null;
      var pid = cf.planId;
      // Design v82/v84 (re-check 3): a card-bound script lives beside the chat in every state (it says truthfully how it went);
      // the card only points to it: waiting, running or ended. Never the generic plan card ("Nothing changed" is not known).
      if (e.plan.kind === 'script') return Object.assign(base, { state: 'script', scriptState: ps.state === 'scriptOnly' ? 'ended' : FIX.scriptPointer(e.plan, e.op), onShowScript: function () { showScript(pid); } });
      if (ps.state === 'scriptOnly') return null;   // stopped, not a script: no panel on the card
      var plan = planCardProps(Object.assign({ planId: pid }, e), {
        leftOut: FIX.leftOutView(e.plan), removed: FIX.removedLabels(e.plan),
        onPermissions: function () { openPermissions(id, true); },
        onApprove: function (kept) { approveInCard(pid, kept); },
        onDecline: function () { declinePlan(pid); },
        // "Change something…": this plan is withdrawn and the card goes back to working (never while Windows asks).
        onChangeRequest: function (text) { if (!(plansRef.current[pid] || {}).approving) fixAsk(cid, text, pid); },
        // A verified fix closes the card (the host); Next goes on as a card's own change does.
        onNext: ps.state === 'verified' ? function () { leave(cid, 'done'); } : null,
        onClose: ps.state === 'verified' ? null : function () { dismissFix(cid); } });
      delete plan.requestedBy; if (!plan.from) delete plan.from;
      plan.key = pid;   // review 6: a replacement plan is a new card (its step switches start all on)
      return Object.assign(base, { plan: plan });
    }
    // Requests not of a card on screen (a question beside the chat, a paired window, or a card further down the deck); a card's
    // own request shows in that card while card.fix waits for it, and never moves here.
    var sideChecks = Object.keys(checkReqs).map(function (k) { return checkReqs[k]; }).filter(function (q) {
      return !q.hidden && !(q.cardId && deckIds[q.cardId]);
    });
    var agentPanels = function () {
      return sideChecks.map(function (q) {
        var v = FIX.checksView(q, Date.now());
        // As the design's AdminRights page shows it beside the chat: in a glass panel.
        return h(GlassPanel, { key: 'checks:' + q.requestId, className: 'ot-enter ot-checks-side', style: { padding: 16 } }, h(OT.AdminChecks, { state: v.state, cause: v.cause || undefined, secondsLeft: v.secondsLeft || undefined,
          unread: v.unread, ai: aiLabel || undefined, checks: v.checks, onRun: function () { approveChecks(q.requestId); }, onDecline: function () { declineChecks(q.requestId); } }));
      }).concat(sidePlans.map(function (e) {
        var pl = e.plan;
        var close = function () { putPlan(e.planId, { closed: true }); };
        // ot-plan-<id>: a card's Show script finds it.
        // Re-check 3: closing a card's ended script also ends the card's fix (fix.dismiss); the card is back to normal.
        // It is hidden once the dismissal succeeded (a failed one leaves it shown). Review: only the card's current script
        // ends the card's fix; an older one (reopened from Activity) only closes.
        var closeScript = pl.cardId && FIX.scriptPointer(pl, e.op) === 'ended' && cardFixPlan(pl.cardId) === e.planId ? function () {
          if ((plansRef.current[e.planId] || {}).closing) return;
          putPlan(e.planId, { closing: true });
          dismissFix(pl.cardId, function (ok) { putPlan(e.planId, ok ? { closing: false, closed: true } : { closing: false }); });
        } : close;
        if (pl.kind === 'script') return h(ScriptApproval, scriptCardProps(e, { key: 'plan:' + e.planId, className: 'ot-enter ot-plan-' + e.planId, onClose: closeScript }));
        // A fix, or a single change an assistant asked for (or an Undo): the approval card (a single change is one step).
        var extra = { key: 'plan:' + e.planId, className: 'ot-enter', onClose: close };
        // Undo of a single change: its own card replaces this one.
        if (pl.kind !== 'fix') extra.onUndoStep = function () { close(); undoOperation(e.op); };
        return h(ApprovalCard, planCardProps(e, extra));
      }).concat([
        pairing && id !== 'login' && !pairInPanel && h(PairingCard, { key: 'pair', className: 'ot-enter', client: WINDOW_NAME, code: pairing.code, busy: pairBusy,
          onPair: function () { decidePairing(true); }, onCancel: function () { decidePairing(false); } }),
        grantFor && h(PermissionGrant, { key: 'grant:' + grantFor.kind + ':' + (perm ? perm.permissions.revision : 0), className: 'ot-enter', client: grantName,
          preset: permToggles(perm ? perm.permissions : permDefaults()), webUnavailable: grantAvail.web === 'no', adminUnavailable: grantAvail.admin === false, provider: grantName,
          showScripts: false, busy: !!grantFor.busy, updated: grantUpdated, onCancel: function () { setGrant(null); }, onContinue: allowPairing }),
        // design v72: once the task's latest question is answered and nothing runs, the finished view (the steps folded).
        // A typed question's answer panel carries the answer, so the panel gets none: the question is its headline. The
        // first look (no typed goal) keeps its answer under “First look at your PC”.
        showActivity && task && h(ActivityPanel, { key: 'act', className: 'ot-enter', title: aiName(task.recipient.kind, task.recipient.label) + (taskActive ? '' : ' · ' + (TASK_WORD[task.status] || task.status)),
          finished: !!lastDone && !runInProgress, agent: aiName(task.recipient.kind, task.recipient.label), goal: lastDone && !lastDone.scan ? lastDone.q : null,
          answer: lastDone && lastDone.scan && !runInProgress ? OT.runAnswer(lastDone.text, (lastDone.citations || []).map(citeChip), openCite) : null,
          active: taskActive, items: actItems, nativeClient: !!(task.recipient && /_mcp$/.test(task.recipient.kind || '') && task.recipient.mode !== 'headless'), used: task.used, limits: task.grant && task.grant.limits, highlight: actHit,
          // Stop only while a turn or run is in progress, and it cancels that run (agent.cancel), not the task.
          onStop: runInProgress ? cancelRun : null, onClose: function () { setShowActivity(false); setActHit(null); } })]));
    };

    var toastEl = toast && h('div', { className: 'ot-toast text-mono-sm ot-fade', role: 'status' }, toast);
    // A setup step moves on only once it is saved (saveOnb shows "Not saved" otherwise).
    function saveStep(patch, next, after) {
      if (stepSaving.current) return;
      stepSaving.current = true;
      saveOnb(patch).then(function () { stepSaving.current = false; if (after) after(); go(next); }, function () { stepSaving.current = false; });
    }
    var body = null;
    var notice = OT.backendNotice(bstate, bdetail);   // "Update Optimus" / "Optimus is open in another session"
    if (notice || (failed && id === 'boot')) {
      body = h('div', { key: 'down', className: 'ot-flow-panel ot-enter' }, h(GlassPanel, { strong: true, className: 'ot-choice ot-frost' },
        h('h2', { className: 'text-title-2' }, notice || 'Backend unavailable'),
        !notice && h('div', { className: 'ot-choice-foot' }, h(Button, { variant: 'primary', onClick: boot }, 'Retry'))));
    } else if (id === 'login') {
      body = h('div', { key: 'login', className: cls('ot-flow-panel') }, h(LoginPanel, {
        // A retired saved AI selects nothing; otherwise the saved choice's tile (Claude when local).
        initialProvider: conn.retired ? null : isLocal ? 'claude' : OT.pickerTile(conn.value),
        // Back leaves everything as it was: a half-made connection on the chooser is dropped.
        onBack: returnTo ? function () { var r = returnTo; setReturnTo(null); setConnect(null); go(r); } : null,
        initialApi: conn.kind === 'openai_api' ? 'openai' : 'anthropic', connect: connect, pairBusy: pairBusy,
        onApprove: function () { decidePairing(true); }, onDeny: function () { decidePairing(false); },
        onContinue: function () { if (connect) commitProvider({ value: connect.tile, label: connect.label || conn.label, kind: connect.kind }); },
        onCopyFail: function () { flash('Copy failed'); },
        tileStates: tileStates, waitLong: waitLong,
        onTile: checkClient, onClientAction: clientAction, clientViews: clientViews, tileStatus: tileStatus,
        onLater: function () { chooseProvider('later', 'Later'); },
        onConnect: chooseProvider }),
        toastEl);
    } else if (id === PERM_STAGE) {
      // (permSeen is cleared below whenever the page is not showing.)
      // M9 §1.4: the same page after the chooser, at boot, and from the pill (Back and Continue return where it came from).
      var pr = perm || { permissions: permDefaults(), available: {} }, pAvail = pr.available || {};
      var pName = (curProvider && aiName(curProvider.kind, curProvider.label)) || (isLocal ? null : aiName(conn.kind, conn.label)) || 'your AI';
      if (permSeen.current == null && perm) permSeen.current = perm.permissions.revision;
      var permUpdated = permSeen.current != null && permSeen.current !== 'loading' && !!perm && perm.permissions.revision !== permSeen.current;
      body = h('div', { key: PERM_STAGE, className: cls('ot-flow-panel') }, h(PermissionsPanel, { key: 'perm:' + pr.permissions.revision,
        updated: permUpdated, focusTitle: permFocus.current,
        // design v70: the note over the page only the first time it is finished (connection-state.js permSkipNote).
        skipNote: OT.permSkipNote(perm && perm.permissions),
        agent: pName, provider: pName, initial: permToggles(pr.permissions), explainOnly: pAvail.tools === false, webUnavailable: pAvail.web === 'no',
        // m9 §17.1 (design v75): the administrator row, disabled with its grey line while not available in this version.
        adminUnavailable: pAvail.admin === false,
        showScripts: !!pAvail.scripts, busy: permBusy, onBack: function () { leavePermissions(true); }, onContinue: savePermissions }), toastEl);
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
          placeholder: onMain && report ? 'Ask or reply…' : 'What would you like to check?',
          onSubmit: submitMain }),
        // M9 §1.4: what the AI may do, in one line; Change opens the permissions page.
        onMain && permLine && h(PermissionsSummary, { className: 'ot-perm-under ot-enter', text: permLine, onChange: function () { openPermissions(id, true); } }),
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
            // Scan opens its menu: Full scan or Benchmark (M6a).
            h(Button, { variant: 'neu', size: 'sm', icon: 'repeat', id: 'ot-scan-btn', 'aria-haspopup': 'true', 'aria-expanded': menu === 'scan', 'aria-controls': 'ot-scan-menu',
              className: menu === 'scan' ? 'is-on' : null, 'aria-label': 'Scan', title: 'Scan', onClick: function () { setMenu(menu === 'scan' ? null : 'scan'); } }, tbLabel('Scan')),
            report && h(Button, { variant: 'neu', size: 'sm', icon: 'report', 'aria-label': 'Report', title: 'Report', onClick: function () { setMenu(null); setSheet('open'); } }, tbLabel('Report'))),
          // M9 §6.2: while the report's research runs (design Toolbar `researching`); nothing once it ends.
          toolbarChip && h(OT.ToolbarStatus, Object.assign({ onShowNew: showNewCheckups }, toolbarChip)),
          menu === 'suggest' && h(OT.SuggestionList, { id: 'ot-suggest-menu', toggleId: 'ot-suggest-btn', items: starters, onClose: function () { setMenu(null); },
            onPick: function (s) { setMenu(null); runAudit(s.text, s.intent); } }),
          menu === 'history' && h(OT.HistoryList, { id: 'ot-history-menu', toggleId: 'ot-history-btn', rows: historyRows, onClose: function () { setMenu(null); },
            onOpen: function (r) { if (r.bench) { setMenu(null); bench.openRun(r.bench); focusBench(300); } else showPanel(r.id); }, onDelete: deleteAnswer }),
          menu === 'scan' && h(OT.ScanMenu, { id: 'ot-scan-menu', toggleId: 'ot-scan-btn', benchRunning: bench.active, onClose: function () { setMenu(null); },
            onPick: function (pick) { setMenu(null); if (pick === 'full') fullScan(); else openBench(); } })),
        // Report a problem: the prompt under the toolbar (in the page flow), then the preview like the other panels.
        onMain && problems.prompt('ot-enter'),
        onMain && problems.panel('ot-enter'),
        onMain && bench.open && h(OT.BenchPanel, { key: 'bench', className: 'ot-enter', view: bench.view, caps: bench.caps, run: bench.run, warn: bench.warn, toast: bench.toast,
          starting: bench.starting, cancelling: bench.cancelling, onStart: bench.start, onCancel: bench.cancel, onClose: bench.close,
          onInvestigate: benchAi ? investigate : null, onSaveImage: saveBenchImage }),
        onMain && agentPanels(),
        onMain && summary === 'connect' && h(GlassPanel, { className: 'ot-share ot-enter' },
          h('div', { className: 'text-headline' }, 'Summaries use an AI'),
          h('div', { className: 'ot-choice-foot' },
            h(Button, { variant: 'ghost', onClick: function () { setSummary(null); } }, 'Not now'),
            h(Button, { variant: 'primary', iconRight: 'chevron', onClick: function () { setReturnTo('main'); setWave({ speed: 0.7, words: [] }); go('login'); } }, 'Choose AI'))),
        onMain && classes && h(SharePreview, { className: 'ot-enter', agent: (curProvider && aiName(curProvider.kind, curProvider.label)) || conn.label, classes: classes, size: kib(summary.preview.bytes || 0),
          onCancel: function () { setSummary(null); }, onShare: share }),
        onMain && summary === 'continue' && h(ContinuePanel, { className: 'ot-enter', client: WINDOW_NAME, prompt: CONTINUE_PROMPT,
          onCopyFail: function () { flash('Copy failed'); }, onClose: function () { setSummary(null); } }),
        // m5 §9: each question its own panel, newest first. A follow-up opens a new panel above (same conversation).
        onMain && panels.map(function (a) {
          var mine = !!task && a.taskId === task.taskId;
          // M9 §2 (design GuideAnswer): the built-in guide's answer, with Connect AI.
          if (a.local) return h(OT.GuideAnswer, { key: a.id, answerId: a.id, className: 'ot-enter', question: a.q, text: a.text,
            onClose: function () { setOpen([a.id], false); },
            onConnect: function () { setDetailOpen(false); setReturnTo(id); setWave({ speed: 0.7, words: [] }); go('login'); } });
          return h(OT.AnswerPanel, { key: a.id, className: 'ot-enter', item: a, onCite: openCite, onOpenSource: openSource,
            onFollow: inApp ? function (q) { followUp(a, q); } : null, followBusy: runInProgress,
            onStop: mine && runInProgress ? cancelRun : null, onClose: function () { setOpen([a.id], false); }, onBenchmark: openBench });
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
          ? h(OT.CardGrid, { className: cx('ot-cards', 'n-' + cards.length, openId && 'has-open'), openKey: openId }, deck.map(function (d) {
            var card = d.card, ci = d.i;
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
            // §6.8: a check-up card (the design's CheckupCard): Ask about this and Hide, sources by id, no preview.
            var checkup = aiCard ? OT.checkupOf(curCard) : null;
            // An AI card cites the evidence of the task that added it: shown once fetched, or as no longer available.
            if (aiCard) curEvidence = (card.evidenceIds || []).concat(curDetails ? curEvidence : []).filter(function (x, i, a) { return a.indexOf(x) === i; });
            var aiWhy = aiCard ? curCard.why || (curCard.text !== curCard.title ? curCard.text : null) : null;
            // m9 §17.3 (design FixForMe, FixStates): the card's fix from card.fix; the button while nothing else is on it.
            var cardNote = typeof curCard.note === 'string' && curCard.note ? curCard.note : null;
            var fixPanel = cardFixProps(curCard, { note: cardNote, findingId: !aiCard && !checkup ? curCard.findingId || null : null });
            var opinion = !!(fixPanel && (fixPanel.state === 'needsYou' || fixPanel.state === 'nothing'));
            // Coordinator (e) / design v82: a fix turn's reply is not shown under a card that shows a fix panel (it stays in the chat).
            if (cardAnswer && cardAnswer.fix && fixPanel) cardAnswer = null;
            var fixAvail = curCard.fix && !opinion ? null : FIX.fixAvailability({ tools: fixTools, open: true, changes: fixChanges, busy: runInProgress });
            return h(InsightCard, { key: curId, cardKey: curId, className: cid === openId ? 'is-open' : null, category: curCard.category,
              isNew: isNewCard(cid),
              // An AI card: its title is the claim and its why the observation (the host sends text = why).
              text: aiCard ? curCard.title || curCard.text : curCard.text,
              onFocus: function () { focusCard(cid); }, onCollapse: cid === openId && canFold ? foldCards : null,
              observation: aiCard ? aiWhy : evById[(curCard.evidenceIds || [])[0]] ? evById[curCard.evidenceIds[0]].display : curCard.why || null,
              // m5 §5: the AI's note (plain text, shown as text only) and the AI tag on cards it added.
              // With the AI's opinion shown in the fix panel, its note is shown there (once).
              note: opinion ? null : cardNote, noteBy: aiLabel, aiTag: curCard.origin === 'ai',
              ai: (curCard.fix && curCard.fix.by) || aiLabel || undefined, fix: fixPanel, fixAvailable: fixAvail || undefined,
              onFix: function () { fixAsk(cid); }, onPermissions: function () { openPermissions(id, true); },
              // M9 §6.2: the research label and its note; the card, its severity and its options stay as they are.
              research: !aiCard && card.findingId && researchNotes[card.findingId] ? Object.assign({ onOpenSource: openSource }, researchNotes[card.findingId]) : null,
              checkup: checkup,
              onOpenSource: openSource, replySources: cardAnswer && cardAnswer.text ? cardAnswer.sources : null,
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
              ignoreChoices: aiCard ? null : ignoreChoices(curFinding ? curFinding.ruleId : '', curCard.category),
              onIgnore: function (choice) { ignoreAs(curCard, choice); }, hideLabel: aiCard ? 'Hide' : null,
              options: cardOptions(curCard, curFinding), guide: curCard.guide,
              // Every change in a card is the approval card inside it: a fix with its steps, a single change as one step
              // (preview, Defender scans, can't run, result, Undo).
              change: curPlan && curPlan.kind !== 'script' ? planCardProps(Object.assign({ planId: curState.planId }, curPlanEntry), {
                // No Undo offered for a verified change: the manual route (the finding's Settings page), when there is one.
                manualUri: curSettings,
                onApprove: function () { approvePlan(curState.planId, curId); },
                onDecline: function () { declinePlan(curState.planId); leave(curId, 'decline'); }, onNext: function () { leave(curId, 'done'); },
                // Can't run: Windows Settings is the way; the finding's guided card then offers Check again (design v59: no
                // steps mode on the card itself).
                onPlanSettings: function (uri) { openSettings(uri); if (curCard.findingId && curSettings) previewAction(curCard, 'manual.open'); },
                // Close (Dismiss when expired) drops the plan and the card is as before.
                onClose: function () { putPlan(curState.planId, { closed: true }); setMode(curId, null); unfocus(curId); },
                // Back (design v54): leaves the preview without a decision; the unused plan is left to expire (never declined).
                onBack: curPlan.status === 'awaiting_user' || FIX.isGuided(curPlan) ? function () { setMode(curId, null); unfocus(curId); backFocus.current = curId; } : null,
                guide: curCard.guide || null, guideTitle: curFinding ? curFinding.title : null,
                // Design v59: the Settings path the host sends beside the page (English Windows only), shown as the step's `does`.
                settingsPath: curCard.settingsPath || (curFinding && curFinding.settingsPath) || null }) : null,
              // M9 §3.6: which entry or process, when the finding cites several (then Preview change with that one).
              pick: curState.mode === 'pick' && curState.pick ? { choices: curState.pick.choices,
                onPick: function (evidenceId) { previewAction(curCard, curState.pick.actionId, evidenceId); },
                onBack: function () { setMode(curId, null); backFocus.current = curId; } } : null,
              mode: curPhase, reply: cardAnswer ? (cardAnswer.failedTurn ? failOf(cardAnswer) : cardAnswer.text) : replyText(curCard.reply),
              // M6a §8: the card's AI answer keeps its voltage note and Run benchmark, as an answer panel does.
              replyRisk: !!(cardAnswer && cardAnswer.text && (cardAnswer.warnings || []).indexOf('voltage') >= 0),
              onReplyBenchmark: cardAnswer && cardAnswer.text && cardAnswer.suggest === 'benchmark' ? openBench : null,
              // A report-only answer (no AI in the app): offer the chooser once, under the answer.
              onReplyConnect: !cardAnswer && curCard.reply && !inApp && !nativeLive && !isMcpKind(conn.kind) ? function () { setDetailOpen(false); setReturnTo(id); setWave({ speed: 0.7, words: [] }); go('login'); } : null,
              // A fix ask's work shows in the fix panel, not as Thinking… under the card.
              replyBusy: cardAnswer && cardAnswer.text == null && !cardAnswer.failedTurn && !cardAnswer.fix ? cardAnswer.busy || 'Thinking…' : null,
              replyCites: cardAnswer && cardAnswer.text ? (cardAnswer.citations || []).map(citeChip) : null, leaving: cardLeaving === cid, askToken: askToken.cid === cid ? askToken.n : 0, askRestore: askRestore && askRestore.cid === cid ? askRestore : null,
              index: total - fu.pendingCount + 1 + ci, total: total,
              onOption: function (o) { onOption(curId, o); }, onAsk: function (t) { ask(curId, t); }, onCite: function (eid) { openCite(eid, aiTask); } });
          }))
          : h(ReviewComplete, { key: 'complete', className: 'ot-enter', items: reviewed,
              onUndo: function (cid) { var e = cardOp(cid); if (e && e.op.undo && e.op.undo.available) undoOperation(e.op); },
              onRestore: restoreItem })),
        null);
    }

    return h('div', { className: cx('ot-flow', p.className) },
      // The sea pauses while the full-screen report covers it.
      h(WaveBackground, { speed: wave.speed, words: wave.words, paused: !!sheet && !toLogin }),
      // The top row is the page's first row (inside the scroller): it scrolls with the page, never fixed over content.
      h('div', { className: cx('ot-flow-stage', id === 'login' || id === PERM_STAGE ? 'is-login' : null, signedIn && 'has-top') },
        signedIn && h('div', { className: cx('ot-flow-top', toLogin ? 'ot-sink' : 'ot-enter') },
        problems.menu(),
        // M1 makes no AI connection, so the pill never says Connected.
        // Status only as the backend reports it (provider.list / provider.connect / pairing events).
        // A paired native client working on an active task is the live connection, whatever the chooser says.
        // §9: the label from connectionState; v86: the word and tone are the panel's (pillStatus of pillPanelState).
        h(ConnectionPill, { provider: pillState.label, status: pillWord.status, detail: pillWord.detail,
          onOpen: function () { setDetailOpen(!detailOpen); }, open: detailOpen,
          // Change works anywhere except mid-scan; Back on the login returns to this exact step.
          onChange: id === 'scanning' || stage.leaving ? null : function () { setReturnTo(id); setWave({ speed: 0.7, words: [] }); go('login'); } })),
      // design v86 (PillPanelStates): the panel's state from the connection (pillPanelState); Local only opens it too.
      signedIn && detailOpen && h('div', { className: 'ot-conn-wrap' }, h(ConnectionDetail, { key: pillKind || 'local', className: 'ot-enter', label: pillState.label,
        panel: pillPanel, pairBusy: pairBusy,
        command: pillState.command ? regs.current[pillKind] || null : null, onCopy: function () { return copyCommand(regs.current[pillKind]); },
        onAction: function (name) { pillAction(name, pillKind); },
        // Disconnect asks first in the panel; the call is sent once. Hidden where the state says so, mid-scan and while something runs.
        busy: id === 'scanning' || working,
        onDisconnect: pillKind ? function () { return disconnectKind(pillKind); } : null,
        // M9 §1.4: the permissions page, with Back, as Change opens the chooser.
        onPermissions: id === 'scanning' || !perm ? null : function () { openPermissions(id, true); },
        onChange: id === 'scanning' ? null : function () { setDetailOpen(false); setReturnTo(id); setWave({ speed: 0.7, words: [] }); go('login'); } })),
      // Away from the main page, the report preview opens under the top row.
      signedIn && id !== 'main' && problems.panel('ot-enter ot-rp-top'),
      body),
      peek && h(OT.EvidencePeek, { label: peek.label, ev: peek.ev, missing: peek.missing, when: peek.ev ? observedLabel(peek.ev) : null,
        onReport: peek.inReport ? function () { var id = peek.id; setPeek(null); setSheet(rows.some(function (r) { return r.id === id; }) ? id : 'open'); } : null,
        onClose: function () { setPeek(null); } }),
      sheet && h(ReportSheet, { className: toLogin ? 'ot-sink' : null, rows: sheetRows, categories: report ? report.categories : null, fixed: fixed, when: whenLabel(report), highlight: sheet === 'open' ? null : sheet, summary: summaryLines,
        checkups: sheetCheckups, onOpenSource: openSource,
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
