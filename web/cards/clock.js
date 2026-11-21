// ============================================================================
// 天气壁纸 · 自定义卡片示例：时钟
//
// 这个文件夹（%APPDATA%\WeatherWallpaper\cards\）里的每个 .js 文件就是一种卡片。
// 用法：托盘「卡片设置」→「＋ 新增卡片」→ 在「自定义卡片」下面选它。同一种卡片可以加好几张。
// 想做自己的卡片：把这个文件复制一份、改个名字（比如「我的卡片.js」），改里面的 render 就行。
// 保存后桌面上马上更新；不想要了就删掉，或者把扩展名改成别的（比如 .js.off）。
//
// ---- WeatherWallpaper.card({...}) 能填的东西 ----
//   name       卡片的名字（新增卡片时默认用它，之后可以在卡片设置里改）
//   height     新增卡片时的高度（像素，64–720；之后在卡片设置里改）。宽度固定 336
//   refresh    多少秒重画一次（默认 60，最少 1）。按本地时间对齐：60 = 每分钟整、3600 = 每小时整、86400 = 每天零点；
//              桌面被窗口挡住时最多一分钟一次，出错了 1 分钟后再试
//   style      （可选）卡片的 CSS
//   data(api)  （可选）准备数据，可以写成 async 的：联网、读文件都在这里做。返回值交给 render
//   render(d, api)  返回卡片的 HTML（字符串）。d = data() 的返回值
//
// ---- api ----
//   api.now                这一次重画的时间（毫秒）
//   api.date(t)            日期时间（t 不填 = 现在）：{ year, month, day, hour, minute, second, weekday（0 = 周日）,
//                          week '星期六', weekShort '周六', text '11月21日', time '08:05', lunar '十月十三', lunarMonth, lunarDay, lunarLeap }
//   api.daysUntil(t)       离那天还有几天（今天 = 0，过去了是负数）。t 可以是 '2027-01-01'、毫秒数、Date
//   api.nextLunar(月, 日)  下一个农历某月某日是哪天（毫秒），比如 api.nextLunar(1, 1) 是下一个春节
//   api.weather            默认卡片那个城市的天气（还没拿到时是 null）：
//                            { city, temp, feels（体感）, humidity, text '多云', kind, isDay, wind '东北风 3级', hi, lo,
//                              aqi: { value, text '良', color } 或 null, sunrise '06:41', sunset '17:02',
//                              hours: [{ time '14:00', temp, kind, text, isDay, rain（降水概率 %）}] 接下来 24 小时,
//                              days: [{ date '2026-11-21', week '周六', hi, lo, kind, text, rain, sunrise, sunset }] 从今天起 }
//   api.icon(kind, isDay)  天气图标（SVG 文本），kind 用上面天气里的
//   api.esc(文字)          转成能放进 HTML 的文字。网上拿来的、文件里读的文字放进 HTML 之前一定要 esc
//   api.card               { width, height }：卡片里面能画的大小（像素）
//   await api.fetchJson(网址, 选项) / api.fetchText / api.fetchImage    联网只能用这几个。选项（可选）：{ method: 'POST', body, headers }
//                          fetchImage 返回 data: 网址，写进 <img src="..."> 用
//                          限制：只能访问互联网上的网站（本机、局域网里的地址不行）；内容最大 1 MB、15 秒超时；
//                          同一个请求 60 秒内只真的发一次（再要就给上次的结果）；每张卡片每分钟最多 10 个不同的请求；
//                          不保存 Cookie（要用就自己写在 headers 里；跳转到别的网站时不再带 Authorization 和 Cookie）
//   await api.readText(文件名) / api.readJson / api.image     读这个文件夹里的文件（最大 256 KB；可以带子文件夹，比如 'pic/a.png'）
//                          image 返回 data: 网址。符号链接、目录联接不能读
//                          读文件、联网合起来每分钟最多 600 次（在循环里一直要数据的会被结束）
//
// ---- 规矩（犯了的话卡片会藏起来，原因在卡片设置里能看到）----
//   · 只能看、不能点：不能有链接、按钮、输入框；HTML 里的 <script>、onclick 不会运行
//   · 不能有一直在动的东西：CSS 动画（animation / @keyframes）、动图、<marquee> 都不行；要变的内容靠 refresh 重画
//   · 图片只能用 data: 网址（api.image / api.fetchImage 给的）；CSS 里不能用 url() 引用外部文件
//   · render 要在 2 秒内返回；脚本里没有 setTimeout、fetch、document 这些
//   · 卡片里默认留了 16px 20px 的边距、白色 14px 的字，想改就在 style 里写 body { ... }
//
// 出错时卡片会先藏起来（位置留着，桌面不会乱），改好保存后自动出来；
// 出错原因在卡片设置里看得到，也写在 %LOCALAPPDATA%\WeatherWallpaper\log.txt 里（搜 card）。
// ============================================================================

WeatherWallpaper.card({
  name: '时钟',
  height: 132,
  refresh: 60,   // 只显示到分钟：每分钟整点重画一次就够了

  style: `
    .top { display: flex; align-items: center; justify-content: space-between; }
    .time { font-size: 56px; font-weight: 200; line-height: 1; letter-spacing: -1px; font-variant-numeric: tabular-nums; }
    .date { text-align: right; font-size: 14px; line-height: 1.6; }
    .date .lunar { font-size: 12.5px; opacity: .75; }
    .wx { display: flex; align-items: center; gap: 6px; margin-top: 10px; height: 24px; font-size: 13.5px; opacity: .92; white-space: nowrap; overflow: hidden; }
    .wx svg { width: 28px; height: 28px; flex: none; }
    .wx .city { overflow: hidden; text-overflow: ellipsis; }
    .dot { display: inline-block; width: 7px; height: 7px; border-radius: 50%; margin: 0 4px 1px 4px; }
  `,

  render(d, api) {
    const t = api.date();
    let html = `<div class="top"><div class="time">${t.time}</div>
      <div class="date">${t.text} ${t.weekShort}<div class="lunar">农历${t.lunar}</div></div></div>`;
    const w = api.weather;
    if (w) {
      html += `<div class="wx">${api.icon(w.kind, w.isDay)}<span class="city">${api.esc(w.city)}</span><span>${w.text} ${w.temp}°</span>`
        + (w.aqi ? `<span><i class="dot" style="background:${w.aqi.color}"></i>空气${w.aqi.text}</span>` : '') + '</div>';
    }
    return html;
  },
});
