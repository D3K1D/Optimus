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
    minus: 'M6 12h12',
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
    download: 'M12 4v11M7.5 10.5L12 15l4.5-4.5M5 19.5h14',
    // M9 (design: AI permissions)
    globe: 'M12 3.5a8.5 8.5 0 110 17 8.5 8.5 0 010-17zM3.5 12h17M12 3.5c2.4 2.4 3.5 5.3 3.5 8.5s-1.1 6.1-3.5 8.5M12 3.5C9.6 5.9 8.5 8.8 8.5 12s1.1 6.1 3.5 8.5',
    external: 'M14 4h6v6M20 4l-8.5 8.5M18 14v5a1 1 0 01-1 1H5a1 1 0 01-1-1V7a1 1 0 011-1h5',
    grid: 'M4 4h6v6H4zM14 4h6v6h-6zM4 14h6v6H4zM14 14h6v6h-6z'
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

  // Tile status words that need attention (shown with an alert mark).
  var PROVIDER_WARN = ['Couldn’t check', 'Update needed', 'Signed out', 'Blocked on this PC', 'Needs repair', 'Limit reached', 'No credit', 'Key rejected', 'Offline', 'Not available for this account'];
  function ProviderOption(p) {
    var sel = !!p.selected;
    return h('button', {
      type: 'button', role: 'radio', 'aria-checked': sel, tabIndex: p.tabIndex, 'data-value': p.value, className: cx('ot-provider', sel && 'is-selected', p.className),
      onClick: function () { if (p.onSelect) p.onSelect(p.value); }
    },
      h('span', { className: 'ot-provider-glyph', 'aria-hidden': true }, p.icon ? h(Icon, { name: p.icon, size: 22 }) : h('span', { className: 'text-mono-sm' }, p.glyph)),
      h('span', { className: 'ot-provider-text' },
        h('span', { className: 'text-headline' }, p.title),
        p.description && h('span', { className: 'ot-muted text-callout' }, p.description),
        p.status && h('span', { className: cx('text-mono-sm ot-provider-status', (p.status.warn || PROVIDER_WARN.indexOf(p.status.text || p.status) >= 0) && 'is-warn') },
          (p.status.warn || PROVIDER_WARN.indexOf(p.status.text || p.status) >= 0) && h(Icon, { name: 'alert', size: 12 }), p.status.text || p.status)),
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

  /* AI research on a finding (design: ResearchTag, ResearchNote; contracts/m9-ai-powers.md §6.2): a clickable label in
     the card's head that opens the explanation under the claim. The finding itself never changes. */
  var RESEARCH = {
    safe: { cls: 'is-safe', icon: 'check', text: 'AI researched: considered safe' },
    look: { cls: 'is-look', icon: 'alert', text: 'AI researched: worth a look' },
    running: { cls: 'is-running', icon: 'globe', text: 'AI researching…' },
    checkup: { cls: 'is-checkup', icon: 'globe', text: 'AI check-up' }
  };
  function ResearchTag(p) {
    var r = RESEARCH[p.verdict] || RESEARCH.safe;
    var inner = [p.verdict === 'running' ? h(Logo, { key: 'i', size: 12, mono: true, spin: true, decorative: true }) : h(Icon, { key: 'i', name: r.icon, size: 12, strokeWidth: 2.5 }),
      h('span', { key: 't' }, r.text)];
    if (!p.onClick) return h('span', { className: cx('ot-tag ot-research-tag', r.cls, 'text-mono-sm', p.className) }, inner);
    return h('button', { type: 'button', className: cx('ot-tag ot-research-tag is-button', r.cls, p.open && 'is-open', 'text-mono-sm', p.className), 'aria-expanded': !!p.open, 'aria-controls': p.open ? p.controls : undefined, onClick: p.onClick },
      inner, h(Icon, { name: 'chevron', size: 12, strokeWidth: 2.5, className: 'ot-research-chev' }));
  }
  // Numbered sources, 1–3: title + site + external icon (design v48 SourceList, shared by ResearchNote, the check-up
  // card and answers). Each opens by id through the shell (shell.openSource): the app never holds a URL; a url is for
  // previews only. `max` (answers list up to 8) is this port's only addition.
  function SourceList(p) {
    var srcs = (p.sources || []).slice(0, p.max || 3);
    if (!srcs.length) return null;
    return h('div', { className: cx('ot-sources', p.className) },
      h('span', { className: 'text-caption ot-muted ot-sources-cap' }, 'Sources'),
      h('ol', { className: 'ot-sources-list' }, srcs.map(function (s, i) {
        var body = [h('span', { key: 'n', className: 'ot-source-n text-mono-sm' }, i + 1), h('span', { key: 't', className: 'ot-source-t' }, s.title), h('span', { key: 's', className: 'text-mono-sm ot-muted' }, s.site || s.domain),
          h(Icon, { key: 'x', name: 'external', size: 14, className: 'ot-source-x' })];
        return h('li', { key: s.sourceId || s.url || i }, s.url && !p.onOpenSource
          ? h('a', { className: 'ot-source', href: s.url, target: '_blank', rel: 'noopener noreferrer', title: s.title }, body)
          : h('button', { type: 'button', className: 'ot-source', title: s.title, onClick: function () { if (p.onOpenSource) p.onOpenSource(s.sourceId); } }, body));
      })));
  }
  var RESEARCH_CLOSING = 'This is about the program in general, based on its name and verified publisher. It is not a scan of your copy.';
  function ResearchNote(p) {
    var srcs = (p.sources || []).slice(0, 3);
    return h('div', { id: p.id, className: cx('ot-research', RESEARCH[p.verdict] ? RESEARCH[p.verdict].cls : 'is-safe', p.className) },
      p.summary && h('p', { className: 'text-callout ot-research-text' }, p.summary),
      p.unsigned && h('p', { className: 'text-callout ot-research-unsigned' }, h(Icon, { name: 'alert', size: 16 }), 'Not signed, so the name alone doesn’t prove what it is.'),
      h('p', { className: 'text-callout ot-muted ot-research-closing' }, RESEARCH_CLOSING),
      h(SourceList, { sources: srcs, onOpenSource: p.onOpenSource }));
  }

  function FindingCard(p) {
    return h(NeuCard, { as: 'article', className: cx('ot-finding', p.kind === 'gap' && 'ot-finding-gap', p.className) },
      h('div', { className: 'ot-finding-head' },
        h('span', { className: 'ot-finding-tags' }, h(SeverityBadge, { kind: p.kind }), p.research && h(ResearchTag, { verdict: p.research })),
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
      p.onChange !== null && h(Button, { variant: 'ghost', size: 'sm', 'aria-label': p.actionLabel ? undefined : 'Change AI', onClick: p.onChange }, p.actionLabel || 'Change'));
  }

  /* ---------- AI permissions (M9; design: PermissionsStep, PermissionsPanel, PermissionsSummary) ---------- */
  /* Rows from the M9 permission model. `fixed` rows have no toggle; `when` is the grey line per toggle state (at most one). */
  var PERM_SEE = [
    { id: 'findings', icon: 'report', title: 'Report and measurements', fixed: 'Always' },
    { id: 'specs', icon: 'cpu', title: 'PC specs', on: true },
    { id: 'history', icon: 'history', title: 'Past reports and benchmarks', on: true },
    { id: 'names', icon: 'grid', title: 'Share app and device names', on: false },
    { id: 'secrets', icon: 'lock', title: 'Paths, accounts, addresses', fixed: 'Never' }
  ];
  var PERM_DO = [
    { id: 'checks', icon: 'scan', title: 'Run read-only checks', on: true },
    { id: 'ask', icon: 'check', title: 'Ask to make single changes', on: true, when: ['Gives steps you do', 'You approve each'] },
    { id: 'fix', icon: 'list', title: 'Make approved multi-step fixes', on: true, when: [null, 'A whole fix, approved once'] },
    { id: 'scripts', icon: 'terminal', title: 'Run approved scripts', on: false, when: [null, 'You read and approve each one · can’t be undone'] },
    // design v75 (AdminRights): off and never pre-selected; disabled with its grey line while not available (adminUnavailable).
    { id: 'admin', icon: 'key', title: 'Use administrator rights', on: false, when: [null, 'Windows asks each time.'] },
    { id: 'web', icon: 'globe', title: 'Look things up online', on: false, web: true, when: [null, 'Sends the names, makers and versions of what’s on this PC · uses your AI plan'] }
  ];
  function permDefaults() { var o = {}; PERM_SEE.concat(PERM_DO).forEach(function (r) { if (!r.fixed) o[r.id] = r.on; }); return o; }
  // The toggles' linking lives in connection-state.js (permSet, tested there; loaded after this file, used at click time).
  function permSet(cur, id, val) { return window.OptTool.permSet(cur, id, val); }

  function PermRow(p) {
    var r = p.row, on = !!p.value, line = p.line !== undefined ? p.line : r.when ? r.when[on ? 1 : 0] : null;
    return h('div', { className: cx('ot-perm-row', r.fixed && 'is-fixed', p.disabled && 'is-off') },
      h('span', { className: 'ot-perm-icon', 'aria-hidden': true }, h(Icon, { name: r.icon, size: 18 })),
      h('span', { className: 'ot-perm-text' },
        h('span', { className: 'text-callout ot-perm-title' }, r.title),
        line && h('span', { className: 'text-mono-sm ot-muted ot-perm-line' }, line)),
      r.fixed ? h('span', { className: cx('ot-perm-fixed text-mono-sm', r.fixed === 'Never' ? 'is-never' : 'is-always') }, h(Icon, { name: r.fixed === 'Never' ? 'lock' : 'check', size: 14, strokeWidth: 2.25 }), r.fixed)
        : h(Toggle, { checked: on && !p.disabled, disabled: p.disabled, label: h('span', { className: 'ot-sr' }, r.title), onChange: function (v) { p.onChange(r.id, v); } }));
  }

  // The permissions content: full page after "Choose your AI" (frosted, on the sea) and compact when an outside window pairs.
  // App additions: `showScripts` (the scripts row only where the host offers it) and `busy` (while the choice is saved).
  // design v70: Continue first opens the note over the blurred page (its Continue goes on; Esc closes it); `skipNote`
  // goes straight on, `noteOpen` opens it at once (demos).
  function PermissionsPanel(p) {
    var s = React.useState(p.initial || permDefaults()), v = s[0], setV = s[1];
    var nt = React.useState(!!p.noteOpen), note = nt[0], setNote = nt[1];
    var noteBtn = React.useRef(null);
    React.useEffect(function () { if (note && noteBtn.current) noteBtn.current.focus(); }, [note]);
    var agent = p.agent || (p.compact ? 'Claude Code' : 'Claude'), explain = !!p.explainOnly;
    function set(id, val) { setV(function (cur) { return permSet(cur, id, val); }); }
    function rows(list, isDo) {
      return list.filter(function (r) { return r.id !== 'scripts' || p.showScripts; }).map(function (r) {
        var off = isDo && (explain || (r.web && p.webUnavailable) || (r.id === 'admin' && p.adminUnavailable));
        var line = r.web && p.webUnavailable ? 'Not available with ' + (p.provider || agent) : r.id === 'admin' && p.adminUnavailable ? 'Not available in this version yet' : undefined;
        return h(PermRow, { key: r.id, row: r, value: v[r.id], onChange: set, disabled: off, line: line });
      });
    }
    var groups = h('div', { className: 'ot-perm-cols' },
      h('section', { className: 'ot-perm-group', 'aria-label': 'Can see' }, h('h2', { className: 'text-caption ot-muted' }, 'Can see'), rows(PERM_SEE, false)),
      h('section', { className: 'ot-perm-group', 'aria-label': 'Can do' }, h('h2', { className: 'text-caption ot-muted' }, 'Can do'),
        explain && h('p', { className: 'ot-perm-note text-callout' }, h(Icon, { name: 'eye', size: 16 }), 'Explain only with this account'),
        rows(PERM_DO, true)));
    function done() { if (p.onContinue && !p.busy) p.onContinue(v); }
    // focusTitle (opened from the pill or from Change): the page replaces what had focus, so focus starts at its title.
    var titleRef = React.useRef(null);
    React.useEffect(function () { if (p.focusTitle && titleRef.current) titleRef.current.focus({ preventScroll: true }); }, []);
    var updated = h('span', { role: 'status', className: 'ot-perm-updated-wrap' }, p.updated && h('span', { className: 'ot-perm-updated text-mono-sm' }, h(Icon, { name: 'repeat', size: 12 }), 'Updated'));
    if (p.compact) {
      return h(GlassPanel, { strong: true, className: cx('ot-perm-panel', 'is-compact', p.className), role: 'dialog', 'aria-label': agent + ' permissions' },
        h('div', { className: 'ot-perm-chead' }, h(Icon, { name: 'terminal', size: 18 }), h('span', { className: 'text-headline' }, 'What ' + agent + ' may see and do in its own window.'), updated),
        groups,
        h('div', { className: 'ot-login-actions' },
          h(Button, { variant: 'ghost', onClick: p.onCancel }, 'Not now'),
          h(Button, { variant: 'primary', disabled: !!p.busy, onClick: done }, 'Allow')));
    }
    return h(GlassPanel, { strong: true, className: cx('ot-login-panel', 'ot-perm-panel', 'ot-frost', p.className) },
      h('div', { className: 'ot-brand' }, h(Logo, { size: 30 }), h('span', { className: 'text-callout' }, 'Optimus')),
      h('div', { className: 'ot-perm-titlerow' }, h('h1', { ref: titleRef, tabIndex: -1, className: 'text-title-1 ot-focus-title' }, 'What can ' + agent + ' do?'), updated),
      groups,
      h('div', { className: 'ot-login-actions' },
        p.onBack && h(Button, { variant: 'ghost', icon: 'back', className: 'ot-login-back', onClick: p.onBack }, 'Back'),
        h(Button, { variant: 'primary', iconRight: 'chevron', disabled: !!p.busy, onClick: function () { if (p.skipNote) done(); else setNote(true); } }, 'Continue')),
      // The choices are saved only from the note's Continue, so Esc leaves the page as it was, still editable.
      note && h('div', { className: 'ot-perm-note-layer', onKeyDown: function (e) {
          if (e.key !== 'Escape') return;
          e.stopPropagation(); setNote(false);
          // Focus goes back to the page's Continue.
          var back = e.currentTarget.parentNode && e.currentTarget.parentNode.querySelector(':scope > .ot-login-actions .ot-btn-primary'); if (back) back.focus();
        } },
        h(GlassPanel, { strong: true, className: 'ot-perm-notecard', role: 'dialog', 'aria-modal': true, 'aria-labelledby': 'ot-perm-note-t' },
          h('p', { id: 'ot-perm-note-t', className: 'text-callout' }, 'You can change these settings any time from the AI pill.'),
          h('div', { className: 'ot-login-actions' },
            h('button', { ref: noteBtn, type: 'button', className: 'ot-btn ot-btn-primary ot-btn-md', disabled: !!p.busy, onClick: function () { setNote(false); done(); } }, 'Continue', h(Icon, { name: 'chevron', size: 18 }))))));
  }
  function PermissionsStep(p) {
    return h('div', { className: cx('ot-login', p.className) },
      h(WaveBackground, null),
      h('div', { className: 'ot-login-scroll' }, h(PermissionsPanel, Object.assign({}, p, { className: null }))));
  }
  // For paired outside windows: the compact panel, pre-filled (`preset`), with Not now / Allow.
  function PermissionGrant(p) {
    return h(PermissionsPanel, { compact: true, agent: p.client || p.recipient || 'Claude Code', initial: p.preset && p.preset !== true ? p.preset : undefined,
      explainOnly: p.explainOnly, webUnavailable: p.webUnavailable, adminUnavailable: p.adminUnavailable, provider: p.provider, showScripts: p.showScripts, busy: p.busy, updated: p.updated,
      onCancel: p.onCancel, onContinue: p.onContinue, className: p.className });
  }
  // One line under the ask box in chat, in place of a grant panel. `text`: the app's own line (permissionLine).
  function permSummaryText(v, explainOnly) {
    v = v || permDefaults();
    var parts = ['Reads your report'];
    if (v.checks && !explainOnly) parts.push('runs checks');
    parts.push(explainOnly || !(v.ask || v.fix) ? 'explains only' : 'asks before changes');
    if (v.scripts && !explainOnly) parts.push('runs approved scripts');
    if (v.admin && !explainOnly) parts.push('can ask for administrator rights');
    if (v.web && !explainOnly) parts.push('looks things up');
    return parts.join(' · ');
  }
  function PermissionsSummary(p) {
    // Icon, text and Change stay on one row; only the text wraps.
    return h('div', { className: cx('ot-perm-sum text-mono-sm', p.className) },
      h(Icon, { name: 'eye', size: 14 }),
      h('span', { className: 'ot-perm-sum-text' }, p.text || permSummaryText(p.values, p.explainOnly)),
      h('button', { type: 'button', className: 'ot-perm-change', onClick: p.onChange }, 'Change'));
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

  function AgentActivityLive(p) {
    var steps = p.steps || [], b = p.budget || {};
    function meter(label, v) {
      if (!v) return null;
      return h('div', { className: 'ot-meter' },
        h('div', { className: 'ot-meter-label text-mono-sm ot-muted' }, h('span', null, label), h('span', null, v[0] + ' / ' + v[1] + (v[2] || ''))),
        h('div', { className: 'ot-track' }, h('span', { style: { width: Math.min(100, v[0] / v[1] * 100) + '%' } })));
    }
    return h(GlassPanel, { className: cx('ot-activity', p.embedded && 'is-embedded', p.className) },
      !p.embedded && h('div', { className: 'ot-activity-head' },
        h('div', null,
          h('div', { className: 'ot-live text-caption' }, h('span', { className: 'ot-live-dot', 'aria-hidden': true }), (p.agent || 'Claude') + ' · live'),
          h('h2', { className: 'text-headline' }, p.goal)),
        h(Button, { variant: 'neu', size: 'sm', icon: 'stop', onClick: p.onStop }, 'Stop')),
      h('ol', { className: 'ot-steps' }, steps.map(function (s, i) {
        var st = STEP[s.state] || STEP.done;
        return h('li', { key: i, className: 'ot-step is-' + s.state },
          h('span', { className: 'ot-step-mark', 'aria-hidden': true }, st.icon && h(Icon, { name: st.icon, size: 14, strokeWidth: 2.25 })),
          h('div', { className: 'ot-step-body' },
            h('div', { className: 'ot-step-top' },
              h('span', { className: cx('ot-origin', s.origin === 'client' && 'is-client', 'text-mono-sm') }, s.origin === 'client' ? (p.agent || 'Claude') : 'Optimizer'),
              h('span', { className: 'text-mono-sm ot-muted' }, s.tool)),
            h('div', { className: 'text-callout ot-step-title' }, s.title),
            s.detail && h('div', { className: 'text-mono-sm ot-muted' }, s.detail),
            s.action && h(Button, { variant: 'ghost', size: 'sm', icon: s.action.icon || 'file', className: 'ot-step-action', onClick: s.action.onClick }, s.action.label)),
          h('span', { className: 'ot-step-state text-mono-sm' }, st.label));
      })),
      !p.embedded && h('div', { className: 'ot-meters' }, meter('Checks', b.diagnostics), meter('Shared', b.shared), meter('Turns', b.turns)));
  }

  /* Design AgentActivity (v72). Live: the step list as before. Finished: the answer first (the AI's name with the spark
     and, with `onClose`, the × at the right of that line; the goal as a headline, “First look at your PC” when there is
     none; the text in paragraphs split by a blank line, none without `answer`), then the steps folded into one line,
     “Steps <AI> took · N”, which opens the list. `list` renders the list (default: the design's live list, embedded). */
  function FinishedRun(p) {
    var o = React.useState(false), open = o[0], setOpen = o[1];
    var who = p.agent || 'Claude';
    var paras = typeof p.answer === 'string' ? p.answer.split(/\n\n+/) : null;
    return h(GlassPanel, { className: cx('ot-activity is-finished', p.className), role: p.role, 'aria-label': p.ariaLabel },
      h('div', { className: 'ot-activity-answer' },
        h('div', { className: 'ot-answer-head' },
          h('div', { className: 'text-caption ot-muted ot-answer-from' }, h(Icon, { name: 'spark', size: 14 }), who),
          p.onClose && h('button', { type: 'button', className: 'ot-rp-x', 'aria-label': 'Close', onClick: p.onClose }, h(Icon, { name: 'close', size: 18 }))),
        h('h2', { className: 'text-headline' }, p.goal || 'First look at your PC'),
        paras ? paras.map(function (t, i) { return h('p', { key: i, className: 'text-body' }, t); }) : p.answer),
      p.count > 0 && h('button', { type: 'button', className: 'ot-steps-toggle text-callout', 'aria-expanded': open, onClick: function () { setOpen(!open); } },
        h(Icon, { name: 'list', size: 16 }), h('span', null, 'Steps ' + who + ' took'), h('span', { className: 'text-mono-sm ot-muted' }, p.count),
        h(Icon, { name: 'chevron', size: 16, className: 'ot-steps-chev' })),
      h(Expand, { contentKey: open ? 'open' : 'closed' }, open ? p.list() : null));
  }
  function AgentActivity(p) {
    if (!p.finished) return AgentActivityLive(p);
    return h(FinishedRun, { className: p.className, agent: p.agent, goal: p.goal, answer: p.answer, onClose: p.onClose, count: (p.steps || []).length,
      list: function () { return AgentActivityLive(Object.assign({}, p, { embedded: true, className: null })); } });
  }

  /* Every change, single or multi-step (design: ApprovalCard with `steps`, M9 §4; a single change is one step, v53).
     States: awaiting → applying → verified | partial | stopped | failed, ineligible (which step and why), and expired
     (stale or lapsed before approval: a plain line and Dismiss). */
  var STEP_BADGES = {
    admin: { text: 'Admin', icon: 'key' },
    irreversible: { text: 'Can’t be undone', icon: 'lock', risk: true },
    unsaved: { text: 'May lose unsaved work', icon: 'alert', risk: true },
    program: { text: 'Runs a program', icon: 'terminal' },
    restart: { text: 'Restart needed', icon: 'repeat' },
    signout: { text: 'Sign-out needed', icon: 'back' },
    network: { text: 'Changes network', icon: 'network' },
    slow: { text: 'Takes a while', icon: 'clock' }
  };
  var STATE_ALIAS = { ask: 'awaiting', running: 'applying', done: 'verified' };
  var ASTEP = { done: 'Verified', running: 'Applying', queued: 'Next', failed: 'Failed', skipped: 'Not run', undone: 'Undone', unclear: 'Unclear', blocked: 'Blocked' };
  var ASTEP_NOTE = { unclear: 'Optimus couldn’t confirm this worked.' };
  // Why an approval couldn't start (design RebuildCases, v53); the free-text error is only the fallback.
  var START_ERRORS = {
    busy: 'Another change is running. Try again when it finishes.',
    scan: 'A scan is running. Try again when it finishes.',
    benchmark: 'A benchmark is running. Try again when it finishes.',
    closed: 'The Windows approval window was closed, so nothing changed.',
    cancelled: 'This plan was cancelled. Nothing changed.'
  };
  function startError(p) { return START_ERRORS[p.errorCause] || (p.error ? 'Couldn’t start: ' + p.error : null); }
  // Results of actions that only report (design GuidedAndReports, v56). Keys for the engine; text for anything else.
  var REPORT_RESULTS = {
    sfcClean: 'Windows checked its files and found nothing wrong.',
    sfcRepaired: 'Windows found damaged files and repaired them.',
    sfcPartial: 'Windows found damaged files but couldn’t repair some of them. Repairing the Windows image may help.',
    dismHealthy: 'The Windows image is healthy. Nothing needed repair.',
    dismRepaired: 'Windows repaired its image. Check Windows files again to finish.',
    dismFailed: 'Windows couldn’t repair its image.',
    dnsCleared: 'The DNS cache is cleared. Websites will look up their addresses again.',
    // design ReportsMore (v59)
    sfcUnknown: 'Windows finished checking its files. Optimus couldn’t read what it found.',
    dismUnknown: 'Windows finished repairing its image. Optimus couldn’t read the result.',
    explorerRestarted: 'Windows Explorer restarted.',
    explorerStarted: 'Windows Explorer had to be started again. It’s running now.',
    explorerDown: 'Windows Explorer didn’t come back. Press Ctrl+Shift+Esc, choose Run new task, type explorer and press Enter.'
  };
  // Tone per key when the app doesn't pass one (design v59).
  var REPORT_TONE = { sfcPartial: 'problem', dismFailed: 'problem', explorerDown: 'problem', sfcUnknown: 'unknown', dismUnknown: 'unknown', sfcRepaired: 'fixed', dismRepaired: 'fixed', explorerStarted: 'fixed' };
  // A report-only action's failure on its step (the Failed card, design v59 step.failKey; explorerFailed v60).
  var FAIL_NOTES = { sfcFailed: 'Windows couldn’t finish checking its files. Restart your PC and try again.', dnsFailed: 'Windows couldn’t clear the DNS cache.',
    explorerFailed: 'Windows Explorer couldn’t be restarted. If the taskbar is missing, restart your PC.' };
  // Why a script never started (design notStartedCause, v53); never the app's or the host's own text.
  var NOT_STARTED = {
    changed: 'The script changed after you approved it, so it didn’t run.',
    policy: 'A setting on this PC now blocks scripts.',
    powershell: 'Windows PowerShell on this PC couldn’t be checked, so it didn’t run.',
    failed: 'Windows couldn’t start it.',
    version: 'Scripts can’t run in this version of Optimus.',
    appstopped: 'Optimus closed before it started.',
    declined: 'You closed the Windows prompt, so it didn’t run.',
    helper: 'Windows didn’t allow it to run as administrator.'
  };
  // `undo` (from the operation) wins; without it, the design's rule from the badges; never a step with nothing to undo
  // (design v62). The rule lives in fix-plan.js (stepCanUndo), shared with the tests.
  function canUndo(s) { return window.OptTool.fixPlan.stepCanUndo(s); }
  function stepWord(list, single) { return single ? 'This' : (list.length === 1 ? 'Step ' : 'Steps ') + list.join(list.length === 2 ? ' and ' : ', '); }
  // The red banner and the required tick before Approve (irreversible or data-loss). risky / lose: step numbers.
  function RiskTick(p) {
    var risky = p.risky || [], lose = p.lose || [], hasR = risky.length > 0, hasL = lose.length > 0;
    return h('div', { className: 'ot-plan-warn', role: 'note' },
      h(Icon, { name: 'alert', size: 18 }),
      h('div', { className: 'ot-plan-warn-body' },
        h('span', { className: 'text-callout' },
          hasR && h('strong', null, stepWord(risky, p.single) + ' can’t be undone.'), hasR && hasL ? ' ' : '',
          hasL && h('strong', null, stepWord(lose, p.single) + ' may lose unsaved work.'),
          p.restorePoint ? ' A restore point is made first.' : ''),
        h('label', { className: 'ot-plan-tick text-callout' },
          h('input', { type: 'checkbox', checked: !!p.ticked, disabled: p.disabled, onChange: function (e) { p.onTick(e.target.checked); } }),
          'I understand this can’t be undone')));
  }

  // When a card changes state the control that had focus can disappear (Approve becomes Stop, Stop becomes Transcript).
  // Focus then falls to the page; it goes to the first control in the card's foot instead. Never moves focus that is
  // somewhere else on the page.
  // checking: Check again is disabled while it runs (it reads Checking…), so focus falls to the page; when the answer
  // leaves the card as it was, focus goes back to Check again.
  function useKeepFocus(state, checking) {
    var ref = React.useRef(null), seen = React.useRef(state), was = React.useRef(!!checking);
    React.useEffect(function () {
      if (seen.current === state) return;   // not on mount: a card that appears never takes focus
      seen.current = state;
      var a = document.activeElement;
      if (a && a !== document.body) return;
      var b = ref.current && ref.current.querySelector('button:not(:disabled)');
      if (b) b.focus();
    }, [state]);
    React.useEffect(function () {
      var before = was.current; was.current = !!checking;
      if (!before || checking) return;
      var a = document.activeElement;
      if (a && a !== document.body) return;
      var b = ref.current && [].slice.call(ref.current.querySelectorAll('button:not(:disabled)')).filter(function (x) { return x.textContent.trim() === 'Check again'; })[0];
      if (b) b.focus();
    }, [checking]);
    return ref;
  }

  // Design ApprovalCard with steps (v51; report results, failKey and recheck v59; guided with no Windows page v60), ported. App additions: the foot keeps focus when its control disappears
  // (useKeepFocus, also around Check again's Checking…), one polite live region for progress and outcome, an accessible name per step Undo, `undoBusy`
  // (an Undo on its way), and a step's `undo` from the operation (wins over the design's badge rule).
  function ApprovalCard(p) {
    var steps = p.steps;
    var t = React.useState(false), ticked = t[0], setTicked = t[1];
    // design v75 (PlanInCard): steps switched off in a card (stepToggles); onApprove gets the kept indexes.
    var of = React.useState({}), offSteps = of[0], setOffSteps = of[1];
    var footRef = useKeepFocus(p.state, p.checking);
    if (!steps) return null;   // a single change is one step (the old rows/details card is retired)
    steps = steps.slice(0, 6);   // the host refuses more (too_large); never cut here
    var st = STATE_ALIAS[p.state] || p.state || 'awaiting';
    var ended = st === 'verified' || st === 'unclear' || st === 'doneLeftOut' || st === 'partial' || st === 'failed' || st === 'stopped' || st === 'cantrun' || st === 'expired';
    var single = steps.length === 1;
    var tone = p.result ? (p.result.tone || REPORT_TONE[p.result.key] || 'ok') : 'ok';
    var risky = [], lose = [];
    steps.forEach(function (s, i) { if (offSteps[i]) return; var b = s.badges || []; if (b.indexOf('irreversible') >= 0) risky.push(i + 1); if (b.indexOf('unsaved') >= 0) lose.push(i + 1); });
    var needsTick = risky.length + lose.length > 0;
    var hasRP = steps.some(function (s) { return s.restorePoint; });
    var doneN = steps.filter(function (s) { return s.state === 'done'; }).length;
    var undoneN = steps.filter(function (s) { return s.state === 'undone'; }).length;
    var undoable = window.OptTool.fixPlan.undoAllCount(steps);
    var failedAt = steps.map(function (s) { return s.state; }).indexOf('failed');
    var blockedAt = steps.map(function (s) { return s.state; }).indexOf('blocked');
    var stepName = function (n) { return single ? 'This change' : 'Step ' + n; };
    var head = st === 'awaiting' ? h(SeverityBadge, { kind: 'exposure', icon: 'alert' }, 'Approve?')
      : st === 'applying' ? h('span', { className: 'ot-badge ot-badge-run' }, h(Logo, { size: 14, mono: true, spin: true, decorative: true }), 'Applying')
        : st === 'verified' ? h(SeverityBadge, { kind: 'verified' }, 'Verified')
          : st === 'unclear' ? h(SeverityBadge, { kind: 'gap', icon: 'report' }, 'Not confirmed')
          : st === 'doneLeftOut' ? h(SeverityBadge, { kind: 'gap', icon: 'check' }, 'Done, steps left out')
          : st === 'partial' ? h(SeverityBadge, { kind: 'exposure', icon: 'alert' }, 'Partly done')
            : st === 'stopped' ? h(SeverityBadge, { kind: 'gap', icon: 'stop' }, 'Stopped')
              : st === 'cantrun' ? h(SeverityBadge, { kind: 'gap', icon: 'lock' }, 'Can’t run')
                : st === 'expired' ? h(SeverityBadge, { kind: 'gap', icon: 'clock' }, 'Expired')
                  : st === 'reported' ? h(SeverityBadge, { kind: tone === 'problem' ? 'exposure' : tone === 'unknown' ? 'gap' : 'verified', icon: tone === 'problem' ? 'alert' : 'report' }, tone === 'problem' ? 'Finished with problems' : 'Finished')
                  : st === 'guided' ? h('span', { className: 'ot-badge ot-badge-run' }, h(Icon, { name: 'sliders', size: 14 }), 'You do this one')
                  : h(SeverityBadge, { kind: 'threat', icon: 'alert' }, 'Failed');
    function row(r, k) {
      // A row whose before and after are the same only names the thing: label and value, no arrow.
      var same = r.length < 3 || r[2] == null || r[1] === r[2];
      return h('div', { key: k, className: cx('ot-plan-row text-mono-sm', same && 'is-name') },
        h('span', { className: 'ot-muted' }, r[0]),
        same ? h('span', { className: 'ot-diff-after' }, r[1]) : [h('span', { key: 'b', className: 'ot-diff-before' }, r[1]),
          h(Icon, { key: 'a', name: 'arrow', size: 14, className: 'ot-muted' }), h('span', { key: 'c', className: 'ot-diff-after' }, r[2])]);
    }
    var list = st !== 'expired' && h('ol', { className: cx('ot-plan', steps.length === 1 && 'is-single') }, steps.map(function (s, i) {
      var ss = s.state || 'queued';
      var live = st !== 'awaiting' && st !== 'guided' && st !== 'reported' && !(st === 'cantrun' && ss !== 'blocked');
      var mark = !live ? i + 1 : ss === 'done' ? h(Icon, { name: 'check', size: 14, strokeWidth: 2.5 }) : ss === 'failed' ? h(Icon, { name: 'close', size: 14, strokeWidth: 2.5 })
        : ss === 'undone' ? h(Icon, { name: 'undo', size: 14 }) : ss === 'running' ? h(Logo, { size: 14, mono: true, spin: true, decorative: true })
          : ss === 'unclear' ? '?' : ss === 'blocked' ? h(Icon, { name: 'lock', size: 13 }) : i + 1;
      var note = s.note || FAIL_NOTES[s.failKey] || ASTEP_NOTE[ss];
      var end = live && ss === 'done' && (s.settings && p.onOpenSettings ? h(Button, { variant: 'ghost', size: 'sm', icon: 'sliders', onClick: function () { p.onOpenSettings(i); } }, 'Open settings')
        : canUndo(s) && p.onUndoStep ? h(Button, { variant: 'ghost', size: 'sm', icon: 'undo', 'aria-label': 'Undo step ' + (i + 1), disabled: !!p.undoBusy, onClick: function () { p.onUndoStep(i); } }, 'Undo') : null)
        || live && (ss === 'unclear' || ss === 'failed') && [
          ss === 'failed' && s.settings && p.onOpenSettings && h(Button, { key: 's', variant: 'ghost', size: 'sm', icon: 'sliders', onClick: function () { p.onOpenSettings(i); } }, 'Open settings'),
          p.onCheckStep && h(Button, { key: 'c', variant: 'ghost', size: 'sm', icon: 'repeat', onClick: function () { p.onCheckStep(i); } }, 'Check again')];
      if (Array.isArray(end) && !end.some(Boolean)) end = null;
      // In a card, each step has a switch to leave it out; at least one stays on. Review 6: locked while the approval is on its way.
      var toggles = p.stepToggles && st === 'awaiting' && steps.length > 1;
      if (toggles) {
        var keptN = steps.filter(function (x, k) { return !offSteps[k]; }).length;
        end = h(Toggle, { checked: !offSteps[i], disabled: (!offSteps[i] && keptN <= 1) || !!p.waiting, label: h('span', { className: 'ot-sr' }, 'Include step ' + (i + 1)),
          onChange: function (v) { setOffSteps(function (cur) { var n = Object.assign({}, cur); if (v) delete n[i]; else n[i] = true; return n; }); } });
      }
      return h('li', { key: s.n || i, className: cx('ot-plan-step', live && 'is-' + ss, toggles && offSteps[i] && 'is-left-out') },
        h('span', { className: 'ot-plan-num text-mono-sm', 'aria-hidden': true }, mark),
        h('div', { className: 'ot-plan-body' },
          h('div', { className: 'ot-plan-top' },
            h('span', { className: 'text-callout ot-plan-title' }, s.label || s.title),
            live && h('span', { className: cx('ot-plan-state text-mono-sm', 'is-' + ss) }, ASTEP[ss] || '')),
          s.does && !(live && ss === 'done' && s.receipt) && h('p', { className: 'text-callout ot-muted ot-plan-does' }, s.does),
          live && ss === 'done' && s.receipt && h('p', { className: 'text-callout ot-plan-receipt' }, s.receipt),
          live && ss === 'running' && s.progress && h('div', { className: 'ot-plan-progress' },
            h('span', { className: 'text-mono-sm ot-muted', 'aria-live': 'polite' }, [s.progress.text, s.progress.pct != null ? s.progress.pct + '%' : null].filter(Boolean).join(' · ')),
            s.progress.pct != null ? h('div', { className: 'ot-track ot-track-thin', role: 'progressbar', 'aria-label': s.label || s.title, 'aria-valuenow': s.progress.pct, 'aria-valuemin': 0, 'aria-valuemax': 100 }, h('span', { style: { width: s.progress.pct + '%' } }))
              : h('div', { className: 'ot-track ot-track-thin is-indeterminate', role: 'progressbar', 'aria-label': s.label || s.title, 'aria-valuetext': s.progress.text || 'Running' }, h('span', null))),
          st === 'awaiting' && (single || s.undoNote) && canUndo(s) && h('p', { className: 'text-mono-sm ot-muted ot-plan-undo' }, h(Icon, { name: 'undo', size: 12 }), s.undoNote || 'You can undo this afterwards.'),
          st === 'awaiting' && s.settings && h('p', { className: 'text-mono-sm ot-muted ot-plan-undo' }, h(Icon, { name: 'sliders', size: 12 }), 'Optimus can’t undo this one. You can change it back in Settings.'),
          s.rows && s.rows.length > 0 && h('div', { className: 'ot-plan-rows' }, s.rows.slice(0, 4).map(row)),
          (function () {
            // One trade-off: one line. Several: "Trade-offs:" and a short list (v66).
            var to = [].concat(s.tradeoff || []).filter(Boolean);
            if (!to.length) return null;
            if (to.length === 1) return h('p', { className: 'text-mono-sm ot-muted ot-plan-tradeoff' }, 'Trade-off: ' + to[0]);
            return h('div', { className: 'text-mono-sm ot-muted ot-plan-tradeoff' }, 'Trade-offs:', h('ul', null, to.map(function (x, k) { return h('li', { key: k }, x); })));
          })(),
          s.badges && s.badges.length > 0 && h('div', { className: 'ot-plan-badges' }, s.badges.map(function (b) {
            var d = STEP_BADGES[b]; if (!d) return null;
            return h('span', { key: b, className: cx('ot-step-badge text-mono-sm', d.risk && 'is-risk') }, h(Icon, { name: d.icon, size: 12, strokeWidth: 2.25 }), d.text);
          })),
          s.why && !live && h('p', { className: 'text-mono-sm ot-muted ot-plan-why' }, 'Why: ' + s.why),
          live && s.settings && ss === 'done' && h('p', { className: 'text-mono-sm ot-muted' }, 'Optimus can’t undo this one. Change it back in Settings.'),
          live && note && (ss === 'failed' || ss === 'blocked' || ss === 'unclear') && h('p', { className: cx('text-callout', ss === 'unclear' ? 'ot-muted' : 'ot-plan-fail') }, note)),
        end && h('span', { className: 'ot-plan-end' }, end));
    }));
    var leftN = (p.removed || []).length + (p.leftOut || []).length;
    var count = st === 'doneLeftOut' && !undoneN ? doneN + ' done · ' + leftN + ' left out' : undoneN ? undoneN + ' undone' + (doneN ? ' · ' + doneN + ' kept' : '') : (st === 'cantrun' || st === 'expired' || (st === 'failed' && !doneN)) ? 'Nothing changed' : single ? (doneN ? 'Done' : 'Not done') : doneN + ' of ' + steps.length + ' done';
    var closeBtns = [p.onClose && h(Button, { key: 'c', variant: 'ghost', onClick: p.onClose }, st === 'expired' ? 'Dismiss' : 'Close'),
      p.onNext && st !== 'expired' && h(Button, { key: 'n', variant: 'neu', iconRight: 'chevron', onClick: p.onNext }, 'Next')];
    var foot;
    if (st === 'reported') foot = h('div', { className: 'ot-approval-actions' },
      p.onClose && h(Button, { variant: 'ghost', onClick: p.onClose }, 'Close'),
      p.onCheckAgain && h(Button, { variant: 'neu', icon: 'repeat', disabled: p.checking, onClick: p.onCheckAgain }, p.checking ? 'Checking…' : 'Check again'));
    else if (st === 'guided') foot = h('div', { className: 'ot-approval-actions' },
      p.onBack && h(Button, { variant: 'ghost', icon: 'back', className: 'ot-plan-back', onClick: p.onBack }, 'Back'),
      p.onClose && h(Button, { variant: 'ghost', onClick: p.onClose }, 'Close'),
      p.onCheckAgain && h(Button, { variant: p.onOpenPlanSettings ? 'neu' : 'primary', icon: 'repeat', disabled: p.checking, onClick: p.onCheckAgain }, p.checking ? 'Checking…' : 'Check again'),
      p.onOpenPlanSettings && h(Button, { variant: 'primary', icon: 'sliders', onClick: p.onOpenPlanSettings }, 'Open settings'));
    else if (st === 'awaiting') foot = h('div', { className: 'ot-approval-actions' },
      p.onBack && h(Button, { variant: 'ghost', icon: 'back', className: 'ot-plan-back', onClick: p.onBack }, 'Back'),
      p.waiting ? h('span', { className: 'text-mono-sm ot-muted ot-plan-count', 'aria-live': 'polite' }, 'Approve in the Windows window that opened.')
        : startError(p) ? h('span', { className: 'text-callout ot-plan-fail ot-plan-count', role: 'alert' }, startError(p)) : null,
      h(Button, { variant: 'ghost', disabled: p.waiting, onClick: p.onDecline }, p.errorCause === 'cancelled' ? 'Close' : p.inCard ? 'Not now' : 'Decline'),
      p.errorCause !== 'cancelled' && h(Button, { variant: 'primary', icon: p.waiting ? null : startError(p) ? 'repeat' : 'check', disabled: p.waiting || (needsTick && !ticked),
        onClick: function () { if (p.onApprove) p.onApprove(steps.map(function (x, k) { return k; }).filter(function (k) { return !offSteps[k]; })); } },
        p.waiting ? 'Waiting…' : startError(p) ? 'Retry' : p.inCard ? 'Approve…' : 'Approve'));
    else if (st === 'applying') foot = h('div', { className: 'ot-approval-actions' },
      h('span', { className: 'text-mono-sm ot-muted ot-plan-count', 'aria-live': 'polite' }, single ? (p.stopping ? 'Stopping…' : 'Stop ends it now. Anything already done stays.') : p.stopping ? 'Stopping after this step…' : 'Step ' + Math.min(doneN + 1, steps.length) + ' of ' + steps.length),
      p.onStop && h(Button, { variant: 'neu', icon: 'stop', disabled: p.stopping, onClick: p.onStop }, p.stopping ? 'Stopping…' : single ? 'Stop' : 'Stop after this step'));
    else foot = h('div', { className: 'ot-approval-actions' },
      h('span', { className: 'text-mono-sm ot-muted ot-plan-count' }, count),
      undoable > 1 && p.onUndoAll && h(Button, { variant: 'neu', icon: 'undo', disabled: !!p.undoBusy, onClick: p.onUndoAll }, 'Undo all'),
      (st === 'partial' || st === 'failed') && p.onRetry && h(Button, { variant: 'primary', onClick: p.onRetry }, 'Retry'),
      st === 'cantrun' && p.onOpenPlanSettings && h(Button, { variant: 'neu', icon: 'sliders', onClick: p.onOpenPlanSettings }, 'Open settings'),
      closeBtns);
    var banner = null;
    if (st === 'cantrun' && blockedAt < 0) banner = h('div', { className: 'ot-plan-warn is-neutral', role: 'alert' }, h(Icon, { name: 'lock', size: 18 }),
      h('span', { className: 'text-callout' }, h('strong', null, 'This fix can’t run. Nothing was changed.'), p.blockedWhy ? ' ' + p.blockedWhy : ''));
    else if (st === 'cantrun' && blockedAt >= 0) banner = h('div', { className: 'ot-plan-warn is-neutral', role: 'alert' }, h(Icon, { name: 'lock', size: 18 }),
      h('span', { className: 'text-callout' }, h('strong', null, stepName(blockedAt + 1) + ' can’t run. Nothing was changed.'), steps[blockedAt].note ? ' ' + steps[blockedAt].note : ''));
    else if (st === 'guided') banner = h('div', { className: 'ot-plan-warn is-guide', role: 'note' }, h(Icon, { name: 'sliders', size: 18 }),
      h('span', { className: 'text-callout' }, h('strong', null, p.onOpenPlanSettings ? 'You do this one in Windows Settings.' : 'You do this one yourself.'), ' ',
        p.guideText || (p.onOpenPlanSettings ? 'Open settings takes you to the right page. Come back and press Check again.' : 'Follow the steps, then come back and press Check again.'),
        h('span', { className: 'ot-plan-recheck text-mono-sm', role: 'status' }, p.stillSame ? 'Checked again. Nothing has changed yet.' : '')));
    else if (st === 'reported' && p.result) banner = h('div', { className: cx('ot-plan-warn', tone === 'problem' ? 'is-result' : tone === 'unknown' ? 'is-neutral' : 'is-ok'), role: 'status' }, h(Icon, { name: tone === 'problem' ? 'alert' : tone === 'unknown' ? 'report' : 'check', size: 18 }),
      h('span', { className: 'text-callout' }, p.result.text || REPORT_RESULTS[p.result.key] || '',
        h('span', { className: 'ot-plan-recheck text-mono-sm', role: 'status' }, p.recheck === 'gone' ? 'Checked again: the problem is gone.' : p.recheck === 'still' ? 'Checked again: the problem is still there.' : '')));
    else if (st === 'expired') banner = h('div', { className: 'ot-plan-warn is-neutral', role: 'note' }, h(Icon, { name: 'clock', size: 18 }),
      h('span', { className: 'text-callout' }, h('strong', null, 'This plan is out of date.'), ' Ask again for a new one.'));
    else if (st === 'unclear') banner = h('div', { className: 'ot-plan-warn is-neutral', role: 'status' }, h(Icon, { name: 'report', size: 18 }),
      h('span', { className: 'text-callout' }, h('strong', null, 'Optimus couldn’t confirm this worked.'), single ? ' Press Check again.' : ' Press Check again on the step marked Unclear.'));
    else if (st === 'doneLeftOut') banner = h('div', { className: 'ot-plan-warn is-neutral', role: 'status' }, h(Icon, { name: 'check', size: 18 }),
      h('span', { className: 'text-callout' }, h('strong', null, 'Done, with steps left out.'), ' The steps that ran are verified. The next scan shows whether the problem is gone.'));
    else if (st === 'stopped') banner = h('div', { className: 'ot-plan-warn is-neutral', role: 'status' }, h(Icon, { name: 'stop', size: 18 }),
      h('span', { className: 'text-callout' }, h('strong', null, 'Stopped.'), single ? ' Anything already done stays.' : ' Later steps weren’t run.'));
    else if ((st === 'partial' || st === 'failed') && failedAt >= 0) banner = h('div', { className: 'ot-plan-warn is-result', role: 'alert' }, h(Icon, { name: 'alert', size: 18 }),
      h('span', { className: 'text-callout' }, h('strong', null, stepName(failedAt + 1) + ' failed.'), ' ', doneN ? 'Earlier steps are done and keep their Undo. Later steps weren’t run.' : 'Nothing was changed.'));
    // One live region for the whole run, so progress and a calm outcome are heard (failures and Can't run are alerts).
    var announce = st === 'applying' ? (single ? '' : 'Step ' + Math.min(doneN + 1, steps.length) + ' of ' + steps.length)
      : st === 'verified' || st === 'unclear' || st === 'doneLeftOut' || st === 'partial' ? count : '';
    return h(NeuCard, { as: 'section', className: cx('ot-approval', 'is-' + st, p.className), 'aria-busy': st === 'applying' || undefined, 'aria-label': p.title || undefined },
      h('span', { className: 'ot-sr', 'aria-live': 'polite' }, announce),
      h('div', { className: 'ot-approval-head' }, head, p.requestedBy !== null && h('span', { className: 'text-mono-sm ot-muted' }, p.requestedBy || 'Claude')),
      h('h3', { className: 'text-title-2' }, p.title),
      p.reason && st !== 'expired' && h('blockquote', { className: 'ot-plan-quote text-callout' }, h('span', { className: 'ot-plan-from' }, 'From ' + (p.from || 'Claude') + ': '), p.reason),
      st === 'applying' && h('div', { className: cx('ot-track', single && steps[0].progress && steps[0].progress.pct == null && 'is-indeterminate'), role: 'progressbar', 'aria-label': 'Steps done', 'aria-valuenow': doneN, 'aria-valuemin': 0, 'aria-valuemax': steps.length }, h('span', { style: { width: (single && steps[0].progress && steps[0].progress.pct != null ? steps[0].progress.pct : doneN / steps.length * 100) + '%' } })),
      banner,
      list,
      // design v75/v76: left out for administrator rights (before approval, with Permissions when they are off); after
      // approval, what the narrowed plan left out (removed: by you) and what Optimus left out.
      st === 'awaiting' && p.leftOut && p.leftOut.length > 0 && h('ul', { className: 'ot-plan-leftout' }, p.leftOut.map(function (x, k) {
        return h('li', { key: k, className: 'text-callout' }, h(Icon, { name: 'lock', size: 14 }),
          h('span', null, h('strong', null, x.label), ' · ', x.reason === 'off' ? 'Left out: needs administrator rights, which are off.' : 'Left out: needs administrator rights, not available in this version yet.'),
          x.reason === 'off' && p.onPermissions && h('button', { type: 'button', className: 'ot-perm-change', onClick: p.onPermissions }, 'Permissions'));
      })),
      st !== 'awaiting' && leftN > 0 && h('ul', { className: 'ot-plan-leftout' },
        (p.removed || []).map(function (x, k) { return h('li', { key: 'r' + k, className: 'text-callout' }, h(Icon, { name: 'minus', size: 14 }), h('span', null, h('strong', null, x), ' · Left out by you')); }),
        (p.leftOut || []).map(function (x, k) { return h('li', { key: 'a' + k, className: 'text-callout' }, h(Icon, { name: 'lock', size: 14 }), h('span', null, h('strong', null, x.label), ' · Left out: needs administrator rights')); })),
      st === 'awaiting' && p.onChangeRequest && h(AskField, { placeholder: 'Change something…', onSend: p.onChangeRequest, className: 'ot-plan-change' }),
      st === 'awaiting' && needsTick && h(RiskTick, { single: single, risky: risky, lose: lose, restorePoint: hasRP, ticked: ticked, onTick: setTicked, disabled: p.waiting }),
      ended && hasRP && p.restorePointAt && h('div', { className: 'ot-plan-line text-callout' }, h(Icon, { name: 'history', size: 16 }), 'Restore point · ' + p.restorePointAt),
      h('div', { className: 'ot-approval-foot', ref: footRef }, foot));
  }
  /* ---------- Script approval (M9 §5.2; design ScriptApproval) ---------- */
  /* A script is always approved on its own. The full text must be scrolled to the end, the user ticks that it can't be
     undone, and an optional restore point comes first. No Undo anywhere on this card. */
  var SCRIPT_RP_REASON = { off: 'Restore points are off on this PC', unsupported: 'Not available in this version' };
  var SCRIPT_ENDED = { finished: 1, failed: 1, stopped: 1, timeout: 1, interrupted: 1 };

  function ScriptCode(p) {
    var ref = React.useRef(null);
    // Tabs show as four spaces; CRLF (the approved bytes) and LF both end a line; nothing is folded or cut.
    var lines = (p.script || '').replace(/\r\n?/g, '\n').replace(/\t/g, '    ').replace(/\n$/, '').split('\n').slice(0, 200);
    var parts = (p.parts || []).slice(0, 12);
    React.useEffect(function () {
      var el = ref.current; if (!el || !p.onReadAll) return;
      if (el.scrollHeight <= el.clientHeight + 4) p.onReadAll();
    }, []);
    function onScroll(e) { var el = e.currentTarget; if (p.onReadAll && el.scrollTop + el.clientHeight >= el.scrollHeight - 4) p.onReadAll(); }
    var rows = [], w = String(lines.length).length;
    lines.forEach(function (ln, i) {
      var n = i + 1, part = parts.filter(function (q) { return n >= q.fromLine && n <= q.toLine; })[0];
      rows.push(h('div', { key: 'l' + n, className: cx('ot-script-line', part && 'is-part') },
        h('span', { className: 'ot-script-n', 'aria-hidden': true }, String(n).padStart(w, ' ')),
        h('span', { className: 'ot-script-src' }, ln || ' ')));
      parts.forEach(function (q, k) {
        if (q.toLine === n) rows.push(h('blockquote', { key: 'p' + k, className: 'ot-script-explain text-callout' },
          h('span', { className: 'text-mono-sm ot-muted' }, (q.fromLine === q.toLine ? 'Line ' + q.fromLine : 'Lines ' + q.fromLine + '–' + q.toLine) + ' · ' + (p.ai || 'Claude') + '’s explanation, not checked by Optimus'),
          h('span', null, q.explains)));
      });
    });
    return h('div', { ref: ref, className: 'ot-script-code text-mono-sm', role: 'region', tabIndex: 0, 'aria-label': 'Script, ' + lines.length + ' lines', onScroll: onScroll }, rows);
  }

  // Design ScriptApproval (v51), ported. App additions: `contentKey` (a new plan resets the read and the tick), a
  // controlled transcript view (`view` / `onView`), focus kept in the foot, one polite live region for the run.
  function ScriptApproval(p) {
    var st = p.state || 'awaiting';
    var r = React.useState(false), readAll = r[0], setReadAll = r[1];
    var t = React.useState(false), ticked = t[0], setTicked = t[1];
    var rpAvail = !p.restorePoint || p.restorePoint === 'available';
    var rp = React.useState(rpAvail), rpOn = rp[0], setRpOn = rp[1];
    var tr = React.useState(p.transcriptOpen ? 'transcript' : p.sentOpen ? 'sent' : null), ownView = tr[0], setOwnView = tr[1];
    var view = p.onView ? p.view || null : ownView, setView = p.onView || setOwnView;
    var so = React.useState(true), shareOn = so[0], setShareOn = so[1];
    var footRef = useKeepFocus(st);
    // Any change of plan content resets the read-to-the-end and the tick (a changed byte is a new plan). Done while
    // rendering, so the new script's own "fits without scrolling" check (a child effect) comes after it.
    var ck = React.useState(p.contentKey), seenKey = ck[0], setSeenKey = ck[1];
    if (seenKey !== p.contentKey) { setSeenKey(p.contentKey); setReadAll(false); setTicked(false); setRpOn(rpAvail); setShareOn(true); }
    var ai = p.from || 'Claude';
    var warnings = p.warnings || [];
    var head = st === 'awaiting' ? h(SeverityBadge, { kind: 'exposure', icon: 'alert' }, 'Approve script?')
      : st === 'running' ? h('span', { className: 'ot-badge ot-badge-run' }, h(Logo, { size: 14, mono: true, spin: true, decorative: true }), 'Running · ' + (p.elapsed || '0:00'))
        : st === 'finished' ? h(SeverityBadge, { kind: 'verified' }, 'Finished · exit ' + (p.exitCode == null ? 0 : p.exitCode))
          : st === 'stopped' ? h(SeverityBadge, { kind: 'gap', icon: 'stop' }, 'Stopped')
            : st === 'timeout' ? h(SeverityBadge, { kind: 'threat', icon: 'clock' }, 'Timed out after 10 min')
              : st === 'interrupted' ? h(SeverityBadge, { kind: 'exposure', icon: 'alert' }, 'Interrupted')
              : st === 'notrun' ? h(SeverityBadge, { kind: 'gap', icon: 'gap' }, 'Not run')
              : st === 'notstarted' ? h(SeverityBadge, { kind: 'gap', icon: 'lock' }, 'Not started')
                : h(SeverityBadge, { kind: 'threat', icon: 'alert' }, 'Failed · exit ' + (p.exitCode == null ? 1 : p.exitCode));
    var ended = !!SCRIPT_ENDED[st];
    // The running clock is not read out; Not started, Not run and Interrupted are alerts of their own.
    var announce = st === 'awaiting' ? (readAll ? 'Read to the end' : '') : st === 'running' ? 'Running'
      : st === 'finished' ? 'Finished, exit ' + (p.exitCode == null ? 0 : p.exitCode) : st === 'stopped' ? 'Stopped' : st === 'timeout' ? 'Timed out'
        : st === 'failed' ? 'Failed' + (p.exitCode == null ? '' : ', exit ' + p.exitCode) : '';
    return h(NeuCard, { as: 'section', className: cx('ot-approval', 'ot-script', 'is-' + st, p.className), 'aria-busy': st === 'running' || undefined, 'aria-label': p.title || undefined },
      h('span', { className: 'ot-sr', 'aria-live': 'polite' }, announce),
      h('div', { className: 'ot-approval-head' }, head, h('span', { className: 'text-mono-sm ot-muted' }, p.requestedBy || 'Claude')),
      h('h3', { className: 'text-title-2' }, p.title),
      p.reason && h('blockquote', { className: 'ot-plan-quote text-callout' }, h('span', { className: 'ot-plan-from' }, 'From ' + ai + ': '), p.reason),
      h('div', { className: 'ot-script-meta' },
        p.admin && h('span', { className: 'ot-step-badge text-mono-sm' }, h(Icon, { name: 'key', size: 12, strokeWidth: 2.25 }), 'Admin'),
        p.admin && h('span', { className: 'text-callout' }, 'Runs as administrator.'),
        h('span', { className: 'text-callout ot-muted ot-script-fixed' }, h(Icon, { name: 'alert', size: 16 }), 'Optimus can’t check what a script does and can’t undo it. No warning here doesn’t mean it is safe.'),
        p.policyNote && h('span', { className: 'text-callout ot-script-fixed' }, h(Icon, { name: 'lock', size: 16 }), 'Runs even though scripts are normally off on this PC.')),
      warnings.length > 0 && h('ul', { className: 'ot-script-warn', 'aria-label': 'Found by Optimus' }, warnings.map(function (w, i) {
        return h('li', { key: i, className: 'text-callout' }, h(Icon, { name: 'alert', size: 16 }), h('span', null, w));
      })),
      h(ScriptCode, { key: p.contentKey || 'code', ai: ai, script: p.script, parts: p.parts, onReadAll: function () { setReadAll(true); } }),
      st === 'interrupted' && h('div', { className: 'ot-plan-warn is-result', role: 'alert' }, h(Icon, { name: 'alert', size: 18 }), h('span', { className: 'text-callout' }, h('strong', null, 'Interrupted.'), ' Optimus doesn’t know how far it got.')),
      st === 'notstarted' && h('div', { className: 'ot-plan-warn is-neutral', role: 'alert' }, h(Icon, { name: 'lock', size: 18 }), h('span', { className: 'text-callout' }, h('strong', null, 'Not started.'), ' ', p.notStartedWhy || NOT_STARTED[p.notStartedCause] || 'Something changed after you approved it, so it didn’t run.', ' Nothing was changed.')),
      st === 'notrun' && h('div', { className: 'ot-plan-warn is-result', role: 'alert' }, h(Icon, { name: 'alert', size: 18 }), h('span', { className: 'text-callout' }, h('strong', null, 'Not run: no restore point was made.'))),
      st === 'awaiting' && h('div', { className: cx('ot-script-rp', !rpAvail && 'is-off') },
        h('label', { className: 'ot-plan-tick text-callout' },
          h('input', { type: 'checkbox', checked: rpAvail && rpOn, disabled: !rpAvail, onChange: function (e) { setRpOn(e.target.checked); } }),
          'Make a restore point first'),
        h('span', { className: 'text-mono-sm ot-muted' }, rpAvail ? 'Covers Windows files and settings, not your files.' : SCRIPT_RP_REASON[p.restorePoint] || SCRIPT_RP_REASON.off),
        h('label', { className: 'ot-plan-tick text-callout ot-script-share' },
          h('input', { type: 'checkbox', checked: shareOn, onChange: function (e) { setShareOn(e.target.checked); } }),
          'Let ' + ai + ' see the last lines of output'),
        h('span', { className: 'text-mono-sm ot-muted' }, 'Paths and names are removed where recognised.')),
      st === 'awaiting' && h('div', { className: 'ot-plan-warn', role: 'note' },
        h(Icon, { name: 'alert', size: 18 }),
        h('div', { className: 'ot-plan-warn-body' },
          h('span', { className: 'text-callout' }, h('strong', null, 'Can’t be undone')),
          h('label', { className: 'ot-plan-tick text-callout' },
            h('input', { type: 'checkbox', checked: ticked, onChange: function (e) { setTicked(e.target.checked); } }),
            'I understand this can’t be undone'))),
      ended && view === 'transcript' && h('div', { className: 'ot-script-out' },
        h('pre', { className: 'ot-script-tr text-mono-sm', tabIndex: 0, role: 'region', 'aria-label': 'Transcript' }, p.transcript || ''),
        p.transcriptCut && h('span', { className: 'text-mono-sm ot-muted' }, 'Output cut at 1 MB')),
      ended && view === 'sent' && p.sentExcerpt != null && h('div', { className: 'ot-script-out' },
        h('span', { className: 'text-mono-sm ot-muted' }, 'Exactly what was sent to ' + ai),
        h('pre', { className: 'ot-script-tr text-mono-sm', tabIndex: 0, role: 'region', 'aria-label': 'Sent to ' + ai }, p.sentExcerpt)),
      h('div', { className: 'ot-approval-foot', ref: footRef },
        st === 'awaiting' && h('div', { className: 'ot-approval-actions' },
          p.waiting ? h('span', { className: 'text-mono-sm ot-muted ot-plan-count', 'aria-live': 'polite' }, 'Approve in the Windows window that opened.')
            : startError(p) ? h('span', { className: 'text-callout ot-plan-fail ot-plan-count', role: 'alert' }, startError(p))
              : !readAll && h('span', { className: 'text-mono-sm ot-muted ot-plan-count' }, 'Read to the end to approve'),
          h(Button, { variant: 'ghost', disabled: p.waiting, onClick: p.onDecline }, 'Decline'),
          h(Button, { variant: 'primary', icon: p.waiting ? null : startError(p) ? 'repeat' : 'check', disabled: p.waiting || !(readAll && ticked), onClick: function () { if (p.onApprove) p.onApprove({ restorePoint: rpAvail && rpOn, shareOutput: shareOn }); } }, p.waiting ? 'Waiting…' : startError(p) ? 'Retry' : 'Approve')),
        st === 'running' && h('div', { className: 'ot-approval-actions' },
          h('span', { className: 'text-mono-sm ot-muted ot-plan-count' }, p.stopping ? 'Stopping…' : 'Stop ends the script. Changes it already made, and anything it started elsewhere, stay.'),
          h(Button, { variant: 'neu', icon: 'stop', disabled: p.stopping, onClick: p.onStop }, p.stopping ? 'Stopping…' : 'Stop')),
        (st === 'notstarted' || st === 'notrun') && p.onClose && h('div', { className: 'ot-approval-actions' }, h(Button, { variant: 'ghost', onClick: p.onClose }, 'Close')),
        ended && h('div', { className: 'ot-approval-actions' },
          h(Button, { variant: 'neu', icon: 'file', 'aria-expanded': view === 'transcript', onClick: function () { var v = view === 'transcript' ? null : 'transcript'; setView(v); if (v && p.onTranscript) p.onTranscript(); } }, 'Transcript'),
          p.sentExcerpt != null && h(Button, { variant: 'ghost', icon: 'eye', 'aria-expanded': view === 'sent', onClick: function () { setView(view === 'sent' ? null : 'sent'); } }, 'Sent to ' + ai),
          p.onClose && h(Button, { variant: 'ghost', onClick: p.onClose }, 'Close'))));
  }

  var PROVIDERS = [
    { value: 'claude', glyph: 'C', title: 'Claude', description: 'Your Claude plan', shares: 'Only what you allow' },
    { value: 'chatgpt', glyph: 'GPT', title: 'ChatGPT', description: 'Your ChatGPT plan', shares: 'Only what you allow', needsKind: 'chatgpt_plan' },
    { value: 'api-key', glyph: 'API', title: 'API key', description: 'Anthropic or OpenAI', shares: 'Only what you allow · billed to your key' },
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

  // Design PairingCard (v51): an outside Claude Code window asks to pair; its code must match. App addition: `busy`
  // (the answer is on its way) disables both buttons.
  function PairingCard(p) {
    var code = String(p.code || '').split('');
    return h(GlassPanel, { strong: true, className: cx('ot-pair', p.className), role: 'dialog', 'aria-labelledby': 'ot-pair-t' },
      h('div', { className: 'ot-perm-chead' }, h(Icon, { name: 'terminal', size: 18 }), h('span', { id: 'ot-pair-t', className: 'text-headline' }, 'Pair ' + (p.client || 'Claude Code') + '?')),
      h('p', { className: 'text-callout ot-muted' }, 'Check that this code matches the one in ' + (p.client || 'Claude Code') + '’s window.'),
      h('div', { className: 'ot-pair-code' },
        h('span', { className: 'text-caption ot-muted', id: 'ot-pair-l' }, 'Code'),
        h('span', { className: 'ot-pair-digits', role: 'text', 'aria-labelledby': 'ot-pair-l', 'aria-label': 'Code ' + code.join(' ') },
          code.map(function (c, i) { return h('span', { key: i, className: 'ot-pair-digit', 'aria-hidden': true }, c); }))),
      h('div', { className: 'ot-login-actions' },
        h(Button, { variant: 'ghost', disabled: !!p.busy, onClick: p.onCancel }, 'Not now'),
        h(Button, { variant: 'primary', disabled: !!p.busy, onClick: p.onPair }, 'Pair')));
  }

  // Picker tile → provider kind (the mapping and its tests live in connection-state.js, loaded after this file).
  function tileKind(tile, api) { return window.OptTool.tileKind(tile, api); }
  var READY_ROWS = { paired: 1, active: 1, client_closed: 1 };
  function ConnectedLine() {
    return h('div', { className: 'ot-connected-line text-callout' }, h(Icon, { name: 'check', size: 16, strokeWidth: 2.5 }), 'Connected');
  }

  // Design ConnectStep (v70): number (or check, alert or its own mark), its title, and its button (turning gears while
  // busy). A finished step shows only its check and title: no button, no state word (screen readers still hear it).
  function ConnectStep(p) {
    return h('li', { className: cx(p.done && 'is-done', p.warn && 'is-warn') },
      h('span', { className: 'ot-connect-num text-mono-sm' }, p.done ? h(Icon, { name: 'check', size: 14, strokeWidth: 2.5 }) : p.warn ? h(Icon, { name: 'alert', size: 14 }) : p.markIcon ? h(Icon, { name: p.markIcon, size: 14 }) : p.n),
      h('div', { className: 'ot-connect-title' }, h('span', { className: 'text-callout', style: { fontWeight: 600 } }, p.title),
        h('span', { className: 'ot-sr' }, p.done ? 'Done' : p.busy ? 'In progress' : p.warn ? 'Needs you' : 'To do')),
      p.action && !p.done ? h(Button, { variant: 'neu', size: 'sm', icon: p.busy ? null : p.icon, disabled: p.disabled, onClick: p.onClick },
        p.busy && h(Logo, { size: 14, mono: true, spin: true, decorative: true }), p.action) : h('span', null));
  }

  /* Design ConnectNote (v53): one grey line under the Claude steps for cases the three steps don't cover
     (claudeNote from connection-state.js). */
  var CLAUDE_NOTES = {
    installing: { text: 'Installing Claude Code. This takes about a minute.' },
    updating: { text: 'Updating Claude Code. This takes about a minute.' },
    signinfailed: { text: 'Sign-in didn’t open. Try again.' },
    repair: { text: 'Part of the Claude Code setup is broken. Repair installs it again to fix it.' },
    desktoponly: { text: 'You have the Claude app. Claude Code is separate, so install it here.' },
    installfailed: { text: 'Install didn’t finish. You can install it yourself.', action: 'command' },
    blocked: { text: 'This PC doesn’t allow installing programs. Ask whoever manages it.' },
    unknown: { text: 'Couldn’t check Claude Code. It may still be starting.', action: 'check' },
    signingin: { text: 'Finish signing in in your browser.' },
    apikey: { text: 'Claude Code is set to use an API key, so it’s billed to that key, not your Claude plan.' }
  };
  function ConnectNote(p) {
    var n = CLAUDE_NOTES[p.note]; var s = React.useState(false), show = s[0], setShow = s[1];
    if (!n) return null;
    return h('div', { className: 'ot-connect-note' },
      h('span', { className: 'text-callout ot-muted', role: 'status' }, n.text),
      n.action === 'check' && p.onCheckAgain && h(Button, { variant: 'ghost', size: 'sm', icon: 'repeat', onClick: p.onCheckAgain }, 'Check again'),
      n.action === 'command' && h(Button, { variant: 'ghost', size: 'sm', icon: 'terminal', 'aria-expanded': show, onClick: function () { setShow(!show); } }, show ? 'Hide command' : 'Show command'),
      show && h('code', { className: 'ot-connect-cmd text-mono-sm' }, p.command || ''));
  }

  // Where Claude setup stands: 1 install, 2 sign in, 3 connected (4: all done).
  function nativeProgress(cv, st) {
    var row = st && st.row;
    var ready = !!(row === 'connected' || (row && READY_ROWS[row]));
    if (ready) return { step: 4, ready: true };
    if (cv && cv.notInstalled) return { step: 1, ready: false };
    if (row === 'signed_out' || (cv && cv.installed)) return { step: 2, ready: false };
    return { step: 1, ready: false };
  }

  // Design CHATGPT_STATES (v72): the ChatGPT line's words per chatgptState (signedin is the check line).
  var CHATGPT_STATES = {
    new: { title: 'Sign in with ChatGPT', action: 'Sign in', note: 'Opens your browser.' },
    signingin: { title: 'Sign in with ChatGPT', action: 'Signing in…', busy: true, note: 'Finish signing in in your browser.' },
    failed: { title: 'Sign-in didn’t finish', action: 'Try again', warn: true, note: 'Opens your browser.' },
    signedout: { title: 'Signed out of ChatGPT', action: 'Sign in', warn: true, note: 'Sign in again to keep using it.' },
    limit: { title: 'ChatGPT plan limit reached', action: null, warn: true, note: 'Try again later, or choose another AI.' },
    notavailable: { title: 'Not available for this account', action: null, warn: true, note: 'Choose another AI.' }
  };

  // Design ProviderDetails (v53) with the app's live state. App additions: the connection hint line for ChatGPT and API
  // keys, and the API key's own Windows dialog (no key field on this page).
  function ProviderDetails(p) {
    var cur = p.provider;
    if (!cur || cur.value === 'local') return null;
    var st = p.tileState || null;
    // Disconnect is not in the picker (design v70): it is in the AI pill's panel, next to Permissions.
    // A failed check or sign-in says why in a line (connectionState hint).
    var why = st && st.hint && h('div', { className: 'text-callout ot-muted', role: 'status' }, st.hint);
    if (cur.value === 'chatgpt') {
      // ChatGPT signs in through the browser; nothing to install. One line whose words follow the state, and its note
      // (design v72 chatgptState, connection-state.js). The app's own reason text is not shown.
      var signing = !!((p.connect && p.connect.tile === 'chatgpt') || (st && st.row === 'checking'));
      var gs = window.OptTool.chatgptState(st, signing), signedIn = gs === 'signedin';
      var cg = CHATGPT_STATES[gs] || CHATGPT_STATES['new'];
      return h('div', { className: 'ot-detail' },
        h('ol', { className: 'ot-connect-steps is-one' },
          h(ConnectStep, { n: 1, markIcon: 'globe', warn: !signedIn && !!cg.warn, busy: cg.busy, title: signedIn ? 'Signed in with ChatGPT' : cg.title,
            action: signedIn ? null : cg.action, icon: 'external', done: signedIn, disabled: cg.busy, onClick: p.onSignIn })),
        !signedIn && h('span', { className: 'text-mono-sm ot-muted', role: 'status' }, cg.note));
    }
    if (cur.value === 'api-key') {
      // The key is typed into the app's own Windows dialog, never into this page.
      return h('div', { className: 'ot-detail' },
        h(SegmentedControl, { label: 'Provider', value: p.api, onChange: p.setApi, options: [
          { value: 'anthropic', label: 'Anthropic' }, { value: 'openai', label: 'OpenAI' }] }), st && st.row === 'connected' && h(ConnectedLine, null), why);
    }
    // Claude runs through the Claude Code program: install it, sign in, connected.
    var cv = p.client || null, g = nativeProgress(cv, st), note = cv ? cv.note || null : 'unknown';
    // Once the connection itself is ready, a status check without an answer changes nothing the user can do.
    if (g.ready && note === 'unknown') note = null;
    var update = !!(cv && cv.outdated), install = g.step > 1, signin = g.step > 2;
    var busy1 = note === 'installing' || note === 'updating', repair = note === 'repair';
    var signedOut = !!(st && st.row === 'signed_out') && g.step === 2;
    var connected = g.ready && !update && !repair && !busy1;   // = connection-state.js claudeConnected (the bottom button)
    // Optional: the connector in Claude Code's own window too (the pairing path).
    var also = connected && cv && cv.installed && (cv.registered === 'no' || cv.registered === 'outdated') &&
      h(Button, { variant: 'ghost', size: 'sm', icon: 'terminal', className: 'ot-wrap-btn', disabled: !!cv.adding, onClick: cv.onRegister }, 'Also use in Claude Code’s window');
    return h('div', { className: 'ot-detail' },
      h('ol', { className: 'ot-connect-steps' },
        h(ConnectStep, { n: 1, warn: update || repair, busy: busy1,
          title: update ? 'Update needed' : repair ? 'Needs repair' : install && !busy1 ? 'Installed' : 'Install Claude Code',
          action: note === 'installing' ? 'Installing…' : note === 'updating' ? 'Updating…' : update ? 'Update Claude Code' : repair ? 'Repair' : install ? 'Installed' : 'Install',
          icon: update || repair ? 'repeat' : 'download', done: install && !update && !repair && !busy1,
          disabled: !cv || note === 'blocked' || busy1, onClick: cv ? cv.onInstall : null }),
        h(ConnectStep, { n: 2, warn: signedOut || note === 'signinfailed', busy: note === 'signingin',
          title: update ? 'Sign in after updating' : repair ? 'Sign in after the repair' : signedOut ? 'Signed out' : signin ? 'Signed in' : 'Sign in',
          action: update || repair ? null : signin ? 'Signed in' : note === 'signingin' ? 'Signing in…' : 'Sign in', icon: 'external',
          disabled: !install || busy1 || repair || note === 'signingin' || !cv, done: signin && !update && !repair, onClick: cv ? cv.onSignIn : null }),
        h(ConnectStep, { n: 3, title: connected ? 'Connected' : 'Not connected yet', done: connected })),
      note && h(ConnectNote, { note: note, command: cv && cv.installCmd, onCheckAgain: cv ? cv.onCheck : p.onCheckAgain }),
      connected && also && h('div', { className: 'ot-detail-row' }, also));
  }

  function LoginPanel(p) {
    // initialProvider null: nothing selected (a saved AI that no longer exists).
    var s1 = React.useState(p.initialProvider === undefined ? 'claude' : p.initialProvider), prov = s1[0], setProv = s1[1];
    var s2 = React.useState(p.initialApi || 'anthropic');
    // Selecting Claude checks whether Claude Code is installed and signed in (shell.clientStatus); it is checked at start too.
    React.useEffect(function () { if (p.onTile && prov) p.onTile(prov); }, [prov]);
    React.useEffect(function () { if (p.onTile && prov !== 'claude') p.onTile('claude'); }, []);
    // A tile that needs a provider kind (ChatGPT) shows only when the backend offers it.
    var tiles = PROVIDERS.filter(function (x) { return !x.needsKind || (p.tileStates && p.tileStates[x.needsKind]); });
    var cur = tiles.filter(function (x) { return x.value === prov; })[0] || null;
    var views = p.clientViews || {}, cv = (prov && views[prov]) || null;
    var states = p.tileStates || {};
    var st = states[tileKind(prov, s2[0])] || null;
    var details = ProviderDetails({ provider: cur, api: s2[0], setApi: s2[1], connect: p.connect,
      tileState: st, client: cv, onCheckAgain: function () { if (p.onTile) p.onTile(prov); },
      onSignIn: function () { go('chatgpt'); } });
    var API_NAMES = { openai: 'OpenAI (API)', anthropic: 'Claude (API)' };
    var c = p.connect && p.connect.tile === prov ? p.connect : null;
    var g = nativeProgress(cv, st);
    // design v70: “Continue” when the chosen AI is already connected, else “Connect” (connection-state.js connectLabel).
    var primaryLabel = window.OptTool.connectLabel(prov, st, cv);
    var cgPrimary = window.OptTool.pickerPrimary(prov, st, !!(p.connect && p.connect.tile === 'chatgpt'));
    // Claude: the button goes on once connected; before that it does the next step (install, update, sign in, check again).
    function nativePrimary() {
      if (g.ready) return h(Button, { variant: 'primary', iconRight: 'chevron', onClick: function () { go(prov); } }, primaryLabel);
      if (!cv) return h(Button, { variant: 'primary', iconRight: 'chevron', onClick: function () { if (p.onTile) p.onTile(prov); } }, 'Connect');
      var s = g.step === 1 ? { disabled: cv.note === 'blocked' || !!cv.installing, onClick: cv.note === 'unknown' ? cv.onCheck : cv.onInstall }
        : { disabled: !!cv.signing, onClick: cv.note === 'unknown' ? cv.onCheck : cv.onSignIn };
      return h(Button, { variant: 'primary', iconRight: 'chevron', disabled: !!s.disabled, onClick: s.onClick }, 'Connect');
    }
    // Reports the choice with a display name for the connection pill ("Claude", "ChatGPT", "Claude (API)", "Local only").
    function go(choice) {
      if (!p.onConnect) return;
      var pick = PROVIDERS.filter(function (x) { return x.value === choice; })[0] || cur;
      p.onConnect(choice, choice === 'api-key' ? API_NAMES[s2[0]] : pick.title, s2[0]);
    }
    // One tab stop for the tiles; arrow keys, Home and End move and select, like native radio buttons.
    function onTilesKey(e) {
      var keys = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 }; if (!keys[e.key] && e.key !== 'Home' && e.key !== 'End') return;
      e.preventDefault();
      var vals = tiles.map(function (x) { return x.value; }), i = vals.indexOf(prov), n = vals.length;
      var j = e.key === 'Home' ? 0 : e.key === 'End' ? n - 1 : ((i < 0 ? (keys[e.key] > 0 ? -1 : 0) : i) + keys[e.key] + n) % n;
      setProv(vals[j]);
      var btn = e.currentTarget.querySelector('[data-value="' + vals[j] + '"]'); if (btn) btn.focus();
    }
    var tabStop = cur ? prov : tiles.length ? tiles[0].value : null;
    return h(GlassPanel, { strong: true, className: cx('ot-login-panel', 'ot-frost', p.className) },
      h('div', { className: 'ot-brand' },
        h(Logo, { size: 30 }),
        h('span', { className: 'text-callout' }, 'Optimus')),
      h('h1', { className: 'text-title-1' }, 'Choose your AI'),
      h('div', { className: 'ot-provider-grid', role: 'radiogroup', 'aria-label': 'AI help', onKeyDown: onTilesKey },
        // Each tile keeps its own line; the mono word under it says where it stands (design tileStatus, v53).
        tiles.map(function (x) {
          return h(ProviderOption, Object.assign({ key: x.value, selected: x.value === prov, onSelect: setProv, tabIndex: x.value === tabStop ? 0 : -1 }, x, { status: p.tileStatus && p.tileStatus[x.value] }));
        })),
      h(Expand, { contentKey: prov || 'none' }, details),
      cur && h('div', { className: 'ot-shares' },
        h(Icon, { name: 'eye', size: 18 }),
        h('div', null,
          h('div', { className: 'text-caption ot-muted' }, 'Shared'),
          h('div', { key: prov, className: 'text-callout ot-fade' }, cur.shares))),
      h('div', { className: 'ot-login-actions' },
        p.onBack && h(Button, { variant: 'ghost', icon: 'back', className: 'ot-login-back', onClick: p.onBack }, 'Back'),
        h(Button, { variant: 'ghost', onClick: function () { if (p.onLater) p.onLater(); else go('local'); } }, 'Later'),
        !cur ? h(Button, { variant: 'primary', iconRight: 'chevron', disabled: true }, 'Connect')
          : prov === 'claude' ? nativePrimary()
          // design v73: ChatGPT signing in: “Waiting for browser…” (disabled, turning gears); plan limit or not
          // available: no primary button (Later and Back stay). connection-state.js pickerPrimary.
          : cgPrimary === 'waiting' ? h(Button, { variant: 'primary', disabled: true }, h(Logo, { size: 14, mono: true, spin: true, decorative: true }), 'Waiting for browser…')
          : cgPrimary === 'none' ? null
          // The API key: Connecting… while its dialog is open, Checking… while the key is checked.
          : c && c.phase === 'key' ? h(Button, { variant: 'primary', disabled: true }, 'Connecting…')
          : st && st.row === 'checking' ? h(Button, { variant: 'primary', disabled: true }, 'Checking…')
          : h(Button, { variant: 'primary', iconRight: 'chevron', onClick: function () { go(prov); } }, primaryLabel)));
  }

  function LoginScreen(p) {
    return h('div', { className: cx('ot-login', p.className) },
      h(WaveBackground, null),
      h('div', { className: 'ot-login-scroll' }, h(LoginPanel, { initialProvider: p.initialProvider, claudeState: p.claudeState, claudeNote: p.claudeNote, installCommand: p.installCommand, onCheckAgain: p.onCheckAgain, onAlsoWindow: p.onAlsoWindow, tileStatus: p.tileStatus, onConnect: p.onConnect })));
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
  /* ---------- Fix for me (design v75–v80: FixForMe, FixStates, PlanInCard, AdminRights) ---------- */
  /* The button on an open card. availability: 'yes' | 'busy' (another fix is being worked on) | 'nochanges' (changes are off).
     No AI or explain-only: not rendered (fixAvailable unset). Returns the button and its line, for the card's action row. */
  function FixButton(p) {
    var s = React.useState(false), why = s[0], setWhy = s[1];
    var ai = p.ai || 'Claude';
    var btn = h(Button, { key: 'b', variant: p.primary ? 'primary' : 'neu', size: 'sm', icon: 'spark', disabled: p.availability === 'busy',
      onClick: function () { if (p.availability === 'nochanges') setWhy(!why); else if (p.onFix) p.onFix(); } }, 'Fix for me');
    return [btn,
      p.availability === 'busy' && h('span', { key: 'n', className: 'text-mono-sm ot-muted ot-fix-note' }, ai + ' is working on another fix.'),
      p.availability === 'nochanges' && why && h('span', { key: 'w', className: 'ot-fix-note ot-fix-why text-callout', role: 'status' }, ai + ' can’t suggest changes yet.',
        h('button', { type: 'button', className: 'ot-perm-change', onClick: p.onPermissions }, 'Permissions'))];
  }

  /* design v84 (FixMoreStates, script pointer states): ported as it is; the turning gears are Logo spin. App addition (review 5): working's Stop is
     off until the fix's task is known (no onStop). */
  /* The AI asks to read things that need administrator rights. Used in a card and beside the chat. */
  /* States: ask | wait (a no was given under 60 s ago; secondsLeft) | running | done | declined | expired (no answer in 10 minutes)
     | unavailable (not in this version) | failed (cause: failed | timeout) | cancelled (cause: permissions | ai | stopped | ended | limit). */
  var CHECKS_CANCELLED = {
    permissions: 'Checks cancelled because permissions changed.',
    ai: 'Checks cancelled because you changed the AI.',
    stopped: 'Checks cancelled because you stopped {ai}.',
    ended: 'Checks cancelled because the conversation ended.',
    limit: 'Checks cancelled because {ai} reached its step limit.'
  };
  // One line per way the checks can end; also used by FixPanel 'working' (checksResult) while the AI carries on.
  function checksEndLine(st, cause, ai) {
    if (st === 'done') return 'Checks done. ' + ai + ' is reading the results.';
    if (st === 'partial') return 'Some checks couldn’t be read. ' + ai + ' uses the rest.';
    if (st === 'noneRead') return 'None of the checks could be read. ' + ai + ' goes on without them.';
    if (st === 'declined') return 'Checks skipped. ' + ai + ' goes on without them.';
    if (st === 'expired') return 'Nobody answered, so the checks were skipped. ' + ai + ' goes on without them.';
    if (st === 'unavailable') return 'Administrator checks aren’t available in this version yet. ' + ai + ' goes on without them.';
    if (st === 'failed' || st === 'timeout') return (st === 'timeout' || cause === 'timeout' ? 'The checks took too long and were stopped. ' : 'The checks couldn’t run. ') + ai + ' goes on without them.';
    if (st === 'cancelled') return (CHECKS_CANCELLED[cause] || CHECKS_CANCELLED.permissions).replace('{ai}', ai);
    return null;
  }
  function AdminChecks(p) {
    var st = p.state || 'ask', ai = p.ai || 'Claude', checks = (p.checks || []).slice(0, 4);
    var asking = st === 'ask' || st === 'wait';
    // done with unread (indexes into checks): each check is marked read or couldn't be read.
    var unread = st === 'done' ? (p.unread || []).filter(function (i) { return i >= 0 && i < checks.length; }) : [];
    var noneRead = unread.length > 0 && unread.length >= checks.length, someUnread = unread.length > 0 && !noneRead;
    var head = asking ? ai + ' wants to read these. They need administrator rights.'
      : st === 'running' ? 'Running checks…'
        : noneRead ? checksEndLine('noneRead', null, ai)
          : someUnread ? checksEndLine('partial', null, ai)
            : st === 'done' ? 'Checks done. ' + ai + ' can use the results.'
              : checksEndLine(st, p.cause, ai);
    var icon = noneRead ? 'alert' : someUnread ? 'report' : st === 'done' ? 'check' : st === 'failed' ? 'alert' : st === 'expired' ? 'clock' : st === 'unavailable' ? 'lock' : st === 'cancelled' || st === 'declined' ? 'close' : 'key';
    var secs = Math.max(1, Math.ceil(p.secondsLeft || 60));
    return h('div', { className: cx('ot-adminchecks', 'is-' + st, p.className), role: asking ? 'group' : 'status', 'aria-label': 'Administrator checks' },
      h('div', { className: 'ot-adminchecks-head text-callout' }, h(Icon, { name: icon, size: 16 }), head),
      (asking || st === 'running' || st === 'done') && h('ul', { className: 'ot-adminchecks-list' }, checks.map(function (c, i) {
        var miss = unread.indexOf(i) >= 0;
        return h('li', { key: i, className: cx('text-callout', miss && 'is-unread') }, st === 'running' ? h(Logo, { size: 14, mono: true, spin: true, decorative: true }) : h(Icon, { name: miss ? 'minus' : st === 'done' ? 'check' : 'eye', size: 14 }),
          h('span', null, c, miss && h('span', { className: 'ot-muted' }, ' · couldn’t be read')));
      })),
      asking && h('p', { className: 'text-mono-sm ot-muted' }, st === 'wait' ? 'You said no a moment ago. You can run these in ' + secs + (secs === 1 ? ' second.' : ' seconds.') : 'Nothing is changed. Windows asks first.'),
      asking && h('div', { className: 'ot-approval-actions' },
        h(Button, { variant: 'ghost', size: 'sm', onClick: p.onDecline }, 'Not now'),
        h(Button, { variant: 'primary', size: 'sm', icon: 'key', disabled: st === 'wait', onClick: p.onRun }, 'Run checks')));
  }

  // The AI's own opinion ranks below what Optimus knows: shown with the card's buttons, never instead of them.
  var FIX_OPINION = { needsYou: true, nothing: true };
  // Why a plan can't be done (FixPanel blocked, cause). Only adminOff gets the Permissions button.
  var BLOCKED_WHY = {
    adminOff: 'Everything in it needs administrator rights, which are off.',
    adminUnavailable: 'It needs administrator rights, which aren’t available in this version yet.',
    changed: 'The setting changed since {ai} looked.',
    managed: 'Windows or your organisation manages this setting.',
    protected: 'It would change a protected part of Windows.',
    conflict: 'Two of its steps change the same thing.',
    tooLarge: 'It has too many steps.',
    unsupported: 'This PC doesn’t support it.',
    other: 'Optimus can’t run it.'
  };

  /* Inside the card, after Fix for me. The host decides the state; the claim and observation stay above. */
  function FixPanel(p) {
    var st = p.state, ai = p.ai || 'Claude';
    function line(icon, strong, rest, tone) {
      return h('div', { key: 'l', className: cx('ot-fix-line', tone && 'is-' + tone), role: 'status' }, h(Icon, { name: icon, size: 16 }),
        h('span', { className: 'text-callout' }, h('strong', null, strong), rest ? ' ' + rest : ''));
    }
    var body;
    if (st === 'working') body = [
      h('div', { key: 'l', className: 'ot-fix-line', role: 'status' }, h(Logo, { size: 16, mono: true, spin: true, decorative: true }), h('span', { className: 'text-callout' }, ai + ' is looking into it…')),
      (p.checksResult || p.declined) && h('p', { key: 'd', className: 'text-mono-sm ot-muted', role: 'status' }, checksEndLine(p.checksResult || 'declined', p.checksCause, ai)),
      h('div', { key: 'a', className: 'ot-approval-actions' }, h(Button, { variant: 'neu', size: 'sm', icon: 'stop', disabled: !p.onStop, onClick: p.onStop }, 'Stop'))];
    else if (st === 'script') body = [
      // The script itself waits beside the chat (it needs the room); the card only points to it. Design v84: scriptState.
      p.scriptState === 'running' ? h('div', { key: 'l', className: 'ot-fix-line', role: 'status' }, h(Logo, { size: 16, mono: true, spin: true, decorative: true }), h('span', { className: 'text-callout' }, h('strong', null, ai + '’s script is running.'), ' Follow it beside the chat.'))
        : p.scriptState === 'ended' ? line('terminal', ai + '’s script has ended.', 'See what happened beside the chat.')
          : line('terminal', ai + ' wrote a script for this.', 'Read it beside the chat.'),
      p.onShowScript && h('div', { key: 'a', className: 'ot-approval-actions' }, h(Button, { variant: 'neu', size: 'sm', icon: 'terminal', onClick: p.onShowScript }, 'Show script'))];
    else if (st === 'checks' || st === 'checksRunning') body = h(AdminChecks, { state: st === 'checksRunning' ? 'running' : p.checksState || 'ask', cause: p.checksCause, secondsLeft: p.secondsLeft, unread: p.unread, ai: ai, checks: p.checks, onRun: p.onRunChecks, onDecline: p.onDeclineChecks });
    else if (st === 'finished') body = h(ApprovalCard, Object.assign({ inCard: true, from: ai, requestedBy: null, className: 'is-incard' }, p.plan, { state: 'reported' }));
    else if (st === 'plan') body = h(ApprovalCard, Object.assign({ inCard: true, stepToggles: true, from: ai, requestedBy: null, className: 'is-incard' }, p.plan));
    else if (st === 'blocked') body = [
      line('lock', ai + '’s plan can’t be done.', p.reason || (BLOCKED_WHY[p.cause] || BLOCKED_WHY.other).replace('{ai}', ai)),
      h('div', { key: 'a', className: 'ot-approval-actions' },
        p.onPermissions && (!p.cause || p.cause === 'adminOff') && h(Button, { variant: 'ghost', size: 'sm', icon: 'sliders', onClick: p.onPermissions }, 'Permissions'),
        h(Button, { variant: 'neu', size: 'sm', icon: 'spark', onClick: p.onRetry }, 'Fix for me'))];
    else if (st === 'needsYou') body = [
      line('sliders', 'You need to do this one yourself.', null),
      p.note && h('p', { key: 'n', className: 'text-callout ot-fix-note-ai' }, p.note),
      // Report cards can be checked again; AI-added and check-up cards only close.
      p.recheck && h('p', { key: 'r', className: cx('ot-fix-line text-callout', p.recheck === 'gone' && 'is-ok'), role: 'status' },
        h(Icon, { name: p.recheck === 'gone' ? 'check' : 'repeat', size: 16 }), p.recheck === 'gone' ? 'Checked again: the problem is gone.' : 'Checked again: the problem is still there.'),
      h('div', { key: 'a', className: 'ot-approval-actions' }, p.onCheckAgain ? h(Button, { variant: 'neu', size: 'sm', icon: 'repeat', disabled: p.checking, onClick: p.onCheckAgain }, p.checking ? 'Checking…' : 'Check again')
        : h(Button, { variant: 'ghost', size: 'sm', onClick: p.onClose }, 'Close'))];
    else if (st === 'nothing') body = [
      line('check', ai + ' thinks this doesn’t need fixing.', null, 'ok'),
      p.note && h('p', { key: 'n', className: 'text-callout ot-fix-note-ai' }, p.note),
      h('p', { key: 's', className: 'text-mono-sm ot-muted' }, 'Optimus keeps this card open until a scan shows it’s fine.'),
      h('div', { key: 'a', className: 'ot-approval-actions' }, h(Button, { variant: 'ghost', size: 'sm', onClick: p.onClose }, 'Close'))];
    else if (st === 'noFix') body = [
      line('gap', ai + ' didn’t find a fix.', null),
      h('div', { key: 'a', className: 'ot-approval-actions' }, h(Button, { variant: 'neu', size: 'sm', icon: 'spark', onClick: p.onRetry }, 'Try again'))];
    else body = h(ApprovalCard, Object.assign({ inCard: true, from: ai, requestedBy: null, className: 'is-incard' }, p.plan, { state: st }));
    return h('div', { className: cx('ot-fixpanel', 'is-' + st) }, body);
  }


  var DISPOSE = { Intended: 1, Ignore: 1, Hide: 1 };
  var FIX_MODES = { preview: 1, running: 1, verified: 1, failed: 1, ineligible: 1 };
  // A click on the card itself (not on one of its controls) makes it the main card.
  var CONTROLS = 'button, a, input, textarea, select, label, [role="button"], [role="group"]';
  function InsightCard(p) {
    var key = CATS[p.category] ? p.category : 'misc', c = CATS[key];
    var w = React.useState(false), why = w[0], setWhy = w[1];
    var ig = React.useState(false), ignoring = ig[0], setIgnoring = ig[1];
    var mode = p.mode || 'idle';
    var ro = React.useState(!!(p.research && p.research.open)), rOpen = ro[0], setROpen = ro[1];
    var rid = 'ot-research-' + (p.cardKey || key) + (p.index || '');
    // Every change in a card (a single change or a fix) is the approval card with its steps (design v53).
    var change = p.change && FIX_MODES[mode] ? p.change : null;
    // design v75: Fix for me's panel replaces the action rows; the AI's opinion (needsYou, nothing) sits above them.
    var fixShown = !!(p.fix && p.fix.state), rowsShown = mode === 'idle' && (!fixShown || !!FIX_OPINION[p.fix.state]);
    // A guided finding's steps are the guided approval card (design v59); the card has no steps mode of its own.
    var opts = p.options || [];
    // Fix for me is first and primary unless the card's first option is its own action (Preview change, View steps, …).
    var hasFix = !!p.fixAvailable && rowsShown, fixFirst = hasFix && (!opts.length || opts[0] === 'Ask about this' || !!DISPOSE[opts[0]]);
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
    if (p.checkup) return h(CheckupCard, p);
    return h('section', { className: cx('ot-insight', 'cat-' + key, p.leaving && 'is-leaving', p.onFocus && 'is-focusable', p.className), 'aria-label': c.label,
      'data-card': p.cardKey || undefined, onClick: onCard },
      h('div', { className: 'ot-insight-head' },
        h('span', { className: 'ot-insight-tags' },
          h('span', { className: 'ot-insight-cat text-caption' }, h(Icon, { name: c.icon, size: 14, strokeWidth: 2 }), c.label, p.aiTag && h('span', { className: 'ot-tag is-ai' }, 'AI')),
          // M9 §6.2: the research label beside the category; it opens the explanation under the claim.
          p.research && h(ResearchTag, { verdict: p.research.verdict, open: rOpen, controls: rid, onClick: p.research.verdict === 'running' ? null : function () { setROpen(!rOpen); } }),
          p.isNew && h('span', { className: 'ot-new-tag text-mono-sm' }, 'New')),
        h('span', { className: 'ot-insight-meta' },
          p.total ? h('span', { className: 'text-mono-sm ot-muted' }, p.index + ' / ' + p.total) : null,
          // The open card folds only by this, Escape, or opening another card (a click elsewhere leaves it open).
          p.onCollapse && h(Button, { variant: 'ghost', size: 'sm', icon: 'collapse', className: 'ot-insight-fold', 'aria-label': 'Collapse', title: 'Collapse', onClick: p.onCollapse }))),
      h('p', { className: 'text-headline ot-insight-claim' }, p.text),
      p.research && p.research.verdict !== 'running' && h(Expand, { contentKey: rOpen ? 'research' : 'none' }, rOpen ? h(ResearchNote, Object.assign({ id: rid }, p.research)) : null),
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
      mode === 'loading' && h('div', { className: 'text-callout ot-muted' }, 'Preparing…'),
      // M9 §3.6: the finding cites several entries or processes; the user picks the one to preview.
      // Design PickChoice: the choices' labels are unique (targetChoices numbers repeats), so a label names its evidence.
      mode === 'pick' && p.pick && h(PickChoice, { question: p.pick.question, items: p.pick.choices.map(function (c) { return c.label; }),
        onPick: function (label) { var c = p.pick.choices.filter(function (x) { return x.label === label; })[0]; if (c) p.pick.onPick(c.evidenceId); }, onBack: p.pick.onBack }),
      change && h(ApprovalCard, Object.assign({ className: 'ot-insight-fix' }, change)),
      fixShown && h(FixPanel, Object.assign({ ai: p.ai }, p.fix)),
      // Things to do on one row; deciding about the card (Intended, Ignore, Later) on a quieter row of its own.
      // "Ask about this" is the field below, so it isn't repeated as a button.
      rowsShown && h('div', { className: 'ot-insight-actions' }, (function () {
        var btns = opts.filter(function (o) { return !DISPOSE[o] && o !== 'Ask about this'; }).map(function (o, i) {
          return h(Button, { key: o, variant: i === 0 && !fixFirst ? 'primary' : 'neu', size: 'sm', icon: o === 'Open settings' ? 'link' : undefined, onClick: function () { option(o); } }, o);
        });
        if (hasFix) btns.splice(fixFirst ? 0 : 1, 0, h(FixButton, { key: 'fix', availability: p.fixAvailable, ai: p.ai, primary: fixFirst, onFix: p.onFix, onPermissions: p.onPermissions }));
        return btns;
      })()),
      rowsShown && h('div', { className: 'ot-insight-quiet' }, opts.filter(function (o) { return DISPOSE[o]; }).map(function (o) {
        return h(Button, { key: o, variant: 'ghost', size: 'sm', 'aria-expanded': o === 'Ignore' && p.ignoreChoices ? ignoring : undefined, onClick: function () { option(o); } }, o);
      }), p.onLater && h(Button, { key: 'later', variant: 'ghost', size: 'sm', onClick: p.onLater }, 'Later')),
      rowsShown && p.ignoreChoices && h(Expand, { contentKey: ignoring ? 'ignore' : 'none' }, ignoring ? h('div', { className: 'ot-ignore-choice', role: 'group', 'aria-label': 'Ignore' },
        p.ignoreChoices.map(function (c) {
          return h('button', { key: c.id, type: 'button', className: 'ot-suggest-chip is-small', title: c.title || undefined, onClick: function () { setIgnoring(false); if (p.onIgnore) p.onIgnore(c.id); } }, c.label);
        })) : null),
      cardReply(p),
      h(AskField, { placeholder: 'Ask about this…', onSend: p.onAsk, focusToken: p.askToken, restore: p.askRestore, onFocusIn: p.onAskFocus, onFocusOut: p.onAskBlur }));
  }
  // A card's AI answer (both card variants): the voltage note, the reply or "Thinking…", its citations and sources.
  function cardReply(p) {
    return [
      // M6a §8: an AI reply with warnings ["voltage"] gets the risk block above it, once.
      p.reply && p.replyRisk && h(RiskNote, { key: 'risk', className: 'ot-reply-risk' }),
      (p.reply || p.replyBusy) && h('div', { key: 'reply', className: cx('ot-reply text-callout', !p.reply && 'is-busy') },
        p.reply ? h(Icon, { name: 'spark', size: 16 }) : h(Logo, { size: 16, spin: true, decorative: true, className: 'ot-busy-logo' }),
        h('span', null, p.reply ? h(RichText, { text: p.reply }) : h('span', { className: 'ot-muted' }, p.replyBusy),
          (p.replyCites || []).map(function (c) { return h('button', { key: c.id, type: 'button', className: 'ot-cite text-mono-sm', title: c.id, onClick: function () { if (p.onCite) p.onCite(c.id); } }, c.label); }),
          p.reply && p.onReplyConnect && h(Button, { variant: 'ghost', size: 'sm', icon: 'spark', className: 'ot-reply-connect', onClick: p.onReplyConnect }, 'Connect AI'))),
      p.reply && h(SourceList, { key: 'src', sources: p.replySources, max: 8, onOpenSource: p.onOpenSource }),
      p.reply && p.onReplyBenchmark && h('div', { key: 'bench', className: 'ot-answer-acts' }, h(Button, { variant: 'neu', size: 'sm', icon: 'gauge', onClick: p.onReplyBenchmark }, 'Run benchmark'))
    ];
  }

  /* AI check-up (design v48 CheckupCard; contracts/m9-ai-powers.md §6.8): a normal issue card for a problem the AI found
     reported online for something on this PC. Nothing on it changes the PC: Ask about this and Hide, plus the ask
     field; no preview or approve. The deck's own parts are kept: the card key, a click to open it, Collapse, and the
     AI answer as on other cards. */
  var CHECKUP_KINDS = ['Bug', 'Security', 'End of life', 'Conflict', 'Update'];
  function CheckupCard(p) {
    var c = p.checkup || {}, key = CATS[p.category] ? p.category : 'misc', cat = CATS[key];
    var opts = p.options || ['Ask about this', 'Hide'];
    function onCard(e) {
      if (!p.onFocus || (e.target && e.target.closest && e.target.closest(CONTROLS))) return;
      p.onFocus();
    }
    return h('section', { className: cx('ot-insight', 'ot-checkup', 'cat-' + key, p.leaving && 'is-leaving', p.onFocus && 'is-focusable', p.className), 'aria-label': 'AI check-up',
      'data-card': p.cardKey || undefined, onClick: onCard },
      h('div', { className: 'ot-insight-head' },
        h('span', { className: 'ot-insight-tags' },
          h('span', { className: 'ot-insight-cat text-caption' }, h(Icon, { name: cat.icon, size: 14, strokeWidth: 2 }), cat.label),
          h(ResearchTag, { verdict: 'checkup' }),
          // design CheckupCard isNew (v54): the small accent tag until the card has been on screen.
          p.isNew && h('span', { className: 'ot-new-tag text-mono-sm' }, 'New')),
        h('span', { className: 'ot-insight-meta' },
          p.total ? h('span', { className: 'text-mono-sm ot-muted' }, p.index + ' / ' + p.total) : null,
          p.onCollapse && h(Button, { variant: 'ghost', size: 'sm', icon: 'collapse', className: 'ot-insight-fold', 'aria-label': 'Collapse', title: 'Collapse', onClick: p.onCollapse }))),
      h('div', { className: 'ot-checkup-title' },
        c.kind && CHECKUP_KINDS.indexOf(c.kind) >= 0 && h('span', { className: 'ot-checkup-kind text-mono-sm' }, c.kind),
        h('p', { className: 'text-headline ot-insight-claim' }, c.title)),
      c.text && h('p', { className: 'text-callout ot-insight-obs' }, c.text),
      h(SourceList, { sources: c.sources, onOpenSource: p.onOpenSource }),
      h('p', { className: 'text-mono-sm ot-muted ot-checkup-fixed' }, 'Nothing was changed.'),
      p.fix && p.fix.state && h(FixPanel, Object.assign({ ai: p.ai }, p.fix)),
      (!(p.fix && p.fix.state) || FIX_OPINION[p.fix.state]) && h('div', { className: 'ot-insight-actions' },
        p.fixAvailable && h(FixButton, { key: 'fix', availability: p.fixAvailable, ai: p.ai, primary: true, onFix: p.onFix, onPermissions: p.onPermissions }), opts.map(function (o, i) {
          return h(Button, { key: o, variant: i === 0 && !p.fixAvailable ? 'neu' : 'ghost', size: 'sm', onClick: function () { if (p.onOption) p.onOption(o); } }, o);
        })),
      cardReply(p),
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

  var ACT_STATE = { running: 'running', complete: 'done', error: 'declined', denied: 'declined' };
  var ACT_WORD = { running: '…', complete: 'Done', error: 'Failed', denied: 'Not allowed' };
  // Optimizer tool calls only: plain words, origin, size shared, state; the task's budget; Stop.
  // A running row's state: "12 s left" for a sample, else "Running… 3 s" (the title keeps the design's words).
  function runningText(it, now) {
    var secs = Math.max(0, Math.floor((now - (it.startedAt || now)) / 1000));
    if (it.window) return { title: it.title, state: Math.max(0, it.window - secs) + ' s left' };
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
    var close = p.onClose && h(Button, { variant: 'ghost', size: 'sm', icon: 'close', 'aria-label': 'Close', onClick: p.onClose });
    // design v72: a finished run: the answer first (none for a typed question, whose answer panel carries it: the question
    // is the headline), the × at the right of the AI's name, the steps folded into “Steps <AI> took · N”.
    if (p.finished) {
      return h(FinishedRun, { className: cx('is-compact', p.className), role: 'region', ariaLabel: 'Activity', agent: p.agent, goal: p.goal,
        answer: p.answer, count: items.length, onClose: p.onClose, list: function () { return h('div', { className: 'ot-activity is-embedded' }, list()); } });
    }
    return h(GlassPanel, { className: cx('ot-activity is-compact', p.className), role: 'region', 'aria-label': 'Activity' },
      h('div', { className: 'ot-activity-head' },
        h('div', { className: cx('text-caption', p.active ? 'ot-live' : 'ot-muted') }, p.active && h('span', { className: 'ot-live-dot', 'aria-hidden': true }), p.title),
        h('div', { className: 'ot-grant-actions' },
          p.active && p.onStop && h(Button, { variant: 'neu', size: 'sm', icon: 'stop', onClick: p.onStop }, 'Stop'),
          close)),
      list(),
      h('div', { className: 'ot-meters' },
        meter('Checks', used.diagnosticCalls || 0, lim.diagnosticCalls),
        meter('Shared', used.bytesShared || 0, lim.totalBytes, function (b) { return Math.round(b / 1024) + ' KiB'; }),
        // Claude Code runs its own model turns, so only the in-app assistant has a turn budget to show.
        p.nativeClient ? null : meter('Turns', used.turns || 0, lim.turns)));
    function list() {
      return items.length ? h('ol', { className: 'ot-steps' }, items.map(function (it, i) {
        var st = ACT_STATE[it.status] || 'done', live = it.status === 'running' ? runningText(it, now) : null;
        // Newest first; the latest row flashes once when it appears or changes state (the key includes its state).
        return h('li', { key: it.id + ':' + it.status, className: cx('ot-step', 'is-' + st, i === 0 && 'is-fresh', p.highlight && it.evidenceIds && it.evidenceIds.indexOf(p.highlight) >= 0 && 'is-hit') },
          h('span', { className: 'ot-step-mark', 'aria-hidden': true }, st === 'done' && h(Icon, { name: 'check', size: 14, strokeWidth: 2.25 }), st === 'declined' && h(Icon, { name: 'gap', size: 14 })),
          // Design AgentActivity row (v53): where it ran, the tool id (and how much it read), the title, its detail, one small action.
          h('div', { className: 'ot-step-body' },
            h('div', { className: 'ot-step-top' },
              h('span', { className: cx('ot-origin text-mono-sm', it.origin !== 'in_app' && 'is-client') }, it.originLabel),
              it.tool && h('span', { className: 'text-mono-sm ot-muted' }, it.tool),
              it.size && h('span', { className: 'text-mono-sm ot-muted' }, it.size)),
            h('div', { className: 'text-callout ot-step-title' }, live ? live.title : it.title),
            it.detail && h('div', { className: 'text-mono-sm ot-muted' }, it.detail),
            // M9 §5.4: a script run's row opens its transcript.
            it.action && h(Button, { variant: 'ghost', size: 'sm', icon: it.action.icon || 'file', className: 'ot-step-action', onClick: it.action.onClick }, it.action.label)),
          h('span', { className: 'ot-step-state text-mono-sm' }, live ? live.state : ACT_WORD[it.status] || it.status));
      })) : h('div', { className: 'text-callout ot-muted' }, 'No checks yet');
    }
  }
  // A finished run's answer for the design's finished view: paragraphs split by a blank line (light Markdown kept), the
  // citation chips after the last one.
  function runAnswer(text, cites, onCite) {
    var paras = String(text || '').split(/\n\s*\n+/).filter(function (t) { return t.trim(); });
    return paras.map(function (t, i) {
      return h('p', { key: i, className: 'text-body ot-run-para' }, h(RichText, { text: t.trim() }),
        i === paras.length - 1 && cites && cites.length ? h(React.Fragment, null, ' ', h(CiteChips, { cites: cites, onCite: onCite })) : null);
    });
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
      // M9 §7: the sources this turn's web lookups used (agent.message.sources, up to 8), opened by id.
      a.text && h(SourceList, { sources: a.sources, max: 8, onOpenSource: p.onOpenSource }),
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
  // design v70: once answered, the run is shown finished (the answer first, the steps folded); a failed run keeps the
  // live list and the failure line.
  function AiScanPanel(p) {
    var a = p.answer, answered = !!(a && a.text && !a.failed);
    return h('div', { className: cx('ot-aiscan', p.className) },
      h(ActivityPanel, { title: p.title || 'AI', active: p.active && !p.done, items: p.items, nativeClient: true,
        finished: answered, agent: p.title || 'AI', answer: answered ? runAnswer(a.text, a.cites, p.onCite) : null }),
      a && !answered && a.failed && h(AgentAnswers, { items: [Object.assign({}, a, { q: null })], onCite: p.onCite }),
      h('div', { className: 'ot-choice-foot' },
        !p.done && h(Button, { variant: 'ghost', onClick: p.onSkip }, 'Skip'),
        h(Button, { variant: 'primary', iconRight: 'chevron', disabled: !p.done, onClick: p.onContinue }, 'Continue')));
  }

  /* Design AIPillPanel (v73, PillPanelStates): one status per state. Head: the dot, the AI's name and a status word;
     then an optional grey hint; for pairing the code with its “Code” label; for manual the command with Copy and a grey
     line after it; the actions: Change and Permissions (secondary), the state's extra, then its one primary at the right;
     Disconnect as a small text button on its own row, which asks first, in place: “Sign <AI> out of Optimus? …”
     [Cancel] [Disconnect]. Hidden for Local only and while something runs (`busy` or live). `onAction(name)` gets the
     button's name (Local only's Connect an AI calls onChange, as in the design).
     App additions (no designed place): `hint`, `primary` and `tone` override the state's (undefined keeps the design's,
     null drops it) for a connection the design has no state for (connection-state.js pillPanelState own); Change and
     Permissions show only with their handlers; `onCopy` may return a promise (Copied once it resolves); `onDisconnect`
     may return a promise: Disconnect is sent once and waits for it (it comes back if the disconnect fails). */
  var PILL_STATES = {
    connected: { tone: 'ok', word: 'Connected' },
    live: { tone: 'live', word: 'Working…', primary: 'Activity', extra: 'Stop', noDisconnect: true, noChange: true },
    signedout: { tone: 'warn', word: 'Signed out', primary: 'Sign in' },
    setup: { tone: 'warn', word: 'Not set up', hint: 'Claude Code isn’t installed or can’t start on this PC.', primary: 'Set up' },
    nokey: { tone: 'warn', word: 'Needs a key', primary: 'Add key' },
    badkey: { tone: 'warn', word: 'Key rejected', hint: 'The key was turned down. Check it and add it again.', primary: 'Update key' },
    offline: { tone: 'warn', word: 'Can’t reach it', hint: 'Check your internet connection.', primary: 'Retry' },
    busy: { tone: 'warn', word: 'Busy right now', hint: 'Try again in a minute.', primary: 'Retry' },
    pairing: { tone: 'live', word: 'Wants to pair', hint: 'Check this code matches the one in Claude Code’s window.', primary: 'Approve', extra: 'Deny', code: true },
    paired: { tone: 'ok', word: 'Paired', hint: 'Claude Code can now work in its own window.', primary: 'Start' },
    manual: { tone: 'warn', word: 'Not added yet', hint: 'Run this in a terminal to add Optimus to Claude Code:', command: true, after: 'Then run claude mcp list to check.', primary: 'Check again' },
    local: { tone: 'muted', word: 'No AI connected', primary: 'Connect an AI', noPermissions: true, noDisconnect: true, noChange: true }
  };
  function AIPillPanel(p) {
    var c = React.useState(!!p.confirming), confirm = c[0], setConfirm = c[1];
    var b = React.useState(false), sending = b[0], setSending = b[1];
    var cp = React.useState(false), copied = cp[0], setCopied = cp[1];
    var sent = React.useRef(false), moved = React.useRef(false), confirmRef = React.useRef(null), offRef = React.useRef(null);
    var st = PILL_STATES[p.state] || PILL_STATES.connected;
    var who = p.state === 'local' ? 'Local only' : (p.provider || 'Claude');
    var hint = p.hint === undefined ? st.hint : p.hint, primary = p.primary === undefined ? st.primary : p.primary;
    var extra = p.primary === undefined ? st.extra : null, tone = p.tone || st.tone;
    var canDisconnect = !!p.onDisconnect && !st.noDisconnect && !p.busy;
    // Focus follows the swap: Cancel when the question opens, Disconnect again after Cancel.
    React.useEffect(function () {
      if (!moved.current) { moved.current = true; return; }
      var el = confirm ? confirmRef.current && confirmRef.current.querySelector('button') : offRef.current;
      if (el) el.focus();
    }, [confirm]);
    function act(name) { return p.onAction ? function () { p.onAction(name); } : undefined; }
    function copy() { Promise.resolve(p.onCopy ? p.onCopy() : null).then(function () { setCopied(true); }, function () {}); }
    function off() {
      if (sent.current || !p.onDisconnect) return;
      sent.current = true; setSending(true);
      function done() { sent.current = false; setSending(false); }
      Promise.resolve(p.onDisconnect()).then(done, done);
    }
    var digits = String(p.code || '').split('');
    return h(GlassPanel, { strong: true, className: cx('ot-pillpanel', p.className), role: 'dialog', 'aria-label': who },
      h('div', { className: 'ot-pillpanel-head' },
        h('span', { className: cx('ot-pill-dot', 'is-' + tone), 'aria-hidden': true }),
        h('span', { className: 'text-headline' }, who),
        h('span', { className: 'text-mono-sm ot-muted' }, p.detail || st.word)),
      hint && h('p', { className: 'text-callout ot-muted ot-pillpanel-hint' }, hint),
      st.code && p.code && h('div', { className: 'ot-pair-code is-small' },
        h('span', { className: 'text-caption ot-muted', id: 'ot-pp-code' }, 'Code'),
        h('span', { className: 'ot-pair-digits', role: 'text', 'aria-label': 'Code ' + digits.join(' ') },
          digits.map(function (d, i) { return h('span', { key: i, className: 'ot-pair-digit', 'aria-hidden': true }, d); }))),
      st.command && h('div', { className: 'ot-pillpanel-cmd' },
        h('code', { className: 'text-mono-sm' }, p.command || ''),
        h(Button, { variant: 'ghost', size: 'sm', icon: copied ? 'check' : 'copy', onClick: copy }, copied ? 'Copied' : 'Copy')),
      st.after && h('p', { className: 'text-mono-sm ot-muted ot-pillpanel-hint' }, st.after),
      confirm ? h('div', { ref: confirmRef, className: 'ot-pillpanel-confirm', role: 'alert' },
        h('p', { className: 'text-callout' }, 'Sign ' + who + ' out of Optimus? You can connect it again any time.'),
        h('div', { className: 'ot-approval-actions' },
          h(Button, { variant: 'ghost', disabled: sending, onClick: function () { setConfirm(false); } }, 'Cancel'),
          h(Button, { variant: 'primary', icon: 'close', disabled: sending, onClick: off }, 'Disconnect')))
        : [
          h('div', { key: 'a', className: 'ot-pillpanel-actions' },
            !st.noChange && p.onChange && h(Button, { variant: 'neu', size: 'sm', icon: 'repeat', 'aria-label': 'Change AI', onClick: p.onChange }, 'Change'),
            !st.noPermissions && p.onPermissions && h(Button, { variant: 'neu', size: 'sm', icon: 'sliders', onClick: p.onPermissions }, 'Permissions'),
            extra && h(Button, { variant: 'neu', size: 'sm', icon: extra === 'Stop' ? 'stop' : 'close', onClick: act(extra) }, extra),
            primary && h(Button, { variant: 'primary', size: 'sm', className: 'ot-pillpanel-primary', onClick: primary === 'Connect an AI' ? p.onChange : act(primary) }, primary)),
          canDisconnect && h('button', { key: 'd', ref: offRef, type: 'button', className: 'ot-pillpanel-off text-callout', onClick: function () { setConfirm(true); } }, 'Disconnect')
        ]);
  }
  // The app's pill panel: AIPillPanel with the connection's panel state (connection-state.js pillPanelState).
  // panel: { state, code?, detail?, hint?, primary?, tone? }; label: the AI's name.
  function ConnectionDetail(p) {
    var ps = p.panel || { state: 'connected' };
    return h(AIPillPanel, { className: p.className, state: ps.state, provider: p.label, detail: ps.detail, hint: ps.hint, primary: ps.primary, tone: ps.tone,
      code: ps.code, command: p.command, onCopy: p.onCopy, busy: p.busy, onAction: p.onAction, confirming: p.confirming,
      onChange: p.onChange, onPermissions: p.onPermissions, onDisconnect: p.onDisconnect });
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
          h('span', { className: 'text-callout ot-rrow-title' }, r.title, r.isNew && h('span', { className: 'ot-tag is-new' }, 'New'),
            r.research && h(ResearchTag, { verdict: r.research, className: 'ot-rrow-research' })),
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
            })),
          // §6.8 (design ReportSheet `checkups`, v51): found online, never measured here, so a group of its own after the measured ones.
          p.checkups && p.checkups.length > 0 && h('section', { className: 'ot-rgroup is-checkup' },
            h('h3', { className: 'ot-rgroup-head text-caption' }, h(Icon, { name: 'globe', size: 14, strokeWidth: 2 }), 'AI check-up', h('span', { className: 'ot-muted' }, p.checkups.length)),
            h('p', { className: 'text-mono-sm ot-muted ot-rgroup-note' }, 'Found online for what is on this PC. Not measured here.'),
            p.checkups.map(function (c) {
              var srcs = c.sources || [], first = srcs[0];
              return h('div', { key: c.id, 'data-row': c.id, className: cx('ot-rrow is-checkup', p.highlight === c.id && 'is-hit') },
                h('span', { className: 'text-mono-sm ot-muted' }, c.id),
                h('span', { className: 'ot-rrow-main' },
                  h('span', { className: 'text-callout ot-rrow-title' }, c.kind && h('span', { className: 'ot-checkup-kind text-mono-sm' }, c.kind), c.title),
                  c.text && h('span', { className: 'text-callout ot-muted ot-rrow-clamp' }, c.text)),
                h('span', { className: 'text-mono-sm ot-muted ot-rrow-src' },
                  first ? (p.onOpenSource ? h('button', { type: 'button', className: 'ot-rrow-srcbtn', onClick: function () { p.onOpenSource(first.sourceId); } }, first.site) : first.site) : 'AI check-up',
                  srcs.length > 1 ? ' +' + (srcs.length - 1) : '', h('br'), c.time));
            }))))));
  }

  /* The online check-up's five areas, as they read inside a sentence (design v55). */
  var CHECKUP_AREAS = { hardware: 'hardware', drivers: 'drivers', windows: 'Windows', programs: 'programs', background: 'background items' };
  function andList(a) { return a.length < 2 ? a.join('') : a.slice(0, -1).join(', ') + ' and ' + a[a.length - 1]; }
  // 1–2 areas missed: name them. 3–4 missed: name what was checked instead. Unknown: "some areas".
  function checkupPartial(missed) {
    var keys = Object.keys(CHECKUP_AREAS), m = (missed || []).map(function (k) { return CHECKUP_AREAS[k] || k; });
    if (!m.length) return 'Checked online, except some areas. It will try those again later.';
    if (m.length <= 2) return 'Checked online, except ' + andList(m) + '. It will try those again later.';
    var done = keys.filter(function (k) { return m.indexOf(CHECKUP_AREAS[k]) < 0; }).map(function (k) { return CHECKUP_AREAS[k]; });
    return 'Only checked ' + andList(done) + ' online. It will try the rest again later.';
  }

  // The toolbar's status chip (design Toolbar v55: researching / checking / newCheckups / checkupStatus / researchStatus).
  // The app draws its own toolbar buttons, so only the chip is ported; toolbarChip (connection-state.js) picks which one.
  function ToolbarStatus(p) {
    if (p.researching || p.checking) return h('div', { className: 'ot-tb-status text-mono-sm', role: 'status' }, h(Logo, { size: 14, mono: true, spin: true, decorative: true }),
      p.checking === 'setup' ? 'Getting online lookups ready…' : p.checking ? 'Checking online for known problems…' : (p.researching === true || !p.researching[1]) ? 'Researching…' : 'Researching ' + p.researching[0] + ' of ' + p.researching[1] + '…');
    if (p.newCheckups > 0) return h('div', { className: 'ot-tb-status is-new text-mono-sm', role: 'status' }, h(Icon, { name: 'globe', size: 14 }),
      p.newCheckups + ' new from the online check-up',
      p.onShowNew && h('button', { type: 'button', className: 'ot-tb-status-btn', onClick: p.onShowNew }, 'Show'));
    if (p.checkupStatus) return h('div', { className: cx('ot-tb-status text-mono-sm', p.checkupStatus !== 'none' && 'is-quiet'), role: 'status' },
      h(Icon, { name: p.checkupStatus === 'none' ? 'check' : p.checkupStatus === 'unavailable' ? 'gap' : 'alert', size: 14 }),
      p.checkupStatus === 'none' ? 'Checked online · nothing new found'
        : p.checkupStatus === 'failed' ? 'Couldn’t finish the online check-up. It will try again later.'
          : p.checkupStatus === 'unavailable' ? 'The online check-up isn’t available with ' + (p.ai || 'this AI') + '.'
            : checkupPartial(p.missed));
    if (p.researchStatus) return h('div', { className: 'ot-tb-status is-quiet text-mono-sm', role: 'status' }, h(Icon, { name: p.researchStatus === 'failed' ? 'alert' : 'gap', size: 14 }),
      p.researchStatus === 'failed' ? 'Couldn’t finish looking things up. Try again later.' : 'Looking things up isn’t available with ' + (p.ai || 'this AI') + '.');
    return null;
  }

  // Design GuideAnswer (v51): with no AI connected, a question about the app is answered from the built-in guide.
  function GuideAnswer(p) {
    var paras = String(p.text || '').split(/\n\n+/);
    // App addition: data-answer and tabIndex, so the page can scroll to and focus it like any answer panel.
    return h(GlassPanel, { className: cx('ot-guide-answer', p.className), role: 'region', 'aria-label': 'Built-in guide', 'data-answer': p.answerId, tabIndex: p.answerId ? -1 : undefined },
      h('div', { className: 'ot-guide-head text-caption ot-muted' }, h(Icon, { name: 'report', size: 14 }), 'From the built-in guide'),
      p.question && h('div', { className: 'text-headline' }, p.question),
      paras.map(function (t, i) { return h('p', { key: i, className: 'text-callout' }, t); }),
      h('div', { className: 'ot-guide-foot' },
        h('span', { className: 'text-callout ot-muted' }, 'Connect an AI to ask about this PC.'),
        h('span', { className: 'ot-guide-btns' },
          p.onClose && h(Button, { variant: 'ghost', size: 'sm', onClick: p.onClose }, 'Close'),
          h(Button, { variant: 'neu', size: 'sm', icon: 'spark', onClick: p.onConnect }, 'Connect AI'))));
  }

  // Design PickChoice (v51): which item a change is for, before its preview.
  function PickChoice(p) {
    var s = React.useState(p.value || null), v = s[0], setV = s[1];
    return h('div', { className: 'ot-pick' },
      h('div', { className: 'text-callout ot-pick-q', id: 'pick-q' }, p.question || 'Which one?'),
      h('div', { className: 'ot-pick-chips', role: 'radiogroup', 'aria-labelledby': 'pick-q' }, (p.items || []).map(function (it) {
        return h(CheckRow, { key: it, label: it, checked: v === it, onClick: function () { setV(it); } });
      })),
      h('div', { className: 'ot-insight-actions' },
        h(Button, { variant: 'ghost', size: 'sm', icon: 'back', onClick: p.onBack }, 'Back'),
        h(Button, { variant: 'primary', size: 'sm', disabled: !v, onClick: function () { if (p.onPick) p.onPick(v); } }, 'Preview')));
  }

  window.OptTool = Object.assign(window.OptTool || {}, {
    Logo: Logo, GridBackground: GridBackground, WaveBackground: WaveBackground, GlassPanel: GlassPanel, NeuCard: NeuCard, Expand: Expand,
    Button: Button, Icon: Icon, ProviderOption: ProviderOption, SegmentedControl: SegmentedControl,
    Toggle: Toggle, KeyInput: KeyInput, SeverityBadge: SeverityBadge, FindingCard: FindingCard, ScanProgress: ScanProgress,
    ConnectionPill: ConnectionPill, PermissionGrant: PermissionGrant, AgentActivity: AgentActivity, AgentActivityLive: AgentActivityLive, runAnswer: runAnswer, ApprovalCard: ApprovalCard, ScriptApproval: ScriptApproval,
    CheckRow: CheckRow, AskField: AskField, ChoicePanel: ChoicePanel, SpecScanPanel: SpecScanPanel, PromptBox: PromptBox,
    StarterPrompt: StarterPrompt, ScanStatus: ScanStatus, CopyField: CopyField, PairingCard: PairingCard, ToolbarStatus: ToolbarStatus, checkupPartial: checkupPartial, GuideAnswer: GuideAnswer, PickChoice: PickChoice,
    CardGrid: CardGrid, ActivityPanel: ActivityPanel, AgentAnswers: AgentAnswers, AnswerPanel: AnswerPanel, RiskNote: RiskNote, Popover: Popover, SuggestionList: SuggestionList, HistoryList: HistoryList, AiScanPanel: AiScanPanel, EvidencePeek: EvidencePeek, ContinuePanel: ContinuePanel, ConnectionDetail: ConnectionDetail, AIPillPanel: AIPillPanel, InsightCard: InsightCard, CheckupCard: CheckupCard, FixButton: FixButton, FixPanel: FixPanel, AdminChecks: AdminChecks, ReviewComplete: ReviewComplete, SharePreview: SharePreview, ReportSheet: ReportSheet,
    LoginPanel: LoginPanel, LoginScreen: LoginScreen, ProviderDetails: ProviderDetails,
    PermissionsStep: PermissionsStep, PermissionsPanel: PermissionsPanel, PermissionsSummary: PermissionsSummary,
    ResearchTag: ResearchTag, ResearchNote: ResearchNote, SourceList: SourceList,
    // Shared, data-free helpers for app-flow.js.
    ui: { cx: cx, CATS: CATS, SCAN_CATS: SCAN_CATS, PROVIDERS: PROVIDERS, LOGO_SVG: LOGO_SVG }
  });
})();
