'use strict';
// 天气壁纸渲染器：天空 / 太阳 / 月亮 / 星空 / 云 / 雨 / 雪 / 雷电 / 雾
(() => {
  // 分层（自下而上）：backdrop 系统壁纸底图（仅壁纸叠加）→ sky 天空（不透明；内容变了才重画）
  // → cloud 云（半分辨率，有云才存在）→ veil 压暗 / 雾霾（CSS 渐变）→ fx 雨雪闪电（按设定帧率）
  // 壁纸叠加模式（L.overlayWall）：底图常驻，天空画布改成带 alpha，天空渐变只画很淡的一层，日月云雨照常不透明——
  // 壁纸从天空的空隙里透出来，太阳月亮云还是实心的，整体像"浮动在壁纸上的一层天气"
  let cvs = document.getElementById('sky');
  const cldCvs = document.getElementById('cloud'), fxCvs = document.getElementById('fx');
  const veil = document.getElementById('veil');
  const sceneEl = document.getElementById('scene'), backEl = document.getElementById('backdrop');
  // 天空画布默认不透明（合成器可以跳过混合）。壁纸叠加时换成带 alpha 的画布，见 syncSkyAlpha
  let bctx = cvs.getContext('2d', { alpha: false });
  const cctx = cldCvs.getContext('2d'), fctx = fxCvs.getContext('2d');
  let ctx = bctx;
  const cardsEl = document.getElementById('cards');
  const host = window.chrome && window.chrome.webview;
  const post = m => { if (host) host.postMessage(m); };
  window.onerror = (msg, src, line) => post('log:' + msg + ' @' + line);

  // 天空画布：普通模式用不透明画布（合成器可以跳过混合，省一点 GPU）；壁纸叠加时换成带 alpha 的画布。
  // canvas 的 alpha 属性在第一次 getContext 时定死、之后改不了，只能整个换一个画布元素。
  let skyHasAlpha = false;
  // 底图消息到了没有：壁纸叠加在拿到第一份底图之前天空先按不透明画，免得启动那一下露出黑底（带图时解码期间底下是纯色近似垫着）
  let backReady = false;
  /// 换成带 / 不带 alpha 的天空画布。返回 true = 换了，调用方要 resize()（按当前尺寸重新分配、重画）。
  function syncSkyAlpha() {
    const want = !!L.overlayWall;
    if (want === skyHasAlpha) return false;
    skyHasAlpha = want;
    const old = cvs, nc = document.createElement('canvas');
    nc.id = old.id;
    old.parentNode.replaceChild(nc, old);
    cvs = nc;
    bctx = cvs.getContext('2d', { alpha: want });
    cvs.addEventListener('contextrestored', resize);
    skySigLast = ''; lastSky = -1e9;
    return true;
  }

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

  // cards：这块屏上的卡片 [{id, key, x, y}]（位置由宿主算好，CSS 像素）；自定义卡片是 {id, script, v, x, y, h}（v = 脚本文件的修改时间）；
  // scene：全屏天气画面跟着哪个城市（城市键）；defKey：默认卡片的城市键（自定义卡片的 api.weather 用它）
  // overlayWall：壁纸叠加——天空半透明、系统壁纸常驻垫底；skyOpacity：天空那层的不透明度（0.05–1）；
  // cloudAlpha：云的不透明度（极简模式不叠壁纸时恒为 1）
  const L = { primary: location.search.includes('primary'), ground: 0, overlay: false, panel: false, fps: 30, lowmem: false, minimal: false, overlayWall: false, skyOpacity: 0.45, cloudAlpha: 0.72, festive: true, scene: '', defKey: '', cards: [] };

  let W = 0, H = 0, DPR = 1;
  // weathers：各城市的天气（城市键 → 宿主发来的 weather 消息）；weather：画面城市的那一份（天空、太阳、雨雪都按它）
  const weathers = {};
  // 自定义卡片（cards.js）：脚本要天气时给默认卡片城市的那份（整理过的）。每画一轮都要：整理的结果只和这份天气、那个城市当地的钟点有关，
  // 两样都没变就用上次的（refresh = 1 秒的卡片一秒一次）
  const wxSum = { w: null, hour: 0, v: null };
  UserCards.init({ post, width: () => CARD_W, weather: () => {   // width：CARD_W 在下面才定义，画的时候再取
    const w = weathers[L.defKey];
    if (!w || !w.fc) return null;
    const hour = Math.floor((Date.now() + (w.fc.utc_offset_seconds || 0) * 1000) / 3600000);
    if (wxSum.w !== w || wxSum.hour !== hour) { const v = WX.summary(w, Date.now()); wxSum.w = w; wxSum.hour = hour; wxSum.v = v; }   // 整理出错（数据不全）不记下来
    return wxSum.v;
  } });
  let weather = null, preview = 'live', demo = null, paused = false;
  function pickWeather() {
    const w = weathers[L.scene] || null;
    if (w != null) { staleSince = 0; if (staleTm) { clearTimeout(staleTm); staleTm = 0; } }   // 当前画面城市有数据：stale 计时作废（含从无数据的城市切回来的情况）
    if (w === weather) return;
    // 新画面城市的天气还没到（第一次用这个城市、也没有缓存）：先留着旧城市的画面接着画，等数据到了再平滑切过去。
    // 最多等 2 分钟：一直拿不到（断网）就回到默认晴天，别无限期挂着别的城市的天气（卡片和托盘早就换成新城市了）。
    // 到点用定时器立刻切（没有它就得等下一轮 60 秒刷新，最长拖到近 3 分钟）
    if (w == null && weather != null) {
      // 又换到另一个也没数据的城市：从现在重新等，不沿用上一个城市的计时
      if (!staleSince || staleKey !== L.scene) {
        staleSince = Date.now(); staleKey = L.scene;
        if (staleTm) clearTimeout(staleTm);
        staleTm = setTimeout(() => { staleTm = 0; pickWeather(); }, 120000);
      }
      if (Date.now() - staleSince < 120000) return;
    }
    weather = w; dayCache.clear(); tgt = targetScene();
  }
  let staleSince = 0, staleTm = 0, staleKey = '';
  // 极简模式：fxKind = 宿主让播放的天气动画（start / rain / heavy / thunder / snow / rainbow，null = 不播）；
  // asleep = 天气画面整层已淡出并释放（画布缩成 1×1、主循环停掉），只剩系统壁纸 + 卡片
  let fxKind = null, asleep = false, sleepTm = 0, backdrop = null, backKey = '', backTok = 0;

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
    rainbow: { code: 1, cc: 20, time: 'bow', bow: true, label: '雨后彩虹' },
    // 节日预览：fest = 按哪个节日装饰，fw = 烟花密度（1 隔一阵一发 / 2 零点前后那样密），moon: 'full' = 按满月画
    cny: { code: 0, cc: 5, time: 'night', fest: 'cny', fw: 2, label: '春节' },
    lantern: { code: 0, cc: 5, time: 'night', fest: 'lantern', fw: 1, moon: 'full', label: '元宵节' },
    dragon: { code: 1, cc: 25, time: 'day', fest: 'dragon', label: '端午节' },
    midautumn: { code: 0, cc: 5, time: 'night', fest: 'moon', moon: 'full', label: '中秋节' },
    national: { code: 0, cc: 10, time: 'day', fest: 'national', label: '国庆节' },
    newyear: { code: 0, cc: 5, time: 'night', fest: 'newyear', fw: 2, label: '跨年' },
    xmas: { code: 73, cc: 100, wind: 6, time: 'night', fest: 'xmas', label: '圣诞节' },
    halloween: { code: 1, cc: 20, time: 'night', fest: 'halloween', label: '万圣节' },
    easter: { code: 1, cc: 15, time: 'day', fest: 'easter', label: '复活节' },
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
    s.bow = (pv ? pv.bow : fxKind === 'rainbow') ? 1 : 0;   // 雨后彩虹：预览，或宿主判断雨停转晴时播
    return s;
  }
  const cur = { cloud: 0.2, rain: 0, snow: 0, fog: 0, thunder: 0, dark: 0, windX: 0.2, bow: 0 };
  let tgt = targetScene(), firstScene = true;

  /// mode：天气预览（live = 实时天气）；day：一天快进演示（用 mode 的天气，从凌晨 4 点快进 24 小时，48 秒走一遍）
  function setPreview(mode, day) {
    const keepDemo = !!day && dayDemo && demo;   // 演示进行中只换天气，时间轴接着走
    const was = preview;
    // 自定义效果的预览（user:文件名）可能比效果脚本先到：先按实时天气，脚本注册时再切过去
    userPv = typeof mode === 'string' && mode.startsWith('user:') && !PREV[mode] ? { mode, day } : null;
    preview = PREV[mode] ? mode : 'live';
    // 预览里堆的雪人别带回实时画面：进预览时记下积雪，回到实时天气时还原
    if (was === 'live' && preview !== 'live') snowSaved = { acc: snowAcc, men: men.slice() };
    else if (was !== 'live' && preview === 'live' && snowSaved) { snowAcc = snowSaved.acc; snowSaved.men.forEach((m, i) => { men[i] = m; }); snowSaved = null; }
    if (preview !== 'live' && preview !== was) { snowAcc = 0; men.fill(null); }   // 每个预览都从光秃秃的地面开始：别把圣诞预览的雪人带进复活节
    festAt = -1e9;   // 节日马上按新的预览重新算
    flagPv = false;  // 再选一次国庆预览：再演示一遍升旗
    if (PREV[preview] && PREV[preview].fest) { sleighNext = simT + 2.5; batNext = simT + 2; }   // 预览只有几十秒：雪橇、蝙蝠马上就来
    dayDemo = !!day;
    const pv = PREV[preview], li = locInfo(), t = Date.now(), di = dayInfo(t, li);
    if (!keepDemo) demo = null;
    if (dayDemo) { if (!keepDemo) demo = { base: di.mid + 4 * 3600000, start: performance.now(), speed: 1800, end: 86400000 }; }
    else if (pv && pv.time === 'rise' && di.rise) demo = { base: di.rise - 12 * 60000, start: performance.now(), speed: 20 };
    else if (pv && pv.time === 'set' && di.set) demo = { base: di.set - 45 * 60000, start: performance.now(), speed: 20 };
    else if (pv && pv.time === 'night') demo = { base: nightAt(di, li), start: performance.now(), speed: 1 };
    else if (pv && pv.time === 'bow') demo = { base: bowAt(di, li), start: performance.now(), speed: 1 };
    else if (pv && pv.time === 'day') demo = { base: di.mid + 13.5 * 3600000, start: performance.now(), speed: 1 };   // 白天的节日装饰：下午一点半
    tgt = targetScene();
    renderPanel();
  }
  // 彩虹预览放在下午太阳降到 28° 左右的时候（真实的彩虹也多见于上午、傍晚太阳不高时）
  function bowAt(di, li) {
    if (di.rise == null || di.set == null) return Date.now();
    const noon = (di.rise + di.set) / 2;
    for (let t = noon; t < di.set; t += 600000) if (sunElev(t, li.lat, li.lon) < 28) return t;
    return noon;
  }
  // 晴夜预览默认看当晚 22:30；那时月亮已落下或还没升起，就换到当晚月亮最高的时刻，让预览里看得到今天的月相
  // （初一前后整夜无月，仍是 22:30）
  function nightAt(di, li) {
    const def = di.mid + 22.5 * 3600000;
    const alt = t => { const p = moonPos(t, li).p; return p > 0 && p < 1 ? Math.sin(Math.PI * p) : 0; };
    if (alt(def) > 0.3) return def;
    let best = def, bh = 0.3;
    for (let t = di.mid + 18 * 3600000; t <= di.mid + 30 * 3600000; t += 900000) {
      const h = alt(t);
      if (h > bh && sunElev(t, li.lat, li.lon) < -10) { best = t; bh = h; }
    }
    return best;
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
    d.l = (10 + 24 * d.z) * (0.6 + 0.5 * cur.rain + 0.3 * lightRain()) * H / 1080;   // 小雨的雨丝也拉长一点：太短的点画在灰底上看不出是雨
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

  // ------------------------------------------------------------------ 彩蛋：雪地 / 雪人 / 流星 / 节日
  // 都归设置里的「节日装饰与彩蛋」（Festive）管；效果预览里不管开没开都照常演示
  const qParam = k => new URLSearchParams(location.search).get(k);
  const eggsOn = () => L.festive || preview !== 'live';
  // 今天的节日（按天气所在城市的当地日期，见 fest.js）。预览里选了节日就只按预览的来
  let fest = { keys: [], fw: 0 }, festAt = -1e9;
  const DECOR = new Set(['cny', 'lantern', 'dragon', 'moon', 'national', 'halloween', 'xmas', 'easter']);   // 有装饰的节日（跨年只放烟花）
  const hasFest = k => fest.keys.includes(k);
  const festDecor = () => fest.keys.some(k => DECOR.has(k));
  const festBorn = {};   // 节日装饰出现的时刻（simT）：先"长"出来 / 灯笼从上面落下来
  function refreshFest() {
    const pv = PREV[preview];
    if (pv && pv.fest) fest = { keys: [pv.fest], fw: pv.fw || 0 };
    else if (!L.festive || !window.FEST) fest = { keys: [], fw: 0 };
    else fest = window.FEST.at(now() + locInfo().off * 1000);
    if (qParam('fest') && !(pv && pv.fest)) fest = { keys: qParam('fest').split(','), fw: +qParam('fw') || 0 };   // 调试：?fest=xmas,national&fw=1
    for (const k of fest.keys) if (festBorn[k] == null) festBorn[k] = simT;
    for (const k in festBorn) if (!fest.keys.includes(k)) delete festBorn[k];
    checkFlag();
    refreshUserFx();
  }
  const pvFest = () => !!(PREV[preview] && PREV[preview].fest);

  // 降雪积累：下雪时按强度攒"降雪秒数"（地面先铺出雪地，攒得够久陆续堆起小雪人），停雪后慢慢消融，雪人也跟着化掉
  let snowAcc = 0, snowSaved = null;
  const SNOW_AT = [300, 660, 1020];   // 攒到多少"降雪秒数"堆第 1 / 2 / 3 个雪人（约 5 / 11 / 17 分钟的雪）；圣诞节快三倍多
  const SNOW_MELT = 0.45;             // 停雪后每秒消掉的量，约为积累速度的一半：雪停了雪人还在，过一阵才化
  const men = [null, null, null];     // 三个雪人坑位（按堆出来的顺序站左 / 中 / 右）
  const snowFast = qParam('snowfast') === '1' ? 30 : 1;   // 调试：积累 / 消融 30 倍速，方便看效果
  const snowCover = () => smooth(45, 420, snowAcc);
  const decorOn = () => snowAcc > 2 || !!(men[0] || men[1] || men[2]) || festDecor() || userFxOn();   // 地上有东西：前景层保持分配、低频重画
  const easeBack = t => { const c = 1.6, u = t - 1; return 1 + u * u * ((c + 1) * u + c); };   // 雪人从雪地里冒出来时带一点回弹
  // easeBack 的反函数（[0, 0.59] 单调上升段，雪人复活倒推生长进度用；y 最大 1，段内够用）
  const backOf = y => { let a = 0, b = 0.59; for (let i = 0; i < 22; i++) { const t = (a + b) / 2; if (easeBack(t) < y) a = t; else b = t; } return (a + b) / 2; };
  const growK = m => m.dying ? Math.max(0, 1 - (simT - m.dying) / 9) : Math.min(1, (simT - m.born) / 3.5);
  function stepEaster(dt) {
    if (!eggsOn()) { snowAcc = 0; men.fill(null); return; }
    const fast = snowFast * (preview !== 'live' ? 20 : 1);   // 预览只有几十秒：雪人堆得快一些才看得到
    snowAcc = clamp(snowAcc + (cur.snow > 0.04 ? dt * (0.4 + 0.6 * cur.snow) : -dt * SNOW_MELT) * fast, 0, 1400);
    const xk = hasFest('xmas') ? 0.3 : 1;   // 圣诞节：一下雪很快就有雪人
    for (let i = 0; i < 3; i++) {
      const at = SNOW_AT[i] * xk;
      if (!men[i]) {
        if (snowAcc >= at) { men[i] = { xf: [0.22, 0.53, 0.79][i] + (rand() - 0.5) * 0.1, z: 0.55 + 0.45 * rand(), born: simT, dying: 0 }; wake(); }
      } else if (men[i].dying) {
        if (snowAcc >= at) {
          // 又下回来了：接着化掉时的样子长回去。化着的高度是 size×(0.35+0.65g)、透明度是 g，
          // 生长分支画的是 size×easeBack(gk)、透明度 1——高度用反函数倒推 born 对齐，透明度和脚下的水洼走 1.2 秒过渡（画的地方看 fadeAt）
          const m = men[i], g = Math.max(0, 1 - (simT - m.dying) / 9);
          m.born = simT - 3.5 * backOf(0.35 + 0.65 * g);
          m.fadeAt = simT; m.fadeFrom = g;
          m.dying = 0;
        }
        else if (simT - men[i].dying > 9) men[i] = null;  // 化完，收走
      } else if (snowAcc < at - 90 * xk) men[i].dying = simT;  // 消融到阈值以下：开始化（留一点迟滞，别在阈值边上反复横跳）
    }
  }
  // 流星：晴朗的夜隔一阵子划一颗。星空夜本来就要 300ms 一档重画闪烁的星星，只有飞行中的零点几秒才需要满帧
  const meteorAsk = qParam('meteor') === '1';   // 调试：条件一满足立刻先划一颗
  let meteor = null, meteorNext = meteorAsk ? 0 : 45 + rand() * 150;
  function stepMeteor(dt) {
    if (meteor) {
      meteor.t += dt;
      if (meteor.t >= meteor.dur) { meteor = null; meteorNext = simT + 100 + rand() * 260; }
      return;
    }
    if (simT < meteorNext || !eggsOn()) return;
    if (lastStarA > 0.45 && cur.cloud < 0.35 && cur.fog < 0.2 && cur.rain < 0.05 && cur.snow < 0.04) {
      const dir = rand() < 0.5 ? 1 : -1;   // 往右下或左下划（ang 只给向下的斜率，水平方向由 dir 翻转——cos 是偶函数，直接乘进角一半流星会往上飞）
      meteor = { t: 0, dur: 0.75 + rand() * 0.4, x0: W * (dir > 0 ? 0.12 + rand() * 0.6 : 0.3 + rand() * 0.58), y0: H * (0.04 + rand() * 0.26), len: W * (0.09 + rand() * 0.06), ang: 0.3 + rand() * 0.22, dir };
      wake();   // 马上按满帧画它
    } else meteorNext = simT + (meteorAsk ? 1 : 20);   // 阴天 / 雨雪 / 天亮了：过一会儿再看
  }

  // 烟花：春节每晚、元宵、国庆、跨年的夜里（时段见 fest.js）。下雨打雷不放；只有空中有烟花的那几秒才满帧
  const rockets = [], bursts = [], flashes = [];
  let fwNext = 0;
  const FW_COLORS = ['#ff5a5a', '#ffd25a', '#7dff8a', '#5ac8ff', '#c78bff', '#ffffff', '#ffa04d', '#ff7ad9'];
  function launch(x, apex) {   // x / apex：从哪升空、在多高炸开（不填就随机；自定义效果的 fx.firework 会填）
    const k = H / 1080, hz = horizonY(), dur = 1 + rand() * 0.5;
    if (apex == null) apex = H * (0.12 + rand() * 0.3);
    const vy = -2 * (hz - apex) / dur;   // 匀减速升到最高点
    rockets.push({ x: x != null ? x : W * (0.12 + rand() * 0.76), y: hz, vx: x != null ? 0 : (rand() - 0.5) * 30 * k, vy, ay: -vy / dur, t: 0, dur });
  }
  function explode(r) {
    const k = H / 1080, kind = rand(), n = kind < 0.25 ? 40 : 64;
    const c1 = FW_COLORS[(rand() * FW_COLORS.length) | 0], c2 = FW_COLORS[(rand() * FW_COLORS.length) | 0];
    const willow = kind > 0.8, ring = kind < 0.25, v0 = (180 + 110 * rand()) * k;   // 炸开后火花能飞出 1080p 的 130–200 像素
    // 一发烟花拆成一两组同色火花，各自整组画（一组一次 stroke，几百个火花也只要几次绘制调用）
    const groups = willow ? [{ c: '#ffcf6a', p: [] }] : c1 !== c2 && rand() < 0.4 ? [{ c: c1, p: [] }, { c: c2, p: [] }] : [{ c: c1, p: [] }];
    for (let i = 0; i < n; i++) {
      const a = ring ? TAU * i / n : rand() * TAU, v = ring ? v0 : v0 * (0.35 + 0.65 * Math.sqrt(rand()));
      groups[i % groups.length].p.push({ x: r.x, y: r.y, vx: Math.cos(a) * v, vy: Math.sin(a) * v });
    }
    for (const g of groups) bursts.push({ c: g.c, p: g.p, t: 0, life: willow ? 2.8 : 1.6 + rand() * 0.6, drag: willow ? 2.2 : 1.4, grav: (willow ? 55 : 38) * k, willow });
    flashes.push({ x: r.x, y: r.y, t: 0, c: groups[0].c });
  }
  function stepFireworks(dt) {
    const want = (eggsOn() || userFxOn()) && fest.fw > 0 && lastDaylight < 0.35 && cur.rain < 0.15 && cur.thunder < 0.3;   // 自定义效果要的烟花不归「节日装饰与彩蛋」开关管
    if (want && simT >= fwNext && rockets.length < 6) {
      launch(); if (rand() < (fest.fw >= 2 ? 0.45 : 0.18)) launch();
      fwNext = simT + (fest.fw >= 2 ? 0.35 + rand() * 1.1 : 2.2 + rand() * 5);
      wake();
    }
    for (let i = rockets.length - 1; i >= 0; i--) {
      const r = rockets[i];
      r.t += dt; r.x += r.vx * dt; r.y += r.vy * dt; r.vy += r.ay * dt;
      if (r.t >= r.dur) { explode(r); rockets[i] = rockets[rockets.length - 1]; rockets.pop(); }
    }
    for (let i = bursts.length - 1; i >= 0; i--) {
      const b = bursts[i];
      b.t += dt;
      const dk = Math.exp(-dt * b.drag);
      for (const p of b.p) { p.vx *= dk; p.vy = p.vy * dk + b.grav * dt; p.x += p.vx * dt; p.y += p.vy * dt; }
      if (b.t >= b.life) { bursts[i] = bursts[bursts.length - 1]; bursts.pop(); }
    }
    for (let i = flashes.length - 1; i >= 0; i--) if ((flashes[i].t += dt) > 0.6) { flashes[i] = flashes[flashes.length - 1]; flashes.pop(); }
  }

  // 圣诞雪橇：平安夜和圣诞节的夜里，隔几分钟从天上飞过一趟；月亮在天上时正好从月亮前面掠过
  let sleigh = null, sleighNext = 8 + rand() * 40;
  const dust = [];   // 雪橇后面撒的金粉
  let lastMoon = null, lastMoonV = 0;
  function stepSleigh(dt) {
    for (let i = dust.length - 1; i >= 0; i--) { const d = dust[i]; d.t += dt; d.y += 14 * dt * H / 1080; if (d.t > 1.4) { dust[i] = dust[dust.length - 1]; dust.pop(); } }
    if (sleigh) {
      sleigh.t += dt;
      const p = sleighAt(sleigh);
      if (dust.length < 90) dust.push({ x: p.x - p.dir * 42 * H / 1080 + (rand() - 0.5) * 8, y: p.y + (rand() - 0.2) * 8, t: 0 });
      if (sleigh.t >= sleigh.dur) { sleigh = null; sleighNext = simT + (pvFest() ? 14 : 150 + rand() * 210); }
      return;
    }
    if (!eggsOn() || !hasFest('xmas') || lastDaylight > 0.25 || simT < sleighNext) return;
    const dir = rand() < 0.5 ? 1 : -1, moonUp = lastMoon && lastMoonV > 0.3 && lastMoon.y < horizonY() * 0.8;
    const xm = moonUp ? lastMoon.x : W * (0.3 + rand() * 0.4), ym = moonUp ? lastMoon.y : H * (0.16 + rand() * 0.14);
    const um = Math.max(xm / W + 0.12, 1.12 - xm / W);   // 抛物线两头离最高点最远的横向距离
    sleigh = { t: 0, dur: 10 + rand() * 2, dir, xm, ym, cv: Math.min(H * 0.5, Math.max(0, horizonY() * 0.85 - ym) / (um * um)) };   // 月亮低的时候弧度放平：两头别扎到地平线下面
    wake();
  }
  function sleighAt(s) {
    const p = s.t / s.dur, x = s.dir > 0 ? -0.12 * W + 1.24 * W * p : 1.12 * W - 1.24 * W * p;
    const u = (x - s.xm) / W;
    return { x, y: s.ym + s.cv * u * u + Math.sin(s.t * 3) * 2 * H / 1080, dir: s.dir };   // 抛物线：越近月亮飞得越高，正好穿过月亮
  }

  // 万圣节：夜里时不时飞过一群蝙蝠
  let bats = null, batNext = 6 + rand() * 30;
  function stepBats(dt) {
    if (bats) { bats.t += dt; if (bats.t >= bats.dur) { bats = null; batNext = simT + (pvFest() ? 10 : 70 + rand() * 120); } return; }
    if (!eggsOn() || !hasFest('halloween') || lastDaylight > 0.3 || simT < batNext) return;
    const list = [];
    for (let i = 0; i < 7; i++) list.push({ dx: (rand() - 0.5) * 0.12, dy: (rand() - 0.5) * 0.09, ph: rand() * TAU, s: 0.7 + rand() * 0.6, wob: rand() * TAU });
    bats = { t: 0, dur: 7 + rand() * 2, dir: rand() < 0.5 ? 1 : -1, y0: H * (0.14 + rand() * 0.2), list };
    wake();
  }
  function stepFest(dt) {
    if (simT - festAt > (demo && demo.speed > 1 ? 0.5 : 20)) { festAt = simT; refreshFest(); }
    stepFireworks(dt); stepSleigh(dt); stepBats(dt);
  }
  const skyFxBusy = () => rockets.length > 0 || bursts.length > 0 || flashes.length > 0 || !!sleigh || dust.length > 0 || !!bats;

  // ------------------------------------------------------------------ 自定义效果
  // 用户自己写的效果脚本放在 %APPDATA%\WeatherWallpaper\effects\*.js（宿主映射成 https://effects.local/，文件列表随 layout 发来）。
  // 脚本里调 WeatherWallpaper.effect({ name, dates, draw(ctx, fx) { … } })，到了日子每帧调它的 draw，画在前景层（雨雪下面、卡片下面）。
  // 写法见那个文件夹里的「生日示例.js」和 README。脚本出错只停掉它自己，不影响壁纸
  const userFx = [];   // { file, def, name, on, born, state, ready, errs, dead, lastT }
  let userFxSig = null, userPv = null;   // userFxSig：已加载的文件列表（变了就整页重载）；userPv：预览指令比脚本先到时记下，注册后再切
  const userFxOn = () => userFx.some(u => u.on && !u.dead);
  const userFxFps = () => userFx.reduce((m, u) => u.on && !u.dead ? Math.max(m, clamp(+u.def.fps || 30, 1, 60)) : m, 0);
  const userFxFast = () => userFxFps() >= 20;   // 要 20 帧以上的走 rAF 满帧；低于的走定时器
  const fxLog = (file, msg) => { post('log:effect ' + file + ': ' + msg); console.warn('[effect ' + file + '] ' + msg); };
  function loadUserFx(list) {
    for (const e of list) {
      const s = document.createElement('script');
      s.src = (qParam('fxbase') || 'https://effects.local/') + encodeURIComponent(e.f) + '.js?v=' + e.v;   // v = 修改时间：改过的文件不吃缓存；fxbase 是浏览器调试用
      s.dataset.fx = e.f;
      s.onerror = () => fxLog(e.f, '脚本加载失败');
      // 跨域脚本的语法错误页面拿不到详情（只有 "Script error."）：跑完了却没注册，多半是写错了
      s.onload = () => { if (!userFx.some(u => u.file === e.f)) fxLog(e.f, '脚本没有调用 WeatherWallpaper.effect(...)（多半是有语法错误：用 VS Code 之类的编辑器打开看看哪里标红）'); };
      document.body.appendChild(s);
    }
  }
  window.WeatherWallpaper = {
    version: 1,
    effect(def) {
      const sc = document.currentScript, file = (sc && sc.dataset.fx) || 'effect' + (userFx.length + 1);
      if (!def || typeof def.draw !== 'function') { fxLog(file, 'WeatherWallpaper.effect() 要传一个带 draw(ctx, fx) 函数的对象'); return; }
      const i = userFx.findIndex(u => u.file === file);
      if (i >= 0) userFx.splice(i, 1);   // 同一个文件注册两次：后一次算数
      const name = String(def.name || file);
      userFx.push({ file, def, name, on: false, born: 0, state: {}, ready: false, errs: 0, dead: false, lastT: null });
      // 托盘「效果预览 → 自定义」演示时的天气和时间：preview: { time: 'night' | 'day', weather: 'clear' | 'snow' | … }，默认晴朗的夜晚
      const pv = def.preview || {}, day = pv.time === 'day', base = (PREV[pv.weather] && !PREV[pv.weather].fest && PREV[pv.weather]) || PREV[day ? 'clear' : 'night'];
      PREV['user:' + file] = { code: base.code, cc: base.cc, wind: base.wind, time: day ? 'day' : 'night', fest: 'user:' + file, fw: clamp(def.fireworks | 0, 0, 2), label: name };
      if (userPv && userPv.mode === 'user:' + file) { const p = userPv; setPreview(p.mode, p.day); }
      festAt = -1e9; wake();
    },
  };
  // 当地日期（天气所在城市）：给 dates / when 判断用，也原样交给 draw 的 fx.date
  function fxDate() {
    const ms = now() + locInfo().off * 1000, d = new Date(ms);
    let lun;
    return {
      year: d.getUTCFullYear(), month: d.getUTCMonth() + 1, day: d.getUTCDate(), weekday: d.getUTCDay(), hour: d.getUTCHours(), minute: d.getUTCMinutes(),
      get lunar() { return lun !== undefined ? lun : (lun = window.FEST && FEST.lunar ? FEST.lunar(ms) : null); },   // { month, day, leap }，用到才算
    };
  }
  // dates 里每一项：'03-15' 每年公历 3 月 15 日；'2027-01-01' 只那一天；'lunar:08-15' / '农历08-15' 每年农历八月十五（闰月不算）
  function userMatch(def, d) {
    for (const s0 of Array.isArray(def.dates) ? def.dates : [def.dates]) {
      const s = String(s0 == null ? '' : s0).trim();
      let m;
      if ((m = /^(\d{4})-(\d{1,2})-(\d{1,2})$/.exec(s))) { if (+m[1] === d.year && +m[2] === d.month && +m[3] === d.day) return true; }
      else if ((m = /^(\d{1,2})-(\d{1,2})$/.exec(s))) { if (+m[1] === d.month && +m[2] === d.day) return true; }
      else if ((m = /^(?:lunar:|农历)\s*(\d{1,2})-(\d{1,2})$/i.exec(s))) { const l = d.lunar; if (l && !l.leap && l.month === +m[1] && l.day === +m[2]) return true; }
    }
    return typeof def.when === 'function' && !!def.when(d);
  }
  function userErr(u, e) {
    u.errs++;
    fxLog(u.file, String((e && e.stack) || e).split('\n').slice(0, 2).join(' | '));
    if (u.errs >= 3) { u.dead = true; fxLog(u.file, '连续出错，先停用（改好保存后壁纸会自动重新加载）'); }
  }
  // 跟着节日一起刷新（refreshFest 里调）：今天哪些自定义效果该出现。预览里只演示选中的那个
  function refreshUserFx() {
    if (!userFx.length) return;
    const pv = PREV[preview], pvKey = pv && pv.fest ? pv.fest : null, d = fxDate();
    for (const u of userFx) {
      let on = false;
      if (pvKey) on = pvKey === 'user:' + u.file;
      else if (!u.dead) { try { on = userMatch(u.def, d); } catch (e) { userErr(u, e); } }
      if (on && !u.on) { u.on = true; u.born = simT; u.state = {}; u.ready = false; u.lastT = null; wake(); }
      else if (!on) u.on = false;
      if (u.on && !pvKey && u.def.fireworks) fest.fw = Math.max(fest.fw, clamp(u.def.fireworks | 0, 0, 2));
    }
  }
  const okColor = c => typeof c === 'string' && /^#[0-9a-f]{6}$/i.test(c);
  const userGlowCache = new Map();   // 用户效果 fx.glow 的发光贴图：和内置的分开（见 fxApi 里的说明）
  // 交给效果脚本的 fx：尺寸、时间、天气、日期、卡片位置，外加几个画图帮手
  function fxApi(u, daylight) {
    const dt = u.lastT == null ? 0 : clamp(simT - u.lastT, 0, 0.25);   // 隔了很久才画一帧（暂停后恢复）：别让动画一下跳出去老远
    u.lastT = simT;
    return {
      t: simT - u.born, dt, time: simT,                   // t：这个效果出现了多少秒（做入场动画）；time：全局动画时钟
      W, H, k: H / 1080, ground: horizonY(),              // 屏幕 CSS 像素；按 1080p 设计的尺寸乘 k；ground = 地平线（任务栏顶 / 屏幕底）
      night: 1 - daylight, daylight,                      // 0 白天 … 1 深夜
      weather: { rain: cur.rain, snow: cur.snow, cloud: cur.cloud, fog: cur.fog, thunder: cur.thunder, wind: cur.windX },   // 0–1；wind 正 = 往右吹
      date: fxDate(), preview: preview !== 'live',
      cards: shownCards().map(c => ({ x: c.x, y: c.y, w: CARD_W, h: cardH(c) })),
      state: u.state,                                     // 这个效果自己的存储（每次出现时清空）
      free: (x, w, y0, y1) => placeX(x, w, y0, y1),       // 宽 w 的东西在 [y0, y1] 这段高度上、离 x 最近又不被卡片挡住的中心 x
      glow: (c, x, y, r, a = 1) => {                      // 柔光（叠加发光）；颜色写 #rrggbb
        if (!okColor(c)) return;
        // 用户脚本的发光单独缓存：它要是一直换颜色，清的也只是自己那份，内置装饰（灯笼 / 彩灯 / 烟花…）的贴图不受影响
        if (userGlowCache.size > 96 && !userGlowCache.has(c)) userGlowCache.clear();
        const op = ctx.globalCompositeOperation; ctx.globalCompositeOperation = 'lighter'; glowAt(c, x, y, r, a, userGlowCache); ctx.globalCompositeOperation = op;
      },
      shade: (c, amount) => okColor(c) ? sh(c, amount == null ? (1 - daylight) * 0.5 : amount) : c,   // 夜里压暗的颜色（和内置装饰同一套夜色）
      firework: (x, y) => { if (rockets.length < 12) { launch(x, y != null ? Math.min(y, horizonY() - 60) : null); wake(); } },   // 在 x 处放一发、在 y 高度炸开（都可不填）
      rand,
    };
  }
  function drawUserFx(daylight) {
    for (const u of userFx) {
      if (!u.on || u.dead) continue;
      ctx.save();
      try {
        const api = fxApi(u, daylight);
        if (!u.ready) { u.ready = true; if (typeof u.def.setup === 'function') u.def.setup(api); }
        u.def.draw(ctx, api);
        u.errs = 0;
      } catch (e) { userErr(u, e); }
      ctx.restore();
    }
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
    stepEaster(dt);
    stepMeteor(dt);
    stepFest(dt);
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

    if (simT - lastScenePost > 1) postScene();
    if (simT - panelT > 60) { panelT = simT; renderPanel(); }
  }
  // 告诉宿主当前的雨雪（任务栏 / 屋檐效果跟着它走）和托盘提示文字。极简模式没在播放时报 0，任务栏和屋檐也一起歇着
  function postScene() {
    if (!L.primary) { lastSceneMsg = ''; return; }   // 不归这块屏报了：忘掉上次报的，以后重新轮到它时第一份一定发出去（宿主那边已经是别的屏报的值）
    lastScenePost = simT;
    let text = '';
    if (weather) {
      const c = weather.fc.current;
      text = `${weather.loc.name} ${WX.info(c.weather_code).text} ${Math.round(c.temperature_2m)}°`;
    }
    const on = sceneWanted();
    const msg = JSON.stringify({ t: 'scene', rain: on ? +tgt.rain.toFixed(2) : 0, snow: on ? +tgt.snow.toFixed(2) : 0, wind: +tgt.windX.toFixed(2), text });
    if (msg !== lastSceneMsg) { lastSceneMsg = msg; post(JSON.parse(msg)); }
  }
  let firstFill = true;

  // ------------------------------------------------------------------ 绘制
  // 暂停时的静态帧 / 录制逐帧：每一层都强制画一遍；前景画布也要按需分配，否则雨雪画在 1×1 的隐藏画布上
  function draw() {
    const st = bgState();
    updateBgRes(st, true);
    drawSky(st); drawClouds(st); updateVeil();
    const on = fxNeeded() || decorOn(); fxAlloc(on); fxDirty = on; if (on) drawFx();
  }
  // 天空画布分辨率自适应：有星星 / 月亮 / 清晰的太阳时用全分辨率；阴天、雨雪、雾等画面本来就柔和，用半分辨率省显存
  let bgK = 1, wantK = 1, wantSince = 0;
  function bgAlloc(k) {
    bgK = k;
    cvs.width = Math.round(W * DPR * k); cvs.height = Math.round(H * DPR * k);
    bctx.setTransform(DPR * k, 0, 0, DPR * k, 0, 0);
  }
  // 返回 true 表示刚重新分配过：画布已被清空，调用方必须马上重画天空，否则会露出一帧黑底
  function updateBgRes(st, force) {
    const k = st.starA > 0.01 || st.moonV > 0.05 || st.sunV > 0.25 ? 1 : 0.5;
    if (k !== wantK) { wantK = k; wantSince = performance.now(); }
    if (wantK === bgK || !(force || performance.now() - wantSince > 3000)) return false;   // 持续 3 秒才切换，避免临界处来回抖
    bgAlloc(wantK);
    return true;
  }
  // 云画布固定半分辨率：云本来就是软边渐变，放大一倍肉眼看不出，显存只要 1/4；没有云时缩成 1×1 并隐藏
  const CK = 0.5;
  let cloudOn = false;
  function cloudAlloc(on) {
    if (on === cloudOn) return;
    cloudOn = on;
    cldCvs.width = on ? Math.round(W * DPR * CK) : 1; cldCvs.height = on ? Math.round(H * DPR * CK) : 1;
    cldCvs.style.display = on ? '' : 'none';
    if (on) cctx.setTransform(DPR * CK, 0, 0, DPR * CK, 0, 0);
  }
  function cloudsVisible() {
    if (cur.fog > 0.01) return true;
    for (const c of clouds) if (c.a >= 0.01) return true;
    return false;
  }

  // 天空 / 云共用的派生量，每个循环周期算一次
  function bgState() {
    const li = locInfo(), t = now();
    const sp = sunPos(t, li), e = sp.e;
    const hz = horizonY();
    const daylight = smooth(-6, 8, e);
    const golden = smooth(-5, 0, e) * (1 - smooth(5, 15, e));
    const storm = clamp(cur.rain * 0.7 + cur.thunder * 0.3 + cur.dark, 0, 1);
    const vis = (1 - smooth(0.45, 1, cur.cloud) * 0.93) * (1 - cur.fog * 0.6);
    const starA = (1 - smooth(-14, -5, e)) * (1 - smooth(0.25, 0.75, cur.cloud)) * (1 - cur.fog);
    const mp = moonPos(t, li);
    const moonV = e < -2 && mp.p > 0 && mp.p < 1
      ? (1 - smooth(-6, -2, e)) * smooth(0, 0.04, mp.p) * (1 - smooth(0.96, 1, mp.p)) * (1 - smooth(0.55, 1, cur.cloud) * 0.9) * (1 - cur.fog * 0.6) : 0;
    const sunV = vis * smooth(-4, 0.5, e);
    const rays = sunV > 0.3 && (1 - smooth(1, 22, e)) < 0.97;   // 与 drawSun 画光芒的条件一致
    lastDaylight = daylight;
    lastStarA = starA;
    lastMoon = mp; lastMoonV = moonV;   // 圣诞雪橇要从月亮前面飞过
    // 云的颜色：下雨的云是乌云——只要在下雨，至少往乌云色靠一半（毛毛雨按 storm 算只有 0.2 左右，云还是白的），雨越大越暗
    const rainCloud = Math.max(storm, 0.5 * smooth(0.02, 0.18, cur.rain) + 0.5 * storm);
    return { li, t, sp, e, hz, daylight, golden, storm, vis, starA, mp, moonV, sunV, rays, cc: cloudColors(e, daylight, golden, rainCloud) };
  }

  // 天空画面的"指纹"：只取肉眼分辨得出的精度。指纹没变就不重画，白天晴空一分钟也画不了几次。
  // 星星闪烁、日芒缓慢转动是仅有的持续动画，按 0.3 / 0.5 秒一档计入指纹。
  function skySig(st) {
    const q = v => Math.round(v * 200);
    return [bgK, W, H, Math.round(L.ground * 1000), L.lowmem ? 1 : 0, Math.round(st.e * 8), L.overlayWall ? q(L.skyOpacity) : 0,
      q(cur.cloud), q(cur.fog), q(cur.dark), q(cur.rain), q(cur.thunder), q(cur.bow), Math.round(st.sp.x), Math.round(st.sp.y),
      st.moonV > 0 ? q(st.moonV) + ':' + Math.floor(st.t / 30000) : 0,
      st.rays ? Math.floor(simT / 0.5) : 0, st.starA > 0.01 ? Math.floor(simT / 0.3) : 0].join();
  }

  let skySigLast = '';
  function drawSky(st) {
    ctx = bctx;
    const { li, t, sp, e, hz, daylight, golden, storm, vis, starA, moonV } = st;
    skySigLast = skySig(st);
    // 壁纸叠加：天空底色只占 skyA，壁纸从里面透出来；日月、云、雨雪照常不透明（天气效果本身要看得清）
    const skyA = L.overlayWall && backReady ? clamp(L.skyOpacity, 0.05, 1) : 1;
    if (skyHasAlpha) ctx.clearRect(0, 0, W, H);   // 带 alpha 的画布不会自动擦掉上一帧

    // 天空
    let [top, mid, bot] = skyAt(e);
    const ovAmt = clamp(Math.pow(cur.cloud, 1.6) * 0.82 + cur.dark * 0.25 + cur.fog * 0.3, 0, 0.95);
    const ovTop = mix(OV_NIGHT[0], OV_DAY[0], daylight), ovMid = mix(OV_NIGHT[1], OV_DAY[1], daylight), ovBot = mix(OV_NIGHT[2], OV_DAY[2], daylight);
    const darkK = 1 - cur.dark * 0.55;
    top = mix(top, ovTop, ovAmt).map(v => v * darkK); mid = mix(mid, ovMid, ovAmt).map(v => v * darkK); bot = mix(bot, ovBot, ovAmt).map(v => v * (1 - cur.dark * 0.4));
    const sg = ctx.createLinearGradient(0, 0, 0, hz);
    sg.addColorStop(0, css(top, skyA)); sg.addColorStop(0.55, css(mid, skyA)); sg.addColorStop(1, css(bot, skyA));
    ctx.globalCompositeOperation = 'source-over'; ctx.globalAlpha = 1;
    ctx.fillStyle = sg; ctx.fillRect(0, 0, W, hz);
    ctx.fillStyle = css(bot, skyA); ctx.fillRect(0, hz - 1, W, H - hz + 1);

    // 日出日落地平线辉光
    const glowAmt = smooth(-11, -1, e) * (1 - smooth(4, 14, e)) * (1 - ovAmt * 0.8);
    if (glowAmt > 0.01) {
      ctx.save();
      ctx.translate(clamp(sp.x, -W * 0.1, W * 1.1), hz);
      ctx.scale(1, 0.42);
      const R = W * 0.75;
      const gg = ctx.createRadialGradient(0, 0, 0, 0, 0, R);
      const gc = mix(hex('#ff6a2b'), hex('#ffb066'), smooth(-4, 6, e));
      gg.addColorStop(0, css(gc, 0.65 * glowAmt * skyA)); gg.addColorStop(0.35, css(gc, 0.25 * glowAmt * skyA)); gg.addColorStop(1, css(gc, 0));
      ctx.fillStyle = gg; ctx.fillRect(-R, -R, 2 * R, 2 * R);
      ctx.restore();
    }

    // 星星（闪烁放慢一半：整屏每 0.3 秒才重画一次，慢速闪烁在低刷新下依然平滑）
    if (starA > 0.01) {
      ctx.fillStyle = '#fff';
      for (const s of stars) {
        if (s.y > hz) continue;
        ctx.globalAlpha = starA * (0.55 + 0.45 * Math.sin(simT * s.f * 0.5 + s.p)) * (1 - s.y / hz * 0.6);
        ctx.fillRect(s.x, s.y, s.r, s.r);
      }
      ctx.globalAlpha = 1;
    }

    // 月亮
    if (moonV > 0) drawMoon(st.mp, moonV);
    // 太阳
    if (e > -4) drawSun(sp, vis);
    // 雨后彩虹（画在天空层，云层盖在上面会自然挡住一部分）
    if (cur.bow > 0.01) drawRainbow(st);

    // 阴天时顶部压一层云色（云画布盖在它上面）。壁纸叠加时这层也按 skyA 减淡：不然阴天会把壁纸整片盖灰
    const cc = st.cc, ov = smooth(0.65, 1, cur.cloud);
    if (ov > 0.01) {
      const og = ctx.createLinearGradient(0, 0, 0, hz * 0.85);
      og.addColorStop(0, css(cc[1], ov * 0.8 * skyA)); og.addColorStop(0.6, css(mix(cc[0], cc[1], 0.5), ov * 0.35 * skyA)); og.addColorStop(1, css(cc[0], 0));
      ctx.fillStyle = og; ctx.fillRect(0, 0, W, hz);
    }
  }

  // 云层：云朵 + 雾（雾的渐变和飘动的雾团）。半分辨率透明画布，没有云 / 雾时不存在。
  function drawClouds(st) {
    const on = cloudsVisible();
    cloudAlloc(on);
    if (!on) return;
    ctx = cctx;
    ctx.clearRect(0, 0, W, H);
    recolorSprites(st.cc[0], st.cc[1], performance.now());
    const sx = W / 1920;
    // 壁纸叠加：云也半透明一点，壁纸从云后面透出来（云太实的话整片天就又被盖住了）
    const cloudA = L.overlayWall && backReady && !L.minimal ? clamp(L.cloudAlpha, 0.1, 1) : 1;
    for (const layer of [3, 0, 1, 2]) {   // 层云垫底，然后由远及近
      for (const c of clouds) {
        if (c.layer !== layer || c.a < 0.01) continue;
        const w = SW * c.sc * sx, h = SH * c.sc * sx, x = c.fx * W;
        if (x > W || x + w < 0) continue;   // 在屏幕外的不画
        ctx.globalAlpha = c.a * cloudA;
        ctx.drawImage(sprites[c.spr], x, c.fy * H, w, h);
      }
    }
    ctx.globalAlpha = 1;
    if (cur.fog > 0.01) {
      const fc = mix(hex('#3a3f47'), hex('#d5d9de'), st.daylight);
      const fg = ctx.createLinearGradient(0, H * 0.15, 0, H);
      fg.addColorStop(0, css(fc, cur.fog * 0.25 * cloudA)); fg.addColorStop(1, css(fc, cur.fog * 0.75 * cloudA));
      ctx.fillStyle = fg; ctx.fillRect(0, 0, W, H);
      for (let i = 0; i < 5; i++) {
        const x = ((simT * (6 + i * 3) / W + i * 0.27) % 1.6 - 0.4) * W;
        ctx.globalAlpha = cur.fog * 0.55 * cloudA;
        ctx.drawImage(sprites[8 + i % 4], x, H * (0.45 + i * 0.09), W * 0.9, H * 0.35);
      }
      ctx.globalAlpha = 1;
    }
  }

  // 雨天压暗 / 雾霾：盖在云上面的整屏渐变，直接交给 CSS（合成器画，不占画布），内容变了才写样式
  let aqiW = null, aqiV = null, veilKey = '';
  function aqiNow() {
    if (weather !== aqiW) { aqiW = weather; aqiV = weather ? WX.chinaAqi(weather.aq && weather.aq.current) : null; }
    return weather && preview === 'live' ? aqiV : null;
  }
  function updateVeil() {
    const parts = [];
    // 壁纸叠加时压暗也减淡一些：雨天的阴沉还在，但底下的壁纸不至于被整片压黑
    const k = L.overlayWall && backReady ? 0.5 + 0.5 * clamp(L.skyOpacity, 0.05, 1) : 1;
    const aqi = aqiNow();
    if (aqi > 100) {
      const ha = Math.min(0.3, (aqi - 100) / 500);
      parts.push(`linear-gradient(rgba(190,175,150,${(ha * 0.4 * k).toFixed(3)}),rgba(190,175,150,${(ha * k).toFixed(3)}))`);
    }
    if (cur.dark > 0.01) parts.push(`linear-gradient(rgba(6,8,14,${(cur.dark * 0.5 * k).toFixed(3)}),rgba(6,8,14,${(cur.dark * 0.15 * k).toFixed(3)}))`);
    const key = parts.join(',');
    if (key !== veilKey) { veilKey = key; veil.style.background = key; }
  }

  // 前景层：降水与闪电（透明画布，按设定帧率绘制；没有降水时整层停掉）
  let lastDaylight = 1, lastStarA = 0;
  function drawFx() {
    ctx = fctx;
    ctx.clearRect(0, 0, W, H);
    const daylight = lastDaylight;
    // 天上的远景（流星、烟花、雪橇、蝙蝠）垫在最底下；流星只在晴朗的夜里划，压在薄云上面也看不出破绽——它出现时天上基本没云
    if (meteor) drawMeteor();
    if (rockets.length || bursts.length || flashes.length) drawFireworks();
    if (sleigh || dust.length) drawSleigh();
    if (bats) drawBats();
    drawEaster(daylight);   // 然后是雪地、雪人、节日装饰，雨雪落在它们前面
    if (userFxOn()) drawUserFx(daylight);   // 自定义效果：压在内置装饰上面、雨雪下面
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

  // ------------------------------------------------------------------ 彩蛋绘制
  function drawEaster(daylight) {
    const k = H / 1080, hz = horizonY(), cov = snowCover();
    if (cov > 0.01) {
      const dk = (1 - daylight) * 0.55;
      const hi = mix(hex('#f6f9fd'), hex('#66759c'), dk), lo = mix(hex('#dce5f2'), hex('#4a5678'), dk);
      // 一片起伏的雪地铺满地面，远处两个雪坡添点层次
      ctx.beginPath(); ctx.moveTo(-2, H + 2);
      for (let i = 0; i <= 28; i++) {
        const x = W * i / 28;
        ctx.lineTo(x, hz + 1 + (Math.sin(x * 0.011 + 1.7) * 2.2 + Math.sin(x * 0.031 + 4.2) * 1.1) * k);
      }
      ctx.lineTo(W + 2, H + 2); ctx.closePath();
      const g = ctx.createLinearGradient(0, hz, 0, H);
      g.addColorStop(0, css(hi, 0.96 * cov)); g.addColorStop(1, css(lo, 0.88 * cov));
      ctx.fillStyle = g; ctx.fill();
      ctx.fillStyle = css(hi, 0.5 * cov);
      ctx.beginPath(); ctx.ellipse(W * 0.3, hz + 2 * k, W * 0.16, 7 * k, 0, Math.PI, TAU); ctx.fill();
      ctx.beginPath(); ctx.ellipse(W * 0.74, hz + 2 * k, W * 0.2, 5.5 * k, 0, Math.PI, TAU); ctx.fill();
    }
    if (festDecor()) drawDecor(daylight, k, hz);
    for (const m of men) if (m) drawSnowman(m, daylight, k, hz);
  }

  function drawSnowman(m, daylight, k, hz) {
    const gk = growK(m);
    if (gk <= 0) return;
    const dying = !!m.dying;
    const x = m.xf * W, gy = hz + 3 * k;
    const h = (50 + 36 * m.z) * k * (dying ? 0.35 + 0.65 * gk : easeBack(Math.min(1, gk)));
    if (h < 3) return;
    const r1 = h * 0.30, r2 = h * 0.21, r3 = h * 0.15;
    const y1 = gy - r1 * 0.8, y2 = y1 - r1 * 0.92 - r2 * 0.78, y3 = y2 - r2 * 0.9 - r3 * 0.74;
    const dk = (1 - daylight) * 0.5;
    // 复活后的头一小段：透明度从"化着"的值淡回 1，脚下的水洼也顺势收掉（growK 的回推只对齐了高度）
    let fade = 1;
    if (!dying && m.fadeAt != null) fade = Math.min(1, m.fadeFrom + (simT - m.fadeAt) / 1.2);
    const hi = mix(hex('#f8fafd'), hex('#5d6c96'), dk), lo = mix(hex('#dde6f3'), hex('#49557a'), dk);
    const dark = css(mix(hex('#33384a'), hex('#141826'), dk * 0.8));
    ctx.save();
    ctx.globalAlpha = dying ? gk : fade;
    // 影子；化掉时脚下摊开一小摊水（刚复活还在收的那一小段也画着，跟着 fade 一起收掉）
    ctx.fillStyle = css(mix(hex('#5b688c'), hex('#2b3350'), dk), 0.22);
    ctx.beginPath(); ctx.ellipse(x, gy + 1, r1 * 1.45, r1 * 0.3, 0, 0, TAU); ctx.fill();
    if (dying || fade < 1) { const fk = dying ? 1 - gk : 1 - fade; ctx.fillStyle = css(mix(hex('#9fb4d8'), hex('#3d4c74'), dk), 0.32 * fk); ctx.beginPath(); ctx.ellipse(x, gy, r1 * (1.5 + 1.1 * fk), r1 * 0.36, 0, 0, TAU); ctx.fill(); }
    // 三个雪球
    const ball = (bx, by, r) => {
      const gg = ctx.createRadialGradient(bx - r * 0.4, by - r * 0.45, r * 0.1, bx, by, r);
      gg.addColorStop(0, css(hi)); gg.addColorStop(1, css(lo));
      ctx.fillStyle = gg; ctx.beginPath(); ctx.arc(bx, by, r, 0, TAU); ctx.fill();
    };
    ball(x, y1, r1); ball(x, y2, r2); ball(x, y3, r3);
    // 树枝手臂
    ctx.strokeStyle = css(mix(hex('#7c5b3f'), hex('#3a2f26'), dk)); ctx.lineWidth = Math.max(1, 1.6 * k); ctx.lineCap = 'round';
    const ay = y2 + r2 * 0.05;
    ctx.beginPath();
    ctx.moveTo(x - r2 * 0.75, ay); ctx.lineTo(x - r2 * 0.75 - r1 * 0.8, ay - r2 * 0.6);
    ctx.moveTo(x + r2 * 0.75, ay); ctx.lineTo(x + r2 * 0.75 + r1 * 0.8, ay - r2 * 0.9);
    ctx.stroke();
    // 围巾：脖子一圈，飘带顺着风轻轻摆
    ctx.strokeStyle = css(mix(hex('#c14f4c'), hex('#5c2f3a'), dk));
    ctx.lineWidth = Math.max(1.5, r3 * 0.5);
    ctx.beginPath(); ctx.arc(x, y3 + r3 * 1.12, r3 * 0.95, Math.PI * 0.1, Math.PI * 0.9); ctx.stroke();
    const sway = Math.sin(simT * 1.8 + m.xf * 9) * 0.22 * clamp(Math.abs(cur.windX) * 3, 0.3, 1);
    ctx.lineWidth = Math.max(1, r3 * 0.38);
    ctx.beginPath(); ctx.moveTo(x + r3 * 0.45, y3 + r3 * 1.5);
    ctx.quadraticCurveTo(x + r3 * (1.1 + sway), y3 + r3 * 2.1, x + r3 * (1.5 + sway * 1.8), y3 + r3 * 2.6);
    ctx.stroke();
    // 眼睛、胡萝卜鼻（朝着风来的方向）、扣子
    ctx.fillStyle = dark;
    ctx.beginPath();
    ctx.arc(x - r3 * 0.36, y3 - r3 * 0.18, Math.max(0.7, r3 * 0.11), 0, TAU);
    ctx.arc(x + r3 * 0.36, y3 - r3 * 0.18, Math.max(0.7, r3 * 0.11), 0, TAU);
    ctx.fill();
    const nd = cur.windX >= 0 ? 1 : -1;
    ctx.fillStyle = css(mix(hex('#e2833a'), hex('#6e4526'), dk));
    ctx.beginPath(); ctx.moveTo(x, y3 + r3 * 0.05); ctx.lineTo(x + r3 * 0.9 * nd, y3 + r3 * 0.22); ctx.lineTo(x, y3 + r3 * 0.36); ctx.closePath(); ctx.fill();
    ctx.fillStyle = dark;
    for (let j = -1; j <= 1; j++) { ctx.beginPath(); ctx.arc(x, y2 + r2 * j * 0.42, Math.max(0.6, r2 * 0.09), 0, TAU); ctx.fill(); }
    ctx.restore();
  }

  function drawMeteor() {
    const p = clamp(meteor.t / meteor.dur, 0, 1), fade = Math.sin(Math.PI * p);
    const D = meteor.len * 3.2;
    const c = Math.cos(meteor.ang) * meteor.dir, s = Math.sin(meteor.ang);   // dir 翻转水平方向；竖直分量恒为正：流星只会往下落
    const hx = meteor.x0 + c * D * p, hy = meteor.y0 + s * D * p;
    const tx = hx - c * meteor.len, ty = hy - s * meteor.len;
    const k = H / 1080;
    const g = ctx.createLinearGradient(hx, hy, tx, ty);
    g.addColorStop(0, `rgba(255,255,255,${0.9 * fade})`); g.addColorStop(1, 'rgba(190,205,255,0)');
    ctx.strokeStyle = g; ctx.lineWidth = Math.max(1, 1.8 * k); ctx.lineCap = 'round';
    ctx.beginPath(); ctx.moveTo(hx, hy); ctx.lineTo(tx, ty); ctx.stroke();
    ctx.fillStyle = `rgba(255,255,255,${0.85 * fade})`;
    ctx.beginPath(); ctx.arc(hx, hy, Math.max(1, 1.5 * k), 0, TAU); ctx.fill();
  }

  // ------------------------------------------------------------------ 节日绘制
  const NIGHT = hex('#141826');
  const sh = (c, dk, a = 1) => css(mix(hex(c), NIGHT, dk), a);   // 不发光的东西：夜里压暗
  // 往当前路径里加一个椭圆（先 moveTo 到起点，免得和上一段连出一条线）
  const oval = (x, y, rx, ry, rot = 0) => { ctx.moveTo(x + rx * Math.cos(rot), y + rx * Math.sin(rot)); ctx.ellipse(x, y, rx, ry, rot, 0, TAU); };
  // 发光贴图：径向渐变每种颜色只画一次，之后按位置 / 大小 / 透明度贴上去（每帧新建渐变会撑大渐变缓存，见文件开头）。调用方自己设 'lighter'
  const glowCache = new Map();
  function glowAt(c, x, y, r, a, cache = glowCache) {
    if (a <= 0.01 || r <= 0) return;
    let g = cache.get(c);
    if (!g) {
      g = document.createElement('canvas'); g.width = g.height = 64;
      const gx = g.getContext('2d'), rgb = hex(c), gr = gx.createRadialGradient(32, 32, 0, 32, 32, 32);
      gr.addColorStop(0, css(rgb, 1)); gr.addColorStop(0.3, css(rgb, 0.5)); gr.addColorStop(1, css(rgb, 0));
      gx.fillStyle = gr; gx.fillRect(0, 0, 64, 64);
      cache.set(c, g);
    }
    const ga = ctx.globalAlpha;
    ctx.globalAlpha = ga * Math.min(1, a);
    ctx.drawImage(g, x - r, y - r, 2 * r, 2 * r);
    ctx.globalAlpha = ga;
  }
  function lighter(fn) { ctx.globalCompositeOperation = 'lighter'; fn(); ctx.globalCompositeOperation = 'source-over'; }

  // 烟花：火箭拖着尾迹升空，炸开后每组火花一次 stroke（每个火花一小段顺着速度方向的线），快灭时闪烁
  function drawFireworks() {
    const k = H / 1080;
    ctx.save();
    ctx.globalCompositeOperation = 'lighter';
    ctx.lineCap = 'round';
    for (const f of flashes) { const q = 1 - f.t / 0.6; glowAt(f.c, f.x, f.y, 170 * k, 0.4 * q * q); }
    if (rockets.length) {
      ctx.strokeStyle = 'rgba(255,205,140,0.5)'; ctx.lineWidth = Math.max(1, 1.5 * k);
      ctx.beginPath();
      for (const r of rockets) { ctx.moveTo(r.x, r.y); ctx.lineTo(r.x - r.vx * 0.08, r.y - r.vy * 0.08); }
      ctx.stroke();
      for (const r of rockets) glowAt('#ffe2b0', r.x, r.y, 6 * k, 0.9);
    }
    for (const b of bursts) {
      const q = b.t / b.life;
      let a = Math.pow(1 - q, 1.2);
      if (q > 0.55) a *= 0.65 + 0.35 * Math.sin(simT * 37 + b.life * 11);
      const tl = b.willow ? 0.35 : 0.07, droop = b.willow ? 10 * k * q : 0;   // 垂柳：金色的长尾巴往下垂
      ctx.globalAlpha = clamp(a, 0, 1);
      ctx.strokeStyle = b.c; ctx.lineWidth = Math.max(1, (b.willow ? 1.4 : 2) * k * (1 - 0.45 * q));
      ctx.beginPath();
      for (const p of b.p) { ctx.moveTo(p.x, p.y); ctx.lineTo(p.x - p.vx * tl, p.y - p.vy * tl - droop); }
      ctx.stroke();
    }
    ctx.restore();
  }

  // 圣诞老人的雪橇：四头驯鹿拉着，剪影从月亮前面掠过，后面撒一路金粉；领头的鲁道夫鼻子是红的
  function drawSleigh() {
    const k = H / 1080;
    ctx.save();
    if (dust.length) lighter(() => { for (const d of dust) glowAt('#ffd98a', d.x, d.y, 4 * k, 0.9 * (1 - d.t / 1.4)); });
    if (sleigh) {
      const s = sleigh, p = sleighAt(s), q = sleighAt({ ...s, t: s.t + 0.05 });
      ctx.translate(p.x, p.y);
      ctx.scale(s.dir * k, k);
      ctx.rotate(clamp(Math.atan2(q.y - p.y, (q.x - p.x) * s.dir), -0.5, 0.5));
      const ink = 'rgba(16,20,34,0.94)';
      ctx.fillStyle = ink; ctx.strokeStyle = ink; ctx.lineCap = 'round'; ctx.lineJoin = 'round';
      ctx.lineWidth = 1.2;
      ctx.beginPath(); ctx.moveTo(-13, -12); ctx.lineTo(6, -3);   // 手里的缰绳
      for (let i = 0; i < 4; i++) ctx.lineTo(24 + i * 22, -5 - i * 1.2);
      ctx.stroke();
      for (let i = 0; i < 4; i++) {
        const x = 30 + i * 22, y = -4 - i * 1.2, g = Math.sin(simT * 11 + i * 1.7);
        ctx.beginPath(); oval(x, y, 8, 3.4, -0.05); oval(x + 11.5, y - 7, 3.2, 1.8, 0.25); ctx.fill();   // 身子、头
        ctx.lineWidth = 2.4; ctx.beginPath(); ctx.moveTo(x + 5, y - 1); ctx.lineTo(x + 10, y - 6); ctx.stroke();   // 脖子
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(x + 10, y - 8.5); ctx.lineTo(x + 8, y - 14); ctx.moveTo(x + 9, y - 11.5); ctx.lineTo(x + 6, y - 13);   // 鹿角
        ctx.moveTo(x + 11.5, y - 8.5); ctx.lineTo(x + 12.5, y - 14); ctx.moveTo(x + 12, y - 11.5); ctx.lineTo(x + 14.5, y - 13);
        ctx.moveTo(x + 5, y + 2); ctx.lineTo(x + 6 + 5 * g, y + 8);   // 腿：前后交替地蹬
        ctx.moveTo(x + 3.5, y + 2); ctx.lineTo(x + 4 + 3 * g, y + 8.5);
        ctx.moveTo(x - 5, y + 2); ctx.lineTo(x - 6 - 5 * g, y + 8);
        ctx.moveTo(x - 3.5, y + 2); ctx.lineTo(x - 4 - 3 * g, y + 8.5);
        ctx.moveTo(x - 7.5, y - 1); ctx.lineTo(x - 10, y - 3);   // 尾巴
        ctx.stroke();
      }
      ctx.lineWidth = 1.6;
      ctx.beginPath(); ctx.moveTo(-40, 11); ctx.lineTo(4, 11); ctx.quadraticCurveTo(12, 11, 11, 3);   // 滑板
      ctx.moveTo(-31, 7); ctx.lineTo(-31, 11); ctx.moveTo(-6, 7); ctx.lineTo(-6, 11); ctx.stroke();
      ctx.beginPath(); ctx.moveTo(-39, -7); ctx.lineTo(-36, 7); ctx.lineTo(2, 7); ctx.quadraticCurveTo(9, 6, 7, -4);
      ctx.lineTo(-6, -3); ctx.quadraticCurveTo(-12, -3, -14, -9); ctx.closePath(); ctx.fill();   // 车身
      ctx.beginPath(); oval(-31, -11, 7.5, 7.5); oval(-18, -12, 6.5, 6.5); oval(-16, -21, 4, 4); oval(-22.5, -30, 1.8, 1.8); ctx.fill();   // 礼物袋、圣诞老人、帽子尖上的绒球
      ctx.beginPath(); ctx.moveTo(-20, -23); ctx.lineTo(-12.5, -23.5); ctx.lineTo(-22, -30); ctx.closePath(); ctx.fill();   // 帽子
      const nx = 30 + 3 * 22 + 14.6, ny = -4 - 3 * 1.2 - 6.4;
      lighter(() => glowAt('#ff3b30', nx, ny, 7, 0.95));
      ctx.fillStyle = '#ff4a3a'; ctx.beginPath(); oval(nx, ny, 1.4, 1.4); ctx.fill();
    }
    ctx.restore();
  }

  // 万圣节的蝙蝠：一群从天上扑棱着飞过。翅膀左右镜像且把点序反过来，所有形状都是顺时针，一次 fill 不会互相抵消出窟窿
  const WING = [0, -0.08, 0.5, -0.25, 1, 0, 0.85, 0.05, 0.68, 0.16, 0.55, 0.08, 0.38, 0.2, 0.22, 0.06, 0, 0.12];
  const WING_UP = [0, 0.7, 1, 0.55, 0.45, 0.25, 0.15, 0, 0];   // 每个点随翅膀扇动上下移动的比例（翅尖最大）
  function drawBats() {
    const k = H / 1080, b = bats, p = b.t / b.dur;
    const cx = b.dir > 0 ? -0.1 * W + 1.2 * W * p : 1.1 * W - 1.2 * W * p, cy = b.y0 + Math.sin(p * TAU) * 0.03 * H;
    ctx.fillStyle = 'rgba(18,14,28,0.92)';
    ctx.beginPath();
    for (const m of b.list) {
      const x = cx + m.dx * W, y = cy + m.dy * H + Math.sin(simT * 2.3 + m.wob) * 6 * k, sp = 13 * k * m.s;
      const up = Math.sin(simT * 15 / m.s + m.ph) * 0.5, n = WING.length / 2;
      for (const side of [1, -1]) {
        const pt = i => { const j = side > 0 ? i : n - 1 - i; return [x + side * WING[2 * j] * sp, y + (WING[2 * j + 1] - up * WING_UP[j]) * sp]; };
        ctx.moveTo(...pt(0));
        for (let i = 1; i < n; i += 2) { const c = pt(i), e = pt(i + 1); ctx.quadraticCurveTo(c[0], c[1], e[0], e[1]); }
        ctx.closePath();
      }
      oval(x, y + sp * 0.04, sp * 0.13, sp * 0.22);
      ctx.moveTo(x - sp * 0.1, y - sp * 0.05); ctx.lineTo(x - sp * 0.09, y - sp * 0.27); ctx.lineTo(x - sp * 0.03, y - sp * 0.17);   // 头和两只尖耳朵
      ctx.lineTo(x + sp * 0.03, y - sp * 0.17); ctx.lineTo(x + sp * 0.09, y - sp * 0.27); ctx.lineTo(x + sp * 0.1, y - sp * 0.05); ctx.closePath();
    }
    ctx.fill();
  }

  // 节日装饰：灯笼从屏幕顶上垂下来，其余的摆在地上（左右两个位置，同一天有两个节日就一边一个）；都绕开卡片
  const popK = key => festBorn[key] == null ? 0 : clamp((simT - festBorn[key]) / 1.8, 0, 1);
  const GROUND = {
    xmas: { w: 140, h: 175, draw: drawTree },
    halloween: { w: 130, h: 120, draw: drawPumpkins },
    easter: { w: 130, h: 70, draw: drawEggHunt },
    dragon: { w: 140, h: 110, draw: drawZongzi },
    moon: { w: 140, h: 70, draw: drawMooncakes },
  };   // 国庆的国旗不占左右两个位置：单独立在屏幕正中（drawNationalFlag）
  // 天气卡片的 CSS 尺寸：宽固定（style.css 的 .card、宿主 src/CardLayout.cs 的 W），高度每张卡片按显示的内容不同，宿主在 layout 里给（CardLayout.HeightOf）；
  // 没给高度（浏览器里调试）按全显示的高度算。卡片两边的让位边距 16
  const CARD_W = 336, CARD_H = 446;
  const cardH = c => c.h || CARD_H;
  // 屏幕上看得见的卡片（装饰绕开它们、交给效果脚本的 fx.cards）：自定义卡片藏着的（还没画好、出错了，见 cards.js）不算，那块地方是空的。
  // cardEls 在下面「天气卡片」里定义：这里只在画每一帧时调用，那时早就有了
  const shownCards = () => (L.panel ? L.cards || [] : []).filter(c => {
    const e = c.script && cardEls.get(c.id);
    return !e || !(e.el.classList.contains('hidden') || e.el.classList.contains('cerr'));
  });
  // 在 [y0, y1] 这段高度上，卡片没挡住的横向空地 [[a, b], ...]（CSS 像素，卡片两边再让出一点）
  function freeSpans(y0, y1) {
    const bl = shownCards().filter(c => c.y < y1 && c.y + cardH(c) > y0).map(c => [c.x - 16, c.x + CARD_W + 16]).sort((a, b) => a[0] - b[0]);
    const out = [];
    let x = 0;
    for (const [a, b] of bl) { if (a > x) out.push([x, a]); x = Math.max(x, b); }
    if (x < W) out.push([x, W]);
    return out;
  }
  // 宽 w 的东西尽量摆在 x 附近的空地上（哪都放不下就照原位置放，被卡片挡住也无妨）
  function placeX(x, w, y0, y1) {
    let best = x, bd = Infinity;
    for (const [a, b] of freeSpans(y0, y1)) {
      if (b - a < w) continue;
      const c = clamp(x, a + w / 2, b - w / 2), d = Math.abs(c - x);
      if (d < bd) { bd = d; best = c; }
    }
    return best;
  }
  function drawDecor(daylight, k, hz) {
    const nt = 1 - daylight, dk = nt * 0.5, gy = hz - 4 * k;   // 比地平线略高：没有底部任务栏时地平线就是屏幕下边，盘子、影子的下半圈别画到屏幕外
    let slot = 0;
    for (const key of fest.keys) {
      if (!DECOR.has(key)) continue;
      const g = popK(key);
      if (g <= 0) continue;
      const e = easeBack(g);
      ctx.save();
      ctx.globalAlpha = Math.min(1, g * 2);
      if (key === 'cny' || key === 'lantern' || key === 'moon') hangLanterns(key, e, k, nt);
      if (key === 'national') drawNationalFlag(k, gy, nt, dk);
      const gd = GROUND[key];
      if (gd) {
        const x = placeX(W * (slot % 2 ? 0.915 : 0.085), gd.w * k, gy - gd.h * k, gy);
        slot++;
        ctx.translate(x, gy); ctx.scale(k * e, k * e);
        ctx.lineCap = 'round'; ctx.lineJoin = 'round';
        gd.draw(dk, nt);
      }
      ctx.restore();
    }
  }

  // 灯笼：春节一对写着"福"的大红灯笼，中秋一对橘黄的圆灯笼，元宵一串彩色小花灯；从顶上落下来，随风轻轻晃
  function hangLanterns(key, e, k, nt) {
    const big = key !== 'lantern', h = (big ? 150 : 110) * k;
    const w = big ? 150 * k : Math.min(W * 0.42, 640 * k);
    const sp = freeSpans(0, h).find(([a, b]) => b - a >= w * (big ? 1 : 0.6));
    if (!sp) return;
    const len = Math.min(w, sp[1] - sp[0]), x0 = sp[0] + Math.min(W * 0.035, (sp[1] - sp[0] - len) / 2);
    ctx.save();
    ctx.translate(0, -(1 - e) * h);
    if (big) {
      const red = key === 'cny', col = red ? '#d8261e' : '#f08a24', gc = red ? '#ff5a3a' : '#ffb050', word = red ? '福' : '';
      drawLantern(x0 + 38 * k, 0, 46, 30, col, gc, nt, 0, word);
      drawLantern(x0 + 112 * k, 0, 70, 26, col, gc, nt, 2.1, word);
    } else {
      // 一根绳子从左到右垂下一道弧，小花灯挂在上面
      const sag = 34 * k, n = 7, cols = [['#e8342c', '#ff5a3a'], ['#ff8a2a', '#ffb050'], ['#ef4f78', '#ff7a9a'], ['#f2b42e', '#ffd060']];
      ctx.strokeStyle = sh('#4a3826', nt * 0.5); ctx.lineWidth = Math.max(1, 1.3 * k);
      ctx.beginPath(); ctx.moveTo(x0, -2); ctx.quadraticCurveTo(x0 + len / 2, -2 + 2 * sag, x0 + len, -2); ctx.stroke();
      for (let i = 0; i < n; i++) {
        const t = (i + 0.5) / n, [c, g] = cols[i % cols.length];
        drawLantern(x0 + len * t, -2 + 4 * sag * t * (1 - t), 8 + (i % 2) * 14, 15, c, g, nt, i * 1.3, '');
      }
    }
    ctx.restore();
  }
  // 一盏灯笼：(x, y) 是挂点，len 是吊绳长，r 是半宽（都按 1080p 的像素算）；col 灯笼纸的颜色，gc 里面透出来的光。
  // 夜里里面亮着灯：中心透亮、外面一圈同色的光晕
  function drawLantern(x, y, len, r, col, gc, nt, ph, word) {
    const k = H / 1080, lit = smooth(0.2, 0.8, nt), dk = nt * 0.4;   // 灯纸本身是亮色的，夜里只稍微压一点（亮着的地方靠光晕补回来）
    ctx.save();
    ctx.translate(x, y); ctx.scale(k, k);
    ctx.rotate(Math.sin(simT * 0.9 + ph) * 0.05 + clamp(cur.windX, -1, 1) * 0.1);
    const ry = r * 0.82, cy = len + r * 0.14 + ry;
    ctx.strokeStyle = sh('#4a3826', dk); ctx.lineWidth = 1.2;
    ctx.beginPath(); ctx.moveTo(0, 0); ctx.lineTo(0, cy - ry); ctx.stroke();
    if (lit > 0.01) lighter(() => glowAt(gc, 0, cy, r * 2.7, 0.35 * lit));
    ctx.fillStyle = sh(col, dk);
    ctx.beginPath(); oval(0, cy, r, ry); ctx.fill();
    lighter(() => glowAt(gc, 0, cy, r * 0.95, 0.12 + 0.45 * lit));   // 里面的亮心
    ctx.strokeStyle = css(mix(hex(col), [70, 12, 10], 0.45), 0.55); ctx.lineWidth = 1;   // 竹骨
    ctx.beginPath(); oval(0, cy, r * 0.45, ry); oval(0, cy, r * 0.8, ry); ctx.moveTo(0, cy - ry); ctx.lineTo(0, cy + ry); ctx.stroke();
    ctx.fillStyle = sh('#e0aa3a', dk);   // 上下的金边
    ctx.fillRect(-r * 0.42, cy - ry - r * 0.14, r * 0.84, r * 0.2);
    ctx.fillRect(-r * 0.42, cy + ry - r * 0.06, r * 0.84, r * 0.2);
    const tz = cy + ry + r * 0.14, sw = Math.sin(simT * 1.3 + ph) * r * 0.06;
    ctx.strokeStyle = sh('#e8b040', dk); ctx.lineWidth = 1.1;   // 流苏
    ctx.beginPath();
    for (let i = -2; i <= 2; i++) { ctx.moveTo(i * r * 0.07, tz); ctx.lineTo(i * r * 0.09 + sw, tz + r * 0.85); }
    ctx.stroke();
    if (word) {
      ctx.fillStyle = css(mix(hex('#ffd35a'), [255, 240, 190], 0.4 * lit));
      ctx.font = `bold ${Math.round(r * 0.95)}px "Microsoft YaHei UI","Microsoft YaHei",sans-serif`;
      ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
      ctx.fillText(word, 0, cy + r * 0.04);
    }
    ctx.restore();
  }

  function starPath(x, y, ro, ri, n, rot = -Math.PI / 2) {
    ctx.beginPath();
    for (let i = 0; i < n * 2; i++) { const r = i % 2 ? ri : ro, a = rot + Math.PI * i / n; ctx.lineTo(x + Math.cos(a) * r, y + Math.sin(a) * r); }
    ctx.closePath();
  }

  // 以下地上的装饰都在"原点 = 地面中点、1 单位 = 1080p 的 1 像素"的坐标里画（y 向上是负数）
  // 圣诞树：三层树冠（下雪时挂着雪），一圈圈彩灯轮流闪，顶上一颗星，树下几个礼物
  const LIGHTS = ['#ff4b4b', '#ffd34b', '#4bc8ff', '#7dff8a', '#ff7ad9'];
  function drawTree(dk, nt) {
    const cov = snowCover(), lit = smooth(0.15, 0.7, nt);
    ctx.fillStyle = sh('#3a4560', dk, 0.22); ctx.beginPath(); oval(0, 0, 70, 6); ctx.fill();   // 影子
    ctx.fillStyle = sh('#6b4a2f', dk); ctx.fillRect(-7, -18, 14, 18);   // 树干
    const tiers = [[-14, 60, -88], [-52, 47, -120], [-88, 34, -152]];   // 每层：[底边 y, 半宽, 尖 y]
    const greens = ['#1c5e34', '#22703e', '#2a8049'];
    let li = 0;
    tiers.forEach(([by, hw, ty], i) => {
      ctx.fillStyle = sh(greens[i], dk);
      ctx.beginPath(); ctx.moveTo(0, ty); ctx.lineTo(hw, by);
      for (let j = 1; j <= 4; j++) ctx.quadraticCurveTo(hw - hw * (j - 0.5) / 2, by + 8, hw - hw * j / 2, by);   // 底边一排松针的弧
      ctx.closePath(); ctx.fill();
      if (cov > 0.05) {   // 树枝上的雪
        const s = 7, f = 1 - s / (by - ty);
        ctx.fillStyle = css(mix(hex('#f4f8fc'), hex('#66759c'), dk), 0.92 * cov);
        ctx.beginPath(); ctx.moveTo(hw * f, by - s); ctx.lineTo(hw * 0.98, by);
        for (let j = 1; j <= 4; j++) ctx.quadraticCurveTo(hw * 0.98 - hw * 0.98 * (j - 0.5) / 2, by + 4, hw * 0.98 - hw * 0.98 * j / 2, by);
        ctx.lineTo(-hw * f, by - s); ctx.closePath(); ctx.fill();
      }
      // 彩灯串：沿着这一层垂一道弧
      const gb = by - 16, gw = hw * (1 - 16 / (by - ty)) * 0.92;
      ctx.strokeStyle = sh('#e0c060', dk, 0.6); ctx.lineWidth = 1.2;
      ctx.beginPath();
      for (let j = 0; j <= 12; j++) { const t = j / 12; ctx.lineTo(-gw + 2 * gw * t, gb + 6 * Math.sin(Math.PI * t)); }
      ctx.stroke();
      for (let j = 0; j < 5; j++, li++) {
        const t = (j + 0.5) / 5, x = -gw + 2 * gw * t, y = gb + 6 * Math.sin(Math.PI * t);
        const c = LIGHTS[li % LIGHTS.length], on = (Math.floor(simT * 1.4) + li * 3) % 5 !== 0;
        ctx.fillStyle = on ? css(mix(hex(c), [255, 255, 255], 0.15 * lit)) : sh(c, 0.6);
        ctx.beginPath(); oval(x, y, 3, 3); ctx.fill();
        if (on && lit > 0.05) lighter(() => glowAt(c, x, y, 13, 0.6 * lit));
      }
    });
    const tw = 0.85 + 0.15 * Math.sin(simT * 2.2);
    lighter(() => glowAt('#ffd75a', 0, -156, 28, (0.12 + 0.4 * lit) * tw));
    ctx.fillStyle = css(mix(hex('#f2c230'), [255, 236, 150], 0.4 * lit));
    starPath(0, -156, 11, 4.6, 5); ctx.fill();
    const gift = (x, w, h, c, rb) => {
      ctx.fillStyle = sh(c, dk); ctx.fillRect(x - w / 2, -h, w, h);
      ctx.fillStyle = sh(rb, dk); ctx.fillRect(x - 2, -h, 4, h); ctx.fillRect(x - w / 2, -h * 0.6 - 2, w, 4);
      ctx.beginPath(); oval(x - 4, -h - 2.5, 4, 2.6, -0.5); oval(x + 4, -h - 2.5, 4, 2.6, 0.5); ctx.fill();
    };
    gift(-50, 24, 17, '#c8323c', '#f2c94c'); gift(44, 20, 14, '#2f6fc0', '#f4f4f4'); gift(63, 13, 19, '#3b9a5a', '#e8384a');
  }

  // 万圣节：三个南瓜（两个刻了脸，夜里里面的蜡烛一闪一闪），夜里上面还飘着个小幽灵
  function drawPumpkins(dk, nt) {
    const lit = smooth(0.15, 0.7, nt);
    pumpkin(-50, 16, true, 2, dk, lit);
    pumpkin(44, 19, false, 1, dk, lit);
    pumpkin(-6, 30, true, 0, dk, lit);
    if (lit > 0.02) {
      const x = 16 + Math.sin(simT * 0.5) * 14, y = -82 + Math.sin(simT * 1.3) * 6, s = 13;
      ctx.fillStyle = `rgba(236,240,255,${0.75 * lit})`;
      ctx.beginPath(); ctx.moveTo(x - s, y); ctx.arc(x, y, s, Math.PI, 0); ctx.lineTo(x + s, y + s * 1.3);
      for (let i = 1; i <= 3; i++) ctx.quadraticCurveTo(x + s - 2 * s * (i - 0.5) / 3, y + s * (i % 2 ? 1.0 : 1.6) + Math.sin(simT * 3 + i) * 1.5, x + s - 2 * s * i / 3, y + s * 1.3);
      ctx.closePath(); ctx.fill();
      ctx.fillStyle = `rgba(30,26,46,${0.85 * lit})`;
      ctx.beginPath(); oval(x - s * 0.35, y - s * 0.05, s * 0.14, s * 0.22); oval(x + s * 0.35, y - s * 0.05, s * 0.14, s * 0.22); oval(x, y + s * 0.42, s * 0.12, s * 0.16); ctx.fill();
    }
  }
  function pumpkin(cx, r, carved, seed, dk, lit) {
    const ry = r * 0.78, cy = -ry;
    ctx.fillStyle = sh('#3a2a20', dk, 0.22); ctx.beginPath(); oval(cx, 0, r * 1.15, r * 0.16); ctx.fill();
    ctx.fillStyle = sh('#bf5a17', dk); ctx.beginPath(); oval(cx - r * 0.4, cy, r * 0.62, ry); oval(cx + r * 0.4, cy, r * 0.62, ry); ctx.fill();
    ctx.fillStyle = sh('#e07a22', dk); ctx.beginPath(); oval(cx, cy, r * 0.6, ry * 1.03); ctx.fill();
    ctx.fillStyle = sh('#5a6b2a', dk);   // 蒂
    ctx.beginPath(); ctx.moveTo(cx - r * 0.08, cy - ry * 0.92); ctx.quadraticCurveTo(cx - r * 0.06, cy - ry * 1.3, cx + r * 0.14, cy - ry * 1.4);
    ctx.lineTo(cx + r * 0.12, cy - ry * 1.24); ctx.quadraticCurveTo(cx + r * 0.05, cy - ry * 1.08, cx + r * 0.09, cy - ry * 0.92); ctx.closePath(); ctx.fill();
    if (!carved) return;
    const on = lit * (0.78 + 0.22 * Math.sin(simT * 7.3 + seed * 2) * Math.sin(simT * 3.1 + seed));
    const poly = pts => { ctx.moveTo(cx + pts[0][0] * r, cy + pts[0][1] * r); for (let i = 1; i < pts.length; i++) ctx.lineTo(cx + pts[i][0] * r, cy + pts[i][1] * r); ctx.closePath(); };
    ctx.fillStyle = css(mix(hex('#3b1d08'), hex('#ffc548'), on));
    ctx.beginPath();
    poly([[-0.44, -0.08], [-0.12, -0.08], [-0.3, -0.38]]);
    poly([[0.12, -0.08], [0.44, -0.08], [0.3, -0.38]]);
    poly([[-0.08, 0.1], [0.08, 0.1], [0, -0.04]]);
    poly([[-0.5, 0.2], [-0.22, 0.28], [-0.16, 0.4], [-0.08, 0.3], [0.1, 0.3], [0.17, 0.42], [0.24, 0.28], [0.5, 0.2], [0.3, 0.52], [-0.3, 0.52]]);
    ctx.fill();
    if (on > 0.05) lighter(() => glowAt('#ff9a2a', cx, cy, r * 2.4, 0.5 * on));
  }

  // 复活节：草丛里藏着几个彩蛋，旁边蹲着一只兔子（时不时抖一下耳朵）
  function drawEggHunt(dk) {
    ctx.strokeStyle = sh('#4f9a3c', dk); ctx.lineWidth = 1.6;
    ctx.beginPath();
    for (let i = 0; i < 16; i++) { const x = -62 + i * 8.3, h = 7 + (i * 37) % 7; ctx.moveTo(x, 1); ctx.quadraticCurveTo(x + 1, -h * 0.6, x + 2.5 * Math.sin(i * 2.1 + simT * 0.8), -h); }
    ctx.stroke();
    egg(-52, 10, '#f7a8c4', '#ffffff', -0.2, dk);
    egg(-30, 12, '#8fd0f5', '#f7e08a', 0.15, dk);
    egg(-7, 9, '#f7d86a', '#e070a0', -0.1, dk);
    egg(14, 11, '#b2e89a', '#8a7ff0', 0.25, dk);
    bunny(46, 1, 1, dk, '#d8c8b8', '#3a2a22');
  }
  function egg(x, r, c1, c2, rot, dk) {
    ctx.save();
    ctx.translate(x, -r * 0.95); ctx.rotate(rot);
    ctx.beginPath(); ctx.ellipse(0, 0, r * 0.82, r * 1.25, 0, Math.PI, TAU); ctx.ellipse(0, 0, r * 0.82, r * 0.95, 0, 0, Math.PI); ctx.closePath();   // 上尖下圆
    ctx.fillStyle = sh(c1, dk); ctx.fill();
    ctx.clip();
    ctx.fillStyle = sh(c2, dk);   // 一道锯齿花纹 + 上面几个点
    ctx.beginPath(); ctx.moveTo(-r, -r * 0.12);
    for (let i = 0; i <= 6; i++) ctx.lineTo(-r + r * i / 3, i % 2 ? -r * 0.32 : -r * 0.12);
    for (let i = 6; i >= 0; i--) ctx.lineTo(-r + r * i / 3, i % 2 ? r * 0.02 : r * 0.22);
    ctx.closePath(); ctx.fill();
    ctx.beginPath(); oval(-r * 0.3, -r * 0.7, r * 0.12, r * 0.12); oval(r * 0.3, -r * 0.7, r * 0.12, r * 0.12); oval(0, r * 0.6, r * 0.14, r * 0.14); ctx.fill();
    ctx.restore();
  }
  // 兔子侧面坐着；dir = 1 面朝左。s 缩放，fur 毛色，eye 眼睛颜色（中秋的玉兔是红眼睛）
  function bunny(x, s, dir, dk, fur, eye) {
    ctx.save(); ctx.translate(x, 0); ctx.scale(dir * s, s);
    const tw = (Math.floor(simT / 0.35) + Math.round(x)) % 11 === 0 ? 0.35 : 0;   // 时不时抖一下耳朵
    const inner = sh('#f2b4bc', dk);
    ctx.fillStyle = sh('#3a4560', dk, 0.2); ctx.beginPath(); oval(0, 0, 24, 3.5); ctx.fill();
    ctx.fillStyle = sh(fur, dk);
    ctx.beginPath(); oval(2, -15, 17, 15); oval(-13, -32, 10, 9); oval(-17, -50, 3.6, 12, -0.18); oval(-10, -49, 3.6, 12, 0.22 + tw); oval(-9, -3, 6, 3); ctx.fill();
    ctx.fillStyle = sh('#f8f4f0', dk); ctx.beginPath(); oval(19, -10, 5.5, 5.5); ctx.fill();   // 尾巴
    ctx.fillStyle = inner; ctx.beginPath(); oval(-17, -50, 1.6, 8.5, -0.18); oval(-10, -49, 1.6, 8.5, 0.22 + tw); ctx.fill();
    ctx.fillStyle = sh(eye, dk * 0.5); ctx.beginPath(); oval(-17, -34, 1.7, 1.9); ctx.fill();
    ctx.fillStyle = inner; ctx.beginPath(); oval(-22.5, -30.5, 1.5, 1.2); ctx.fill();
    ctx.restore();
  }

  // 端午：竹盘里摞着三只粽子，旁边插一小捆艾草（扎着红绳，随风晃）
  function drawZongzi(dk) {
    ctx.fillStyle = sh('#b88a4a', dk); ctx.beginPath(); oval(-10, -3, 52, 7); ctx.fill();
    ctx.strokeStyle = sh('#8a6232', dk); ctx.lineWidth = 1; ctx.beginPath(); oval(-10, -3, 45, 5); ctx.stroke();
    zong(-9, -21, 17, 0, Math.min(1, dk + 0.35));   // 后排压暗的一只：填住三只之间漏出来的三角缝
    zong(-28, -17, 20, -0.1, dk); zong(10, -17, 20, 0.12, dk); zong(-9, -37, 18, 0.02, dk);
    const x = 54, sway = Math.sin(simT * 0.9) * 2 + clamp(cur.windX, -1, 1) * 4;
    for (let i = 0; i < 5; i++) {
      const a = (i - 2) * 0.16, h = 78 + (i % 3) * 12, tx = x + Math.sin(a) * h + sway * h / 90, ty = -h;
      ctx.strokeStyle = sh('#6f8a4a', dk); ctx.lineWidth = 1.6;
      ctx.beginPath(); ctx.moveTo(x + (i - 2) * 1.5, 0); ctx.quadraticCurveTo(x + Math.sin(a) * h * 0.4, -h * 0.5, tx, ty); ctx.stroke();
      ctx.fillStyle = sh(i % 2 ? '#7fa05a' : '#8fae68', dk);
      ctx.beginPath();
      for (let j = 0; j < 5; j++) {
        const t = 0.35 + j * 0.15, side = j % 2 ? 1 : -1;
        oval(lerp(x, tx, t) + side * 5, lerp(0, ty, t) + 2, 6, 2.2, side * 0.6 + a);
      }
      ctx.fill();
    }
    ctx.strokeStyle = sh('#d23a3a', dk); ctx.lineWidth = 2.4;
    ctx.beginPath(); ctx.moveTo(x - 6, -16); ctx.lineTo(x + 6, -17); ctx.stroke();
  }
  function zong(x, y, s, rot, dk) {   // 一只粽子：圆角三角，粽叶的纹路，十字捆一道绳
    ctx.save(); ctx.translate(x, y); ctx.rotate(rot);
    ctx.lineWidth = s * 0.35;
    ctx.fillStyle = ctx.strokeStyle = sh('#5e9a3a', dk);
    ctx.beginPath(); ctx.moveTo(0, -s * 0.95); ctx.lineTo(s, s * 0.55); ctx.lineTo(-s, s * 0.55); ctx.closePath(); ctx.fill(); ctx.stroke();
    ctx.strokeStyle = sh('#3f7426', dk); ctx.lineWidth = 1;
    ctx.beginPath();
    for (const t of [0.25, 0.5, 0.75]) { const xb = -s + 2 * s * t; ctx.moveTo(xb, s * 0.55); ctx.lineTo(xb * 0.35, -s * 0.55); }
    ctx.stroke();
    ctx.strokeStyle = sh('#e6d3a0', dk); ctx.lineWidth = 2;
    ctx.beginPath(); ctx.moveTo(-s * 0.66, 0); ctx.lineTo(s * 0.66, 0); ctx.moveTo(0, -s * 0.95); ctx.lineTo(0, s * 0.62); ctx.stroke();
    ctx.restore();
  }

  // 中秋：盘子里两块月饼，旁边蹲着一只玉兔
  function drawMooncakes(dk) {
    ctx.fillStyle = sh('#e8e2d6', dk); ctx.beginPath(); oval(-16, -3, 50, 7); ctx.fill();
    mooncake(-32, -5, 22, dk); mooncake(4, -2, 18, dk);
    bunny(50, 0.85, 1, dk, '#f4f1ee', '#d23a4a');
  }
  function mooncake(x, y, r, dk) {
    const h = r * 0.55, ry = r * 0.36;
    ctx.fillStyle = sh('#b8762e', dk);
    ctx.beginPath(); ctx.moveTo(x - r, y - h); ctx.lineTo(x - r, y); ctx.ellipse(x, y, r, ry, 0, Math.PI, 0, true); ctx.lineTo(x + r, y - h); ctx.closePath(); ctx.fill();
    ctx.fillStyle = sh('#dba052', dk); ctx.beginPath(); oval(x, y - h, r, ry); ctx.fill();
    ctx.strokeStyle = sh('#a8662a', dk); ctx.lineWidth = 1.1;
    ctx.beginPath(); oval(x, y - h, r * 0.78, ry * 0.78); oval(x, y - h, r * 0.4, ry * 0.4);
    for (let i = 0; i < 12; i++) { const a = TAU * i / 12; oval(x + Math.cos(a) * r * 0.6, y - h + Math.sin(a) * ry * 0.6, r * 0.07, ry * 0.1); }
    ctx.stroke();
  }

  // 国庆：屏幕正中一根高旗杆（让开卡片），旗面挂在屏幕中上方，国庆假期（10 月 1–7 日）一直迎风飘。
  // 只有 10 月 1 日当天：每块屏第一次开机 / 解锁时（或开着机跨进 10 月 1 日时）从杆底升上去，升到顶迎一阵大风。
  // 旗面先画进离屏小画布，再切成竖条按波浪错开贴，波峰亮一点、波谷暗一点
  const FLAG_RISE = 12;   // 升旗用时（秒）
  let flagWant = true, flagPv = false, flagRise = null;   // flagWant：刚开机 / 刚解锁、还没看过要不要升旗；flagRise = { t0 }：升旗开始的 simT
  const localDay = () => new Date(now() + locInfo().off * 1000).toISOString().slice(0, 10);   // 天气所在城市的当地日期
  function checkFlag() {
    if (!hasFest('national')) { flagRise = null; flagPv = false; return; }
    if (pvFest()) { if (!flagPv) { flagPv = true; flagRise = { t0: simT + 1.2 }; } return; }   // 预览：每次都演示一遍升旗
    flagPv = false;
    if (!flagWant) return;   // 10 月 2–7 日开着机跨过零点：不算开机 / 解锁，旗子接着飘
    if (host && (paused || asleep)) return;   // 桌面这会儿被窗口挡着 / 画面收起了：等露出来再升，别没人看见就升完了
    if (demo) return;   // 一天快进把时间拨快了：快进到 10 月 1 日不算真的到了，也别把演示日期记成"今天升过旗"（真的到了国庆节那天照常升）
    flagWant = false;
    if (!localDay().endsWith('-10-01')) return;   // 升旗只在国庆节当天，假期里其余几天旗子一直挂在杆顶
    // 每块屏各记各的：几块屏的页面共用一份 localStorage，不分开的话只有先跑到这里的那块屏会升旗。
    // ?date= 调试页把日期平移了（demo 拦不住它）：假日期记到带后缀的键上，别把真的"今天升过旗"占掉
    const key = 'ww.flagDay.' + (L.screen || 'page') + (qParam('date') ? '@dbg' : ''), day = localDay();
    let seen = null;
    try { seen = localStorage.getItem(key); localStorage.setItem(key, day); } catch (e) { /* 存不了就每次都升 */ }
    if (flagRise) return;   // 正升着（升旗途中又锁屏 / 解锁了一次）：标记在上面已经写下，接着升完
    flagRise = seen === day ? null : { t0: simT + 1.2 };   // 今天这块屏已经升过（重启程序、换显示器设置后页面重载）：直接挂在杆顶
  }
  // 升旗进度 p：0 在杆底 … 1 到顶；gust：到顶后那阵大风（慢慢平息）
  function flagLift() {
    if (!flagRise) return { p: 1, gust: 0 };
    const t = simT - flagRise.t0;
    if (t <= 0) return { p: 0, gust: 0 };
    const u = Math.min(1, t / FLAG_RISE), p = u * 0.75 + u * u * (3 - 2 * u) * 0.25;   // 基本匀速，起步和到顶稍缓
    const after = t - FLAG_RISE;
    if (after > 9) flagRise = null;
    return { p, gust: after > 0 ? smooth(0, 0.6, after) * Math.exp(-after / 2.5) : 0 };
  }
  function drawNationalFlag(k, gy, nt, dk) {
    const fw = 300 * k, fh = 200 * k, pw = 7 * k, top = Math.max(H * 0.2, 40 * k);   // 旗面 3:2；top = 杆顶
    // 整块（底座 + 旗杆 + 旗面）横跨 [px - 36k, px + fw + 12k]：旗杆尽量在正中，卡片挡着就就近挪开
    const off = (fw + 12 * k - 36 * k) / 2;
    const px = placeX(W / 2 + off, fw + 48 * k, top - 14 * k, gy) - off;
    const lit = smooth(0.2, 0.75, nt), base = gy - 22 * k;
    ctx.fillStyle = sh('#8f959f', dk); ctx.fillRect(px - 36 * k, gy - 10 * k, 72 * k, 10 * k);   // 两级石台
    ctx.fillStyle = sh('#aab0ba', dk); ctx.fillRect(px - 22 * k, base, 44 * k, 12 * k);
    ctx.fillStyle = sh('#dadde3', dk); ctx.fillRect(px - pw / 2, top, pw, base - top);           // 旗杆：亮面 + 一道暗面，看着是圆的
    ctx.fillStyle = sh('#9ca1ab', dk); ctx.fillRect(px + pw * 0.1, top, pw * 0.4, base - top);
    ctx.fillStyle = sh('#f0c84a', dk * 0.6); ctx.beginPath(); oval(px, top - 6 * k, 7 * k, 7 * k); ctx.fill();   // 杆顶金球
    const rx = px + pw / 2 + 1.5 * k;   // 升降绳：从杆顶垂到下面的绳扣，旗子挂在绳上
    ctx.strokeStyle = sh('#eeeeee', dk, 0.5); ctx.lineWidth = Math.max(1, 1.2 * k);
    ctx.beginPath(); ctx.moveTo(rx, top + 3 * k); ctx.lineTo(rx, base - 50 * k); ctx.stroke();

    const { p, gust } = flagLift();
    const yTop = top + 8 * k, yLow = base - 50 * k - fh;   // 升旗起点：旗面下边在绳扣那儿（大约人手的高度）
    const fy = yLow + (yTop - yLow) * p;
    const wind = (0.65 + clamp(Math.abs(cur.windX) * 2, 0, 1) * 0.5) * (1 + 0.8 * gust);
    const fdk = dk * (1 - 0.6 * lit);   // 夜里有灯照着旗：旗面不像别的装饰压那么暗
    if (lit > 0.02) lighter(() => glowAt('#fff0cc', rx + fw * 0.5, fy + fh * 0.5, fw * 0.8, 0.1 * lit));
    const img = flagSprite(), N = clamp(Math.round(fw / 2.5), 48, 140), sw = img.width / N, w = fw / N + 0.8;   // 条切得够细（约 2.5px 一条），旗边的波浪才不出锯齿台阶
    const sq = ctx.imageSmoothingQuality;
    ctx.imageSmoothingQuality = 'high';
    for (let i = 0; i < N; i++) {
      const t = i / N, ph = simT * 2.4 - t * 6.5;
      const y = fy + Math.sin(ph) * fh * 0.075 * wind * t + fh * 0.05 * t * t * Math.max(0, 1.3 - wind);   // 越往旗尾摆得越大；风小时旗尾往下耷一点
      const x = rx + fw * t;
      ctx.drawImage(img, i * sw, 0, sw, img.height, x, y, w, fh);
      const s = Math.cos(ph) * 0.22 * Math.min(1, wind) * Math.min(1, t * 3);
      if (s > 0.01) { ctx.fillStyle = `rgba(255,255,255,${(s * (1 - fdk)).toFixed(3)})`; ctx.fillRect(x, y, w, fh); }
      const d = fdk * 0.9 + Math.max(0, -s);
      if (d > 0.01) { ctx.fillStyle = css(NIGHT, Math.min(0.8, d)); ctx.fillRect(x, y, w, fh); }
    }
    ctx.imageSmoothingQuality = sq;
  }
  let flagCvs = null;
  function flagSprite() {
    if (flagCvs) return flagCvs;
    const c = document.createElement('canvas'); c.width = 480; c.height = 320;   // 比 1080p 上的旗面（300×200）大一些，2K / 4K 屏上星星也不发虚
    const g = c.getContext('2d'), u = 480 / 30;   // 国旗按 30×20 的网格定星位
    g.fillStyle = '#de2910'; g.fillRect(0, 0, 480, 320);
    g.fillStyle = '#ffde00';
    const st = (cx, cy, R, rot) => {
      g.beginPath();
      for (let i = 0; i < 10; i++) { const r = i % 2 ? R * 0.382 : R, a = rot + Math.PI * i / 5; g.lineTo((cx + Math.cos(a) * r) * u, (cy + Math.sin(a) * r) * u); }
      g.closePath(); g.fill();
    };
    st(5, 5, 3, -Math.PI / 2);
    for (const [x, y] of [[10, 2], [12, 4], [12, 7], [10, 9]]) st(x, y, 1, Math.atan2(5 - y, 5 - x));   // 四颗小星各有一个角正对大星中心
    return (flagCvs = c);
  }

  function cloudColors(e, daylight, golden, storm) {
    let top = mix(hex('#3b4357'), hex('#ffffff'), daylight), bot = mix(hex('#1a1f2c'), hex('#c3cdda'), daylight);
    top = mix(top, hex('#ffd0a4'), golden * 0.7); bot = mix(bot, hex('#8b7693'), golden * 0.6);
    const sTop = mix(hex('#2a2f38'), hex('#8f98a4'), daylight), sBot = mix(hex('#14171c'), hex('#4e5662'), daylight);   // 乌云：白天也是铅灰色
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

  // 彩虹出现在背对太阳的方向：画在离太阳远的一侧，圆心在地平线以下，太阳越高弧越矮。
  // 主虹内紫外红，外侧一道颜色反过来、更宽更淡的副虹，主虹里面的天空略亮一些
  const BOW = ['#8a5cf0', '#3d7bff', '#36c070', '#ffe03a', '#ff9a2e', '#ff3b30'].map(hex);
  function drawRainbow(st) {
    const a = cur.bow * st.daylight * (1 - smooth(0.55, 0.95, cur.cloud)) * (1 - cur.fog) * (1 - clamp(cur.rain * 2, 0, 1));
    if (a < 0.01) return;
    const { sp, e, hz } = st;
    const R = Math.min(H * 0.62, W * 0.45), away = R * 1.35;
    const cx = clamp(sp.x >= W / 2 ? sp.x - away : sp.x + away, R * 0.3, W - R * 0.3);
    const cy = hz + R * (0.1 + 0.35 * smooth(10, 50, e));
    const ring = (r0, r1, cols, alpha) => {
      const g = ctx.createRadialGradient(cx, cy, r0, cx, cy, r1), n = cols.length;
      g.addColorStop(0, css(cols[0], 0));
      for (let i = 0; i < n; i++) g.addColorStop(0.14 + 0.72 * i / (n - 1), css(cols[i], alpha));
      g.addColorStop(1, css(cols[n - 1], 0));
      ctx.fillStyle = g; ctx.fillRect(cx - r1, cy - r1, 2 * r1, 2 * r1);
    };
    ctx.save();
    ctx.beginPath(); ctx.rect(0, 0, W, hz); ctx.clip();
    ctx.globalCompositeOperation = 'screen';
    const glow = ctx.createRadialGradient(cx, cy, R * 0.45, cx, cy, R * 0.92);
    glow.addColorStop(0, 'rgba(255,255,255,0)'); glow.addColorStop(0.92, css([255, 255, 255], 0.08 * a)); glow.addColorStop(1, 'rgba(255,255,255,0)');
    ctx.fillStyle = glow; ctx.fillRect(cx - R, cy - R, 2 * R, 2 * R);
    ctx.globalCompositeOperation = 'source-over';
    ring(R * 0.9, R, BOW, 0.34 * a);
    ring(R * 1.12, R * 1.25, BOW.slice().reverse(), 0.07 * a);
    ctx.restore();
  }

  // 月相按真实日期算：取月亮与太阳的黄经差（月亮带上几项主要摄动），朔望时刻误差在 1 小时内；
  // 换算成"月龄"天数，0 = 朔，SYN/2 = 望。只用平均朔望月的话误差可达半天以上，十五十六哪天最圆会算错。
  const SYN = 29.530588853;
  function moonAge(t) {
    const pv = PREV[preview];
    if (pv && pv.moon === 'full') return SYN / 2;   // 中秋 / 元宵预览：按满月画（真到了节日那天，月亮本来就是圆的）
    const d = t / 86400000 - 10957.5;
    const g = (357.529 + 0.98560028 * d) * RAD;
    const ls = 280.459 + 0.98564736 * d + 1.915 * Math.sin(g) + 0.020 * Math.sin(2 * g);
    const Mm = (134.963 + 13.064993 * d) * RAD, D = (297.850 + 12.190749 * d) * RAD;
    const lm = 218.316 + 13.176396 * d + 6.289 * Math.sin(Mm) + 1.274 * Math.sin(2 * D - Mm) + 0.658 * Math.sin(2 * D)
      + 0.214 * Math.sin(2 * Mm) - 0.186 * Math.sin(g);
    return (((lm - ls) % 360 + 360) % 360) / 360 * SYN;
  }
  // 月亮每天比太阳晚升起约 50 分钟，即落后太阳 age/SYN 天：把太阳的东升西落整体推迟这么久，就是此刻月亮在天上的位置。
  // 于是初一与太阳同升同落（夜里看不见），初七八傍晚高挂、半夜落下，十五六日落时东升、整夜可见，二十二三后半夜才升起。
  function moonPos(t, li) {
    const age = moonAge(t);
    const tm = t - age / SYN * 86400000, di = dayInfo(tm, li);
    let a = di.rise, b = di.set;
    if (a == null || b == null) { a = di.mid + 6 * 3600000; b = di.mid + 18 * 3600000; }   // 极夜：按 6 点升 18 点落
    const p = (tm - a) / (b - a), hz = horizonY();
    return { age, p, x: W * (0.12 + 0.76 * p), y: hz - (hz - H * 0.16) * Math.sin(Math.PI * p) * 0.9 };
  }

  function drawMoon(mp, v) {
    if (v < 0.01) return;
    const { x, y, age, p } = mp, hz = horizonY();
    const ph = Math.cos(TAU * age / SYN), illum = (1 - ph) / 2;
    const r = Math.min(W, H) * 0.026;
    ctx.globalCompositeOperation = 'lighter';
    const gg = ctx.createRadialGradient(x, y, r * 0.5, x, y, r * 9);
    gg.addColorStop(0, `rgba(200,215,255,${0.22 * v * (0.3 + illum)})`); gg.addColorStop(1, 'rgba(200,215,255,0)');
    ctx.fillStyle = gg; ctx.fillRect(x - r * 9, y - r * 9, r * 18, r * 18);
    ctx.globalCompositeOperation = 'source-over';
    // 亮面朝向太阳：太阳在同一条天弧上领先月亮 age/SYN 圈（已落到地平线下），取两者连线方向。
    // 傍晚西边的月牙亮面朝下偏右，后半夜东边的残月亮面朝左下
    const th = Math.PI * p, ts = th + TAU * age / SYN;
    const ang = Math.atan2((hz - H * 0.16) * 0.9 * (Math.sin(th) - Math.sin(ts)), W * 0.38 * (Math.cos(th) - Math.cos(ts)));
    ctx.save(); ctx.translate(x, y); ctx.rotate(ang);
    // 暗面的灰光是地照（"新月抱旧月"）：只有细月牙时看得见，月亮越满越淡，过了上弦就没了
    const es = 0.12 * v * (1 - smooth(0.12, 0.5, illum));
    if (es > 0.004) {
      ctx.fillStyle = `rgba(160,170,195,${es})`;
      ctx.beginPath(); ctx.arc(0, 0, r, 0, TAU); ctx.fill();
    }
    ctx.beginPath();
    ctx.arc(0, 0, r, -Math.PI / 2, Math.PI / 2, false);
    ctx.ellipse(0, 0, r * Math.abs(ph), r, 0, Math.PI / 2, -Math.PI / 2, ph > 0);
    ctx.closePath();
    const ca = Math.cos(ang), sa = Math.sin(ang);   // 高光固定在屏幕左上方，不跟着亮面方向转
    const mg = ctx.createRadialGradient(-r * 0.3 * (ca + sa), -r * 0.3 * (ca - sa), 0, 0, 0, r);
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
      const lw = T[t][0] * k * DPR * (1 + 0.4 * lr), ow = lw + 2 * DPR, w = Math.ceil(ow + 2), h = 64;   // 小雨加粗
      const c = rainSpr[t] || (rainSpr[t] = document.createElement('canvas'));
      c.width = w; c.height = h;
      const g = c.getContext('2d');
      // 深色描边：落在亮云上的细雨也能看清
      const og = g.createLinearGradient(0, 0, 0, h);
      const oa = T[t][1] * (0.18 + 0.45 * lr);
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
    // 白天小雨芯提亮到近白：灰底、乌云上靠亮芯，白云上靠深色描边
    const col = mix([150, 165, 190], mix([210, 222, 238], [238, 243, 252], lightRain()), daylight);
    const vxk = cur.windX * 0.22, k = H / 1080;
    const spr = rainSprites(col, k);
    // x' = x + s*y：沿雨丝方向剪切，贴图竖直画，落到屏幕上就是斜的
    ctx.setTransform(DPR, 0, vxk * DPR, DPR, 0, 0);
    ctx.globalAlpha = 0.5 * (1 + 0.9 * lightRain());
    // 按层级分三批画：同一张贴图连续绘制，浏览器可以把整批合并成一次提交（雨滴在数组里是乱序的，交替换贴图就合并不了）
    for (let tier = 0; tier < 3; tier++) {
      const s = spr[tier];
      for (let i = 0; i < drops.length; i++) {
        const d = drops[i];
        if (d.tier === tier) ctx.drawImage(s, d.x - vxk * d.y - s.lw / 2, d.y - d.l, s.lw, d.l);
      }
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
  // 每张卡片一个 .card，按宿主给的位置和高度摆（高度跟着卡片显示的内容走，c.show）；城市的天气还没到时先显示"正在获取天气…"。
  // 内容没变就不动 DOM：卡片带毛玻璃，重排一次要重新模糊整块背景
  const cardEls = new Map();   // id → { el, html, x, y, h }
  function renderPanel() {
    const list = L.panel ? L.cards || [] : [];
    const seen = new Set();
    for (const c of list) {
      seen.add(c.id);
      let e = cardEls.get(c.id);
      if (!e) {
        const el = document.createElement('div');
        el.className = 'card hidden';
        cardsEl.appendChild(el);
        e = { el, html: null, x: null, y: null, h: null };
        cardEls.set(c.id, e);
      }
      e.gone = false;   // 淡出中又回来了：别让排好的删除把它撤掉
      const x = c.x == null ? 0 : c.x, y = c.y == null ? 0 : c.y;   // 没有位置的消息不会来；这里只是防御
      if (x !== e.x || y !== e.y) { e.x = x; e.y = y; e.el.style.left = x + 'px'; e.el.style.top = y + 'px'; }
      const h = c.h ? c.h + 'px' : 'auto';   // 浏览器里调试没有宿主算的高度：按内容撑开
      if (h !== e.h) { e.h = h; e.el.style.height = h; }
      // 自定义卡片：内容由 cards.js 跑用户脚本画（放在卡片里的 iframe 里），显隐也归它管（第一次画好才淡入，出错就藏起来）
      if (c.script) {
        // 原来是天气卡片（settings.json 里给它加了 Script）：先藏起来，和新建的一样等脚本画好再显示（不然空着的毛玻璃框摆在那，装饰还要绕开它）
        if (!e.custom) { e.custom = true; e.html = null; e.err = ''; e.el.textContent = ''; e.el.classList.add('custom', 'hidden'); }
        UserCards.update(c, e.el);
        continue;
      }
      if (e.custom) { e.custom = false; UserCards.drop(c.id, e.el); e.el.textContent = ''; e.el.classList.remove('custom', 'cerr'); }
      const w = weathers[c.key];
      let html;
      try {
        html = w ? WX.panelHtml(w, Date.now() + w.fc.utc_offset_seconds * 1000, c.key === L.scene ? previewLabel() : '', c.show)
          : '<div class="wait">正在获取天气…</div>';
        e.err = '';   // 好了：之后再出同样的错要重新记日志
      } catch (err) {
        // 数据有问题：卡片照样显示出来（不然新卡片会一直停在淡入前的透明状态），同样的错误连续出现只记一次日志
        if (e.err !== err.message) { e.err = err.message; post('log:panel ' + err.message); }
        html = '<div class="wait">天气数据暂时无法显示</div>';
      }
      if (html !== e.html) { e.html = html; e.el.innerHTML = html; WX.fitChips(e.el); }
      if (e.el.classList.contains('hidden')) { void e.el.offsetWidth; e.el.classList.remove('hidden'); }   // 新卡片淡入（先让初始样式生效，过渡才会触发）
    }
    // 消失的卡片淡出后再移除（和 1 秒淡入对称）：淡出期间回来了就留着（上面 e.gone = false）
    for (const [id, e] of cardEls) if (!seen.has(id) && !e.gone)
    {
      e.gone = true;
      e.el.classList.add('hidden');
      // 自定义卡片：马上停掉脚本（不然淡出期间画好一次又会把自己显示出来）；淡出期间又回来了就重新载入
      if (e.custom) UserCards.drop(id, e.el);
      // 只认最后一次淡出排的定时器（来回切换时早先排的那个到点了别提前撤）；只删自己那条（同一 id 可能已经换成新建的卡片）
      const tok = e.fade = (e.fade || 0) + 1;
      setTimeout(() => { if (e.gone && e.fade === tok) { e.el.remove(); if (cardEls.get(id) === e) cardEls.delete(id); } }, 1000);
    }
  }
  // 告诉宿主页面的实际视口（CSS 像素）：卡片位置按它换算，系统缩放、浏览器缩放怎么变都和页面一致
  let lastViewport = '';
  function reportViewport() {
    const m = { t: 'viewport', w: window.innerWidth, h: window.innerHeight, dpr: window.devicePixelRatio || 1 };
    const k = m.w + 'x' + m.h + '@' + m.dpr;
    if (k !== lastViewport && host) { lastViewport = k; post(m); }
  }

  // ------------------------------------------------------------------ 主循环
  // 按需调度，不再无条件每个垂直同步都跑：
  //  - 有雨雪 / 闪电：requestAnimationFrame，按 L.fps 绘制前景（和以前一样）
  //  - 没有降水：setTimeout，只在下一次真要画的时候醒来——有云 12 FPS；天空变化期 15 FPS；
  //    有星星 / 日芒的夜晚 / 晴天 3 FPS；其余时候每秒只查一次"指纹"有没有变
  let last = performance.now(), lastSky = -1e9, lastCloud = -1e9, raf = 0, tm = 0, fxDirty = false;
  const cloudFps = () => Math.min(L.fps, 12);
  const bgFps = () => demo && demo.speed > 1 ? L.fps : Math.min(L.fps, 15);
  const FADE_KEYS = ['cloud', 'fog', 'dark', 'rain', 'thunder', 'bow'];   // 影响天空 / 云画面的场景量
  function transitioning() {
    for (const k of FADE_KEYS) if (Math.abs(cur[k] - tgt[k]) > 0.004) return true;
    return false;
  }
  function idleDelay(st, cloudsOn) {
    let d = 1000;
    if (cloudsOn) d = Math.min(d, 1000 / cloudFps());
    if (transitioning() || (demo && demo.speed > 1)) d = Math.min(d, 1000 / bgFps());
    if (st.starA > 0.01) d = Math.min(d, 300);
    if (st.rays) d = Math.min(d, 500);
    if (decorOn()) d = Math.min(d, 160);   // 雪人冒出来 / 化掉、围巾摆动、灯笼轻晃、彩灯闪烁：6 帧上下够用
    if (hasFest('national')) d = Math.min(d, 1000 / Math.min(L.fps, 15));   // 正中那面大旗一直在飘：6 帧看着一顿一顿的
    const uf = userFxFps(); if (uf) d = Math.min(d, 1000 / Math.min(L.fps, uf));   // 自定义效果要的帧率
    return d;
  }
  /// 状态变了（天气 / 预览 / 布局 / 恢复）：别等下一次定时，马上跑一轮
  function wake() {
    if (paused || asleep || raf) return;   // rAF 循环在跑时它自己会处理
    clearTimeout(tm);
    tm = setTimeout(() => frame(performance.now()), 0);
  }
  function frame(ts) {
    raf = 0; tm = 0;
    if (paused || asleep) return;
    const fxActive = fxNeeded();
    if (fxActive && ts - last < 1000 / L.fps - 3) { raf = requestAnimationFrame(frame); return; }
    const dt = Math.min(fxActive ? 0.1 : 1.5, (ts - last) / 1000);
    last = ts;
    let st = null, cloudsOn = false;
    try {
      update(dt);
      if (fxActive || decorOn()) { fxAlloc(true); drawFx(); fxDirty = true; }   // 雨雪 / 闪电 / 流星 / 烟花满帧画；雪地、雪人、节日装饰只是低频画
      else if (fxDirty) { fxAlloc(false); fxDirty = false; }

      st = bgState();
      const realloc = updateBgRes(st, false);
      if (realloc || (skySig(st) !== skySigLast && ts - lastSky >= 1000 / bgFps() - 3)) { lastSky = ts; drawSky(st); }
      cloudsOn = cloudsVisible();
      if (cloudsOn ? ts - lastCloud >= 1000 / cloudFps() - 3 : cloudOn) { lastCloud = ts; drawClouds(st); }
      updateVeil();
    } finally {
      // 这一帧出错（比如收到残缺的天气数据）也照样排下一帧，否则画面会永远停住
      if (fxNeeded()) raf = requestAnimationFrame(frame);
      else tm = setTimeout(() => frame(performance.now()), st ? idleDelay(st, cloudsOn) : 1000);
    }
  }
  // 前景画布只在有雨雪 / 闪电 / 流星时才分配整屏大小；雪地和雪人还留着时也保持分配（低频重画）。晴天、多云时缩成 1×1 并隐藏，省下一整屏的显存和合成开销
  function fxNeeded() { return drops.length > 0 || flakes.length > 0 || splash.length > 0 || ripples.length > 0 || !!flash || !!meteor || skyFxBusy() || !!flagRise || userFxFast() || tgt.rain > 0 || tgt.snow > 0; }
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
    reportViewport();
    initStars();
    drops.length = 0; flakes.length = 0; firstFill = true;
    if (asleep) return;   // 睡着时不分配画布，醒来时按当时的尺寸重建
    bgAlloc(bgK);
    { const was = fxOn; fxOn = !was; fxAlloc(was); }   // 按新尺寸重新分配（或确保初始为 1×1）
    { const was = cloudOn; cloudOn = !was; cloudAlloc(was); }
    skySigLast = ''; lastSky = lastCloud = -1e9; fxDirty = true;
    wake();
  }
  window.addEventListener('resize', resize);
  // GPU 重置（驱动更新、切换显卡等）后画布内容和变换都会丢；天空现在只在指纹变化时重画，得主动按当前尺寸重建一遍
  for (const c of [cvs, cldCvs, fxCvs]) c.addEventListener('contextrestored', resize);
  resize();

  // ------------------------------------------------------------------ 极简模式
  // 平时天气画面整层淡出后"睡着"：三张画布缩成 1×1、主循环停掉，页面上只剩系统壁纸底图 + 卡片，CPU / GPU 基本为 0。
  // 宿主让播放（fx）、或者在看效果预览时醒来：按当前天气立刻画好一帧再淡入，播完再淡出睡回去。
  const FADE_MS = 1500;
  // 壁纸叠加不是极简模式：天气画面一直显示（只是天空半透明），所以始终要画
  function sceneWanted() { return !L.minimal || L.overlayWall || !!fxKind || preview !== 'live' || dayDemo; }
  function applyMinimal() {
    if (!gotLayout) return;
    if (sceneWanted()) {
      clearTimeout(sleepTm); sleepTm = 0;
      if (asleep) {
        asleep = false; firstScene = true;
        if (flagWant) festAt = -1e9;   // 国庆节当天还没升旗：醒来马上看一眼
        sceneEl.style.display = '';
        resize();                   // 按当前尺寸重新分配画布并启动主循环
        update(0.016); draw();      // 先画好完整的一帧再淡入，不会从空白画布淡进来
        void sceneEl.offsetWidth;   // 让 display 的变化先生效，下面的 opacity 才有过渡
      }
      sceneEl.style.opacity = '1';
    } else if (!asleep && !sleepTm) {
      if (sceneEl.style.opacity === '0') sleep();
      else { sceneEl.style.opacity = '0'; sleepTm = setTimeout(sleep, FADE_MS + 200); }
    }
    // 第一次显隐立即生效（先强制一次样式计算），之后恢复淡入淡出
    if (sceneEl.style.transition === 'none') { void sceneEl.offsetWidth; sceneEl.style.transition = ''; }
  }
  function sleep() {
    sleepTm = 0;
    if (sceneWanted()) return;
    asleep = true;
    cancelAnimationFrame(raf); clearTimeout(tm); raf = tm = 0;
    sceneEl.style.display = 'none';
    cvs.width = 1; cvs.height = 1; skySigLast = '';
    cloudAlloc(false); fxAlloc(false); fxDirty = false;
    drops.length = 0; flakes.length = 0; splash.length = 0; ripples.length = 0; flash = null;
    postScene();
  }
  // 底图画进一张不透明画布后立即丢掉图片：显存里只留一份整屏纹理（CSS 背景图会同时占着解码缓存和光栅瓦片两份）
  // 极简模式和壁纸叠加都要垫系统壁纸：极简模式是"平时只有它"，壁纸叠加是"它从半透明的天空里透出来"
  function applyBackdrop() {
    const b = L.minimal || L.overlayWall ? backdrop || {} : null;
    const hadReady = backReady;
    // 壁纸叠加：底图还没到 / 没有底图时天空画不透明（不然揭掉天空就露出 WebView2 的黑底）
    backReady = !!(b && (b.url || b.color));
    if (backReady !== hadReady) { skySigLast = ''; lastSky = -1e9; }   // 天空那层要按新的不透明度重画
    backEl.style.display = b ? 'block' : 'none';   // 样式表默认 display:none
    const key = b ? (b.color || '') + '|' + (b.url || '') : '';
    if (key === backKey) return;
    backKey = key;
    const tok = ++backTok, ctx = backEl.getContext('2d', { alpha: false });
    const fill = () => { backEl.width = 1; backEl.height = 1; ctx.fillStyle = (b && b.color) || '#000'; ctx.fillRect(0, 0, 1, 1); };
    // 纯色壁纸、非极简模式：1×1 纯色拉满全屏（顺带把整屏纹理还回去）
    if (!b || !b.url) { fill(); return; }
    // 换图：旧图留到新图加载好再替换，不闪黑；还没有图时先用纯色垫着
    if (backEl.width <= 1) fill();
    const img = new Image();
    img.onload = () => {
      img.onload = img.onerror = null;   // 下面清空 src 会再触发一次 error
      if (tok === backTok) {
        // 按图片本身的像素尺寸画（宿主已按屏幕物理分辨率生成）：非整数缩放时 W×DPR 会差一两个像素，整张图被重采样变糊
        backEl.width = img.naturalWidth; backEl.height = img.naturalHeight;
        ctx.drawImage(img, 0, 0);
      }
      img.src = '';
    };
    img.onerror = () => {
      img.onload = img.onerror = null;
      if (tok !== backTok) return;
      post('log:backdrop load failed ' + b.url);
      b.retry = (b.retry || 0) + 1;
      if (b.retry <= 3) setTimeout(() => { if (tok === backTok) { backKey = ''; applyBackdrop(); } }, 3000 * b.retry);
    };
    img.src = b.url;
  }
  // GPU 重置后底图画布内容也会丢，按原图重画
  backEl.addEventListener('contextrestored', () => { backKey = ''; applyBackdrop(); });
  // 宿主模式下先藏着（不带过渡），等第一条 layout 知道是不是极简模式：普通模式立即显示，极简模式直接睡下（不闪一下天气画面）。
  // 宿主在页面 ready 前排队的消息（天气等）会先于 layout 到达，这之前不决定显隐
  let gotLayout = !host;
  if (host) { sceneEl.style.transition = 'none'; sceneEl.style.opacity = '0'; }

  if (host) {
    host.addEventListener('message', ev => {
      const m = ev.data;
      if (!m || !m.t) return;
      if (m.t === 'csrc' || m.t === 'cfile' || m.t === 'cget') { UserCards.onHost(m); return; }   // 自定义卡片要的脚本、文件、网上的数据：和画面无关
      switch (m.t) {
        case 'layout': {
          const g0 = L.ground, dk0 = L.defKey;
          Object.assign(L, m); gotLayout = true;
          festAt = -1e9;   // 「节日装饰与彩蛋」可能刚开 / 关：马上重新算今天的节日
          if (m.keys) for (const k of Object.keys(weathers)) if (!m.keys.includes(k)) delete weathers[k];   // 不再用到的城市（卡片换了城市）：丢掉它的天气数据
          {   // 自定义效果脚本：第一次照列表加载；之后列表变了（增删、改过文件）就整页重载，旧脚本干干净净地卸掉
            const sig = JSON.stringify(m.effects || []);
            if (userFxSig === null) { userFxSig = sig; loadUserFx(m.effects || []); }
            else if (sig !== userFxSig) { location.reload(); return; }
          }
          if (L.ground !== g0) { drops.length = 0; flakes.length = 0; firstFill = true; }   // 地平线变了：雨雪按新的落点重新生成
          if (syncSkyAlpha()) { resize(); frame(performance.now()); }   // 切换壁纸叠加：天空画布整个换掉，重新分配后马上画一帧——别让合成器拿到一张空白的新画布
          pickWeather(); renderPanel(); applyBackdrop(); reportViewport();
          if (L.defKey !== dk0) UserCards.weatherChanged();   // 默认卡片换了城市（新城市的天气可能早就有了，不会再单独发来）
          break;
        }
        case 'weather':
          weathers[m.key || ''] = m; pickWeather(); renderPanel();
          if ((m.key || '') === L.defKey) UserCards.weatherChanged();   // 自定义卡片的 api.weather 是默认卡片城市的天气
          break;
        case 'preview': setPreview(m.mode, m.day); break;
        case 'fx': fxKind = m.kind || null; tgt = targetScene(); break;
        case 'backdrop': backdrop = m; applyBackdrop(); break;
        case 'pause': paused = true; UserCards.setPaused(true); break;
        case 'perf': L.fps = clamp(m.fps | 0, 5, 60); break;
        case 'unlock': flagWant = true; festAt = -1e9; break;   // 解锁屏幕：国庆节当天这块屏还没升过旗就升一次；锁屏期间可能过了零点，节日马上重算
      }
      applyMinimal();
      // 暂停中（被最大化窗口遮挡）也要保证画面是最新的静态帧，取消遮挡时不会闪黑
      if (paused && !asleep) { firstScene = true; update(0.016); draw(); }
      switch (m.t) {
        case 'resume': if (paused) { paused = false; last = performance.now(); if (flagWant) festAt = -1e9; } UserCards.setPaused(false); break;   // 桌面重新露出来：等着的升旗马上开始
      }
      postScene();
      wake();
    });
    post('ready');
  } else {
    // 浏览器里直接打开调试：?mode=rain&day=1&data=sample.json&panel=0
    const q = new URLSearchParams(location.search);
    L.panel = q.get('panel') !== '0'; L.primary = true; L.ground = 0.0444;
    L.scene = 'dbg'; L.cards = [{ id: 'dbg', key: 'dbg', x: Math.max(0, window.innerWidth - CARD_W - 28), y: 28 }];   // 一张卡片，右上角（尺寸同 .card）
    if (q.has('show')) L.cards[0].show = q.get('show');   // ?show=date,week,lunar,hilo 只显示这几项（高度按内容撑开）
    const cp = /^(\d+),(\d+)$/.exec(q.get('card') || '');   // ?card=40,560 把卡片挪到别处（看节日装饰会不会让开）
    if (cp) { L.cards[0].x = +cp[1]; L.cards[0].y = +cp[2]; }
    L.minimal = q.get('minimal') === '1'; fxKind = q.get('fx') || null;   // ?minimal=1&fx=rainbow 调试极简模式
    // ?overlay=1&sky=0.45&back=图片URL 调试壁纸叠加（back 模拟宿主发来的系统壁纸底图；不填用灰色渐变占位）
    L.overlayWall = q.get('overlay') === '1';
    const sv = parseFloat(q.get('sky')); if (!isNaN(sv)) L.skyOpacity = clamp(sv, 0.05, 1);   // 写 0 夹到最小值，不当缺参
    const cv = parseFloat(q.get('cloud')); if (!isNaN(cv)) L.cloudAlpha = clamp(cv, 0.1, 1);
    if (L.overlayWall) backdrop = { url: q.get('back') || null, color: '#5b6470' };
    if (q.get('manual') === '1') {
      // 录制用：停掉 rAF，由外部按固定步长推进虚拟时钟逐帧绘制（截图慢也不影响动画流畅度）
      let vt = performance.now();
      performance.now = () => vt;
      paused = true;
      window.__step = (dt) => { vt += dt * 1000; update(dt); draw(); };
      window.__snow = v => { snowAcc = +v || 0; stepEaster(0); };   // 手动设降雪积累（配 ?snowfast=1 看雪人 / 融化）
      window.__pv = m => { setPreview(m, false); };   // 调试：切预览天气（雪停 = 雪人开始化）
      window.__snowmen = () => ({ simT, men: men.map(m => m && { born: m.born, dying: m.dying, fadeAt: m.fadeAt, fadeFrom: m.fadeFrom, z: m.z }) });   // 调试：雪人状态（复活连续性测试用）
      window.meteorObj = () => meteor;   // 调试用：看看这颗流星往哪边飞
      window.__fest = () => ({ keys: fest.keys, fw: fest.fw, rockets: rockets.length, bursts: bursts.length, sleigh: !!sleigh, bats: !!bats, daylight: +lastDaylight.toFixed(2), men: men.filter(Boolean).length,
        flag: flagRise ? +(simT - flagRise.t0).toFixed(1) : null, user: userFx.map(u => u.file + (u.on ? ' on' : '') + (u.dead ? ' dead' : '')) });
      window.__unlock = () => { flagWant = true; festAt = -1e9; };
    }
    // ?effects=birthday&fxbase=/web/effects/ 加载自定义效果脚本（调试；宿主里是 layout 带来的文件列表）
    if (q.get('effects')) { userFxSig = '[]'; loadUserFx(q.get('effects').split(',').map(f => ({ f, v: 0 }))); }
    // ?ucard=clock&uh=220&ubase=/web/cards/ 把那张卡片换成自定义卡片（调试；宿主里脚本列表在 layout 里，由宿主读 cards 文件夹）。
    // 浏览器里没有宿主：脚本和 api.readText / image 从 ubase 取，api.fetch* 直接 fetch（对方要允许跨域）；出错写在控制台
    if (q.get('ucard')) {
      const base = q.get('ubase') || '/web/cards/';
      const reply = m => setTimeout(() => UserCards.onHost(m), 0);
      const toData = b => new Promise(ok => { const f = new FileReader(); f.onload = () => ok(f.result); f.readAsDataURL(b); });
      UserCards.init({
        post: m => {
          if (!m || typeof m !== 'object') return;
          if (m.t === 'csrc') fetch(base + m.f + '.js').then(r => r.ok ? r.text() : Promise.reject(new Error('找不到 ' + m.f + '.js')))
            .then(src => reply({ t: 'csrc', f: m.f, v: 0, src }), e => reply({ t: 'csrc', f: m.f, err: e.message }));
          else if (m.t === 'cfile' || m.t === 'cget') {
            fetch(m.t === 'cfile' ? base + m.path : m.url, m.t === 'cget' ? { method: m.method, body: m.body, headers: m.headers } : undefined)
              .then(async r => {
                if (m.t === 'cfile' && !r.ok) throw new Error('cards 文件夹里没有 ' + m.path);
                const o = { t: m.t, rid: m.rid, ok: true, status: r.status, type: r.headers.get('content-type') || '' };
                if (m.kind === 'image') o.data = await toData(await r.blob()); else o.text = await r.text();
                return o;
              })
              .then(reply, e => reply({ t: m.t, rid: m.rid, ok: false, err: e.message }));
          } else if (m.t === 'cstate' && !m.gone) console[m.err ? 'warn' : 'log']('[自定义卡片] ' + (m.err || '正常'));
        },
        weather: () => weathers.dbg && weathers.dbg.fc ? WX.summary(weathers.dbg, Date.now()) : null,
        width: () => CARD_W,
      });
      L.cards[0] = { id: 'dbg', script: q.get('ucard'), v: 0, h: clamp(parseInt(q.get('uh'), 10) || 160, 64, 720), x: L.cards[0].x, y: L.cards[0].y };
    }
    const dd = Date.parse(q.get('date') + 'T00:00:00');   // ?date=2026-11-24 换一天（看月相）
    if (!isNaN(dd)) { const d0 = new Date().setHours(0, 0, 0, 0), real = Date.now; Date.now = () => real() + dd - d0; }
    const apply = () => {
      setPreview(q.get('mode') || 'live', q.get('day') === '1');
      const at = /^(\d+):(\d+)$/.exec(q.get('at') || '');   // ?at=13:00 固定当地时间
      if (at && !dayDemo) demo = { base: dayInfo(Date.now(), locInfo()).mid + (at[1] * 60 + +at[2]) * 60000, start: performance.now(), speed: 1 };
      tgt = targetScene();
      if (syncSkyAlpha()) resize();
      applyBackdrop(); applyMinimal();
      wake();
    };
    if (q.get('data')) fetch(q.get('data')).then(r => r.json()).then(m => { weathers.dbg = m; pickWeather(); apply(); firstScene = true; window.__ready = true; });
    else { apply(); window.__ready = true; }
  }
  // 每分钟：睡着时只刷新卡片（逐小时预报会随时间滚动）；暂停时补一张最新的静态帧
  setInterval(() => {
    pickWeather();   // 画面城市的天气一直没到（断网）时，这里兜底再推一把兜底定时器（正常情况它到点自己会切）
    if (asleep) renderPanel(); else if (paused) { firstScene = true; update(0.016); draw(); }
  }, 60000);
  wake();
})();
