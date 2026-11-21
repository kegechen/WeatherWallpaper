// 节日判定：按"当地日期"（天气所在城市的时区）算今天有哪些节日、夜里放不放烟花。
// 农历节日用香港天文台《公历与农历日期对照表》抽出来的公历日期（ICU 的 chinese 历法个别年份会差一天，比如把 2027 年春节算成 2 月 7 日）；
// 表外的年份才按 lunar.js（1900–2100 查表，再往外是 ICU）逐日推算。
(function (root) {
  'use strict';
  // 每年：春节（正月初一）、元宵、端午、七夕、中秋 的公历 MMDD（2025–2050，闰月不算）
  const TABLE =
    '2025:0129,0212,0531,0829,1006;2026:0217,0303,0619,0819,0925;2027:0206,0220,0609,0808,0915;2028:0126,0209,0528,0826,1003;' +
    '2029:0213,0227,0616,0816,0922;2030:0203,0217,0605,0805,0912;2031:0123,0206,0624,0824,1001;2032:0211,0225,0612,0812,0919;' +
    '2033:0131,0214,0601,0801,0908;2034:0219,0305,0620,0820,0927;2035:0208,0222,0610,0810,0916;2036:0128,0211,0530,0828,1004;' +
    '2037:0215,0301,0618,0817,0924;2038:0204,0218,0607,0807,0913;2039:0124,0207,0527,0826,1002;2040:0212,0226,0614,0814,0920;' +
    '2041:0201,0215,0603,0803,0910;2042:0122,0205,0622,0822,0928;2043:0210,0224,0611,0811,0917;2044:0130,0213,0531,0731,1005;' +
    '2045:0217,0303,0619,0819,0925;2046:0206,0220,0608,0808,0915;2047:0126,0209,0529,0827,1004;2048:0214,0228,0615,0816,0922;' +
    '2049:0202,0216,0604,0805,0911;2050:0123,0206,0623,0823,0930';
  const NAMES = ['cny', 'lantern', 'dragon', 'qixi', 'moon'];
  const DAY = 86400000;
  const utc = (y, m, d) => Date.UTC(y, m - 1, d);
  const table = {};
  for (const row of TABLE.split(';')) {
    const [y, v] = row.split(':'), o = {};
    v.split(',').forEach((s, i) => { o[NAMES[i]] = utc(+y, +s.slice(0, 2), +s.slice(2)); });
    table[y] = o;
  }

  // 表外年份：按农历逐日扫一遍（每年只算一次）；算不出农历（表外又没有 ICU 的 chinese 历法）时就没有农历节日
  const scanCache = {};
  function scanDays(y) {
    if (scanCache[y]) return scanCache[y];
    const out = {};
    const want = { '1-1': 'cny', '1-15': 'lantern', '5-5': 'dragon', '7-7': 'qixi', '8-15': 'moon' };
    for (let t = utc(y, 1, 1); t < utc(y + 1, 1, 1); t += DAY) {
      const l = lunar(t);
      const k = l && !l.leap ? want[l.month + '-' + l.day] : null;   // 闰月不算
      if (k && out[k] == null) out[k] = t;
    }
    return (scanCache[y] = out);
  }
  const lunarDays = y => table[y] || scanDays(y);

  // 复活节（西方教会）：春分月圆后的第一个星期日，匿名公历算法
  function easter(y) {
    const a = y % 19, b = Math.floor(y / 100), c = y % 100, d = Math.floor(b / 4), e = b % 4, f = Math.floor((b + 8) / 25);
    const g = Math.floor((b - f + 1) / 3), h = (19 * a + b - d - g + 15) % 30, i = Math.floor(c / 4), k = c % 4;
    const l = (32 + 2 * e + 2 * i - h - k) % 7, m = Math.floor((a + 11 * h + 22 * l) / 451);
    const n = h + l - 7 * m + 114;
    return utc(y, Math.floor(n / 31), n % 31 + 1);
  }

  /// localMs = 当地时间当成 UTC 的毫秒数（真实时间 + 时区偏移）。
  /// 返回 { keys: 今天的节日, fw: 烟花 0 不放 / 1 隔一阵放一发 / 2 跨年、除夕零点前后最密 }
  function at(localMs) {
    const day = Math.floor(localMs / DAY) * DAY, hour = (localMs - day) / 3600000;
    const dt = new Date(day), y = dt.getUTCFullYear(), m = dt.getUTCMonth() + 1, d = dt.getUTCDate();
    const keys = [];
    let fw = 0;
    const on = v => { fw = Math.max(fw, v); };
    const lu = lunarDays(y);
    const within = (t, a, b) => t != null && day >= t + a * DAY && day <= t + b * DAY;
    // 春节：除夕到正月初七挂灯笼；每晚都放烟花，除夕夜一直放到初一凌晨，零点前后最密
    if (within(lu.cny, -1, 6)) {
      keys.push('cny');
      const eve = day === lu.cny - DAY, first = day === lu.cny;
      if (hour >= 18.5 && (hour < 23.5 || eve)) on(eve && hour >= 23.6 ? 2 : 1);
      if (first && hour < 1.5) on(hour < 0.5 ? 2 : 1);
    }
    if (within(lu.lantern, -1, 1)) { keys.push('lantern'); if (day === lu.lantern && hour >= 18.5 && hour < 23) on(1); }
    if (within(lu.dragon, -1, 1)) keys.push('dragon');
    if (within(lu.moon, -1, 1)) keys.push('moon');
    if (m === 10 && d <= 7) { keys.push('national'); if (d === 1 && hour >= 19.5 && hour < 22.5) on(1); }
    if (m === 10 && d === 31) keys.push('halloween');
    if (m === 12 && (d === 24 || d === 25)) keys.push('xmas');
    if (within(easter(y), -1, 1)) keys.push('easter');
    // 跨年：12 月 31 日晚上到元旦凌晨放烟花，零点前后最密
    if (m === 12 && d === 31 && hour >= 21) on(hour >= 23.83 ? 2 : 1);
    if (m === 1 && d === 1 && hour < 1) on(hour < 0.34 ? 2 : 1);
    return { keys, fw };
  }

  // 某一天的农历月 / 日 { month, day, leap }（自定义效果的 'lunar:08-15' 用）：见 lunar.js
  const lunar = localMs => root.LUNAR ? root.LUNAR.of(localMs) : null;

  root.FEST = { at, lunarDays, easter, lunar };
})(typeof window !== 'undefined' ? window : globalThis);
