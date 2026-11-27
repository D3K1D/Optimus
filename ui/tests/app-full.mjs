// The real app (AppFlow and every component it renders, with their own state) on the fixture backend, in node, through
// mini-react.mjs. Shared by the Fix for me tests: the backend is prepared as the host would be (a finished report,
// permissions set, Claude Code connected headless in the app), every call is recorded, and `gate` can hold or answer
// calls the test wants to time.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import fs from 'node:fs';
import vm from 'node:vm';
import { createMiniReact, all, text, byClass, button, click, hasClass } from './mini-react.mjs';
const require = createRequire(import.meta.url);
const { createFixtureAdapter } = require('../js/adapter-fixture.js');
const fixture = JSON.parse(fs.readFileSync(new URL('../fixtures/m1-simulated-audit.json', import.meta.url), 'utf8'));
const extras = JSON.parse(fs.readFileSync(new URL('../fixtures/m2-extras.json', import.meta.url), 'utf8'));
const src = (f) => fs.readFileSync(process.env.OT_JS_DIR ? process.env.OT_JS_DIR + '/' + f : new URL('../js/' + f, import.meta.url), 'utf8');   // OT_JS_DIR: another copy (to check a test fails without its fix)
export const tick = (ms = 0) => new Promise((r) => setTimeout(r, ms));
export const key = () => crypto.randomUUID();
export { all, text, byClass, button, click, hasClass };
function memStore() {
  const m = {};
  return { getItem: (k) => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); }, removeItem: (k) => { delete m[k]; } };
}
const selectors = [];   // what the app looked for in the page (document.querySelector)
export { selectors };
function load(R) {
  const window = {
    React: R.React, OptTool: {}, localStorage: memStore(),
    matchMedia: () => ({ matches: true, addEventListener() {}, removeEventListener() {} }),
    addEventListener() {}, removeEventListener() {}
  };
  const document = { documentElement: { dataset: {}, style: {} }, querySelector: (q) => { selectors.push(q); return null; }, querySelectorAll: () => [], getElementById: () => null,
    addEventListener() {}, removeEventListener() {}, body: { appendChild() {} }, activeElement: null };
  const ctx = vm.createContext({ window, document, navigator: {}, console, crypto, setTimeout, clearTimeout, setInterval, clearInterval,
    requestAnimationFrame: () => 0, cancelAnimationFrame() {} });
  window.window = window;
  for (const f of ['components.js', 'connection-state.js', 'bench.js', 'report-problem.js', 'fix-plan.js', 'app-flow.js']) vm.runInContext(src(f), ctx, { filename: f });
  return window.OptTool;
}
async function prepared(o) {
  const be = createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 });
  const ob = await be.call('onboarding.get');
  await be.call('onboarding.save', { expectedRevision: ob.revision, patch: { uses: ['Gaming'], inventoryConsent: 'skipped', provider: { value: 'claude-code', label: 'Claude', kind: 'claude_code_mcp' }, step: 'complete' } });
  await be.call('fixture.setClient', { client: 'claude_code', installed: true, signedIn: true, registered: 'yes' });
  const fin = new Promise((r) => { const off = be.onEvent((e) => { if (e.type === 'scan.finished') { off(); r(e); } }); });
  await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  await fin;
  if (o.flag) await be.call('fixture.setAdminAvailable', { available: true });
  const p = await be.call('ai.getPermissions');
  const rec = JSON.parse(JSON.stringify(p.permissions));
  rec.set = true; rec.do.changes = o.changes || 'fix'; rec.do.admin = !!o.admin; rec.do.scripts = !!o.scripts;
  await be.call('ai.setPermissions', { idempotencyKey: key(), revision: p.permissions.revision, permissions: rec });
  return be;
}
// o: {flag, admin, changes}. gate(method, params) may return a promise to answer the call instead of the fixture (or
// undefined to let it through); events can be held with holdEvents(pred) and let go with releaseEvents().
export async function startFullApp(o = {}) {
  const inner = await prepared(o);
  const calls = [], toasts = [];
  let gate = null, held = [], holdPred = null;
  const listeners = [], stateListeners = [];
  inner.onState((st, info) => stateListeners.forEach((f) => f(st, info)));
  inner.onEvent((ev) => {
    if (holdPred && holdPred(ev)) { held.push(ev); return; }
    listeners.forEach((f) => f(ev));
  });
  const backend = {
    kind: 'fixture', state: () => inner.state(), detail: () => (inner.detail ? inner.detail() : null),
    onEvent: (f) => { listeners.push(f); return () => { const i = listeners.indexOf(f); if (i >= 0) listeners.splice(i, 1); }; },
    onState: (f) => { stateListeners.push(f); return () => { const i = stateListeners.indexOf(f); if (i >= 0) stateListeners.splice(i, 1); }; },
    call(method, params) {
      const rec = { method, params: params || {} };
      calls.push(rec);
      const g = gate && gate(method, params, () => inner.call(method, params));
      const p = g || inner.call(method, params);
      return p.then((r) => { rec.result = r; return r; }, (e) => { rec.error = e; throw e; });
    }
  };
  let R = createMiniReact(), OT = load(R);
  let tree = null;
  const renderOnce = () => {
    tree = R.render(() => R.React.createElement(OT.AppFlow, { backend }));
    const t = byClass(tree, 'ot-toast')[0];
    if (t && toasts[toasts.length - 1] !== text(t)) toasts.push(text(t));
  };
  async function settle(ms = 100) {
    const end = Date.now() + ms;
    renderOnce();
    do { await tick(2); if (R.dirty) renderOnce(); } while (Date.now() < end || R.dirty);
  }
  const app = {
    inner, calls, toasts, OT, settle,
    get tree() { return tree; },
    set gate(g) { gate = g; },
    holdEvents(pred) { holdPred = pred; },
    // The shell reconnected to the host (the app re-reads its snapshots).
    resume() { stateListeners.forEach((f) => f(inner.state(), { resumed: true, detail: null })); },
    // An event as the host would send it, straight to the app.
    emit(ev) { listeners.forEach((f) => f(Object.assign({ seq: null, at: new Date().toISOString() }, ev))); },
    releaseEvents() { holdPred = null; const h = held; held = []; h.forEach((ev) => listeners.forEach((f) => f(ev))); },
    after(i, method) { return calls.slice(i).filter((c) => c.method === method); },
    cards() { return all(tree).filter((n) => n.props && n.props['data-card']); },
    card(cid) { return all(tree).find((n) => n.props && n.props['data-card'] === cid) || null; },
    reportCardId() {
      const c = app.cards().find((n) => !text(n).includes('AI check-up') && !all(n).some((x) => hasClass(x, 'is-ai')));
      return c ? c.props['data-card'] : null;
    },
    panel(cid) { const c = app.card(cid); return c ? byClass(c, 'ot-fixpanel')[0] || null : null; },
    side() { return all(tree).find((n) => hasClass(n, 'ot-checks-side')) || null; },
    taskId() { const s = calls.filter((c) => c.method === 'agent.start' && c.result).pop(); return s ? s.result.taskId : null; },
    async until(pred, what, ms = 4000) {
      const end = Date.now() + ms;
      while (Date.now() < end) { await settle(15); if (pred()) return; }
      assert.fail('timed out waiting for ' + what);
    },
    async click(b) { click(b); await settle(); },
    // The window reloaded: a fresh AppFlow (new page state, empty storage) on the same host; the old page hears nothing more.
    async reload() {
      listeners.length = 0; stateListeners.length = 0;
      R = createMiniReact(); OT = load(R); app.OT = OT;
      await settle(200);
    }
  };
  // Claude Code chosen and signed in (headless in the app): the deck with Fix for me on its cards.
  await app.until(() => !!app.reportCardId() && !!button(app.card(app.reportCardId()), 'Fix for me'), 'the deck with Fix for me');
  return app;
}
