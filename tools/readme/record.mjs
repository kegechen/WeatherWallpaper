// Record animated previews of the wallpaper page for README (offscreen, headless Edge -> frames -> ffmpeg GIF).
// usage: cd tools/readme && npm i && node record.mjs [scene ...]
// Native effects (taskbar / window eaves) are not rendered by the page; record those with record-native.ps1.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import puppeteer from 'puppeteer-core';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '../..');
const outDir = path.join(root, 'docs');
const tmp = path.join(here, '.frames');
fs.mkdirSync(outDir, { recursive: true });

const EDGE = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const W = 1920, H = 1080, SCALE = 0.4;   // 页面按 1920x1080 排版，输出 768x432

// 示例数据：伦敦的真实天气（不带任何本机位置信息）
const samplePath = path.join(outDir, 'sample-london.json');
async function sample() {
  if (fs.existsSync(samplePath) && !process.argv.includes('--refresh')) return;
  const ll = 'latitude=51.5072&longitude=-0.1276';
  const fc = await (await fetch('https://api.open-meteo.com/v1/forecast?' + ll +
    '&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,precipitation,rain,showers,snowfall,weather_code,cloud_cover,wind_speed_10m,wind_direction_10m' +
    '&hourly=temperature_2m,weather_code,precipitation_probability,is_day' +
    '&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset,precipitation_probability_max&timezone=auto&forecast_days=6')).json();
  const aq = await (await fetch('https://air-quality-api.open-meteo.com/v1/air-quality?' + ll +
    '&current=us_aqi,pm2_5,pm10,ozone,nitrogen_dioxide,carbon_monoxide&timezone=auto')).json();
  fs.writeFileSync(samplePath, JSON.stringify({ t: 'weather', loc: { name: '伦敦 · 威斯敏斯特', lat: 51.5072, lon: -0.1276, src: 'manual' }, fc, aq, ts: Date.now() }));
}

// name: [query, output seconds, output fps, speed-up]  —— 帧按虚拟时钟逐帧推进，不受截图速度影响
const SCENES = {
  clear:   ['mode=clear&at=12:30', 4, 12, 1],
  partly:  ['mode=partly&at=10:30', 4, 12, 1],
  sunset:  ['mode=sunset', 6, 12, 1],
  night:   ['mode=night', 4, 12, 1],
  drizzle: ['mode=drizzle&at=13:00', 3, 15, 1],
  heavy:   ['mode=heavy&at=14:00', 3, 15, 1],
  thunder: ['mode=thunder&at=17:30', 5, 15, 1],
  snow:    ['mode=snow&at=11:00', 4, 15, 1],
  day:     ['mode=partly&day=1', 10, 12, 4.8],   // 48 秒的一天快进，压成 10 秒
};

const server = http.createServer((req, res) => {
  const p = path.join(root, decodeURIComponent(req.url.split('?')[0]));
  if (!p.startsWith(root) || !fs.existsSync(p) || fs.statSync(p).isDirectory()) { res.writeHead(404); return res.end(); }
  const ext = path.extname(p);
  res.writeHead(200, { 'Content-Type': { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json' }[ext] || 'application/octet-stream' });
  fs.createReadStream(p).pipe(res);
});

await sample();
await new Promise(r => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const browser = await puppeteer.launch({ executablePath: EDGE, headless: true, args: ['--hide-scrollbars', '--force-color-profile=srgb'] });
const page = await browser.newPage();
await page.setViewport({ width: W, height: H, deviceScaleFactor: SCALE });

const names = process.argv.slice(2).filter(a => !a.startsWith('--'));
for (const name of names.length ? names.filter(n => SCENES[n]) : Object.keys(SCENES)) {
  const [query, secs, fps, speed] = SCENES[name];
  fs.rmSync(tmp, { recursive: true, force: true }); fs.mkdirSync(tmp);
  await page.goto(`http://127.0.0.1:${port}/web/index.html?manual=1&data=/docs/sample-london.json&${query}`);
  await page.waitForFunction('window.__ready === true');
  const dt = speed / fps;
  if (name !== 'day') await page.evaluate(() => { for (let k = 0; k < 60; k++) window.__step(1 / 20); });   // 先跑 3 秒让雨雪铺满
  const n = Math.round(secs * fps);
  for (let i = 0; i < n; i++) {
    await page.evaluate(d => window.__step(d), dt);
    await page.screenshot({ path: path.join(tmp, `f${String(i).padStart(4, '0')}.png`) });
  }
  const out = path.join(outDir, `${name}.webp`);
  execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-framerate', String(fps), '-i', path.join(tmp, 'f%04d.png'),
    '-c:v', 'libwebp_anim', '-lossless', '0', '-quality', '72', '-compression_level', '6', '-loop', '0', out]);
  console.log(`${name}: ${n} frames -> ${out} (${(fs.statSync(out).size / 1024).toFixed(0)} KB)`);
}
// 天气卡片清晰大图（实时数据，无预览标签）
if (!names.length || names.includes('card')) {
  const page = await browser.newPage();
  await page.setViewport({ width: W, height: H, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/web/index.html?manual=1&data=/docs/sample-london.json&at=12:30`);
  await page.waitForFunction('window.__ready === true');
  await page.evaluate(() => { for (let k = 0; k < 20; k++) window.__step(1 / 20); });
  const box = await (await page.$('#panel')).boundingBox();
  const pad = 24;
  await page.screenshot({ path: path.join(outDir, 'card.png'), clip: { x: box.x - pad, y: box.y - pad, width: box.width + pad * 2, height: box.height + pad * 2 }, captureBeyondViewport: false });
  console.log('card -> docs/card.png');
}
await browser.close();
server.close();
fs.rmSync(tmp, { recursive: true, force: true });
