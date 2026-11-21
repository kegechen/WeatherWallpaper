// 公历 → 农历（1900–2100）：查表，和香港天文台的《公历与农历日期对照表》一致
// （ICU 的 chinese 历法在朔日正好落在午夜前后的月份会差一天，如把 2027 年春节算成 2 月 7 日，整个月都跟着错，不能直接拿来显示日期）。
// 表外的年份退回 ICU。卡片的日期行、节日判定和自定义效果的 'lunar:08-15' 都用这一份。
(function (root) {
  'use strict';
  // 每年一个数（5 位十六进制）：低 4 位 = 闰几月（0 没有闰月）；第 16 位 = 闰月是大月（30 天）；
  // 第 15…4 位 = 正月…腊月是不是大月。数据来自 calendar.js（jjonline，MIT），已逐日核对过 2025–2050 年香港天文台的春节、元宵、端午、七夕、中秋
  const INFO = (
    '04bd8 04ae0 0a570 054d5 0d260 0d950 16554 056a0 09ad0 055d2' +   // 1900–1909
    '04ae0 0a5b6 0a4d0 0d250 1d255 0b540 0d6a0 0ada2 095b0 14977' +   // 1910–1919
    '04970 0a4b0 0b4b5 06a50 06d40 1ab54 02b60 09570 052f2 04970' +   // 1920–1929
    '06566 0d4a0 0ea50 16a95 05ad0 02b60 186e3 092e0 1c8d7 0c950' +   // 1930–1939
    '0d4a0 1d8a6 0b550 056a0 1a5b4 025d0 092d0 0d2b2 0a950 0b557' +   // 1940–1949
    '06ca0 0b550 15355 04da0 0a5b0 14573 052b0 0a9a8 0e950 06aa0' +   // 1950–1959
    '0aea6 0ab50 04b60 0aae4 0a570 05260 0f263 0d950 05b57 056a0' +   // 1960–1969
    '096d0 04dd5 04ad0 0a4d0 0d4d4 0d250 0d558 0b540 0b6a0 195a6' +   // 1970–1979
    '095b0 049b0 0a974 0a4b0 0b27a 06a50 06d40 0af46 0ab60 09570' +   // 1980–1989
    '04af5 04970 064b0 074a3 0ea50 06b58 05ac0 0ab60 096d5 092e0' +   // 1990–1999
    '0c960 0d954 0d4a0 0da50 07552 056a0 0abb7 025d0 092d0 0cab5' +   // 2000–2009
    '0a950 0b4a0 0baa4 0ad50 055d9 04ba0 0a5b0 15176 052b0 0a930' +   // 2010–2019
    '07954 06aa0 0ad50 05b52 04b60 0a6e6 0a4e0 0d260 0ea65 0d530' +   // 2020–2029
    '05aa0 076a3 096d0 04afb 04ad0 0a4d0 1d0b6 0d250 0d520 0dd45' +   // 2030–2039
    '0b5a0 056d0 055b2 049b0 0a577 0a4b0 0aa50 1b255 06d20 0ada0' +   // 2040–2049
    '14b63 09370 049f8 04970 064b0 168a6 0ea50 06b20 1a6c4 0aae0' +   // 2050–2059
    '092e0 0d2e3 0c960 0d557 0d4a0 0da50 05d55 056a0 0a6d0 055d4' +   // 2060–2069
    '052d0 0a9b8 0a950 0b4a0 0b6a6 0ad50 055a0 0aba4 0a5b0 052b0' +   // 2070–2079
    '0b273 06930 07337 06aa0 0ad50 14b55 04b60 0a570 054e4 0d160' +   // 2080–2089
    '0e968 0d520 0daa0 16aa6 056d0 04ae0 0a9d4 0a2d0 0d150 0f252' +   // 2090–2099
    '0d520'                                                            // 2100
  ).match(/[0-9a-f]{5}/g).map(s => parseInt(s, 16));   // 每项正好 5 位：行尾不用补空格
  const DAY = 86400000, BASE = Date.UTC(1900, 0, 31);   // 1900 年正月初一
  const leapOf = y => INFO[y - 1900] & 0xf;
  const leapLen = y => leapOf(y) ? (INFO[y - 1900] & 0x10000 ? 30 : 29) : 0;
  const monLen = (y, m) => INFO[y - 1900] & (0x10000 >> m) ? 30 : 29;
  const yearLen = [];
  for (let y = 1900; y <= 2100; y++) { let n = leapLen(y); for (let m = 1; m <= 12; m++) n += monLen(y, m); yearLen.push(n); }

  function fromTable(day) {
    let off = Math.round((day - BASE) / DAY);
    if (off < 0) return null;
    let y = 1900;
    for (; y <= 2100 && off >= yearLen[y - 1900]; y++) off -= yearLen[y - 1900];
    if (y > 2100) return null;
    const lp = leapOf(y);
    for (let m = 1; m <= 12; m++) {
      let n = monLen(y, m);
      if (off < n) return { month: m, day: off + 1, leap: false };
      off -= n;
      if (m === lp) { n = leapLen(y); if (off < n) return { month: m, day: off + 1, leap: true }; off -= n; }
    }
    return null;
  }

  let icuFmt = null;
  function fromIcu(day) {
    try {
      icuFmt = icuFmt || new Intl.DateTimeFormat('en-u-ca-chinese', { timeZone: 'UTC', month: 'numeric', day: 'numeric' });
      const p = icuFmt.formatToParts(new Date(day + DAY / 2));
      const mon = (p.find(x => x.type === 'month') || {}).value || '', dd = parseInt((p.find(x => x.type === 'day') || {}).value, 10);
      const r = { month: parseInt(mon, 10), day: dd, leap: /bis/i.test(mon) };   // 闰月是 "6bis" 这种
      return r.month >= 1 && r.day >= 1 ? r : null;
    } catch (e) { return null; /* 没有 chinese 历法 */ }
  }

  const cache = new Map();
  /// localMs = 当地时间当成 UTC 的毫秒数（真实时间 + 时区偏移）。返回 { month, day, leap }，算不出来返回 null
  function of(localMs) {
    const day = Math.floor(localMs / DAY) * DAY;
    if (cache.has(day)) return cache.get(day);
    if (cache.size > 64) cache.clear();
    const r = fromTable(day) || fromIcu(day);
    cache.set(day, r);
    return r;
  }

  const MON = ['正', '二', '三', '四', '五', '六', '七', '八', '九', '十', '冬', '腊'];
  const TEN = ['初', '十', '廿', '三'];
  const ONE = ['十', '一', '二', '三', '四', '五', '六', '七', '八', '九'];
  /// 「十月十三」「闰六月初一」「腊月廿九」；算不出来返回 ''
  function text(localMs) {
    const l = of(localMs);
    if (!l) return '';
    const d = l.day === 10 ? '初十' : l.day === 20 ? '二十' : l.day === 30 ? '三十' : TEN[Math.floor(l.day / 10)] + ONE[l.day % 10];
    return (l.leap ? '闰' : '') + MON[l.month - 1] + '月' + d;
  }

  root.LUNAR = { of, text };
})(typeof window !== 'undefined' ? window : globalThis);
