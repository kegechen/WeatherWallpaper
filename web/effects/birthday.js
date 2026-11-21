// ============================================================================
// 天气壁纸 · 自定义效果示例：生日
//
// 这个文件夹（%APPDATA%\WeatherWallpaper\effects\）里的每个 .js 文件就是一个效果。
// 想给家人过生日：把这个文件复制一份、改个名字（比如「妈妈生日.js」），再改下面的 WHO 和 dates 就行。
// 保存后壁纸会自动重新加载；托盘「效果预览 → 自定义」里点它的文件名可以马上看效果。
// 不想要了就删掉，或者把扩展名改成别的（比如 .js.off）。
//
// ---- WeatherWallpaper.effect({...}) 能填的东西 ----
//   name      托盘预览、卡片上显示的名字
//   dates     哪些日子出现（按天气所在城市的当地日期），可以写好几个：
//               '03-15'        每年公历 3 月 15 日
//               'lunar:08-15'  每年农历八月十五（也可以写 '农历08-15'；闰月不算）
//               '2027-01-01'   只在那一天
//   when(d)   （可选）更灵活的判断，返回 true 就出现。d = { year, month, day, weekday(0=周日), hour, minute, lunar: { month, day, leap } }
//               例：when: d => d.weekday === 5 && d.hour >= 18   // 每周五晚上
//   fireworks 当天夜里放烟花：0 不放 / 1 隔一阵放一发 / 2 很密
//   preview   托盘预览时的天气和时间：{ time: 'night' 或 'day', weather: 'clear' / 'partly' / 'snow' / 'rain' … }
//   fps       想要的帧率（默认 30；画面动得慢可以写 10–15，更省电）
//   setup(fx)      效果出现时调一次（可选），一般在这里初始化 fx.state
//   draw(ctx, fx)  每帧调一次，用 ctx（Canvas 2D）往壁纸上画。画在卡片和雨雪下面，每帧画之前画布已经清空
//
// ---- draw 拿到的 fx ----
//   fx.W, fx.H       屏幕大小（CSS 像素）
//   fx.k             缩放系数 = H / 1080：按 1080p 屏设计的尺寸乘上它，在各种分辨率上大小都合适
//   fx.ground        地平线的 y（任务栏顶边；没有底部任务栏时是屏幕下边）
//   fx.t             这个效果出现了多少秒（做入场动画）；fx.dt 距上一帧的秒数；fx.time 全局动画时钟
//   fx.night         0 = 白天 … 1 = 深夜（fx.daylight 正好相反）
//   fx.weather       { rain, snow, cloud, fog, thunder, wind }，都是 0–1，wind 正数往右吹
//   fx.date          同上面 when 的 d；fx.preview 是不是在效果预览里
//   fx.cards         屏幕上卡片的位置 [{ x, y, w, h }]（天气卡片和自定义卡片；藏着的——还没画好、出错了——不算）
//   fx.state         这个效果自己的小本子，随便往里放东西（每次出现时清空）
//   fx.free(x, w, y0, y1)    宽 w 的东西放在 [y0, y1] 这段高度上：离 x 最近、又不被卡片挡住的中心 x
//   fx.glow(颜色, x, y, 半径, 亮度)   一团柔光（颜色写 '#rrggbb'）
//   fx.shade(颜色)           按夜色压暗的颜色，和内置的节日装饰一个色调
//   fx.firework(x, y)        放一发烟花：从 x 处升空，在 y 高度炸开（都可以不填）
//   fx.rand()                0–1 的随机数
//
// 脚本出错不会影响壁纸，只会停掉这个效果；出错信息写在 %LOCALAPPDATA%\WeatherWallpaper\log.txt 里（搜 effect）。
// ============================================================================

const WHO = '';   // 寿星的称呼，比如 '妈妈'；空着就只写「生日快乐」

WeatherWallpaper.effect({
  name: '生日快乐（示例）',
  dates: [],                  // 示例默认不在任何一天出现。改成比如 ['03-15'] 或 ['lunar:08-15']
  fireworks: 1,
  preview: { time: 'night' },
  fps: 30,

  setup(fx) {
    const s = fx.state;
    s.balloons = [];
    for (let i = 0; i < 9; i++) s.balloons.push(newBalloon(fx, true));
    s.confetti = [];
    s.fwLeft = 3;             // 天黑时一出来先放三发
    s.fwAt = 1.5;
  },

  draw(ctx, fx) {
    const s = fx.state, k = fx.k;
    drawBalloons(ctx, fx, s);
    drawCake(ctx, fx);
    drawBanner(ctx, fx);
    drawConfetti(ctx, fx, s);
    // 夜里一出现就连放三发烟花
    if (fx.night > 0.6 && s.fwLeft > 0 && fx.t > s.fwAt) {
      fx.firework(fx.W * (0.25 + fx.rand() * 0.5), fx.H * (0.15 + fx.rand() * 0.15));
      s.fwLeft--; s.fwAt = fx.t + 0.7;
    }
  },
});

const COLORS = ['#ff6b8b', '#ffb84d', '#6bc7ff', '#9b7bff', '#5fd99a', '#ff8fd2'];

// ---- 气球：从屏幕下面飘上来，飘出顶上再从下面重新来 ----
function newBalloon(fx, first) {
  const k = fx.k, r = (24 + fx.rand() * 12) * k;
  return {
    x: fx.W * (0.05 + fx.rand() * 0.9),
    y: fx.H + r * 2 + (first ? fx.rand() * fx.H * 0.6 : 0),
    r, c: COLORS[(fx.rand() * COLORS.length) | 0],
    sp: (38 + fx.rand() * 30) * k, ph: fx.rand() * 6.28,
  };
}
function drawBalloons(ctx, fx, s) {
  const k = fx.k, want = fx.weather.rain > 0.3 ? 4 : 9;   // 下雨天少放几个
  for (let i = s.balloons.length - 1; i >= 0; i--) {
    const b = s.balloons[i];
    b.y -= b.sp * fx.dt;
    b.x += (Math.sin(fx.time * 0.8 + b.ph) * 14 + fx.weather.wind * 30) * k * fx.dt;
    if (b.y < -b.r * 4) { if (s.balloons.length > want) s.balloons.splice(i, 1); else s.balloons[i] = newBalloon(fx, false); continue; }
    const sway = Math.sin(fx.time * 1.3 + b.ph) * 6 * k;
    // 绳子
    ctx.strokeStyle = 'rgba(230,230,240,0.55)'; ctx.lineWidth = 1.2 * k;
    ctx.beginPath(); ctx.moveTo(b.x, b.y + b.r * 1.2);
    ctx.quadraticCurveTo(b.x + sway, b.y + b.r * 2.3, b.x - sway * 0.5, b.y + b.r * 3.6); ctx.stroke();
    // 球身 + 底下的小结 + 高光
    ctx.fillStyle = fx.shade(b.c);
    ctx.beginPath(); ctx.ellipse(b.x, b.y, b.r, b.r * 1.18, 0, 0, Math.PI * 2); ctx.fill();
    ctx.beginPath(); ctx.moveTo(b.x - 4 * k, b.y + b.r * 1.26); ctx.lineTo(b.x + 4 * k, b.y + b.r * 1.26); ctx.lineTo(b.x, b.y + b.r * 1.12); ctx.fill();
    ctx.fillStyle = 'rgba(255,255,255,0.35)';
    ctx.beginPath(); ctx.ellipse(b.x - b.r * 0.35, b.y - b.r * 0.45, b.r * 0.22, b.r * 0.32, -0.5, 0, Math.PI * 2); ctx.fill();
  }
}

// ---- 地上一个两层蛋糕，三根蜡烛（夜里烛光更亮） ----
function drawCake(ctx, fx) {
  const k = fx.k, gy = fx.ground - 4 * k;
  const pop = Math.min(1, fx.t / 0.8), e = 1 - Math.pow(1 - pop, 3);   // 出来时往上"长"一下
  const x = fx.free(fx.W * 0.3, 160 * k, gy - 130 * k, gy);            // 尽量摆在左边三分之一处，让开卡片
  ctx.save();
  ctx.translate(x, gy); ctx.scale(k, k * e);                           // 从这里开始按 1080p 的像素画，原点在蛋糕底部正中
  ctx.fillStyle = fx.shade('#e9e4da'); ctx.beginPath(); ctx.ellipse(0, 0, 78, 8, 0, 0, Math.PI * 2); ctx.fill();   // 盘子
  ctx.fillStyle = fx.shade('#f39bb8'); ctx.fillRect(-62, -48, 124, 46);   // 下层
  ctx.fillStyle = fx.shade('#fff1d6'); ctx.fillRect(-42, -84, 84, 36);    // 上层
  ctx.fillStyle = fx.shade('#ffffff');                                     // 两层的奶油花边
  for (let i = 0; i < 8; i++) { ctx.beginPath(); ctx.arc(-54 + i * 15.5, -48, 8, 0, Math.PI); ctx.fill(); }
  for (let i = 0; i < 6; i++) { ctx.beginPath(); ctx.arc(-35 + i * 14, -84, 7, 0, Math.PI); ctx.fill(); }
  for (let i = 0; i < 3; i++) {
    const cx = -22 + i * 22;
    ctx.fillStyle = fx.shade(COLORS[i * 2]); ctx.fillRect(cx - 3, -110, 6, 26);   // 蜡烛
    const fl = 0.8 + 0.2 * Math.sin(fx.time * 17 + i * 2.1) * Math.sin(fx.time * 7.3 + i);   // 火苗一闪一闪
    fx.glow('#ffb347', cx, -118, 22 + 10 * fx.night, (0.35 + 0.5 * fx.night) * fl);
    ctx.fillStyle = '#ffd36b';
    ctx.beginPath(); ctx.ellipse(cx, -118, 3.6 * fl, 7 * fl, 0, 0, Math.PI * 2); ctx.fill();
  }
  ctx.restore();
}

// ---- 屏幕中上方一行字，每个字依次跳一下 ----
function drawBanner(ctx, fx) {
  const k = fx.k, text = (WHO ? WHO + '，' : '') + '生日快乐！';
  const a = Math.min(1, fx.t / 1.2);                     // 淡入
  ctx.font = `bold ${Math.round(72 * k)}px "Microsoft YaHei", sans-serif`;
  ctx.textBaseline = 'middle';
  const chars = [...text], ws = chars.map(c => ctx.measureText(c).width), total = ws.reduce((p, w) => p + w, 0);
  const y = fx.H * 0.3, cx = fx.free(fx.W / 2, total + 60 * k, y - 60 * k, y + 60 * k);
  let x = cx - total / 2;
  ctx.globalAlpha = a;
  ctx.lineWidth = 6 * k; ctx.lineJoin = 'round'; ctx.strokeStyle = 'rgba(60,30,40,0.45)';
  chars.forEach((c, i) => {
    const jump = Math.max(0, Math.sin(fx.time * 2.2 - i * 0.5)) * 14 * k;   // 波浪一样一个接一个跳
    ctx.strokeText(c, x, y - jump);
    ctx.fillStyle = COLORS[i % COLORS.length];
    ctx.fillText(c, x, y - jump);
    x += ws[i];
  });
  ctx.globalAlpha = 1;
}

// ---- 出现后的前 12 秒撒彩纸 ----
function drawConfetti(ctx, fx, s) {
  const k = fx.k;
  if (fx.t < 12) for (let n = Math.round(60 * fx.dt); n > 0 && s.confetti.length < 260; n--)
    s.confetti.push({ x: fx.rand() * fx.W, y: -10 * k, vx: (fx.rand() - 0.5) * 60 * k, vy: (60 + fx.rand() * 80) * k, a: fx.rand() * 6.28, va: (fx.rand() - 0.5) * 10, c: COLORS[(fx.rand() * COLORS.length) | 0] });
  for (let i = s.confetti.length - 1; i >= 0; i--) {
    const p = s.confetti[i];
    p.x += (p.vx + Math.sin(fx.time * 3 + i) * 30 * k) * fx.dt; p.y += p.vy * fx.dt; p.a += p.va * fx.dt;
    if (p.y > fx.ground) { s.confetti.splice(i, 1); continue; }
    ctx.save(); ctx.translate(p.x, p.y); ctx.rotate(p.a);
    ctx.fillStyle = p.c; ctx.fillRect(-4 * k, -2 * k * Math.cos(p.a * 2), 8 * k, 4 * k * Math.cos(p.a * 2));   // 翻转时一会儿宽一会儿窄
    ctx.restore();
  }
}
