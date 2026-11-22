// ============================================================================
// 天气壁纸 · 自定义卡片示例：每日一句
//
// 每天换一句话。句子从哪来（按顺序试，前面的拿不到就用后面的）：
//   1. 这个文件夹里的「每日一句.txt」：自己建一个，一行一句，出处写在「 — 」后面，比如：知足者常乐 — 老子
//   2. 网上的「一言」（hitokoto.cn）
//   3. 下面内置的几句
// 不想要今天这句：卡片设置里点「换一句」马上换（refreshButton）。
// 演示怎么读文件夹里的文件（api.readText）、怎么联网（api.fetchJson）、拿不到时怎么兜底，以及点了刷新按钮怎么办（api.manual）。
// 能用的东西（api 都有什么、有哪些规矩）写在「时钟天气示例.js」的开头。
// ============================================================================

const FILE = '每日一句.txt';
const API_URL = 'https://v1.hitokoto.cn/?c=d&c=i&c=k&max_length=40&encode=json';   // d 文学、i 诗词、k 哲学
const BUILTIN = [
  ['路漫漫其修远兮，吾将上下而求索。', '屈原《离骚》'],
  ['千里之行，始于足下。', '老子《道德经》'],
  ['不积跬步，无以至千里。', '荀子《劝学》'],
  ['长风破浪会有时，直挂云帆济沧海。', '李白《行路难》'],
  ['纸上得来终觉浅，绝知此事要躬行。', '陆游《冬夜读书示子聿》'],
  ['山重水复疑无路，柳暗花明又一村。', '陆游《游山西村》'],
  ['人生如逆旅，我亦是行人。', '苏轼《临江仙》'],
];
let today = null;   // 今天从网上取到的那句：{ day, q }。脚本里顶层的变量一直留着，直到脚本重新加载
let skip = { day: -1, n: 0 };   // 今天点了几次「换一句」：文件里的、内置的句子往后挑几句
let netDown = 0;   // 上次没从网上取到的时间：之后 10 分钟不再去取，直接用内置的（连不上的网络要等十几秒才失败，每次「换一句」都等就像没反应）

WeatherWallpaper.card({
  name: '每日一句',
  height: 116,
  refresh: 86400,   // 每天零点换一句（壁纸启动时也会画一次）
  refreshButton: '换一句',   // 卡片设置里刷新按钮上的字

  style: `
    body { display: flex; flex-direction: column; justify-content: center; }
    .q { font-size: 16px; line-height: 1.6; display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 3; overflow: hidden; }
    .from { margin-top: 4px; text-align: right; font-size: 12.5px; opacity: .7; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  `,

  async data(api) {
    const day = Math.floor((api.now - new Date(api.now).getTimezoneOffset() * 60000) / 86400000);   // 今天是第几天：同一天挑同一句
    // 点了「换一句」（api.manual）：挑下一句
    if (skip.day !== day) skip = { day, n: 0 };
    if (api.manual) skip.n++;
    const k = day + skip.n;
    // 1. 自己写的句子
    try {
      const lines = (await api.readText(FILE)).split(/\r?\n/).map(s => s.trim()).filter(Boolean);
      if (lines.length) {
        const [text, from] = lines[k % lines.length].split(/\s+[—-]{1,2}\s+/);
        return { text, from: from || '' };
      }
    } catch (e) { /* 没有这个文件：试下一种 */ }
    // 2. 网上的。一言每次给的都不一样：今天取到过就一直用那句（同一天也可能重画，比如卡片调了高度）；点了「换一句」才重新取
    //    （这一轮联网不用 60 秒内的缓存，会真去取一次）
    if (today && today.day === day && !api.manual) return today.q;
    if (api.now - netDown > 10 * 60000) {
      try {
        const j = await api.fetchJson(API_URL);
        if (j && j.hitokoto) {
          today = { day, q: { text: j.hitokoto, from: (j.from_who || '') + (j.from ? '《' + j.from + '》' : '') } };
          return today.q;
        }
      } catch (e) { /* 没联网、网站出了问题、这个网络上不去：用内置的 */ }
      netDown = api.now;
    }
    // 3. 内置的
    const [text, from] = BUILTIN[k % BUILTIN.length];
    return { text, from };
  },

  render(q, api) {
    // 网上来的、文件里读的文字，一定要 api.esc 之后再放进 HTML
    return `<div class="q">${api.esc(q.text)}</div>` + (q.from ? `<div class="from">—— ${api.esc(q.from)}</div>` : '');
  },
});
