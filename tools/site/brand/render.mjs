// Regenerates the raster brand images in site/ from site/images/*.svg and
// site/fonts. Needs Playwright (global install is fine) and its Chromium.
// Usage: node tools/site/brand/render.mjs
// Outputs: site/images/og.png (1200x630), site/apple-touch-icon.png (180x180),
// site/favicon.ico (16x16 + 32x32 PNG entries).
import { createRequire } from 'node:module';
import { execSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const site = join(here, '..', '..', '..', 'site');
const require = createRequire(import.meta.url);
let playwright;
try { playwright = require('playwright'); }
catch { playwright = require(join(execSync('npm root -g').toString().trim(), 'playwright')); }

const browser = await playwright.chromium.launch({ executablePath: process.env.CHROME_PATH || undefined });
async function shot(file, query, width, height, omitBackground) {
  const page = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: 1 });
  await page.goto(pathToFileURL(join(here, file)).href + query);
  await page.evaluate(() => document.fonts.ready);
  await page.waitForFunction(() => [...document.images].every((i) => i.complete && i.naturalWidth > 0));
  const png = await page.screenshot({ omitBackground, clip: { x: 0, y: 0, width, height } });
  await page.close();
  return png;
}

writeFileSync(join(site, 'images', 'og.png'), await shot('og.html', '', 1200, 630, false));
writeFileSync(join(site, 'apple-touch-icon.png'), await shot('icon.html', '?size=180&bg=1&pad=26', 180, 180, false));
const icons = [];
for (const size of [16, 32]) icons.push({ size, png: await shot('icon.html', `?size=${size}`, size, size, true) });
await browser.close();

// ICO: 6-byte header (reserved 0, type 1, count), 16-byte directory entries,
// then the PNG payloads (PNG-in-ICO, supported since Windows Vista).
const header = Buffer.alloc(6 + 16 * icons.length);
header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(icons.length, 4);
let offset = header.length;
icons.forEach(({ size, png }, i) => {
  const e = 6 + 16 * i;
  header.writeUInt8(size % 256, e); header.writeUInt8(size % 256, e + 1);
  header.writeUInt8(0, e + 2); header.writeUInt8(0, e + 3);
  header.writeUInt16LE(1, e + 4); header.writeUInt16LE(32, e + 6);
  header.writeUInt32LE(png.length, e + 8); header.writeUInt32LE(offset, e + 12);
  offset += png.length;
});
writeFileSync(join(site, 'favicon.ico'), Buffer.concat([header, ...icons.map((i) => i.png)]));
console.log('wrote site/images/og.png, site/apple-touch-icon.png, site/favicon.ico');
