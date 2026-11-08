/* OptTool.backend: the one interface the UI talks to (protocol v1, contracts/protocol-v1.md).
   Two implementations: the desktop adapter (adapter-desktop.js, §7 bridge messages to the WebView2 shell) and the
   fixture adapter (adapter-fixture.js, in-browser simulation of the same methods and events).
   Selection: desktop when window.chrome.webview exists, else fixture; ?adapter=fixture forces the fixture. */

/**
 * @typedef {'connecting'|'ready'|'reconnecting'|'incompatible'} BackendState
 * @typedef {{previous:(BackendState|null), detail:any, resumed:boolean}} StateInfo  resumed: reconnecting → ready; re-read snapshots
 * @typedef {Error & {code:number, data:any}} BackendError  JSON-RPC error (§3): -32002 stale_revision carries data.current
 *
 * @typedef {{stream:string, seq:number, type:string, at:string, payload:object}} EventEnvelope  §5 events.notify params
 *   types: scan.progress {done,total,current} · scan.coverage {collectorId,status} · scan.finished {status,reportId}
 *          inventory.fact {kind,label} · inventory.finished {status}
 *
 * @typedef {{value:('local'|'later'|'claude-code'|'codex'|'api'), label:string}} ProviderChoice
 * @typedef {Object} Onboarding
 * @property {number} revision
 * @property {'provider'|'use'|'goal'|'specs'|'complete'} step
 * @property {{provider:(ProviderChoice|null), uses:string[], customUse:string, goals:string[], customGoal:string,
 *             inventoryConsent:('unset'|'granted'|'skipped')}} profile
 * @property {{inventoryId:(string|null), scanId:(string|null), reportId:(string|null)}} resume
 *
 * @typedef {{inventoryId:string, status:('running'|'complete'|'partial'|'cancelled'), facts:{seq:number,kind:string,label:string}[], coverage:object}} Inventory
 * @typedef {{id:string, text:string, intent:string, basis:('general'|'profile'|'inventory')}} Starter
 *
 * @typedef {{collectorId:string, category:string, status:('complete'|'partial'|'denied'|'unsupported'|'timed_out'|'failed'|'skipped'|'cancelled'), observations:number, reason:(string|null)}} Coverage
 * @typedef {Object} Scan
 * @property {string} scanId
 * @property {string} intent  'audit.full' or a targeted 'audit.*'
 * @property {'queued'|'running'|'complete'|'complete_with_gaps'|'cancelled'|'interrupted'|'failed'} status
 * @property {string} startedAt
 * @property {(string|null)} finishedAt
 * @property {{done:number, total:number, categories:{id:string,label:string,status:('queued'|'running'|'done'|'gap')}[]}} progress
 * @property {Coverage[]} coverage
 * @property {(string|null)} reportId
 * @property {{stream:string, seq:number}} eventCursor
 *
 * @typedef {{id:string, category:string, source:string, observedAt:string, interval:(string|null), display:string, value:object,
 *            collectorId:string, collectorVersion:string, quality:string}} Evidence
 * @typedef {{id:string, ruleId:string, ruleVersion:string, fingerprint:string, category:string, title:string, severity:string,
 *            confidence:string, evidenceIds:string[], actionEligibility:('manual_review'|'preview_available')}} Finding
 * @typedef {{id:string, category:string, title:string, display:string, collectorId:string, status:string}} Gap
 * @typedef {Object} Report
 * @property {string} reportId
 * @property {string} scanId
 * @property {number} revision
 * @property {'complete'|'complete_with_gaps'|'partial'} status
 * @property {string} generatedAt
 * @property {string} intent
 * @property {{findings:number, gaps:number}} counts
 * @property {Evidence[]} evidence
 * @property {Finding[]} findings
 * @property {Coverage[]} coverage
 * @property {Gap[]} gaps
 *
 * @typedef {{rows:{label:string,before:string,after:string}[], details:string[][]}} Preview
 * @typedef {{id:string, findingId:string, category:string, title:string, text:string, evidenceIds:string[], options:string[],
 *            guide:(string[]|null), preview:(Preview|null), actionId:(string|null), reply:({question:string,text:string,evidenceIds:string[]}|null)}} Card
 * @typedef {{cardId:string, status:('ignored'|'declined'|'done'|'verified'|'restored'), title:string}} Decision
 * @typedef {{reportId:string, revision:number, total:number, pendingCount:number, card:(Card|null), decisions:Decision[]}} Followup
 *
 * Methods (§6): onboarding.get → Onboarding · onboarding.save {expectedRevision, patch} → Onboarding ·
 *   inventory.start {idempotencyKey} → {inventoryId,status} · inventory.get {inventoryId} → Inventory · inventory.cancel {inventoryId} ·
 *   starter.list {profileRevision} → {starters:Starter[]} · scan.start {idempotencyKey,intent,text} → Scan · scan.get {scanId} → Scan ·
 *   scan.cancel {scanId} → Scan · report.get {reportId} → Report · report.list {limit,before?} · evidence.get {reportId,evidenceId} → Evidence ·
 *   followup.get {reportId} → Followup · followup.respond {idempotencyKey,reportId,expectedRevision,scope,cardId?,option?,text?} → Followup
 *
 * @typedef {Object} Backend
 * @property {'desktop'|'fixture'} kind
 * @property {(method:string, params?:object) => Promise<any>} call  rejects with BackendError
 * @property {(fn:(ev:EventEnvelope) => void) => (() => void)} onEvent  returns an unsubscribe function
 * @property {(fn:(state:BackendState, info:StateInfo) => void) => (() => void)} onState  backend connection state
 * @property {() => BackendState} state
 */
(function () {
  var OT = window.OptTool = window.OptTool || {};

  function storage() { try { return window.localStorage; } catch (e) { return null; } }

  /** @returns {Backend} */
  OT.selectBackend = function () {
    var forced = /[?&]adapter=fixture\b/.test(location.search);
    if (!forced && window.chrome && window.chrome.webview) return OT.createDesktopAdapter(window.chrome.webview);
    return OT.createFixtureAdapter({ fixtureUrl: 'fixtures/m1-simulated-audit.json', storage: storage() });
  };
})();
