'use strict';
// 天气图标（内联 SVG，64x64 视口）
const Icons = (() => {
  const CLOUD = 'M18 46h28a10 10 0 0 0 1-20 14 14 0 0 0-27-3 9 9 0 0 0-2 23z';
  const defs = `<defs>
    <radialGradient id="gSun" cx=".4" cy=".35" r=".7"><stop offset="0" stop-color="#FFF4B8"/><stop offset=".6" stop-color="#FFD23F"/><stop offset="1" stop-color="#FFA928"/></radialGradient>
    <linearGradient id="gCloud" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#FFFFFF"/><stop offset="1" stop-color="#DCE4EE"/></linearGradient>
    <linearGradient id="gCloud2" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#C9D2DD"/><stop offset="1" stop-color="#9AA6B4"/></linearGradient>
    <linearGradient id="gDark" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#9AA3B0"/><stop offset="1" stop-color="#5F6875"/></linearGradient>
    <linearGradient id="gMoon" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#FFF8DC"/><stop offset="1" stop-color="#E9D99A"/></linearGradient>
  </defs>`;

  function sun(cx, cy, r) {
    let rays = '';
    for (let i = 0; i < 8; i++) {
      const a = i * Math.PI / 4, c = Math.cos(a), s = Math.sin(a);
      rays += `<line x1="${cx + c * r * 1.45}" y1="${cy + s * r * 1.45}" x2="${cx + c * r * 1.95}" y2="${cy + s * r * 1.95}"/>`;
    }
    return `<g stroke="#FFC83D" stroke-width="${r * 0.3}" stroke-linecap="round">${rays}</g><circle cx="${cx}" cy="${cy}" r="${r}" fill="url(#gSun)"/>`;
  }
  function moon(cx, cy, r) {
    return `<path d="M${cx} ${cy - r}A${r} ${r} 0 1 0 ${cx} ${cy + r}A${r * 0.62} ${r} 0 0 1 ${cx} ${cy - r}z" fill="url(#gMoon)" transform="rotate(-25 ${cx} ${cy})"/>`;
  }
  const cloud = (fill, tf = '') => `<path d="${CLOUD}" fill="url(#${fill})" transform="${tf}"/>`;
  function lines(n, len, color, w, y0 = 47) {
    let s = '';
    const xs = n === 4 ? [20, 28, 36, 44] : n === 2 ? [27, 39] : [22, 32, 42];
    xs.forEach((x, i) => {
      const y = y0 + (i % 2) * 3;
      s += `<line x1="${x + 2}" y1="${y}" x2="${x - len * 0.35}" y2="${y + len}"/>`;
    });
    return `<g stroke="${color}" stroke-width="${w}" stroke-linecap="round">${s}</g>`;
  }
  function flakes(n) {
    const xs = n === 2 ? [26, 40] : [22, 32, 42];
    return xs.map((x, i) => `<circle cx="${x}" cy="${52 + (i % 2) * 5}" r="2.6" fill="#fff"/>`).join('');
  }

  function svg(kind, day) {
    const lift = 'translate(0 -6)';
    let b = '';
    switch (kind) {
      case 'clear': b = day ? sun(32, 32, 12) : moon(32, 32, 15); break;
      case 'partly':
        b = (day ? sun(24, 22, 9) : moon(24, 22, 10)) + cloud('gCloud', 'translate(4 6) scale(.92)'); break;
      case 'cloudy': b = cloud('gCloud2', 'translate(10 -5) scale(.8)') + cloud('gCloud', 'translate(-2 3)'); break;
      case 'fog':
        b = cloud('gCloud', 'translate(3 -8) scale(.9)') +
          '<g stroke="#fff" stroke-width="3" stroke-linecap="round" opacity=".85"><line x1="14" y1="46" x2="50" y2="46"/><line x1="18" y1="53" x2="46" y2="53"/><line x1="22" y1="60" x2="42" y2="60"/></g>'; break;
      case 'drizzle': b = lines(3, 5, '#8FD0FF', 2.6) + cloud('gCloud', lift); break;
      case 'rain': b = lines(3, 9, '#6EC1FF', 3) + cloud('gCloud2', lift); break;
      case 'heavy': b = lines(4, 12, '#57B2FF', 3.2) + cloud('gDark', lift); break;
      case 'thunder':
        b = lines(2, 9, '#6EC1FF', 3) + cloud('gDark', lift) +
          '<path d="M35 38l-9 12h7l-4 11 12-15h-7l4-8z" fill="#FFD23F" stroke="#F5A623" stroke-width="1" stroke-linejoin="round"/>'; break;
      case 'snow': b = flakes(3) + cloud('gCloud', lift); break;
      case 'sleet': b = lines(2, 8, '#6EC1FF', 3) + flakes(2) + cloud('gCloud2', lift); break;
      default: b = cloud('gCloud');
    }
    return `<svg viewBox="0 0 64 64" xmlns="http://www.w3.org/2000/svg">${defs}${b}</svg>`;
  }

  const pin = '<svg viewBox="0 0 24 24"><path fill="#fff" d="M12 2a7 7 0 0 0-7 7c0 5.2 7 13 7 13s7-7.8 7-13a7 7 0 0 0-7-7zm0 9.5A2.5 2.5 0 1 1 12 6.5a2.5 2.5 0 0 1 0 5z"/></svg>';
  return { svg, pin };
})();
