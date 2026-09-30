// Rasterize the SVG masters with installed headless Chrome and write PNG/ICO/BMP outputs.
//
//   node render.mjs [--svg <svg dir>] [--out <output root>] [--manifest <file>] [--chrome <chrome.exe>]
//
// Defaults: SVG masters from <repo>/TinyWall/Resources/img/svg, outputs written over the
// product files in <repo> (paths from jobs.mjs). Pass --out to render into a staging folder
// instead. Each size is rasterized from the vector at that exact pixel size (SVG
// width/height rewritten), never downscaled. --manifest writes a Markdown table of outputs.
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { jobs } from './jobs.mjs';
import { buildIco, buildBmp24 } from './pack.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const arg = (name, def) => {
  const i = process.argv.indexOf(name);
  return i > 0 ? process.argv[i + 1] : def;
};
const repo = path.resolve(here, '..', '..');
const outRoot = path.resolve(arg('--out', repo));
const manifestPath = arg('--manifest', null);
const svgDir = path.resolve(arg('--svg', path.join(repo, 'TinyWall/Resources/img/svg')));
const chrome = arg('--chrome', 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe');

const dims = (s) => (Array.isArray(s) ? s : [s, s]);

// One render request per (svg, size) pair.
const requests = new Map();
for (const job of jobs) {
  for (const s of job.sizes) {
    const [w, h] = dims(s);
    const key = `${job.src}@${w}x${h}`;
    if (!requests.has(key)) {
      const text = fs.readFileSync(path.join(svgDir, job.src), 'utf8');
      const sized = text.replace(/<svg\b[^>]*>/, (tag) =>
        tag.replace(/\swidth="[^"]*"/, ` width="${w}"`).replace(/\sheight="[^"]*"/, ` height="${h}"`));
      requests.set(key, { key, w, h, svg: sized });
    }
  }
}

const page = `<!doctype html><meta charset="utf-8"><body><script>
const reqs = ${JSON.stringify([...requests.values()])};
function b64(bytes) {
  let s = '';
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  return btoa(s);
}
(async () => {
  const out = {};
  for (const r of reqs) {
    const img = new Image();
    img.src = 'data:image/svg+xml;base64,' + btoa(unescape(encodeURIComponent(r.svg)));
    await img.decode();
    const c = document.createElement('canvas');
    c.width = r.w; c.height = r.h;
    const g = c.getContext('2d', { colorSpace: 'srgb', willReadFrequently: true });
    g.drawImage(img, 0, 0, r.w, r.h);
    out[r.key] = {
      png: c.toDataURL('image/png').split(',')[1],
      rgba: b64(new Uint8Array(g.getImageData(0, 0, r.w, r.h).data.buffer)),
    };
  }
  const pre = document.createElement('pre');
  pre.id = 'result';
  pre.textContent = '@@' + 'RESULT' + JSON.stringify(out) + '@@' + 'DONE';
  document.body.appendChild(pre);
})().catch((e) => { document.body.textContent = '@@' + 'RESULT' + 'ERROR ' + e + '@@' + 'DONE'; });
</script>`;

const work = fs.mkdtempSync(path.join(os.tmpdir(), 'sw-icons-'));
const htmlPath = path.join(work, 'render.html');
fs.writeFileSync(htmlPath, page);
const dom = execFileSync(chrome, [
  '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
  '--window-position=-2400,-2400', '--force-device-scale-factor=1',
  `--user-data-dir=${path.join(work, 'profile')}`,
  '--virtual-time-budget=60000', '--dump-dom', pathToFileURL(htmlPath).href,
], { maxBuffer: 1 << 30, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
fs.rmSync(work, { recursive: true, force: true, maxRetries: 5, retryDelay: 500 });

const m = /@@RESULT([\s\S]*)@@DONE/.exec(dom);
if (!m) throw new Error('Chrome returned no render result');
if (m[1].startsWith('ERROR')) throw new Error('render page failed: ' + m[1]);
const rendered = JSON.parse(m[1].replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>'));

const manifest = [];
for (const job of jobs) {
  const entries = job.sizes.map((s) => {
    const [w, h] = dims(s);
    const r = rendered[`${job.src}@${w}x${h}`];
    return { w, h, png: Buffer.from(r.png, 'base64'), rgba: Buffer.from(r.rgba, 'base64') };
  });
  let data;
  let format;
  if (job.kind === 'ico') {
    data = buildIco(entries);
    format = 'ICO, 32-bit BGRA DIB entries' + (entries.some((e) => e.w >= 256) ? ', 256 px entry PNG-compressed' : '');
  } else if (job.kind === 'bmp') {
    data = buildBmp24(entries[0].w, entries[0].h, entries[0].rgba);
    format = 'BMP, 24-bit, bottom-up, uncompressed';
  } else {
    data = entries[0].png;
    format = 'PNG, 32-bit RGBA';
  }
  const dest = path.join(outRoot, job.out);
  fs.mkdirSync(path.dirname(dest), { recursive: true });
  fs.writeFileSync(dest, data);
  const sizes = entries.map((e) => (e.w === e.h ? `${e.w}` : `${e.w}x${e.h}`)).join(', ');
  manifest.push(`| \`${job.out}\` | \`${job.src}\` | ${sizes} | ${format} | ${data.length} |`);
  console.log(`${job.out}  [${sizes}]  ${data.length} bytes`);
}

if (manifestPath) fs.writeFileSync(path.resolve(manifestPath), [
  '# SecureWall icon outputs',
  '',
  'Generated by `tools/icons/render.mjs` from the SVG masters in `TinyWall/Resources/img/svg/`.',
  'Paths are relative to the output root and mirror each file\'s location in the repo.',
  'Every size is rasterized from the vector at that pixel size with headless Chrome (device scale 1).',
  '',
  '| Output | Source SVG | Pixel sizes | Format | Bytes |',
  '|---|---|---|---|---|',
  ...manifest,
  '',
].join('\n'));
console.log(`${jobs.length} files written under ${outRoot}`);
