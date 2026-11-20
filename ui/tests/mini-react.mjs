// A small React for the node tests that renders the WHOLE tree: function components are called with their own hook
// state per instance (kept by position and key, reset when the type or the key changes, unmounted with their effect
// cleanups when gone), effects run after each render, and the result is a host tree ({type, props, children}) whose
// event handlers the tests call the way a browser would. Not a DOM: refs to host elements stay null.
export function createMiniReact() {
  const insts = new Map();
  let cur = null, seen = null, effects = [];
  const R = { dirty: true };
  const same = (a, b) => !!a && !!b && a.length === b.length && a.every((x, i) => Object.is(x, b[i]));
  function hook() { const i = cur.i++; return [cur.hooks, i]; }
  function Component(props) { this.props = props; }
  const React = {
    createElement(type, props, ...children) {
      const p = Object.assign({}, props);
      const key = p.key == null ? null : String(p.key);
      delete p.key;
      if (children.length) p.children = children.length === 1 ? children[0] : children;
      return { $el: true, type, props: p, key };
    },
    Fragment: 'fragment', Component,
    useState(v) {
      const [h, i] = hook();
      if (!(i in h)) h[i] = { v: typeof v === 'function' ? v() : v };
      const s = h[i];
      if (!s.set) s.set = (n) => { const next = typeof n === 'function' ? n(s.v) : n; if (!Object.is(next, s.v)) { s.v = next; R.dirty = true; } };
      return [s.v, s.set];
    },
    useRef(v) { const [h, i] = hook(); if (!(i in h)) h[i] = { current: v === undefined ? null : v }; return h[i]; },
    useMemo(fn, deps) { const [h, i] = hook(); if (h[i] && deps && same(deps, h[i].deps)) return h[i].v; h[i] = { v: fn(), deps }; return h[i].v; },
    useCallback(fn, deps) { return React.useMemo(() => fn, deps); },
    useEffect(fn, deps) {
      const [h, i] = hook();
      const s = h[i] || (h[i] = { deps: null, cleanup: null, ran: false });
      if (s.ran && deps && same(deps, s.deps)) return;
      s.deps = deps; s.ran = true;
      effects.push(() => { if (typeof s.cleanup === 'function') s.cleanup(); s.cleanup = fn(); });
    },
    useLayoutEffect(fn, deps) { return React.useEffect(fn, deps); },
    useId() { return 'id' + cur.path.length; },
    forwardRef: (f) => f, memo: (f) => f
  };
  function unmount(inst) { inst.hooks.forEach((s) => { if (s && typeof s.cleanup === 'function') { try { s.cleanup(); } catch (e) { /* ignore */ } } }); }
  function node(n, path) {
    if (n == null || n === false || n === true) return null;
    if (Array.isArray(n)) return n.map((c, i) => node(c, path + '/' + (c && c.$el && c.key != null ? 'k:' + c.key : i)));
    if (typeof n !== 'object' || !n.$el) return typeof n === 'object' ? n : String(n);
    const at = path + (n.key != null ? '#' + n.key : '');
    if (typeof n.type === 'function') {
      let inst = insts.get(at);
      if (!inst || inst.type !== n.type) { if (inst) unmount(inst); inst = { type: n.type, hooks: [], i: 0, path: at }; insts.set(at, inst); }
      seen.add(at);
      const prev = cur; cur = inst; inst.i = 0;
      let out;
      try { out = n.type(Object.assign({}, n.props)); } finally { cur = prev; }
      return node(out, at + '>');
    }
    const props = Object.assign({}, n.props);
    const kids = node(props.children, at + '/' + n.type);
    delete props.children;
    return { type: n.type, props, children: kids == null ? [] : [].concat(kids) };
  }
  // Renders root() until nothing changes (state set by effects re-renders); returns the host tree.
  R.render = function (root) {
    let tree = null, n = 0;
    do {
      R.dirty = false; seen = new Set();
      tree = node(root(), '');
      for (const [k, inst] of insts) if (!seen.has(k)) { unmount(inst); insts.delete(k); }
      const fx = effects; effects = [];
      fx.forEach((f) => f());
      if (++n > 50) throw new Error('render loop');
    } while (R.dirty);
    return tree;
  };
  R.React = React;
  return R;
}

// Host-tree helpers.
export function all(n, out = []) {
  if (n == null || typeof n !== 'object') return out;
  if (Array.isArray(n)) { n.forEach((x) => all(x, out)); return out; }
  out.push(n); (n.children || []).forEach((c) => all(c, out));
  return out;
}
export function text(n) {
  if (n == null) return '';
  if (Array.isArray(n)) return n.map(text).join('');
  if (typeof n !== 'object') return String(n);
  if (n.props && n.props.dangerouslySetInnerHTML) return '';
  return (n.children || []).map(text).join('');
}
export const hasClass = (n, c) => String((n.props && n.props.className) || '').split(/\s+/).includes(c);
export const byClass = (t, c) => all(t).filter((n) => hasClass(n, c));
export const buttons = (t) => all(t).filter((n) => n.type === 'button');
export const button = (t, label) => buttons(t).find((b) => text(b).trim() === label) || null;
export const click = (b) => { if (!b) throw new Error('no such button'); if (b.props.disabled) throw new Error('button is disabled: ' + text(b)); b.props.onClick && b.props.onClick({ preventDefault() {}, stopPropagation() {}, target: b, currentTarget: b }); };
