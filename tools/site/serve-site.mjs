#!/usr/bin/env node
// Serves a downloaded or patched secwall.org directory for local review.
//
//   node tools/site/serve-site.mjs <dir> [port]
//
// Static files only, with / mapped to index.html. Nothing is cached, so a
// re-patched build shows on reload.

import fs from "node:fs";
import http from "node:http";
import path from "node:path";

const root = path.resolve(process.argv[2] ?? "");
const port = Number(process.argv[3] ?? 4317);
if (!process.argv[2] || !fs.existsSync(path.join(root, "index.html"))) {
  console.error("usage: serve-site.mjs <dir containing index.html> [port]");
  process.exit(1);
}

const types = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".jpg": "image/jpeg",
  ".ico": "image/x-icon",
  ".woff2": "font/woff2",
  ".txt": "text/plain; charset=utf-8",
  ".xml": "application/xml",
};

// The site's vercel.json headers (other than caching), so the walkthrough's
// frame-ancestors applies locally too. Sources are exact paths or a prefix
// followed by "(.*)", which is all the site's vercel.json uses.
let headerRules = [];
try {
  headerRules = JSON.parse(fs.readFileSync(path.join(root, "vercel.json"), "utf8")).headers ?? [];
} catch {}
function siteHeaders(rel) {
  const out = {};
  for (const rule of headerRules) {
    const src = rule.source ?? "";
    const hit = src.endsWith("(.*)") ? rel.startsWith(src.slice(0, -4)) : rel === src;
    if (!hit) continue;
    for (const { key, value } of rule.headers ?? []) if (key.toLowerCase() !== "cache-control") out[key] = value;
  }
  return out;
}

http.createServer((req, res) => {
  // A malformed path (for example "/%") gets a 400 instead of throwing out
  // of this callback, which would stop the server.
  let rel;
  try {
    rel = decodeURIComponent(new URL(req.url, "http://localhost").pathname);
  } catch {
    res.writeHead(400, { "Content-Type": "text/plain" }).end("bad request");
    return;
  }
  if (rel.endsWith("/")) rel += "index.html";
  const file = path.join(root, rel);
  // A file can vanish between checks while the patch tool rewrites the
  // folder being served, and stat throws on paths such as "/%00"; both are
  // a 404, not a crash.
  let isFile = false;
  try {
    isFile = !path.relative(root, file).startsWith("..") && fs.statSync(file).isFile();
  } catch {}
  if (!isFile) {
    res.writeHead(404, { "Content-Type": "text/plain" }).end("not found");
    return;
  }
  res.writeHead(200, {
    ...siteHeaders(rel),
    "Content-Type": types[path.extname(file)] ?? "application/octet-stream",
    "Cache-Control": "no-store",
  });
  fs.createReadStream(file).on("error", () => res.destroy()).pipe(res);
}).listen(port, () => console.log(`serving ${root} at http://localhost:${port}/`));
