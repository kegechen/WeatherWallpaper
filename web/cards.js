'use strict';
// 自定义卡片的运行环境（宿主那一半在 src/CustomCards.cs；写法见 README「自定义卡片」和 cards 文件夹里的示例）。
//
// 每张自定义卡片一个 Worker 跑用户脚本，怎么防不允许的行为：
//  · Worker 由一个带 CSP 的隐藏 iframe 创建，继承它的 CSP（default-src 'none'）：fetch / XHR / WebSocket / importScripts / import() / eval 全被拦，断网；
//  · 启动代码（BOOT）和用户脚本拼成同一个脚本，BOOT 先跑：把 postMessage、定时器、importScripts、fetch 等全局删掉，用户代码只能通过 api 找宿主要数据
//    （联网、读 cards 文件夹由宿主做，有大小、超时、频率限制）；没有 DOM、没有定时器，什么时候重画由这边按 refresh 安排；
//  · 卡住（死循环）：忙的时候每 0.5 秒 ping 一次，2 秒没回就 terminate。画完之后、闲着时交给它请求结果之后也要回一次 ping 才算闲下来
//    （render 里留下的、请求回来才跑的 Promise 死循环也算卡住）；能当定时器用的 scheduler、Atomics.waitAsync 也删掉。
//    在循环里一直找宿主要数据的（每次 await 都让出来，ping 照回）按请求的次数认：一分钟超过 API_PER_MIN 个也 terminate；
//  · render 返回的 HTML 先过白名单检查（script / iframe / 表单 / 链接 / on* / javascript: / 动画 / 外部 url() / 非 data: 图片 / 动图都算出错，并说明原因），
//    再放进一个不许运行脚本、CSP 只许内联样式和 data: 图片的 iframe 里显示（检查漏了的，CSP 兜底）；
//  · 出错（含上面所有情况）卡片就藏起来（位置保留，桌面不跳），原因报给宿主（写日志、卡片设置里显示），好了自动出来
//    （画好、脚本闲下来之后才出来：画完之后才出的错会在那之前报上来，卡片不会闪一下）。
//  · 内存挡不住：脚本把内存吃光会让整个页面崩溃，宿主重新载入页面，接连崩溃就先不运行自定义卡片（index.html?nocards=1）。
const UserCards = (() => {
  const LOAD_HANG = 5000, HANG = 2000, TICK_MAX = 40000, HOST_WAIT = 30000, MAX_HTML = 1 << 20, MAX_API = 16, API_PER_MIN = 600;
  // 出错后多久重试：宿主那边同一个请求 60 秒内直接给上次的结果，早于 60 秒重试拿到的还是那个错误
  const RETRY = 61000;
  const NOCARDS = /[?&]nocards=1/.test(location.search);
  // cardW：卡片宽度（CSS 像素），app.js 在 init 里给（它的 CARD_W，和 style.css、宿主的 CardLayout.W 是同一个数），这里不再另写一份
  let post = () => {}, getWeather = () => null, cardW = () => 0, paused = false;

  // ------------------------------------------------------------------ Worker 的启动代码
  // 在用户脚本前面、同一个脚本里先跑。LIB = lunar.js + icons.js 的源码（农历、天气图标，api.date / api.icon 用），关在函数里不污染全局。
  // 注意：这里任何改动都会改变用户代码的行号偏移，偏移是数出来的（OFFSET），不用手动改
  const boot = lib => `(function () {
'use strict';
const send = self.postMessage.bind(self), listen = self.addEventListener.bind(self);
const LIB = (function () {
${lib}
return { LUNAR: self.LUNAR, Icons: typeof Icons !== 'undefined' ? Icons : null };
})();
try { delete self.LUNAR; } catch (e) {}
const GONE = ['postMessage', 'setTimeout', 'setInterval', 'clearTimeout', 'clearInterval', 'importScripts', 'fetch', 'XMLHttpRequest', 'WebSocket', 'EventSource',
  'WebTransport', 'close', 'BroadcastChannel', 'MessageChannel', 'Worker', 'SharedWorker', 'requestAnimationFrame', 'cancelAnimationFrame', 'indexedDB', 'caches', 'onmessage', 'scheduler'];
const unset = (o, k) => {
  try { delete o[k]; } catch (e) {}
  if (Object.prototype.hasOwnProperty.call(o, k)) try { Object.defineProperty(o, k, { value: undefined, writable: false, configurable: false }); } catch (e) {}
};
for (let o = self; o && o !== Object.prototype; o = Object.getPrototypeOf(o))
  for (const k of GONE) if (Object.prototype.hasOwnProperty.call(o, k)) unset(o, k);
// 带超时的也是定时器：Atomics.waitAsync(…, 毫秒)、AbortSignal.timeout(毫秒) 到点会回调
if (typeof Atomics === 'object') unset(Atomics, 'waitAsync');
if (typeof AbortSignal === 'function') unset(AbortSignal, 'timeout');
let def = null, calls = 0, seqNow = 0, rid = 0;
Object.defineProperty(self, 'WeatherWallpaper', { value: Object.freeze({ card(d) { if (++calls === 1) def = d; }, version: 1 }), enumerable: false });
const pend = new Map();
const call = (op, args) => new Promise((ok, no) => { const id = ++rid; pend.set(id, { ok, no }); send({ t: 'api', rid: id, op, args }); });
const short = s => { s = String(s); return s.length > 100 ? s.slice(0, 100) + '…' : s; };
const errText = e => {
  if (e instanceof Error) return (e.name && e.name !== 'Error' ? e.name + ': ' : '') + e.message;
  let s;
  try { s = e && typeof e === 'object' ? JSON.stringify(e) : String(e); } catch (x) { s = Object.prototype.toString.call(e); }   // 循环引用、BigInt、toString 抛错
  return 'throw 了 ' + short(s);
};
const esc = s => String(s == null ? '' : s).replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[ch]);
const WEEK = ['星期日', '星期一', '星期二', '星期三', '星期四', '星期五', '星期六'], SHORT = ['周日', '周一', '周二', '周三', '周四', '周五', '周六'];
const DAY = 86400000, pad = n => (n < 10 ? '0' : '') + n;
// 只有日期的 '2027-01-01' 按标准是 UTC 零点（西半球就成了前一天）：补上时间，按本地零点算（'2027-01-01T00:00' 这样带时间不带时区的是本地时间）
const toDate = (t, fn) => { const d = t == null ? new Date() : t instanceof Date ? new Date(t.getTime()) : new Date(typeof t === 'string' && /^\\d{4}-\\d{2}-\\d{2}$/.test(t.trim()) ? t.trim() + 'T00:00' : t); if (isNaN(d.getTime())) throw new TypeError(fn + '：不是有效的日期 ' + short(t)); return d; };
const localMs = d => d.getTime() - d.getTimezoneOffset() * 60000;
function date(t) {
  const d = toDate(t, 'api.date'), lm = localMs(d), l = LIB.LUNAR ? LIB.LUNAR.of(lm) : null;
  return { year: d.getFullYear(), month: d.getMonth() + 1, day: d.getDate(), hour: d.getHours(), minute: d.getMinutes(), second: d.getSeconds(),
    weekday: d.getDay(), week: WEEK[d.getDay()], weekShort: SHORT[d.getDay()], text: (d.getMonth() + 1) + '月' + d.getDate() + '日',
    time: pad(d.getHours()) + ':' + pad(d.getMinutes()), lunar: LIB.LUNAR ? LIB.LUNAR.text(lm) : '', lunarMonth: l ? l.month : null, lunarDay: l ? l.day : null, lunarLeap: l ? l.leap : false };
}
const midnight = d => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
function daysUntil(t) { return Math.round((midnight(toDate(t, 'api.daysUntil')) - midnight(new Date())) / DAY); }
function nextLunar(month, day) {
  if (!LIB.LUNAR) throw new Error('api.nextLunar：农历数据不可用');
  month = Number(month); day = Number(day);
  if (!(month >= 1 && month <= 12 && day >= 1 && day <= 30)) throw new RangeError('api.nextLunar(月, 日)：月 1–12，日 1–30');
  const now = new Date();
  for (let i = 0; i < 400; i++) {
    const d = new Date(now.getFullYear(), now.getMonth(), now.getDate() + i), l = LIB.LUNAR.of(localMs(d));
    if (l && !l.leap && l.month === month && l.day === day) return d.getTime();
  }
  // 这一年里没有这一天（如腊月三十只在大月有）：取那个月的最后一天
  for (let i = 0; i < 400; i++) {
    const d = new Date(now.getFullYear(), now.getMonth(), now.getDate() + i), l = LIB.LUNAR.of(localMs(d)), n = LIB.LUNAR.of(localMs(d) + DAY);
    if (l && !l.leap && l.month === month && l.day < day && n && (n.month !== month || n.leap)) return d.getTime();
  }
  return null;
}
const KINDS = ['clear', 'partly', 'cloudy', 'fog', 'drizzle', 'rain', 'heavy', 'thunder', 'snow', 'sleet'];
function icon(kind, isDay) {
  if (!LIB.Icons) return '';
  if (KINDS.indexOf(kind) < 0) throw new TypeError('api.icon：不认识的天气类型 ' + short(kind) + '（可以用 ' + KINDS.join(' / ') + '）');
  return LIB.Icons.svg(kind, isDay !== false);
}
function fetchOpt(o) {
  if (o == null) return { method: 'GET', body: null, headers: {} };
  if (typeof o !== 'object') throw new TypeError('第二个参数要是 { method, body, headers } 这样的对象');
  let body = o.body == null ? null : o.body;
  const headers = {};
  if (o.headers != null) for (const k of Object.keys(o.headers)) headers[k] = String(o.headers[k]);
  if (body != null && typeof body !== 'string') {
    body = JSON.stringify(body);
    if (!Object.keys(headers).some(k => k.toLowerCase() === 'content-type')) headers['Content-Type'] = 'application/json';
  }
  return { method: o.method ? String(o.method).toUpperCase() : body != null ? 'POST' : 'GET', body, headers };
}
async function get(kind, url, o) {
  const r = await call('fetch', Object.assign({ kind, url: String(url) }, fetchOpt(o)));
  if (r.status >= 400) throw new Error('HTTP ' + r.status + '：' + short(url));
  return r;
}
async function readJson(p) {
  const t = await call('file', { kind: 'text', path: String(p) }).then(r => r.text);
  try { return JSON.parse(t.replace(/^\\uFEFF/, '')); } catch (e) { throw new SyntaxError('「' + short(p) + '」不是合法的 JSON：' + e.message); }
}
// used.w：这一轮有没有读 api.weather（没读过的卡片天气更新时不用重画）。每一轮各记各的：被放弃的那一轮 data() 还在跑、读了天气，不算到新的一轮上
const mkApi = (m, used) => Object.freeze({
  now: m.now, get weather() { used.w = true; return m.weather || null; }, card: Object.freeze({ width: m.w, height: m.h, name: m.name }),
  fetchText: async (u, o) => (await get('text', u, o)).text,
  fetchJson: async (u, o) => { const r = await get('text', u, o); try { return JSON.parse(r.text.replace(/^\\uFEFF/, '')); } catch (e) { throw new SyntaxError('返回的不是 JSON：' + short(u)); } },
  fetchImage: async (u, o) => (await get('image', u, o)).data,
  readText: async p => (await call('file', { kind: 'text', path: String(p) })).text,
  readJson, image: async p => (await call('file', { kind: 'image', path: String(p) })).data,
  esc, date, daysUntil, nextLunar, icon,
});
function init() {
  const bad = msg => send({ t: 'bad', msg });
  if (calls === 0) return bad('脚本里没有调用 WeatherWallpaper.card({ ... })');
  if (calls > 1) return bad('WeatherWallpaper.card() 只能调用一次（现在调用了 ' + calls + ' 次）');
  const d = def;
  if (!d || typeof d !== 'object') return bad('WeatherWallpaper.card() 要传一个对象：{ name, height, refresh, data, render }');
  if (typeof d.render !== 'function') return bad('缺少 render(d, api) 函数（它返回卡片的 HTML）');
  if (d.data != null && typeof d.data !== 'function') return bad('data 要是函数：async data(api) { … }');
  const refresh = d.refresh == null ? 60 : Number(d.refresh);
  if (!(refresh >= 1)) return bad('refresh 要是不小于 1 的秒数（现在是 ' + short(d.refresh) + '）');
  if (d.style != null && typeof d.style !== 'string') return bad('style 要是 CSS 文本（字符串）');
  send({ t: 'def', name: d.name == null ? '' : String(d.name), refresh, style: d.style || '' });
}
async function tick(m) {
  seqNow = m.seq;
  const used = { w: false };
  try {
    const a = mkApi(m, used);
    let v;
    if (def.data) { send({ t: 'phase', seq: m.seq, p: 'data' }); v = await def.data(a); }
    if (m.seq !== seqNow) return;   // 这一轮已经被放弃（data 太久），下一轮已经开始
    send({ t: 'phase', seq: m.seq, p: 'render' });
    const html = def.render(v, a);
    if (html && typeof html.then === 'function') throw new TypeError('render() 不能是 async：要等的东西放到 data() 里，render 直接返回 HTML 字符串');
    if (typeof html !== 'string' && typeof html !== 'number') throw new TypeError('render() 要返回 HTML 字符串（现在返回的是 ' + (html === null ? 'null' : typeof html) + '）');
    send({ t: 'out', seq: m.seq, html: String(html), w: used.w });
  } catch (e) { send({ t: 'fail', seq: m.seq, msg: errText(e), stack: e && e.stack ? String(e.stack) : '', w: used.w }); }
}
// 只认浏览器发来的事件（isTrusted）：用户代码 dispatchEvent 一个假的 { t: 'ping' } 就能替自己回 pong，死循环也不会被发现
listen('unhandledrejection', e => { if (!e.isTrusted) return; e.preventDefault(); const x = e.reason; send({ t: 'stray', msg: '没有处理的 Promise 错误：' + errText(x), stack: x && x.stack ? String(x.stack) : '' }); });
listen('message', e => {
  if (!e.isTrusted) return;
  const m = e.data;
  if (!m || typeof m !== 'object') return;
  switch (m.t) {
    case 'ping': send({ t: 'pong', n: m.n }); break;
    case 'init': init(); break;
    case 'tick': tick(m); break;
    case 'res': { const p = pend.get(m.rid); if (p) { pend.delete(m.rid); if (m.ok) p.ok(m.value); else p.no(new Error(m.err)); } break; }
  }
});
})();
`;

  // ------------------------------------------------------------------ 隐藏的 iframe：只用来创建 Worker（Worker 继承它的 CSP）
  let libSrc = null, libPromise = null, prefix = '', OFFSET = 0, spawner = null, spawnerPromise = null;
  function ready() {
    if (!libPromise) libPromise = Promise.all(['lunar.js', 'icons.js'].map(f => fetch(f).then(r => r.ok ? r.text() : '').catch(() => '')))
      .then(([lunar, icons]) => {
        libSrc = lunar + '\n;\n' + icons + '\n';
        prefix = boot(libSrc) + '\n';
        OFFSET = (prefix.match(/\n/g) || []).length;   // 用户脚本第 1 行在整个脚本里是第 OFFSET + 1 行
      });
    if (!spawnerPromise) spawnerPromise = new Promise(res => {
      const f = document.createElement('iframe');
      f.style.display = 'none';
      f.setAttribute('aria-hidden', 'true');
      f.srcdoc = `<!doctype html><meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src blob:; worker-src blob:">`;
      f.onload = () => { spawner = f.contentWindow; res(); };
      document.body.appendChild(f);
    });
    return Promise.all([libPromise, spawnerPromise]);
  }

  // ------------------------------------------------------------------ 显示卡片内容的 iframe：不许运行脚本；CSP 只许内联样式和 data: 图片
  const FRAME = `<!doctype html><html><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:">
<style>html, body { margin: 0; height: 100%; overflow: hidden; background: transparent; }
body { box-sizing: border-box; padding: 16px 20px; color: #fff; font: 14px/1.45 "Segoe UI Variable Display", "Segoe UI", "Microsoft YaHei UI", "Microsoft YaHei", sans-serif;
  text-shadow: 0 1px 4px rgba(0,0,0,.3); user-select: none; cursor: default; }</style>
<style id="u"></style></head><body></body></html>`;

  // ------------------------------------------------------------------ HTML 检查（白名单）
  const SVG_NS = 'http://www.w3.org/2000/svg';
  const HTML_OK = new Set(('div span p br hr b strong i em u s small big sub sup mark h1 h2 h3 h4 h5 h6 ul ol li dl dt dd table thead tbody tfoot tr th td caption colgroup col ' +
    'img figure figcaption blockquote pre code abbr time progress meter section article header footer main aside nav ruby rt rp wbr bdi bdo q cite del ins kbd var samp dfn address center font data style').split(' '));
  const SVG_OK = new Set(('svg g path circle ellipse line polyline polygon rect text tspan textpath defs lineargradient radialgradient stop clippath mask pattern symbol use title desc image marker metadata ' +
    'filter fegaussianblur feoffset feblend fecolormatrix fecomposite feflood femerge femergenode fedropshadow femorphology fecomponenttransfer fefunca fefuncr fefuncg fefuncb feturbulence ' +
    'fedisplacementmap fespecularlighting fediffuselighting fepointlight fedistantlight fespotlight fetile feconvolvematrix style').split(' '));
  const NO = {};
  const deny = (tags, why) => { for (const t of tags.split(' ')) NO[t] = why; };
  deny('script', () => '不能有 <script>：卡片里的 HTML 不会运行脚本，要算的东西在 data() / render() 里算好');
  deny('iframe frame frameset object embed applet portal fencedframe', t => '不能嵌入别的网页或插件（<' + t + '>）');
  deny('video audio source track', t => '不能放视频、音频（<' + t + '>）');
  deny('canvas', () => '不能用 <canvas>：卡片里的脚本不会运行，画不了；图形可以用 SVG');
  deny('a area map form input button select textarea option optgroup label fieldset legend datalist output details summary dialog menu', t => '卡片只能看不能点，不能有链接、按钮和表单（<' + t + '>）');
  deny('link meta base', t => '不能引用外部文件（<' + t + '>）：网上的数据用 api.fetchJson / fetchText / fetchImage 在 data() 里取');
  deny('marquee blink animate animatemotion animatetransform set discard', t => '卡片不能有循环动画（<' + t + '>）：要变的内容按 refresh 重新画');
  deny('foreignobject feimage template slot noscript', t => '不支持 <' + t + '>');
  // 带网址的属性：只有图片的 src / SVG 的 href 可以有（data: 图片或 #id），其余一律不许
  const URL_ATTRS = new Set('srcset action formaction poster background ping lowsrc dynsrc longdesc manifest codebase archive profile usemap data'.split(' '));

  const imgCache = new Map(), IMG_CHARS = 4 << 20;   // data: 网址 → 查出来的错误（'' = 没问题）
  let imgChars = 0;
  /** data: 图片检查：格式、动图。where = 出错时说明是哪里 */
  function checkImage(u, where) {
    u = String(u).trim();
    if (imgCache.has(u)) { const v = imgCache.get(u); if (v) throw new Error(v); return; }
    let err = '';
    const m = /^data:image\/(png|jpeg|jpg|gif|webp|svg\+xml|bmp|x-icon|vnd\.microsoft\.icon|avif)((?:;[^,;]*)*),/i.exec(u);
    if (!m) err = where + '只能用 data: 图片（cards 文件夹里的图片用 api.image 读，网上的图片用 api.fetchImage 取）';
    else {
      // 是不是 base64 按浏览器的认法：参数按 ; 分开、去掉空白后有一个是 base64（「; base64」「;base64;charset=…」都算）
      const type = m[1].toLowerCase(), b64 = m[2].split(';').some(p => p.trim().toLowerCase() === 'base64'), body = u.slice(m[0].length);
      let bin = '';
      try { bin = b64 ? atob(body.replace(/\s+/g, '')) : unescape(body); } catch (e) { err = where + '的 data: 图片编码不对'; }
      if (!err && animated(type, bin)) err = where + '不能用动图（卡片不能有循环动画）';
    }
    // 大图也留着：refresh = 1 秒的时钟卡片用 api.image 读的背景图，每秒重画都要查同一张，每次都解码一遍太费。
    // 键就是整个网址，所以按总长度设上限（最多约 4M 字符）；很长的字符串当键查起来也快（V8 按长度算哈希，命中再整段比）
    if (u.length <= IMG_CHARS / 2) {
      if (imgCache.size > 64 || imgChars + u.length > IMG_CHARS) { imgCache.clear(); imgChars = 0; }
      imgCache.set(u, err); imgChars += u.length;
    }
    if (err) throw new Error(err);
  }
  /** 是不是动图：按文件内容认格式（浏览器解码图片也是看内容，标成 png 的 GIF 照样会动），SVG 看写的类型 */
  function animated(type, b) {
    if (b.slice(0, 4) === 'GIF8') return gifFrames(b) > 1;
    if (b.slice(0, 4) === '\x89PNG') { const a = b.indexOf('acTL'), d = b.indexOf('IDAT'); return a >= 0 && (d < 0 || a < d); }   // APNG
    if (b.slice(0, 4) === 'RIFF' && b.slice(8, 12) === 'WEBP') return b.slice(12, 16) === 'VP8X' && (b.charCodeAt(20) & 2) !== 0;
    if (b.slice(4, 8) === 'ftyp') { const n = (b.charCodeAt(0) << 24 | b.charCodeAt(1) << 16 | b.charCodeAt(2) << 8 | b.charCodeAt(3)) >>> 0; return b.slice(8, Math.min(n, 256)).indexOf('avis') >= 0; }   // AVIF 图像序列
    if (type === 'svg+xml') {
      let t = b;
      try { t = decodeURIComponent(escape(b)); } catch (e) { /* 不是 UTF-8：按原样查 */ }
      // 还原 XML 字符引用（anim&#97;tion）和 CSS 转义、注释再查；带命名空间前缀的元素（<svg:animate>）也算
      t = plainCss(t.replace(/&#x([0-9a-f]+);?/gi, (x, h) => fromCp(parseInt(h, 16))).replace(/&#(\d+);?/g, (x, d) => fromCp(+d)));
      return /<\s*([\w.-]+:)?(animate|animateMotion|animateTransform|set|discard)\b|@(-webkit-)?keyframes|animation(-[a-z-]+)?\s*:/i.test(t);
    }
    return false;
  }
  /** GIF 里有几帧（数到 2 就停） */
  function gifFrames(b) {
    if (b.slice(0, 3) !== 'GIF') return 0;
    const skipBlocks = p => { while (p < b.length) { const s = b.charCodeAt(p++); if (!s) break; p += s; } return p; };
    let p = 13, n = 0;
    const f0 = b.charCodeAt(10);
    if (f0 & 0x80) p += 3 * (1 << ((f0 & 7) + 1));
    while (p < b.length) {
      const c = b.charCodeAt(p++);
      if (c === 0x3B) break;
      if (c === 0x21) p = skipBlocks(p + 1);
      else if (c === 0x2C) {
        if (++n > 1) return n;
        const f = b.charCodeAt(p + 8);
        p += 9;
        if (f & 0x80) p += 3 * (1 << ((f & 7) + 1));
        p = skipBlocks(p + 1);
      } else break;
    }
    return n;
  }

  const fromCp = c => c > 0 && c <= 0x10FFFF ? String.fromCodePoint(c) : '';
  /** 去掉 CSS 注释、还原转义（u\72l( 也是 url(），检查时用 */
  const plainCss = s => String(s).replace(/\/\*[\s\S]*?\*\//g, ' ').replace(/\\([0-9a-fA-F]{1,6})\s?/g, (x, h) => fromCp(parseInt(h, 16))).replace(/\\(.)/g, '$1');

  /** CSS 检查（style 属性、<style>、style 字段）：不许 @import / @font-face / 动画 / 外部 url() */
  function checkCss(css, where) {
    const s = plainCss(css), low = s.toLowerCase();
    if (/@import/.test(low)) throw new Error(where + '里不能用 @import（不能引用外部样式）');
    if (/@font-face/.test(low)) throw new Error(where + '里不能用 @font-face（不能加载字体文件；系统里装了的字体可以直接写在 font-family 里）');
    if (/@(-webkit-)?keyframes/.test(low) || /(^|[^a-z-])(-webkit-)?animation(-[a-z-]+)?\s*:/.test(low))
      throw new Error(where + '里不能有 animation / @keyframes：卡片不能有循环动画，要变的内容按 refresh 重新画');
    if (/image-set\s*\(/.test(low)) throw new Error(where + '里不能用 image-set()');
    const re = /url\s*\(\s*(?:"([^"]*)"|'([^']*)'|([^)]*))/gi;
    let m;
    while ((m = re.exec(s))) {
      const v = (m[1] != null ? m[1] : m[2] != null ? m[2] : m[3]).trim();
      if (v.startsWith('#')) continue;   // SVG 的渐变、裁剪等引用本文档里的定义
      if (/^data:/i.test(v)) { checkImage(v, where + '里的 url() '); continue; }
      throw new Error(where + '里不能用 url() 引用外部文件：' + (v.length > 60 ? v.slice(0, 60) + '…' : v) + '（图片用 api.image / api.fetchImage 拿到 data: 网址）');
    }
  }

  /** 检查 render 的结果，返回解析好的节点（要导入显示用的 iframe）；不合格抛出带原因的错误 */
  function sanitize(html) {
    // 先写一个 <body>：后面的 <style> 之类也留在 body 里（不然会被挪进 head）。DOMParser 解析出来的文档是"惰性"的：不运行脚本、不加载图片
    const doc = new DOMParser().parseFromString('<!doctype html><body>' + html, 'text/html');
    const walk = node => {
      for (const n of Array.from(node.childNodes)) {
        if (n.nodeType === 3) continue;
        if (n.nodeType !== 1) { n.remove(); continue; }   // 注释等
        const svg = n.namespaceURI === SVG_NS, tag = n.localName.toLowerCase();
        if (n.namespaceURI !== SVG_NS && n.namespaceURI !== 'http://www.w3.org/1999/xhtml') throw new Error('不支持 <' + tag + '>（只能用常见的排版标签、图片和 SVG）');
        if (NO[tag]) throw new Error(NO[tag](n.localName));
        if (!(svg ? SVG_OK : HTML_OK).has(tag)) throw new Error('不支持 <' + n.localName + '>（只能用常见的排版标签、图片和 SVG）');
        if (tag === 'progress' && !n.hasAttribute('value')) throw new Error('<progress> 要写 value：不写是一直在动的"进行中"进度条（卡片不能有循环动画）');
        for (const a of Array.from(n.attributes)) {
          const name = a.name.toLowerCase(), v = a.value;
          const where = '<' + n.localName + '> 的 ' + a.name + ' ';
          if (name.startsWith('on')) throw new Error('不能有 ' + a.name + ' 这类事件属性（卡片里的脚本不会运行，也不能点）');
          if (/^(javascript|vbscript):/i.test(v.replace(/[\s\u0000-\u001f]+/g, ''))) throw new Error(where + '不能是 javascript: 网址');
          if (URL_ATTRS.has(name)) throw new Error('不支持 <' + n.localName + '> 的 ' + a.name + ' 属性' + (name === 'srcset' ? '（图片用 src 写 data: 网址）' : ''));
          if (name === 'style') checkCss(v, where);
          else if (name === 'src') { if (tag !== 'img') throw new Error('不支持 <' + n.localName + '> 的 src 属性'); checkImage(v, where); }
          else if (name === 'href' || name === 'xlink:href') {
            if (v.trim().startsWith('#') && tag !== 'image') continue;
            if (tag === 'image') checkImage(v, where);
            else throw new Error(where + '只能指向本 SVG 里的定义（#id）');
          } else if (/url\s*\(/i.test(v)) checkCss(name + ':' + v, where);   // fill="url(#g)" 之类
        }
        if (tag === 'style') checkCss(n.textContent, '<style> ');
        else walk(n);
      }
    };
    walk(doc.body);
    return Array.from(doc.body.childNodes);
  }

  // ------------------------------------------------------------------ 每张卡片一个 Runner
  const runners = new Map();      // 卡片 id → runner
  const srcWait = new Map();      // 脚本名 → 等它源码的 runner 集合
  const hostWait = new Map();     // 发给宿主的请求 rid → { r, gen, rid（Worker 里的）, at }
  // 随机起步：页面重新载入后，旧页面还没回来的请求宿主照样发给这个窗口，编号从 0 数的话会撞上新页面的请求、把数据交错卡片
  let hostRid = Math.floor(Math.random() * 1e12);

  function update(c, el) {
    let r = runners.get(c.id);
    // reported = 上次报给宿主的状态；null = 还没报过：第一次不管好坏都报一次（页面重新载入过，宿主那边可能还记着上一个页面报的错误）
    if (!r) { r = { id: c.id, gen: 0, seq: 0, pn: 0, kills: 0, err: '', reported: null, shown: false }; runners.set(c.id, r); }
    const h = c.h || 160, resized = r.h != null && r.h !== h;
    r.h = h;
    if (r.el !== el) { r.el = el; makeFrame(r); }
    if (r.script !== c.script || r.v !== c.v) {
      r.script = c.script; r.v = c.v; r.kills = 0;
      stop(r);
      if (NOCARDS) { fail(r, '壁纸页面接连崩溃了几次，自定义卡片先停止运行：半小时后或者改过卡片脚本后会重新运行'); return; }
      load(r);
    } else if (resized && r.state === 'ready') { if (r.busy) r.again = true; else tick(r); }   // 卡片高度改了：按新尺寸马上重画（正在画的那张画完再来一轮）
  }

  /** 卡片没了（删掉、挪到别的屏、开始淡出）：停掉脚本。el = 那张卡片的元素（同一个 id 已经换成新元素时不动新的） */
  function drop(id, el) {
    const r = runners.get(id);
    if (!r || (el && r.el !== el)) return;
    stop(r);
    runners.delete(id);
    if (r.reported) post({ t: 'cstate', id, script: r.script, gone: true });
  }

  function makeFrame(r) {
    for (const old of r.el.querySelectorAll('iframe.ucard')) old.remove();
    const f = document.createElement('iframe');
    f.className = 'ucard';
    f.setAttribute('sandbox', 'allow-same-origin');   // 不带 allow-scripts：里面什么脚本都不运行；同源只是为了这边能把内容放进去
    f.setAttribute('aria-hidden', 'true');
    f.tabIndex = -1;
    f.srcdoc = FRAME;
    r.doc = null;
    f.onload = () => { if (r.frame !== f) return; r.doc = f.contentDocument; if (r.pending) { const p = r.pending; r.pending = null; paint(r, p); } };
    r.frame = f;
    r.el.appendChild(f);
  }

  function stop(r) {
    r.gen++;
    clearTimeout(r.timer); r.timer = 0;
    if (r.worker) { try { r.worker.terminate(); } catch (e) {} r.worker = null; }
    if (r.url) { try { spawner.URL.revokeObjectURL(r.url); } catch (e) {} r.url = null; }
    r.state = 'off'; r.busy = false; r.again = false; r.drain = 0; r.api = 0; r.apiN = 0; r.reveal = false; r.usesWeather = undefined;
    for (const set of srcWait.values()) set.delete(r);
  }

  function load(r) {
    r.state = 'src';
    let set = srcWait.get(r.script);
    if (!set) {
      srcWait.set(r.script, set = new Set());
      set.at = Date.now();
      post({ t: 'csrc', f: r.script });
    }
    set.add(r);
    watch();
  }

  function onSource(m) {
    const set = srcWait.get(m.f);
    if (!set) return;
    srcWait.delete(m.f);
    for (const r of set) {
      if (r.script !== m.f) continue;
      // 比这张卡片知道的版本旧：要的时候文件还没改，回来之前改了（load 时同名脚本的请求已经在路上，就没再要）——再要一次。
      // 每个版本只再要一次：文件换成了修改时间更早的（复制过来保留了时间），新的 layout 到之前读多少次都是旧的
      if (!m.err && typeof m.v === 'number' && typeof r.v === 'number' && m.v < r.v && r.reask !== r.v) { r.reask = r.v; load(r); continue; }
      if (m.err) { r.src = null; srcLater(r, m.err); continue; }
      r.src = m.src;
      start(r);
    }
  }
  /** 没拿到脚本（读不出来、宿主没回复）：报错，30 秒后再要一次（文件被编辑器占着之类的会自己好；脚本文件改了马上就会重新加载） */
  function srcLater(r, msg) {
    r.state = 'off';
    fail(r, msg);
    const g = r.gen;
    clearTimeout(r.timer);
    r.timer = setTimeout(() => { if (r.gen === g && runners.get(r.id) === r) load(r); }, 30000);
  }

  function start(r) {
    stop(r);
    const gen = r.gen;
    r.state = 'boot';
    ready().then(() => {
      if (r.gen !== gen || runners.get(r.id) !== r) return;
      let w;
      try {
        r.url = spawner.URL.createObjectURL(new spawner.Blob([prefix + r.src], { type: 'text/javascript' }));
        w = new spawner.Worker(r.url, { name: r.script });
      } catch (e) { fail(r, '启动不了脚本：' + e.message); return; }
      r.worker = w;
      r.state = 'loading';
      r.pongAt = Date.now();
      watch();
      w.onmessage = e => { if (r.gen === gen) fromWorker(r, e.data || {}); };
      w.onerror = e => {
        e.preventDefault();
        if (r.gen !== gen) return;
        const line = e.lineno - OFFSET;
        const msg = String(e.message || '脚本出错').replace(/^Uncaught\s+/, '');
        const at = line > 0 ? '第 ' + line + ' 行：' : '';
        if (r.state === 'loading') { stop(r); fail(r, at + msg); }   // 语法错误、顶层代码出错：脚本没法用，改过文件后重新加载
        else failIdle(r, at + msg);
      };
      w.postMessage({ t: 'init' });
    });
  }

  /** 出错的 stack 里找用户脚本里的那一行（boot 里的行跳过），换成「第 N 行」 */
  function where(stack) {
    const re = /blob:[^\s)]*?:(\d+):(\d+)/g;
    let m;
    while ((m = re.exec(stack || ''))) { const l = +m[1] - OFFSET; if (l > 0) return '第 ' + l + ' 行：'; }
    return '';
  }

  function fromWorker(r, m) {
    switch (m.t) {
      // 等它闲下来（drain）只认 settle 之后发出的 ping：之前发出的可能是交给它请求结果之前回的，那时候等着结果的代码还没开始跑
      case 'pong':
        r.pongAt = Date.now();
        if (r.drain && m.n >= r.drain) { r.drain = 0; if (r.reveal && !r.api && !r.busy) reveal(r); }
        break;
      case 'def':
        r.pongAt = Date.now();
        r.def = m; r.state = 'ready';
        r.html = null;   // 脚本重新加载过：只改了 style 的话 render 的结果和上次一样，也要重画
        if (r.url) { try { spawner.URL.revokeObjectURL(r.url); } catch (e) {} r.url = null; }
        if (m.style) { try { checkCss(m.style, 'style 字段'); } catch (e) { stop(r); fail(r, e.message); return; } }
        tick(r);
        break;
      case 'bad': stop(r); fail(r, m.msg); break;
      case 'phase': if (m.seq === r.seq) r.phase = m.p; break;
      case 'api': onApi(r, m); break;
      // 先记下这一轮的好坏再排下一轮（finish）：出错了下一轮要早排（schedule 看 r.err）
      case 'out':
        if (m.seq !== r.seq || !r.busy) return;
        r.usesWeather = !!m.w;
        try {
          if (m.html.length > MAX_HTML) throw new Error('render() 返回的内容太大（超过 1 MB）');
          if (m.html !== r.html || r.err) { const nodes = sanitize(m.html); r.html = m.html; paint(r, { nodes, style: r.def.style }); }
          // 画的期间报了没接住的错（failIdle）：画是画上了，这一轮还是算出错（照样藏着、1 分钟后再试）。
          // 不然每一轮都出这个错的卡片每一轮都要「显示 → 藏起来」闪一下
          if (r.errSeq !== r.seq) ok(r);
        } catch (e) { r.html = null; fail(r, e.message); }
        finish(r);
        break;
      case 'fail':
        if (m.seq !== r.seq || !r.busy) return;
        r.usesWeather = !!m.w;
        fail(r, where(m.stack) + m.msg);
        finish(r);
        break;
      case 'stray': failIdle(r, where(m.stack) + m.msg); break;
    }
  }
  /** 不是在一轮里报的错（没接住的 Promise 错误、脚本自己加的事件处理里抛的错）：卡片藏起来；画完之后才出的，下一轮早就按没出错排好了，
   *  refresh 长的要藏到那时候：按出错重排（正在画的这一轮画完会自己排） */
  function failIdle(r, msg) {
    fail(r, msg);
    if (r.busy) r.errSeq = r.seq;   // 正在画：这一轮算出错（见 out）
    else schedule(r);
  }

  function tick(r) {
    clearTimeout(r.timer); r.timer = 0;
    if (!r.worker || r.state !== 'ready' || r.busy) return;
    r.busy = true; r.seq++; r.phase = '';
    r.busySince = r.pongAt = Date.now();
    watch();
    let weather = null;
    try { weather = getWeather(); } catch (e) { /* 天气数据有问题：当没有 */ }
    r.worker.postMessage({ t: 'tick', seq: r.seq, now: Date.now(), weather, w: cardW() - 2, h: Math.round(r.h - 2), name: r.def.name });
  }

  /** 这一轮结束：排下一轮。按本地时间对齐 refresh 的整数倍（1 = 每秒整、60 = 每分钟整、86400 = 每天零点），
   *  暂停（桌面被挡住）时最多每分钟一次；出错了 RETRY 后再试 */
  function finish(r) {
    r.busy = false;
    settle(r);
    if (r.again) { r.again = false; tick(r); }   // 这一轮画的期间天气更新了 / 卡片高度改了：马上再画一轮
    else schedule(r);
  }
  /** 脚本的代码可能还在跑（画完了，render 里留下的 Promise 还在转；闲着时交给它请求结果，等着它的代码接着跑）：盯到它回一次现在之后发的 ping */
  function settle(r) { r.drain = r.pn + 1; r.pongAt = Date.now(); watch(); }
  function schedule(r) {
    clearTimeout(r.timer);
    if (!r.worker || r.state !== 'ready') return;
    let ms = Math.max(1, r.def.refresh) * 1000;
    if (paused) ms = Math.max(ms, 60000);
    let delay;
    // 出错（多半是网络）：别等到下一个整点，过 RETRY 就试（不对齐：对齐了实际等多久没准，早于 60 秒还会白试一轮）
    if (r.err && ms > RETRY) delay = RETRY;
    else {
      const now = Date.now(), local = now - new Date(now).getTimezoneOffset() * 60000;
      delay = Math.min(ms - local % ms + 15, 2147483000);   // setTimeout 最多 24.8 天
    }
    r.timer = setTimeout(() => tick(r), delay);
  }

  /** 这一轮画好了。藏着的（第一次画、出过错）要等脚本闲下来再显示（reveal）：画完之后才报的错（data() 里没等的请求失败了）
   *  会在那之前到，卡片就一直藏着，不会「显示一下又藏起来」 */
  function ok(r) {
    r.kills = 0;
    r.err = '';
    r.reveal = true;
    report(r);
  }
  /** 闲下来了（settle 之后发的 ping 回来了、没有没回来的请求），这期间没再出错：显示出来 */
  function reveal(r) {
    r.reveal = false;
    // 元素上的 cerr 也要看：淡出期间又回来的卡片沿用原来的元素，新 runner 的 err 是空的，上一个留下的 cerr 不去掉的话会一直透明
    r.el.classList.remove('cerr');
    if (!r.shown || r.el.classList.contains('hidden')) { r.shown = true; void r.el.offsetWidth; r.el.classList.remove('hidden'); }
  }
  function fail(r, msg) {
    // 截短：throw new Error(很长的文字) 原样报给宿主，消息超过它的 JSON 上限（2M 字符）就解析不了、这个错误报不上去（宿主那边也只留 600 个字）
    msg = String(msg).trim();
    r.err = (r.script || '') + '.js：' + (msg.length > 1000 ? msg.slice(0, 1000) + '…' : msg);
    r.reveal = false;
    r.el.classList.add('cerr');
    report(r);
  }
  function report(r) {
    if (r.reported === r.err) return;
    r.reported = r.err;
    post({ t: 'cstate', id: r.id, script: r.script, err: r.err });
  }

  /** 卡住了：杀掉 Worker，按 30 秒、1 分钟、2 分钟…（最多 10 分钟）再重新启动（脚本文件改了马上重新加载） */
  function kill(r, msg) {
    stop(r);
    fail(r, msg);
    r.kills++;
    const gen = r.gen, src = r.src;
    if (!src) return;
    r.timer = setTimeout(() => { if (r.gen === gen && runners.get(r.id) === r) start(r); }, Math.min(600, 30 * Math.pow(2, r.kills - 1)) * 1000);
  }

  function paint(r, p) {
    if (!r.doc) { r.pending = p; return; }
    const d = r.doc;
    d.getElementById('u').textContent = p.style || '';
    const frag = d.createDocumentFragment();
    for (const n of p.nodes) frag.appendChild(d.importNode(n, true));
    d.body.replaceChildren(frag);
  }

  // ------------------------------------------------------------------ api：Worker 的请求转给宿主
  function onApi(r, m) {
    const reply = (ok, value, err) => { if (r.worker) r.worker.postMessage({ t: 'res', rid: m.rid, ok, value, err }); };
    if (r.api >= MAX_API) { kill(r, '同时发出的请求太多（超过 ' + MAX_API + ' 个还没回来），已停止'); return; }
    // 次数：每次 await 都会让出来、ping 照回的死循环（do { … await api.fetchJson(u) } while (…)：同一个请求 60 秒内宿主马上给结果）只能这样认。
    // 不合格、马上回错误的请求也算（循环里一直发也一样停不下来）
    const now = Date.now();
    if (!r.apiN || now - r.apiWin >= 60000) { r.apiWin = now; r.apiN = 0; }
    if (++r.apiN > API_PER_MIN) { kill(r, '请求太频繁：一分钟里发了超过 ' + API_PER_MIN + ' 个请求（读文件、联网合计；是不是在循环里一直发？），已停止'); return; }
    const a = m.args || {};
    let msg;
    if (m.op === 'fetch') {
      const url = String(a.url || ''), body = a.body == null ? null : String(a.body);
      if (!/^https?:\/\//i.test(url)) return reply(false, null, '只能访问 http:// 或 https:// 开头的网址：' + url.slice(0, 100));
      if (url.length > 4000) return reply(false, null, '网址太长');
      if (body != null && body.length > 65536) return reply(false, null, 'POST 的内容太大（最多 64 KB）');
      const headers = {};
      let n = 0;
      for (const k of Object.keys(a.headers || {})) { if (++n > 20) return reply(false, null, '请求头太多（最多 20 个）'); headers[k] = String(a.headers[k]).slice(0, 2000); }
      msg = { t: 'cget', id: r.id, kind: a.kind === 'image' ? 'image' : 'text', url, method: String(a.method || 'GET'), body, headers };
    } else if (m.op === 'file') {
      msg = { t: 'cfile', path: String(a.path || '').slice(0, 300), kind: a.kind === 'image' ? 'image' : 'text' };
    } else return reply(false, null, '不认识的请求');
    msg.rid = ++hostRid;
    r.api++;
    hostWait.set(msg.rid, { r, gen: r.gen, rid: m.rid, at: Date.now() });
    watch();
    post(msg);
  }
  function onReply(m) {
    const w = hostWait.get(m.rid);
    if (!w) return;
    hostWait.delete(m.rid);
    const r = w.r;
    if (r.gen !== w.gen || !r.worker) return;   // 这期间卡片重新加载过：旧请求的回复不要了
    r.api = Math.max(0, r.api - 1);
    const v = m.t === 'cget' ? { status: m.status, type: m.type, text: m.text, data: m.data } : { text: m.text, data: m.data };
    r.worker.postMessage({ t: 'res', rid: w.rid, ok: !!m.ok, value: m.ok ? v : null, err: m.err || '' });
    if (!r.busy) settle(r);   // 没在画（data() 里没等它就画完了）：等着它的代码接着跑，没人盯
  }

  // ------------------------------------------------------------------ 看门狗：忙的 Worker 每 0.5 秒 ping 一次，太久不回就杀掉。
  // 有事要盯（等脚本源码、脚本在加载、正在画、画完了还没回 ping、有请求没回来）时才跑，都闲下来就停：没有自定义卡片、卡片都在等下一轮时不唤醒页面
  let dog = 0;
  const watched = r => r.state === 'src' || (!!r.worker && (r.state === 'loading' || r.busy || r.drain));
  function watch() { if (!dog) dog = setInterval(patrol, 500); }
  function patrol() {
    const now = Date.now();
    for (const r of runners.values()) {
      if (r.state === 'src') {
        const set = srcWait.get(r.script);
        if (set && now - set.at > HOST_WAIT) {
          srcWait.delete(r.script);
          for (const x of set) srcLater(x, '没有拿到脚本（宿主没有回复），30 秒后再试');
        }
        continue;
      }
      if (!r.worker) continue;
      if (r.state === 'loading') {
        if (now - r.pongAt > LOAD_HANG) { kill(r, '脚本加载超过 ' + LOAD_HANG / 1000 + ' 秒还没完成（顶层代码里是不是有死循环？）'); continue; }
        r.worker.postMessage({ t: 'ping', n: ++r.pn });
      } else if (r.busy || r.drain) {
        if (now - r.pongAt > HANG) {
          kill(r, (!r.busy ? '画完之后脚本还在一直运行' : r.phase === 'render' ? 'render() 卡住了' : r.phase === 'data' ? 'data() 卡住了' : '脚本卡住了') +
            '：超过 ' + HANG / 1000 + ' 秒没有响应（是不是有死循环？' + (r.busy ? '' : '比如一直在转的 Promise') + '），已停止');
          continue;
        }
        if (r.busy && now - r.busySince > TICK_MAX) {
          // data() 一直在等（请求都有 15 秒超时，正常到不了这里）：放弃这一轮，Worker 留着（ping 照回，就是在等着，不费什么；
          // 在循环里一直发请求的由 onApi 按次数停掉）
          r.busy = false; r.seq++;
          fail(r, 'data() 超过 ' + TICK_MAX / 1000 + ' 秒还没有完成');
          schedule(r);
          continue;
        }
        r.worker.postMessage({ t: 'ping', n: ++r.pn });
      }
    }
    for (const [id, w] of hostWait) if (now - w.at > HOST_WAIT) {
      hostWait.delete(id);
      const r = w.r;
      if (r.gen !== w.gen || !r.worker) continue;
      r.api = Math.max(0, r.api - 1);
      r.worker.postMessage({ t: 'res', rid: w.rid, ok: false, err: '宿主没有回复（超过 ' + HOST_WAIT / 1000 + ' 秒）' });
      if (!r.busy) settle(r);
    }
    if (!hostWait.size && ![...runners.values()].some(watched)) { clearInterval(dog); dog = 0; }
  }

  return {
    init(o) { post = o.post; getWeather = o.weather; cardW = o.width; },
    update, drop,
    /** 宿主发来的：csrc（脚本源码）、cfile / cget（读文件、联网的结果） */
    onHost(m) { if (m.t === 'csrc') onSource(m); else onReply(m); },
    setPaused(p) {
      if (p === paused) return;
      paused = p;
      // 只管 refresh 不到一分钟的（暂停时被拖成一分钟一次）：挡住时重排、露出来马上画一次。别的照原来的时间画，
      // 不然窗口一最小化再还原就全部重画一遍（联网的卡片多发请求，「每日一句」这种一天换一次的会换掉）
      for (const r of runners.values()) if (r.state === 'ready' && !r.busy && r.def.refresh < 60) { if (p) schedule(r); else tick(r); }
    },
    /** 默认卡片的天气更新了 / 默认卡片换了城市：api.weather 变了，读过它的卡片不等下一轮 refresh，马上重画（正在画的那张画完再来一轮，它拿到的是旧天气）。
     *  还没画完过第一轮的（usesWeather 还不知道）也算 */
    weatherChanged() {
      for (const r of runners.values()) if (r.state === 'ready' && r.worker && r.usesWeather !== false) { if (r.busy) r.again = true; else tick(r); }
    },
    // 测试用
    _check: sanitize, _css: checkCss,
  };
})();
