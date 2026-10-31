#!/usr/bin/env node
// Applies this repository's headline patches to a copy of secwall.org.
//
// The site has no source; its live deployment is the source of truth. This
// script copies a downloaded deployment, patches the prebuilt bundle with
// anchored replacements that must each match exactly once, adds the
// terrain-text script and its CSS, and renames every changed /build/* file
// (served immutable). It can run on an already patched deployment: it replaces
// its own earlier additions instead of stacking them.
//
//   node tools/release/update-secwall-site.mjs --download .tmp/site-live
//   node tools/site/patch-secwall-site.mjs --from .tmp/site-live --out .tmp/site-patched
//
// Deploying is left to the caller. A preview: vercel deploy .tmp/site-patched

import { createHash } from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const CSS_START = "/* sw-terrain-text start */";
const CSS_END = "/* sw-terrain-text end */";

// Hides the DOM headline once the terrain draws it; the text stays in the
// accessibility tree and selectable. The dark overlay that kept the DOM text
// readable would dim the terrain letters, so only its bottom band stays, for
// the tagline. Also moves the tagline below the headline so it no longer sits
// under "connects".
const CSS = `${CSS_START}
.sw-terrain-text .js-hero h1{color:transparent!important;transition:color .8s ease}
.sw-terrain-text .js-hero{background:linear-gradient(0deg,rgba(9,13,20,.6),transparent 32%)!important}
@media(min-width:1024px){
.js-hero .grid>div:first-child{width:100%!important}
.js-hero .grid>div:nth-child(2){width:100%!important;margin-left:0!important;margin-top:28px}
.js-hero p.t-lazare{max-width:560px!important}
}
${CSS_END}`;

// Anchored bundle patches: [anchor, replacement, already-applied marker].
const BUNDLE_PATCHES = [
  [
    "this.mesh.name=this.options.name;",
    "this.mesh.name=this.options.name;(window.__swLayers=window.__swLayers||[]).push(this);",
    "window.__swLayers",
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

function overlaps(a, b) {
  const rel = path.relative(b, a);
  return rel === "" || (!rel.startsWith("..") && !path.isAbsolute(rel));
}

function patch(from, out) {
  if (overlaps(out, from) || overlaps(from, out)) fail(`--out ${out} must not overlap --from ${from}`);
  if (!fs.existsSync(path.join(from, "index.html"))) fail(`${from} has no index.html`);
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

  // Terrain-text script: drop an earlier copy, add the current one before the
  // bundle so window.__swLayers is watched before the terrain is built.
  for (const old of listFiles(out).filter(f => /^build\/sw-terrain-text-[A-Za-z0-9_-]{8}\.js$/.test(f))) {
    fs.rmSync(path.join(out, old));
    console.log(`removed ${old}`);
  }
  html = html.replace(/<script src="\/build\/sw-terrain-text-[A-Za-z0-9_-]{8}\.js"><\/script>/g, "");
  const script = fs.readFileSync(path.join(HERE, "terrain-text.js"));
  const scriptName = `sw-terrain-text-${hash8(script)}.js`;
  write(`build/${scriptName}`, script);
  const entry = `<script type="module" src="/build/${bundleName}"></script>`;
  if (count(html, entry) !== 1) fail("index.html: entry bundle script tag not found once");
  html = html.replace(entry, `<script src="/build/${scriptName}"></script>${entry}`);
  write("index.html", html);
  console.log(`added build/${scriptName}`);

  // CSS lives in the root securewall.css, which is not served immutable.
  let css = read("securewall.css");
  const start = css.indexOf(CSS_START);
  if (start >= 0) {
    const end = css.indexOf(CSS_END, start);
    if (end < 0) fail("securewall.css: start marker without end marker");
    css = css.slice(0, start).trimEnd() + css.slice(end + CSS_END.length);
  }
  write("securewall.css", `${css.trimEnd()}\n\n${CSS}\n`);
  console.log("updated securewall.css");

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
