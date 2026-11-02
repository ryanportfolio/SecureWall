// Hero panel for README.md: four SVG variants (wide/narrow x light/dark).
//
// Constraint contract. Read before editing the art.
// - Conceit: the panel is the corner of a Windows screen. A connection lands in
//   Network Activity as blocked by default deny, SecureWall's real popup rises in
//   the bottom-right corner, the cursor clicks Allow outgoing, and the app's next
//   connection is listed as allowed. Exactly the product's loop, nothing invented.
// - Every word inside the popup and every status string comes from the C# source
//   through facts.mjs. Popup geometry and colours are the WinForms designer's,
//   scaled; the popup stays light in both themes because the real window is.
// - Example data uses documentation address ranges (RFC 5737) and a generic
//   updater.exe. It is illustration, and the alt text says so.
// - Survives 390px: the narrow variant is its own composition (two columns,
//   popup below the list), not the wide one scaled down.
// - Survives both themes: the background is transparent; ink, rules and status
//   colours swap per theme. Only the popup keeps fixed colours.
// - Survives no scripts, no hover, no external requests: CSS keyframes only, a
//   10 s loop, system fonts. The authored (unanimated) state is the decision
//   moment: blocked row, popup up, cursor on Allow outgoing. Reduced motion
//   restores exactly that state.
// - The stacked rainbow layers under "connects" echo the stacked-slice headlines
//   on secwall.org. They are the only decoration; add nothing beside them.
import { FACTS } from './facts.mjs';

export const OUT_DIR = 'docs/readme';

const THEMES = {
  light: { ink: '#1f2328', mute: '#59636e', rule: '#d1d9e0', blocked: '#cf222e', allowed: '#1a7f37', shadow: 0.18 },
  dark: { ink: '#f0f6fc', mute: '#9198a1', rule: '#3d444d', blocked: '#ff7b72', allowed: '#3fb950', shadow: 0.55 },
};
const SLICES = ['#4aa8ff', '#3ddc97', '#ffb347', '#ff5c7a'];
const MONO = "ui-monospace,'Cascadia Mono','SF Mono',Menlo,Consolas,'Liberation Mono',monospace";
const SANS = "'Segoe UI',-apple-system,BlinkMacSystemFont,'Noto Sans',Helvetica,Arial,sans-serif";

const APP = { name: 'updater.exe', path: 'C:\\Program Files\\Updater\\updater.exe', remote: '203.0.113.20:443', proto: 'TCP' };
const ROWS = [
  { app: 'msedge.exe', remote: '198.51.100.14:443' },
  { app: 'svchost.exe', remote: '192.0.2.53:53' },
];

export const ALT =
  `Animated illustration: SecureWall lists ${APP.name} as "${FACTS.status.defaultDeny}" in Network Activity, ` +
  `shows its "${FACTS.popup.title}" popup in the bottom-right corner, the cursor clicks ` +
  `"${FACTS.popup.allow.label}", and the app's next connection is listed as "${FACTS.status.allowed}".`;

const esc = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

function wrap(text, maxChars) {
  const lines = [];
  let line = '';
  for (const word of text.split(' ')) {
    if (line && (line + ' ' + word).length > maxChars) { lines.push(line); line = word; }
    else line = line ? line + ' ' + word : word;
  }
  if (line) lines.push(line);
  return lines;
}

// The popup at its native 460x270 designer size; callers scale it.
function popup() {
  const p = FACTS.popup;
  const pt = n => (n * 96) / 72; // designer fonts are in points
  const noticeSize = pt(8.5);
  const noticeLines = wrap(p.notice.text, Math.floor(p.notice.w / (noticeSize * 0.52)));
  if (noticeLines.length > 3) throw new Error('panels: popup notice wraps past three lines');
  const buttons = p.buttons.map(b => `
    <rect x="${b.x}" y="${b.y}" width="${b.w}" height="${b.h}" fill="${b.fill}"/>
    ${b.id === 'allowButton' ? `<rect class="press" x="${b.x}" y="${b.y}" width="${b.w}" height="${b.h}" fill="#000" opacity="0"/>` : ''}
    <text x="${b.x + b.w / 2}" y="${b.y + b.h / 2 + 4}" fill="${b.ink}" font-family="${SANS}" font-size="${pt(9)}" font-weight="600" text-anchor="middle">${esc(b.label)}</text>`).join('');
  return `
    <rect x="0" y="0" width="${p.w}" height="${p.h}" fill="#FFFFFF" stroke="#6B7280" stroke-width="1"/>
    <rect x="${p.titleBar.x}" y="${p.titleBar.y}" width="${p.titleBar.w}" height="${p.titleBar.h}" fill="${p.titleBar.fill}"/>
    <text x="14" y="${p.titleBar.y + 27}" fill="#FFFFFF" font-family="${SANS}" font-size="${pt(10)}" font-weight="600">${esc(p.title)}</text>
    <path d="M${p.close.x + 14} ${p.close.y + 12}l10 10m0 -10l-10 10" stroke="#FFFFFF" stroke-width="1.6" stroke-linecap="round"/>
    <text x="${p.identity.x}" y="${p.identity.y + 18}" fill="#111827" font-family="${SANS}" font-size="${pt(11)}" font-weight="600">${esc(APP.name)}</text>
    <text x="${p.path.x}" y="${p.path.y + 13}" fill="${p.path.ink}" font-family="${SANS}" font-size="${pt(8.5)}">${esc(APP.path)}</text>
    <text x="${p.destination.x}" y="${p.destination.y + 15}" fill="#111827" font-family="${SANS}" font-size="${pt(9)}" xml:space="preserve">${esc(`${p.destinationPrefix}${APP.proto}  ${APP.remote}`)}</text>
    ${noticeLines.map((l, i) => `<text x="${p.notice.x}" y="${p.notice.y + 13 + i * 15}" fill="${p.notice.ink}" font-family="${SANS}" font-size="${noticeSize}">${esc(l)}</text>`).join('\n    ')}
    ${buttons}`;
}

const CURSOR = 'M0 0L0 17L4.6 12.6L7.6 19.4L10.4 18.2L7.5 11.6L13.6 11.6Z';

function css(t, cursorFrom) {
  return `
  .ink{fill:${t.ink}} .mute{fill:${t.mute}} .blk{fill:${t.blocked}} .alw{fill:${t.allowed}}
  .rb{animation:rb 10s linear infinite}
  .ra{opacity:0;animation:ra 10s linear infinite}
  .pop{animation:pop 10s cubic-bezier(.2,.7,.2,1) infinite}
  .cur{animation:cur 10s ease-in-out infinite}
  .press{animation:press 10s linear infinite}
  @keyframes rb{0%,4%{opacity:0}8%,92%{opacity:1}97%,100%{opacity:0}}
  @keyframes ra{0%,56%{opacity:0}60%,92%{opacity:1}97%,100%{opacity:0}}
  @keyframes pop{0%,10%{opacity:0;transform:translateY(28px)}17%,46%{opacity:1;transform:translateY(0)}52%,100%{opacity:0;transform:translateY(28px)}}
  @keyframes cur{0%,18%{opacity:0;transform:translate(${cursorFrom[0]}px,${cursorFrom[1]}px)}22%{opacity:1}36%,46%{opacity:1;transform:translate(0,0)}52%,100%{opacity:0;transform:translate(0,0)}}
  @keyframes press{0%,39%{opacity:0}40%,45%{opacity:.28}46%,100%{opacity:0}}
  @media (prefers-reduced-motion:reduce){
    .rb,.ra,.pop,.cur,.press{animation:none!important}
    .rb,.pop,.cur{opacity:1;transform:none}
    .ra,.press{opacity:0}
  }`;
}

function headline(x, y1, y2, size) {
  const slices = SLICES.map((c, i) =>
    `<text x="${x}" y="${y2 + (SLICES.length - i) * size * 0.035}" fill="${c}" opacity="0.9">connects</text>`).join('');
  return `<g font-family="${MONO}" font-size="${size}" font-weight="700">
  <text class="ink" x="${x}" y="${y1}">Choose what</text>
  ${slices}
  <text class="ink" x="${x}" y="${y2}">connects</text>
</g>`;
}

// cols: [{key, x}] where key is 'app' | 'remote' | 'status'
function table(t, { x, y, cols, width }) {
  const rowH = 24;
  const statusOf = r => r.status ?? FACTS.status.allowed;
  const rows = [
    ...ROWS.map(r => ({ ...r, cls: 'alw' })),
    { app: APP.name, remote: APP.remote, status: FACTS.status.defaultDeny, cls: 'blk', g: 'rb' },
    { app: APP.name, remote: APP.remote, status: FACTS.status.allowed, cls: 'alw', g: 'ra' },
  ];
  const head = cols.map(c => `<text class="mute" x="${c.x}" y="${y + 22}">${c.key.toUpperCase()}</text>`).join('');
  const body = rows.map((r, i) => {
    const ry = y + 50 + i * rowH;
    const cells = cols.map(c => {
      const v = c.key === 'status' ? statusOf(r) : r[c.key];
      return `<text class="${c.key === 'status' ? r.cls : 'ink'}" x="${c.x}" y="${ry}">${esc(v)}</text>`;
    }).join('');
    const rule = `<line x1="${x}" x2="${x + width}" y1="${ry + 8}" y2="${ry + 8}" stroke="${t.rule}"/>`;
    return r.g ? `<g class="${r.g}">${cells}${rule}</g>` : cells + rule;
  }).join('\n  ');
  return `<g font-family="${MONO}" font-size="12">
  <text class="mute" x="${x}" y="${y}" font-size="11" letter-spacing="1.5">NETWORK ACTIVITY</text>
  ${head}
  <line x1="${x}" x2="${x + width}" y1="${y + 30}" y2="${y + 30}" stroke="${t.rule}"/>
  ${body}
</g>`;
}

function hero(t, narrow) {
  const W = narrow ? 400 : 880;
  const H = narrow ? 492 : 370; // narrow: the popup rises over the row the Allowed line later fills
  const p = FACTS.popup;
  const scale = narrow ? 0.78 : 0.8;
  const pw = p.w * scale;
  const ph = p.h * scale;
  const px = narrow ? Math.round((W - pw) / 2) : W - 20 - pw;
  const py = H - 18 - ph;
  const a = p.allow;
  const tip = [px + (a.x + a.w * 0.55) * scale, py + (a.y + a.h * 0.6) * scale];
  const cursorFrom = narrow ? [60, 70] : [150, 90];

  // No subline: the paragraph under the picture says what SecureWall is.
  const head = narrow ? headline(20, 58, 104, 40) : headline(36, 92, 150, 54);
  const list = narrow
    ? table(t, { x: 20, y: 150, width: 360, cols: [{ key: 'app', x: 20 }, { key: 'status', x: 160 }] })
    : table(t, { x: 36, y: 204, width: 430, cols: [{ key: 'app', x: 36 }, { key: 'remote', x: 156 }, { key: 'status', x: 300 }] });

  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${W} ${H}" width="${W}" height="${H}" role="img" aria-label="${esc(ALT)}">
<style>${css(t, cursorFrom)}
</style>
<defs><filter id="sh" x="-20%" y="-20%" width="140%" height="150%"><feGaussianBlur stdDeviation="10"/></filter></defs>
${head}

${list}
<g class="pop">
  <rect x="${px + 4}" y="${py + 10}" width="${pw}" height="${ph}" fill="#000" opacity="${t.shadow}" filter="url(#sh)"/>
  <g transform="translate(${px} ${py}) scale(${scale})">${popup()}
  </g>
</g>
<g class="cur"><path transform="translate(${tip[0].toFixed(1)} ${tip[1].toFixed(1)})" d="${CURSOR}" fill="#FFFFFF" stroke="#111827" stroke-width="1.2" stroke-linejoin="round"/></g>
</svg>
`;
}

export const PANELS = { hero };

export function render() {
  const files = {};
  for (const [name, build] of Object.entries(PANELS)) {
    for (const theme of Object.keys(THEMES)) {
      files[`${OUT_DIR}/${name}-${theme}.svg`] = build(THEMES[theme], false);
      files[`${OUT_DIR}/${name}-narrow-${theme}.svg`] = build(THEMES[theme], true);
    }
  }
  return files;
}

// Every variant must carry the words it claims to show, escaped as SVG text.
export function verify(files) {
  const need = [
    FACTS.popup.title, FACTS.status.defaultDeny, FACTS.status.allowed,
    ...FACTS.popup.buttons.map(b => b.label), APP.name,
  ];
  let bad = 0;
  for (const [file, svg] of Object.entries(files)) {
    for (const s of need) {
      if (!svg.includes(`>${esc(s)}<`)) { console.error(`FAIL ${file}: missing "${s}"`); bad++; }
    }
    for (const word of wrap(FACTS.popup.notice.text, 1000)[0].split(' ')) {
      if (!svg.includes(esc(word))) { console.error(`FAIL ${file}: notice word "${word}" missing`); bad++; }
    }
  }
  return bad;
}
