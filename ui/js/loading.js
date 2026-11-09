/* Loading screen controller (design: components/LoadingScreen). The markup is plain HTML in index.html so it paints before
   any script; this runs right after it, before React loads. The app drives it through window.OT_loading:
     set(state)   'starting' | 'slow' | 'update' | 'session' | 'failed'
     ready()      the app is usable: remove at once if it was never seen (under 300 ms), else fade out (150 ms)
     on(handlers) { update, close, retry } for the buttons
   No dependencies; safe to call before or after the element is gone. */
(function () {
  'use strict';
  var t0 = (window.performance && performance.now) ? performance.now() : Date.now();
  var el = document.getElementById('ot-load');
  var handlers = {};
  var state = 'starting', done = false;
  var MSG = {
    slow: 'Still starting…',
    update: 'Your saved data is from a newer Optimus.',
    session: 'Optimus is open in another session.',
    failed: 'Optimus couldn’t start.'
  };
  var PRIMARY = { update: ['Update Optimus', 'update'], failed: ['Try again', 'retry'] };

  function q(sel) { return el ? el.querySelector(sel) : null; }

  function render() {
    if (!el) return;
    el.className = 'ot-load is-' + state;
    var waiting = state === 'starting' || state === 'slow' || state === 'session';
    if (waiting) el.setAttribute('aria-busy', 'true'); else el.removeAttribute('aria-busy');
    var logo = q('.ot-load-logo');
    if (logo) logo.classList.toggle('is-spinning', waiting);
    var status = q('.ot-load-status');
    if (status) {
      status.textContent = '';
      if (state === 'starting') {
        var sr = document.createElement('span'); sr.className = 'ot-sr'; sr.textContent = 'Starting Optimus';
        var later = document.createElement('p'); later.className = 'ot-load-msg ot-muted is-later'; later.setAttribute('aria-hidden', 'true'); later.textContent = MSG.slow;
        status.appendChild(sr); status.appendChild(later);
      } else {
        var p = document.createElement('p'); p.className = 'ot-load-msg' + (state === 'slow' ? ' ot-muted' : ''); p.textContent = MSG[state] || '';
        status.appendChild(p);
      }
    }
    var actions = q('.ot-load-actions'), primary = q('[data-load="primary"]'), close = q('[data-load="close"]');
    var pr = PRIMARY[state];
    if (actions) actions.hidden = !pr;
    if (primary && pr) { primary.textContent = pr[0]; primary.setAttribute('data-act', pr[1]); }
    if (close) close.hidden = state !== 'update';
  }

  function remove() { if (el && el.parentNode) el.parentNode.removeChild(el); el = null; }

  if (el) {
    el.addEventListener('click', function (e) {
      var b = e.target && e.target.closest ? e.target.closest('button[data-load]') : null;
      if (!b) return;
      var act = b.getAttribute('data-load') === 'close' ? 'close' : b.getAttribute('data-act');
      if (act === 'retry') { state = 'starting'; render(); }
      if (handlers[act]) handlers[act]();
    });
  }

  window.OT_loading = {
    set: function (next) {
      if (done || !el || !next || next === state) return;
      if (next !== 'starting' && !MSG[next]) return;
      state = next; render();
    },
    state: function () { return done ? 'ready' : state; },
    ready: function () {
      if (done) return; done = true;
      if (!el) return;
      var now = (window.performance && performance.now) ? performance.now() : Date.now();
      // Never seen: "starting" stays invisible for its first 300 ms.
      if (state === 'starting' && now - t0 < 300) { remove(); return; }
      el.classList.add('is-leaving');
      setTimeout(remove, 160);
    },
    on: function (h) { handlers = h || {}; }
  };
})();
