/**
 * build-svg.mjs — 由 JSON 数据生成 NetCable 风格的网络拓扑示意图（纯 SVG，零第三方依赖）
 *
 * 用法：node tools/build-svg.mjs data/demo.json netcable-demo-topology.svg
 *       node tools/build-svg.mjs data/lan.json  netcable-lan-topology.svg
 *
 * 视觉令牌取自 NetCable-v1 源码：
 *   frontend/src/index.css      .custom-node / .status-online / .status-offline
 *   frontend/src/components/NetworkMap.jsx
 *       线缆：smoothstep 虚线、stroke-width 2.5、闭合箭头
 *       着色：calcularCorConexao() —— 目标离线 → #ef4444，正常 → #10b981
 *       图标：getIcon() —— Firewall→Shield、Roteador→Wifi、PC→Monitor、其余→Server
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '..');
const inputPath = resolve(root, process.argv[2] || 'data/demo.json');
const outputPath = resolve(root, process.argv[3] || 'netcable-topology.svg');

const data = JSON.parse(readFileSync(inputPath, 'utf8'));
const W = data.meta.canvas.width;
const H = data.meta.canvas.height;

const CARD_W = 300;
const CARD_H = 104;
const WAN_W = 250;
const WAN_H = 124;

const P = {
  bg: '#0b1220',
  card: '#1e293b',
  border: '#334155',
  name: '#f8fafc',
  muted: '#94a3b8',
  faint: '#64748b',
  online: '#10b981',
  offline: '#ef4444',
  unknown: '#64748b',
  blue: '#3b82f6',
  amber: '#f59e0b',
  slate: '#94a3b8',
  violet: '#a78bfa',
};

const TONE = { online: P.online, offline: P.offline, muted: P.unknown };

const ICONS = {
  shield:
    '<path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"/>',
  wifi:
    '<path d="M12 20h.01"/><path d="M2 8.82a15 15 0 0 1 20 0"/><path d="M5 12.86a10 10 0 0 1 14 0"/><path d="M8.5 16.43a5 5 0 0 1 7 0"/>',
  server:
    '<rect x="2" y="2" width="20" height="8" rx="2"/><rect x="2" y="14" width="20" height="8" rx="2"/><path d="M6 6h.01"/><path d="M6 18h.01"/>',
  monitor: '<rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8"/><path d="M12 17v4"/>',
  laptop:
    '<path d="M20 16V7a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v9m16 0H4m16 0 1.28 2.55a1 1 0 0 1-.9 1.45H3.62a1 1 0 0 1-.9-1.45L4 16"/>',
  smartphone: '<rect width="14" height="20" x="5" y="2" rx="2"/><path d="M12 18h.01"/>',
  globe:
    '<circle cx="12" cy="12" r="10"/><path d="M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20"/><path d="M2 12h20"/>',
  activity: '<path d="M22 12h-4l-3 9L9 3l-3 9H2"/>',
};

const KIND = {
  firewall: { icon: 'shield', color: P.amber },
  router: { icon: 'wifi', color: P.blue },
  ap: { icon: 'wifi', color: P.blue },
  switch: { icon: 'server', color: P.online },
  nas: { icon: 'server', color: P.online },
  printer: { icon: 'server', color: P.online },
  desktop: { icon: 'monitor', color: P.slate },
  laptop: { icon: 'laptop', color: P.slate },
  phone: { icon: 'smartphone', color: P.violet },
  wan: { icon: 'globe', color: P.blue },
};

const esc = (s) =>
  String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const n = (v) => Number(v.toFixed(2));

const byId = Object.fromEntries(data.devices.map((d) => [d.id, d]));
const box = (d) => (d.kind === 'wan' ? { w: WAN_W, h: WAN_H } : { w: CARD_W, h: CARD_H });
const isWan = (d) => d.kind === 'wan';

/** 粗略字宽估算：CJK 按 1em，其余按 0.55em（用于自动缩放与标签底板） */
function est(text, size) {
  let w = 0;
  for (const ch of String(text)) {
    const code = ch.codePointAt(0);
    if (code > 0x2e7f) w += size;
    else if (/[A-Z0-9]/.test(ch)) w += size * 0.62;
    else w += size * 0.53;
  }
  return w;
}

const tone = (d) => (d.status === 'online' ? 'online' : d.status === 'offline' ? 'offline' : 'muted');

const counted = data.devices.filter((d) => !isWan(d));
const counts = {
  online: counted.filter((d) => d.status === 'online').length,
  offline: counted.filter((d) => d.status === 'offline').length,
};
counts.unknown = counted.length - counts.online - counts.offline;

/* ---------------- 走线 ---------------- */

function routePoints(link) {
  const a = byId[link.from];
  const b = byId[link.to];
  if (!a || !b) throw new Error(`unknown link endpoint: ${link.from} -> ${link.to}`);
  const A = box(a);
  const B = box(b);

  if (link.route === 'trunk') {
    const sy = a.y + (link.start_offset || 0);
    return [
      { x: a.x + A.w / 2, y: sy },
      { x: link.trunk_x, y: sy },
      { x: link.trunk_x, y: b.y },
      { x: b.x - B.w / 2, y: b.y },
    ];
  }
  if (a.y === b.y) {
    return [
      { x: a.x + A.w / 2, y: a.y },
      { x: b.x - B.w / 2, y: b.y },
    ];
  }
  if (a.x === b.x) {
    const upwards = b.y < a.y;
    return [
      { x: a.x, y: a.y + (upwards ? -A.h / 2 : A.h / 2) },
      { x: b.x, y: b.y + (upwards ? B.h / 2 : -B.h / 2) },
    ];
  }
  throw new Error(`no route for ${link.from} -> ${link.to}`);
}

function pathFromPoints(pts, radius = 18) {
  const d = [`M${n(pts[0].x)} ${n(pts[0].y)}`];
  for (let i = 1; i < pts.length - 1; i++) {
    const p0 = pts[i - 1];
    const p1 = pts[i];
    const p2 = pts[i + 1];
    const v1 = { x: p0.x - p1.x, y: p0.y - p1.y };
    const v2 = { x: p2.x - p1.x, y: p2.y - p1.y };
    const l1 = Math.hypot(v1.x, v1.y) || 1;
    const l2 = Math.hypot(v2.x, v2.y) || 1;
    const r = Math.min(radius, l1 / 2, l2 / 2);
    d.push(`L${n(p1.x + (v1.x / l1) * r)} ${n(p1.y + (v1.y / l1) * r)}`);
    d.push(`Q${n(p1.x)} ${n(p1.y)} ${n(p1.x + (v2.x / l2) * r)} ${n(p1.y + (v2.y / l2) * r)}`);
  }
  const last = pts[pts.length - 1];
  d.push(`L${n(last.x)} ${n(last.y)}`);
  return d.join(' ');
}

/** 标签放在最长的水平段中点；没有水平段则退化为最长段 */
function labelAnchor(pts) {
  let best = null;
  for (let i = 1; i < pts.length; i++) {
    const a = pts[i - 1];
    const b = pts[i];
    const len = Math.hypot(b.x - a.x, b.y - a.y);
    const horizontal = Math.abs(b.y - a.y) < 0.5;
    const score = horizontal ? len * 2 : len;
    if (!best || score > best.score) {
      best = { score, x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
    }
  }
  return best;
}

const MARKER = { online: 'url(#arrowGreen)', offline: 'url(#arrowRed)', muted: 'url(#arrowGrey)' };

function renderLink(link) {
  const target = byId[link.to];
  const t = link.tone || tone(target);
  const color = TONE[t];
  const pts = routePoints(link);
  const d = pathFromPoints(pts);
  const parts = [
    '  <g class="link">',
    `    <path d="${d}" fill="none" stroke="${color}" stroke-width="8" stroke-linecap="round" opacity="0.10" filter="url(#edgeGlow)"/>`,
    `    <path class="flow" d="${d}" fill="none" stroke="${color}" stroke-width="2.5" stroke-linecap="round" stroke-dasharray="9 8" marker-end="${MARKER[t]}"/>`,
  ];
  if (link.label) {
    const a = labelAnchor(pts);
    const w = Math.round(est(link.label, 11.5) + 18);
    parts.push(
      `    <g class="edge-label">`,
      `      <rect x="${n(a.x - w / 2)}" y="${n(a.y - 10.5)}" width="${w}" height="21" rx="10.5" fill="#132034" stroke="#2b3d55" stroke-width="1"/>`,
      `      <text x="${n(a.x)}" y="${n(a.y + 4)}" text-anchor="middle" fill="#c7d5e6" font-size="11.5">${esc(link.label)}</text>`,
      `    </g>`
    );
  }
  parts.push('  </g>');
  return parts.join('\n');
}

/* ---------------- 节点 ---------------- */

function renderIcon(name, x, y, size, color, width = 1.9) {
  const body = ICONS[name];
  if (!body) throw new Error(`unknown icon ${name}`);
  return [
    `  <g transform="translate(${n(x)} ${n(y)}) scale(${n(size / 24)})" fill="none" stroke="${color}" stroke-width="${width}" stroke-linecap="round" stroke-linejoin="round">`,
    body,
    '  </g>',
  ].join('\n');
}

function renderWan(d) {
  const left = d.x - WAN_W / 2;
  const top = d.y - WAN_H / 2;
  const kind = KIND.wan;
  return [
    '  <g class="node node-wan">',
    `    <rect x="${left}" y="${top}" width="${WAN_W}" height="${WAN_H}" rx="30" fill="#0f1c2e" stroke="#2a3f5c" stroke-width="1.6" stroke-dasharray="7 6" filter="url(#cardShadow)"/>`,
    renderIcon(kind.icon, left + 22, d.y - 17, 34, kind.color, 1.7),
    `    <text x="${left + 70}" y="${n(d.y - 2)}" fill="${P.name}" font-size="18" font-weight="600">${esc(d.name)}</text>`,
    `    <text x="${left + 70}" y="${n(d.y + 21)}" fill="${P.faint}" font-size="12">${esc(d.detail || '')}</text>`,
    '  </g>',
  ].join('\n');
}

function nameSize(name) {
  let size = 18;
  const max = CARD_W - 68 - 44;
  while (size > 13 && est(name, size) * 1.04 > max) size -= 0.5;
  return size;
}

function renderCard(d) {
  const left = d.x - CARD_W / 2;
  const top = d.y - CARD_H / 2;
  const kind = KIND[d.kind];
  const t = tone(d);
  const dot = TONE[t];
  const detail =
    d.detail ||
    (d.status === 'online' && d.latency_ms != null
      ? `${d.latency_ms}ms`
      : d.status === 'offline'
        ? '无响应'
        : '未知');
  const ns = nameSize(d.name);
  const ds = est(detail, 12.5) > CARD_W - 68 - 44 ? 11 : 12.5;
  const textX = left + 68;

  return [
    '  <g class="node">',
    `    <rect x="${left}" y="${top}" width="${CARD_W}" height="${CARD_H}" rx="12" fill="${P.card}" stroke="${P.border}" stroke-width="1.6" filter="url(#cardShadow)"/>`,
    `    <rect x="${left + 1.6}" y="${top + 1.6}" width="${CARD_W - 3.2}" height="${CARD_H - 3.2}" rx="10.6" fill="none" stroke="#ffffff" stroke-opacity="0.035"/>`,
    renderIcon(kind.icon, left + 24, d.y - 15, 30, kind.color),
    `    <text x="${textX}" y="${n(d.y - 12)}" fill="${P.name}" font-size="${ns}" font-weight="600">${esc(d.name)}</text>`,
    `    <text x="${textX}" y="${n(d.y + 10)}" fill="${P.muted}" font-size="13.5" font-family="'Cascadia Mono','Consolas',ui-monospace,monospace">${esc(d.ip)}</text>`,
    renderIcon('activity', textX, d.y + 24, 11, dot, 2.2),
    `    <text x="${textX + 16}" y="${n(d.y + 34)}" fill="${dot}" font-size="${ds}">${esc(detail)}</text>`,
    `    <circle cx="${left + CARD_W - 22}" cy="${top + 22}" r="11" fill="${dot}" opacity="0.16"/>`,
    `    <circle cx="${left + CARD_W - 22}" cy="${top + 22}" r="5.5" fill="${dot}"/>`,
    '  </g>',
  ].join('\n');
}

/* ---------------- 页眉 / 图例 ---------------- */

function renderChip(x, y, color, value, label) {
  return [
    '  <g>',
    `    <rect x="${x}" y="${y}" width="112" height="34" rx="17" fill="#0f1a2b" stroke="#1f2d44"/>`,
    `    <circle cx="${x + 22}" cy="${y + 17}" r="4.5" fill="${color}"/>`,
    `    <text x="${x + 36}" y="${y + 22}" fill="#e2e8f0" font-size="14" font-weight="600">${value}</text>`,
    `    <text x="${x + 54}" y="${y + 22}" fill="${P.faint}" font-size="12.5">${esc(label)}</text>`,
    '  </g>',
  ].join('\n');
}

function renderLegend(items, y) {
  let x = 48;
  const out = [];
  for (const it of items) {
    const color = TONE[it.tone];
    if (it.kind === 'line') {
      out.push(
        `  <path d="M${x} ${y - 4} h38" fill="none" stroke="${color}" stroke-width="2.5" stroke-linecap="round" stroke-dasharray="9 8"/>`
      );
      out.push(
        `  <text x="${x + 52}" y="${y}" fill="${P.muted}" font-size="12.5">${esc(it.label)}</text>`
      );
      x += 52 + est(it.label, 12.5) + 42;
    } else {
      out.push(`  <circle cx="${x + 6}" cy="${y - 4}" r="5.5" fill="${color}"/>`);
      out.push(
        `  <text x="${x + 22}" y="${y}" fill="${P.muted}" font-size="12.5">${esc(it.label)}</text>`
      );
      x += 22 + est(it.label, 12.5) + 42;
    }
  }
  return out.join('\n');
}

/* ---------------- 文档 ---------------- */

const chips = [
  { color: P.online, value: counts.online, label: '在线' },
  { color: P.offline, value: counts.offline, label: '离线' },
  { color: P.faint, value: counts.unknown, label: '未知' },
];
const chipW = 112;
const chipGap = 14;
const chipStart = 1552 - (chips.length * chipW + (chips.length - 1) * chipGap);

const footer = data.meta.footer || {};
const desc = `${data.meta.title}：${counted.length} 台设备，在线 ${counts.online}、离线 ${counts.offline}、未知 ${counts.unknown}。${footer.source || ''}`;

const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-labelledby="title desc" font-family="'Segoe UI','Microsoft YaHei','PingFang SC','Noto Sans SC',sans-serif">
<title id="title">${esc(data.meta.title)}</title>
<desc id="desc">${esc(desc)}</desc>
<style>
  .flow { stroke-dasharray: 9 8; animation: dash 1.8s linear infinite; }
  @keyframes dash { to { stroke-dashoffset: -34; } }
  @media (prefers-reduced-motion: reduce) { .flow { animation: none; } }
</style>
<defs>
  <radialGradient id="bgGlow" cx="50%" cy="38%" r="78%">
    <stop offset="0%" stop-color="#11203a"/>
    <stop offset="62%" stop-color="#0b1220"/>
    <stop offset="100%" stop-color="#050a14"/>
  </radialGradient>
  <pattern id="grid" width="28" height="28" patternUnits="userSpaceOnUse">
    <circle cx="1.6" cy="1.6" r="1.05" fill="#1c2a41"/>
  </pattern>
  <filter id="cardShadow" x="-30%" y="-30%" width="160%" height="170%">
    <feDropShadow dx="0" dy="7" stdDeviation="11" flood-color="#000000" flood-opacity="0.5"/>
  </filter>
  <filter id="edgeGlow" x="-50%" y="-50%" width="200%" height="200%">
    <feGaussianBlur stdDeviation="4"/>
  </filter>
  <marker id="arrowGreen" viewBox="0 0 14 14" refX="12.4" refY="7" markerWidth="14" markerHeight="14" markerUnits="userSpaceOnUse" orient="auto">
    <path d="M1.5 2 L12.5 7 L1.5 12 Z" fill="${P.online}"/>
  </marker>
  <marker id="arrowRed" viewBox="0 0 14 14" refX="12.4" refY="7" markerWidth="14" markerHeight="14" markerUnits="userSpaceOnUse" orient="auto">
    <path d="M1.5 2 L12.5 7 L1.5 12 Z" fill="${P.offline}"/>
  </marker>
  <marker id="arrowGrey" viewBox="0 0 14 14" refX="12.4" refY="7" markerWidth="14" markerHeight="14" markerUnits="userSpaceOnUse" orient="auto">
    <path d="M1.5 2 L12.5 7 L1.5 12 Z" fill="${P.unknown}"/>
  </marker>
</defs>

<!-- 背景 -->
<rect width="${W}" height="${H}" fill="${P.bg}"/>
<rect width="${W}" height="${H}" fill="url(#bgGlow)"/>
<rect width="${W}" height="${H}" fill="url(#grid)" opacity="0.55"/>

<!-- 页眉 -->
<g transform="translate(48 36)">
  <rect width="34" height="34" rx="9" fill="#12213a" stroke="#1f3350"/>
  <g transform="translate(5 5)" fill="none" stroke="${P.blue}" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
    <circle cx="5" cy="6" r="2.2"/>
    <circle cx="17" cy="6" r="2.2"/>
    <circle cx="11" cy="17" r="2.2"/>
    <path d="M6.6 7.6 9.6 14.9M15.4 7.6 12.4 14.9M7.2 6h7.6"/>
  </g>
</g>
<text x="98" y="58" fill="${P.name}" font-size="25" font-weight="600">${esc(data.meta.title)}</text>
<text x="98" y="82" fill="#7e8fa7" font-size="12.5">${esc(data.meta.subtitle)}</text>
${chips.map((c, i) => renderChip(chipStart + i * (chipW + chipGap), 40, c.color, c.value, c.label)).join('\n')}
<path d="M48 112 H1552" stroke="#1c2b42" stroke-width="1"/>

<!-- 链路 -->
${data.links.map(renderLink).join('\n')}

<!-- 节点 -->
${data.devices.map((d) => (isWan(d) ? renderWan(d) : renderCard(d))).join('\n')}

<!-- 页脚 -->
<path d="M48 916 H1552" stroke="#1c2b42" stroke-width="1"/>
${renderLegend(data.meta.legend || [], 946)}
<text x="48" y="978" fill="#4b5b73" font-size="11.5">${esc(footer.note || '')}</text>
<text x="1552" y="946" fill="#55657d" font-size="12" text-anchor="end">${esc(footer.source || '')}</text>
<text x="1552" y="970" fill="#3f4d63" font-size="11" text-anchor="end">${esc(footer.meta || `${W} × ${H}`)}</text>
</svg>
`;

writeFileSync(outputPath, svg, 'utf8');
console.log(`wrote ${outputPath} (${Buffer.byteLength(svg)} bytes)`);
