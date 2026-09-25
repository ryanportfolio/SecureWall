#!/usr/bin/env node
// Conservative pruner for the prebuilt Tailwind stylesheet in site/build/ (no dependencies).
//
// The React source for secwall.org is not in this repo, so the stylesheet cannot be rebuilt;
// this script removes rules from the prebuilt file instead. It never rewrites a kept rule:
// kept text is copied byte-for-byte from the input, in source order.
//
// How site/build/index-*.css was produced (2026-09-25, from the imported production file
// build/index-CWwH66Uu.css, 717,851 B, 13,442 style rules):
//
//   git show 6b0eacb:site/build/index-CWwH66Uu.css > original.css
//   node tools/site/prune-css.mjs --css original.css --out <new.css> \
//     --ref site/build/index-DJa5U_1D.js --ref site/index.html --ref site/securewall.css \
//     --drop-woff --drop-face LazareGrotesk-Regular-RnosNKiO.woff2
//
//   Then name the output build/index-<first 8 chars of base64url sha256>.css and update the
//   <link rel="stylesheet"> in site/index.html. Result: 440 style rules, 1 of 2 @keyframes,
//   3 @font-face (woff2 only).
//
// 1. Token safelist (decides the output). A style rule is kept when ANY of these hold:
//    - its selector has no class token (element, :root, *, attribute or id selectors), or one
//      part of its selector list has none;
//    - one of its class tokens (Tailwind escapes undone, e.g. `max-w-500@sm`) occurs as a
//      substring of any --ref file (the JS bundle, index.html, securewall.css);
//    - a class token starts with a dynamic prefix the --ref text builds classes from
//      (`foo--${x}` or "foo-"+x, e.g. split-border--, pin-spacer-);
//    - (optional) Chrome coverage marked it used, or its class was seen on a live element (--cov).
//    @font-face, @property, @charset, @import and @layer statements are kept. @keyframes are
//    kept when a kept rule's animation/animation-name, or the --ref text, names them.
//    @media/@supports/... blocks keep their surviving children and are dropped when empty.
//
// 2. Coverage walk (cross-check, not required to reproduce). Headed Chromium (xvfb, SwiftShader
//    WebGL) walked the original site through ~90 states per configuration (load, scroll steps
//    through every section, hover and focus on every link and button, in-page nav clicks, the
//    mobile menu where shown, and the in-app not-found route) at 1440/1024 desktop and
//    1024/820/390 touch, with and without reduced motion (9 configs),
//    recording CSS rule usage, class names on live elements, font-face status and requests.
//    Every rule coverage marked used (213) is also kept by the token safelist: running this
//    script with or without the coverage files gives byte-identical output.
//
// 3. Proof before shipping (evidence lived in the audit scratch dir, not in the repo):
//    - In-page swap at every walkthrough state (1,097 states, 10 configs incl. 1920 px): every
//      computed property of every element, ::before and ::after, original vs pruned sheet,
//      transitions/animations frozen -> 0 differences caused by the prune.
//    - Every removed rule whose @media/@supports condition was active matched 0 live elements
//      (552 states at 390, 820, 1024, 1440, 1920 and 2048 px).
//    - --drop-woff: the .woff fallbacks were never requested in any state (every browser that
//      runs this bundle supports woff2). --drop-face: the LazareGrotesk weight-400 face stayed
//      `unloaded` in every state, and every LazareGrotesk class in the bundle pairs with weight 300.
//
// Re-run and re-verify whenever the bundle changes; a new class in the JS is protected
// automatically, but a class assembled from pieces the prefix scan misses is not.
//
// usage: node prune-css.mjs --css in.css --out out.css --ref a.js [--ref ...]
//          [--cov coverage.json ...] [--policy conservative|coverage-only]
//          [--drop-woff] [--drop-face <font file name> ...] [--report report.json]
import { readFileSync, writeFileSync } from 'node:fs';
import { basename } from 'node:path';
import { gzipSync, brotliCompressSync, constants } from 'node:zlib';

const args = process.argv.slice(2);
const opt = { ref: [], cov: [], dropFace: [], policy: 'conservative', dropWoff: false };
for (let i = 0; i < args.length; i++) {
  const a = args[i];
  if (a === '--css') opt.css = args[++i];
  else if (a === '--out') opt.out = args[++i];
  else if (a === '--ref') opt.ref.push(args[++i]);
  else if (a === '--cov') opt.cov.push(args[++i]);
  else if (a === '--policy') opt.policy = args[++i];
  else if (a === '--drop-woff') opt.dropWoff = true;
  else if (a === '--drop-face') opt.dropFace.push(args[++i]);
  else if (a === '--report') opt.report = args[++i];
  else if (a === '--sheet-suffix') opt.suffix = args[++i];
  else throw new Error('unknown arg ' + a);
}
if (!opt.css) throw new Error('--css is required');
const src = readFileSync(opt.css, 'utf8');
// coverage JSON keys are sheet URLs; match them by the input file name
const suffix = opt.suffix ?? basename(opt.css);
// ---------- tokenizer / parser ----------
// skip a string or comment starting at i; returns index after it, or -1 if none starts here
function skipAtom(s, i) {
  const c = s[i];
  if (c === '"' || c === "'") {
    let j = i + 1;
    while (j < s.length && s[j] !== c) { if (s[j] === '\\') j++; j++; }
    return j + 1;
  }
  if (c === '/' && s[i + 1] === '*') { const j = s.indexOf('*/', i + 2); return j < 0 ? s.length : j + 2; }
  return -1;
}
// scan from i to the first top-level char in `stops` (outside strings/comments/parens/brackets)
function scanTo(s, i, stops) {
  let depth = 0;
  while (i < s.length) {
    const k = skipAtom(s, i); if (k >= 0) { i = k; continue; }
    const c = s[i];
    if (c === '\\') { i += 2; continue; }
    if (c === '(' || c === '[') depth++;
    else if (c === ')' || c === ']') depth--;
    else if (depth === 0 && stops.includes(c)) return i;
    i++;
  }
  return i;
}
// index of the '}' matching the '{' at i
function matchBrace(s, i) {
  let depth = 0;
  while (i < s.length) {
    const k = skipAtom(s, i); if (k >= 0) { i = k; continue; }
    const c = s[i];
    if (c === '\\') { i += 2; continue; }
    if (c === '{') depth++;
    else if (c === '}') { depth--; if (depth === 0) return i; }
    i++;
  }
  throw new Error('unbalanced braces at ' + i);
}
const GROUP = new Set(['media', 'supports', 'layer', 'container', 'document', 'scope', 'starting-style']);
function parse(s, i, end, cond = []) {
  const nodes = [];
  while (i < end) {
    const k = skipAtom(s, i);
    if (k >= 0 && s[i] === '/') { i = k; continue; } // comment
    if (/\s/.test(s[i])) { i++; continue; }
    if (s[i] === '}') throw new Error('stray } at ' + i);
    const start = i;
    if (s[i] === '@') {
      const name = /^@([\w-]+)/.exec(s.slice(i, i + 64))[1].toLowerCase();
      const p = scanTo(s, i, '{;');
      if (s[p] === ';' || p >= end) { nodes.push({ type: 'at-stmt', name, start, end: p + 1 }); i = p + 1; continue; }
      const close = matchBrace(s, p);
      const node = { type: 'at', name, start, preludeEnd: p, end: close + 1 };
      if (GROUP.has(name)) node.children = parse(s, p + 1, close, [...cond, s.slice(i, p).trim()]);
      nodes.push(node); i = close + 1; continue;
    }
    const p = scanTo(s, i, '{');
    const close = matchBrace(s, p);
    nodes.push({ type: 'rule', start, preludeEnd: p, end: close + 1, selector: s.slice(start, p), cond });
    i = close + 1;
  }
  return nodes;
}
const tree = parse(src, 0, src.length);
const allRules = [];
(function collect(ns) { for (const n of ns) { if (n.type === 'rule') allRules.push(n); if (n.children) collect(n.children); } })(tree);

// ---------- class tokens ----------
function unescapeIdent(raw) {
  return raw.replace(/\\([0-9a-fA-F]{1,6}\s?|[\s\S])/g, (_, e) => /^[0-9a-fA-F]/.test(e) && /^[0-9a-fA-F]{1,6}\s?$/.test(e) ? String.fromCodePoint(parseInt(e, 16)) : e);
}
function classTokens(sel) {
  const out = []; let i = 0; let bracket = 0;
  while (i < sel.length) {
    const k = skipAtom(sel, i); if (k >= 0) { i = k; continue; }
    const c = sel[i];
    if (c === '\\') { i += 2; continue; }
    if (c === '[') { bracket++; i++; continue; }
    if (c === ']') { bracket--; i++; continue; }
    if (c === '.' && bracket === 0) {
      let j = i + 1;
      while (j < sel.length) {
        if (sel[j] === '\\') { const m = /^\\([0-9a-fA-F]{1,6}\s?|[\s\S])/.exec(sel.slice(j, j + 8)); j += m[0].length; continue; }
        if (/[A-Za-z0-9_-]/.test(sel[j]) || sel.charCodeAt(j) > 127) { j++; continue; }
        break;
      }
      if (j > i + 1) out.push(unescapeIdent(sel.slice(i + 1, j)));
      i = j; continue;
    }
    i++;
  }
  return out;
}
const refText = opt.ref.map(f => readFileSync(f, 'utf8')).join('\n\u0000\n');
// dynamic class prefixes from the JS: `foo--${x}` and "foo-"+x
const dynPrefixes = new Set();
for (const m of refText.matchAll(/([A-Za-z][A-Za-z0-9_-]{2,})\$\{/g)) if (/[-_]$/.test(m[1])) dynPrefixes.add(m[1]);
for (const m of refText.matchAll(/["']([A-Za-z][A-Za-z0-9_-]{2,}[-_])["']\s*\+/g)) dynPrefixes.add(m[1]);

// ---------- coverage ----------
const usedStarts = new Set(); const observed = new Set(); let covFiles = 0;
for (const f of opt.cov) {
  const j = JSON.parse(readFileSync(f, 'utf8')); covFiles++;
  for (const [url, list] of Object.entries(j.used)) if (url.endsWith(suffix)) for (const r of list) usedStarts.add(Number(r.split(':')[0]));
  for (const c of j.classes || []) observed.add(c);
}
// Chrome reports the offset where the rule's selector text starts.
const ruleByStart = new Map(allRules.map(r => [r.start, r]));
let unmatched = 0; for (const s of usedStarts) if (!ruleByStart.has(s)) unmatched++;

const protectedBy = tok => {
  if (refText.includes(tok)) return 'ref';
  for (const p of dynPrefixes) if (tok.startsWith(p)) return 'dyn:' + p;
  if (observed.has(tok)) return 'observed';
  return null;
};
const stats = { rulesBefore: allRules.length, usedByCoverage: 0, keptNoClassToken: 0, keptByToken: 0, removed: 0, unmatchedCoverageOffsets: unmatched, covFiles, dynPrefixes: [...dynPrefixes] };
const removedList = [];
function keepRule(r) {
  if (usedStarts.has(r.start)) { stats.usedByCoverage++; return true; }
  const toks = classTokens(r.selector);
  if (opt.policy === 'coverage-only') { removedList.push({ sel: r.selector, cond: r.cond }); stats.removed++; return false; }
  // a selector list where any part has no class token (element/attr/id/:root/*) is kept
  const parts = r.selector.split(/,(?![^(]*\))/);
  if (!toks.length || parts.some(p => classTokens(p).length === 0)) { stats.keptNoClassToken++; return true; }
  for (const t of toks) if (protectedBy(t)) { stats.keptByToken++; return true; }
  removedList.push({ sel: r.selector, cond: r.cond }); stats.removed++; return false;
}

// ---------- emit ----------
function emit(nodes) {
  let out = '';
  for (const n of nodes) {
    if (n.type === 'rule') { if (keepRule(n)) out += src.slice(n.start, n.end); continue; }
    if (n.children) { const inner = emit(n.children); if (inner) out += src.slice(n.start, n.preludeEnd + 1) + inner + '}'; continue; }
    if (n.type === 'at' && n.name === 'keyframes') { out += '\u0001KF' + n.start + '\u0001'; continue; } // decided after rules
    let t = src.slice(n.start, n.end);
    if (n.type === 'at' && n.name === 'font-face' && opt.dropFace.some(f => t.includes(f))) continue;
    if (n.type === 'at' && n.name === 'font-face' && opt.dropWoff) t = t.replace(/,\s*url\([^)]*\.woff\)\s*format\(["']?woff["']?\)/g, '');
    out += t;
  }
  return out;
}
let out = emit(tree);
const keptRulesText = out;
const kf = []; (function collect(ns) { for (const n of ns) { if (n.type === 'at' && n.name === 'keyframes') kf.push(n); if (n.children) collect(n.children); } })(tree);
stats.keyframes = {};
for (const n of kf) {
  const name = /@keyframes\s+([^\s{]+)/.exec(src.slice(n.start, n.preludeEnd + 1))[1];
  // names referenced by animation / animation-name declarations of kept rules
  const refd = new Set();
  for (const m of keptRulesText.matchAll(/animation(?:-name)?\s*:([^;}]*)/g)) for (const t of m[1].split(/[\s,]+/)) refd.add(t.replace(/!important$/, ''));
  const used = refd.has(name) || refText.includes(name);
  stats.keyframes[name] = used ? 'kept' : 'removed';
  out = out.replace('\u0001KF' + n.start + '\u0001', used ? src.slice(n.start, n.end) : '');
}
// count rules after
const after = []; (function collect(ns) { for (const n of ns) { if (n.type === 'rule') after.push(n); if (n.children) collect(n.children); } })(parse(out, 0, out.length));
stats.rulesAfter = after.length;
const size = t => { const b = Buffer.from(t); return { raw: b.length, gzip: gzipSync(b, { level: 9 }).length, brotli: brotliCompressSync(b, { params: { [constants.BROTLI_PARAM_QUALITY]: 11 } }).length }; };
stats.before = size(src); stats.after = size(out);
const groups = t => (t.match(/@(media|supports)/g) || []).length;
stats.groupRulesBefore = groups(src); stats.groupRulesAfter = groups(out);
if (opt.out) writeFileSync(opt.out, out);
if (opt.report) writeFileSync(opt.report, JSON.stringify({ policy: opt.policy, stats, removed: removedList }, null, 1));
console.log(JSON.stringify({ policy: opt.policy, ...stats }, null, 1));
