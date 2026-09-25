// Local preview of site/ that follows the parts of Vercel's static hosting the
// site relies on: vercel.json "headers", 404.html with status 404, and
// brotli/gzip encoding. Usage: node tools/site/serve.mjs [root] [port]
import { createServer } from 'node:http';
import { readFileSync, statSync } from 'node:fs';
import { join, normalize, extname, resolve } from 'node:path';
import { brotliCompressSync, gzipSync } from 'node:zlib';

const root = resolve(process.argv[2] ?? 'site');
const port = Number(process.argv[3] ?? 4173);

const types = {
  '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8', '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml', '.png': 'image/png', '.jpg': 'image/jpeg', '.webp': 'image/webp',
  '.ico': 'image/x-icon', '.woff2': 'font/woff2', '.woff': 'font/woff',
  '.txt': 'text/plain; charset=utf-8', '.xml': 'application/xml; charset=utf-8',
};
const compressible = /^(text\/|application\/(javascript|json|xml)|image\/svg)/;

function loadRules() {
  let config = {};
  try { config = JSON.parse(readFileSync(join(root, 'vercel.json'), 'utf8')); } catch { }
  return (config.headers ?? []).map(({ source, headers }) => ({
    re: new RegExp('^' + source.replace(/[.+?^${}|[\]\\]/g, '\\$&').replace(/\(\\\.\*\)/g, '(.*)').replace(/:(\w+)\*/g, '(.*)') + '$'),
    headers,
  }));
}

function file(pathname) {
  let p = normalize(join(root, decodeURIComponent(pathname)));
  if (!p.startsWith(root)) return null;
  try {
    if (statSync(p).isDirectory()) p = join(p, 'index.html');
    return statSync(p).isFile() ? p : null;
  } catch { return null; }
}

createServer((req, res) => {
  const { pathname } = new URL(req.url, 'http://localhost');
  let status = 200;
  let p = file(pathname);
  if (!p) { status = 404; p = file('/404.html'); }
  const type = p ? types[extname(p)] ?? 'application/octet-stream' : 'text/plain; charset=utf-8';
  let body = p ? readFileSync(p) : Buffer.from('The page could not be found\n\nNOT_FOUND\n');
  const headers = { 'content-type': type, 'cache-control': 'public, max-age=0, must-revalidate' };
  for (const rule of loadRules()) {
    if (rule.re.test(pathname)) for (const { key, value } of rule.headers) headers[key.toLowerCase()] = value;
  }
  const accept = String(req.headers['accept-encoding'] ?? '');
  if (compressible.test(type)) {
    if (/\bbr\b/.test(accept)) { body = brotliCompressSync(body); headers['content-encoding'] = 'br'; }
    else if (/\bgzip\b/.test(accept)) { body = gzipSync(body); headers['content-encoding'] = 'gzip'; }
    headers.vary = 'Accept-Encoding';
  }
  headers['content-length'] = body.length;
  res.writeHead(status, headers);
  res.end(req.method === 'HEAD' ? undefined : body);
}).listen(port, () => console.log(`serving ${root} on http://localhost:${port}`));
