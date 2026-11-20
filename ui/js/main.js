// Boots the app on the selected backend (desktop bridge inside the WebView2 shell, fixture in a browser).
(function () {
  var OT = window.OptTool;
  OT.backend = OT.selectBackend();
  var startup = Promise.resolve();   // fixture: a ?fixtureBackend state is applied before the loading screen is wired
  if (OT.backend.kind === 'fixture') {
    // Dev hook (website/fixture only): show any §9 connection state offline.
    //   OT_fixture.setProviderState('anthropic_api', 'error', 'auth')   ·   ?fixtureState=claude_code_mcp:client_closed
    var set = function (kind, status, detail, code) { return OT.backend.call('fixture.setProviderState', { kind: kind, status: status, detail: detail || null, code: code || null }); };
    // Startup states (protocol-v1 §7):  OT_fixture.setBackendState('connecting', 'needs_update')  ·  ?fixtureBackend=connecting:cant_start[:recover]
    var setBackend = function (state, detail, recover) { return OT.backend.call('fixture.setBackendState', { state: state, detail: detail || null, recoverOnRetry: !!recover }); };
    // M9: what the selected provider offers on the permissions page:  OT_fixture.setAvailable({ tools: false, web: 'no', scripts: true })
    var setAvailable = function (a) { return OT.backend.call('fixture.setAvailable', a || {}); };
    // M9 §4 fix plans:  OT_fixture.proposeFix('risky')  (startup | risky | ineligible | conflict | admin)  ·  OT_fixture.failStep(3, 'indeterminate')
    //   OT_fixture.setApproval({ tick: false })  ·  OT_fixture.resetFixPc()
    //   OT_fixture.setAdminAvailable(true)  administrator steps (off by default, as the host: such a fix is ineligible, admin_unavailable;
    //   on, 'risky' gets its admin step, run last)
    var proposeFix = function (sample) { return OT.backend.call('fixture.proposeFix', typeof sample === 'object' ? { args: sample } : { sample: sample || 'startup' }); };
    var failStep = function (n, outcome) { return OT.backend.call('fixture.failStep', { n: n, outcome: outcome || 'failed' }); };
    var setApproval = function (a) { return OT.backend.call('fixture.setApproval', a || {}); };
    var resetFixPc = function () { return OT.backend.call('fixture.resetFixPc'); };
    var setAdminAvailable = function (on) { return OT.backend.call('fixture.setAdminAvailable', { available: on !== false }); };
    // M9 §6 research:  OT_fixture.setResearch('running', 3, 7)  ·  OT_fixture.setResearch('running', 0, 3, true)  (the trial)
    //   OT_fixture.setResearchVerdict('RemoteHelper.exe', 'worth_a_look')  (null: no label)  ·  OT_fixture.setResearch({ trialOutcome: 'no' })
    //   next run ends partial or failed:  OT_fixture.setResearch({ budgetCalls: 1 })  ·  ({ failCalls: 1 })  ·  ({ failCalls: 'all' })
    var setResearch = function (status, done, total, trial) { return OT.backend.call('fixture.setResearch', typeof status === 'object' ? status : { status: status, done: done || 0, total: total || 0, trial: !!trial }); };
    var setResearchVerdict = function (name, verdict, basis) { return OT.backend.call('fixture.setResearch', { label: { name: name, verdict: verdict === undefined ? 'considered_safe' : verdict, basis: basis || null } }); };
    // M9 §6.8 check-up:  OT_fixture.checkupTick(7)  (moves the clock on 7 days and runs the hourly check: a check-up starts once a week)
    //   OT_fixture.setCheckup({ status: 'running' })  (off | idle | running | done | partial | failed | unavailable; null clears)
    //   next check-up:  OT_fixture.setCheckup({ failCalls: 1 })  ·  ({ failCalls: 'all' })  ·  ({ budgetCalls: 1 })  ·  ({ outcome: 'unsupported' })
    //   OT_fixture.getCheckup()  what the last check-up’s five calls were sent
    var checkupTick = function (days) { return OT.backend.call('fixture.checkupTick', { days: days || 0 }); };
    var setCheckup = function (o) { return OT.backend.call('fixture.setCheckup', o || {}); };
    var getCheckup = function () { return OT.backend.call('fixture.getCheckup'); };
    // M9 §5 scripts (simulated; nothing runs):  OT_fixture.proposeScript('plain')  (plain | warnings | elevated | long | refused | non_ascii,
    //   or {title, why, script, parts, elevated}; a second argument names a task, which applies its gates)
    //   OT_fixture.setScriptRun({ outcome: 'failed' })  (completed | failed | timeout | interrupted | not_started | declined | helper; truncated, bigOutput, seconds,
    //   policy: bypass | default | managed, parser: false → unchecked)
    //   As the host until the broker batch, elevated scripts are refused and no restore point is offered (flag_off):
    //   OT_fixture.setAdminAvailable(true) lets elevated ones through; OT_fixture.setRestorePoint({ available: 'available' | 'protection_off', result: 'failed' })
    //   OT_fixture.getOperationTool(operationId)  (or { planId }): get_operation as the AI gets it
    //   OT_fixture.setAvailable({ scripts: false }) stands in for a build without scripts (the row hides; propose_script is refused).
    var proposeScript = function (sample, taskId) { return OT.backend.call('fixture.proposeScript', Object.assign(typeof sample === 'object' ? { args: sample } : { sample: sample || 'warnings' }, taskId ? { taskId: taskId } : {})); };
    var setScriptRun = function (o) { return OT.backend.call('fixture.setScriptRun', o || {}); };
    var setRestorePoint = function (o) { return OT.backend.call('fixture.setRestorePoint', o || {}); };
    var getOperationTool = function (id) { return OT.backend.call('fixture.getOperationTool', typeof id === 'object' ? id : { operationId: id }); };
    // Design v54 one-step cards:  OT_fixture.setLongScan()  the next Defender scan takes ~8 s with progress (Stop ends it)
    //   OT_fixture.failSingle()  the next single change fails (Check again, Open settings)
    //   guided task: View steps, or Preview change on a card without a catalog action (manual.open); Check again is "still the same" once, then verified
    var setLongScan = function (on) { return OT.backend.call('fixture.setLongScan', { long: on !== false }); };
    var failSingle = function () { return OT.backend.call('fixture.failSingle'); };
    // Design v56/v59 report-only results:  OT_fixture.requestAction('repair.sfc', findingId)  (repair.dism_restorehealth | network.flush_dns | explorer.restart)
    //   OT_fixture.setReportResult('could_not_repair')  the next one reports this: sfc finished | failed, explorer started | not_running | failed … (see adapter-fixture-actions.js)
    //   OT_fixture.setWindowsLanguage(false)  Windows in another language: no Settings path on guided steps (plans now, cards from the next scan)
    var requestAction = function (actionId, findingId) { return OT.backend.call('fixture.requestAction', { actionId: actionId, findingId: findingId || null }); };
    var setReportResult = function (result) { return OT.backend.call('fixture.setReportResult', { result: result || null }); };
    var setWindowsLanguage = function (english) { return OT.backend.call('fixture.setWindowsLanguage', { english: english !== false }); };
    window.OT_fixture = { setLongScan: setLongScan, failSingle: failSingle, requestAction: requestAction, setReportResult: setReportResult, setWindowsLanguage: setWindowsLanguage,
      setProviderState: set, setBackendState: setBackend, setAvailable: setAvailable,
      proposeFix: proposeFix, failStep: failStep, setApproval: setApproval, resetFixPc: resetFixPc, setAdminAvailable: setAdminAvailable,
      setResearch: setResearch, setResearchVerdict: setResearchVerdict, checkupTick: checkupTick, setCheckup: setCheckup, getCheckup: getCheckup,
      proposeScript: proposeScript, setScriptRun: setScriptRun, setRestorePoint: setRestorePoint, getOperationTool: getOperationTool };
    var m = /[?&]fixtureState=([^&]+)/.exec(location.search);
    if (m) decodeURIComponent(m[1]).split(',').forEach(function (one) { var a = one.split(':'); set(a[0], a[1], a[2], a[3]).catch(function () {}); });
    var b = /[?&]fixtureBackend=([^&]+)/.exec(location.search);
    if (b) { var bp = decodeURIComponent(b[1]).split(':'); startup = setBackend(bp[0], bp[1], bp[2] === 'recover').catch(function () {}); }
  }
  ReactDOM.createRoot(document.getElementById('root')).render(React.createElement(OT.ErrorBoundary, null, React.createElement(OT.AppFlow, { backend: OT.backend })));
  startup.then(function () { OT.wireLoading(OT.backend, window.OT_loading); });
})();
