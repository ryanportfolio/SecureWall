// The repository's About panel (description, website, topics) lives in repo.json.
//
//   node tools/readme/meta.mjs --lint    validate repo.json offline (pull requests)
//   node tools/readme/meta.mjs --check   compare with GitHub and fetch the README's remote links (main only)
//   node tools/readme/meta.mjs --apply   push repo.json to GitHub, then --check
//
// --check and --apply need the gh CLI with a token (GH_TOKEN in CI).
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { ROOT } from './facts.mjs';

const meta = JSON.parse(fs.readFileSync(path.join(ROOT, 'tools', 'readme', 'repo.json'), 'utf8'));
const mode = process.argv[2];
if (!['--lint', '--check', '--apply'].includes(mode)) {
  console.error('usage: meta.mjs --lint | --check | --apply');
  process.exit(2);
}

const gh = (...args) => execFileSync('gh', args, { encoding: 'utf8', env: { ...process.env, MSYS_NO_PATHCONV: '1' } });

let bad = 0;
const fail = msg => { console.error(`FAIL meta: ${msg}`); bad++; };

function lint() {
  if (!/^[\w.-]+\/[\w.-]+$/.test(meta.repo ?? '')) fail('repo must be owner/name');
  if (typeof meta.description !== 'string' || !meta.description.trim()) fail('description is empty');
  else if (meta.description.length > 350) fail(`description is ${meta.description.length} characters (350 max)`);
  if (typeof meta.homepage !== 'string') fail('homepage must be a string ("" for none)');
  else if (meta.homepage && !/^https:\/\//.test(meta.homepage)) fail('homepage must be https');
  if (!Array.isArray(meta.topics)) fail('topics must be an array');
  else {
    if (meta.topics.length > 20) fail(`${meta.topics.length} topics (20 max)`);
    for (const t of meta.topics) if (!/^[a-z0-9][a-z0-9-]{0,49}$/.test(t)) fail(`bad topic "${t}"`);
    if (new Set(meta.topics).size !== meta.topics.length) fail('duplicate topics');
  }
}

async function check() {
  const live = JSON.parse(gh('api', `repos/${meta.repo}`));
  if ((live.description ?? '') !== meta.description) fail(`description differs on GitHub: "${live.description}"`);
  if ((live.homepage ?? '') !== meta.homepage) fail(`homepage differs on GitHub: "${live.homepage}"`);
  const topics = [...(live.topics ?? [])].sort().join(',');
  if (topics !== [...meta.topics].sort().join(',')) fail(`topics differ on GitHub: ${topics}`);

  const md = fs.readFileSync(path.join(ROOT, 'README.md'), 'utf8');
  const links = new Set([...md.matchAll(/\]\((https?:\/\/[^)\s]+)\)/g)].map(m => m[1]));
  if (meta.homepage) links.add(meta.homepage);
  for (const url of links) {
    let status = 0;
    for (const method of ['HEAD', 'GET']) {
      try {
        status = (await fetch(url, { method, redirect: 'follow' })).status;
      } catch (e) {
        status = 0;
      }
      if (status > 0 && status < 400) break;
    }
    if (!(status > 0 && status < 400)) fail(`${url} answered ${status || 'no response'}`);
  }
}

lint();
if (!bad && mode === '--apply') {
  gh('api', '--method', 'PATCH', `repos/${meta.repo}`, '-f', `description=${meta.description}`, '-f', `homepage=${meta.homepage}`);
  const body = path.join(ROOT, '.tmp', 'topics.json');
  fs.mkdirSync(path.dirname(body), { recursive: true });
  fs.writeFileSync(body, JSON.stringify({ names: meta.topics }));
  gh('api', '--method', 'PUT', `repos/${meta.repo}/topics`, '--input', body);
  console.log('meta: applied');
}
if (!bad && mode !== '--lint') await check();
if (bad) process.exitCode = 1;
else console.log(`meta: ${mode.slice(2)} ok`);
