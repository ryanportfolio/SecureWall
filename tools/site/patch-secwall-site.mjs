#!/usr/bin/env node
// Applies this repository's headline patches to a copy of secwall.org.
//
// The site has no source; its live deployment is the source of truth. This
// script copies a downloaded deployment, patches the prebuilt bundle with
// anchored replacements that must each match exactly once, adds the headline
// scripts and their CSS, and renames every changed /build/* file (served
// immutable). It can run on an already patched deployment: it replaces its own
// earlier additions instead of stacking them.
//
// Scripts: tools/site/lib/swtext.js (the shared SWText library), then every
// tools/site/sections/*.js in alphabetical order, each served as
// /build/sw-<basename>-<hash>.js from a classic <script> placed before the
// entry bundle, so window.__swLayers is watched before the terrain is built.
// CSS: every tools/site/sections/*.css, alphabetical, concatenated into one
// marked block at the end of securewall.css.
// Walkthrough: tools/site/walkthrough (the player the Tour section frames),
// without its README and review page, replaces /walkthrough/. vercel.json gets
// one headers entry for it: frame-ancestors 'self', so only the site itself
// can frame it, and noindex, so the bare player stays out of search results.
//
//   node tools/release/update-secwall-site.mjs --download .tmp/site-live
//   node tools/site/patch-secwall-site.mjs --from .tmp/site-live --out .tmp/site-patched
//
// --out is deleted and rebuilt; a non-empty --out without an index.html is
// refused. Deploying is left to the caller. A preview: vercel deploy .tmp/site-patched

import { createHash } from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const CSS_START = "/* sw-terrain-text start */";
const CSS_END = "/* sw-terrain-text end */";
const SECTIONS = path.join(HERE, "sections");
// Added scripts and their tags, including the older sw-terrain-text-<hash>.js.
const ADDED_FILE = /^build\/sw-[A-Za-z0-9_-]+-[A-Za-z0-9_-]{8}\.js$/;
const ADDED_TAG = /<script src="\/build\/sw-[A-Za-z0-9_-]+-[A-Za-z0-9_-]{8}\.js"><\/script>/g;
const WALKTHROUGH = path.join(HERE, "walkthrough");
const WALKTHROUGH_SKIP = new Set(["README.md", "film.html"]);
const WALKTHROUGH_HEADERS = {
  source: "/walkthrough/(.*)",
  headers: [
    { key: "Content-Security-Policy", value: "frame-ancestors 'self'" },
    { key: "X-Robots-Tag", value: "noindex" },
  ],
};

// Anchored bundle patches: [anchor, replacement, already-applied marker].
const BUNDLE_PATCHES = [
  [
    "this.mesh.name=this.options.name;",
    "this.mesh.name=this.options.name;(window.__swLayers=window.__swLayers||[]).push(this);",
    "window.__swLayers",
  ],
  [
    'this.compositePass.name="Final Composite",',
    'this.compositePass.name="Final Composite",window.__swComposite=this.compositePass,',
    "window.__swComposite",
  ],
  // Hero tagline: two lines, no periods (owner copy decision).
  [
    'children:"A default-deny firewall for Windows. Decide which apps connect."',
    'children:["A default-deny firewall for Windows",j.jsx("br",{}),"Decide which apps connect"]',
    '"Decide which apps connect"]',
  ],
];

class Failure extends Error {}
const fail = message => { throw new Failure(message); };
const arg = name => {
  const i = process.argv.indexOf(name);
  return i > 0 ? process.argv[i + 1] : undefined;
};
const hash8 = bytes => createHash("sha256").update(bytes).digest("base64url").slice(0, 8);
const count = (text, needle) => text.split(needle).length - 1;

function listFiles(dir) {
  return fs.readdirSync(dir, { recursive: true, withFileTypes: true })
    .filter(e => e.isFile())
    .map(e => path.relative(dir, path.join(e.parentPath, e.name)).split(path.sep).join("/"));
}

// Section files with this extension, sorted by name.
function sectionFiles(ext) {
  return fs.readdirSync(SECTIONS, { withFileTypes: true })
    .filter(e => e.isFile() && e.name.endsWith(ext))
    .map(e => e.name)
    .sort();
}

function overlaps(a, b) {
  const rel = path.relative(b, a);
  return rel === "" || (!rel.startsWith("..") && !path.isAbsolute(rel));
}

function patch(from, out) {
  if (overlaps(out, from) || overlaps(from, out)) fail(`--out ${out} must not overlap --from ${from}`);
  if (!fs.existsSync(path.join(from, "index.html"))) fail(`${from} has no index.html`);
  if (fs.existsSync(out) && fs.readdirSync(out).length &&!fs.existsSync(path.join(out, "index.html"))) {
    fail(`--out ${out} is not empty and holds no site; refusing to replace it`);
  }
  fs.rmSync(out, { recursive: true, force: true });
  fs.cpSync(from, out, { recursive: true });
  const read = rel => fs.readFileSync(path.join(out, rel), "utf8");
  const write = (rel, text) => fs.writeFileSync(path.join(out, rel), text);

  let html = read("index.html");
  const bundles = [...html.matchAll(/<script type="module" src="\/(build\/index-[A-Za-z0-9_-]{8}\.js)"><\/script>/g)].map(m => m[1]);
  if (bundles.length !== 1) fail(`expected one entry bundle in index.html, found ${bundles.length}`);
  const bundle = bundles[0];

  // Bundle patches.
  let js = read(bundle);
  const before = js;
  for (const [anchor, replacement, marker] of BUNDLE_PATCHES) {
    if (js.includes(marker)) {
      if (count(js, replacement) !== 1) fail(`${bundle}: "${marker}" present but not as this patch applies it`);
      console.log(`already patched: ${anchor}`);
      continue;
    }
    const n = count(js, anchor);
    if (n !== 1) fail(`${bundle}: anchor "${anchor}" found ${n} times, expected 1`);
    js = js.replace(anchor, replacement);
    console.log(`patched: ${anchor}`);
  }
  let bundleName = path.posix.basename(bundle);
  if (js !== before) {
    const match = bundleName.match(/^(.+)-[A-Za-z0-9_-]{8}\.js$/);
    const renamed = `${match[1]}-${hash8(js)}.js`;
    if (renamed === bundleName) fail(`${bundle}: cannot derive a new hashed name`);
    const importers = listFiles(out).filter(f => f.startsWith("build/") && f !== bundle && read(f).includes(bundleName));
    if (importers.length) fail(`${importers.join(", ")} reference ${bundleName}; only a single entry bundle is supported`);
    fs.rmSync(path.join(out, bundle));
    write(`build/${renamed}`, js);
    html = html.split(bundleName).join(renamed);
    console.log(`renamed ${bundleName} to ${renamed}`);
    bundleName = renamed;
  }

  // Scripts: drop every earlier copy and tag, then add the library and the
  // sections before the bundle, library first.
  for (const old of listFiles(out).filter(f => ADDED_FILE.test(f))) {
    fs.rmSync(path.join(out, old));
    console.log(`removed ${old}`);
  }
  html = html.replace(ADDED_TAG, "");
  const sources = [
    path.join(HERE, "lib", "swtext.js"),
    ...sectionFiles(".js").map(f => path.join(SECTIONS, f)),
  ];
  const tags = [];
  const seen = new Set();
  for (const src of sources) {
    const base = path.basename(src, ".js");
    if (!/^[A-Za-z0-9_-]+$/.test(base)) fail(`${src}: script names may use only letters, digits, "_" and "-"`);
    if (seen.has(base)) fail(`two scripts are named ${base}.js`);
    seen.add(base);
    const bytes = fs.readFileSync(src);
    const name = `sw-${base}-${hash8(bytes)}.js`;
    write(`build/${name}`, bytes);
    tags.push(`<script src="/build/${name}"></script>`);
    console.log(`added build/${name}`);
  }
  const entry = `<script type="module" src="/build/${bundleName}"></script>`;
  if (count(html, entry) !== 1) fail("index.html: entry bundle script tag not found once");
  html = html.replace(entry, `${tags.join("")}${entry}`);
  if ((html.match(ADDED_TAG) ?? []).length !== tags.length) fail("index.html: added script tags not found once each");
  write("index.html", html);

  // CSS lives in the root securewall.css, which is not served immutable.
  const blocks = sectionFiles(".css").map(f => {
    const text = fs.readFileSync(path.join(SECTIONS, f), "utf8").replace(/\r\n/g, "\n").trim();
    if (text.includes(CSS_START) || text.includes(CSS_END)) fail(`${f} contains a block marker`);
    return `/* ${f} */\n${text}`;
  });
  let css = read("securewall.css");
  const start = css.indexOf(CSS_START);
  if (start >= 0) {
    const end = css.indexOf(CSS_END, start);
    if (end < 0) fail("securewall.css: start marker without end marker");
    css = css.slice(0, start).trimEnd() + css.slice(end + CSS_END.length);
  }
  if (css.includes(CSS_START) || css.includes(CSS_END)) fail("securewall.css: more than one marked block");
  write("securewall.css", `${css.trimEnd()}\n\n${[CSS_START, ...blocks, CSS_END].join("\n")}\n`);
  console.log(`updated securewall.css (${blocks.length} section CSS files)`);

  // The walkthrough player, replacing any earlier copy.
  fs.rmSync(path.join(out, "walkthrough"), { recursive: true, force: true });
  const players = listFiles(WALKTHROUGH).filter(f => !WALKTHROUGH_SKIP.has(f));
  if (!players.includes("index.html")) fail(`${WALKTHROUGH} has no index.html`);
  for (const rel of players) {
    fs.mkdirSync(path.dirname(path.join(out, "walkthrough", rel)), { recursive: true });
    fs.copyFileSync(path.join(WALKTHROUGH, rel), path.join(out, "walkthrough", rel));
  }
  console.log(`added walkthrough/ (${players.length} files)`);
  const vercel = JSON.parse(read("vercel.json"));
  if (!Array.isArray(vercel.headers)) fail("vercel.json: no headers list");
  vercel.headers = vercel.headers.filter(h => h.source !== WALKTHROUGH_HEADERS.source);
  vercel.headers.push(WALKTHROUGH_HEADERS);
  write("vercel.json", `${JSON.stringify(vercel, null, 2)}\n`);
  console.log("updated vercel.json (walkthrough headers)");

  for (const src of [...html.matchAll(/src="\/(build\/[^"]+\.js)"/g)].map(m => m[1])) {
    if (!fs.existsSync(path.join(out, src))) fail(`index.html references missing ${src}`);
  }
  console.log(`patched site ready in ${out}`);
}

try {
  const from = arg("--from");
  const out = arg("--out");
  if (!from || !out) fail("usage: --from <downloaded site> --out <dir>");
  patch(path.resolve(from), path.resolve(out));
} catch (err) {
  if (!(err instanceof Failure)) throw err;
  console.error(`error: ${err.message}`);
  process.exitCode = 1;
}
