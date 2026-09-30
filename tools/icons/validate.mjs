// Structural check of every output: ICO directory and entries, BMP headers, PNG IHDR,
// each compared with the sizes jobs.mjs asks for. Exit code 1 on any failure.
//   node validate.mjs [--out <output root>]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { jobs } from './jobs.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const i = process.argv.indexOf('--out');
const outRoot = path.resolve(i > 0 ? process.argv[i + 1] : path.join(here, '..', '..'));
const PNG_SIG = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
const dims = (s) => (Array.isArray(s) ? s : [s, s]);

function png(buf) {
  if (!buf.subarray(0, 8).equals(PNG_SIG)) throw new Error('bad PNG signature');
  if (buf.toString('ascii', 12, 16) !== 'IHDR') throw new Error('IHDR missing');
  return { w: buf.readUInt32BE(16), h: buf.readUInt32BE(20), depth: buf[24], color: buf[25] };
}

function ico(buf) {
  if (buf.readUInt16LE(0) !== 0 || buf.readUInt16LE(2) !== 1) throw new Error('not an icon (reserved/type)');
  const n = buf.readUInt16LE(4);
  const out = [];
  for (let k = 0; k < n; k++) {
    const d = 6 + 16 * k;
    const w = buf[d] || 256;
    const h = buf[d + 1] || 256;
    const bpp = buf.readUInt16LE(d + 6);
    const len = buf.readUInt32LE(d + 8);
    const off = buf.readUInt32LE(d + 12);
    if (off + len > buf.length) throw new Error(`entry ${k} runs past end of file`);
    const img = buf.subarray(off, off + len);
    let kind;
    if (img.subarray(0, 8).equals(PNG_SIG)) {
      const p = png(img);
      if (p.w !== w || p.h !== h) throw new Error(`entry ${k}: PNG ${p.w}x${p.h} vs directory ${w}x${h}`);
      if (p.depth !== 8 || p.color !== 6) throw new Error(`entry ${k}: PNG not 8-bit RGBA`);
      kind = 'PNG';
    } else {
      const hs = img.readUInt32LE(0);
      const dw = img.readInt32LE(4);
      const dh = img.readInt32LE(8);
      const dbpp = img.readUInt16LE(14);
      const comp = img.readUInt32LE(16);
      const expect = 40 + w * h * 4 + Math.ceil(w / 32) * 4 * h;
      if (hs !== 40 || dw !== w || dh !== 2 * h || dbpp !== 32 || comp !== 0 || len !== expect) {
        throw new Error(`entry ${k}: DIB header mismatch (hs ${hs}, ${dw}x${dh}, ${dbpp} bpp, comp ${comp}, len ${len}/${expect})`);
      }
      kind = 'DIB';
    }
    if (bpp !== 32) throw new Error(`entry ${k}: directory bit count ${bpp}`);
    out.push({ w, h, bpp, kind, len });
  }
  return out;
}

function bmp(buf) {
  if (buf.toString('ascii', 0, 2) !== 'BM') throw new Error('bad BMP signature');
  const size = buf.readUInt32LE(2);
  const off = buf.readUInt32LE(10);
  const hs = buf.readUInt32LE(14);
  const w = buf.readInt32LE(18);
  const h = buf.readInt32LE(22);
  const planes = buf.readUInt16LE(26);
  const bpp = buf.readUInt16LE(28);
  const comp = buf.readUInt32LE(30);
  const img = buf.readUInt32LE(34);
  const stride = Math.ceil((w * 3) / 4) * 4;
  if (size !== buf.length) throw new Error(`file size field ${size} vs ${buf.length}`);
  if (off !== 54 || hs !== 40 || planes !== 1 || bpp !== 24 || comp !== 0 || img !== stride * h) {
    throw new Error(`header mismatch (off ${off}, hs ${hs}, planes ${planes}, ${bpp} bpp, comp ${comp}, image ${img})`);
  }
  return { w, h, bpp };
}

let failed = 0;
for (const job of jobs) {
  const file = path.join(outRoot, job.out);
  const want = job.sizes.map(dims);
  try {
    const buf = fs.readFileSync(file);
    let line;
    if (job.kind === 'ico') {
      const e = ico(buf);
      const got = e.map((x) => `${x.w}`).join(',');
      const exp = want.map(([w]) => `${w}`).join(',');
      if (got !== exp || e.some((x) => x.w !== x.h)) throw new Error(`sizes ${got}, expected ${exp}`);
      line = `ICO ${e.length} entries: ${e.map((x) => `${x.w} ${x.bpp}bpp ${x.kind}`).join('; ')}`;
    } else if (job.kind === 'bmp') {
      const b = bmp(buf);
      if (b.w !== want[0][0] || b.h !== want[0][1]) throw new Error(`size ${b.w}x${b.h}`);
      line = `BMP ${b.w}x${b.h} ${b.bpp}bpp bottom-up BI_RGB`;
    } else {
      const p = png(buf);
      if (p.w !== want[0][0] || p.h !== want[0][1]) throw new Error(`size ${p.w}x${p.h}`);
      line = `PNG ${p.w}x${p.h} ${p.depth}-bit color type ${p.color}`;
    }
    console.log(`OK   ${job.out}: ${line}`);
  } catch (e) {
    failed++;
    console.log(`FAIL ${job.out}: ${e.message}`);
  }
}
console.log(failed ? `${failed} file(s) failed` : `all ${jobs.length} files passed`);
process.exit(failed ? 1 : 0);
