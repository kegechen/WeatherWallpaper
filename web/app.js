'use strict';
// 天气壁纸渲染器：天空 / 太阳 / 月亮 / 星空 / 云 / 雨 / 雪 / 雷电 / 雾
(() => {
  // 两层画布：背景（天空/日月/云，慢速重绘）+ 前景（雨雪闪电，按帧率重绘）
  const cvs = document.getElementById('sky'), fxCvs = document.getElementById('fx');
  const bctx = cvs.getContext('2d', { alpha: false }), fctx = fxCvs.getContext('2d');
  let ctx = bctx;
  const panel = document.getElementById('panel');
  const host = window.chrome && window.chrome.webview;
  const post = m => { if (host) host.postMessage(m); };
  window.onerror = (msg, src, line) => post('log:' + msg + ' @' + line);

  const TAU = Math.PI * 2, RAD = Math.PI / 180;
  const clamp = (v, a, b) => v < a ? a : v > b ? b : v;
  const lerp = (a, b, t) => a + (b - a) * t;
  const smooth = (e0, e1, x) => { const t = clamp((x - e0) / (e1 - e0), 0, 1); return t * t * (3 - 2 * t); };
  const hex = s => [parseInt(s.slice(1, 3), 16), parseInt(s.slice(3, 5), 16), parseInt(s.slice(5, 7), 16)];
  const mix = (a, b, t) => [lerp(a[0], b[0], t), lerp(a[1], b[1], t), lerp(a[2], b[2], t)];
  // 颜色和透明度量化：背景每帧都会新建渐变，Chromium 的渐变缓存按颜色去重，不量化的话缓存会随时间不断增长（实测每分钟约 13 MB）
  const css = (c, a = 1) => L.lowmem
    ? `rgba(${(c[0] >> 2) << 2},${(c[1] >> 2) << 2},${(c[2] >> 2) << 2},${Math.round(a * 50) / 50})`
    : `rgba(${c[0] | 0},${c[1] | 0},${c[2] | 0},${a})`;
  // 渐变缓存：背景每帧要建十几个渐变，Blink 的垃圾堆积得很快（实测每分钟 +11 MB，手动 GC 后才掉回来）。
  // createXGradient 只返回"记录单"，赋给 fillStyle 时才按（几何参数 + 颜色）查缓存、命中就复用同一个真渐变。
  (() => {
    const P = CanvasRenderingContext2D.prototype, pool = new Map();
    class Spec {
      constructor(k, a) { this.k = k; this.a = L.lowmem ? a.map(v => Math.round(v)) : a; this.s = []; }
      addColorStop(p, c) { this.s.push(p, c); }
      real(g) {
        if (!L.lowmem) {   // 省内存模式关闭：每次新建，行为与原来一致
          const r = g[this.k](...this.a);
          for (let i = 0; i < this.s.length; i += 2) r.addColorStop(this.s[i], this.s[i + 1]);
          return r;
        }
        const key = this.k + this.a + '|' + this.s;
        let r = pool.get(key);
        if (!r) {
          if (pool.size > 400) pool.clear();
          r = g[this.k](...this.a);
          for (let i = 0; i < this.s.length; i += 2) r.addColorStop(this.s[i], this.s[i + 1]);
          pool.set(key, r);
        }
        return r;
      }
    }
    const gl = document.createElement('canvas').getContext('2d');
    const lin = gl.createLinearGradient.bind(gl), rad = gl.createRadialGradient.bind(gl);   // 先取原版，再替换
    P.createLinearGradient = function (...a) { return new Spec('createLinearGradient0', a); };
    P.createRadialGradient = function (...a) { return new Spec('createRadialGradient0', a); };
    for (const prop of ['fillStyle', 'strokeStyle']) {
      const d = Object.getOwnPropertyDescriptor(P, prop);
      Object.defineProperty(P, prop, {
        configurable: true, enumerable: true, get: d.get,
        set(v) { d.set.call(this, v instanceof Spec ? v.real({ createLinearGradient0: lin, createRadialGradient0: rad }) : v); },
      });
    }
  })();
  const rand = Math.random;
  function prng(seed) { return () => { seed |= 0; seed = seed + 0x6D2B79F5 | 0; let t = Math.imul(seed ^ seed >>> 15, 1 | seed); t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; }; }

  const L = { primary: location.search.includes('primary'), ground: 0, overlay: false, panel: false, fps: 30, lowmem: false, pos: 'tr', mx: 28, my: 28 };

  // 卡片位置：tr / tl / tc / br / bl；底部时让开任务栏
  function placePanel() {
    const s = panel.style, p = L.pos || 'tr', bottom = p[0] === 'b';
    s.top = bottom ? 'auto' : L.my + 'px';
    s.bottom = bottom ? Math.round(L.ground * H + L.my) + 'px' : 'auto';
    s.left = p[1] === 'l' ? L.mx + 'px' : p[1] === 'c' ? '50%' : 'auto';
    s.right = p[1] === 'r' ? L.mx + 'px' : 'auto';
    s.transform = p[1] === 'c' ? 'translateX(-50%)' : '';
  }
  let W = 0, H = 0, DPR = 1;
  let weather = null, preview = 'live', demo = null, paused = false;

  // ------------------------------------------------------------------ 时间与太阳
  function locInfo() {
    if (weather) return { lat: weather.loc.lat, lon: weather.loc.lon, off: weather.fc.utc_offset_seconds };
    // 还没拿到天气数据时：经度按时区粗估，纬度取中纬度
    const off = -new Date().getTimezoneOffset() * 60;
    return { lat: 35, lon: off / 240, off };
  }
  function now() {
    if (!demo) return Date.now();
    let el = (performance.now() - demo.start) * demo.speed;
    if (demo.end && el > demo.end) el = demo.end;   // 快进演示只走一遍，走完停在终点，由宿主切回实时
    return demo.base + el;
  }
  function sunElev(ms, lat, lon) {
    const d = ms / 86400000 - 10957.5;
    const g = (357.529 + 0.98560028 * d) * RAD;
    const q = 280.459 + 0.98564736 * d;
    const Lr = (q + 1.915 * Math.sin(g) + 0.020 * Math.sin(2 * g)) * RAD;
    const e = (23.439 - 0.00000036 * d) * RAD;
    const ra = Math.atan2(Math.cos(e) * Math.sin(Lr), Math.cos(Lr));
    const dec = Math.asin(Math.sin(e) * Math.sin(Lr));
    const gmst = ((18.697374558 + 24.06570982441908 * d) % 24 + 24) % 24;
    const ha = (gmst * 15 + lon) * RAD - ra, la = lat * RAD;
    return Math.asin(Math.sin(la) * Math.sin(dec) + Math.cos(la) * Math.cos(dec) * Math.cos(ha)) / RAD;
  }
  const dayCache = new Map();
  function dayInfo(ms, li) {
    const off = li.off * 1000;
    const mid = Math.floor((ms + off) / 86400000) * 86400000 - off;
    const key = mid + ':' + li.lat.toFixed(2) + ':' + li.lon.toFixed(2);
    let v = dayCache.get(key);
    if (v) return v;
    let rise = null, set = null, maxE = -90, prev = sunElev(mid, li.lat, li.lon);
    const step = 300000, H0 = -0.833;
    for (let i = 1; i <= 288; i++) {
      const t = mid + i * step, e = sunElev(t, li.lat, li.lon);
      if (prev < H0 && e >= H0 && rise == null) rise = t - step + step * (H0 - prev) / (e - prev);
      if (prev >= H0 && e < H0) set = t - step + step * (H0 - prev) / (e - prev);
      maxE = Math.max(maxE, e); prev = e;
    }
    if (rise == null && maxE > H0) rise = mid;
    if (set == null && maxE > H0) set = mid + 86400000;
    v = { mid, rise, set, maxE };
    if (dayCache.size > 8) dayCache.clear();
    dayCache.set(key, v);
    return v;
  }
  const horizonY = () => H * (1 - L.ground);

  function sunPos(ms, li) {
    const di = dayInfo(ms, li), e = sunElev(ms, li.lat, li.lon);
    const p = di.rise != null ? (ms - di.rise) / (di.set - di.rise) : 0.5;
    const hz = horizonY(), top = H * 0.13;
    return { x: W * (0.1 + 0.8 * clamp(p, -0.15, 1.15)), y: hz - (hz - top) * (e / Math.max(di.maxE, 35)), e, p, di };
  }

  // ------------------------------------------------------------------ 场景
  const PREV = {
    clear: { code: 0, cc: 0, label: '晴天' }, sunrise: { code: 0, cc: 10, time: 'rise', label: '日出' },
    sunset: { code: 1, cc: 25, time: 'set', label: '日落' }, night: { code: 0, cc: 5, time: 'night', label: '晴夜' },
    partly: { code: 2, cc: 50, label: '多云' }, cloudy: { code: 3, cc: 100, label: '阴天' }, fog: { code: 45, cc: 70, label: '雾' },
    drizzle: { code: 53, cc: 95, label: '毛毛雨' }, rain: { code: 63, cc: 100, wind: 15, label: '中雨' },
    heavy: { code: 65, cc: 100, wind: 32, label: '暴雨' }, thunder: { code: 95, cc: 100, wind: 25, label: '雷阵雨' },
    snow: { code: 73, cc: 100, wind: 8, label: '雪' },
  };
  let dayDemo = false;
  const previewLabel = () => {
    const pv = PREV[preview];
    return [pv ? pv.label : '', dayDemo ? '一天快进' : ''].filter(Boolean).join(' · ');
  };
  function targetScene() {
    let code = 1, cc = 20, kmh = 8, dir = 270;
    if (weather) { const c = weather.fc.current; code = c.weather_code; cc = c.cloud_cover; kmh = c.wind_speed_10m; dir = c.wind_direction_10m; }
    const pv = PREV[preview];
    if (pv && pv.code != null) { code = pv.code; cc = pv.cc; if (pv.wind != null) kmh = pv.wind; }
    const s = WX.scene(code, cc);
    const east = Math.sin((dir + 180) * RAD);
    const sign = east >= 0 ? 1 : -1;
    s.windX = clamp(kmh / 45, 0.06, 0.9) * sign * Math.max(0.35, Math.abs(east));
    s.cloudDir = sign;
    s.kmh = kmh;
    s.code = code;
    return s;
  }
  const cur = { cloud: 0.2, rain: 0, snow: 0, fog: 0, thunder: 0, dark: 0, windX: 0.2 };
  let tgt = targetScene(), firstScene = true;

  /// mode：天气预览（live = 实时天气）；day：一天快进演示（用 mode 的天气，从凌晨 4 点快进 24 小时，48 秒走一遍）
  function setPreview(mode, day) {
    const keepDemo = !!day && dayDemo && demo;   // 演示进行中只换天气，时间轴接着走
    preview = PREV[mode] ? mode : 'live';
    dayDemo = !!day;
    const pv = PREV[preview], li = locInfo(), t = Date.now(), di = dayInfo(t, li);
    if (!keepDemo) demo = null;
    if (dayDemo) { if (!keepDemo) demo = { base: di.mid + 4 * 3600000, start: performance.now(), speed: 1800, end: 86400000 }; }
    else if (pv && pv.time === 'rise' && di.rise) demo = { base: di.rise - 12 * 60000, start: performance.now(), speed: 20 };
    else if (pv && pv.time === 'set' && di.set) demo = { base: di.set - 45 * 60000, start: performance.now(), speed: 20 };
    else if (pv && pv.time === 'night') demo = { base: di.mid + 22.5 * 3600000, start: performance.now(), speed: 1 };
    tgt = targetScene();
    renderPanel();
  }

  // ------------------------------------------------------------------ 天空调色
  const SKY = [
    [-18, '#04060f', '#080c1e', '#0e1430'],
    [-11, '#0a1130', '#141c48', '#262c5c'],
    [-5, '#1a2556', '#3d4380', '#a7607a'],
    [-1, '#28407a', '#6a679c', '#ee8a62'],
    [3, '#3765a6', '#9a9fc4', '#ffb877'],
    [9, '#3b7cc4', '#8bb8e4', '#ffdcb0'],
    [20, '#2c7bd0', '#6fb1ec', '#c3e3f9'],
    [50, '#1d6bcc', '#57a3ec', '#b0dafa'],
  ].map(k => [k[0], hex(k[1]), hex(k[2]), hex(k[3])]);
  function skyAt(e) {
    if (e <= SKY[0][0]) return SKY[0].slice(1);
    for (let i = 1; i < SKY.length; i++) {
      if (e <= SKY[i][0]) {
        const a = SKY[i - 1], b = SKY[i], t = (e - a[0]) / (b[0] - a[0]);
        return [mix(a[1], b[1], t), mix(a[2], b[2], t), mix(a[3], b[3], t)];
      }
    }
    return SKY[SKY.length - 1].slice(1);
  }
  const OV_DAY = ['#8a949f', '#a2abb4', '#b8bfc6'].map(hex), OV_NIGHT = ['#121519', '#191c22', '#22262d'].map(hex);

  // ------------------------------------------------------------------ 星空
  let stars = [];
  function initStars() {
    const R = prng(7);
    stars = [];
    const n = Math.round(260 * W * H / (1920 * 1080));
    for (let i = 0; i < n; i++) stars.push({ x: R() * W, y: Math.pow(R(), 1.4) * H * 0.8, r: 0.4 + Math.pow(R(), 3) * 1.4, f: 0.5 + R() * 2.5, p: R() * TAU });
  }

  // ------------------------------------------------------------------ 云
  const SW = 640, SH = 300;
  const masks = [], sprites = [];
  function makeMask(seed, flat) {
    const c = document.createElement('canvas'); c.width = SW; c.height = SH;
    const g = c.getContext('2d'), R = prng(seed);
    const n = flat ? 26 : 18;
    if (flat) { g.translate(0, SH * 0.3); g.scale(1, 0.45); }
    for (let i = 0; i < n; i++) {
      const u = R(), mid = 1 - Math.abs(u - 0.5) * 2;
      const x = SW * (0.12 + 0.76 * u);
      const r = SH * (0.1 + 0.2 * mid * (0.5 + R()) + 0.04);
      const y = SH * 0.64 - r * 0.5 * R() - mid * SH * 0.12;
      const gr = g.createRadialGradient(x, y, 0, x, y, r);
      gr.addColorStop(0, 'rgba(255,255,255,1)'); gr.addColorStop(0.5, 'rgba(255,255,255,.8)'); gr.addColorStop(1, 'rgba(255,255,255,0)');
      g.fillStyle = gr; g.beginPath(); g.arc(x, y, r, 0, TAU); g.fill();
    }
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.globalCompositeOperation = 'destination-out';
    const fb = g.createLinearGradient(0, SH * 0.64, 0, SH * 0.86);
    fb.addColorStop(0, 'rgba(0,0,0,0)'); fb.addColorStop(1, 'rgba(0,0,0,1)');
    g.fillStyle = fb; g.fillRect(0, SH * 0.64, SW, SH);
    return c;
  }
  for (let i = 0; i < 8; i++) masks.push(makeMask(101 + i * 17, false));
  for (let i = 0; i < 4; i++) masks.push(makeMask(907 + i * 31, true));
  for (let i = 0; i < masks.length; i++) { const c = document.createElement('canvas'); c.width = SW; c.height = SH; sprites.push(c); }
  let spriteKey = '', spriteT = -1e9;
  function recolorSprites(top, bot, t) {
    const key = top.concat(bot).map(v => Math.round(v / 6)).join(',');
    if (key === spriteKey || t - spriteT < 400) return;
    spriteKey = key; spriteT = t;
    for (let i = 0; i < masks.length; i++) {
      const g = sprites[i].getContext('2d');
      g.globalCompositeOperation = 'copy'; g.drawImage(masks[i], 0, 0);
      g.globalCompositeOperation = 'source-atop';
      const sh = g.createLinearGradient(0, SH * 0.2, 0, SH * 0.78);
      sh.addColorStop(0, css(top)); sh.addColorStop(1, css(bot));
      g.fillStyle = sh; g.fillRect(0, 0, SW, SH);
    }
  }
  const clouds = [];
  (() => {
    const R = prng(42);
    for (let i = 0; i < 26; i++) {
      const layer = i % 3;
      clouds.push({
        i, layer, spr: (i * 5) % 8, a: 0,
        fx: R() * 1.3 - 0.15,
        fy: layer === 0 ? 0.03 + R() * 0.26 : layer === 1 ? 0.02 + R() * 0.36 : -0.06 + R() * 0.46,
        sc: layer === 0 ? 0.45 + R() * 0.25 : layer === 1 ? 0.7 + R() * 0.3 : 1.0 + R() * 0.55,
        sp: layer === 0 ? 3 + R() * 4 : layer === 1 ? 6 + R() * 6 : 11 + R() * 9,
      });
    }
    for (let i = 0; i < 7; i++) clouds.push({ i: 100 + i, layer: 3, spr: 8 + i % 4, a: 0, fx: i / 6 - 0.1, fy: -0.06 + (i % 3) * 0.07, sc: 2.4 + (i % 2) * 0.5, sp: 4 + i % 3 });
  })();

  // ------------------------------------------------------------------ 雨 / 雪 / 溅射 / 闪电
  const drops = [], flakes = [], splash = [], ripples = [];
  let hitsR = [], hitsS = [];
  function newDrop(d, initial) {
    d.z = 0.3 + 0.7 * Math.pow(rand(), 0.8);
    d.v = (900 + 700 * d.z) * H / 1080;
    d.l = (10 + 24 * d.z) * (0.6 + 0.5 * cur.rain) * H / 1080;
    const slant = Math.abs(cur.windX) * 0.22 * H;
    d.x = rand() * (W + slant) - (cur.windX > 0 ? slant : 0);
    d.y = initial ? rand() * H : -rand() * H * 0.25 - d.l;
    const gy = horizonY();
    d.gy = d.z > 0.62 ? gy : gy * (0.86 + 0.14 * rand());
    d.tier = d.z < 0.55 ? 0 : d.z < 0.8 ? 1 : 2;
    return d;
  }
  function newFlake(f, initial) {
    f.z = 0.3 + 0.7 * rand();
    const k = H / 1080;
    f.r = (0.8 + 2.6 * f.z * f.z) * k;
    f.v = (22 + 68 * f.z) * k;
    f.amp = (10 + 30 * f.z) * k; f.fr = 0.4 + rand() * 1.1; f.ph = rand() * TAU;
    f.x = rand() * W * 1.2 - W * 0.1;
    f.y = initial ? rand() * H : -10 - rand() * H * 0.2;
    const gy = horizonY();
    f.gy = f.z > 0.6 ? gy : gy * (0.85 + 0.15 * rand());
    f.tier = f.z < 0.55 ? 0 : f.z < 0.8 ? 1 : 2;
    return f;
  }
  function localSplash(x, z) {
    const gy = horizonY(), k = H / 1080;
    for (let i = 0; i < 3 && splash.length < 400; i++)
      splash.push({ x, y: gy - 1, vx: (rand() - 0.5) * 140 * k + cur.windX * 40 * k, vy: -(50 + rand() * 120) * k * z, r: (0.7 + rand() * 0.7) * k });
    if (ripples.length < 150) ripples.push({ x, t: 0, life: 0.25 + rand() * 0.1, z });
  }

  let ltNext = 3, flash = null;
  function makeBolt() {
    const pts = [], k = H / 1080;
    let x = W * (0.15 + rand() * 0.7), y = -10;
    const end = H * (0.45 + rand() * 0.25);
    pts.push([x, y]);
    const branches = [];
    while (y < end) {
      x += (rand() - 0.5) * 46 * k; y += (10 + rand() * 22) * k;
      pts.push([x, y]);
      if (rand() < 0.07 && branches.length < 3) {
        const b = [[x, y]]; let bx = x, by = y, dir = rand() < 0.5 ? -1 : 1;
        for (let j = 0; j < 6 + rand() * 6; j++) { bx += (dir * 12 + (rand() - 0.5) * 20) * k; by += (8 + rand() * 16) * k; b.push([bx, by]); }
        branches.push(b);
      }
    }
    return { main: pts, branches };
  }

  // ------------------------------------------------------------------ 更新
  let simT = 0, lastScenePost = 0, lastSceneMsg = '', panelT = 0;
  function update(dt) {
    simT += dt;
    tgt = targetScene();
    const k = firstScene ? 1 : 1 - Math.exp(-dt / (demo || preview !== 'live' ? 1.5 : 4));
    firstScene = false;
    for (const key in cur) cur[key] += (tgt[key] - cur[key]) * k;

    // 云
    const wind = 0.6 + Math.min(tgt.kmh, 50) / 22;
    const count = Math.round(cur.cloud * 26);
    const ov = smooth(0.65, 1, cur.cloud);
    for (const c of clouds) {
      const want = c.layer === 3 ? ov * 0.95 : c.i < count ? [0.72, 0.86, 0.95][c.layer] : 0;
      c.a += (want - c.a) * (1 - Math.exp(-dt / 3));
      c.fx += c.sp * wind * tgt.cloudDir * dt / W;
      const wFrac = SW * c.sc * (W / 1920) / W;
      if (c.fx > 1.05) c.fx = -wFrac - 0.02; else if (c.fx < -wFrac - 0.05) c.fx = 1.02;
    }

    // 雨
    const gy = horizonY();
    const wantDrops = Math.round(cur.rain * (1 + 2.2 * lightRain()) * 1100 * W / 1920);
    while (drops.length < wantDrops) drops.push(newDrop({}, drops.length < wantDrops * 0.9 && firstFill));
    firstFill = false;
    const vxk = cur.windX * 0.22;
    for (let i = drops.length - 1; i >= 0; i--) {
      const d = drops[i];
      d.y += d.v * dt; d.x += d.v * vxk * dt;
      if (d.y >= d.gy) {
        if (d.z > 0.82 && d.gy === gy && d.x >= 0 && d.x <= W) {
          if (L.overlay) { if (hitsR.length < 80) hitsR.push((d.x / W).toFixed(4) + ':' + d.z.toFixed(2)); }
          else localSplash(d.x, d.z);
        }
        if (drops.length > wantDrops) { drops[i] = drops[drops.length - 1]; drops.pop(); } else newDrop(d, false);
      }
    }
    // 雪
    const wantFlakes = Math.round(cur.snow * 650 * W / 1920);
    while (flakes.length < wantFlakes) flakes.push(newFlake({}, true));
    for (let i = flakes.length - 1; i >= 0; i--) {
      const f = flakes[i];
      f.y += f.v * dt;
      f.x += (cur.windX * 45 * f.z + Math.cos(simT * f.fr + f.ph) * f.amp * f.fr) * dt;
      if (f.y >= f.gy) {
        if (L.overlay && f.z > 0.7 && f.gy === gy && hitsS.length < 40 && f.x >= 0 && f.x <= W) hitsS.push((f.x / W).toFixed(4) + ':' + f.z.toFixed(2));
        if (flakes.length > wantFlakes) { flakes[i] = flakes[flakes.length - 1]; flakes.pop(); } else newFlake(f, false);
      }
    }
    // 本地溅射（没有任务栏特效层时）
    const grav = 1100 * H / 1080;
    for (let i = splash.length - 1; i >= 0; i--) {
      const s = splash[i];
      s.vy += grav * dt; s.x += s.vx * dt; s.y += s.vy * dt;
      if (s.y > gy && s.vy > 0) { splash[i] = splash[splash.length - 1]; splash.pop(); }
    }
    for (let i = ripples.length - 1; i >= 0; i--) { ripples[i].t += dt; if (ripples[i].t > ripples[i].life) { ripples[i] = ripples[ripples.length - 1]; ripples.pop(); } }

    // 闪电
    if (tgt.thunder > 0.5) {
      ltNext -= dt;
      if (ltNext <= 0) { flash = { t: 0, bolt: rand() < 0.65 ? makeBolt() : null }; ltNext = (rand() < 0.25 ? 0.6 : 4) + rand() * 11; }
    }
    if (flash) { flash.t += dt; if (flash.t > 1.4) flash = null; }

    if ((hitsR.length || hitsS.length) && L.primary && L.overlay) post('H' + hitsR.join(',') + '|' + hitsS.join(','));
    hitsR = []; hitsS = [];

    if (L.primary && simT - lastScenePost > 1) {
      lastScenePost = simT;
      let text = '';
      if (weather) {
        const c = weather.fc.current;
        text = `${weather.loc.name} ${WX.info(c.weather_code).text} ${Math.round(c.temperature_2m)}°`;
      }
      const msg = JSON.stringify({ t: 'scene', rain: +tgt.rain.toFixed(2), snow: +tgt.snow.toFixed(2), wind: +tgt.windX.toFixed(2), text });
      if (msg !== lastSceneMsg) { lastSceneMsg = msg; post(JSON.parse(msg)); }
    }
    if (simT - panelT > 60) { panelT = simT; renderPanel(); }
  }
  let firstFill = true;

  // ------------------------------------------------------------------ 绘制
  // 暂停时的静态帧 / 录制逐帧：前景画布也要按需分配，否则雨雪画在 1×1 的隐藏画布上
  function draw() { drawBg(); const on = fxNeeded(); fxAlloc(on); fxDirty = on; if (on) drawFx(); }
  // 背景画布分辨率自适应：有星星 / 月亮 / 清晰的太阳时用全分辨率；阴天、雨雪、雾等画面本来就柔和，用半分辨率省显存
  let bgK = 1, wantK = 1, wantSince = 0;
  function bgAlloc(k) {
    bgK = k;
    cvs.width = Math.round(W * DPR * k); cvs.height = Math.round(H * DPR * k);
    bctx.setTransform(DPR * k, 0, 0, DPR * k, 0, 0);
  }
  function drawBg() {
    ctx = bctx;
    if (wantK !== bgK && performance.now() - wantSince > 3000) bgAlloc(wantK);   // 持续 3 秒才切换，避免临界处来回抖
    const li = locInfo(), t = now();
    const sp = sunPos(t, li), e = sp.e;
    const hz = horizonY();
    const daylight = smooth(-6, 8, e);
    const golden = smooth(-5, 0, e) * (1 - smooth(5, 15, e));
    const storm = clamp(cur.rain * 0.7 + cur.thunder * 0.3 + cur.dark, 0, 1);

    // 天空
    let [top, mid, bot] = skyAt(e);
    const ovAmt = clamp(Math.pow(cur.cloud, 1.6) * 0.82 + cur.dark * 0.25 + cur.fog * 0.3, 0, 0.95);
    const ovTop = mix(OV_NIGHT[0], OV_DAY[0], daylight), ovMid = mix(OV_NIGHT[1], OV_DAY[1], daylight), ovBot = mix(OV_NIGHT[2], OV_DAY[2], daylight);
    const darkK = 1 - cur.dark * 0.55;
    top = mix(top, ovTop, ovAmt).map(v => v * darkK); mid = mix(mid, ovMid, ovAmt).map(v => v * darkK); bot = mix(bot, ovBot, ovAmt).map(v => v * (1 - cur.dark * 0.4));
    const sg = ctx.createLinearGradient(0, 0, 0, hz);
    sg.addColorStop(0, css(top)); sg.addColorStop(0.55, css(mid)); sg.addColorStop(1, css(bot));
    ctx.globalCompositeOperation = 'source-over'; ctx.globalAlpha = 1;
    ctx.fillStyle = sg; ctx.fillRect(0, 0, W, hz);
    ctx.fillStyle = css(bot); ctx.fillRect(0, hz - 1, W, H - hz + 1);

    // 日出日落地平线辉光
    const glowAmt = smooth(-11, -1, e) * (1 - smooth(4, 14, e)) * (1 - ovAmt * 0.8);
    if (glowAmt > 0.01) {
      ctx.save();
      ctx.translate(clamp(sp.x, -W * 0.1, W * 1.1), hz);
      ctx.scale(1, 0.42);
      const R = W * 0.75;
      const gg = ctx.createRadialGradient(0, 0, 0, 0, 0, R);
      const gc = mix(hex('#ff6a2b'), hex('#ffb066'), smooth(-4, 6, e));
      gg.addColorStop(0, css(gc, 0.65 * glowAmt)); gg.addColorStop(0.35, css(gc, 0.25 * glowAmt)); gg.addColorStop(1, css(gc, 0));
      ctx.fillStyle = gg; ctx.fillRect(-R, -R, 2 * R, 2 * R);
      ctx.restore();
    }

    // 星星
    const starA = (1 - smooth(-14, -5, e)) * (1 - smooth(0.25, 0.75, cur.cloud)) * (1 - cur.fog);
    if (starA > 0.01) {
      ctx.fillStyle = '#fff';
      for (const s of stars) {
        if (s.y > hz) continue;
        ctx.globalAlpha = starA * (0.55 + 0.45 * Math.sin(simT * s.f + s.p)) * (1 - s.y / hz * 0.6);
        ctx.fillRect(s.x, s.y, s.r, s.r);
      }
      ctx.globalAlpha = 1;
    }

    const vis = (1 - smooth(0.45, 1, cur.cloud) * 0.93) * (1 - cur.fog * 0.6);
    // 月亮
    const moonV = e < -2 ? (1 - smooth(-6, -2, e)) * (1 - smooth(0.55, 1, cur.cloud) * 0.9) * (1 - cur.fog * 0.6) : 0;
    if (moonV > 0) drawMoon(t, li, sp.di, moonV);
    const k = !L.lowmem || starA > 0.01 || moonV > 0.05 || vis * smooth(-4, 0.5, e) > 0.25 ? 1 : 0.5;
    if (k !== wantK) { wantK = k; wantSince = performance.now(); }
    // 太阳
    if (e > -4) drawSun(sp, vis);

    // 云
    const cc = cloudColors(e, daylight, golden, storm);
    recolorSprites(cc[0], cc[1], performance.now());
    const ov = smooth(0.65, 1, cur.cloud);
    if (ov > 0.01) {
      const og = ctx.createLinearGradient(0, 0, 0, hz * 0.85);
      og.addColorStop(0, css(cc[1], ov * 0.8)); og.addColorStop(0.6, css(mix(cc[0], cc[1], 0.5), ov * 0.35)); og.addColorStop(1, css(cc[0], 0));
      ctx.fillStyle = og; ctx.fillRect(0, 0, W, hz);
    }
    const sx = W / 1920;
    for (const layer of [3, 0, 1, 2]) {   // 层云垫底，然后由远及近
      for (const c of clouds) {
        if (c.layer !== layer || c.a < 0.01) continue;
        const w = SW * c.sc * sx, h = SH * c.sc * sx;
        ctx.globalAlpha = c.a;
        ctx.drawImage(sprites[c.spr], c.fx * W, c.fy * H, w, h);
      }
    }
    ctx.globalAlpha = 1;

    // 雨天压暗 / 雾 / 雾霾
    if (cur.dark > 0.01) {
      const dg = ctx.createLinearGradient(0, 0, 0, H);
      dg.addColorStop(0, `rgba(6,8,14,${cur.dark * 0.5})`); dg.addColorStop(1, `rgba(6,8,14,${cur.dark * 0.15})`);
      ctx.fillStyle = dg; ctx.fillRect(0, 0, W, H);
    }
    if (cur.fog > 0.01) {
      const fc = mix(hex('#3a3f47'), hex('#d5d9de'), daylight);
      const fg = ctx.createLinearGradient(0, H * 0.15, 0, H);
      fg.addColorStop(0, css(fc, cur.fog * 0.25)); fg.addColorStop(1, css(fc, cur.fog * 0.75));
      ctx.fillStyle = fg; ctx.fillRect(0, 0, W, H);
      for (let i = 0; i < 5; i++) {
        const x = ((simT * (6 + i * 3) / W + i * 0.27) % 1.6 - 0.4) * W;
        ctx.globalAlpha = cur.fog * 0.55;
        ctx.drawImage(sprites[8 + i % 4], x, H * (0.45 + i * 0.09), W * 0.9, H * 0.35);
      }
      ctx.globalAlpha = 1;
    }
    const aqi = weather && preview === 'live' ? WX.chinaAqi(weather.aq && weather.aq.current) : null;
    if (aqi > 100) {
      const ha = Math.min(0.3, (aqi - 100) / 500);
      const hg = ctx.createLinearGradient(0, 0, 0, H);
      hg.addColorStop(0, `rgba(190,175,150,${ha * 0.4})`); hg.addColorStop(1, `rgba(190,175,150,${ha})`);
      ctx.fillStyle = hg; ctx.fillRect(0, 0, W, H);
    }
    lastDaylight = daylight;
  }

  // 前景层：降水与闪电（透明画布，按设定帧率绘制；没有降水时整层停掉）
  let lastDaylight = 1;
  function drawFx() {
    ctx = fctx;
    ctx.clearRect(0, 0, W, H);
    const daylight = lastDaylight;
    if (drops.length) drawRain(daylight);
    if (flakes.length) drawSnow(daylight);
    if (splash.length || ripples.length) drawSplash(daylight);

    // 闪电
    if (flash) {
      const ft = flash.t;
      const a = ft < 0.05 ? 0.55 : ft < 0.1 ? 0.08 : ft < 0.2 ? 0.42 : 0.42 * Math.exp(-(ft - 0.2) * 5);
      ctx.globalCompositeOperation = 'lighter';
      ctx.fillStyle = `rgba(185,195,235,${a * 0.55})`; ctx.fillRect(0, 0, W, H);
      if (flash.bolt && ft < 0.32) {
        ctx.strokeStyle = `rgba(235,240,255,${ft < 0.05 || (ft > 0.1 && ft < 0.2) ? 1 : 0.5})`;
        ctx.shadowColor = 'rgba(170,185,255,1)'; ctx.shadowBlur = 24;
        ctx.lineJoin = 'round';
        const path = (pts, w) => { ctx.lineWidth = w; ctx.beginPath(); ctx.moveTo(pts[0][0], pts[0][1]); for (const p of pts) ctx.lineTo(p[0], p[1]); ctx.stroke(); };
        path(flash.bolt.main, 2.6 * H / 1080);
        for (const b of flash.bolt.branches) path(b, 1.3 * H / 1080);
        ctx.shadowBlur = 0;
      }
      ctx.globalCompositeOperation = 'source-over';
    }
  }

  function cloudColors(e, daylight, golden, storm) {
    let top = mix(hex('#3b4357'), hex('#ffffff'), daylight), bot = mix(hex('#1a1f2c'), hex('#c3cdda'), daylight);
    top = mix(top, hex('#ffd0a4'), golden * 0.7); bot = mix(bot, hex('#8b7693'), golden * 0.6);
    const sTop = mix(hex('#2a2f38'), hex('#a7afb9'), daylight), sBot = mix(hex('#14171c'), hex('#5f6773'), daylight);
    return [mix(top, sTop, storm), mix(bot, sBot, storm)];
  }

  function drawSun(sp, vis) {
    const { x, y, e } = sp;
    const R0 = Math.min(W, H) * 0.036;
    const low = 1 - smooth(1, 22, e);
    const core = mix(hex('#fffbea'), hex('#ff9a3c'), low), glow = mix(hex('#fff0c0'), hex('#ff7a2e'), low);
    const r = R0 * (1 + 0.2 * low);
    const fade = smooth(-4, 0.5, e);
    const v = vis * fade;
    if (v < 0.01) return;
    ctx.globalCompositeOperation = 'screen';
    // 大范围光晕
    const g1 = ctx.createRadialGradient(x, y, 0, x, y, r * 16);
    g1.addColorStop(0, css(glow, 0.55 * v)); g1.addColorStop(0.08, css(glow, 0.3 * v)); g1.addColorStop(0.3, css(glow, 0.09 * v)); g1.addColorStop(1, css(glow, 0));
    ctx.fillStyle = g1; ctx.fillRect(x - r * 16, y - r * 16, r * 32, r * 32);
    // 光芒（贴近地平线时减弱）
    if (v > 0.3 && low < 0.97) {
      const rayK = 1 - low * 0.75;
      ctx.save(); ctx.translate(x, y); ctx.rotate(simT * 0.02);
      const n = 14;
      for (let i = 0; i < n; i++) {
        const len = r * (5 + 3 * Math.sin(i * 2.3 + simT * 0.3));
        const rg = ctx.createLinearGradient(0, 0, len, 0);
        rg.addColorStop(0, css(glow, 0.16 * v * rayK)); rg.addColorStop(1, css(glow, 0));
        ctx.fillStyle = rg;
        ctx.beginPath(); ctx.moveTo(0, 0); ctx.lineTo(len, -r * 0.22); ctx.lineTo(len, r * 0.22); ctx.closePath(); ctx.fill();
        ctx.rotate(TAU / n);
      }
      ctx.restore();
    }
    // 日轮：正常叠加保证颜色饱和、边缘清晰；高空时再用 screen 提亮成耀眼的白
    ctx.globalCompositeOperation = 'source-over';
    const inner = mix([255, 255, 248], mix(hex('#ffd59a'), core, 0.4), low);
    const g2 = ctx.createRadialGradient(x, y, 0, x, y, r * 1.35);
    g2.addColorStop(0, css(inner, v)); g2.addColorStop(0.6, css(core, v)); g2.addColorStop(0.74, css(core, 0.55 * v)); g2.addColorStop(1, css(core, 0));
    ctx.fillStyle = g2; ctx.beginPath(); ctx.arc(x, y, r * 1.35, 0, TAU); ctx.fill();
    ctx.globalCompositeOperation = 'screen';
    if (low < 0.6) {
      const g3 = ctx.createRadialGradient(x, y, 0, x, y, r * 2.2);
      g3.addColorStop(0, css([255, 255, 255], 0.6 * v * (1 - low))); g3.addColorStop(1, 'rgba(255,255,255,0)');
      ctx.fillStyle = g3; ctx.beginPath(); ctx.arc(x, y, r * 2.2, 0, TAU); ctx.fill();
    }
    // 镜头光斑
    if (v > 0.55 && e > 2) {
      const cx = W / 2, cy = H / 2, dx = cx - x, dy = cy - y;
      [[0.45, 0.6, 0.05], [0.8, 0.25, 0.07], [1.25, 1.1, 0.04], [1.6, 0.4, 0.06]].forEach(([k, s, a]) => {
        const fx = x + dx * k, fy = y + dy * k, fr = r * s * 1.6;
        const fg = ctx.createRadialGradient(fx, fy, 0, fx, fy, fr);
        fg.addColorStop(0, css(glow, a * v)); fg.addColorStop(0.7, css(glow, a * 0.6 * v)); fg.addColorStop(1, css(glow, 0));
        ctx.fillStyle = fg; ctx.beginPath(); ctx.arc(fx, fy, fr, 0, TAU); ctx.fill();
      });
    }
    ctx.globalCompositeOperation = 'source-over';
  }

  function drawMoon(t, li, di, v) {
    if (v < 0.01) return;
    let a, b;
    if (di.set != null && t > di.set) { a = di.set; b = dayInfo(t + 86400000, li).rise; }
    else { a = dayInfo(t - 86400000, li).set; b = di.rise; }
    if (a == null || b == null) { a = di.mid - 6 * 3600000; b = di.mid + 6 * 3600000; }
    const p = clamp((t - a) / (b - a), 0, 1);
    const hz = horizonY();
    const x = W * (0.12 + 0.76 * p), y = hz - (hz - H * 0.16) * Math.sin(Math.PI * p) * 0.9;
    const T = 29.530588853, age = (((t - 947182440000) / 86400000) % T + T) % T;
    const ph = Math.cos(TAU * age / T), waxing = age < T / 2;
    const r = Math.min(W, H) * 0.026;
    ctx.globalCompositeOperation = 'lighter';
    const illum = (1 - ph) / 2;
    const gg = ctx.createRadialGradient(x, y, r * 0.5, x, y, r * 9);
    gg.addColorStop(0, `rgba(200,215,255,${0.22 * v * (0.3 + illum)})`); gg.addColorStop(1, 'rgba(200,215,255,0)');
    ctx.fillStyle = gg; ctx.fillRect(x - r * 9, y - r * 9, r * 18, r * 18);
    ctx.globalCompositeOperation = 'source-over';
    ctx.save(); ctx.translate(x, y); ctx.rotate(-0.35);
    if (!waxing) ctx.scale(-1, 1);
    ctx.fillStyle = `rgba(160,170,195,${0.12 * v})`;
    ctx.beginPath(); ctx.arc(0, 0, r, 0, TAU); ctx.fill();
    ctx.beginPath();
    ctx.arc(0, 0, r, -Math.PI / 2, Math.PI / 2, false);
    ctx.ellipse(0, 0, r * Math.abs(ph), r, 0, Math.PI / 2, -Math.PI / 2, ph > 0);
    ctx.closePath();
    const mg = ctx.createRadialGradient(-r * 0.3, -r * 0.3, 0, 0, 0, r);
    mg.addColorStop(0, `rgba(255,252,235,${v})`); mg.addColorStop(1, `rgba(225,220,200,${v})`);
    ctx.fillStyle = mg; ctx.fill();
    ctx.restore();
  }

  // 雨丝用预渲染贴图 + 剪切变换 drawImage：比每帧描上千段抗锯齿线便宜得多（GPU 进程不用每帧剖分路径）
  // 0 = 中雨及以上，1 = 极小的雨：毛毛雨本来就稀、细，按原比例画几乎看不见，需要加密 + 提亮
  const lightRain = () => clamp(1 - cur.rain / 0.45, 0, 1);
  const rainSpr = [];
  let rainSprKey = '';
  function rainSprites(col, k) {
    const lr = Math.round(lightRain() * 5) / 5;
    const key = col.map(v => Math.round(v / 4)).join(',') + '|' + k.toFixed(2) + '|' + DPR + '|' + lr;
    if (key === rainSprKey) return rainSpr;
    rainSprKey = key;
    const T = [[0.8, 0.4], [1.1, 0.64], [1.6, 0.96]];   // 画时再乘 globalAlpha（0.5 起，小雨提亮）
    const dark = [70, 80, 100];
    for (let t = 0; t < 3; t++) {
      const lw = T[t][0] * k * DPR, ow = lw + 2 * DPR, w = Math.ceil(ow + 2), h = 64;
      const c = rainSpr[t] || (rainSpr[t] = document.createElement('canvas'));
      c.width = w; c.height = h;
      const g = c.getContext('2d');
      // 深色描边：落在亮云上的细雨也能看清
      const og = g.createLinearGradient(0, 0, 0, h);
      const oa = T[t][1] * (0.18 + 0.32 * lr);
      og.addColorStop(0, css(dark, 0)); og.addColorStop(0.35, css(dark, oa * 0.7)); og.addColorStop(1, css(dark, oa));
      g.fillStyle = og;
      g.fillRect((w - ow) / 2, 0, ow, h);
      g.clearRect((w - lw) / 2, 0, lw, h);
      const gr = g.createLinearGradient(0, 0, 0, h);
      gr.addColorStop(0, css(col, 0)); gr.addColorStop(0.35, css(col, T[t][1] * 0.7)); gr.addColorStop(1, css(col, T[t][1]));
      g.fillStyle = gr;
      g.fillRect((w - lw) / 2, 0, lw, h);
      c.lw = w / DPR;
    }
    return rainSpr;
  }

  function drawRain(daylight) {
    const col = mix([150, 165, 190], [210, 222, 238], daylight);
    const vxk = cur.windX * 0.22, k = H / 1080;
    const spr = rainSprites(col, k);
    // x' = x + s*y：沿雨丝方向剪切，贴图竖直画，落到屏幕上就是斜的
    ctx.setTransform(DPR, 0, vxk * DPR, DPR, 0, 0);
    ctx.globalAlpha = 0.5 * (1 + 0.75 * lightRain());
    for (const d of drops) {
      const s = spr[d.tier];
      ctx.drawImage(s, d.x - vxk * d.y - s.lw / 2, d.y - d.l, s.lw, d.l);
    }
    ctx.globalAlpha = 1;
    ctx.setTransform(DPR, 0, 0, DPR, 0, 0);
    // 雨幕
    const mg = ctx.createLinearGradient(0, H * 0.5, 0, H);
    mg.addColorStop(0, css(col, 0)); mg.addColorStop(1, css(col, 0.1 * cur.rain));
    ctx.fillStyle = mg; ctx.fillRect(0, H * 0.5, W, H * 0.5);
  }

  function drawSnow(daylight) {
    const col = mix([175, 185, 205], [255, 255, 255], daylight);
    const A = [0.5, 0.72, 0.92];
    for (let tier = 0; tier < 3; tier++) {
      ctx.beginPath();
      for (const f of flakes) {
        if (f.tier !== tier) continue;
        ctx.moveTo(f.x + f.r, f.y); ctx.arc(f.x, f.y, f.r, 0, TAU);
      }
      ctx.fillStyle = css(col, A[tier]);
      ctx.fill();
    }
  }

  function drawSplash(daylight) {
    const col = mix([170, 185, 205], [225, 235, 248], daylight), gy = horizonY(), k = H / 1080;
    ctx.fillStyle = css(col, 0.75);
    ctx.beginPath();
    for (const s of splash) { ctx.moveTo(s.x + s.r, s.y); ctx.arc(s.x, s.y, s.r, 0, TAU); }
    ctx.fill();
    ctx.lineWidth = 1;
    for (const r of ripples) {
      const q = r.t / r.life;
      ctx.strokeStyle = css(col, 0.5 * (1 - q));
      ctx.beginPath(); ctx.ellipse(r.x, gy, (2 + 9 * q) * k * (0.6 + 0.6 * r.z), (0.8 + 1.6 * q) * k, 0, 0, TAU); ctx.stroke();
    }
  }

  // ------------------------------------------------------------------ 天气卡片
  function renderPanel() {
    if (!(L.panel && L.primary) || !weather) { panel.classList.add('hidden'); return; }
    placePanel();
    try {
      panel.innerHTML = WX.panelHtml(weather, Date.now() + weather.fc.utc_offset_seconds * 1000, previewLabel());
      panel.classList.remove('hidden');
    } catch (err) { post('log:panel ' + err.message); }
  }

  // ------------------------------------------------------------------ 主循环
  // 背景层变化很慢（云每秒只飘十几像素），最多 15 FPS；前景层只在有降水/闪电时按 L.fps 绘制
  let last = performance.now(), lastBg = -1e9, raf = 0, fxDirty = false;
  function frame(ts) {
    raf = 0;
    if (paused) return;
    raf = requestAnimationFrame(frame);
    const fxActive = fxNeeded();
    const bgFps = demo && demo.speed > 1 ? L.fps : Math.min(L.fps, 15);
    const interval = 1000 / (fxActive ? L.fps : bgFps);
    if (ts - last < interval - 3) return;
    const dt = Math.min(0.1, (ts - last) / 1000);
    last = ts;
    update(dt);
    if (fxActive) { fxAlloc(true); drawFx(); fxDirty = true; }
    else if (fxDirty) { fxAlloc(false); fxDirty = false; }
    if (ts - lastBg >= 1000 / bgFps - 3) { lastBg = ts; drawBg(); }
  }
  // 前景画布只在有雨雪 / 闪电时才分配整屏大小；晴天、多云时缩成 1×1 并隐藏，省下一整屏的显存和合成开销
  function fxNeeded() { return drops.length > 0 || flakes.length > 0 || splash.length > 0 || ripples.length > 0 || !!flash || tgt.rain > 0 || tgt.snow > 0; }
  let fxOn = false;
  function fxAlloc(on) {
    if (on === fxOn) return;
    fxOn = on;
    fxCvs.width = on ? Math.round(W * DPR) : 1; fxCvs.height = on ? Math.round(H * DPR) : 1;
    fxCvs.style.display = on ? '' : 'none';
    if (on) fctx.setTransform(DPR, 0, 0, DPR, 0, 0);
  }
  function resize() {
    DPR = window.devicePixelRatio || 1;
    W = window.innerWidth; H = window.innerHeight;
    bgAlloc(bgK);
    { const was = fxOn; fxOn = !was; fxAlloc(was); }   // 按新尺寸重新分配（或确保初始为 1×1）
    lastBg = -1e9; fxDirty = true;
    initStars();
    drops.length = 0; flakes.length = 0; firstFill = true;
  }
  window.addEventListener('resize', resize);
  resize();

  if (host) {
    host.addEventListener('message', ev => {
      const m = ev.data;
      if (!m || !m.t) return;
      switch (m.t) {
        case 'layout': Object.assign(L, m); drops.length = 0; flakes.length = 0; firstFill = true; renderPanel(); break;
        case 'weather': weather = m; dayCache.clear(); tgt = targetScene(); renderPanel(); break;
        case 'preview': setPreview(m.mode, m.day); break;
        case 'pause': paused = true; break;
        case 'perf': L.fps = clamp(m.fps | 0, 5, 60); break;
      }
      // 暂停中（被最大化窗口遮挡）也要保证画面是最新的静态帧，取消遮挡时不会闪黑
      if (paused) { firstScene = true; update(0.016); draw(); }
      switch (m.t) {
        case 'resume': if (paused) { paused = false; last = performance.now(); if (!raf) raf = requestAnimationFrame(frame); } break;
      }
    });
    post('ready');
  } else {
    // 浏览器里直接打开调试：?mode=rain&day=1&data=sample.json&panel=0
    const q = new URLSearchParams(location.search);
    L.panel = q.get('panel') !== '0'; L.primary = true; L.ground = 0.0444;
    if (q.get('manual') === '1') {
      // 录制用：停掉 rAF，由外部按固定步长推进虚拟时钟逐帧绘制（截图慢也不影响动画流畅度）
      let vt = performance.now();
      performance.now = () => vt;
      paused = true;
      window.__step = (dt) => { vt += dt * 1000; update(dt); draw(); };
    }
    const apply = () => {
      setPreview(q.get('mode') || 'live', q.get('day') === '1');
      const at = /^(\d+):(\d+)$/.exec(q.get('at') || '');   // ?at=13:00 固定当地时间
      if (at && !dayDemo) demo = { base: dayInfo(Date.now(), locInfo()).mid + (at[1] * 60 + +at[2]) * 60000, start: performance.now(), speed: 1 };
    };
    if (q.get('data')) fetch(q.get('data')).then(r => r.json()).then(m => { weather = m; dayCache.clear(); apply(); firstScene = true; window.__ready = true; });
    else { apply(); window.__ready = true; }
  }
  setInterval(() => { if (paused) { firstScene = true; update(0.016); draw(); } }, 60000);
  raf = requestAnimationFrame(frame);
})();
