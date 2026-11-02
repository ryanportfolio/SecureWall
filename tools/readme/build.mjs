// Builds README.md and its art. Node 20+, no dependencies.
//
//   node tools/readme/build.mjs           write README.md and docs/readme/*.svg
//   node tools/readme/build.mjs --check   fail if any committed output is stale (CI)
//
// Edit tools/readme/body.md for prose, tools/readme/items.json for the
// documentation table, and tools/readme/panels.mjs for the art.
import fs from 'node:fs';
import path from 'node:path';
import { ROOT } from './facts.mjs';
import * as panels from './panels.mjs';
import * as readme from './readme.mjs';

const check = process.argv.includes('--check');

const files = panels.render();
let bad = panels.verify(files);
const md = readme.render();
bad += readme.verify(md, files);
files['README.md'] = md;
if (bad) {
  console.error(`readme: ${bad} check(s) failed`);
  process.exit(1);
}

const norm = s => s.replace(/\r\n/g, '\n');
let stale = 0;
for (const [rel, content] of Object.entries(files)) {
  const abs = path.join(ROOT, rel);
  const current = fs.existsSync(abs) ? norm(fs.readFileSync(abs, 'utf8')) : null;
  if (current === content) continue;
  if (check) {
    console.error(`STALE ${rel}: run node tools/readme/build.mjs and commit the result`);
    stale++;
  } else {
    fs.mkdirSync(path.dirname(abs), { recursive: true });
    fs.writeFileSync(abs, content);
    console.log(`wrote ${rel}`);
  }
}
const extra = fs.existsSync(path.join(ROOT, panels.OUT_DIR))
  ? fs.readdirSync(path.join(ROOT, panels.OUT_DIR)).map(f => `${panels.OUT_DIR}/${f}`).filter(f => !(f in files))
  : [];
for (const f of extra) { console.error(`UNUSED ${f}: not produced by the build; delete it`); stale++; }
if (stale) process.exit(1);
console.log(check ? 'readme: up to date' : 'readme: done');
