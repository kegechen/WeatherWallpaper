// 天气壁纸 · treeland 版的宿主，代替 Windows 版的 C# 宿主（Program.cs / WeatherService.cs），直接跑在网页里。
//
// main.qml 用 WebEngineScript 在文档创建时注入（比 app.js 先运行），提供 window.chrome.webview：
//   页面 → 宿主：postMessage('ready' / 'log:…' / { t: 'viewport' | 'scene' … })
//   宿主 → 页面：message 事件，ev.data 是消息对象（layout / weather / preview / fx / backdrop / pause / resume）
// 天气在这里联网取：Open-Meteo，接口和参数同 WeatherService.Fetch；位置用配置里的城市 / 经纬度，没配就按 IP 定位。
// main.qml 通过 window.__wwp 通知暂停 / 恢复（pause）和是否在锁屏上（lock）。
//
// 配置（后者覆盖前者）：
//   1. 壁纸包 manifest.json 里的 "host"（main.qml 放进 URL 参数 wwp 传进来）
//   2. /etc/wwp/weather.json（可选，所有天气壁纸包共用；factory 以 dde 用户运行，读不到用户家目录）
//      每 2 秒看一次有没有改过，改了马上生效（设置窗口 wwp-settings 写的就是它），不用切换壁纸
// 字段：
//   city        城市名。只填它会去地名库查经纬度；和 lat/lon 一起填则只用作显示名
//   lat, lon    经纬度
//   fps         页面帧率（5–60，默认 24）
//   panel       显示天气卡片（默认 true）
//   show        卡片上显示哪些内容（城市和当前天气总是显示），同 Windows 版 CardSpec.Show 的键，逗号分隔的字符串或数组：
//               date 公历日期、week 星期、lunar 农历、hilo 今日最高 / 最低、src 定位来源、aqi 空气、feel 体感、hum 湿度、wind 风、
//               hours 今天逐小时、days 未来几天、sun 日出日落、upd 更新时间。不填 = 默认（除日期行外都显示），"" = 只显示城市和当前天气
//   hide        从 show（没填就是默认）里去掉这几项，写法同 show。只想关掉一两项时比列全 show 省事，如 "hide": "hours"
//   cardPos     卡片位置：tl 左上 / tc 顶部居中 / tr 右上（默认）/ bl 左下 / bc 底部居中 / br 右下
//   cardX, cardY  卡片离所靠的左右边、上下边的距离（CSS 像素，默认 32；居中时 cardX 是相对中线的偏移，可以为负）。
//               靠下时从任务栏上沿（ground）算起。超出屏幕时收进来
//   lockZoom    treeland 锁屏界面把壁纸放大的倍数（默认 1.2，同 Greeter.qml）。锁屏时卡片层按它反向缩小，卡片和桌面上一样；1 = 不处理
//   ground      底部任务栏占屏幕高度的比例，雨雪落在它上沿（默认 0.0444）
//   festive     节日装饰与彩蛋（默认 true）
//   backdrop    底层壁纸（本地路径或 URL，默认空 = 没有）。填了就是"壁纸叠加"：垫在天气下面，天空半透明
//   minimal     极简模式（默认 false）：平时只显示底层壁纸 + 卡片，天气有变化时才播放一小段天气动画（同 Windows 版）；
//               没填 backdrop 时垫 treeland 的默认壁纸。和壁纸叠加互斥：两个都开按极简模式
//   playSeconds 每次播放天气动画的秒数（极简模式的播放、雨后彩虹共用，5–3600，默认 30）
//   skyOpacity  叠加时天空的不透明度（0.05–1，默认 0.45）；cloudAlpha 云的不透明度（0.1–1，默认 0.72）
//   refresh     取天气的间隔（分钟，5–180，默认 15）
//   preview     效果预览（默认空 = 实时天气）：clear sunrise sunset night partly cloudy fog drizzle rain heavy thunder snow rainbow，
//               节日 cny lantern dragon midautumn national newyear halloween xmas easter；后面加 " day" 是按这种天气一天快进
(() => {
  'use strict';
  if (window.top !== window) return;   // 只管主页面（自定义卡片的 iframe 不要）

  const P = new URLSearchParams(location.search);
  const SNAPSHOT = P.get('wwpsnap') === '1';   // factory --snapshot 离线渲染缩略图：不联网、不显示卡片，画一个固定场景
  const KEY = 'wwp';   // 只有一个城市：卡片、画面、默认卡片都用它
  const CARD_W = 336;   // 同 app.js
  const USER_CFG = '/etc/wwp/weather.json';
  const DEFAULT_BACKDROP = '/usr/share/backgrounds/default_background.jpg';   // treeland 的 defaultBackground
  const log = msg => console.log('wwp host: ' + msg);
  const num = (v, d, lo, hi) => {
    const n = Number(v);
    return v == null || v === '' || !isFinite(n) ? d : Math.min(hi, Math.max(lo, n));
  };
  const yes = v => v === true || v === 1 || v === 'true';

  if (SNAPSHOT) {
    // 缩略图画白天：把页面的时钟拨到今天 13:00（同 app.js 调试参数 ?date= 的做法），不然晚上编译出来的是一片黑
    const noon = new Date();
    noon.setHours(13, 0, 0, 0);
    const real = Date.now, shift = noon.getTime() - real();
    Date.now = () => real() + shift;
  }

  let base = {};
  try { base = JSON.parse(P.get('wwp') || '{}') || {}; } catch (e) { log('URL 参数 wwp 不是 JSON：' + e.message); }
  let cfg = base;
  // 底层壁纸、极简模式、壁纸叠加：填了 backdrop 就叠加；极简模式优先（它平时不画天气，没有可叠的），缩略图不用极简模式
  const userBackdrop = () => typeof cfg.backdrop === 'string' ? cfg.backdrop.trim() : '';
  const minimal = () => !SNAPSHOT && yes(cfg.minimal);
  const overlayWall = () => !minimal() && userBackdrop() !== '';
  // 卡片显示内容：键和默认值同 Windows 版 Settings.cs 的 ShowKeys / DefaultShow（web/weather.js 也有一份默认值）。
  // 整理成页面要的样子：小写、去空格、去掉不认识的和重复的、按卡片上从上到下的顺序排（页面按逗号直接切，不容错）
  const SHOW_KEYS = ['date', 'week', 'lunar', 'hilo', 'src', 'aqi', 'feel', 'hum', 'wind', 'hours', 'days', 'sun', 'upd'];
  const DEFAULT_SHOW = 'hilo,src,aqi,feel,hum,wind,hours,days,sun,upd';
  const keyList = v => (Array.isArray(v) ? v : typeof v === 'string' ? v.split(',') : [])
    .map(s => String(s).trim().toLowerCase()).filter(Boolean);
  const hasList = v => Array.isArray(v) || typeof v === 'string';
  function cardShow() {
    const want = new Set(keyList(hasList(cfg.show) ? cfg.show : DEFAULT_SHOW));
    for (const k of keyList(cfg.hide)) want.delete(k);
    return SHOW_KEYS.filter(k => want.has(k)).join(',');
  }
  // 卡片高度（CSS 像素），同 Windows 版 CardLayout.HeightOf：各段高度和 style.css 里 .card 下各元素写死的一一对应。
  // 页面拿它摆卡片，节日装饰也按它绕开卡片（不给就按全内容的 446 算，加了日期行的卡片下面一截会被装饰压住）
  function cardHeight(show) {
    const S = new Set(show.split(',')), has = k => S.has(k);
    let h = 1 + 18 + 20;                                                    // 上边框 + 上内边距 + 城市行
    if (has('date') || has('week') || has('lunar')) h += 3 + 17;            // 日期行
    h += 6 + 76;                                                            // 当前天气
    if (has('aqi') || has('feel') || has('hum') || has('wind')) h += 8 + 22;   // 指标标签
    if (has('hours')) h += 10 + 1 + 8 + 79;                                 // 分隔线 + 逐小时
    if (has('days')) h += (has('hours') ? 4 : 10) + 1 + 8 + 142;            // 分隔线 + 未来几天
    if (has('sun') || (has('upd') && has('src'))) h += 12 + 15;             // 底部一行（只有更新时间、不显示定位来源时它放到城市行右边）
    return h + 14 + 1;                                                      // 下内边距 + 下边框
  }

  // ---------------------------------------------------------------- 和页面通信
  const listeners = new Set();
  let ready = false, paused = false, locked = false;
  const send = m => setTimeout(() => {
    for (const f of [...listeners]) {
      try { f({ data: structuredClone(m) }); } catch (e) { console.error('wwp host: 页面处理 ' + (m && m.t) + ' 出错', e); }
    }
  }, 0);
  if (!window.chrome) window.chrome = {};
  window.chrome.webview = {
    postMessage: m => setTimeout(() => fromPage(m), 0),
    addEventListener: (type, f) => { if (type === 'message' && typeof f === 'function') listeners.add(f); },
    removeEventListener: (type, f) => { if (type === 'message') listeners.delete(f); },
  };
  window.__wwp = {
    pause(p) {
      p = !!p;
      if (p === paused) return;
      paused = p;
      if (ready) send({ t: p ? 'pause' : 'resume' });
    },
    // 这一页正显示在锁屏上（main.qml 按 treeland 的事件判断）：卡片层抵消锁屏的放大；极简模式锁屏时不开播
    lock(v) {
      v = !!v;
      if (v === locked) return;
      locked = v;
      log(v ? '锁屏' : '回到桌面');
      if (ready) applyLockZoom();
    },
    refresh: () => refresh(),
  };

  function fromPage(m) {
    if (m === 'ready' || (m && m.t === 'ready')) { onReady(); return; }
    if (typeof m === 'string') { if (m.startsWith('log:')) log('页面：' + m.slice(4)); return; }
    if (m && m.t === 'viewport') {
      log('视口 ' + m.w + 'x' + m.h + ' dpr ' + m.dpr + '（screen ' + screen.width + 'x' + screen.height + '）');
      sendLayout(); updateBackdrop();
    }
  }

  // 本地配置用 XHR 读（fetch 不支持 file:）；没有这个文件就是空配置
  const readLocal = path => new Promise(ok => {
    const x = new XMLHttpRequest();
    x.onload = () => ok(x.responseText);
    x.onerror = () => ok(null);
    try { x.open('GET', 'file://' + path); x.send(); } catch (e) { ok(null); }
  });
  // 用读到的 /etc/wwp/weather.json 原文换掉 cfg。first = 启动时；之后返回 false 表示文件是坏的、没换（改到一半的文件先不理）
  let cfgText = null;
  function setUserCfg(t, first) {
    let user = {};
    if (t) try {
      user = JSON.parse(t);
      if (!user || typeof user !== 'object' || Array.isArray(user)) throw new Error('最外层不是 { }');
    } catch (e) {
      log(USER_CFG + ' 不是合法的 JSON，' + (first ? '忽略' : '先不换') + '：' + e.message);
      if (!first) return false;
      user = {};
    }
    cfg = Object.assign({}, base, user);
    if (t || !first) log((first ? '配置：' : '配置已更新：') + JSON.stringify(user));
    if (minimal()) log('极简模式：平时只显示底层壁纸和卡片，天气变化时播放 ' + playSecs() + ' 秒');
    else if (overlayWall()) log('壁纸叠加：' + userBackdrop());
    const bad = [...keyList(cfg.show), ...keyList(cfg.hide)].filter(k => !SHOW_KEYS.includes(k));
    if (bad.length) log('show / hide 里不认识的项（已忽略）：' + bad.join(',') + '。可用：' + SHOW_KEYS.join(','));
    if (cfg.show != null && !hasList(cfg.show)) log('show 应该是逗号分隔的字符串或数组，按默认内容显示');
    if (hasList(cfg.show) || cfg.hide != null) log('卡片内容：' + (cardShow() || '只有城市和当前天气'));
    return true;
  }
  const cfgReady = (SNAPSHOT ? Promise.resolve(null) : readLocal(USER_CFG)).then(t => {
    cfgText = t;
    setUserCfg(t, true);
    const c = store.get(WX_KEY);   // 上次的天气：12 小时内、位置配置没变就先拿来显示
    if (!SNAPSHOT && c && c.sig === locSig() && c.p && Date.now() - c.p.ts < 12 * 36e5) { payload = c.p; fxOnWeather(payload, false); }
    if (!SNAPSHOT) setInterval(watchCfg, 2000);
  });
  // 配置文件改了（设置窗口保存、手改）：马上按新配置摆卡片、换底图、切模式；位置变了就重新取天气
  let watching = false;
  async function watchCfg() {
    if (watching) return;
    watching = true;
    try {
      const t = await readLocal(USER_CFG);
      if (t === cfgText) return;
      const old = { loc: locSig(), minimal: minimal(), period: period() };
      if (!setUserCfg(t, false)) return;   // 坏文件：cfgText 不更新，下次再读
      cfgText = t;
      if (!ready) return;   // 页面还没好：onReady 会按新配置发
      sendLayout(); updateBackdrop(); applyLockZoom(); sendPreview();
      if (old.minimal && !minimal() && fx.kind && fx.kind !== 'rainbow') { log('关了极简模式，停掉 ' + fx.kind); fx.kind = null; }
      if (old.loc !== locSig()) {
        log('位置配置变了，重新取天气');
        fx.cat = null; fx.lastWet = 0; fx.pending = null;   // 换了城市：不拿上一个城市的天气比"转雨"、彩虹；极简模式会像刚启动一样播一段
        if (started && !busy) { clearTimeout(timer); refresh(); }   // 正在取的那次取完会发现位置变了，自己再取一次
      } else if (old.period !== period() && started && !busy && !fails && lastOk) {
        later(Math.max(0, lastOk + period() - Date.now()));
      }
    } finally { watching = false; }
  }

  // 效果预览（同 Windows 版的托盘“效果预览” / preview 命令，录演示视频、调效果用）：配置里 "preview": "heavy"，
  // 天气或节日的名字见 web/app.js 的 PREV；"clear day" = 按这种天气一天快进；不填或 "live" = 实时天气。
  // 缩略图画多云（太阳、白云、蓝天都有，明亮；雨太灰，叠在控制中心列表里像出了故障）
  let sentPreview = '';
  function sendPreview(force) {   // force：页面刚好（含渲染进程崩溃后重载），不管上次发过什么都发
    const w = SNAPSHOT ? ['partly'] : typeof cfg.preview === 'string' ? cfg.preview.trim().toLowerCase().split(/\s+/) : [];
    const m = { t: 'preview', mode: w[0] || 'live', day: w[1] === 'day' };
    const sig = m.mode + (m.day ? ' day' : '');
    if (sig === sentPreview && !force) return;
    if (sentPreview || m.mode !== 'live') log('效果预览：' + sig);
    sentPreview = sig;
    send(m);
  }

  async function onReady() {
    await cfgReady;
    ready = true;
    sendLayout(true);
    if (locked) applyLockZoom();
    sendPreview(true);
    fxSent = fx.kind;   // 页面重载（渲染进程崩溃）时正在播的接着播
    send({ t: 'fx', kind: fx.kind });
    if (payload) send(payload);
    // 缩略图也发 pause：暂停时页面每收到一条消息就直接画出目标场景的完整一帧（不走几秒的过渡），截图时机就无所谓了
    if (paused || SNAPSHOT) send({ t: 'pause' });
    if (SNAPSHOT) {   // 再放开 1.5 秒让雨落满全屏（叠加底图到达时雨会重新生成，只剩顶上一截），然后定住等截图
      setTimeout(() => send({ t: 'resume' }), 400);
      setTimeout(() => send({ t: 'pause' }), 1900);
    }
    updateBackdrop();
    if (!started && !SNAPSHOT) {
      started = true;
      // factory 按路径一个壁纸一个页面：桌面和锁屏用不同的天气壁纸包时有两个页面，共用 localStorage，另一个刚取过就不用再取
      const age = payload ? Date.now() - payload.ts : Infinity;
      if (age < period()) { lastOk = payload.ts; later(period() - age); }
      else refresh();
    }
  }

  let sentLayout = '';
  function sendLayout(force) {
    const W = window.innerWidth || 1440, H = window.innerHeight || 960;
    const show = cardShow(), h = cardHeight(show), ground = num(cfg.ground, 0.0444, 0, 0.3);
    // 位置同 Windows 版 CardSpec.Pos：第一个字母贴上 / 下边，第二个字母靠左 / 中 / 右
    const pos = /^[tb][lcr]$/.test(cfg.cardPos) ? cfg.cardPos : 'tr';
    const ox = num(cfg.cardX, 32, pos[1] === 'c' ? -1e4 : 0, 1e4), oy = num(cfg.cardY, 32, 0, 1e4);
    const x = pos[1] === 'l' ? ox : pos[1] === 'r' ? W - CARD_W - ox : (W - CARD_W) / 2 + ox;
    const y = pos[0] === 'b' ? H * (1 - ground) - oy - h : oy;
    const card = {
      id: KEY, key: KEY, h, show,
      x: Math.round(Math.max(0, Math.min(x, W - CARD_W))), y: Math.round(Math.max(0, Math.min(y, H - h))),
    };
    const m = {
      t: 'layout', primary: true, ground, overlay: false,
      panel: !SNAPSHOT && cfg.panel !== false, noWeather: false,
      fps: num(cfg.fps, 24, 5, 60) | 0, lowmem: !!cfg.lowmem, minimal: minimal(),
      overlayWall: overlayWall(), skyOpacity: num(cfg.skyOpacity, 0.45, 0.05, 1), cloudAlpha: num(cfg.cloudAlpha, 0.72, 0.1, 1),
      festive: cfg.festive !== false, scene: KEY, screen: 'treeland', cards: [card],
      effects: [],   // 自定义效果脚本暂不支持（列表一变页面会整页重载，这里保持不变）
      defKey: KEY, keys: [KEY],
    };
    const j = JSON.stringify(m);
    if (!ready || (!force && j === sentLayout)) return;
    sentLayout = j;
    send(m);
  }
  // 锁屏：treeland 的锁屏界面（Greeter.qml）把壁纸以屏幕中心放大 1.2 倍再裁掉四周（每边各少 1/12），靠边的卡片会被裁掉。
  // 这里让卡片层反过来以屏幕中心缩小到 1/1.2，两者抵消：卡片在锁屏上的位置、大小和桌面上一模一样，天气画面照样放大。
  // 只动卡片层（#cards，里面的 .card 是 position: fixed）：先让它铺满视口，加了 transform 之后卡片改以它为参照，位置不变；
  // 过渡用 easeOutExpo，和 treeland 的缩放动画（1 秒 OutExpo）大致同步
  function applyLockZoom() {
    const el = document.getElementById('cards');
    if (!el) return;
    if (!el.dataset.wwpLock) {
      el.style.position = 'fixed';
      el.style.inset = '0';
      el.style.transformOrigin = '50% 50%';
      el.style.transition = 'transform .6s cubic-bezier(.16, 1, .3, 1)';
    }
    const z = locked ? num(cfg.lockZoom, 1.2, 1, 3) : 1;
    el.dataset.wwpLock = String(z);
    el.style.transform = z === 1 ? '' : 'scale(' + (1 / z).toFixed(5) + ')';
  }

  let resizeTm = 0;
  window.addEventListener('resize', () => {
    clearTimeout(resizeTm);
    resizeTm = setTimeout(() => { sendLayout(); updateBackdrop(); }, 300);
  });

  // ---------------------------------------------------------------- 壁纸叠加的底图
  // 页面把底图按图片本身的像素画进整屏画布再拉伸铺满（见 app.js applyBackdrop），所以这里先按屏幕物理分辨率
  // 裁成"铺满"（cover）的样子，和 Windows 版 Backdrop.cs 生成的一样，否则比例不同的图会被拉变形
  let backSig = '', backUrl = '';
  // 壁纸叠加用配置里的 backdrop；极简模式平时只有它，没配就垫 treeland 的默认壁纸
  function updateBackdrop() {
    if (!ready) return;
    const src = minimal() ? userBackdrop() || DEFAULT_BACKDROP : userBackdrop();
    if (!src) {
      if (backSig) { backSig = ''; send({ t: 'backdrop', url: null }); }
      return;
    }
    const dpr = window.devicePixelRatio || 1;
    const W = Math.round(window.innerWidth * dpr), H = Math.round(window.innerHeight * dpr);
    const sig = src + '|' + W + 'x' + H;
    if (!W || !H || sig === backSig) return;
    backSig = sig;
    const url = /^[a-z][a-z0-9+.-]*:/i.test(src) ? src : 'file://' + encodeURI(src);
    const img = new Image();
    img.onload = () => {
      if (backSig !== sig) return;
      const c = document.createElement('canvas');
      c.width = W; c.height = H;
      const g = c.getContext('2d', { alpha: false });
      const s = Math.max(W / img.naturalWidth, H / img.naturalHeight);
      const w = img.naturalWidth * s, h = img.naturalHeight * s;
      g.imageSmoothingQuality = 'high';
      g.drawImage(img, (W - w) / 2, (H - h) / 2, w, h);
      let color = '#5b6470';
      try {
        const one = document.createElement('canvas');
        one.width = one.height = 1;
        const o = one.getContext('2d');
        o.drawImage(c, 0, 0, 1, 1);
        const d = o.getImageData(0, 0, 1, 1).data;
        color = '#' + [d[0], d[1], d[2]].map(v => v.toString(16).padStart(2, '0')).join('');
        c.toBlob(b => {
          if (backSig !== sig) return;
          if (!b) { send({ t: 'backdrop', url, color }); return; }
          const old = backUrl;
          backUrl = URL.createObjectURL(b);   // 一直留着：GPU 重置后页面会按这个地址重画底图
          send({ t: 'backdrop', url: backUrl, color });
          if (old) setTimeout(() => URL.revokeObjectURL(old), 10000);
          log('底层壁纸 ' + src + ' → ' + W + 'x' + H);
        }, 'image/jpeg', 0.92);
      } catch (e) {
        // 画布被认为跨源（读不出像素）：直接给原图，比例不同时会拉伸
        log('底层壁纸裁剪失败，用原图：' + e.message);
        send({ t: 'backdrop', url, color });
      }
    };
    img.onerror = () => {
      if (backSig !== sig) return;
      log('底层壁纸读不到：' + src + '（改用纯色。factory 以 dde 用户运行，读不了用户家目录，图片请放到 /usr/local/share/wallpapers 这类地方）');
      send({ t: 'backdrop', url: null, color: '#5b6470' });
    };
    img.src = url;
  }

  // ---------------------------------------------------------------- 天气
  const LOC_KEY = 'wwp.loc', WX_KEY = 'wwp.weather';
  const store = {
    get(k) { try { return JSON.parse(localStorage.getItem(k)); } catch (e) { return null; } },
    set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch (e) { /* 存不下就算了 */ } },
  };
  const locSig = () => JSON.stringify([cfg.city || '', cfg.lat == null ? '' : cfg.lat, cfg.lon == null ? '' : cfg.lon]);

  async function getJson(url, ms = 15000) {
    const ac = new AbortController(), tm = setTimeout(() => ac.abort(), ms);
    try {
      const r = await fetch(url, { signal: ac.signal, cache: 'no-store' });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return await r.json();
    } catch (e) {
      throw new Error(url.split('?')[0] + ' ' + (e.name === 'AbortError' ? '超时' : e.message));
    } finally { clearTimeout(tm); }
  }

  async function reverseName(lat, lon) {
    try {
      const d = await getJson('https://api.bigdatacloud.net/data/reverse-geocode-client?latitude=' + lat + '&longitude=' + lon + '&localityLanguage=zh', 10000);
      const city = d.city || d.principalSubdivision || '', locality = d.locality || '';
      return locality && locality !== city ? city + ' · ' + locality : city;
    } catch (e) { log('反查地名失败：' + e.message); return ''; }
  }

  async function locate() {
    // 手写配置时 "lat": "" 也算没填（Number("") 是 0，会跑到几内亚湾去）
    const coord = v => typeof v === 'number' || (typeof v === 'string' && v.trim() !== '') ? Number(v) : NaN;
    const lat = coord(cfg.lat), lon = coord(cfg.lon);
    if (isFinite(lat) && isFinite(lon) && Math.abs(lat) <= 90 && Math.abs(lon) <= 180)
      return { name: cfg.city || lat.toFixed(2) + ', ' + lon.toFixed(2), lat, lon, src: 'manual' };
    const sig = locSig(), c = store.get(LOC_KEY);
    const fresh = c && c.sig === sig && c.loc && Date.now() - c.at < (cfg.city ? 30 * 864e5 : 6 * 36e5);   // 按城市名查的记 30 天，IP 定位记 6 小时
    if (fresh) return c.loc;
    try {
      let loc;
      if (cfg.city) {
        const d = await getJson('https://geocoding-api.open-meteo.com/v1/search?count=10&language=zh&format=json&name=' + encodeURIComponent(cfg.city));
        const r = (d.results || [])[0];
        if (!r) throw new Error('地名库里没有“' + cfg.city + '”，请在配置里改填 lat、lon');
        loc = { name: cfg.city, lat: r.latitude, lon: r.longitude, src: 'manual' };
      } else {
        const d = await getJson('http://ip-api.com/json/?lang=zh-CN', 10000);
        if (d.status !== 'success') throw new Error('IP 定位失败：' + (d.message || d.status));
        loc = { name: '', lat: d.lat, lon: d.lon, src: 'ip' };
        loc.name = (await reverseName(loc.lat, loc.lon)) || d.city || '当前位置';
      }
      store.set(LOC_KEY, { sig, at: Date.now(), loc });
      log('位置：' + loc.name + ' ' + loc.lat.toFixed(4) + ',' + loc.lon.toFixed(4) + '（' + loc.src + '）');
      return loc;
    } catch (e) {
      if (c && c.sig === sig && c.loc) { log('定位失败，沿用上次的位置：' + e.message); return c.loc; }
      throw e;
    }
  }

  async function fetchWeather(loc) {
    const ll = 'latitude=' + loc.lat.toFixed(4) + '&longitude=' + loc.lon.toFixed(4);
    const fcP = getJson('https://api.open-meteo.com/v1/forecast?' + ll +
      '&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,precipitation,rain,showers,snowfall,weather_code,cloud_cover,wind_speed_10m,wind_direction_10m' +
      '&hourly=temperature_2m,weather_code,precipitation_probability,is_day' +
      '&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset,precipitation_probability_max' +
      '&timezone=auto&forecast_days=6');
    const aqP = getJson('https://air-quality-api.open-meteo.com/v1/air-quality?' + ll +
      '&current=us_aqi,pm2_5,pm10,ozone,nitrogen_dioxide,carbon_monoxide&timezone=auto')
      .catch(e => { log('空气质量取不到：' + e.message); return null; });
    const fc = await fcP, aq = await aqP;
    return { t: 'weather', key: KEY, loc: { name: loc.name, lat: loc.lat, lon: loc.lon, src: loc.src }, fc, aq, ts: Date.now() };
  }

  let payload = null, started = false, busy = false, fails = 0, lastOk = 0, timer = 0, dueAt = 0;
  const period = () => num(cfg.refresh, 15, 5, 180) * 60000;
  // 下一次取天气：dueAt 记墙上时间，给下面每分钟的兜底用（失败退避时也按退避的时间算）
  function later(ms) {
    clearTimeout(timer);
    dueAt = Date.now() + ms;
    timer = setTimeout(refresh, ms);
  }
  async function refresh() {
    if (busy) return;
    busy = true;
    clearTimeout(timer);
    dueAt = 0;
    try {
      const sig = locSig();
      const p = await fetchWeather(await locate());
      if (sig !== locSig()) { later(0); return; }   // 取的过程中设置里换了位置：这份作废，按新位置再取
      payload = p; lastOk = Date.now(); fails = 0;
      store.set(WX_KEY, { sig: locSig(), p });
      fxOnWeather(p, true);
      if (ready) send(p);
      const cur = p.fc && p.fc.current;
      log('天气已更新：' + p.loc.name + (cur ? ' ' + cur.temperature_2m + '°C 天气代码 ' + cur.weather_code : ''));
      later(period());
    } catch (e) {
      fails++;
      const wait = [30, 60, 120, 300, 600][Math.min(fails, 5) - 1];
      log('取天气失败（第 ' + fails + ' 次，' + wait + ' 秒后重试）：' + e.message);
      later(wait * 1000);
    } finally { busy = false; }
  }
  // 另一个页面（桌面 / 锁屏）取到了更新的天气：直接用，自己的定时器重新计时
  window.addEventListener('storage', e => {
    if (e.key !== WX_KEY || !started) return;
    const c = store.get(WX_KEY);
    if (!c || c.sig !== locSig() || !c.p || (payload && c.p.ts <= payload.ts)) return;
    payload = c.p; lastOk = c.p.ts; fails = 0;
    fxOnWeather(payload, true);
    if (!busy) later(period());
    if (ready) send(payload);
  });
  // 待机唤醒后定时器会拖后（睡着的时间不算），每分钟按墙上时间兜底：过了预定时间一分钟还没取就马上取
  // （按 dueAt 判断，断网退避期间不会每分钟都去取）；网络恢复时马上重试
  setInterval(() => { if (started && !busy && dueAt && Date.now() > dueAt + 60000) refresh(); }, 60000);
  window.addEventListener('online', () => { if (started && fails > 0) setTimeout(refresh, 3000); });

  // ---- 天气动画"播一次"（极简模式）与雨后彩虹，同 Windows 版 WeatherFx.cs + Program.FxStep
  // 极简模式平时不画天气，只在这些时候整屏播放 playSeconds 秒：拿到第一份天气、转雨、转暴雨、转雷雨、转雪、雨后转晴（彩虹）。
  // 彩虹普通模式也有（雨停 90 分钟内转晴的白天挂同样长的时间）。同类一小时内不重复播。
  // 锁屏时先记下，解锁后再确认一次天气还对得上才播。Windows 版还会等"桌面露出来、人在操作"，treeland 下 factory 看不到这两样，不判断
  const fx = { kind: null, pending: null, cat: null, day: true, left: 0, lastWet: 0, bowFor: 0, shown: {} };
  let fxSent = null;
  const playSecs = () => num(cfg.playSeconds, 30, 5, 3600) | 0;
  const fxCat = c =>
    c >= 95 ? 'thunder' :
    (c >= 71 && c <= 77) || c === 85 || c === 86 ? 'snow' :
    c === 65 || c === 67 || c === 82 ? 'heavy' :
    (c >= 51 && c <= 67) || (c >= 80 && c <= 82) ? 'rain' :
    c === 45 || c === 48 ? 'fog' :
    c === 3 ? 'cloudy' : 'clear';
  const wet = c => c === 'rain' || c === 'heavy' || c === 'thunder';
  const bowDue = () => fx.cat === 'clear' && fx.day && fx.lastWet > fx.bowFor && Date.now() - fx.lastWet < 90 * 60000;

  // 每拿到一份实时天气调用一次，和上一份比要不要播。fresh = false：启动时的缓存（可能是昨天的雨，不拿来判断"刚下过雨"）
  function fxOnWeather(p, fresh) {
    if (SNAPSHOT) return;
    const cur = p && p.fc && p.fc.current;
    const code = cur ? Number(cur.weather_code) : NaN;
    if (!isFinite(code)) return;
    const prev = fx.cat, cat = fxCat(code);
    fx.cat = cat;
    fx.day = cur.is_day == null || Number(cur.is_day) !== 0;
    if (wet(cat) && fresh) fx.lastWet = Date.now();
    let k = null;
    if (prev == null) k = 'start';
    else if (cat !== prev) {
      if (cat === 'snow') k = 'snow';
      else if (cat === 'thunder') k = 'thunder';
      else if (cat === 'heavy' && prev !== 'thunder') k = 'heavy';
      else if (cat === 'rain' && !wet(prev)) k = 'rain';
    }
    if (!minimal()) k = null;   // 普通模式一直在播天气，只有彩虹需要额外挂一下
    if (k == null && bowDue()) k = 'rainbow';
    if (k && k !== 'start' && fx.shown[k] && Date.now() - fx.shown[k] < 36e5) k = null;
    if (k && k !== fx.pending && k !== fx.kind) { fx.pending = k; log('天气动画等着播：' + k + '（' + prev + ' → ' + cat + '）'); }
  }
  // 延后的动画到真要播时再确认一次：天气已经变回去了就不播
  function fxStillDue(k) {
    switch (k) {
      case 'snow': return fx.cat === 'snow';
      case 'rain': case 'heavy': case 'thunder': return wet(fx.cat);
      case 'rainbow': return bowDue();
      default: return true;
    }
  }
  function fxPlay(k) {
    fx.kind = k; fx.left = playSecs();
    if (fx.pending !== 'rainbow') fx.pending = null;   // 正在按当前天气播了，等着的转雨 / 下雪不用再播一遍（彩虹画面不同，留着）
    fx.shown[k] = Date.now();
    if (k === 'rainbow') fx.bowFor = fx.lastWet;
    log('播放天气动画 ' + k + ' ' + fx.left + ' 秒');
  }
  if (!SNAPSHOT) {
    let tick = Date.now();
    setInterval(() => {
      const now = Date.now(), dt = Math.min(2, Math.max(0, (now - tick) / 1000));   // 待机唤醒后不一下子扣掉一大截
      tick = now;
      if (fx.kind) {
        fx.left -= dt;
        if (fx.left <= 0) { log('天气动画播完：' + fx.kind); fx.kind = null; }
      }
      if (!fx.kind && fx.pending && !locked) {   // 紧接着播下一段：直接换类型，不先淡出再淡入
        const k = fx.pending;
        fx.pending = null;
        if (fxStillDue(k)) fxPlay(k);
        else log('天气已经变了，不播 ' + k + '（现在是 ' + fx.cat + '）');
      }
      if (ready && fx.kind !== fxSent) { fxSent = fx.kind; send({ t: 'fx', kind: fx.kind }); }
    }, 1000);
  }
})();
