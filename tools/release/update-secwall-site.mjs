#!/usr/bin/env node
// Moves https://secwall.org to a new SecureWall release.
//
// secwall.org is a CLI-deployed Vercel project with no Git source, so the live
// production deployment is the source of truth. This script downloads it,
// replaces the release version, renames each edited bundle (/build/* is served
// immutable) and checks that every new GitHub link resolves. Deploying the
// output directory is left to the caller.
//
//   node tools/release/update-secwall-site.mjs --version 0.5.0 --out <dir>
//   node tools/release/update-secwall-site.mjs --check-live 0.5.0
//
// Environment: VERCEL_TOKEN, VERCEL_ORG_ID (team id). Writes changed=true|false
// to $GITHUB_OUTPUT when set.

import { createHash } from "node:crypto";
import fs from "node:fs";
import path from "node:path";

const SITE = "secwall.org";
const REPO_URL = "https://github.com/ryanportfolio/SecureWall";
const TEXT = /\.(html|js|css|json|txt|svg|xml)$/;
// The bundle also embeds Theatre.js's own "0.4.0" state version, so only these
// forms are release versions. Never replace a bare version number. The last
// form is the JSON-LD SoftwareApplication field in index.html.
const versionForms = v => [`v${v}`, `Version ${v}`, `"softwareVersion":"${v}"`];

class Failure extends Error {}

function fail(message) {
  throw new Failure(message);
}

function arg(name) {
  const i = process.argv.indexOf(name);
  return i > 0 ? process.argv[i + 1] : undefined;
}

function parseVersion(v) {
  if (!/^\d+\.\d+\.\d+$/.test(v ?? "")) fail(`expected a version like 0.5.0, got "${v}"`);
  return v.split(".").map(Number);
}

function compareVersions(a, b) {
  const [x, y] = [parseVersion(a), parseVersion(b)];
  for (let i = 0; i < 3; i++) if (x[i] !== y[i]) return x[i] - y[i];
  return 0;
}

function formPattern(form) {
  const escaped = form.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return new RegExp(`(?<![0-9A-Za-z.])${escaped}(?![0-9])`, "g");
}

function count(text, form) {
  return text.match(formPattern(form))?.length ?? 0;
}

function setOutput(name, value) {
  if (process.env.GITHUB_OUTPUT) fs.appendFileSync(process.env.GITHUB_OUTPUT, `${name}=${value}\n`);
}

// The timeout also covers reading the body, so a stalled response cannot hang the job.
function request(url, options = {}) {
  return fetch(url, { ...options, signal: AbortSignal.timeout(60_000) });
}

async function vercel(apiPath) {
  const token = process.env.VERCEL_TOKEN;
  const team = process.env.VERCEL_ORG_ID;
  if (!token || !team) fail("VERCEL_TOKEN and VERCEL_ORG_ID must be set");
  const sep = apiPath.includes("?") ? "&" : "?";
  const res = await request(`https://api.vercel.com${apiPath}${sep}teamId=${team}`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!res.ok) fail(`Vercel API ${apiPath.split("?")[0]} returned ${res.status}`);
  return res.json();
}

async function downloadLive(out) {
  const alias = await vercel(`/v4/aliases/${SITE}`);
  const deployment = alias.deploymentId;
  if (!deployment) fail(`${SITE} does not point at a deployment`);
  console.log(`live deployment: ${deployment}`);

  const files = [];
  const walk = (nodes, prefix) => {
    for (const node of nodes) {
      const p = prefix ? `${prefix}/${node.name}` : node.name;
      if (node.type === "directory") walk(node.children ?? [], p);
      else if (node.type === "file") files.push({ path: p, uid: node.uid });
    }
  };
  walk(await vercel(`/v6/deployments/${deployment}/files`), "");
  if (!files.length || files.some(f => !f.path.startsWith("src/"))) {
    fail("unexpected deployment file layout; expected every file under src/");
  }

  fs.rmSync(out, { recursive: true, force: true });
  for (const file of files) {
    const { data } = await vercel(`/v8/deployments/${deployment}/files/${file.uid}`);
    const bytes = Buffer.from(data, "base64");
    const sha1 = createHash("sha1").update(bytes).digest("hex");
    if (sha1 !== file.uid) fail(`${file.path}: SHA-1 ${sha1} does not match Vercel's ${file.uid}`);
    const target = path.join(out, file.path.slice("src/".length));
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, bytes);
  }
  console.log(`downloaded ${files.length} files`);
}

function listFiles(dir) {
  return fs.readdirSync(dir, { recursive: true, withFileTypes: true })
    .filter(e => e.isFile())
    .map(e => path.relative(dir, path.join(e.parentPath, e.name)).split(path.sep).join("/"));
}

// The release the site links to, read from its GitHub release links.
function siteVersion(text) {
  const found = new Set([...text.matchAll(/SecureWall\/releases\/(?:download|tag)\/v(\d+\.\d+\.\d+)\b/g)].map(m => m[1]));
  if (found.size !== 1) fail(`expected one release version in the site's release links, found ${[...found].join(", ") || "none"}`);
  return [...found][0];
}

// JSON-LD can drift from the release links, so it is checked on its own.
const SOFTWARE_VERSION = /"softwareVersion":"(\d+\.\d+\.\d+)"/g;

function staleMetadata(html, version) {
  return [...html.matchAll(SOFTWARE_VERSION)].map(m => m[1]).find(v => v !== version);
}

function bundleSources(html) {
  return [...html.matchAll(/src="\/(build\/[^"]+\.js)"/g)].map(m => m[1]);
}

async function checkUrl(url) {
  // Range keeps MSI downloads to one byte; GitHub answers 200 or 206.
  const res = await request(url, { headers: { Range: "bytes=0-0" }, redirect: "follow" });
  // A server that ignores Range would otherwise stream the whole MSI.
  await res.body?.cancel();
  if (res.status !== 200 && res.status !== 206) fail(`${url} returned ${res.status}`);
  console.log(`ok ${res.status} ${url}`);
}

async function update(version, out) {
  parseVersion(version);
  await downloadLive(out);
  const read = rel => fs.readFileSync(path.join(out, rel), "utf8");
  const pageText = () => {
    const html = read("index.html");
    return [html, ...bundleSources(html).map(read)].join("\n");
  };
  const old = siteVersion(pageText());

  if (old === version) {
    const stale = staleMetadata(read("index.html"), version);
    if (stale) fail(`release links show v${version} but JSON-LD softwareVersion is ${stale}; fix it by hand`);
    console.log(`${SITE} already shows v${version}; nothing to deploy`);
    setOutput("changed", "false");
    return;
  }
  if (compareVersions(version, old) < 0) fail(`refusing to move ${SITE} back from v${old} to v${version}`);

  const edited = [];
  for (const rel of ["index.html", ...listFiles(out).filter(f => /^build\/[^/]+\.js$/.test(f))]) {
    let text = read(rel);
    const before = text;
    versionForms(old).forEach((form, i) => {
      text = text.replace(formPattern(form), versionForms(version)[i]);
    });
    if (text !== before) {
      fs.writeFileSync(path.join(out, rel), text);
      edited.push(rel);
    }
  }
  console.log(`replaced v${old} with v${version} in ${edited.join(", ")}`);

  // A changed bundle needs a new name, or browsers keep the cached old one.
  for (const rel of edited.filter(f => f.startsWith("build/"))) {
    const oldName = path.posix.basename(rel);
    const hash = createHash("sha256").update(fs.readFileSync(path.join(out, rel))).digest("base64url").slice(0, 8);
    // Bundle hashes are 8 base64url characters and may themselves contain "-".
    const match = oldName.match(/^(.+)-[A-Za-z0-9_-]{8}\.js$/);
    if (!match) fail(`${rel}: expected a name ending in an 8-character hash`);
    const newName = `${match[1]}-${hash}.js`;
    if (newName === oldName) fail(`${rel}: cannot derive a new hashed name`);
    // A build file importing this bundle would change too and need its own new
    // name. The site has a single entry bundle; refuse anything else.
    const importers = listFiles(out).filter(f => f.startsWith("build/") && f !== rel && TEXT.test(f) && read(f).includes(oldName));
    if (importers.length) fail(`${importers.join(", ")} reference ${oldName}; only a single entry bundle is supported`);
    fs.renameSync(path.join(out, rel), path.join(out, "build", newName));
    for (const f of listFiles(out).filter(f => TEXT.test(f))) {
      const text = read(f);
      if (text.includes(oldName)) fs.writeFileSync(path.join(out, f), text.split(oldName).join(newName));
    }
    console.log(`renamed build/${oldName} to build/${newName}`);
  }

  const texts = listFiles(out).filter(f => TEXT.test(f)).map(f => [f, read(f)]);
  for (const [f, text] of texts) {
    for (const form of versionForms(old)) {
      if (count(text, form)) fail(`${f} still contains "${form}"`);
    }
  }
  for (const src of bundleSources(read("index.html"))) {
    if (!fs.existsSync(path.join(out, src))) fail(`index.html references missing ${src}`);
  }
  if (siteVersion(pageText()) !== version) fail("release links did not move to the new version");
  const stale = staleMetadata(read("index.html"), version);
  if (stale) fail(`JSON-LD softwareVersion is ${stale}, not ${version}`);

  const escapedVersion = version.replace(/\./g, "\\.");
  const links = new Set();
  for (const [, text] of texts) {
    const re = new RegExp(`${REPO_URL.replace(/[./]/g, "\\$&")}/[^"'\\s<>]*v${escapedVersion}[^"'\\s<>]*`, "g");
    for (const m of text.matchAll(re)) links.add(m[0]);
  }
  // The bundle builds MSI links by concatenating the architecture, which leaves
  // a truncated ".../SecureWall_" match. Check the published assets instead.
  for (const url of links) if (/\/releases\/download\//.test(url) && !/\.[a-z]+$/i.test(url)) links.delete(url);
  for (const arch of ["x64", "arm64", "x86"]) links.add(`${REPO_URL}/releases/download/v${version}/SecureWall_${arch}.msi`);
  for (const url of links) await checkUrl(url);

  setOutput("changed", "true");
  console.log(`site ready in ${out}`);
}

async function checkLive(version) {
  parseVersion(version);
  for (let attempt = 1; ; attempt++) {
    try {
      const get = async url => {
        const res = await request(url, { cache: "no-store" });
        if (!res.ok) {
          await res.body?.cancel();
          throw Error(`${url} returned ${res.status}`);
        }
        return res.text();
      };
      const html = await get(`https://${SITE}/`);
      const sources = bundleSources(html);
      if (!sources.length) throw Error("index.html references no bundle");
      // Every bundle must load. The page's <noscript> links the release too, so
      // the bundles must show it without index.html's help.
      const bundles = [];
      for (const src of sources) bundles.push(await get(`https://${SITE}/${src}`));
      const inBundles = siteVersion(bundles.join("\n"));
      if (inBundles !== version) throw Error(`bundles link v${inBundles}`);
      const inPage = siteVersion(html);
      if (inPage !== version) throw Error(`index.html links v${inPage}`);
      const stale = staleMetadata(html, version);
      if (stale) throw Error(`JSON-LD softwareVersion is ${stale}`);
      console.log(`${SITE} serves v${version}`);
      return;
    } catch (err) {
      if (attempt === 5) fail(`live check: ${err.message}`);
      await new Promise(r => setTimeout(r, 5000));
    }
  }
}

try {
  if (arg("--check-live")) await checkLive(arg("--check-live"));
  else {
    const version = arg("--version");
    const out = arg("--out");
    if (!version || !out) fail("usage: --version <x.y.z> --out <dir> | --check-live <x.y.z>");
    await update(version, path.resolve(out));
  }
} catch (err) {
  if (!(err instanceof Failure)) throw err;
  console.error(`error: ${err.message}`);
  process.exitCode = 1;
}
