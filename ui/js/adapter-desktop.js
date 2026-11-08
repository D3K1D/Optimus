/* Desktop adapter: speaks the protocol v1 §7 bridge messages to the WebView2 shell.
   Renderer → shell: {type:'rpc', id, method, params} via chrome.webview.postMessage.
   Shell → renderer: rpc.result / rpc.error (by id), event (events.notify params), backend (connection state).
   Plain script (no build); also loadable from Node for tests (module.exports). */
(function (root) {
  'use strict';

  function rpcError(e) {
    var err = new Error((e && e.message) || 'Backend error');
    err.code = e && typeof e.code === 'number' ? e.code : -32603;
    err.data = e && e.data !== undefined ? e.data : null;
    return err;
  }

  /**
   * @param {{postMessage:(msg:any)=>void, addEventListener:(type:string, fn:(e:{data:any})=>void)=>void,
   *          removeEventListener?:(type:string, fn:Function)=>void}} webview  window.chrome.webview
   * @returns {import('./backend.js').Backend}
   */
  function createDesktopAdapter(webview) {
    var nextId = 1;
    var pending = {};            // rpc id → {method, params, resolve, reject, sent}
    var eventFns = [], stateFns = [];
    var state = null;            // null until the shell reports one; calls are posted straight away
    var lastSeq = {};            // stream → last delivered seq (drops duplicates after a reconnect)

    var MAX_RETRIES = 8;
    function holding() { return state === 'connecting' || state === 'reconnecting'; }

    function post(entry) {
      var id = nextId++;
      entry.id = id; entry.sent = true;
      pending[id] = entry;
      webview.postMessage({ type: 'rpc', id: id, method: entry.method, params: entry.params });
    }

    function call(method, params) {
      return new Promise(function (resolve, reject) {
        var entry = { method: method, params: params || {}, resolve: resolve, reject: reject, sent: false };
        if (state === 'incompatible') { reject(rpcError({ code: -32010, message: 'incompatible_protocol' })); return; }
        if (holding()) { held.push(entry); return; }
        post(entry);
      });
    }
    var held = [];

    // After reconnecting → ready, re-send everything that has no answer yet. Mutating calls carry the same
    // params, so the same idempotencyKey: the host replays the recorded result instead of acting twice.
    function flush(resend) {
      var again = [];
      if (resend) Object.keys(pending).forEach(function (id) { again.push(pending[id]); delete pending[id]; });
      again = again.concat(held); held = [];
      again.forEach(post);
    }

    function setState(next, detail) {
      var prev = state;
      state = next;
      if (next === 'incompatible') {
        var all = held.concat(Object.keys(pending).map(function (id) { return pending[id]; }));
        held = []; pending = {};
        all.forEach(function (e) { e.reject(rpcError({ code: -32010, message: 'incompatible_protocol', data: detail })); });
      } else if (next === 'ready') {
        flush(prev === 'reconnecting');
      }
      var info = { previous: prev, detail: detail === undefined ? null : detail, resumed: prev === 'reconnecting' && next === 'ready' };
      stateFns.slice().forEach(function (fn) { try { fn(next, info); } catch (e) { setTimeout(function () { throw e; }); } });
    }

    function onMessage(e) {
      var m = e && e.data;
      if (typeof m === 'string') { try { m = JSON.parse(m); } catch (x) { return; } }
      if (!m || typeof m !== 'object') return;
      if (m.type === 'rpc.result' || m.type === 'rpc.error') {
        var entry = pending[m.id];
        if (!entry) return;                       // late answer for a request that was re-sent
        delete pending[m.id];
        if (m.type === 'rpc.result') entry.resolve(m.result);
        else if (m.error && m.error.data && m.error.data.retryable && (entry.retries || 0) < MAX_RETRIES) {
          // The shell had no backend yet (e.g. the page loaded before the shell's first state message
          // reached it). Hold the call until ready; same params, so the same idempotencyKey.
          entry.retries = (entry.retries || 0) + 1;
          if (holding()) held.push(entry);
          else setTimeout(function () { if (holding()) held.push(entry); else post(entry); }, 250 * entry.retries);
        }
        else entry.reject(rpcError(m.error));
      } else if (m.type === 'event' && m.event) {
        var ev = m.event, s = ev.stream;
        if (s != null && typeof ev.seq === 'number') {
          if (lastSeq[s] != null && ev.seq <= lastSeq[s]) return;
          lastSeq[s] = ev.seq;
        }
        eventFns.slice().forEach(function (fn) { try { fn(ev); } catch (x) { setTimeout(function () { throw x; }); } });
      } else if (m.type === 'backend') {
        setState(m.state, m.detail);
      }
    }
    webview.addEventListener('message', onMessage);

    function sub(list, fn) { list.push(fn); return function () { var i = list.indexOf(fn); if (i >= 0) list.splice(i, 1); }; }

    return {
      kind: 'desktop',
      call: call,
      onEvent: function (fn) { return sub(eventFns, fn); },
      onState: function (fn) { return sub(stateFns, fn); },
      state: function () { return state || 'ready'; },
      dispose: function () { if (webview.removeEventListener) webview.removeEventListener('message', onMessage); }
    };
  }

  if (root) { root.OptTool = root.OptTool || {}; root.OptTool.createDesktopAdapter = createDesktopAdapter; }
  if (typeof module !== 'undefined' && module.exports) module.exports = { createDesktopAdapter: createDesktopAdapter };
})(typeof window !== 'undefined' ? window : null);
