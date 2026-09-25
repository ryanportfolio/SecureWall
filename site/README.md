# secwall.org

`site/` holds the exact static files served at https://secwall.org (Vercel project `securewall`). The apex `secwall.org` is canonical; `www` and the `vercel.app` alias redirect to it. This README is excluded from deploys by `.vercelignore`.

- `build/` is a prebuilt Vite/React bundle. Its source is not in this repository, so never edit it by hand.
- `build/index-*.css` is the bundle's stylesheet with unused rules and the unused font files removed by `tools/site/prune-css.mjs`. The header of `tools/site/prune-css.mjs` gives the exact command and the original input (commit `6b0eacb`). Re-run and re-verify it whenever the bundle changes.
- `index.html` carries the page metadata, JSON-LD and a visually hidden crawler copy of the rendered text inside `#root`. React replaces that copy on mount. When the bundle changes, re-sync the copy with the rendered page.
- `404.html` is a self-contained not-found page (`noindex`). Vercel serves it with status 404 for unknown paths.
- `robots.txt` and `sitemap.xml` are generated. Do not edit them by hand.

## Preview

```sh
node tools/site/serve.mjs site 4173
```

The server applies `vercel.json` headers, serves `404.html` with status 404 and compresses responses with brotli or gzip.

## Check

```sh
node tools/site/seo.mjs --check
```

CI runs this check. It verifies titles, descriptions, canonical/Open Graph/Twitter tags, JSON-LD syntax, the 404 page, local file references, generated files, and that every text snippet of the crawler copy still appears in the JS bundle. That last check is one-way: it catches copy the bundle no longer contains, not content the copy leaves out.

## Regenerate

- Robots and sitemap: edit `ROUTES` or `SITE_URL` in `tools/site/seo.mjs`, then run `node tools/site/seo.mjs --write`.
- Brand images (`images/og.png`, `apple-touch-icon.png`, `favicon.ico`): edit `tools/site/brand/og.html` or `icon.html`, then run `node tools/site/brand/render.mjs`. It needs Playwright and its Chromium (`CHROME_PATH` selects a binary). The wordmark text uses Arial, or the metric-compatible Liberation Sans on Linux.

## Deploy

From `site/`, run `vercel deploy --prod`.
