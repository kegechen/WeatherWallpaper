// fest.js 单测：已知节日日期 + 烟花时段 + 复活节
import fs from 'node:fs';
eval(fs.readFileSync(new URL('../web/fest.js', import.meta.url), 'utf8'));
const F = globalThis.FEST;
const L = (s, hh = 12, mm = 0) => Date.parse(s + 'T00:00:00Z') + (hh * 60 + mm) * 60000;   // 当地时间（按 UTC 表示）
let fail = 0;
const eq = (name, got, want) => { const ok = JSON.stringify(got) === JSON.stringify(want); if (!ok) fail++; console.log((ok ? 'ok  ' : 'FAIL') + ' ' + name + ' -> ' + JSON.stringify(got) + (ok ? '' : ' want ' + JSON.stringify(want))); };
const keys = (s, h, m) => F.at(L(s, h, m)).keys;
const fw = (s, h, m) => F.at(L(s, h, m)).fw;

eq('2027 除夕 2/5', keys('2027-02-05'), ['cny']);
eq('2027 初一 2/6 (ICU 算错的那天)', keys('2027-02-06'), ['cny']);
eq('2027 初七 2/12', keys('2027-02-12'), ['cny']);
eq('2027 初八 2/13 无', keys('2027-02-13'), []);
eq('2027 腊月廿八 2/4 无', keys('2027-02-04'), []);
eq('2026 元宵 3/3', keys('2026-03-03'), ['lantern']);
eq('2026 端午 6/19', keys('2026-06-19'), ['dragon']);
eq('2028 端午 5/28（闰五月年）', keys('2028-05-28'), ['dragon']);
eq('2028 闰五月初五 6/27 无', keys('2028-06-27'), []);
eq('2026 中秋 9/25', keys('2026-09-25'), ['moon']);
eq('2025 中秋+国庆 10/6', keys('2025-10-06'), ['moon', 'national']);
eq('国庆 10/7', keys('2026-10-07'), ['national']);
eq('国庆后 10/8', keys('2026-10-08'), []);
eq('万圣节 10/31', keys('2026-10-31'), ['halloween']);
eq('平安夜 12/24', keys('2026-12-24'), ['xmas']);
eq('圣诞 12/25', keys('2026-12-25'), ['xmas']);
eq('12/26 无', keys('2026-12-26'), []);
eq('复活节 2026 = 4/5', new Date(F.easter(2026)).toISOString().slice(0, 10), '2026-04-05');
eq('复活节 2027 = 3/28', new Date(F.easter(2027)).toISOString().slice(0, 10), '2027-03-28');
eq('复活节 2025 = 4/20', new Date(F.easter(2025)).toISOString().slice(0, 10), '2025-04-20');
eq('复活节周一 2026-04-06', keys('2026-04-06'), ['easter']);
// 烟花
eq('除夕 17:00 不放', fw('2027-02-05', 17), 0);
eq('除夕 20:00 放', fw('2027-02-05', 20), 1);
eq('除夕 23:50 最密', fw('2027-02-05', 23, 50), 2);
eq('初一 00:10 最密', fw('2027-02-06', 0, 10), 2);
eq('初一 01:00 放', fw('2027-02-06', 1), 1);
eq('初一 02:00 不放', fw('2027-02-06', 2), 0);
eq('初三 21:00 放', fw('2027-02-08', 21), 1);
eq('初三 23:45 不放', fw('2027-02-08', 23, 45), 0);
eq('元宵 20:00 放', fw('2026-03-03', 20), 1);
eq('元宵前一天 20:00 不放', fw('2026-03-02', 20), 0);
eq('国庆 10/1 20:00 放', fw('2026-10-01', 20), 1);
eq('国庆 10/2 20:00 不放', fw('2026-10-02', 20), 0);
eq('跨年 12/31 23:55 最密', fw('2026-12-31', 23, 55), 2);
eq('跨年 12/31 22:00 放', fw('2026-12-31', 22), 1);
eq('元旦 00:10 最密', fw('2027-01-01', 0, 10), 2);
eq('元旦 白天不放', fw('2027-01-01', 14), 0);
// 表外年份走 ICU
const ly = F.lunarDays(2060);
eq('2060 走 ICU：春节在 1–2 月', [new Date(ly.cny).getUTCMonth() + 1 <= 2, !!ly.moon], [true, true]);
console.log(fail ? `\n${fail} FAILED` : '\nALL PASS');
