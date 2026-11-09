/* Optimus UI components (React 18, no build step). Production copy of design/design-system/components/bundle.js:
   sample data and AppFlow are removed (AppFlow lives in app-flow.js, data comes from backend.js). Visual output unchanged. */
(function () {
  var React = window.React, h = React.createElement;
  function cx() { return Array.prototype.filter.call(arguments, Boolean).join(' '); }
  function omit(p, keys) { var o = {}; for (var k in p) if (keys.indexOf(k) < 0) o[k] = p[k]; return o; }

  var PATHS = {
    check: 'M5 12.5l4.5 4.5L19 7.5',
    shield: 'M12 3l7 3v5.5c0 4.4-3 8.1-7 9.5-4-1.4-7-5.1-7-9.5V6l7-3z',
    alert: 'M12 4l9 16H3L12 4zM12 10v4.5M12 17.5v.01',
    network: 'M4 9.5a12 12 0 0116 0M7 13a7.5 7.5 0 0110 0M10 16.5a3 3 0 014 0M12 20v.01',
    bolt: 'M13 3L5 13.5h6L10 21l8-10.5h-6L13 3z',
    repeat: 'M17 2.5l3 3-3 3M4 11.5v-1a5 5 0 015-5h11M7 21.5l-3-3 3-3M20 12.5v1a5 5 0 01-5 5H4',
    gap: 'M12 3a9 9 0 110 18 9 9 0 010-18zM5.6 5.6l12.8 12.8',
    key: 'M15 4.5a4.5 4.5 0 110 9 4.5 4.5 0 010-9zM11.8 12.2L4 20M6.5 17.5l2 2M9 15l2 2',
    terminal: 'M4 5h16v14H4zM7.5 9.5l3 2.5-3 2.5M12.5 15h4',
    lock: 'M6 11h12v9H6zM8.5 11V8a3.5 3.5 0 017 0v3',
    scan: 'M4 8V5a1 1 0 011-1h3M16 4h3a1 1 0 011 1v3M20 16v3a1 1 0 01-1 1h-3M8 20H5a1 1 0 01-1-1v-3M4 12h16',
    undo: 'M9 14L4 9l5-5M4 9h10.5a5.5 5.5 0 010 11H11',
    chevron: 'M9 6l6 6-6 6',
    device: 'M3 5h18v11H3zM8 20h8M12 16v4',
    eye: 'M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12zM12 9a3 3 0 110 6 3 3 0 010-6z',
    spark: 'M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8L12 3zM18.5 16l.8 2.2 2.2.8-2.2.8-.8 2.2-.8-2.2-2.2-.8 2.2-.8.8-2.2z',
    stop: 'M7 7h10v10H7z',
    file: 'M6 3h8l4 4v14H6zM14 3v4h4M9 12h6M9 16h6',
    arrow: 'M5 12h14M13 6l6 6-6 6',
    disk: 'M4 6c0-1.1 3.6-2 8-2s8 .9 8 2v12c0 1.1-3.6 2-8 2s-8-.9-8-2zM4 6c0 1.1 3.6 2 8 2s8-.9 8-2M4 12c0 1.1 3.6 2 8 2s8-.9 8-2',
    sliders: 'M4 7h10M18 7h2M4 17h4M12 17h8M14 5v4M8 15v4',
    pulse: 'M3 12h4l2-6 4 12 2-6h6',
    dots: 'M6 12h.01M12 12h.01M18 12h.01',
    close: 'M6 6l12 12M18 6L6 18',
    back: 'M15 6l-6 6 6 6',
    collapse: 'M6 15l6-6 6 6',
    history: 'M4 12a8 8 0 108-8 8 8 0 00-6 2.7M4 4v3.5h3.5M12 8v4.5l3 2',
    trash: 'M5 7h14M10 7V4.5h4V7M7 7l1 13h8l1-13M10.5 11v5M13.5 11v5',
    pause: 'M9 5v14M15 5v14',
    play: 'M8 5l11 7-11 7z',
    list: 'M5 7h14M5 12h14M5 17h14',
    report: 'M7 3h7l4 4v14H7zM14 3v4h4M10 12h5M10 16h5',
    link: 'M10 14a4 4 0 005.7 0l3-3a4 4 0 00-5.7-5.7l-1 1M14 10a4 4 0 00-5.7 0l-3 3a4 4 0 005.7 5.7l1-1',
    // M6a (design: Scan menu and Benchmark, Benchmark additions)
    gauge: 'M4.2 17.5a8.5 8.5 0 1115.6 0M12 13.5l3.8-4.3M12 13.5v.01',
    cpu: 'M7 7h10v10H7zM10 10h4v4h-4zM9.5 3v4M14.5 3v4M9.5 17v4M14.5 17v4M3 9.5h4M3 14.5h4M17 9.5h4M17 14.5h4',
    gpu: 'M2.5 7h17v10h-17zM19.5 10h2M6 17v3M10 17v3M14 12a3 3 0 11-6 0 3 3 0 016 0z',
    ram: 'M3 7.5h18v8H3zM7 10.5v2M11 10.5v2M15 10.5v2M6 15.5v3M18 15.5v3',
    thermo: 'M10 14.6V5a2 2 0 014 0v9.6a4 4 0 11-4 0zM12 9.5v6',
    fan: 'M12 10.6a1.4 1.4 0 110 2.8 1.4 1.4 0 010-2.8zM11 10.8C9.6 7 10.6 3.8 13 4.2c2.2.4 1.8 3.8-.3 6M13.2 11c3.9-1.4 7 .2 6.6 2.6-.4 2.2-3.8 1.8-6-.3M13 13.2c1.4 3.9.4 7-2 6.6-2.2-.4-1.8-3.8.3-6M10.8 13c-3.9 1.4-7-.2-6.6-2.6.4-2.2 3.8-1.8 6 .3',
    plug: 'M9 3v5M15 3v5M6 8h12v3a6 6 0 01-12 0zM12 17v4',
    clock: 'M12 3.5a8.5 8.5 0 110 17 8.5 8.5 0 010-17zM12 7.5V12l3 2',
    copy: 'M9 9h11v11H9zM5 15H4V4h11v1',
    download: 'M12 4v11M7.5 10.5L12 15l4.5-4.5M5 19.5h14'
  };

  function Icon(p) {
    var size = p.size || 20;
    return h('svg', {
      className: cx('ot-icon', p.className), width: size, height: size, viewBox: '0 0 24 24', fill: 'none',
      stroke: 'currentColor', strokeWidth: p.strokeWidth || 1.75, strokeLinecap: 'round', strokeLinejoin: 'round',
      'aria-hidden': p.label ? undefined : true, 'aria-label': p.label, role: p.label ? 'img' : undefined
    }, h('path', { d: PATHS[p.name] || PATHS.gap }));
  }


  /* The app logo: a large blue gear meshing with a small orange one, flat. */
  var LOGO_SVG = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 120 120\" aria-hidden=\"true\" focusable=\"false\"><g stroke-width=\"1.2\" stroke-linejoin=\"round\" fill-rule=\"evenodd\"><path class=\"ot-gear-s\" style=\"transform-box:view-box;transform-origin:91.75px 36.91px\" d=\"M82.2 47.15 L76.28 50.36 A20.5 20.5 0 0 1 72.88 44.92 L78.36 41 A14 14 0 0 1 78.11 33.76L78.11 33.76 L72.37 30.24 A20.5 20.5 0 0 1 75.38 24.57 L81.51 27.36 A14 14 0 0 1 87.66 23.52L87.66 23.52 L87.84 16.79 A20.5 20.5 0 0 1 94.25 16.56 L94.9 23.27 A14 14 0 0 1 101.3 26.67L101.3 26.67 L107.22 23.46 A20.5 20.5 0 0 1 110.62 28.9 L105.14 32.82 A14 14 0 0 1 105.39 40.06L105.39 40.06 L111.13 43.58 A20.5 20.5 0 0 1 108.12 49.25 L101.99 46.46 A14 14 0 0 1 95.84 50.3L95.84 50.3 L95.66 57.03 A20.5 20.5 0 0 1 89.25 57.26 L88.6 50.55 A14 14 0 0 1 82.2 47.15Z M96.75 36.91 A5 5 0 1 0 86.75 36.91 A5 5 0 1 0 96.75 36.91Z\" fill=\"#ff8a3d\" stroke=\"#ff8a3d\"/><path class=\"ot-gear-b\" style=\"transform-box:view-box;transform-origin:46px 65.5px\" d=\"M64 40.26 L69.66 35.76 A38 38 0 0 1 74.02 39.83 L69.92 45.78 A31 31 0 0 1 74.21 52.64L74.21 52.64 L81.36 51.57 A38 38 0 0 1 83.1 57.28 L76.57 60.38 A31 31 0 0 1 76.86 68.47L76.86 68.47 L83.58 71.12 A38 38 0 0 1 82.24 76.93 L75.04 76.36 A31 31 0 0 1 71.24 83.5L71.24 83.5 L75.74 89.16 A38 38 0 0 1 71.67 93.52 L65.72 89.42 A31 31 0 0 1 58.86 93.71L58.86 93.71 L59.93 100.86 A38 38 0 0 1 54.22 102.6 L51.12 96.07 A31 31 0 0 1 43.03 96.36L43.03 96.36 L40.38 103.08 A38 38 0 0 1 34.57 101.74 L35.14 94.54 A31 31 0 0 1 28 90.74L28 90.74 L22.34 95.24 A38 38 0 0 1 17.98 91.17 L22.08 85.22 A31 31 0 0 1 17.79 78.36L17.79 78.36 L10.64 79.43 A38 38 0 0 1 8.9 73.72 L15.43 70.62 A31 31 0 0 1 15.14 62.53L15.14 62.53 L8.42 59.88 A38 38 0 0 1 9.76 54.07 L16.96 54.64 A31 31 0 0 1 20.76 47.5L20.76 47.5 L16.26 41.84 A38 38 0 0 1 20.33 37.48 L26.28 41.58 A31 31 0 0 1 33.14 37.29L33.14 37.29 L32.07 30.14 A38 38 0 0 1 37.78 28.4 L40.88 34.93 A31 31 0 0 1 48.97 34.64L48.97 34.64 L51.62 27.92 A38 38 0 0 1 57.43 29.26 L56.86 36.46 A31 31 0 0 1 64 40.26Z M55.5 65.5 A9.5 9.5 0 1 0 36.5 65.5 A9.5 9.5 0 1 0 55.5 65.5Z\" fill=\"#3a7bff\" stroke=\"#3a7bff\"/></g></svg>";
  var logoSeq = 0;
  function Logo(p) {
    var id = React.useMemo(function () { logoSeq += 1; return 'otlogo' + logoSeq; }, []);
    var size = p.size || 28;
    var markup = React.useMemo(function () { return LOGO_SVG.split('__ID__').join(id); }, [id]);
    // `mono` draws both gears in currentColor (black or white with the theme); `decorative` hides it from assistive tech;
    // `spin` turns the gears slowly while something works (still under reduced motion).
    return h('span', { className: cx('ot-logo', p.mono && 'is-mono', p.spin && 'is-spinning', p.className), style: { width: size, height: size },
      role: p.decorative ? undefined : 'img', 'aria-label': p.decorative ? undefined : (p.label || 'Optimus'), 'aria-hidden': p.decorative || undefined,
      dangerouslySetInnerHTML: { __html: markup } });
  }

  function GridBackground(p) {
    return h('div', Object.assign(omit(p, ['className', 'children']), { className: cx('ot-grid', p.className) }), p.children);
  }

  /* Hex-character ocean: rows of hex digits recede to a horizon, displaced by travelling swells.
     Crests brighten to foam; error codes surface in the swell, ride it, and fade. */
  var HEX = '0123456789ABCDEF';
  var ERRS = ['0xC0000005', 'ERR_TIMEOUT', '0x80070005', 'KERNEL_POWER_41', '0xC000021A', 'E_ACCESSDENIED', 'WHEA_UNCORRECTABLE',
    '0x8024402C', 'DPC_WATCHDOG', 'IO_DEVICE_ERROR', '0x00000133', 'BUGCHECK 0x9F', 'SVC_FAILED', '0x80004005'];
  var SWELLS = [
    { kx: 0.0055, kz: 0.52, a: 1.0, w: 1.05, p: 0 },
    { kx: -0.0105, kz: 0.36, a: 0.5, w: 1.55, p: 1.7 },
    { kx: 0.019, kz: 0.85, a: 0.24, w: 2.3, p: 3.1 },
    { kx: 0.0028, kz: 0.18, a: 0.62, w: 0.55, p: 4.4 }
  ];
  var SWELL_SUM = SWELLS.reduce(function (s, w) { return s + w.a; }, 0);

  // Instanced glyph quads: per glyph [x, y, w, h, atlasX, atlasY, r, g, b, alpha] in device pixels.
  var WAVE_VS = '#version 300 es\n' +
    'layout(location=0) in vec2 corner; layout(location=1) in vec4 rect; layout(location=2) in vec2 src; layout(location=3) in vec4 color;\n' +
    'uniform vec2 view; uniform vec2 tex; out vec2 uv; out vec4 col;\n' +
    'void main() { vec2 p = rect.xy + corner * rect.zw; gl_Position = vec4(p.x / view.x * 2.0 - 1.0, 1.0 - p.y / view.y * 2.0, 0.0, 1.0);\n' +
    '  uv = (src + corner * rect.zw) / tex; col = color; }';
  var WAVE_FS = '#version 300 es\nprecision mediump float; in vec2 uv; in vec4 col; uniform sampler2D atlas; out vec4 o;\n' +
    'void main() { float a = texture(atlas, uv).a * col.a; o = vec4(col.rgb * a, a); }';

  function initGl(gl) {
    function shader(type, src) { var s = gl.createShader(type); gl.shaderSource(s, src); gl.compileShader(s); return s; }
    var prog = gl.createProgram();
    gl.attachShader(prog, shader(gl.VERTEX_SHADER, WAVE_VS)); gl.attachShader(prog, shader(gl.FRAGMENT_SHADER, WAVE_FS));
    gl.linkProgram(prog);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw new Error('wave shader');
    var vao = gl.createVertexArray(); gl.bindVertexArray(vao);
    var quad = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, quad);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([0, 0, 1, 0, 0, 1, 1, 1]), gl.STATIC_DRAW);
    gl.enableVertexAttribArray(0); gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
    var inst = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, inst);
    [[1, 4, 0], [2, 2, 16], [3, 4, 24]].forEach(function (a) {
      gl.enableVertexAttribArray(a[0]); gl.vertexAttribPointer(a[0], a[1], gl.FLOAT, false, 40, a[2]); gl.vertexAttribDivisor(a[0], 1);
    });
    var tex = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.useProgram(prog); gl.uniform1i(gl.getUniformLocation(prog, 'atlas'), 0);
    gl.enable(gl.BLEND); gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA); gl.clearColor(0, 0, 0, 0);
    return { gl: gl, prog: prog, vao: vao, inst: inst, tex: tex, view: gl.getUniformLocation(prog, 'view'), texU: gl.getUniformLocation(prog, 'tex'),
      buf: new Float32Array(10 * 12000), n: 0, inks: [], texW: 1, texH: 1 };
  }

  function flushGl(G, w, h) {
    var gl = G.gl;
    gl.viewport(0, 0, w, h); gl.clear(gl.COLOR_BUFFER_BIT);
    if (!G.n) return;
    gl.useProgram(G.prog); gl.bindVertexArray(G.vao);
    gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, G.tex);
    gl.uniform2f(G.view, w, h); gl.uniform2f(G.texU, G.texW, G.texH);
    gl.bindBuffer(gl.ARRAY_BUFFER, G.inst); gl.bufferData(gl.ARRAY_BUFFER, G.buf.subarray(0, G.n * 10), gl.STREAM_DRAW);
    gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, G.n);
  }

  function WaveBackground(p) {
    var ref = React.useRef(null);
    // Speed and words are read through refs so changing them steers the running sea instead of restarting it.
    var speedRef = React.useRef(1), wordsRef = React.useRef([]), pausedRef = React.useRef(false), wakeRef = React.useRef(null);
    speedRef.current = p.speed == null ? 1 : p.speed; wordsRef.current = p.words || []; pausedRef.current = !!p.paused;
    var reveal = p.reveal !== false;
    React.useEffect(function () {
      var c = ref.current; if (!c) return;
      // The sea is thousands of tiny glyphs. WebGL2 stamps them all in one instanced draw call; the 2D canvas
      // fallback stamps them one drawImage at a time, which costs ~10 ms a frame on a wide screen.
      var gl = null;
      try { gl = c.getContext('webgl2', { alpha: true, premultipliedAlpha: true, antialias: false, depth: false, stencil: false, powerPreference: 'low-power' }); } catch (e0) { gl = null; }
      var ctx = gl ? null : c.getContext('2d'), G = null;
      if (gl) { try { G = initGl(gl); } catch (e1) { G = null; } }
      // The canvas is bound to WebGL now, so a failed setup leaves a still, empty sea rather than a broken page.
      if (gl && !G) return;
      c.addEventListener('webglcontextlost', function (e) { e.preventDefault(); });
      c.addEventListener('webglcontextrestored', function () {
        try { G = initGl(gl); buildAtlas(); rows.forEach(function (r) { r.A = atlas[Math.round(r.d * (B - 1))]; }); } catch (e2) { G = null; }
      });
      var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
      var CHARS = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_x abcdefghijklmnopqrstuvwyz.,-/:@+#%"()×·✓→', IDX = {}, B = 8, FS0 = 6, FS1 = 22;
      for (var ci0 = 0; ci0 < CHARS.length; ci0++) IDX[CHARS[ci0]] = ci0;
      var inks = [], atlas = [], dpr = 1, W = 1, H = 1, rows = [], errors = [], raf, t0 = performance.now(), last = -1e9, lastSpawn = -1;
      var sp = speedRef.current, ts = 0, lastNow = null, lastWord = -1, wordIdx = 0, SK = 3, hbuf = new Float32Array(512);
      function rnd() { return CHARS[(Math.random() * 16) | 0]; }
      // Glyphs are drawn once per size bucket and ink into small sprite strips; each frame only stamps them.
      function buildAtlas() {
        var cs = getComputedStyle(c); inks = [];
        for (var i = 1; i <= 8; i++) inks.push(cs.getPropertyValue('--wave-' + i).trim() || '#888888');
        inks.push(cs.getPropertyValue('--ink').trim() || '#ffffff');
        inks.push(cs.getPropertyValue('--threat').trim() || '#ff5a4f');
        inks.push(cs.getPropertyValue('--accent').trim() || '#7aa7ff');
        atlas = [];
        if (G) { buildGlAtlas(); return; }
        for (var b = 0; b < B; b++) {
          var fs = FS0 + b * (FS1 - FS0) / (B - 1), px = fs * dpr, cw = Math.ceil(px * 0.7) + 2, chh = Math.ceil(px * 1.4), strips = [];
          for (var k = 0; k < inks.length; k++) {
            var o = document.createElement('canvas'); o.width = cw * CHARS.length; o.height = chh;
            var g = o.getContext('2d'); g.font = '500 ' + px + 'px "Geist Mono", ui-monospace, monospace'; g.textBaseline = 'middle'; g.fillStyle = inks[k];
            for (var q = 0; q < CHARS.length; q++) g.fillText(CHARS[q], q * cw + 1, chh / 2);
            strips.push(o);
          }
          atlas.push({ fs: fs, cw: cw, chh: chh, strips: strips });
        }
      }
      // One white glyph sheet (a row per size bucket) uploaded as a texture; each glyph is tinted on the GPU.
      function buildGlAtlas() {
        var probe = document.createElement('canvas').getContext('2d', { willReadFrequently: true });
        G.inks = inks.map(function (col) {
          probe.clearRect(0, 0, 1, 1); probe.fillStyle = '#000'; probe.fillStyle = col; probe.fillRect(0, 0, 1, 1);
          var d = probe.getImageData(0, 0, 1, 1).data; return [d[0] / 255, d[1] / 255, d[2] / 255];
        });
        var sheetW = 1, sheetH = 0;
        for (var b = 0; b < B; b++) {
          var fs = FS0 + b * (FS1 - FS0) / (B - 1), px = fs * dpr, cw = Math.ceil(px * 0.7) + 2, chh = Math.ceil(px * 1.4);
          atlas.push({ fs: fs, cw: cw, chh: chh, px: px, sy: sheetH });
          sheetW = Math.max(sheetW, cw * CHARS.length); sheetH += chh;
        }
        var o = document.createElement('canvas'); o.width = sheetW; o.height = sheetH;
        var g = o.getContext('2d'); g.textBaseline = 'middle'; g.fillStyle = '#fff';
        atlas.forEach(function (A) {
          g.font = '500 ' + A.px + 'px "Geist Mono", ui-monospace, monospace';
          for (var q = 0; q < CHARS.length; q++) g.fillText(CHARS[q], q * A.cw + 1, A.sy + A.chh / 2);
        });
        gl.bindTexture(gl.TEXTURE_2D, G.tex);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, o);
        G.texW = sheetW; G.texH = sheetH;
      }
      // A resize only re-fits the sea: characters, surfacing errors and the clock carry over, so it never restarts.
      function layout() {
        var r = c.getBoundingClientRect(), nd = Math.min(window.devicePixelRatio || 1, 1.25);
        var nW = Math.max(1, Math.round(r.width)), nH = Math.max(1, Math.round(r.height));
        if (rows.length && nW === W && nH === H && nd === dpr) return false;
        var dprChanged = nd !== dpr || !atlas.length;
        dpr = nd; W = nW; H = nH;
        c.width = Math.round(W * dpr); c.height = Math.round(H * dpr); if (ctx) ctx.setTransform(1, 0, 0, 1, 0, 0);
        if (dprChanged) buildAtlas();
        var R = 24, horizon = H * 0.24, old = rows; rows = [];
        for (var j = 0; j < R; j++) {
          var d = (j + 1) / R, b = Math.round(d * (B - 1)), A = atlas[b], step = A.fs * 0.66, n = Math.ceil(W / step) + 3;
          var ch = old[j] ? old[j].ch.slice(0, n) : [];
          while (ch.length < n) ch.push(rnd());
          rows.push({ d: d, y: horizon + Math.pow(d, 1.7) * (H * 1.04 - horizon), A: A, step: step, n: n, ch: ch });
          if (Math.ceil(n / SK) + 2 > hbuf.length) hbuf = new Float32Array(Math.ceil(n / SK) + 2);
        }
        return true;
      }
      // Surfacing text: red error codes, or (while something is being scanned) the data being found, in accent.
      function spawn(t, txt, kind) {
        var lo = kind === 'data' ? 9 : 5, j = lo + ((Math.random() * (rows.length - lo)) | 0), row = rows[j];
        var base = Math.floor(ts * (4 + row.d * 10) / row.step), vis = Math.floor(W / row.step);
        errors.push({ j: j, v0: base + ((Math.random() * Math.max(1, vis - txt.length)) | 0), txt: txt, kind: kind, born: t, life: 3.5 + Math.random() * 3 });
      }
      function draw(now) {
        var t = reduce ? 6 : (now - t0) / 1000;
        if (reduce) ts = 6 * speedRef.current;
        else { var dt = lastNow == null ? 0 : Math.min(0.1, (now - lastNow) / 1000); lastNow = now; sp += (speedRef.current - sp) * Math.min(1, dt * 1.4); ts += dt * sp; }
        // Reveal rises like a tide: a soft line sweeps from the bottom edge up past the horizon.
        var rv = reveal && !reduce ? Math.min(1, t / 2.2) : 1, TIDE = 90, tide = H - (1 - Math.pow(1 - rv, 3)) * (H + TIDE * 2);
        if (ctx) ctx.clearRect(0, 0, c.width, c.height); else G.n = 0;
        var words = wordsRef.current;
        if (reduce) { if (!errors.length) for (var s = 0; s < 6; s++) spawn(t, ERRS[s % ERRS.length], 'err'); }
        else {
          if (words.length && t - lastWord > 0.3 && errors.length < 18) { lastWord = t; spawn(t, words[wordIdx++ % words.length], 'data'); }
          if (t - lastSpawn > (words.length ? 1.6 : 0.7) && errors.length < 18) { lastSpawn = t; spawn(t, ERRS[(Math.random() * ERRS.length) | 0], 'err'); }
          errors = errors.filter(function (e) { return t - e.born < e.life; });
        }
        for (var j = 0; j < rows.length; j++) {
          if (rows[j].y + 40 < tide) continue;
          var row = rows[j], A = row.A, sc = A.fs / 20, drift = ts * (4 + row.d * 10), base = Math.floor(drift / row.step), frac = drift - base * row.step;
          var haze = 0.22 + 0.78 * row.d, rowErr = [];
          for (var q0 = 0; q0 < errors.length; q0++) if (errors[q0].j === j) rowErr.push(errors[q0]);
          if (!reduce) for (var m = 0; m < 2; m++) row.ch[(Math.random() * row.n) | 0] = rnd();
          var gw = A.cw, gh = A.chh, dw = gw, dh = gh, oy = gh / 2;
          // Swell height is sampled every SK characters and blended between samples: same shape, a third of the trig.
          for (var si = 0, ns = Math.ceil(row.n / SK) + 1; si < ns; si++) {
            var wxs = (si * SK * row.step - frac - W / 2) / sc, hsum = 0;
            for (var k = 0; k < SWELLS.length; k++) { var S = SWELLS[k]; hsum += S.a * Math.sin(S.kx * wxs + S.kz * j - S.w * ts + S.p); }
            hbuf[si] = hsum;
          }
          for (var i = 0; i < row.n; i++) {
            var x = i * row.step - frac;
            var s0 = (i / SK) | 0, fr = (i - s0 * SK) / SK, hs = hbuf[s0] + (hbuf[s0 + 1] - hbuf[s0]) * fr;
            var y = row.y - hs * 17 * sc, crest = (hs / SWELL_SUM + 1) / 2;
            if (y < tide) continue;
            var v = i + base, chr = row.ch[((v % row.n) + row.n) % row.n], ink, alpha, er = null;
            for (var q = 0; q < rowErr.length; q++) { var e = rowErr[q]; if (v >= e.v0 && v < e.v0 + e.txt.length) { er = e; chr = e.txt[v - e.v0]; break; } }
            if (er) {
              var age = t - er.born; alpha = (reduce ? 1 : Math.max(0, Math.min(1, age / 0.5, (er.life - age) / 0.8))) * (0.6 + 0.4 * row.d); ink = er.kind === 'data' ? 10 : 9;
            } else if (crest > 0.86) { alpha = haze * 0.95; ink = 8; }
            else {
              var cb = Math.floor((x / W * 0.9 + row.d * 0.35 + ts * 0.015) * 8);
              alpha = haze * (0.14 + 0.8 * crest * crest); ink = ((cb % 8) + 8) % 8;
            }
            if (y - tide < TIDE) alpha *= (y - tide) / TIDE;
            var gi = IDX[chr]; if (gi == null || alpha <= 0.02) continue;
            var px0 = Math.round(x * dpr), py0 = Math.round(y * dpr - oy);
            if (ctx) {
              ctx.globalAlpha = alpha;
              ctx.drawImage(A.strips[ink], gi * gw, 0, gw, gh, px0, py0, dw, dh);
            } else {
              if (G.n * 10 >= G.buf.length) { var grown = new Float32Array(G.buf.length * 2); grown.set(G.buf); G.buf = grown; }
              var o = G.n++ * 10, rgb = G.inks[ink], bf = G.buf;
              bf[o] = px0; bf[o + 1] = py0; bf[o + 2] = gw; bf[o + 3] = gh; bf[o + 4] = gi * gw; bf[o + 5] = A.sy;
              bf[o + 6] = rgb[0]; bf[o + 7] = rgb[1]; bf[o + 8] = rgb[2]; bf[o + 9] = alpha;
            }
          }
        }
        if (ctx) ctx.globalAlpha = 1; else flushGl(G, c.width, c.height);
      }
      // Light by design: 30 fps, woken by a timer (not every display refresh), easing to 16 fps if a frame costs
      // more than 6 ms, and fully stopped while paused or while the window is hidden.
      var stats = c._otWave = { ms: 0, interval: 33, frames: 0, gl: !!G }, timer = null;
      function frame(now) {
        raf = null;
        if (pausedRef.current || document.hidden || (!ctx && !G)) return;
        var t1 = performance.now(); draw(now); var cost = performance.now() - t1;
        stats.ms = stats.ms * 0.9 + cost * 0.1; stats.frames++;
        stats.interval = stats.ms > 6 ? 62 : stats.ms < 4 ? 33 : stats.interval;
        // Wake a little early so the next display refresh lands on schedule instead of one refresh late.
        timer = setTimeout(schedule, Math.max(0, stats.interval - cost - 5));
      }
      function schedule() { timer = null; if (!raf) raf = requestAnimationFrame(frame); }
      function wake() { if (!reduce && !raf && !timer && !pausedRef.current && !document.hidden) { lastNow = null; schedule(); } }
      wakeRef.current = wake;
      document.addEventListener('visibilitychange', wake);
      function redraw() { if (reduce) draw(performance.now()); }
      layout();
      var ro = window.ResizeObserver ? new ResizeObserver(function () { if (layout()) { if (reduce) redraw(); else draw(performance.now()); } }) : null; if (ro) ro.observe(c);
      var mo = new MutationObserver(function () { buildAtlas(); rows.forEach(function (r) { r.A = atlas[Math.round(r.d * (B - 1))]; }); redraw(); });
      mo.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
      if (document.fonts && document.fonts.ready) document.fonts.ready.then(function () { buildAtlas(); rows.forEach(function (r) { r.A = atlas[Math.round(r.d * (B - 1))]; }); redraw(); });
      if (reduce) draw(performance.now()); else schedule();
      return function () { if (raf) cancelAnimationFrame(raf); clearTimeout(timer); document.removeEventListener('visibilitychange', wake); if (ro) ro.disconnect(); mo.disconnect(); };
    }, [reveal]);
    // Resume the sea when un-paused.
    React.useEffect(function () { if (!p.paused && wakeRef.current) wakeRef.current(); }, [p.paused]);
    return h('div', { className: cx('ot-wave', p.className), 'aria-hidden': true },
      h('canvas', { ref: ref, className: 'ot-wave-canvas' }),
      h('div', { className: 'ot-wave-sheen' }));
  }

  function GlassPanel(p) {
    return h(p.as || 'div', Object.assign(omit(p, ['as', 'strong', 'className', 'children']), {
      className: cx('ot-glass', p.strong && 'ot-glass-strong', p.className)
    }), p.children);
  }

  function NeuCard(p) {
    return h(p.as || 'div', Object.assign(omit(p, ['as', 'inset', 'className', 'children']), {
      className: cx('ot-neu', p.inset && 'ot-neu-inset', p.className)
    }), p.children);
  }

  /* Animates its height to whatever its children measure, so content that appears, swaps or
     disappears slides open and closed instead of jumping the layout. */
  function Expand(p) {
    var outer = React.useRef(null), inner = React.useRef(null);
    React.useLayoutEffect(function () {
      var o = outer.current, i = inner.current; if (!o || !i) return;
      function fit() { o.style.height = i.offsetHeight + 'px'; }
      fit();
      var ro = window.ResizeObserver ? new ResizeObserver(fit) : null; if (ro) ro.observe(i);
      return function () { if (ro) ro.disconnect(); };
    }, []);
    var has = p.children != null && p.children !== false;
    return h('div', { ref: outer, className: cx('ot-expand', p.className), 'aria-hidden': has ? undefined : true },
      h('div', { ref: inner, className: 'ot-expand-inner' },
        has && h('div', { key: p.contentKey, className: 'ot-expand-content' }, p.children)));
  }

  function Button(p) {
    var v = p.variant || 'primary', s = p.size || 'md';
    return h('button', Object.assign({ type: 'button' }, omit(p, ['variant', 'size', 'icon', 'iconRight', 'className', 'children']), {
      className: cx('ot-btn', 'ot-btn-' + v, 'ot-btn-' + s, p.className)
    }), p.icon && h(Icon, { name: p.icon, size: s === 'sm' ? 16 : 18 }), p.children, p.iconRight && h(Icon, { name: p.iconRight, size: s === 'sm' ? 16 : 18 }));
  }

  function ProviderOption(p) {
    var sel = !!p.selected;
    return h('button', {
      type: 'button', role: 'radio', 'aria-checked': sel, className: cx('ot-provider', sel && 'is-selected', p.className),
      onClick: function () { if (p.onSelect) p.onSelect(p.value); }
    },
      h('span', { className: 'ot-provider-glyph' }, p.icon ? h(Icon, { name: p.icon, size: 22 }) : h('span', { className: 'text-mono-sm' }, p.glyph)),
      h('span', { className: 'ot-provider-text' },
        h('span', { className: 'text-headline' }, p.title),
        p.description && h('span', { className: 'ot-muted text-callout' }, p.description)),
      h('span', { className: 'ot-provider-check', 'aria-hidden': true }, sel && h(Icon, { name: 'check', size: 14, strokeWidth: 2.5 })));
  }

  function SegmentedControl(p) {
    return h('div', { className: cx('ot-seg', p.className), role: 'radiogroup', 'aria-label': p.label },
      (p.options || []).map(function (o) {
        var sel = o.value === p.value;
        return h('button', { key: o.value, type: 'button', role: 'radio', 'aria-checked': sel, className: cx('ot-seg-opt', 'text-callout', sel && 'is-selected'),
          onClick: function () { if (p.onChange) p.onChange(o.value); } }, o.label);
      }));
  }

  function Toggle(p) {
    return h('label', { className: cx('ot-toggle', p.disabled && 'is-disabled', p.className) },
      h('input', { type: 'checkbox', role: 'switch', 'aria-label': p.ariaLabel, checked: !!p.checked, disabled: p.disabled, onChange: function (e) { if (p.onChange) p.onChange(e.target.checked); } }),
      h('span', { className: 'ot-toggle-track', 'aria-hidden': true }, h('span', { className: 'ot-toggle-knob' })),
      p.label && h('span', { className: 'text-callout' }, p.label));
  }

  function KeyInput(p) {
    var st = React.useState(false), shown = st[0], setShown = st[1];
    var id = React.useMemo(function () { return 'ot-key-' + Math.random().toString(36).slice(2, 8); }, []);
    return h('div', { className: cx('ot-fieldset', p.className) },
      p.label && h('label', { className: 'text-caption ot-muted', htmlFor: id }, p.label),
      h('div', { className: cx('ot-field', p.error && 'is-error') },
        h(Icon, { name: 'key', size: 18, className: 'ot-muted' }),
        h('input', { id: id, className: 'text-mono', type: shown ? 'text' : 'password', value: p.value || '', placeholder: p.placeholder || 'sk-…', autoComplete: 'off', spellCheck: false,
          onChange: function (e) { if (p.onChange) p.onChange(e.target.value); } }),
        h('button', { type: 'button', className: 'ot-field-btn', 'aria-label': shown ? 'Hide key' : 'Show key', onClick: function () { setShown(!shown); } }, h(Icon, { name: 'eye', size: 18 }))),
      (p.error || p.hint) && h('span', { className: cx('text-callout', p.error ? 'ot-error' : 'ot-muted') }, p.error || p.hint));
  }

  var KINDS = {
    threat: { label: 'Threat', icon: 'shield' },
    exposure: { label: 'Exposure', icon: 'network' },
    reliability: { label: 'Reliability', icon: 'repeat' },
    performance: { label: 'Performance', icon: 'bolt' },
    verified: { label: 'Verified', icon: 'check' },
    gap: { label: 'Not checked', icon: 'gap' }
  };

  function SeverityBadge(p) {
    var k = KINDS[p.kind] || KINDS.gap;
    return h('span', { className: cx('ot-badge', 'ot-badge-' + (p.kind || 'gap'), p.className) }, h(Icon, { name: p.icon || k.icon, size: 14, strokeWidth: 2 }), p.children || k.label);
  }

  function FindingCard(p) {
    return h(NeuCard, { as: 'article', className: cx('ot-finding', p.kind === 'gap' && 'ot-finding-gap', p.className) },
      h('div', { className: 'ot-finding-head' },
        h(SeverityBadge, { kind: p.kind }),
        p.confidence && h('span', { className: 'text-mono-sm ot-muted' }, p.confidence)),
      h('h3', { className: 'text-headline' }, p.title),
      h('p', { className: 'text-body ot-muted' }, p.observation),
      h('div', { className: 'ot-finding-meta text-mono-sm ot-muted' }, [p.source, p.observedAt].filter(Boolean).join(' · ')),
      h('div', { className: 'ot-finding-foot' },
        p.undo && h('span', { className: 'ot-finding-undo text-callout ot-muted' }, h(Icon, { name: 'undo', size: 16 }), p.undo),
        h('div', { className: 'ot-finding-actions' }, (p.actions || []).map(function (a, i) {
          return h(Button, { key: i, size: 'sm', variant: a.variant || (i === 0 ? 'primary' : 'neu'), icon: a.icon, onClick: a.onClick }, a.label);
        }))));
  }

  function ScanProgress(p) {
    var cats = p.categories || [];
    var done = cats.filter(function (c) { return c.state === 'done' || c.state === 'unavailable'; }).length;
    var pct = cats.length ? Math.round(cats.reduce(function (s, c) { return s + (c.state === 'done' || c.state === 'unavailable' ? 100 : (c.pct || 0)); }, 0) / cats.length) : 0;
    var label = { done: 'Done', running: '…', queued: 'Queued', unavailable: 'No access' };
    return h(GlassPanel, { className: cx('ot-progress', p.className), style: p.style },
      h('div', { className: 'ot-progress-head' },
        h('div', null,
          h('div', { className: 'text-headline' }, 'Scan · ' + done + '/' + cats.length)),
        p.onStop !== null && h(Button, { variant: 'neu', size: 'sm', onClick: p.onStop }, 'Stop')),
      h('div', { className: 'ot-track ot-track-rainbow', role: 'progressbar', 'aria-valuenow': pct, 'aria-valuemin': 0, 'aria-valuemax': 100 }, h('span', { style: { width: pct + '%' } })),
      h('ul', { className: 'ot-progress-list' }, cats.map(function (c) {
        return h('li', { key: c.name, className: 'ot-progress-row is-' + c.state },
          h('span', { className: 'ot-progress-dot', 'aria-hidden': true }, c.state === 'done' ? h(Icon, { name: 'check', size: 14, strokeWidth: 2.5 }) : c.state === 'unavailable' ? h(Icon, { name: 'gap', size: 14, strokeWidth: 2 }) : null),
          h('span', { className: 'text-callout' }, c.name),
          h('span', { className: 'text-mono-sm ot-muted' }, c.note || (c.state === 'running' ? (c.pct || 0) + '%' : label[c.state])));
      })));
  }

  function ConnectionPill(p) {
    var status = p.status || 'connected';
    return h(GlassPanel, { className: cx('ot-pill', 'is-' + status, p.onChange == null && 'is-bare', p.className) },
      h('span', { className: 'ot-pill-dot', 'aria-hidden': true }),
      h(p.onOpen ? 'button' : 'span', { className: cx('ot-pill-text', p.onOpen && 'ot-pill-open'), type: p.onOpen ? 'button' : undefined,
          'aria-expanded': p.onOpen ? !!p.open : undefined, onClick: p.onOpen || undefined },
        h('span', { className: 'text-callout' }, p.provider || 'Local only'),
        (p.detail || status === 'expired' || status === 'live') && h('span', { className: 'text-mono-sm ot-muted' }, p.detail || (status === 'expired' ? 'Expired' : 'Live'))),
      p.onActivity && h(Button, { variant: 'ghost', size: 'sm', 'aria-pressed': !!p.activityOpen, onClick: p.onActivity }, 'Activity'),
      p.onChange !== null && h(Button, { variant: 'ghost', size: 'sm', onClick: p.onChange }, p.actionLabel || 'Change'));
  }

  function GrantRow(p) {
    return h('div', { className: cx('ot-grant-row', p.locked && 'is-locked') },
      h('div', { className: 'ot-grant-text' },
        h('span', { className: 'text-callout', style: { fontWeight: 600 } }, p.title),
        p.detail && h('span', { className: 'text-mono-sm ot-muted' }, p.detail)),
      p.locked ? h('span', { className: 'ot-grant-lock text-mono-sm ot-muted' }, h(Icon, { name: 'lock', size: 14 }), 'Never') :
        p.fixed ? h('span', { className: 'ot-grant-lock text-mono-sm ot-muted' }, h(Icon, { name: 'check', size: 14, strokeWidth: 2.5 }), 'Shared') :
        h(Toggle, { checked: p.checked, onChange: p.onChange, disabled: p.compact, ariaLabel: p.title }));
  }

  function PermissionGrant(p) {
    var a = React.useState(true), b = React.useState(true), c = React.useState(true), d = React.useState(true);
    var who = p.recipient || 'Claude Code';
    return h(GlassPanel, { className: cx('ot-grant', p.compact && 'is-compact', p.className) },
      !p.compact && h('div', { className: 'ot-grant-head' },
        h('div', { className: 'text-headline' }, p.goal || 'Loud on battery')),
      h('div', { className: 'ot-grant-cols' },
        h('section', null,
          h('h3', { className: 'text-caption ot-muted' }, 'Can do'),
          h(GrantRow, { title: 'Run checks', detail: 'Up to 10', checked: a[0], onChange: a[1], compact: p.compact }),
          h(GrantRow, { title: 'Suggest changes', detail: 'You approve each', checked: b[0], onChange: b[1], compact: p.compact })),
        h('section', null,
          h('h3', { className: 'text-caption ot-muted' }, 'Can share with ' + who),
          h(GrantRow, { title: 'Findings (3)', detail: 'Redacted', checked: c[0], onChange: c[1], compact: p.compact }),
          h(GrantRow, { title: 'New results', detail: 'Up to 128 KiB', checked: d[0], onChange: d[1], compact: p.compact }),
          h(GrantRow, { title: 'Personal data', locked: true }))),
      h('div', { className: 'ot-grant-foot' },
        h('span', { className: 'text-mono-sm ot-muted' }, 'Ends after 15 min idle'),
        !p.compact && h('div', { className: 'ot-grant-actions' },
          h(Button, { variant: 'ghost' }, 'Explain only'),
          h(Button, { variant: 'primary', iconRight: 'arrow' }, 'Start'))));
  }

  var STEP = {
    running: { icon: null, label: 'Running' },
    done: { icon: 'check', label: 'Done' },
    awaiting: { icon: 'alert', label: 'Needs you' },
    verified: { icon: 'check', label: 'Verified' },
    client: { icon: 'terminal', label: 'Reported' },
    queued: { icon: null, label: 'Next' },
    declined: { icon: 'gap', label: 'Declined' }
  };

  function AgentActivity(p) {
    var steps = p.steps || [], b = p.budget || {};
    function meter(label, v) {
      if (!v) return null;
      return h('div', { className: 'ot-meter' },
        h('div', { className: 'ot-meter-label text-mono-sm ot-muted' }, h('span', null, label), h('span', null, v[0] + ' / ' + v[1] + (v[2] || ''))),
        h('div', { className: 'ot-track' }, h('span', { style: { width: Math.min(100, v[0] / v[1] * 100) + '%' } })));
    }
    return h(GlassPanel, { className: cx('ot-activity', p.className) },
      h('div', { className: 'ot-activity-head' },
        h('div', null,
          h('div', { className: 'ot-live text-caption' }, h('span', { className: 'ot-live-dot', 'aria-hidden': true }), (p.agent || 'Claude Code') + ' · live'),
          h('h2', { className: 'text-headline' }, p.goal)),
        h(Button, { variant: 'neu', size: 'sm', icon: 'stop', onClick: p.onStop }, 'Stop')),
      h('ol', { className: 'ot-steps' }, steps.map(function (s, i) {
        var st = STEP[s.state] || STEP.done;
        return h('li', { key: i, className: 'ot-step is-' + s.state },
          h('span', { className: 'ot-step-mark', 'aria-hidden': true }, st.icon && h(Icon, { name: st.icon, size: 14, strokeWidth: 2.25 })),
          h('div', { className: 'ot-step-body' },
            h('div', { className: 'ot-step-top' },
              h('span', { className: cx('ot-origin', s.origin === 'client' && 'is-client', 'text-mono-sm') }, s.origin === 'client' ? (p.agent || 'Claude Code') : 'Optimizer'),
              h('span', { className: 'text-mono-sm ot-muted' }, s.tool)),
            h('div', { className: 'text-callout ot-step-title' }, s.title),
            s.detail && h('div', { className: 'text-mono-sm ot-muted' }, s.detail)),
          h('span', { className: 'ot-step-state text-mono-sm' }, st.label));
      })),
      h('div', { className: 'ot-meters' }, meter('Checks', b.diagnostics), meter('Shared', b.shared), meter('Turns', b.turns)));
  }

  function ApprovalCard(p) {
    var rows = p.rows || [], details = p.details || [];
    return h(NeuCard, { as: 'section', className: cx('ot-approval', p.className) },
      h('div', { className: 'ot-approval-head' },
        h(SeverityBadge, { kind: 'exposure', icon: 'alert' }, 'Approve?'),
        h('span', { className: 'text-mono-sm ot-muted' }, p.requestedBy || 'Claude Code')),
      h('h3', { className: 'text-title-2' }, p.title),
      p.actionId && h('div', { className: 'text-mono-sm ot-muted' }, p.actionId),
      h('div', { className: 'ot-diff' }, rows.map(function (r) {
        return h('div', { key: r.label, className: 'ot-diff-row' },
          h('span', { className: 'text-caption ot-muted' }, r.label),
          h('span', { className: 'ot-diff-before text-mono' }, r.before),
          h(Icon, { name: 'arrow', size: 16, className: 'ot-muted' }),
          h('span', { className: 'ot-diff-after text-mono' }, r.after));
      })),
      h('dl', { className: 'ot-approval-details' }, details.map(function (d) {
        return h(React.Fragment, { key: d[0] }, h('dt', { className: 'text-callout ot-muted' }, d[0]), h('dd', { className: 'text-callout' }, d[1]));
      })),
      h('div', { className: 'ot-approval-foot' },
        h('div', { className: 'ot-approval-actions' },
          h(Button, { variant: 'ghost', onClick: p.onDecline }, 'Decline'),
          h(Button, { variant: 'primary', icon: 'check', onClick: p.onApprove }, 'Approve'))));
  }

  var PROVIDERS = [
    { value: 'claude-code', glyph: 'CC', title: 'Claude Code', name: 'Claude Code', shares: 'Only what you allow' },
    { value: 'codex', glyph: 'Cx', title: 'Codex', name: 'Codex', shares: 'Only what you allow' },
    { value: 'chatgpt', glyph: 'GPT', title: 'ChatGPT', name: 'ChatGPT', shares: 'Only what you allow · uses your ChatGPT plan', needsKind: 'chatgpt_plan' },
    { value: 'api-key', glyph: 'API', title: 'API key', shares: 'Only what you allow · billed to your key' },
    { value: 'local', icon: 'device', title: 'Local only', shares: 'Nothing' }
  ];

  // A command to paste somewhere else, with Copy.
  function CopyField(p) {
    var s = React.useState(false), copied = s[0], setCopied = s[1], ref = React.useRef(null);
    function copy() {
      var done = function () { setCopied(true); setTimeout(function () { setCopied(false); }, 1600); };
      // Without clipboard access the text is selected instead, ready for Ctrl+C.
      var fail = function () { try { var r = document.createRange(); r.selectNodeContents(ref.current); var sel = window.getSelection(); sel.removeAllRanges(); sel.addRange(r); } catch (e) { /* nothing to select */ } if (p.onFail) p.onFail(); };
      try { navigator.clipboard.writeText(p.text).then(done, fail); } catch (e) { fail(); }
    }
    return h('div', { className: cx('ot-copy', p.className) },
      h('code', { ref: ref, className: 'text-mono-sm' }, p.text),
      h(Button, { variant: 'ghost', size: 'sm', icon: copied ? 'check' : 'file', onClick: copy }, copied ? 'Copied' : 'Copy'));
  }

  // A native client asks to pair: its code must match the one it shows. Approve opens the grant sheet.
  function PairingCard(p) {
    var body = [
      !p.inline && h('div', { key: 'h', className: 'text-headline' }, 'Pair ' + (p.client || 'Claude Code') + '?'),
      h('div', { key: 'c', className: 'ot-pair-code text-mono', 'aria-label': 'Code ' + p.code }, p.code),
      h('div', { key: 'f', className: 'ot-choice-foot' },
        h(Button, { variant: 'ghost', onClick: p.onDeny, disabled: !!p.busy }, 'Deny'),
        h(Button, { variant: 'primary', icon: 'check', onClick: p.onApprove, disabled: !!p.busy }, 'Approve'))];
    return p.inline ? h('div', { className: cx('ot-pair is-inline', p.className), role: 'group', 'aria-label': 'Pairing' }, body)
      : h(GlassPanel, { className: cx('ot-share ot-pair', p.className), role: 'group', 'aria-label': 'Pairing' }, body);
  }

  var TILE_KIND = { 'claude-code': 'claude_code_mcp', codex: 'codex_mcp', chatgpt: 'chatgpt_plan' };
  function tileKind(tile, api) { return tile === 'api-key' ? (api === 'openai' ? 'openai_api' : 'anthropic_api') : TILE_KIND[tile] || null; }
  var ACTION_LABEL = { addKey: 'Add key', updateKey: 'Update key', retry: 'Retry', signIn: 'Sign in' };
  var READY_ROWS = { paired: 1, active: 1, client_closed: 1 };
  // Waiting: what to do next, and after a minute how to check the registration.
  function WaitHelp(p) {
    return h(React.Fragment, null,
      p.hint && h('div', { className: 'text-callout ot-muted' }, p.hint),
      p.long && p.cli && h('div', { className: 'text-mono-sm ot-muted' }, 'Run ', h('code', null, p.cli + ' mcp list'), ' to check'));
  }
  function ConnectedLine() {
    return h('div', { className: 'ot-connected-line text-callout' }, h(Icon, { name: 'check', size: 16, strokeWidth: 2.5 }), 'Connected');
  }
  function ProviderDetails(p) {
    var cur = p.provider;
    if (cur.value === 'local') return null;
    var st = p.tileState || null;
    var off = st && st.disconnect && p.onDisconnect && h('div', { className: 'ot-detail-row' }, h('span', null),
      h(Button, { variant: 'ghost', size: 'sm', onClick: function () { p.onDisconnect(tileKind(cur.value, p.api)); } }, 'Disconnect'));
    // A failed check or sign-in says why in a line (connectionState hint); the bottom button offers the next step.
    var why = st && st.hint && h('div', { className: 'text-callout ot-muted', role: 'status' }, st.hint);
    if (cur.value === 'chatgpt') {
      var signing = p.connect && p.connect.tile === 'chatgpt';
      return h('div', { className: 'ot-detail' },
        st && st.row === 'connected' ? h(ConnectedLine, null)
          : signing || (st && st.row === 'checking') ? h('div', { className: 'text-callout ot-muted ot-waiting' }, 'Finish signing in in your browser.')
          : why || h('div', { className: 'text-callout ot-muted' }, 'Signs in with your ChatGPT account in your browser.'), off);
    }
    if (cur.value === 'api-key') {
      // The key is typed into the app's own Windows dialog, never into this page.
      return h('div', { className: 'ot-detail' },
        h(SegmentedControl, { label: 'Provider', value: p.api, onChange: p.setApi, options: [
          { value: 'anthropic', label: 'Anthropic' }, { value: 'openai', label: 'OpenAI' }] }), st && st.row === 'connected' && h(ConnectedLine, null), why, off);
    }
    var cv = p.client || null, g = nativeProgress(cv, st);
    var name = cur.name;
    function step(n, title, sub, more) {
      var done = g.step > n, now = g.step === n;
      return h('li', { key: n, className: cx('ot-cstep', done && 'is-done', now && 'is-now') },
        h('span', { className: 'ot-connect-num text-mono-sm' }, done ? h(Icon, { name: 'check', size: 14, strokeWidth: 2.5 }) : String(n)),
        h('div', { className: 'ot-cs-body' },
          h('div', { className: 'text-callout ot-cs-title' }, title),
          sub && h('div', { className: cx('text-callout ot-muted ot-cs-sub', now && cv && cv.signing && 'ot-waiting') }, sub),
          (now || (n === 3 && g.ready)) && more));
    }
    // Not installed, blocked on this PC, or too old: the same install step (the official installer is the other way in).
    var installMore = cv && cv.notInstalled && h(React.Fragment, null,
      cv.failed && h('div', { className: 'text-callout ot-muted', role: 'status' }, cv.failed),
      h('div', { className: 'ot-detail-row ot-install-row' },
        h(Button, { variant: 'neu', size: 'sm', icon: 'terminal', disabled: cv.installing, onClick: cv.onInstall },
          cv.installing ? (cv.outdated ? 'Updating…' : 'Installing…') : cv.outdated ? 'Update with PowerShell' : 'Install with PowerShell'),
        !cv.app && !(cv.blocked && !cv.reinstall) && h(Button, { variant: 'ghost', size: 'sm', icon: 'repeat', onClick: cv.onCheck }, 'Check again'),
        cv.installCmd && !p.cmdOpen && h(Button, { variant: 'ghost', size: 'sm', onClick: p.onShowCommand }, 'Show command')),
      cv.installCmd && p.cmdOpen && h(CopyField, { text: cv.installCmd, onFail: p.onCopyFail }));
    // Sign-in runs in a window of its own; the step re-checks by itself, Check again is for the impatient.
    // When the check gave no answer, the bottom button checks again and Sign in stays here as the other way.
    var signMore = cv && cv.installed && h(React.Fragment, null,
      cv.failed && h('div', { className: 'text-callout ot-muted', role: 'status' }, cv.failed),
      cv.signing && h('div', null, h(Button, { variant: 'ghost', size: 'sm', icon: 'repeat', onClick: cv.onCheck }, 'Check again')),
      cv.unknown && h('div', null, h(Button, { variant: 'ghost', size: 'sm', onClick: cv.onSignIn }, 'Sign in')));
    var installTitle = cv && cv.blocked ? cv.title : cv && cv.outdated ? 'Update ' + name : 'Install ' + name;
    var installSub = g.step !== 1 || !cv ? null : cv.blocked || cv.outdated ? cv.note : cv.app ? 'Open the Code tab in Claude once' : null;
    var signSub = g.step === 2 && cv && cv.signing ? 'Finish in the window that opened'
      : g.step === 2 && cv && cv.unknown ? 'Couldn’t check if you’re signed in.'
      : g.step > 2 && cv && cv.billing ? cv.billing : null;
    // Optional: the connector in the client's own window too (m3 pairing path).
    var alsoMore = cv && cv.installed && (cv.registered === 'no' || cv.registered === 'outdated') &&
      h('div', null, h(Button, { variant: 'ghost', size: 'sm', disabled: !!cv.adding, onClick: cv.onRegister }, cv.adding || 'Also use in ' + name + '’s window'));
    return h('div', { className: 'ot-detail' },
      h('ol', { className: 'ot-connect-steps' },
        step(1, g.step > 1 ? 'Installed' : installTitle, installSub, installMore),
        step(2, g.step > 2 ? 'Signed in' : 'Sign in', signSub, signMore),
        step(3, 'Connected', null, alsoMore)),
      g.ready && off);
  }

  // Where Claude Code / Codex setup stands (m5 §6): 1 install, 2 sign in, 3 connected (4: all done).
  function nativeProgress(cv, st) {
    var row = st && st.row;
    var ready = !!(row === 'connected' || (row && READY_ROWS[row]));
    if (ready) return { step: 4, ready: true };
    if (cv && cv.notInstalled) return { step: 1, ready: false };
    if (row === 'signed_out' || (cv && cv.installed)) return { step: 2, ready: false };
    return { step: 1, ready: false };
  }

  function LoginPanel(p) {
    var s1 = React.useState(p.initialProvider || 'claude-code'), prov = s1[0], setProv = s1[1];
    var s2 = React.useState(p.initialApi || 'anthropic');
    var s3 = React.useState({}), cmdOpen = s3[0], setCmdOpen = s3[1];
    // Selecting Claude Code / Codex checks whether the client is installed and signed in (shell.clientStatus).
    React.useEffect(function () { if (p.onTile) p.onTile(prov); }, [prov]);
    React.useEffect(function () { if (p.onTile) ['claude-code', 'codex'].forEach(function (t) { if (t !== prov) p.onTile(t); }); }, []);
    // A tile that needs a provider kind (ChatGPT) shows only when the backend offers it.
    var tiles = PROVIDERS.filter(function (x) { return !x.needsKind || (p.tileStates && p.tileStates[x.needsKind]); });
    var cur = tiles.filter(function (x) { return x.value === prov; })[0] || tiles[0];
    var views = p.clientViews || {}, cv = views[prov] || null;
    var states = p.tileStates || {};
    var st = states[tileKind(prov, s2[0])] || null;
    var details = ProviderDetails({ provider: cur, api: s2[0], setApi: s2[1], connect: p.connect, onApprove: p.onApprove, onDeny: p.onDeny, onCopyFail: p.onCopyFail,
      tileState: st, onDisconnect: p.onDisconnect, waitLong: p.waitLong, client: cv, cmdOpen: !!cmdOpen[prov], pairBusy: p.pairBusy,
      onShowCommand: function () { var n = Object.assign({}, cmdOpen); n[prov] = true; setCmdOpen(n); }, onRetryClient: function () { if (p.onClientAction) p.onClientAction(prov); } });
    var STRONG = { pending: 1, paired: 1, active: 1, client_closed: 1, bridge_rejected: 1, connected: 1, signed_out: 1 };
    var READY = { connected: 1, paired: 1, active: 1, client_closed: 1 };
    var primary = prov === 'local' ? 'Continue' : st && ACTION_LABEL[st.action] ? ACTION_LABEL[st.action] : st && READY[st.row] ? 'Continue'
      : st && st.row === 'not_eligible' ? 'Try again' : 'Connect';
    var API_NAMES = { openai: 'OpenAI (API)', anthropic: 'Claude (API)' };
    var c = p.connect && p.connect.tile === prov ? p.connect : null;
    var native = !!TILE_KIND[prov] && prov !== 'chatgpt', g = nativeProgress(cv, st);
    // Claude Code / Codex: one button that always does the next step: Get / Install, Sign in, Continue.
    function nativePrimary() {
      var act = function () { if (p.onClientAction) p.onClientAction(prov); };
      if (g.ready) return h(Button, { variant: 'primary', iconRight: 'chevron', onClick: function () { go(prov); } }, 'Continue');
      if (!cv) return h(Button, { variant: 'primary', icon: 'repeat', onClick: function () { if (p.onTile) p.onTile(prov); } }, 'Check again');
      if (cv.blocked) return h(Button, { variant: 'primary', icon: cv.reinstall ? null : 'repeat', iconRight: cv.reinstall ? 'chevron' : null, disabled: cv.reinstall && !!cv.installing, onClick: act }, cv.reinstall && cv.installing ? 'Installing…' : cv.primary);
      if (cv.outdated) return h(Button, { variant: 'primary', iconRight: 'chevron', disabled: !!cv.installing, onClick: act }, cv.installing ? 'Updating…' : cv.primary);
      if (g.step === 1) return h(Button, { variant: 'primary', iconRight: cv.external ? 'link' : 'chevron', disabled: !!cv.disabled, onClick: act }, cv.primary);
      if (cv.unknown) return h(Button, { variant: 'primary', icon: 'repeat', onClick: act }, 'Check again');
      return h(Button, { variant: 'primary', iconRight: 'chevron', onClick: act }, 'Sign in');
    }
    // Reports the choice with a display name for the connection pill ("Claude (API)", "Codex", "Local only").
    function go(choice) {
      if (!p.onConnect) return;
      var pick = PROVIDERS.filter(function (x) { return x.value === choice; })[0] || cur;
      p.onConnect(choice, choice === 'api-key' ? API_NAMES[s2[0]] : pick.title, s2[0]);
    }
    return h(GlassPanel, { strong: true, className: cx('ot-login-panel', 'ot-frost', p.className) },
      h('div', { className: 'ot-brand' },
        h(Logo, { size: 30 }),
        h('span', { className: 'text-callout' }, 'Optimus')),
      h('h1', { className: 'text-title-1' }, 'Choose your AI'),
      h('div', { className: 'ot-provider-grid', role: 'radiogroup', 'aria-label': 'AI help' },
        tiles.map(function (x) {
          var ts = states[tileKind(x.value, s2[0])];
          var xv = views[x.value];
          var word = xv && (!(ts && STRONG[ts.row]) || (xv.unknown && ts.row === 'signed_out')) ? xv.hint || x.description : ts && ts.word ? ts.word : x.description;
          return h(ProviderOption, Object.assign({ key: x.value, selected: x.value === prov, onSelect: setProv }, x, { description: word }));
        })),
      h(Expand, { contentKey: prov }, details),
      h('div', { className: 'ot-shares' },
        h(Icon, { name: 'eye', size: 18 }),
        h('div', null,
          h('div', { className: 'text-caption ot-muted' }, 'Shared'),
          h('div', { key: prov, className: 'text-callout ot-fade' }, cur.shares))),
      h('div', { className: 'ot-login-actions' },
        p.onBack && h(Button, { variant: 'ghost', icon: 'back', className: 'ot-login-back', onClick: p.onBack }, 'Back'),
        h(Button, { variant: 'ghost', onClick: function () { if (p.onLater) p.onLater(); else go('local'); } }, 'Later'),
        native ? nativePrimary()
          : c && c.phase === 'key' ? h(Button, { variant: 'primary', disabled: true }, 'Connecting…')
          : c && c.phase !== 'pairing' ? h(Button, { variant: 'primary', iconRight: 'chevron', onClick: p.onContinue }, 'Continue')
          : c ? null
          : st && st.row === 'checking' ? h(Button, { variant: 'primary', disabled: true }, 'Checking…')
          : cv && !(st && READY[st.row]) ? h(Button, { variant: 'primary', iconRight: cv.external ? 'link' : 'chevron', disabled: !!cv.busy || !!cv.disabled,
              onClick: function () { if (p.onClientAction) p.onClientAction(prov); } }, cv.busy || cv.primary)
          : h(Button, { variant: 'primary', iconRight: 'chevron', onClick: function () { go(prov); } }, primary)));
  }

  function LoginScreen(p) {
    return h('div', { className: cx('ot-login', p.className) },
      h(WaveBackground, null),
      h('div', { className: 'ot-login-scroll' }, h(LoginPanel, { initialProvider: p.initialProvider, onConnect: p.onConnect })));
  }

  /* ---------- Shared pieces of the main flow ---------- */

  function CheckRow(p) {
    return h('button', { type: 'button', role: p.multi ? 'checkbox' : 'radio', 'aria-checked': !!p.checked,
      className: cx('ot-check', p.checked && 'is-on', p.className), onClick: p.onClick, disabled: p.disabled },
      h('span', { className: cx('ot-check-box', !p.multi && 'is-round'), 'aria-hidden': true }, p.checked && h(Icon, { name: 'check', size: 14, strokeWidth: 2.5 })),
      h('span', { className: 'text-callout ot-check-label' }, p.label));
  }

  function AskField(p) {
    var s = React.useState(''), v = s[0], set = s[1], ref = React.useRef(null);
    React.useEffect(function () { if (p.focusToken && ref.current) ref.current.focus(); }, [p.focusToken]);
    React.useEffect(function () { if (p.restore && p.restore.text) set(p.restore.text); }, [p.restore && p.restore.token]);
    function send() { var t = v.trim(); if (!t) return; if (p.onSend) p.onSend(t); set(''); }
    return h('form', { className: cx('ot-ask', p.className), onSubmit: function (e) { e.preventDefault(); send(); } },
      h('input', { ref: ref, className: 'text-callout', value: v, placeholder: p.placeholder || 'Something else…', 'aria-label': p.placeholder || 'Something else',
        onChange: function (e) { set(e.target.value); },
        onFocus: p.onFocusIn ? function () { p.onFocusIn(); } : undefined,
        onBlur: p.onFocusOut ? function (e) { if (!e.target.value.trim()) p.onFocusOut(); } : undefined }),
      h('button', { type: 'submit', className: 'ot-ask-send', 'aria-label': 'Send', disabled: !v.trim() }, h(Icon, { name: 'arrow', size: 16 })));
  }

  // A setup question: check any options, or add your own; Next sinks it into the sea.
  function ChoicePanel(p) {
    var s = React.useState(p.initial || []), sel = s[0], setSel = s[1];
    var c = React.useState([]), custom = c[0], setCustom = c[1];
    var all = (p.options || []).concat(custom);
    function toggle(o) { setSel(function (cur) { return cur.indexOf(o) >= 0 ? cur.filter(function (x) { return x !== o; }) : cur.concat([o]); }); }
    return h(GlassPanel, { strong: true, className: cx('ot-choice', 'ot-frost', p.className) },
      p.step && h('div', { className: 'text-mono-sm ot-muted' }, p.step),
      h('h2', { className: 'text-title-2' }, p.title),
      h('div', { className: 'ot-choice-grid', role: 'group', 'aria-label': p.title }, all.map(function (o) {
        return h(CheckRow, { key: o, multi: true, label: o, checked: sel.indexOf(o) >= 0, onClick: function () { toggle(o); } });
      })),
      h(AskField, { placeholder: 'Something else…', onSend: function (t) {
        setCustom(function (cu) { return cu.indexOf(t) >= 0 || (p.options || []).indexOf(t) >= 0 ? cu : cu.concat([t]); });
        setSel(function (cur) { return cur.indexOf(t) >= 0 ? cur : cur.concat([t]); });
      } }),
      h('div', { className: 'ot-choice-foot' },
        p.onSkip && h(Button, { variant: 'ghost', onClick: p.onSkip }, 'Skip'),
        h(Button, { variant: 'primary', iconRight: 'chevron', onClick: function () { if (p.onNext) p.onNext(sel); } }, p.nextLabel || 'Next')));
  }

  // Third setup step: a local, no-AI hardware inventory. The rows and what each found come from the backend's
  // inventory events (`steps`: [{name, found}], `current`: -1 before start, the running row, or steps.length when done).
  function SpecScanPanel(p) {
    var steps = p.steps || [], target = p.current == null ? -1 : p.current;
    // Real inventory can finish in a fraction of a second; the rows still rise one at a time
    // (the animation runs independently of collector timing). Reduced motion shows the real state at once.
    var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    var sh = React.useState(target), shown = sh[0], setShown = sh[1];
    React.useEffect(function () {
      if (target < 0 || reduce) { setShown(target); return; }
      if (shown >= target) return;
      var t = setTimeout(function () { setShown(function (s) { return Math.min(target, Math.max(s, 0) + 1); }); }, shown < 0 ? 0 : p.stepMs || 700);
      return function () { clearTimeout(t); };
    }, [target, shown]);
    var step = reduce ? target : Math.min(shown, target);
    var running = step >= 0, finished = running && step >= steps.length;
    React.useEffect(function () {
      if (!finished) return;
      var t = setTimeout(function () { if (p.onDone) p.onDone(); }, p.holdDone || 900);
      return function () { clearTimeout(t); };
    }, [finished]);
    return h(GlassPanel, { strong: true, className: cx('ot-choice', 'ot-frost', p.className) },
      p.step && h('div', { className: 'text-mono-sm ot-muted' }, p.step),
      h('h2', { className: 'text-title-2' }, finished ? 'Done' : running ? 'Checking what’s connected…' : 'May we check what’s connected to this PC?'),
      !running && h('p', { className: 'text-callout ot-muted' }, 'Runs on this PC, without AI.'),
      running && h('ul', { className: 'ot-spec-list' }, steps.map(function (st, i) {
        var state = i < step ? 'done' : i === step ? 'running' : 'queued';
        return h('li', { key: st.name, className: 'ot-spec-row is-' + state },
          h('span', { className: 'ot-spec-dot', 'aria-hidden': true }, state === 'done' && h(Icon, { name: 'check', size: 12, strokeWidth: 2.75 })),
          h('span', { className: 'text-callout' }, st.name),
          h('span', { className: 'text-mono-sm ot-muted ot-spec-found' }, i <= step ? (st.found || []).join(' · ') : ''));
      })),
      !running && h('div', { className: 'ot-choice-foot' },
        h(Button, { variant: 'ghost', onClick: p.onSkip }, 'Skip for now'),
        h(Button, { variant: 'primary', icon: 'scan', onClick: function () { if (p.onStart) p.onStart(); } }, 'Check this PC')));
  }

  function PromptBox(p) {
    var s = React.useState(''), v = s[0], set = s[1];
    // `restore` ({text, token}) puts a draft back, e.g. when the backend answered with a newer revision.
    React.useEffect(function () { if (p.restore && p.restore.text) { set(p.restore.text); if (p.onDraft) p.onDraft(true); } }, [p.restore && p.restore.token]);
    function send() { var t = v.trim(); if (!t || p.busy) return; if (p.onSubmit) p.onSubmit(t); set(''); if (p.onDraft) p.onDraft(false); }
    return h('form', { className: cx('ot-promptbox', p.className), onSubmit: function (e) { e.preventDefault(); send(); } },
      h('div', { className: 'ot-promptbox-inner' },
        h(Logo, { size: 22, mono: true, decorative: true, className: 'ot-promptbox-mark' }),
        h('input', { className: 'ot-promptbox-input', value: v, 'aria-label': 'Ask', autoFocus: p.autoFocus,
          placeholder: p.placeholder || 'Ask anything, or “begin scan”', onChange: function (e) { set(e.target.value); if (p.onDraft) p.onDraft(!!e.target.value.trim()); } }),
        h('button', { type: 'submit', className: 'ot-promptbox-send', 'aria-label': 'Send', disabled: !v.trim() || p.busy }, h(Icon, { name: 'arrow', size: 16 }))));
  }

  /* ---------- Starters: three at a time below the box, all of them a tap away ---------- */

  // Each starter shows the category it checks, by its intent (the same mapping a scan plans with).
  var STARTER_CAT = { 'audit.security': 'security', 'audit.updates': 'security', 'audit.network': 'security',
    'audit.background': 'performance', 'audit.startup': 'performance', 'audit.gaming': 'performance', 'audit.exports': 'performance',
    'audit.storage': 'storage', 'audit.privacy': 'privacy_telemetry', 'audit.reliability': 'diagnostics', 'audit.devices': 'diagnostics',
    'audit.customization': 'customization', 'audit.displays': 'customization' };
  var STARTER_PAGE = 3;

  function StarterChip(p) {
    var it = p.item, cat = STARTER_CAT[it.intent] || 'misc', c = CATS[cat];
    return h('button', { type: 'button', className: cx('ot-suggest-chip', 'cat-' + cat, p.small && 'is-small'), style: p.style,
        title: c.label, onClick: function () { p.onPick(it); } },
      h(Icon, { name: c.icon, size: p.small ? 14 : 16, strokeWidth: 2, className: 'ot-suggest-icon' }), it.text);
  }

  // Three starters rise after a quiet beat; More shows the next three and the list opens every one. Nothing moves
  // on its own, so there is nothing to pause.
  function StarterPrompt(p) {
    var items = p.items || [];
    var s = React.useState(0), page = s[0], setPage = s[1];
    var sh = React.useState(false), shown = sh[0], setShown = sh[1];
    var al = React.useState(false), all = al[0], setAll = al[1];
    React.useEffect(function () { var t = setTimeout(function () { setShown(true); }, p.delay == null ? 1000 : p.delay); return function () { clearTimeout(t); }; }, []);
    var pages = Math.max(1, Math.ceil(items.length / STARTER_PAGE)), at = (page % pages) * STARTER_PAGE;
    var visible = items.slice(at, at + STARTER_PAGE);
    return h('div', { className: cx('ot-starter', p.className) },
      shown && h('div', { className: 'ot-starter-row', key: 'page-' + page },
        visible.map(function (it, i) { return h(StarterChip, { key: it.id, item: it, onPick: p.onPick, style: { animationDelay: (i * 90) + 'ms' } }); })),
      shown && items.length > 0 && h('div', { className: 'ot-starter-controls' },
        pages > 1 && h('button', { type: 'button', className: 'ot-starter-btn', 'aria-label': 'More suggestions', onClick: function () { setPage(function (n) { return n + 1; }); } }, h(Icon, { name: 'chevron', size: 14 })),
        h('button', { type: 'button', className: cx('ot-starter-btn', all && 'is-on'), 'aria-label': 'All suggestions', 'aria-expanded': all, onClick: function () { setAll(!all); } }, h(Icon, { name: 'list', size: 14 }))),
      h(Expand, { contentKey: all ? 'all' : 'none' }, all ? h('ul', { className: 'ot-starter-all', 'aria-label': 'All suggestions' }, items.map(function (it) {
        return h('li', { key: it.id }, h(StarterChip, { item: it, small: true, onPick: p.onPick }));
      })) : null));
  }

  var SCAN_CATS = [
    { id: 'security', label: 'Security' }, { id: 'performance', label: 'Performance' }, { id: 'storage', label: 'Storage' },
    { id: 'privacy_telemetry', label: 'Privacy' }, { id: 'diagnostics', label: 'Diagnostics' }, { id: 'customization', label: 'Customization' }];

  // A single plain-language progress line with Stop, then the category pills and the bar.
  // While a long sampling check runs (current.skippable), the line counts down and offers Skip.
  function ScanStatus(p) {
    var cats = p.cats || SCAN_CATS, done = p.done || 0, pct = Math.round(Math.min(done, cats.length) / cats.length * 100);
    var cur = p.current && p.current.skippable ? p.current : null;
    var rs = React.useState(null), left = rs[0], setLeft = rs[1];
    React.useEffect(function () {
      var r = cur && cur.remainingSeconds != null ? cur.remainingSeconds : null;
      setLeft(r);
      if (r == null) return;
      // One cheap tick a second between backend updates; stops at zero.
      var t = setInterval(function () { setLeft(function (x) { return x > 0 ? x - 1 : 0; }); }, 1000);
      return function () { clearInterval(t); };
    }, [cur && cur.collectorId, cur && cur.remainingSeconds]);
    var line = cur ? (cur.label || 'Measuring') + (left != null ? ' · ' + left + ' s' : '…')
      : done >= cats.length ? 'Building the report…' : 'Checking ' + cats[Math.min(done, cats.length - 1)].label.toLowerCase() + '…';
    return h('div', { className: cx('ot-scanstat', p.className) },
      h('div', { className: 'ot-scanstat-line' },
        h('span', { className: 'text-callout', 'aria-live': cur ? 'off' : 'polite' }, line),
        cur && p.onSkip && h(Button, { variant: 'ghost', size: 'sm', icon: 'chevron', disabled: p.skipping === cur.collectorId, onClick: function () { p.onSkip(cur.collectorId); } }, 'Skip'),
        p.onStop && h(Button, { variant: 'ghost', size: 'sm', icon: 'stop', onClick: p.onStop }, 'Stop')),
      h('div', { className: 'ot-scanstat-cats' }, cats.map(function (c, i) {
        var st = i < done ? 'done' : i === done ? 'running' : 'queued';
        return h('span', { key: c.id, className: 'ot-scanstat-cat text-mono-sm is-' + st },
          h('span', { className: 'ot-spec-dot', 'aria-hidden': true }, st === 'done' && h(Icon, { name: 'check', size: 12, strokeWidth: 2.75 })), c.label);
      })),
      h('div', { className: 'ot-track ot-track-rainbow', role: 'progressbar', 'aria-valuenow': pct, 'aria-valuemin': 0, 'aria-valuemax': 100 }, h('span', { style: { width: pct + '%' } })));
  }

  // Stable category IDs from the contract; display names exactly as specified. Colours map separately.
  var CATS = {
    security: { label: 'Security', icon: 'shield' },
    performance: { label: 'Performance', icon: 'bolt' },
    storage: { label: 'Storage', icon: 'disk' },
    privacy_telemetry: { label: 'Privacy & Telemetry', icon: 'eye' },
    diagnostics: { label: 'Diagnostics', icon: 'pulse' },
    customization: { label: 'System Customization & Tweaks', icon: 'sliders' },
    misc: { label: 'Misc.', icon: 'dots' }
  };

  // One follow-up at a time: an everyday question, one observation, "Why am I seeing this?", and only the
  // options the finding supports. A change opens its exact preview inside the card; approval is separate.
  var DISPOSE = { Intended: 1, Ignore: 1, Hide: 1 };
  // A click on the card itself (not on one of its controls) makes it the main card.
  var CONTROLS = 'button, a, input, textarea, select, label, [role="button"], [role="group"]';
  function InsightCard(p) {
    var key = CATS[p.category] ? p.category : 'misc', c = CATS[key];
    var w = React.useState(false), why = w[0], setWhy = w[1];
    var ig = React.useState(false), ignoring = ig[0], setIgnoring = ig[1];
    var mode = p.mode || 'idle';
    var opts = mode === 'guide' ? ['Done'].concat(p.settingsUri ? ['Open settings'] : [], p.checkAgain ? ['Check again'] : [], ['Ask about this', p.hideLabel || 'Ignore']) : (p.options || []);
    // Ignore stays one button; with ignoreChoices it opens a compact choice of scope.
    function option(o) {
      if (o === 'Ignore' && p.ignoreChoices) { setIgnoring(!ignoring); return; }
      setIgnoring(false);
      if (p.onOption) p.onOption(o);
    }
    function onCard(e) {
      if (!p.onFocus || (e.target && e.target.closest && e.target.closest(CONTROLS))) return;
      p.onFocus();
    }
    return h('section', { className: cx('ot-insight', 'cat-' + key, p.leaving && 'is-leaving', p.onFocus && 'is-focusable', p.className), 'aria-label': c.label,
      'data-card': p.cardKey || undefined, onClick: onCard },
      h('div', { className: 'ot-insight-head' },
        h('span', { className: 'ot-insight-cat text-caption' }, h(Icon, { name: c.icon, size: 14, strokeWidth: 2 }), c.label, p.ai && h('span', { className: 'ot-tag is-ai' }, 'AI')),
        h('span', { className: 'ot-insight-meta' },
          p.total ? h('span', { className: 'text-mono-sm ot-muted' }, p.index + ' / ' + p.total) : null,
          // The open card folds only by this, Escape, or opening another card (a click elsewhere leaves it open).
          p.onCollapse && h(Button, { variant: 'ghost', size: 'sm', icon: 'collapse', className: 'ot-insight-fold', 'aria-label': 'Collapse', title: 'Collapse', onClick: p.onCollapse }))),
      h('p', { className: 'text-headline ot-insight-claim' }, p.text),
      p.observation && h('p', { className: 'text-callout ot-insight-obs' }, p.observation),
      p.note && h('div', { className: 'ot-note text-callout' }, h(Icon, { name: 'spark', size: 14 }),
        h('span', null, p.noteBy && h('span', { className: 'ot-note-by text-mono-sm' }, p.noteBy), p.note)),
      h('button', { type: 'button', className: 'ot-why text-callout', 'aria-expanded': why, onClick: function () { if (!why && p.onWhy) p.onWhy(); if (p.onWhyToggle) p.onWhyToggle(!why); setWhy(!why); } },
        h(Icon, { name: 'report', size: 14 }), 'Why am I seeing this?'),
      h(Expand, { contentKey: why ? 'why' : 'none' }, why ? h('div', { className: 'ot-why-body' }, (p.evidence || []).map(function (ev) {
        return h('div', { key: ev.id, className: 'ot-why-row' },
          h('div', { className: 'text-mono-sm ot-muted', title: ev.id }, [ev.label || ev.id, ev.source, ev.observedAt].filter(Boolean).join(' · '),
            ev.against && h('span', { className: 'ot-tag' }, 'Against')),
          h('div', { className: ev.missing ? 'text-callout ot-muted' : 'text-callout' }, ev.display),
          p.onCite && !ev.missing && h('button', { type: 'button', className: 'ot-cite text-mono-sm', onClick: function () { p.onCite(ev.id); } }, h(Icon, { name: 'report', size: 12 }), 'Open in report'));
      }).concat((p.coverage || []).length ? [h('div', { key: 'cov', className: 'ot-why-cov' }, p.coverage.map(function (c) {
        return h('span', { key: c.collectorId, className: 'text-mono-sm ot-muted' }, c.label + ' · ' + c.state);
      }))] : [])) : null),
      mode === 'guide' && p.guide && h('ol', { className: 'ot-guide text-callout' }, p.guide.map(function (g) { return h('li', { key: g }, g); })),
      mode === 'guide' && p.recheck && h('div', { className: 'text-mono-sm ot-muted' }, p.recheck),
      mode === 'loading' && h('div', { className: 'text-callout ot-muted' }, 'Preparing…'),
      mode === 'ineligible' && h(PlanBlocked, { ineligible: p.ineligible, onOpenSettings: p.onOpenSettings, onBack: p.onBack }),
      (mode === 'preview' || mode === 'running') && p.preview && h('div', { className: 'ot-insight-preview' },
        h('div', { className: 'ot-diff' }, p.preview.rows.map(function (r) {
          return h('div', { key: r.label, className: 'ot-diff-row' },
            h('span', { className: 'text-caption ot-muted' }, r.label),
            h('span', { className: 'ot-diff-before text-mono' }, r.before),
            h(Icon, { name: 'arrow', size: 16, className: 'ot-muted' }),
            h('span', { className: 'ot-diff-after text-mono' }, r.after));
        })),
        h('dl', { className: 'ot-approval-details' }, p.preview.details.map(function (d) {
          return h(React.Fragment, { key: d[0] }, h('dt', { className: 'text-callout ot-muted' }, d[0]), h('dd', { className: 'text-callout' }, d[1]));
        })),
        h('div', { className: 'ot-approval-actions' },
          mode === 'preview' && p.onPreviewBack && h(Button, { variant: 'ghost', size: 'sm', icon: 'back', onClick: p.onPreviewBack }, 'Back'),
          h(Button, { variant: 'ghost', size: 'sm', onClick: p.onDecline, disabled: mode === 'running' }, 'Decline'),
          h(Button, { variant: 'primary', size: 'sm', icon: 'check', onClick: p.onApprove, disabled: mode === 'running' }, mode === 'running' ? 'Applying…' : 'Approve')),
        mode === 'running' && p.progress && h('div', { className: 'ot-turn text-mono-sm ot-muted' }, h('span', { className: 'ot-live-dot', 'aria-hidden': true }), p.progress),
        mode === 'preview' && p.approveError && h(ApproveError, { word: p.approveError, onRetry: p.onRetry })),
      mode === 'verified' && h('div', { className: 'ot-receipt' },
        h(SeverityBadge, { kind: 'verified' }),
        h('span', { className: 'text-callout' }, p.receipt),
        p.onUndo && h(Button, { variant: 'ghost', size: 'sm', icon: 'undo', onClick: p.onUndo }, 'Undo'),
        h(Button, { variant: 'neu', size: 'sm', iconRight: 'chevron', onClick: p.onNext }, 'Next')),
      mode === 'failed' && h('div', { className: 'ot-receipt' },
        h(SeverityBadge, { kind: 'gap' }, p.failWord || 'Not changed'),
        p.onCheck && h(Button, { variant: 'ghost', size: 'sm', onClick: p.onCheck }, 'Check again'),
        p.failManualUri && p.onOpenSettings && h(Button, { variant: 'ghost', size: 'sm', icon: 'link', onClick: function () { p.onOpenSettings(p.failManualUri); } }, 'Open settings'),
        h(Button, { variant: 'neu', size: 'sm', iconRight: 'chevron', onClick: p.onNext }, 'Next')),
      // Things to do on one row; deciding about the card (Intended, Ignore, Later) on a quieter row of its own.
      // "Ask about this" is the field below, so it isn't repeated as a button.
      (mode === 'idle' || mode === 'guide') && h('div', { className: 'ot-insight-actions' }, opts.filter(function (o) { return !DISPOSE[o] && o !== 'Ask about this'; }).map(function (o, i) {
        return h(Button, { key: o, variant: i === 0 ? 'primary' : 'neu', size: 'sm', icon: o === 'Open settings' ? 'link' : undefined, onClick: function () { option(o); } }, o);
      })),
      (mode === 'idle' || mode === 'guide') && h('div', { className: 'ot-insight-quiet' }, opts.filter(function (o) { return DISPOSE[o]; }).map(function (o) {
        return h(Button, { key: o, variant: 'ghost', size: 'sm', 'aria-expanded': o === 'Ignore' && p.ignoreChoices ? ignoring : undefined, onClick: function () { option(o); } }, o);
      }), p.onLater && h(Button, { key: 'later', variant: 'ghost', size: 'sm', onClick: p.onLater }, 'Later')),
      (mode === 'idle' || mode === 'guide') && p.ignoreChoices && h(Expand, { contentKey: ignoring ? 'ignore' : 'none' }, ignoring ? h('div', { className: 'ot-ignore-choice', role: 'group', 'aria-label': 'Ignore' },
        p.ignoreChoices.map(function (c) {
          return h('button', { key: c.id, type: 'button', className: 'ot-suggest-chip is-small', title: c.title || undefined, onClick: function () { setIgnoring(false); if (p.onIgnore) p.onIgnore(c.id); } }, c.label);
        })) : null),
      // M6a §8: an AI reply with warnings ["voltage"] gets the risk block above it, once.
      p.reply && p.replyRisk && h(RiskNote, { className: 'ot-reply-risk' }),
      (p.reply || p.replyBusy) && h('div', { className: cx('ot-reply text-callout', !p.reply && 'is-busy') },
        p.reply ? h(Icon, { name: 'spark', size: 16 }) : h(Logo, { size: 16, spin: true, decorative: true, className: 'ot-busy-logo' }),
        h('span', null, p.reply ? h(RichText, { text: p.reply }) : h('span', { className: 'ot-muted' }, p.replyBusy),
          (p.replyCites || []).map(function (c) { return h('button', { key: c.id, type: 'button', className: 'ot-cite text-mono-sm', title: c.id, onClick: function () { if (p.onCite) p.onCite(c.id); } }, c.label); }),
          p.reply && p.onReplyConnect && h(Button, { variant: 'ghost', size: 'sm', icon: 'spark', className: 'ot-reply-connect', onClick: p.onReplyConnect }, 'Connect AI'))),
      p.reply && p.onReplyBenchmark && h('div', { className: 'ot-answer-acts' }, h(Button, { variant: 'neu', size: 'sm', icon: 'gauge', onClick: p.onReplyBenchmark }, 'Run benchmark')),
      h(AskField, { placeholder: 'Ask about this…', onSend: p.onAsk, focusToken: p.askToken, restore: p.askRestore, onFocusIn: p.onAskFocus, onFocusOut: p.onAskBlur }));
  }

  // The deck: when the main card changes (or a card leaves), cards glide from where they were to their new place
  // (FLIP: measure, re-layout, play the offset back to zero). Positions are taken relative to the grid, so scrolling
  // between renders does not count as movement. Reduced motion: they simply jump.
  function CardGrid(p) {
    var ref = React.useRef(null), last = React.useRef({}), sig = React.useRef(null);
    var reduce = typeof window !== 'undefined' && window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    function measure() {
      var el = ref.current, now = {}, keys = []; if (!el) return { now: now, keys: keys };
      var box = el.getBoundingClientRect();
      Array.prototype.forEach.call(el.children, function (c) {
        var k = c.getAttribute('data-card'); if (!k) return;
        var r = c.getBoundingClientRect(); now[k] = { x: r.left - box.left, y: r.top - box.top }; keys.push(k);
      });
      return { now: now, keys: keys };
    }
    // Panels inside a card open and close without a render here: take a fresh "before" just ahead of any click.
    React.useEffect(function () {
      function before() { last.current = measure().now; }
      window.addEventListener('pointerdown', before, true);
      return function () { window.removeEventListener('pointerdown', before, true); };
    }, []);
    React.useLayoutEffect(function () {
      var el = ref.current; if (!el) return;
      var m = measure(), now = m.now, keys = m.keys;
      var s = (p.openKey || '') + '|' + keys.join(',');
      if (sig.current !== null && s !== sig.current && !reduce && typeof el.animate === 'function') {
        Array.prototype.forEach.call(el.children, function (c) {
          var k = c.getAttribute('data-card'), a = last.current[k], b = now[k];
          if (!a || !b || c.classList.contains('is-leaving')) return;
          var dx = a.x - b.x, dy = a.y - b.y;
          if (Math.abs(dx) < 1 && Math.abs(dy) < 1) return;
          if (c.animate) c.animate([{ transform: 'translate(' + dx + 'px,' + dy + 'px)' }, { transform: 'none' }], { duration: 420, easing: 'cubic-bezier(.2,.75,.2,1)' });
        });
      }
      last.current = now; sig.current = s;
    });
    return h('div', { ref: ref, className: p.className }, p.children);
  }

  // The approval didn't happen (dialog unavailable, host error): a short word and Retry.
  function ApproveError(p) {
    return h('div', { className: 'ot-answer-fail', role: 'status' }, h('span', { className: 'text-callout ot-muted' }, p.word),
      p.onRetry && h(Button, { variant: 'neu', size: 'sm', onClick: p.onRetry }, 'Retry'));
  }
  // A plan that can't run here: its reason and the manual route.
  function PlanBlocked(p) {
    var i = p.ineligible || {};
    return h('div', { className: 'ot-plan-blocked' },
      h('span', { className: 'text-callout ot-muted' }, i.message || 'Not available'),
      h('div', { className: 'ot-approval-actions' },
        p.onBack && h(Button, { variant: 'ghost', size: 'sm', onClick: p.onBack }, p.backLabel || 'Back'),
        i.manualUri && p.onOpenSettings && h(Button, { variant: 'neu', size: 'sm', icon: 'link', onClick: function () { p.onOpenSettings(i.manualUri); } }, 'Open settings')));
  }
  function PlanPreview(p) {
    var pv = p.preview || { rows: [], details: [] };
    return h(React.Fragment, null,
      h('div', { className: 'ot-diff' }, pv.rows.map(function (r) {
        return h('div', { key: r.label, className: 'ot-diff-row' },
          h('span', { className: 'text-caption ot-muted' }, r.label),
          h('span', { className: 'ot-diff-before text-mono' }, r.before),
          h(Icon, { name: 'arrow', size: 16, className: 'ot-muted' }),
          h('span', { className: 'ot-diff-after text-mono' }, r.after));
      })),
      pv.details.length > 0 && h('dl', { className: 'ot-approval-details' }, pv.details.map(function (d) {
        return h(React.Fragment, { key: d[0] }, h('dt', { className: 'text-callout ot-muted' }, d[0]), h('dd', { className: 'text-callout' }, d[1]));
      })));
  }
  // A change asked for by an assistant, or an undo: the same preview, Approve / Decline, then progress and the receipt.
  function PlanCard(p) {
    var st = p.state;
    return h(GlassPanel, { className: cx('ot-share ot-plan', p.className), role: 'group', 'aria-label': p.title },
      h('div', { className: 'ot-share-head' }, h('div', { className: 'text-headline' }, p.title), p.subtitle && h('span', { className: 'text-mono-sm ot-muted' }, p.subtitle)),
      st === 'ineligible' ? h(PlanBlocked, { ineligible: p.ineligible, onOpenSettings: p.onOpenSettings, onBack: p.onClose, backLabel: 'Close' })
        : st === 'verified' ? h('div', { className: 'ot-receipt' }, h(SeverityBadge, { kind: 'verified' }), h('span', { className: 'text-callout' }, p.receipt),
            p.onUndo && h(Button, { variant: 'ghost', size: 'sm', icon: 'undo', onClick: p.onUndo }, 'Undo'),
            h(Button, { variant: 'neu', size: 'sm', onClick: p.onClose }, 'Close'))
        : st === 'failed' ? h('div', { className: 'ot-receipt' }, h(SeverityBadge, { kind: 'gap' }, p.failWord || 'Not changed'),
            p.onCheck && h(Button, { variant: 'ghost', size: 'sm', onClick: p.onCheck }, 'Check again'),
            p.failManualUri && p.onOpenSettings && h(Button, { variant: 'ghost', size: 'sm', icon: 'link', onClick: function () { p.onOpenSettings(p.failManualUri); } }, 'Open settings'),
            h(Button, { variant: 'neu', size: 'sm', onClick: p.onClose }, 'Close'))
        : h(React.Fragment, null, h(PlanPreview, { preview: p.preview }),
            st === 'running' ? h('div', { className: 'ot-turn text-mono-sm ot-muted' }, h('span', { className: 'ot-live-dot', 'aria-hidden': true }), p.progress || 'Applying…')
              : h(React.Fragment, null, p.approveError && h(ApproveError, { word: p.approveError, onRetry: p.onRetry }),
                h('div', { className: 'ot-choice-foot' },
                  h(Button, { variant: 'ghost', onClick: p.onDecline }, 'Decline'),
                  h(Button, { variant: 'primary', icon: 'check', onClick: p.onApprove }, 'Approve')))));
  }

  // End of the review: an accurate completion state, what was done, and reversible decisions.
  function ReviewComplete(p) {
    var items = p.items || [];
    var label = { verified: 'Verified', ignored: 'Hidden', snoozed: 'Snoozed', later: 'Later', intended: 'Intended', declined: 'Declined', done: 'Done', restored: 'Restored' };
    return h(GlassPanel, { className: cx('ot-changes', p.className) },
      h('div', { className: 'ot-changes-head' }, h('div', { className: 'text-headline' }, items.length ? 'You’ve reviewed every item.' : 'Nothing needs your attention.')),
      items.length > 0 && h('div', { className: 'ot-changes-list' }, items.map(function (it) {
        return h('div', { key: it.id, className: 'ot-change-row is-' + it.status },
          h('span', { className: 'ot-spec-dot', 'aria-hidden': true }, it.status === 'verified' && h(Icon, { name: 'check', size: 12, strokeWidth: 2.75 })),
          h('span', { className: 'text-callout' }, it.text),
          h('span', { className: 'ot-change-end' },
            h('span', { className: 'text-mono-sm ot-change-state' }, label[it.status] || it.status),
            it.status === 'verified' && it.undo && p.onUndo && h(Button, { variant: 'ghost', size: 'sm', icon: 'undo', onClick: function () { p.onUndo(it.id); } }, 'Undo'),
            (it.status === 'ignored' || it.status === 'snoozed' || it.status === 'later' || it.status === 'intended') && p.onRestore && h(Button, { variant: 'ghost', size: 'sm', disabled: !!it.busy, onClick: function () { p.onRestore(it.id); } }, 'Restore')));
      })),
      // Scan and Report live on the toolbar above; the foot only shows when a caller still passes them.
      (p.onRescan || p.onReport) && h('div', { className: 'ot-choice-foot' },
        p.onRescan && h(Button, { variant: 'ghost', icon: 'repeat', onClick: p.onRescan }, 'Scan'),
        p.onReport && h(Button, { variant: 'neu', icon: 'report', onClick: p.onReport }, 'Report')));
  }

  // Before any AI summary: who receives it, what is sent and what never leaves the PC.
  function SharePreview(p) {
    var classes = p.classes;
    return h(GlassPanel, { className: cx('ot-share', p.className) },
      h('div', { className: 'ot-share-head' }, h('div', { className: 'text-headline' }, 'Share with ' + (p.agent || 'your AI') + '?'),
        p.size && h('span', { className: 'text-mono-sm ot-muted' }, p.size)),
      classes ? h('ul', { className: 'ot-share-list' }, classes.map(function (c) {
        var label = (c.label || c.id) + (c.count != null ? ' · ' + c.count : '');
        return c.shared ? h('li', { key: c.id }, h(Icon, { name: 'check', size: 14, strokeWidth: 2.5 }), h('span', { className: 'text-callout' }, label))
          : h('li', { key: c.id, className: 'is-locked' }, h(Icon, { name: c.locked ? 'lock' : 'gap', size: 14 }), h('span', { className: 'text-callout' }, label),
            h('span', { className: 'text-mono-sm ot-muted' }, c.locked ? 'Never' : 'Not shared'));
      })) : h('ul', { className: 'ot-share-list' },
        h('li', null, h(Icon, { name: 'check', size: 14, strokeWidth: 2.5 }), h('span', { className: 'text-callout' }, 'Report summary · ' + (p.findings || 0) + ' findings')),
        h('li', null, h(Icon, { name: 'check', size: 14, strokeWidth: 2.5 }), h('span', { className: 'text-callout' }, 'Measurements and times')),
        h('li', { className: 'is-locked' }, h(Icon, { name: 'lock', size: 14 }), h('span', { className: 'text-callout' }, 'Device names, accounts, paths'), h('span', { className: 'text-mono-sm ot-muted' }, 'Not shared'))),
      h('div', { className: 'ot-choice-foot' },
        h(Button, { variant: 'ghost', onClick: p.onCancel }, 'Cancel'),
        h(Button, { variant: 'primary', icon: 'spark', onClick: p.onShare }, 'Share & summarize')));
  }

  // §8 grant sheet: Can do (diagnostics) / Can share, then Start.
  function GrantSheet(p) {
    var init = {}; (p.diagnostics || []).forEach(function (d) { init[d.id] = d.on !== false; });
    var s = React.useState(init), on = s[0], setOn = s[1];
    var n = React.useState(false), names = n[0], setNames = n[1];
    var rc = React.useState(false), canChange = rc[0], setCanChange = rc[1];
    var lim = p.limits || {};
    function toggle(id, v) { var x = Object.assign({}, on); x[id] = v; setOn(x); }
    return h(GlassPanel, { className: cx('ot-grant ot-grant-sheet', p.className), role: 'group', 'aria-label': 'Permissions' },
      h('div', { className: 'ot-grant-head' },
        h('div', { className: 'text-caption ot-muted' }, p.recipient),
        p.goal && h('div', { className: 'text-headline' }, p.goal)),
      h('div', { className: 'ot-grant-cols' },
        h('section', null,
          h('h3', { className: 'text-caption ot-muted' }, 'Can do'),
          (p.diagnostics || []).map(function (d) { return h(GrantRow, { key: d.id, title: d.label, checked: !!on[d.id], onChange: function (v) { toggle(d.id, v); } }); }),
          !(p.diagnostics || []).length && p.changes === false && h('div', { className: 'text-callout ot-muted' }, 'Explain only'),
          p.changes !== false && h(GrantRow, { title: 'Can request changes', checked: canChange, onChange: setCanChange })),
        h('section', null,
          h('h3', { className: 'text-caption ot-muted' }, 'Can share'),
          h(GrantRow, { title: 'Findings · ' + (p.findings || 0), fixed: true }),
          h(GrantRow, { title: 'Measurements and times', fixed: true }),
          h(GrantRow, { title: 'App names', checked: names, onChange: setNames }),
          h(GrantRow, { title: 'Paths, accounts, addresses', locked: true }))),
      h('div', { className: 'ot-grant-foot' },
        h('span', { className: 'text-mono-sm ot-muted' }, [lim.diagnosticCalls && lim.diagnosticCalls + ' checks', lim.totalBytes && Math.round(lim.totalBytes / 1024) + ' KiB', lim.turns && lim.turns + ' turns'].filter(Boolean).join(' · ')),
        h('div', { className: 'ot-grant-actions' },
          p.onCancel && h(Button, { variant: 'ghost', onClick: p.onCancel }, 'Cancel'),
          h(Button, { variant: 'primary', iconRight: 'arrow', disabled: !!p.busy, onClick: function () {
            if (p.busy) return;
            p.onStart({ diagnostics: (p.diagnostics || []).filter(function (d) { return on[d.id]; }).map(function (d) { return d.id; }), share: { names: names }, canRequestChanges: canChange });
          } }, 'Start'))));
  }

  var ACT_STATE = { running: 'running', complete: 'done', error: 'declined', denied: 'declined' };
  var ACT_WORD = { running: '…', complete: 'Done', error: 'Failed', denied: 'Not allowed' };
  // Optimizer tool calls only: plain words, origin, size shared, state; the task's budget; Stop.
  // A running row: "Measuring CPU · 12 s left" for a sample, else its words with "Running… 3 s".
  function runningText(it, now) {
    var secs = Math.max(0, Math.floor((now - (it.startedAt || now)) / 1000));
    if (it.window) return { title: it.title + ' · ' + Math.max(0, it.window - secs) + ' s left', state: '' };
    return { title: it.title, state: 'Running… ' + secs + ' s' };
  }
  function ActivityPanel(p) {
    var items = (p.items || []).slice().reverse(), used = p.used || {}, lim = p.limits || {};
    var anyRunning = items.some(function (it) { return it.status === 'running'; });
    var tk = React.useState(Date.now()), now = tk[0], setNow = tk[1];
    React.useEffect(function () {
      // One cheap tick a second, only while a call is running.
      if (!anyRunning) return;
      setNow(Date.now());
      var t = setInterval(function () { setNow(Date.now()); }, 1000);
      return function () { clearInterval(t); };
    }, [anyRunning]);
    function meter(label, v, max, fmt) {
      if (!max) return null;
      return h('div', { className: 'ot-meter' },
        h('div', { className: 'ot-meter-label text-mono-sm ot-muted' }, h('span', null, label), h('span', null, (fmt ? fmt(v) : v) + ' / ' + (fmt ? fmt(max) : max))),
        h('div', { className: 'ot-track' }, h('span', { style: { width: Math.min(100, (v || 0) / max * 100) + '%' } })));
    }
    return h(GlassPanel, { className: cx('ot-activity is-compact', p.className), role: 'region', 'aria-label': 'Activity' },
      h('div', { className: 'ot-activity-head' },
        h('div', { className: cx('text-caption', p.active ? 'ot-live' : 'ot-muted') }, p.active && h('span', { className: 'ot-live-dot', 'aria-hidden': true }), p.title),
        h('div', { className: 'ot-grant-actions' },
          p.active && p.onStop && h(Button, { variant: 'neu', size: 'sm', icon: 'stop', onClick: p.onStop }, 'Stop'),
          p.onClose && h(Button, { variant: 'ghost', size: 'sm', icon: 'close', 'aria-label': 'Close', onClick: p.onClose }))),
      items.length ? h('ol', { className: 'ot-steps' }, items.map(function (it, i) {
        var st = ACT_STATE[it.status] || 'done', live = it.status === 'running' ? runningText(it, now) : null;
        // Newest first; the latest row flashes once when it appears or changes state (the key includes its state).
        return h('li', { key: it.id + ':' + it.status, className: cx('ot-step', 'is-' + st, i === 0 && 'is-fresh', p.highlight && it.evidenceIds && it.evidenceIds.indexOf(p.highlight) >= 0 && 'is-hit') },
          h('span', { className: 'ot-step-mark', 'aria-hidden': true }, st === 'done' && h(Icon, { name: 'check', size: 14, strokeWidth: 2.25 }), st === 'declined' && h(Icon, { name: 'gap', size: 14 })),
          // What happened first; where it ran and how much it read underneath, quieter.
          h('div', { className: 'ot-step-body' },
            h('div', { className: 'text-callout ot-step-title' }, live ? live.title : it.title),
            h('div', { className: 'ot-step-top' },
              h('span', { className: cx('ot-origin text-mono-sm', it.origin !== 'in_app' && 'is-client') }, it.originLabel),
              it.size && h('span', { className: 'text-mono-sm ot-muted' }, it.size))),
          h('span', { className: 'ot-step-state text-mono-sm' }, live ? live.state : ACT_WORD[it.status] || it.status));
      })) : h('div', { className: 'text-callout ot-muted' }, 'No checks yet'),
      h('div', { className: 'ot-meters' },
        meter('Checks', used.diagnosticCalls || 0, lim.diagnosticCalls),
        meter('Shared', used.bytesShared || 0, lim.totalBytes, function (b) { return Math.round(b / 1024) + ' KiB'; }),
        // Claude Code and Codex run their own model turns, so only the in-app assistant has a turn budget to show.
        p.nativeClient ? null : meter('Turns', used.turns || 0, lim.turns)));
  }

  // What a citation chip points at: the measurement's sentence, its source and time; report evidence can open the report.
  function EvidencePeek(p) {
    var e = p.ev;
    return h('div', { className: 'ot-peek-wrap', onClick: p.onClose },
      h(GlassPanel, { strong: true, className: 'ot-peek ot-enter', role: 'dialog', 'aria-label': 'Evidence', onClick: function (x) { x.stopPropagation(); } },
        h('div', { className: 'ot-peek-head' },
          p.label && h('span', { className: 'ot-cite text-mono-sm' }, p.label),
          h('span', { className: 'text-caption ot-muted' }, e ? e.source : 'Evidence'),
          h(Button, { variant: 'ghost', size: 'sm', icon: 'close', 'aria-label': 'Close', onClick: p.onClose })),
        e ? h('div', { className: 'text-callout' }, e.display)
          : h('div', { className: 'text-callout ot-muted' }, p.missing ? 'No longer available' : 'Loading…'),
        e && h('div', { className: 'ot-peek-foot' },
          h('span', { className: 'text-mono-sm ot-muted' }, p.when || ''),
          p.onReport && h(Button, { variant: 'neu', size: 'sm', icon: 'report', onClick: p.onReport }, 'View report'))));
  }

  // AI text with light Markdown: **bold**, "- " bullets, "# " headings and "| a | b |" tables become plain elements
  // (never HTML).
  function inline(line) { return line.split(/\*\*(.+?)\*\*/g).map(function (x, j) { return j % 2 ? h('strong', { key: j }, x) : x; }); }
  function cells(line) { return line.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map(function (c) { return c.trim(); }); }
  function RichText(p) {
    var lines = String(p.text || '').split('\n'), out = [];
    for (var i = 0; i < lines.length; i++) {
      var line = lines[i];
      if (/^\s*\|.*\|\s*$/.test(line)) {
        var rows = [];
        while (i < lines.length && /^\s*\|.*\|\s*$/.test(lines[i])) { if (!/^\s*\|[\s:|-]+\|\s*$/.test(lines[i])) rows.push(cells(lines[i])); i++; }
        i--;
        out.push(h('table', { key: 't' + i, className: 'ot-rich-table' }, h('tbody', null, rows.map(function (r, ri) {
          return h('tr', { key: ri }, r.map(function (c, ci) { return h(ri === 0 ? 'th' : 'td', { key: ci }, inline(c)); }));
        }))));
        continue;
      }
      var bullet = /^\s*[-*•]\s+/.test(line), head = /^\s*#{1,6}\s+/.test(line);
      line = line.replace(/^\s*[-*•]\s+/, '').replace(/^\s*#{1,6}\s+/, '');
      out.push(h(React.Fragment, { key: i }, bullet ? '• ' : null, head ? h('strong', null, inline(line)) : inline(line), i < lines.length - 1 ? '\n' : null));
    }
    return h(React.Fragment, null, out);
  }

  // Citation chips: the first six, then "+N" to show the rest.
  function CiteChips(p) {
    var s = React.useState(false), all = s[0], setAll = s[1], list = p.cites || [], shown = all ? list : list.slice(0, 6);
    return h(React.Fragment, null, shown.map(function (c) {
      return h('button', { key: c.id, type: 'button', className: 'ot-cite text-mono-sm', title: c.id, onClick: function () { if (p.onCite) p.onCite(c.id); } }, c.label);
    }), !all && list.length > 6 && h('button', { type: 'button', className: 'ot-cite text-mono-sm', onClick: function () { setAll(true); } }, '+' + (list.length - 6)));
  }

  // Assistant answers to report-wide questions: plain text, citation chips, one progress line.
  // The newest finished answer offers the AI's follow-up questions as buttons (m5 §7); Close clears the panel.
  // While one is on its way, the gear logo turns; onStop adds Stop to that row (cancels the run, the task stays).
  function AgentAnswers(p) {
    var items = p.items || [], last = items[items.length - 1];
    var follow = p.onFollow && last && last.text && !last.busy && (last.followUps || []).length ? last.followUps : null;
    return h(GlassPanel, { className: cx('ot-summary-inline ot-answers', p.className), role: 'log', 'aria-live': 'polite' },
      p.onClose && h(Button, { variant: 'ghost', size: 'sm', icon: 'close', className: 'ot-answers-close', 'aria-label': 'Close', onClick: p.onClose }),
      h('ul', null, items.map(function (a) {
        return h('li', { key: a.id, className: 'ot-answer' },
          a.q && h('div', { className: 'text-callout ot-muted' }, a.q),
          a.failed ? h('div', { className: 'ot-answer-fail' }, h('span', { className: 'text-callout ot-muted' }, a.failed),
            a.action && h(Button, { variant: 'neu', size: 'sm', onClick: a.action.onClick }, a.action.label))
          : a.text ? h('div', { className: 'text-callout' }, h(RichText, { text: a.text }), ' ', h(CiteChips, { cites: a.cites, onCite: p.onCite }))
          : h('div', { className: 'ot-answer-busy' },
              h('div', { className: 'text-mono-sm ot-muted ot-turn' }, h(Logo, { size: 16, spin: true, decorative: true, className: 'ot-busy-logo' }), a.busy || 'Thinking…'),
              p.onStop && h(Button, { variant: 'neu', size: 'sm', icon: 'stop', onClick: p.onStop }, 'Stop')));
      })),
      follow && h('div', { className: 'ot-followups' }, follow.map(function (q) {
        return h('button', { key: q, type: 'button', className: 'ot-suggest-chip is-small', onClick: function () { p.onFollow(q); } }, q);
      })));
  }

  // One question from the box and its answer (m5 §9): the question as the title, Close beside it; the answer with its
  // citation chips, or the turning gear with Stop while it runs; then the AI's follow-ups (each asks as a new panel).
  // M6a §8: the risk block for overclocking and voltage. Danger styling, calm words; no action (the app never applies these).
  function RiskNote(p) {
    return h('div', { className: cx('ot-risk', p.className), role: 'note' },
      h(Icon, { name: 'alert', size: 20, strokeWidth: 2, className: 'ot-risk-icon' }),
      h('div', { className: 'ot-risk-text' },
        h('span', { className: 'text-headline' }, p.title || 'Your call'),
        h('span', { className: 'text-callout' }, p.children || 'Overclocking and voltage changes can cause crashes or damage. Optimus never applies them.')));
  }

  function AnswerPanel(p) {
    var a = p.item, running = !a.text && !a.failed;
    var follow = p.onFollow && a.text && (a.followUps || []).length ? a.followUps : null;
    // M6a §8: warnings ["voltage"] → the risk block above the text (once); suggest "benchmark" → Run benchmark.
    var risk = a.text && (a.warnings || []).indexOf('voltage') >= 0;
    var bench = a.text && a.suggest === 'benchmark' && p.onBenchmark;
    return h(GlassPanel, { className: cx('ot-summary-inline ot-answers ot-answer-panel', p.className), role: 'region', 'aria-label': a.q || 'Answer',
        'data-answer': a.id, tabIndex: -1 },
      h('div', { className: 'ot-answer-head' },
        h('h3', { className: 'text-headline ot-answer-title' }, a.q || 'Answer'),
        p.onClose && h(Button, { variant: 'ghost', size: 'sm', icon: 'close', className: 'ot-answer-x', 'aria-label': 'Close', title: 'Close', onClick: p.onClose })),
      risk && h(RiskNote, null),
      h('div', { className: 'ot-answer', 'aria-live': 'polite' },
        a.failed ? h('div', { className: 'ot-answer-fail' }, h('span', { className: 'text-callout ot-muted' }, a.failed),
          a.action && h(Button, { variant: 'neu', size: 'sm', onClick: a.action.onClick }, a.action.label))
        : a.text ? h('div', { className: 'text-callout' }, h(RichText, { text: a.text }), ' ', h(CiteChips, { cites: a.cites, onCite: p.onCite }))
        : h('div', { className: 'ot-answer-busy' },
            h('div', { className: 'text-mono-sm ot-muted ot-turn' }, h(Logo, { size: 16, spin: true, decorative: true, className: 'ot-busy-logo' }), a.busy || 'Thinking…'),
            running && p.onStop && h(Button, { variant: 'neu', size: 'sm', icon: 'stop', onClick: p.onStop }, 'Stop'))),
      bench && h('div', { className: 'ot-answer-acts' }, h(Button, { variant: 'neu', size: 'sm', icon: 'gauge', onClick: p.onBenchmark }, 'Run benchmark')),
      follow && h('div', { className: 'ot-followups' }, follow.map(function (q) {
        return h('button', { key: q, type: 'button', className: 'ot-suggest-chip is-small', disabled: !!p.followBusy, onClick: function () { p.onFollow(q); } }, q);
      })));
  }

  // A small list that drops down under the toolbar (Suggestions, History). It closes on a pick, Escape (focus goes back
  // to its toggle) or a click outside it; arrow keys move between its items. An intentional overlay: it never moves
  // the page. toggleId: the button that opened it (it stays a toggle, so clicking it isn't "outside").
  function Popover(p) {
    var ref = React.useRef(null), menuStep = window.OptTool && window.OptTool.menuStep;
    function items() { return ref.current ? Array.prototype.slice.call(ref.current.querySelectorAll('[data-menu-item]:not(:disabled)')) : []; }
    function back() { var t = p.toggleId && document.getElementById(p.toggleId); if (t && t.focus) t.focus(); }
    // The whole panel stays inside the viewport, on open and when the arrow keys move focus in it (not just the focused row).
    function keep() { var k = window.OptTool && window.OptTool.keepInView; if (k && ref.current) k(ref.current.parentNode); }
    React.useEffect(function () {
      var first = items()[0]; if (first && first.focus) first.focus(); else if (ref.current) ref.current.focus();
      keep();
      function onKey(e) { if (e.key === 'Escape') { e.stopPropagation(); p.onClose(); back(); } }
      function away(e) {
        var t = e.target;
        if (!t || !t.closest || (ref.current && ref.current.contains(t)) || (p.toggleId && t.closest('#' + p.toggleId))) return;
        p.onClose();
      }
      window.addEventListener('keydown', onKey, true);
      document.addEventListener('pointerdown', away);
      return function () { window.removeEventListener('keydown', onKey, true); document.removeEventListener('pointerdown', away); };
    }, []);
    function onKeyDown(e) {
      var list = items(), at = list.indexOf(document.activeElement), next = menuStep ? menuStep(e.key, at, list.length) : -1;
      if (next < 0) return;
      e.preventDefault(); list[next].focus(); keep();
    }
    return h(GlassPanel, { className: cx('ot-popover', p.className), id: p.id, role: 'group', 'aria-label': p.label, tabIndex: -1, onKeyDown: onKeyDown },
      h('div', { ref: ref, className: 'ot-popover-scroll' }, p.children));
  }

  // Suggestions: every starter, each sent from the box as before.
  function SuggestionList(p) {
    var items = p.items || [];
    return h(Popover, { id: p.id, label: 'Suggestions', toggleId: p.toggleId, onClose: p.onClose },
      items.length ? h('ul', { className: 'ot-menu-list' }, items.map(function (it) {
        var cat = STARTER_CAT[it.intent] || 'misc', c = CATS[cat];
        return h('li', { key: it.id }, h('button', { type: 'button', 'data-menu-item': true, className: cx('ot-menu-item', 'cat-' + cat), title: c.label, onClick: function () { p.onPick(it); } },
          h(Icon, { name: c.icon, size: 16, strokeWidth: 2, className: 'ot-suggest-icon' }), h('span', null, it.text)));
      })) : h('p', { className: 'text-callout ot-muted ot-menu-empty' }, 'No suggestions'));
  }

  // History: earlier answers, newest first (question and time). Open brings the panel back; the bin deletes it.
  function HistoryList(p) {
    var rows = p.rows || [];
    return h(Popover, { id: p.id, label: 'History', toggleId: p.toggleId, onClose: p.onClose, className: 'ot-history' },
      rows.length ? h('ul', { className: 'ot-menu-list' }, rows.map(function (r) {
        return h('li', { key: r.key, className: 'ot-history-row' },
          h('button', { type: 'button', 'data-menu-item': true, className: cx('ot-menu-item', r.open && 'is-on'), title: r.title, 'aria-current': r.open ? 'true' : undefined, onClick: function () { p.onOpen(r); } },
            h('span', { className: 'ot-history-title' }, r.title),
            h('span', { className: 'text-mono-sm ot-muted ot-history-when' }, r.running ? 'Now' : r.when || '')),
          !r.keep && h(Button, { variant: 'ghost', size: 'sm', icon: 'trash', className: 'ot-history-del', 'aria-label': 'Delete: ' + r.title, title: 'Delete', disabled: r.running,
            onClick: function () { p.onDelete(r); } }));
      })) : h('p', { className: 'text-callout ot-muted ot-menu-empty' }, 'Nothing yet'));
  }

  // First look after connecting an AI (m5 §6): its live activity, then its answer; Skip any time, Continue when answered.
  function AiScanPanel(p) {
    var a = p.answer;
    return h('div', { className: cx('ot-aiscan', p.className) },
      h(ActivityPanel, { title: p.title || 'AI', active: p.active && !p.done, items: p.items, nativeClient: true }),
      a && (a.text || a.failed) && h(AgentAnswers, { items: [Object.assign({}, a, { q: null })], onCite: p.onCite }),
      h('div', { className: 'ot-choice-foot' },
        !p.done && h(Button, { variant: 'ghost', onClick: p.onSkip }, 'Skip'),
        h(Button, { variant: 'primary', iconRight: 'chevron', disabled: !p.done, onClick: p.onContinue }, 'Continue')));
  }

  function ConnectionDetail(p) {
    var st = p.state, acts = p.actions || [];
    return h(GlassPanel, { className: cx('ot-conn', p.className), role: 'dialog', 'aria-label': st.label },
      h('div', { className: 'ot-conn-head' },
        h('span', { className: 'text-headline' }, st.label),
        st.word && h('span', { className: 'text-mono-sm ot-muted' }, st.word)),
      h(WaitHelp, { hint: st.hint, long: p.waitLong && p.command, cli: st.cli }),
      p.command && h(CopyField, { text: p.command, onFail: p.onCopyFail }),
      h('div', { className: 'ot-choice-foot' },
        p.onDisconnect && h(Button, { variant: 'ghost', size: 'sm', onClick: p.onDisconnect }, 'Disconnect'),
        p.onChange && h(Button, { variant: 'ghost', size: 'sm', onClick: p.onChange }, 'Change'),
        acts.map(function (a, i) { return h(Button, { key: a.label, variant: i === acts.length - 1 ? 'primary' : 'neu', size: 'sm', icon: a.icon, onClick: a.onClick }, a.label); })));
  }

  // On a native client, the conversation happens there: a prompt to paste.
  function ContinuePanel(p) {
    return h(GlassPanel, { className: cx('ot-share', p.className) },
      h('div', { className: 'text-headline' }, 'Continue in ' + p.client),
      h(CopyField, { text: p.prompt, onFail: p.onCopyFail }),
      h('div', { className: 'ot-choice-foot' }, h(Button, { variant: 'ghost', onClick: p.onClose }, 'Not now')));
  }

  var COV_LABEL = { complete: 'Checked', partial: 'Partly checked', none: 'Not checked' };
  var DETAIL_ROWS = 4;
  // The full report, full screen on grid paper, sliding up over the sea. Rows are evidence grouped by category.
  function ReportSheet(p) {
    var bodyRef = React.useRef(null);
    var rows = p.rows || [];
    React.useEffect(function () {
      if (!p.onClose) return;
      function onKey(e) { if (e.key === 'Escape') p.onClose(); }
      window.addEventListener('keydown', onKey);
      return function () { window.removeEventListener('keydown', onKey); };
    }, [p.onClose]);
    React.useEffect(function () {
      if (!p.highlight || !bodyRef.current) return;
      var el = bodyRef.current.querySelector('[data-row="' + p.highlight + '"]');
      if (el && el.scrollIntoView) el.scrollIntoView({ block: 'center', behavior: 'smooth' });
    }, [p.highlight]);
    var ex = React.useState({}), open = ex[0], setOpen = ex[1];
    var cov = {}; (p.categories || []).forEach(function (c) { cov[c.id] = c; });
    // Planned categories always show, so "nothing found" and "not checked" stay distinguishable.
    var groups = Object.keys(CATS).map(function (k) {
      var rs = rows.filter(function (r) { return r.cat === k; });
      return { k: k, main: rs.filter(function (r) { return !r.info; }), info: rs.filter(function (r) { return r.info; }), cov: cov[k] };
    }).filter(function (g) { return g.main.length || g.info.length || g.cov; });
    var hitCat = null; rows.forEach(function (r) { if (r.id === p.highlight && r.info) hitCat = r.cat; });
    function row(r) {
      return h('div', { key: r.id, 'data-row': r.id, className: cx('ot-rrow', r.gap && 'is-gap', r.info && 'is-info', p.highlight === r.id && 'is-hit') },
        h('span', { className: 'text-mono-sm ot-muted', title: r.id }, r.label || r.id),
        h('span', { className: 'ot-rrow-main' },
          h('span', { className: 'text-callout ot-rrow-title' }, r.title, r.isNew && h('span', { className: 'ot-tag is-new' }, 'New')),
          r.value && h('span', { className: 'text-callout ot-muted' }, r.value)),
        h('span', { className: 'text-mono-sm ot-muted ot-rrow-src' }, r.src, r.time && h('br'), r.time));
    }
    return h('div', { className: cx('ot-sheet-wrap', p.className) },
      h('div', { className: 'ot-sheet', role: 'dialog', 'aria-modal': true, 'aria-label': 'Report' },
        h('div', { className: 'ot-sheet-head' },
          h('div', { className: 'ot-sheet-title' },
            p.onClose && h(Button, { variant: 'ghost', size: 'sm', icon: 'back', onClick: p.onClose }, 'Back'),
            h(Icon, { name: 'report', size: 20 }), h('span', { className: 'text-title-2' }, 'Report'),
            h('span', { className: 'text-mono-sm ot-muted' }, p.when || '')),
          h('div', { className: 'ot-sheet-actions' },
            p.onSummarize && h(Button, { variant: 'neu', size: 'sm', icon: 'spark', onClick: p.onSummarize, disabled: !!p.summary }, 'Summarize with AI'))),
        h(GridBackground, { className: 'ot-sheet-body' }, h('div', { ref: bodyRef, className: 'ot-sheet-scroll' },
          p.summary && h('div', { className: 'ot-summary' },
            h('div', { className: 'ot-summary-head text-caption' }, h(Icon, { name: 'spark', size: 14 }), 'Summary'),
            h('ul', null, p.summary.map(function (l) { return h('li', { key: l.text || l, className: 'text-body' }, l.text || l); }))),
          groups.map(function (g) {
            var c = CATS[g.k], all = open[g.k] || hitCat === g.k, info = all ? g.info : g.info.slice(0, DETAIL_ROWS);
            return h('section', { key: g.k, className: cx('ot-rgroup', 'cat-' + g.k) },
              h('h3', { className: 'ot-rgroup-head text-caption' }, h(Icon, { name: c.icon, size: 14, strokeWidth: 2 }), c.label,
                h('span', { className: 'ot-muted' }, g.main.length),
                g.cov && h('span', { className: 'ot-cov text-mono-sm is-' + g.cov.status }, COV_LABEL[g.cov.status] || g.cov.status)),
              g.main.map(row),
              !g.main.length && !g.info.length && h('div', { className: 'ot-rrow is-empty' }, h('span', null),
                h('span', { className: 'text-callout ot-muted' }, g.cov && g.cov.status === 'none' ? 'Not checked' : 'Nothing found')),
              g.info.length > 0 && h('div', { className: 'ot-rsub text-caption ot-muted' }, 'Details', h('span', null, g.info.length)),
              info.map(row),
              g.info.length > DETAIL_ROWS && h(Button, { variant: 'ghost', size: 'sm', className: 'ot-rmore', 'aria-expanded': !!all,
                onClick: function () { var n = Object.assign({}, open); n[g.k] = !all; setOpen(n); } }, all ? 'Show less' : 'Show all'));
          }),
          p.fixed && p.fixed.length > 0 && h('section', { key: 'fixed', className: 'ot-rgroup cat-misc is-fixed' },
            h('h3', { className: 'ot-rgroup-head text-caption' }, h(Icon, { name: 'check', size: 14, strokeWidth: 2 }), 'Fixed', h('span', { className: 'ot-muted' }, p.fixed.length)),
            p.fixed.map(function (f) {
              return h('div', { key: f.id, className: 'ot-rrow is-fixed' }, h('span', null),
                h('span', { className: 'text-callout ot-rrow-title' }, f.title), h('span', { className: 'text-mono-sm ot-muted ot-rrow-src' }, (CATS[f.category] || CATS.misc).label));
            }))))));
  }

  window.OptTool = Object.assign(window.OptTool || {}, {
    Logo: Logo, GridBackground: GridBackground, WaveBackground: WaveBackground, GlassPanel: GlassPanel, NeuCard: NeuCard, Expand: Expand,
    Button: Button, Icon: Icon, ProviderOption: ProviderOption, SegmentedControl: SegmentedControl,
    Toggle: Toggle, KeyInput: KeyInput, SeverityBadge: SeverityBadge, FindingCard: FindingCard, ScanProgress: ScanProgress,
    ConnectionPill: ConnectionPill, PermissionGrant: PermissionGrant, AgentActivity: AgentActivity, ApprovalCard: ApprovalCard,
    CheckRow: CheckRow, AskField: AskField, ChoicePanel: ChoicePanel, SpecScanPanel: SpecScanPanel, PromptBox: PromptBox,
    StarterPrompt: StarterPrompt, ScanStatus: ScanStatus, CopyField: CopyField, PairingCard: PairingCard, GrantSheet: GrantSheet,
    CardGrid: CardGrid, ActivityPanel: ActivityPanel, AgentAnswers: AgentAnswers, AnswerPanel: AnswerPanel, RiskNote: RiskNote, Popover: Popover, SuggestionList: SuggestionList, HistoryList: HistoryList, AiScanPanel: AiScanPanel, EvidencePeek: EvidencePeek, ContinuePanel: ContinuePanel, ConnectionDetail: ConnectionDetail, PlanCard: PlanCard, InsightCard: InsightCard, ReviewComplete: ReviewComplete, SharePreview: SharePreview, ReportSheet: ReportSheet,
    LoginPanel: LoginPanel, LoginScreen: LoginScreen,
    // Shared, data-free helpers for app-flow.js.
    ui: { cx: cx, CATS: CATS, SCAN_CATS: SCAN_CATS, PROVIDERS: PROVIDERS, LOGO_SVG: LOGO_SVG }
  });
})();
