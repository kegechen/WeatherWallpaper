// ============================================================================
// 天气壁纸 · 自定义卡片示例：倒计时
//
// 离几个重要的日子还有几天，最近的排在最前面。改下面的 DAYS 换成你自己的日子。
// 能用的东西（api 都有什么、有哪些规矩）写在「时钟天气示例.js」的开头。
// ============================================================================

// 要倒数的日子：
//   date: '01-01'        每年公历 1 月 1 日
//   date: '2027-06-07'   只有那一天（过了就不显示）
//   lunar: [8, 15]       每年农历八月十五
const DAYS = [
  { name: '春节', lunar: [1, 1] },
  { name: '元旦', date: '01-01' },
  { name: '国庆节', date: '10-01' },
  // { name: '妈妈生日', lunar: [8, 15] },
  // { name: '考试', date: '2027-06-07' },
];
const SHOW = 3;   // 最多显示几个；改了的话，在卡片设置里把卡片高度也调一下（每多一个大约高 30）

WeatherWallpaper.card({
  name: '倒计时',
  height: 126,
  refresh: 3600,   // 每小时整点重画（过了零点天数就变了）

  style: `
    .row { display: flex; align-items: baseline; justify-content: space-between; height: 30px; line-height: 30px; white-space: nowrap; }
    .nm { font-size: 15px; overflow: hidden; text-overflow: ellipsis; }
    .nm small { margin-left: 8px; font-size: 12px; opacity: .65; }
    .n { font-size: 13px; opacity: .9; }
    .n b { font-size: 22px; font-weight: 600; margin: 0 3px; font-variant-numeric: tabular-nums; }
    .today { color: #ffd36b; font-size: 15px; font-weight: 600; }
    .empty { opacity: .7; font-size: 13px; }
  `,

  // 不联网也可以不写 data、直接在 render 里算；这里写出来演示：data 的返回值会交给 render
  data(api) {
    const list = [];
    for (const d of DAYS) {
      const t = nextDate(d, api);
      if (t != null) list.push({ name: d.name, left: api.daysUntil(t), text: api.date(t).text });
    }
    list.sort((a, b) => a.left - b.left);
    return list.slice(0, SHOW);
  },

  render(list, api) {
    if (!list.length) return '<div class="empty">没有要倒数的日子了：改一下脚本里的 DAYS</div>';
    return list.map(x => `<div class="row"><span class="nm">${api.esc(x.name)}<small>${x.text}</small></span>`
      + (x.left === 0 ? '<span class="today">就是今天</span>' : `<span class="n">还有<b>${x.left}</b>天</span>`) + '</div>').join('');
  },
});

// 下一次是哪天（毫秒）；只有一次的日子已经过了就返回 null
function nextDate(d, api) {
  if (d.lunar) return api.nextLunar(d.lunar[0], d.lunar[1]);
  const m = /^(?:(\d{4})-)?(\d{1,2})-(\d{1,2})$/.exec(d.date || '');
  if (!m) throw new Error('「' + d.name + '」的日期写错了：' + d.date + '（要写成 01-01 或 2027-06-07 这样）');
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  if (m[1]) {
    const t = new Date(+m[1], m[2] - 1, +m[3]);
    return t >= today ? t.getTime() : null;
  }
  let t = new Date(today.getFullYear(), m[2] - 1, +m[3]);
  if (t < today) t = new Date(today.getFullYear() + 1, m[2] - 1, +m[3]);
  return t.getTime();
}
