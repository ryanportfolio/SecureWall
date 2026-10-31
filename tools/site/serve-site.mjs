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

http.createServer((req, res) => {
  const url = new URL(req.url, "http://localhost");
  let rel = decodeURIComponent(url.pathname);
  if (rel.endsWith("/")) rel += "index.html";
  const file = path.join(root, rel);
  if (path.relative(root, file).startsWith("..") || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
    res.writeHead(404, { "Content-Type": "text/plain" }).end("not found");
    return;
  }
  res.writeHead(200, {
    "Content-Type": types[path.extname(file)] ?? "application/octet-stream",
    "Cache-Control": "no-store",
  });
  fs.createReadStream(file).pipe(res);
}).listen(port, () => console.log(`serving ${root} at http://localhost:${port}/`));
