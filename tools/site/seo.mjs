// SEO generator and checker for the static site in site/. No dependencies.
//   node tools/site/seo.mjs --write   regenerate site/robots.txt and site/sitemap.xml
//   node tools/site/seo.mjs --check   verify generated files, page metadata, JSON-LD,
//                                     404 page, static copy drift and local references
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const SITE_URL = 'https://secwall.org';
const ROUTES = ['/'];

const site = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'site');
const sitePath = (urlPath) => join(site, ...decodeURIComponent(urlPath).split('/').filter(Boolean));
const routeFile = (route) => join(sitePath(route), 'index.html');

function generated() {
  const urls = ROUTES.map((r) => `  <url>\n    <loc>${SITE_URL}${r}</loc>\n  </url>\n`).join('');
  return {
    'robots.txt': `User-agent: *\nAllow: /\n\nSitemap: ${SITE_URL}/sitemap.xml\n`,
    'sitemap.xml': `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${urls}</urlset>\n`,
  };
}

// --- tiny HTML helpers -------------------------------------------------------
const entities = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' };
const decode = (s) => s.replace(/&(#x[0-9a-f]+|#\d+|\w+);/gi, (m, e) =>
  e[0] === '#' ? String.fromCodePoint(e[1].toLowerCase() === 'x' ? parseInt(e.slice(2), 16) : +e.slice(1))
    : entities[e.toLowerCase()] ?? m);
const norm = (s) => s.replace(/\s+/g, ' ').trim();
const attrs = (tag) => {
  const out = {};
  for (const m of tag.replace(/^<\/?[\w-]+/, '').matchAll(/([\w:-]+)(?:\s*=\s*("[^"]*"|'[^']*'|[^\s>]+))?/g)) {
    out[m[1].toLowerCase()] = m[2] === undefined ? '' : decode(m[2].replace(/^["']|["']$/g, ''));
  }
  return out;
};
const tags = (html, name) => [...html.matchAll(new RegExp(`<${name}\\b[^>]*>`, 'gi'))].map((m) => attrs(m[0]));
const meta = (html, key, value) => tags(html, 'meta').filter((a) => a[key] === value);
// Tokens: { tag, name, close, attrs } or { text }. Comments, scripts and styles are skipped.
function tokenize(html) {
  const out = [];
  const re = /<!--[\s\S]*?-->|<(script|style)\b[^>]*>[\s\S]*?<\/\1>|<(\/?)([a-z][\w-]*)\b[^>]*>|[^<]+/gi;
  for (const m of html.matchAll(re)) {
    if (m[0].startsWith('<!--') || m[1]) continue;
    if (m[3]) out.push({ name: m[3].toLowerCase(), close: m[2] === '/', attrs: attrs(m[0]) });
    else out.push({ text: decode(m[0]) });
  }
  return out;
}

const errors = [];
const fail = (msg) => errors.push(msg);
const read = (file) => readFileSync(file, 'utf8');
const rel = (file) => 'site/' + file.slice(site.length + 1).replaceAll('\\', '/');

function checkPage(file, route) {
  const html = read(file);
  const where = rel(file);
  const titles = [...html.matchAll(/<title>([\s\S]*?)<\/title>/gi)].map((m) => norm(decode(m[1])));
  if (titles.length !== 1) fail(`${where}: expected exactly one <title>, found ${titles.length}`);
  else if (titles[0].length > 60) fail(`${where}: <title> is ${titles[0].length} chars (max 60)`);
  const desc = meta(html, 'name', 'description');
  if (desc.length !== 1) fail(`${where}: expected exactly one meta description, found ${desc.length}`);
  else if (desc[0].content.length > 160) fail(`${where}: meta description is ${desc[0].content.length} chars (max 160)`);
  const canonical = tags(html, 'link').filter((a) => a.rel === 'canonical');
  const expected = SITE_URL + route;
  if (canonical.length !== 1 || canonical[0].href !== expected) {
    fail(`${where}: expected one canonical ${expected}, found ${JSON.stringify(canonical.map((a) => a.href))}`);
  }
  for (const prop of ['og:title', 'og:description', 'og:url', 'og:type', 'og:site_name', 'og:image']) {
    const found = meta(html, 'property', prop);
    if (found.length !== 1 || !found[0].content) fail(`${where}: expected one non-empty ${prop}`);
    else if (['og:url', 'og:image'].includes(prop) && !found[0].content.startsWith(SITE_URL + '/')) {
      fail(`${where}: ${prop} must be an absolute ${SITE_URL}/ URL, found ${found[0].content}`);
    }
  }
  const ogUrl = meta(html, 'property', 'og:url')[0]?.content;
  if (ogUrl && ogUrl !== expected) fail(`${where}: og:url ${ogUrl} differs from canonical ${expected}`);
  if (meta(html, 'name', 'twitter:card').length !== 1) fail(`${where}: expected one twitter:card`);
  if (!tags(html, 'html')[0]?.lang) fail(`${where}: <html> has no lang attribute`);
  checkJsonLd(html, where);
}

function checkJsonLd(html, where) {
  for (const m of html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)) {
    if (attrs(m[1]).type !== 'application/ld+json') continue;
    try { JSON.parse(m[2]); } catch (e) { fail(`${where}: invalid JSON-LD: ${e.message}`); }
  }
}

// Every text node (and img alt) of the crawler copy in <div class="sw-static"> must
// appear in the JS bundle, so the copy is re-synced whenever the bundle changes.
function checkStaticCopy() {
  const html = read(join(site, 'index.html'));
  const script = tags(html, 'script').find((a) => a.type === 'module' && a.src);
  if (!script) return fail('site/index.html: no module script found for the static copy drift check');
  const bundleFile = sitePath(script.src);
  if (!existsSync(bundleFile)) return fail(`site/index.html: bundle ${script.src} not found`);
  const bundle = norm(read(bundleFile));
  const tokens = tokenize(html);
  const start = tokens.findIndex((t) => t.name === 'div' && !t.close && (t.attrs.class ?? '').split(/\s+/).includes('sw-static'));
  if (start < 0) return fail('site/index.html: no <div class="sw-static"> crawler copy found');
  const snippets = [];
  for (let i = start + 1, depth = 1; i < tokens.length && depth > 0; i++) {
    const t = tokens[i];
    if (t.name === 'div') depth += t.close ? -1 : 1;
    else if (t.name === 'img' && t.attrs.alt) snippets.push(t.attrs.alt);
    else if (t.text !== undefined && norm(t.text)) snippets.push(norm(t.text));
  }
  if (snippets.length === 0) fail('site/index.html: crawler copy is empty');
  const inBundle = (s) => bundle.includes(s) || bundle.includes(JSON.stringify(s).slice(1, -1));
  for (const s of snippets) {
    if (inBundle(s) || s.split(/(?<=[.!?])\s+/).every(inBundle)) continue;
    fail(`site/index.html: static copy text not found in ${script.src}: "${s}"`);
  }
}

function checkNotFound() {
  const file = join(site, '404.html');
  if (!existsSync(file)) return fail('site/404.html is missing');
  const html = read(file);
  if (!meta(html, 'name', 'robots').some((a) => /\bnoindex\b/i.test(a.content))) fail('site/404.html: missing <meta name="robots" content="noindex">');
  if (tags(html, 'link').some((a) => a.rel === 'canonical')) fail('site/404.html: must not have a canonical link');
  if (!tags(html, 'html')[0]?.lang) fail('site/404.html: <html> has no lang attribute');
  checkJsonLd(html, 'site/404.html');
}

function checkLocalRefs(file) {
  const html = read(file);
  const refs = [];
  for (const m of html.matchAll(/<[a-z][\w-]*\b[^>]*>/gi)) {
    const a = attrs(m[0]);
    for (const key of ['href', 'src']) if (a[key] !== undefined) refs.push(a[key]);
    if (a.property === 'og:image' && a.content) refs.push(a.content);
  }
  for (const m of html.matchAll(/<style\b[^>]*>([\s\S]*?)<\/style>/gi)) {
    for (const u of m[1].matchAll(/url\(\s*['"]?([^'")]+)['"]?\s*\)/g)) refs.push(u[1]);
  }
  for (let ref of refs) {
    if (ref.startsWith(SITE_URL + '/')) ref = ref.slice(SITE_URL.length);
    if (!ref.startsWith('/') || ref.startsWith('//')) continue;
    const path = ref.replace(/[?#].*$/, '');
    const target = path.endsWith('/') ? join(sitePath(path), 'index.html') : sitePath(path);
    if (!existsSync(target)) fail(`${rel(file)}: referenced file ${ref} does not exist in site/`);
  }
}

const mode = process.argv[2];
if (mode === '--write') {
  for (const [name, text] of Object.entries(generated())) writeFileSync(join(site, name), text);
  console.log(`wrote site/robots.txt and site/sitemap.xml for ${ROUTES.length} route(s)`);
} else if (mode === '--check') {
  for (const [name, text] of Object.entries(generated())) {
    const file = join(site, name);
    if (!existsSync(file) || read(file) !== text) fail(`site/${name} is out of date; run node tools/site/seo.mjs --write`);
  }
  for (const route of ROUTES) {
    const file = routeFile(route);
    if (!existsSync(file)) { fail(`route ${route}: ${rel(file)} not found`); continue; }
    checkPage(file, route);
    checkLocalRefs(file);
  }
  checkStaticCopy();
  checkNotFound();
  if (existsSync(join(site, '404.html'))) checkLocalRefs(join(site, '404.html'));
  if (!existsSync(join(site, 'favicon.ico'))) fail('site/favicon.ico is missing');
  if (errors.length) {
    console.error(`SEO check failed (${errors.length}):\n` + errors.map((e) => `  - ${e}`).join('\n'));
    process.exit(1);
  }
  console.log(`SEO check passed: ${ROUTES.length} route(s), robots.txt, sitemap.xml, 404.html, static copy`);
} else {
  console.error('usage: node tools/site/seo.mjs --write | --check');
  process.exit(2);
}
