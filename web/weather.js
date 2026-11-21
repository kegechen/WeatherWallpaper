'use strict';
// 天气数据解释：WMO 天气码、中国 AQI、风力、渲染场景参数、天气卡片
const WX = (() => {
  const CODES = {
    0: ['clear', '晴'], 1: ['partly', '晴间多云'], 2: ['partly', '多云'], 3: ['cloudy', '阴'],
    45: ['fog', '雾'], 48: ['fog', '冻雾'],
    51: ['drizzle', '小毛毛雨'], 53: ['drizzle', '毛毛雨'], 55: ['drizzle', '大毛毛雨'],
    56: ['sleet', '冻毛毛雨'], 57: ['sleet', '冻毛毛雨'],
    61: ['rain', '小雨'], 63: ['rain', '中雨'], 65: ['heavy', '大雨'],
    66: ['sleet', '冻雨'], 67: ['sleet', '强冻雨'],
    71: ['snow', '小雪'], 73: ['snow', '中雪'], 75: ['snow', '大雪'], 77: ['snow', '米雪'],
    80: ['rain', '阵雨'], 81: ['rain', '强阵雨'], 82: ['heavy', '暴雨'],
    85: ['snow', '阵雪'], 86: ['snow', '强阵雪'],
    95: ['thunder', '雷阵雨'], 96: ['thunder', '雷阵雨伴冰雹'], 99: ['thunder', '强雷暴伴冰雹'],
  };
  const info = code => { const c = CODES[code] || ['cloudy', '—']; return { kind: c[0], text: c[1] }; };

  // 渲染参数：cloud 云量 0..1，rain/snow 强度 0..1，fog，thunder，dark 压暗
  function scene(code, cloudCover) {
    const s = { cloud: (cloudCover == null ? 30 : cloudCover) / 100, rain: 0, snow: 0, fog: 0, thunder: 0, dark: 0 };
    const at = (lo, hi) => { s.cloud = Math.min(hi, Math.max(lo, s.cloud)); };
    switch (code) {
      case 0: at(0, 0.08); break;
      case 1: at(0.15, 0.35); break;
      case 2: at(0.4, 0.65); break;
      case 3: at(0.92, 1); s.dark = 0.08; break;
      case 45: case 48: s.fog = 0.85; at(0.5, 0.8); break;
      case 51: s.rain = 0.1; at(0.85, 1); s.dark = .1; break;
      case 53: s.rain = 0.16; at(0.85, 1); s.dark = .1; break;
      case 55: s.rain = 0.24; at(0.9, 1); s.dark = .12; break;
      case 56: case 57: s.rain = 0.18; s.snow = 0.12; at(0.9, 1); s.dark = .12; break;
      case 61: s.rain = 0.32; at(0.95, 1); s.dark = .15; break;
      case 63: s.rain = 0.58; at(0.95, 1); s.dark = .25; break;
      case 65: s.rain = 0.88; at(0.95, 1); s.dark = .35; break;
      case 66: s.rain = 0.4; s.snow = 0.15; at(0.95, 1); s.dark = .2; break;
      case 67: s.rain = 0.7; s.snow = 0.2; at(0.95, 1); s.dark = .3; break;
      case 71: s.snow = 0.35; at(0.9, 1); s.dark = .05; break;
      case 73: s.snow = 0.6; at(0.92, 1); s.dark = .08; break;
      case 75: s.snow = 0.9; at(0.95, 1); s.dark = .12; break;
      case 77: s.snow = 0.25; at(0.9, 1); break;
      case 80: s.rain = 0.4; at(0.75, 0.95); s.dark = .15; break;
      case 81: s.rain = 0.65; at(0.85, 1); s.dark = .25; break;
      case 82: s.rain = 0.95; at(0.95, 1); s.dark = .4; break;
      case 85: s.snow = 0.5; at(0.85, 1); break;
      case 86: s.snow = 0.85; at(0.9, 1); s.dark = .1; break;
      case 95: s.rain = 0.72; s.thunder = 1; at(0.97, 1); s.dark = .45; break;
      case 96: case 99: s.rain = 0.85; s.thunder = 1; at(0.97, 1); s.dark = .5; break;
    }
    return s;
  }

  // 中国 HJ 633-2012 AQI（用实时浓度近似）
  const BP = {
    pm2_5: [0, 35, 75, 115, 150, 250, 350, 500],
    pm10: [0, 50, 150, 250, 350, 420, 500, 600],
    nitrogen_dioxide: [0, 100, 200, 700, 1200, 2340, 3090, 3840],
    ozone: [0, 160, 200, 300, 400, 800, 1000, 1200],
    carbon_monoxide: [0, 5, 10, 35, 60, 90, 120, 150], // mg/m3
  };
  const IAQI = [0, 50, 100, 150, 200, 300, 400, 500];
  function chinaAqi(cur) {
    if (!cur) return null;
    let best = -1, main = '';
    for (const k in BP) {
      let c = cur[k];
      if (c == null) continue;
      if (k === 'carbon_monoxide') c /= 1000;
      const bp = BP[k];
      let v = 500;
      for (let i = 1; i < bp.length; i++) {
        if (c <= bp[i]) { v = IAQI[i - 1] + (IAQI[i] - IAQI[i - 1]) * (c - bp[i - 1]) / (bp[i] - bp[i - 1]); break; }
      }
      if (v > best) { best = v; main = k; }
    }
    return best < 0 ? (cur.us_aqi != null ? cur.us_aqi : null) : Math.round(best);
  }
  function aqiLevel(v) {
    if (v == null) return null;
    const L = [[50, '优', '#4cd964'], [100, '良', '#ffd60a'], [150, '轻度污染', '#ff9f0a'], [200, '中度污染', '#ff453a'], [300, '重度污染', '#bf5af2'], [1e9, '严重污染', '#b0174a']];
    for (const l of L) if (v <= l[0]) return { text: l[1], color: l[2], value: v };
  }

  const DIRS = ['北', '东北', '东', '东南', '南', '西南', '西', '西北'];
  function wind(kmh, deg) {
    const th = [1, 6, 12, 20, 29, 39, 50, 62, 75, 89, 103, 118];
    let lv = 0; for (const t of th) if (kmh >= t) lv++;
    if (lv === 0) return '无风';
    return `${DIRS[Math.round(deg / 45) % 8]}风 ${lv}级`;
  }

  const hm = s => (s || '').slice(11, 16);
  const esc = s => String(s).replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[ch]);
  const WEEK = ['周日', '周一', '周二', '周三', '周四', '周五', '周六'];

  // 卡片上显示哪些内容（城市和当前天气总是显示）：和宿主 CardSpec.Show 同一套键，默认值也一样（托盘面板总是用默认值）
  const DEFAULT_SHOW = 'hilo,src,aqi,feel,hum,wind,hours,days,sun,upd';
  /** show = 逗号分隔的内容键（宿主已整理过）；null / undefined = 默认。各段高度写死在 style.css 里，和宿主 CardLayout.HeightOf 一一对应 */
  function panelHtml(w, localMs, previewLabel, show) {
    const S = new Set((show == null ? DEFAULT_SHOW : show).split(','));
    const has = k => S.has(k);
    const fc = w.fc, c = fc.current, d = fc.daily, h = fc.hourly;
    const now = info(c.weather_code), day = c.is_day === 1;
    const aqi = aqiLevel(chinaAqi(w.aq && w.aq.current));
    const iso = new Date(localMs).toISOString();
    const today = iso.slice(0, 10), curHour = iso.slice(0, 13);
    let di = d.time.indexOf(today); if (di < 0) di = 0;
    const r = v => Math.round(v);

    let hi = h.time.findIndex(t => t.slice(0, 13) === curHour); if (hi < 0) hi = 0;
    let hours = '';
    for (let i = hi; i < Math.min(h.time.length, hi + 6); i++) {
      const p = h.precipitation_probability ? h.precipitation_probability[i] : 0;
      hours += `<div><span class="h">${i === hi ? '现在' : h.time[i].slice(11, 13) + '时'}</span>
        ${Icons.svg(info(h.weather_code[i]).kind, h.is_day ? h.is_day[i] === 1 : true)}
        <span class="p">${p >= 20 ? p + '%' : ''}</span><span class="t">${r(h.temperature_2m[i])}°</span></div>`;
    }

    const end = Math.min(d.time.length, di + 5);
    let lo = Infinity, hiT = -Infinity;
    for (let i = di; i < end; i++) { lo = Math.min(lo, d.temperature_2m_min[i]); hiT = Math.max(hiT, d.temperature_2m_max[i]); }
    const span = Math.max(1, hiT - lo);
    let days = '';
    for (let i = di; i < end; i++) {
      const dt = new Date(d.time[i] + 'T00:00:00Z');
      const name = i === di ? '今天' : i === di + 1 ? '明天' : WEEK[dt.getUTCDay()];
      const a = (d.temperature_2m_min[i] - lo) / span * 100, b = (d.temperature_2m_max[i] - lo) / span * 100;
      days += `<div class="day"><span>${name}</span>${Icons.svg(info(d.weather_code[i]).kind, true)}
        <span class="lo">${r(d.temperature_2m_min[i])}°</span>
        <span class="bar"><i style="left:${a}%;right:${100 - b}%"></i></span>
        <span class="hi">${r(d.temperature_2m_max[i])}°</span></div>`;
    }

    const upd = new Date(w.ts + fc.utc_offset_seconds * 1000).toISOString().slice(11, 16) + ' 更新';
    // 不显示定位方式和日出日落、只显示更新时间：更新时间放到城市行右边（定位方式那个位置），省掉底部一行。
    // 按勾选判断（不看定位方式有没有数据），和宿主 CardLayout.HeightOf 一致
    const updTop = has('upd') && !has('sun') && !has('src');
    const src = updTop ? upd : has('src') ? { gps: '定位', ip: 'IP 定位', manual: '手动' }[w.loc.src] || '' : '';
    // 日期行：卡片所在城市的当地日期（和节日装饰同一个口径），如「11月21日 周六 · 十月十三」；只勾农历时写成「农历十月十三」免得看不出是什么
    const dateRow = has('date') || has('week') || has('lunar');
    let date = '';
    if (dateRow) {
      const t = new Date(localMs), parts = [];
      if (has('date')) parts.push((t.getUTCMonth() + 1) + '月' + t.getUTCDate() + '日');
      if (has('week')) parts.push(WEEK[t.getUTCDay()]);
      const lu = has('lunar') && window.LUNAR ? LUNAR.text(localMs) : '';
      date = parts.join(' ') + (lu ? (parts.length ? ' · ' + lu : '农历' + lu) : '');
    }
    // 指标标签（空气没有数据时不显示）：一个都不显示时整行去掉，卡片也跟着变矮（宿主按"勾了几项"算高度，没数据的空气不影响）
    const chips = [
      has('aqi') && aqi ? `<span class="chip" style="color:${aqi.color}"><i style="background:${aqi.color}"></i><span class="aqit" style="color:#fff" data-short="${aqi.text}">空气 ${aqi.text} ${aqi.value}</span></span>` : '',
      has('feel') ? `<span class="chip">体感 ${r(c.apparent_temperature)}°</span>` : '',
      has('hum') ? `<span class="chip">湿度 ${r(c.relative_humidity_2m)}%</span>` : '',
      has('wind') ? `<span class="chip">${wind(c.wind_speed_10m, c.wind_direction_10m)}</span>` : '',
    ].join('');
    const anyChip = has('aqi') || has('feel') || has('hum') || has('wind');
    const foot = [has('sun') ? `<span>日出 ${hm(d.sunrise[di])} · 日落 ${hm(d.sunset[di])}</span>` : '', has('upd') && !updTop ? `<span class="upd">${upd}</span>` : ''].join('');
    return `
      <div class="loc">${Icons.pin}<span>${w.loc.name ? esc(w.loc.name) : '当前位置'}</span>${previewLabel ? `<span class="tag">预览 · ${previewLabel}</span>` : ''}<span class="src">${src}</span></div>
      ${dateRow ? `<div class="date">${date}</div>` : ''}
      <div class="now"><div class="big">${Icons.svg(now.kind, day)}</div>
        <div class="temp">${r(c.temperature_2m)}°</div>
        <div class="desc"><b>${now.text}</b>${has('hilo') ? `<span>最高 ${r(d.temperature_2m_max[di])}° 最低 ${r(d.temperature_2m_min[di])}°</span>` : ''}</div></div>
      ${anyChip ? `<div class="chips">${chips}</div>` : ''}
      ${has('hours') ? `<div class="sep"></div><div class="hours">${hours}</div>` : ''}
      ${has('days') ? `<div class="sep"></div><div class="days">${days}</div>` : ''}
      ${foot ? `<div class="foot">${foot}</div>` : ''}`;
  }

  /** 卡片里的四个标签尽量排成一行：一行放不下就按当前宽度估算能放下的字号（一次设定 + 校验），校验不过再半档半档收（每档一次强制重排）；
   *  还放不下先把空气标签缩成级别名（「空气 轻度污染 145」→「轻度污染」，颜色不变）；实在放不下从后往前去掉标签（不换行：卡片高度是宿主按一行算好的，
   *  换成两行会把下面的内容挤出卡片）。root 是卡片元素，要在它插入页面后调用。 */
  function fitChips(root) {
    const el = root && root.querySelector('.chips');
    if (!el) return;
    const chips = el.querySelectorAll('.chip');
    for (const c of chips) c.style.display = '';
    el.style.fontSize = '';
    const shrink = () => {
      let fs = parseFloat(getComputedStyle(el).fontSize) || 12;
      if (el.scrollWidth <= el.clientWidth + 0.5) return;
      // 文字宽度约和字号成正比（标签的内边距、间距不缩）：按一次测量估算，估出来的字号只会偏大不会偏小，后面校验收尾
      fs = Math.max(10, Math.min(fs, Math.floor(fs * el.clientWidth / el.scrollWidth * 2) / 2));
      el.style.fontSize = fs + 'px';
      while (el.scrollWidth > el.clientWidth + 0.5 && fs > 10) { fs -= 0.5; el.style.fontSize = fs + 'px'; }
    };
    shrink();
    const aqi = el.querySelector('.aqit');
    if (el.scrollWidth > el.clientWidth + 0.5 && aqi && aqi.dataset.short) { aqi.textContent = aqi.dataset.short; el.style.fontSize = ''; shrink(); }
    for (let i = chips.length - 1; i > 0 && el.scrollWidth > el.clientWidth + 0.5; i--) chips[i].style.display = 'none';
  }

  /** 给自定义卡片的天气（api.weather）：整理好的、和天气卡片同一个口径的数字和文字（不给原始数据：每次重画都要整份复制进 Worker）。
   *  nowMs = 现在（真实时间）；时间、日期都是那个城市的当地时间。字段说明见 README「自定义卡片」 */
  function summary(w, nowMs) {
    const fc = w.fc, c = fc.current, d = fc.daily, h = fc.hourly;
    // 没有这个字段（Open-Meteo 总会给，防万一）就按 UTC 算：直接乘是 NaN，toISOString 会抛错，api.weather 就一直是 null 了
    const localMs = nowMs + (fc.utc_offset_seconds || 0) * 1000;
    const iso = new Date(localMs).toISOString(), today = iso.slice(0, 10), curHour = iso.slice(0, 13);
    let di = d.time.indexOf(today); if (di < 0) di = 0;
    let hi = h.time.findIndex(t => t.slice(0, 13) === curHour); if (hi < 0) hi = 0;
    const r = v => v == null || isNaN(v) ? null : Math.round(v);
    const now = info(c.weather_code), aqi = aqiLevel(chinaAqi(w.aq && w.aq.current));
    const hours = [], days = [];
    for (let i = hi; i < Math.min(h.time.length, hi + 24); i++) {
      const k = info(h.weather_code[i]);
      hours.push({ time: h.time[i].slice(11, 16), temp: r(h.temperature_2m[i]), kind: k.kind, text: k.text, isDay: h.is_day ? h.is_day[i] === 1 : true,
        rain: h.precipitation_probability ? r(h.precipitation_probability[i]) : null });
    }
    for (let i = di; i < d.time.length; i++) {
      const k = info(d.weather_code[i]);
      days.push({ date: d.time[i], week: WEEK[new Date(d.time[i] + 'T00:00:00Z').getUTCDay()], hi: r(d.temperature_2m_max[i]), lo: r(d.temperature_2m_min[i]),
        kind: k.kind, text: k.text, rain: d.precipitation_probability_max ? r(d.precipitation_probability_max[i]) : null,
        sunrise: hm(d.sunrise && d.sunrise[i]), sunset: hm(d.sunset && d.sunset[i]) });
    }
    return {
      city: w.loc && w.loc.name || '', temp: r(c.temperature_2m), feels: r(c.apparent_temperature), humidity: r(c.relative_humidity_2m),
      kind: now.kind, text: now.text, isDay: c.is_day === 1, code: c.weather_code, cloud: r(c.cloud_cover),
      wind: wind(c.wind_speed_10m, c.wind_direction_10m), windSpeed: r(c.wind_speed_10m), windDir: r(c.wind_direction_10m),
      hi: days.length ? days[0].hi : null, lo: days.length ? days[0].lo : null,
      aqi: aqi ? { value: aqi.value, text: aqi.text, color: aqi.color } : null,
      sunrise: days.length ? days[0].sunrise : '', sunset: days.length ? days[0].sunset : '',
      updated: w.ts || null, timezone: fc.timezone || '', utcOffset: fc.utc_offset_seconds || 0,
      hours, days,
    };
  }

  return { info, scene, chinaAqi, aqiLevel, wind, panelHtml, fitChips, summary };
})();
